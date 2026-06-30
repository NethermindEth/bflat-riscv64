---
layout: default
title: Modules
eyebrow: Link-time patches
lead: >
  Why a zkVM needs these link-time modules, what each one replaces, and the
  constraint it answers — with the per-module implementation detail at the end.
prev: /architecture/
next: /build/
---

## What they are

Each module is a small, self-contained object file — C, C++, or assembly —
that the linker pulls into the final binary, overriding a specific symbol via
`--wrap=`. Together they replace exactly the parts of .NET, musl, and
compiler-RT that a zkVM cannot honour, **without editing a single line of
upstream source**.

## Why it's done at link time

The alternative — forking the runtime and musl — means re-merging on every
upstream release. Instead each adaptation is an isolated object file plus a
`--wrap=` redirect, so the upstream code stays pristine and a .NET version
bump is a rebase, not a fork. (Same philosophy as the
[runtime patches](runtime.md).)

## The constraints they answer

A zkVM gives you far less than a Linux host: no kernel (so no syscalls,
files, threads, signals, or clock), no floating-point hardware, no
compressed instructions, no randomness, and a requirement that every run be
bit-for-bit reproducible. Each module closes one of those gaps.

| Module | What it provides | Constraint it answers |
|--------|------------------|-----------------------|
| [ubootstrap](#ubootstrap) | Runtime entry point — brings .NET up and calls `Main` | No glibc-style startup / OS loader |
| [zkvm_zisk · zkvm_zisk_sim](#zkvm-zisk) | `_start` + the memory layout the prover expects | No kernel; fixed prover memory map |
| [pal](#pal) | env, scheduling, files, time, memory, clean exit | No OS to answer syscalls |
| [rhp](#rhp) | Allocation, dispatch, exception/exit handling | Single-threaded, never-collecting runtime |
| [rhp_native](#rhp-native) | GC ref-assign + dispatch trampoline (asm) | No write barrier; bespoke dispatch |
| [tls](#tls) | A single thread-local block | One thread, no dynamic loader |
| [nofp](#nofp) | Empty soft-float helpers | No floating-point hardware |
| [rng_stupid](#rng-stupid) | Deterministic PRNG | No `/dev/urandom`; proofs must reproduce |
| [security-stub](#security-stub) | Security/GSS functions return failure | Unused network paths must still link |
| [gs_cookie](#gs-cookie) | Stack cookie pinned to a constant | No clock for entropy, no page protection |
| [stdcppshim](#stdcppshim) | `operator new` / `new[]` | Runtime's C++ needs them without libc++ |
| [rust_sys](#rust-sys) | `sys_alloc_aligned` | Interop with adjacent Rust precompiles |
| [ugc-zero](#ugc-zero) | A GC that allocates but never collects | Short-lived proof workloads |

## Results

Because every adaptation is a link-time object, **no upstream source is
modified** to make C# run in a zkVM, and this same set of modules carries
real production C# — Nethermind's
[StatelessExecutor](https://github.com/NethermindEth/nethermind) — end to end
through a zkVM prover on every commit. See [Verification](verification.md).

---

## Under the hood

The rest of this page documents each module in detail — the wrapped symbols,
the data structures, and the assembly. It's developer reference; the
[architecture page](architecture.md#stage-2--the-link-command) shows where
these objects sit in the link line.

The modules live under `src/bflat/modules/`. Each one contains a
`module.c`/`module.cpp`/`module.S` source, an optional `module_params.yml`
listing its linker switches (mostly `--wrap=` declarations), and the compiled
`module.o` produced by `build.sh modules riscv64`. `BuildCommand.cs` wires
them into the link line in a specific order; the sections below follow roughly
the order they matter at runtime.

## ubootstrap — runtime entry point
{: #ubootstrap }

**File:** `modules/ubootstrap/module.cpp`

A minimal re-implementation of the .NET NativeAOT bootstrap. It owns
`uBootstrap_main`, which:

1. Initialises the runtime (`RhInitialize`).
2. Registers the OS module by handing the runtime the start/end of the
   `__managedcode` and `__unbox` linker-defined sections, plus the
   classlib callback table (failfast, exception helpers, etc.).
3. Invokes every module initialiser via `InitializeModules`.
4. Jumps into `__managed__Main` — the AOT-emitted C# entry point.

The argv it passes is a fake `["app"]` because there is no real
command-line on a zkVM.

## zkvm_zisk / zkvm_zisk_sim — entry point and memory map
{: #zkvm-zisk }

**Files:** `modules/zkvm_zisk/{module.S,script.ld}`, `modules/zkvm_zisk_sim/{module.S,script.ld}`

Two siblings. Both contain a tiny `_start` written in assembly that sets
`gp`, `sp`, and tail-calls `__libc_start_main(uBootstrap_main, …)`.

The linker scripts diverge:

| Aspect | `zisk` | `zisk_sim` |
|--------|--------|------------|
| Image base | Split ROM (`0x80000000`, 256 MiB) and RAM (`0xa0020000`, ~256 MiB) | `0x50000000` (see below) |
| Entry section | `.text.init` at the head of `.text` | Same |
| Managed-code anchors | `__start___managedcode` / `__stop___managedcode` and the `__unbox` pair | Same |
| Heap | `_kernel_heap_bottom..._kernel_heap_top` at the tail of RAM | `.heap` as a `NOLOAD` segment mirroring the zisk RAM window (`0xa0020000..0xbfff0000`) |
| Bump-pointer cell | `g_zk_bump_ptr` = `ORIGIN(ram)+LENGTH(ram)-8` = `0xbffefff8`; heap top lowered by 16 so the cell never overlaps | Same address, provided at the exact `0xbffefff8` |
| Discarded sections | `.debug*`, `.comment`, `.riscv.attributes` | (looser — kept for ease of debugging) |

Both linker scripts force code that contains the C# entry point to land
near the start of `.text`, which keeps the call distance short enough
for non-PIC near-jump encodings.

**The fixed bump-pointer cell.** Both scripts reserve the top 8 bytes of
the (real or mirrored) RAM map as a fixed-address cell, `g_zk_bump_ptr` at
`0xbffefff8`, holding the downward bump pointer. Because the address is
fixed, the JIT can bake it into machine code as an `lui`/`addiw`/`slli`
immediate with **no relocation**, and JIT-emitted inline allocation shares
the same pointer with `pal`'s C allocator. `pal/module.c` does
`#define mem g_zk_bump_ptr` so both views are literally the same word.

**Why `zisk_sim` rebases to `0x50000000`.** `pal/module.c` reaches the
heap symbols (`g_zk_bump_ptr`, `_kernel_heap_top`/`_bottom`) with
PC-relative `auipc`/`addi` pairs, whose reach is ±2 GB. From the usual
`0x10000` base the fixed cell at `0xbffefff8` is ~2.68 GB away and
`R_RISCV_PCREL_HI20` overflows; basing the image at `0x50000000` keeps the
whole `0x50000000..0xbfff0000` span within ±2 GB. Real `zisk` avoids this
by placing text in ROM at `0x80000000`. The `.heap` is declared `NOLOAD`
so the Linux loader maps it as zero pages (`p_memsz > p_filesz`), matching
zkVM RAM being zero at boot — so `g_zk_bump_ptr` starts at `0` and is
lazily initialised exactly as on real zisk, and the binary stays small.

## pal — platform abstraction layer
{: #pal }

**File:** `modules/pal/module.c` ·
[symbols](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/modules/pal/module_params.yml)

The largest module by behavioural surface. It overrides musl primitives
that .NET calls during startup or runtime:

| Wrapped symbol | What we return |
|----------------|----------------|
| `getenv` | `"1"` for three CoreLib feature flags, `NULL` otherwise |
| `getcwd` | `/` |
| `getpid`, `getegid`, `geteuid` | `1` |
| `sched_getaffinity`, `sched_getcpu` | Always CPU 0 |
| `sysconf` | Hard-coded answers (CPU count = 1, page size = 4 KiB, …) |
| `open`, `__stdio_write` | Failure (`-1`) — there is no filesystem and no console |
| `clock_gettime` | `-1` — time is non-deterministic; CoreLib must use defaults |
| `pthread_create`, `pthread_sigmask` | No-ops |
| `mmap`, `munmap`, `mlock*` | mmap routed to the bump allocator; lock calls are no-ops |
| `__libc_malloc_impl`, `__libc_realloc`, `__libc_free` | A custom downward bump allocator using the heap symbols from the linker script |
| `signal`, `sigaction`, `sched_yield` | No-ops |
| `syscall` | Whitelist: 0x11b → 0; everything else → `__real_syscall` |
| `exit`, `_Exit`, `abort` | Emit the real ZisK exit ecall (`a7 = 93`, `CAUSE_EXIT`) via `zkvm_raw_exit` |

The bump allocator deserves a note: it grows downward from
`_kernel_heap_top`, stores an 8-byte size header before each allocation,
and never frees. That is enough to satisfy a managed runtime whose own
GC sits on top — see the `ugc-zero` module below — and it removes any
need for musl's full `mallocng`, which is large and uses syscalls.

The bump pointer itself lives in a **fixed-address cell** — the top 8 bytes
of RAM (`g_zk_bump_ptr`, `0xbffefff8`), provided by the linker script —
rather than a `static` variable. That lets JIT-emitted inline allocation
reference it by a hardcoded constant address and share the very same
pointer with this C allocator. zkVM RAM is zero at boot, so the cell starts
at `0` and is lazily initialised to `_kernel_heap_top` on first use.

**Clean termination.** ZisK only treats an `ecall` with `a7 == 93`
(`CAUSE_EXIT`) as "program end"; its trap handler routes that to `ROM_EXIT`,
whose instruction carries the `end` flag the emulator waits for. musl's
`exit`/`_Exit` issue `exit_group` (94), which ZisK does not recognise — the
run would stop "not completed". So `pal` wraps all three terminators to emit
the real ZisK exit ecall (`abort` exits with `134` = 128 + SIGABRT).

**`__wrap_RhpNewFast` — fixed-size fast path.** The hot allocation helper
lives here (not in `rhp`), in the same translation unit as the bump pointer,
the heap bounds, and `align_down_8_uintptr`, so the downward bump is inlined
directly: no nested `malloc` call, a single alignment step, and a leaf body
eligible for frameless-leaf codegen. This mirrors how x64/arm64 get fast
allocation through a tight `RhpNewFast` rather than per-site JIT inlining.
`--wrap=RhpNewFast` (declared by the `rhp` module) redirects managed callers
here regardless of which object file defines the symbol.

## rhp — Redhawk Platform shims
{: #rhp }

**File:** `modules/rhp/module.c`

Patches that target the .NET runtime itself. Responsibilities:

1. **Object allocators.** `RhpNewObject`, `RhpNewArrayFast`,
   `RhpNewPtrArrayFast`, and `RhNewString` are reimplemented on top of the
   bump allocator. The originals expect a thread-local allocation context;
   in our world there is exactly one thread and a bump allocator, so a flat
   path is both simpler and provable. The hottest helper, `RhpNewFast`, is
   *not* here — it moved to [`pal`](#pal) so its downward bump is inlined
   directly; the `--wrap=RhpNewFast` declaration that redirects callers
   still lives in this module's `module_params.yml`.
2. **Subsystem stubs.** EventPipe, ProcessorIdCache, default-locale
   queries, type-cast cache lookups, lock acquisition/release,
   thread-static storage, and a custom `RhpCidResolve` that bypasses the
   cached interface-dispatch fast path. Each of these would otherwise
   pull in code that touches signals, threads, or the OS.
3. **Exceptions and exit.** `RhpThrowEx`, `RhpReversePInvoke`, and
   `FailFast` are wrapped — see below.

The `__rhp_cid_resolve_nocache` function (called via the assembly
trampoline `__wrap_RhpCidResolve` in `rhp_native`) walks a dispatch cell
manually, looks up the interface slot on the object's MethodTable, and
returns the resolved target — replacing the fast-path cache that
NativeAOT normally maintains in writable memory.

### Managed exceptions

A managed `throw` is lowered by the JIT to `CORINFO_HELP_THROW`, which
calls `RhpThrowEx` with the exception object in `a0`. The wrapper hands
that object to a **weak** `ZkvmThrow` symbol:

```c
extern void ZkvmThrow(void *exceptionObj) __attribute__((weak));

void __wrap_RhpThrowEx(void *exceptionObj)
{
    if (ZkvmThrow != NULL) { ZkvmThrow(exceptionObj); return; }
    exit(1);
}
```

A program that exports `ZkvmThrow` via
`[UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]` takes full control of
the throw and receives the live `Exception` reference (the `a0` pointer
*is* the managed object reference). A program that doesn't export it links
fine — the weak reference stays null and the wrapper falls back to
`exit(1)`, preserving the old fail-fast behaviour. `FailFast` carries a
message string, not an exception object, so it keeps the plain `exit(1)`
path rather than routing through `ZkvmThrow`. See the
[ExceptionHandler sample](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples/ExceptionHandler).

To let the handler be entered from the throw path, `RhpReversePInvoke`
and `RhpReversePInvokeReturn` are **no-op'd**. The real CoreLib transition
attaches the thread and parks it at a GC-safe point — meaningful only for a
native→managed boundary entered in preemptive mode. When a managed handler
(an `[UnmanagedCallersOnly]` method) is entered from `__wrap_RhpThrowEx`,
the thread is already cooperative, so the real transition would spin on a
GC rendezvous that never comes in the single-threaded, never-collecting
zkVM.

## rhp_native — assembly RISC-V64 patches
{: #rhp-native }

**File:** `modules/rhp_native/module.S`

Two functions in hand-written RISC-V64 assembly:

- `__wrap_RhpAssignRefRiscV64` — a write-without-write-barrier
  reference assignment. Our GC has no write barrier, so the
  byref-assign helper must be a plain `sd` + post-increment.
- `__wrap_RhpCidResolve` — a trampoline that tail-calls into the C
  resolver above, preserving the dispatch cell pointer that the runtime
  passes in `t5`.

## tls — minimal thread-local storage
{: #tls }

**File:** `modules/tls/module.c`

A static 100 KiB buffer plays the role of TLS. On first access we copy
`.tdata` into it, zero `.tbss`, and return its address. There is one
thread, so there is only ever one TLS block. Calls to `__tls_get_addr`,
`__init_tls`, `__init_tp`, and `__copy_tls` are wrapped to use this
buffer instead of the dynamic-loader logic in musl.

## nofp — floating-point runtime stubs
{: #nofp }

**File:** `modules/nofp/module.c`

A flat list of empty function bodies for every soft-float helper
(`__addsf3`, `__divdf3`, `__floatsidf`, `__fixunsdfsi`, etc.). These
exist because RISC-V toolchains generate calls to compiler-RT helpers
even when the source uses `double` only by accident — for example
through a templated method that is never reached. Linking against an
empty `__addsf3` lets the binary build; if it ever runs at proof time
it would silently no-op, but the AOT pass should already have proven
the call is dead. Without this module the link fails with hundreds of
unresolved-symbol errors.

## rng_stupid — deterministic PRNG
{: #rng-stupid }

**File:** `modules/rng_stupid/module.c`

A linear-congruential PRNG seeded with `0x34095153`. Wraps:

- `minipal_get_cryptographically_secure_random_bytes`
- `CryptoNative_GetRandomBytes`
- `CryptoNative_EnsureOpenSslInitialized` (returns 0)

zkVMs cannot consult `/dev/urandom`. A truly random number would also
make the proof non-deterministic. The PRNG produces the same bytes for
the same execution, which is exactly what proving requires; whether the
caller's algorithm tolerates non-cryptographic randomness is the
caller's problem.

## security-stub — GSS / security functions
{: #security-stub }

**File:** `modules/security-stub/module.c`

A long list of `NetSecurityNative_*` functions that all return `-1`.
.NET's networking stack references these even when no GSS is in use;
returning failure is enough to prevent link errors and never gets
executed at runtime in our workloads.

## gs_cookie — neutralised stack cookie
{: #gs-cookie }

**File:** `modules/gs_cookie/module.c`

One line — `__wrap___security_cookie = 0`, placed in `.data` and bound via
`--wrap=__security_cookie`.

Upstream .NET uses a GS cookie (stack canary) to catch buffer overruns: the
JIT copies a process-global `__security_cookie` into each guarded frame and
re-checks it on return, and the runtime seeds that global **once at startup**
from a timer (`minipal_lowres_ticks`) into a read-only page. Neither half
survives a zkVM:

- **No entropy.** There is no clock, so a timer-seeded cookie is either
  constant (no protection anyway) or non-deterministic — a different value each
  run, which would make the proof non-reproducible.
- **No page protection.** `mprotect` / `PalVirtualProtect` is a no-op in the
  [pal](#pal) layer, and a read-only `.rodata` cookie collides with the
  code/data-split layout the postprocessor manages.

So the cookie is pinned to a constant `0` and the JIT's check always passes.
This **disables stack-canary defense-in-depth by design** — an accepted
trade-off for a single-threaded, deterministic guest with no untrusted
in-process boundary. Forcing the symbol into `.data` also keeps it out of the
read-only segment the postprocessor rewrites.

Two paths reach this symbol: with `--stdlib dotnet` the JIT still emits the
check and binds it to this wrapped `0`; for zerolib builds bflat instead tells
ILC not to emit GS cookies at all (`SettingsTunnel.EmitGSCookies = false`,
which bakes a constant into the code and emits no reference).

## stdcppshim — C++ allocator shims
{: #stdcppshim }

**File:** `modules/stdcppshim/module.cpp`

Just two operators: `operator new(size_t)` and `operator new[](size_t)`,
each forwarded to `malloc`. The .NET runtime's GC code is C++ and uses
`new` in a few places; without these shims we'd need to link a full libc++.

## rust_sys — Rust compatibility layer
{: #rust-sys }

**File:** `modules/rust_sys/module.c`

A single function: `__wrap_sys_alloc_aligned` forwards to our bump
allocator. Some Rust libraries used in adjacent precompile binaries call
it; including the wrapper unconditionally costs nothing.

## ugc-zero — minimal GC
{: #ugc-zero }

**Pulled from:** `dotnet-riscv` release archive, unpacked into
`modules/ugc-zero/release/` by `build.sh modules riscv64`. The upstream
source lives in
[`NethermindEth/ugc`](https://github.com/NethermindEth/ugc).

A complete drop-in for the .NET GC: `uGC.cpp`, `uGCHandleManager.cpp`,
`uGCHandleStore.cpp`, `uGCHeap.cpp`. It implements the GC interface but
never collects — every allocation goes straight to the underlying bump
allocator. For the proof workload this is acceptable because each
execution is short and the heap is sized to hold its working set in
full. `--wrap=GC_Initialize` and `--wrap=GC_VersionInfo` route the
runtime's GC discovery into this shim.

---

## Build flow for modules

`build.sh modules riscv64` walks every directory under
`src/bflat/modules/` and:

1. Compiles `module.c` with `riscv64-linux-gnu-gcc -march=rv64imad`.
2. Assembles `module.S` with `riscv64-linux-gnu-as --march=rv64ima --mabi=lp64`.
3. Compiles `module.cpp` with `riscv64-linux-gnu-g++ -march=rv64imad`.
4. Patches the resulting object's ABI marker byte to keep the linker
   happy when mixing soft-float-marked and hard-float-marked objects.
5. If `module_params.yml` declares a remote `repo` + `tag` + release
   `file`, downloads the release tarball into the module's `release/`
   directory.

Step 4 — patching offset `0x30` of the ELF e_flags — clears the
hard-float bit so the bflat-side modules carry the `lp64` (soft-float)
marker in their ELF header. The whole stack uses the `lp64d` calling
convention, but the runtime objects from dotnet-riscv ship with the
`lp64` marker bit, so flipping the marker on our side makes `ld.lld`
accept the link. The codegen on either side is unchanged — this is
purely about the marker bits the linker checks for ABI consistency.
