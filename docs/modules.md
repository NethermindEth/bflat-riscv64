---
layout: default
title: Modules
eyebrow: Link-time adaptations
lead: >
  Why a zkVM needs these link-time modules, what each one replaces, and the
  constraint it answers — with the per-module implementation detail at the end.
prev: /architecture/
next: /targets/
---

## What they are

Each module is a small, self-contained object file — C, C++, or assembly —
that the linker pulls into the final binary, overriding a specific symbol
via `--wrap=` or by an earlier definition. Together they replace exactly the
parts of .NET, musl and compiler-RT that a zkVM cannot honour, **without
editing a single line of upstream source**: a .NET version bump is a
rebase, not a fork.

A zkVM gives you far less than a Linux host: no kernel (so no syscalls,
files, threads, signals or clock), no floating-point hardware, no compressed
instructions, no randomness, and a requirement that every run be
bit-for-bit reproducible. Each module closes one of those gaps.

The modules are generic: all five zkVM platforms link the same set, and no
module contains code for one zkVM. What is specific to a zkVM — the entry
point, the memory map, the exit and the console — is in its platform; see
[Targets](targets.md).

| Module | What it provides | Constraint it answers | Linked on |
|--------|------------------|-----------------------|-----------|
| [ubootstrap](#ubootstrap) | Runtime entry point — brings .NET up and calls `Main` | No glibc-style startup / OS loader | all platforms |
| [noos](#noos) | Displaces the musl members the runtime's Unix PAL drags in; the start-up without musl | No kernel, no processes, no filesystem, no loader | `noos` flag: sp1, openvm, airbender |
| [nothread](#nothread) | Single-hart locks, waits and thread primitives | One hart; a blocking call can never be woken | `no-atomics` flag: sp1, openvm, airbender |
| [pal](#pal) | env, scheduling, files, time, memory, clean exit | No OS to answer syscalls | all platforms |
| [rhp](#rhp) | Allocation, dispatch, exception/exit handling | Single-threaded, never-collecting runtime | all platforms |
| [rhp_native](#rhp-native) | GC reference-assignment helpers (asm) | No write barrier | all platforms |
| [eh](#eh) | Synthetic program headers for the unwinder | No loader, so nothing maps the real ones | all platforms |
| [tls](#tls) | A single thread-local block | One thread, no dynamic loader | all platforms |
| [nofp](#nofp) | Transcendental libm entry points that trap loudly | A transcendental has no place in a proof | all platforms |
| [guest_subst](#guest-subst) | ILC-stage substitutions and feature switches — data, not an object file | One hart; no OS data | all platforms |
| [rng_stupid](#rng-stupid) | Deterministic PRNG | No `/dev/urandom`; proofs must reproduce | all platforms |
| [gs_cookie](#gs-cookie) | Stack cookie pinned to a constant | No clock for entropy, no page protection | all platforms |
| [stdcppshim](#stdcppshim) | `operator new` / `new[]`, `__cxa_pure_virtual` | Runtime's C++ needs them without libc++ | all platforms |
| [rust_sys](#rust-sys) | `sys_alloc_aligned` | Interop with adjacent Rust precompiles | all platforms |
| [ugc-zero](#ugc-zero) | A GC that allocates but never collects | Short-lived proof workloads | all platforms |

## Results

No upstream source is modified to make C# run in a zkVM: every adaptation is
an object the linker pulls in, or — for `guest_subst` — data the driver
applies one stage earlier. The same set carries Nethermind's
[StatelessExecutor](https://github.com/NethermindEth/nethermind) end to end
through the ZisK, SP1 and OpenVM provers, and on Airbender.

These modules are also the most heavily checked code in the repository:
contracts, unit tests on the real ISA, fuzzing and machine-checked proofs.
See [Verification](verification.md).

---

## Under the hood

The rest of this page is developer reference: the wrapped symbols, the data
structures and the assembly, module by module. The
[architecture page](architecture.md#stage-2--the-link-command) shows where
these objects sit in the link line.

Modules live under `src/bflat/modules/`. Each has a
`module.c`/`module.cpp`/`module.S` source and a `bflat-manifest.json` that
declares its sources, its objects and their place in the link, and its
linker switches (mostly `--wrap=` declarations). `build.sh modules riscv64`
compiles `module.o`, and `build.sh modules riscv32` compiles
`module.rv32.o` from the same source; the manifest selects one of them by
`arch`. The [manifest format](manifest.md) page describes the file. The
sections below follow roughly the order they matter at runtime.

## ubootstrap — runtime entry point {#ubootstrap}
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

## noos — cutting the OS surface the PAL reaches for {#noos}
{: #noos }

**File:** `modules/noos/module.c` · linked in the `pre-libc` group on the
platforms with the `noos` flag

A guest links one object it never asked for: the runtime's Unix PAL. The
chain is short and unavoidable — `RhpEHEnumInitFromStackFrameIterator` pulls
in `EHHelpers.cpp.o`, whose `RhFailFast` pulls in `PalUnix.cpp.o` — and
`PalUnix` comes in whole, so every libc call anywhere inside it goes live,
together with `libSystem.Native`'s `pal_io`/`pal_threading`. Between them
they reach about 150 libc entry points and drag 96 members of musl's
`libc.a` into the image: crash dumps (`fork`, `execv`, `pipe`, `waitpid`),
dynamic loading, CPU counting (`fopen` → `fscanf` → `__intscan`), cgroup
probing, directory walking, signals. None of it can be serviced on one hart
with no processes, no filesystem and no loader, and all of it is dead
weight in a proof.

**Two behaviours, and the split is the design.** Operations that cannot be
honoured terminate with status **253** — the way `nofp` uses 255 and
`nothread` 254 — because returning a plausible-looking failure would let the
guest carry on and produce a wrong answer, which is the worst outcome for a
proving system. The string helpers those same objects call *are* serviced,
as ordinary small implementations, because they sit on live paths. A handful
of calls in between are serviced honestly where the answer is knowable and
harmless: `sigemptyset`/`sigaddset` on a 128-byte mask,
`__libc_current_sigrtmin`, `__sched_cpucount`, `mprotect` returning success
(nothing to protect), `statfs`/`dladdr`/`fopen` returning failure.

**It takes effect by definition, not by `--wrap`.** A wrap redirects the
call but leaves musl's member, and its instructions, in the image. The
linker extracts an archive member only for a symbol still undefined when it
reaches the archive, so this object is linked ahead of `libc.a` and the
members never come in. Where `pal` already wraps a name the two coexist: the
call goes to `pal`'s `__wrap_`, and the definition here is what keeps musl
out.

**It also owns the .NET start-up for the platforms without musl's**, in two
halves that share one body: `noos_start_main(argc, argv)` is the `noreturn`
replacement for musl's `__libc_start_main`, used by the `_start` of OpenVM
and Airbender, and `noos_main(argc, argv)` is the returning half, used by
SP1 through `__wrap_main` because SP1 halts with `main`'s return value. Both
set the thread pointer, run `.init_array` and enter `uBootstrap_main`.

## nothread — one hart, said explicitly {#nothread}
{: #nothread }

**File:** `modules/nothread/module.c` · linked in the `pre-libc` group on
the platforms with the `no-atomics` flag

A zkVM guest has no threads: no thread-creation instruction, no interrupts,
one hart. This module replaces musl's locking, waiting and thread
primitives with implementations for one hart, so no lock, futex or thread
path of musl is in the image.

- The acquire and release primitives are no-ops that report success. With
  no second agent, that is the correct implementation, not a shortcut.
- The primitives that **block** fail fast with status **254**. Nothing can
  wake them, so a return (with `ETIMEDOUT`, for example) would put the
  caller in a loop on a condition that nothing can change.

Like `noos`, it takes effect by definition: it is linked ahead of `libc.a`
and defines every global of each musl member that it displaces. Some of its
functions (`__membarrier_init`, `__do_cleanup_push`) are never called; they
are there so that `pthread_create.lo` is never pulled in for them.

The module also kept the `lr`/`sc`/`amo*` instructions of an rv64ima musl
out of the image. The musl of the layout is now rv64im and has no A
instructions, but the module stays as the clear statement of what a lock
and a wait mean on one hart. The ZisK platforms do not set `no-atomics` and
keep musl's primitives, which also work on one hart.

## Platforms — entry point and memory map
{: #zkvm-zisk }

The entry points, the linker scripts and the exit, console and syscall
code of each zkVM are not bflat modules: they are the platforms in the
bflat-* packages. [Targets](targets.md) describes each one:
[zisk](targets.md#zisk), [zisk_sim](targets.md#zisk_sim),
[sp1](targets.md#sp1), [openvm](targets.md#openvm) and
[airbender](targets.md#airbender).
{: #zkvm-sp1 }

## pal — platform abstraction layer {#pal}
{: #pal }

**File:** `modules/pal/module.c` ·
[symbols](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/modules/pal/bflat-manifest.json)

The largest module by behavioural surface. It wraps 40 musl and runtime
symbols that .NET calls during startup or runtime:

| Wrapped symbol | What we return |
|----------------|----------------|
| `getenv` | `"1"` for three CoreLib settings (invariant globalization, predefined cultures only, the `ArrayPool` partition count), `NULL` otherwise |
| `getcwd` | `/` |
| `getpid`, `getegid`, `geteuid` | `1` |
| `sched_getaffinity`, `sched_getcpu` | Always CPU 0 |
| `sysconf` | Hard-coded answers (CPU count = 1, page size = 4 KiB, …) |
| `open` | Failure (`-1`) — there is no filesystem |
| `__stdio_write` | Only with the `no-console` flag: the platform's `zkvm_platform_write` |
| `clock_gettime`, `__clock_gettime` | `-1` — time is non-deterministic; CoreLib must use defaults |
| `pthread_create`, `pthread_sigmask` | No-ops |
| `mmap`, `munmap`, `mlock`, `munlock`, `mlockall`, `munlockall` | mmap routed to the bump allocator; lock calls are no-ops |
| `__libc_malloc_impl`, `__libc_realloc`, `__libc_free`, `calloc`, `__malloc_allzerop`, `aligned_alloc`, `posix_memalign`, `memalign` | A downward bump allocator on the heap symbols of the linker script |
| `signal`, `sigaction`, `sched_yield` | No-ops |
| `PalGetMaximumStackBounds` | The stack of the linker script. The real one aborts under static musl on RISC-V (`mremap` `EINVAL`). |
| `vfprintf`, `vfscanf`, `__isoc99_vfscanf`, `__floatscan` | A small `vfprintf`, so musl's, with its floating point, stays out of the link; the scanners consume nothing and convert nothing |
| `syscall` | Whitelist: `0x11b` → 0; everything else → the platform's `zkvm_platform_syscall`, or `-1` without one |
| `exit`, `_Exit`, `abort` | The platform's exit sequence, `zkvm_platform_exit`, via `zkvm_raw_exit` |

The bump allocator deserves a note: it grows downward from
`_kernel_heap_top`, stores an 8-byte size header before each allocation,
and never frees. That is enough to satisfy a managed runtime whose own GC
sits on top — see the `ugc-zero` module below — and it removes any need for
musl's `mallocng`, which is large and uses syscalls. Managed allocation
reaches it in two ways: the inline bump that the JIT emits on the platforms
with `JitZkBumpAddr`, and the allocation helpers of [rhp](#rhp).

The bump pointer itself lives in a **fixed-address cell** — the top 8 bytes
of RAM (`g_zk_bump_ptr`), which the linker script provides — rather than a
`static` variable. That lets JIT-emitted inline allocation reference it by
a constant address and share the very same pointer with this C allocator.
zkVM RAM is zero at boot, so the cell starts at `0` and is initialised to
`_kernel_heap_top` on first use. The cell below it, `g_zk_heap_floor`, holds
the lowest address the heap may reach; `pal` writes it once from an
`.init_array` constructor. [Targets](targets.md#summary) lists the address
of each platform.

**Clean termination.** musl's `exit`/`_Exit` issue `exit_group` (94), which
no zkVM takes as the end of the program (ZisK, for example, only treats an
`ecall` with `a7 == 93` as "program end", and the run would stop "not
completed"). So `pal` wraps all three terminators and calls the platform's
`zkvm_platform_exit` (`abort` exits with `134` = 128 + SIGABRT). The exit
sequence of each zkVM is in the entry point of its platform.

**Allocation that must not fail.** `zkvm_alloc_or_abort(bytes, align)`
allocates from the bump heap and ends the guest on exhaustion, for a runtime
that cannot handle `NULL` from its allocator. The ZisK platform binds the
ziskos Rust allocator (`inline_bump_alloc_aligned`) to it.

## rhp — Redhawk Platform shims {#rhp}
{: #rhp }

**File:** `modules/rhp/module.c`

Replacements for parts of the .NET runtime itself (19 wraps).
Responsibilities:

1. **P/Invoke transitions.** `RhpPInvoke`/`RhpReversePInvoke` and their
   return halves become no-ops: the frames they build park a thread at a GC
   rendezvous that never comes in a single-threaded, never-collecting guest.
2. **Allocation.** `RhpNewFast`, `RhpNewArrayFast` and
   `RhpNewPtrArrayFast` bump the allocation context of the thread in C and
   stamp the MethodTable. When the budget is used up, the original helper
   runs and refills it from the heap (`uGCHeap::Alloc`).
   `RhAllocateNewObject` and `RhAllocateNewArray` are wrapped too.
3. **Subsystem stubs.** EventPipe registration, and — on the platforms
   with the `no-console` flag, which have no terminal — console
   initialisation (`InitializeTerminalAndSignalHandling`,
   `SetTerminalInvalidationHandler`), `SystemNative_Write`, `FStat`, `Dup`
   and `IsATty`.
4. **Write barrier.** `RhBulkMoveWithWriteBarrier` is a plain `memmove`;
   uGC never scans, so there is nothing to record.
5. **Fail-fast.** `FailFast`, and in a build without unwind tables also
   `RhpThrowEx` — see below.

### Managed exceptions without unwind tables

`--remove-eh`, or a platform with `"unwind": false` (Airbender), strips the
unwind tables. A `throw` then cannot be dispatched, so `rhp` wraps
`RhpThrowEx` (`"when": {"eh": "failfast"}`) and the guest exits on it. (The
default build dispatches exceptions normally; that path runs through the
[eh module](#eh) and nothing here.)

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
`exit(1)`. No `catch` or `finally` runs on this path: the guest is gone.
`FailFast` carries a message string, not an exception object, so it keeps
the plain `exit(1)` path rather than routing through `ZkvmThrow`. See the
[ExceptionHandler sample](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples/ExceptionHandler).

To let the handler be entered from the throw path, `RhpReversePInvoke`
and `RhpReversePInvokeReturn` are **no-op'd**. The real CoreLib transition
attaches the thread and parks it at a GC-safe point — meaningful only for a
native→managed boundary entered in preemptive mode. When a managed handler
(an `[UnmanagedCallersOnly]` method) is entered from `__wrap_RhpThrowEx`,
the thread is already cooperative, so the real transition would spin on a
GC rendezvous that never comes in the single-threaded, never-collecting
zkVM.

## rhp_native — assembly reference-assignment helpers {#rhp-native}
{: #rhp-native }

**File:** `modules/rhp_native/module.S`

uGC has no card table and never collects, so every GC write barrier is
dead. Four reference-assignment helpers become the bare store, with no
heap-bounds check and no card mark:

| Helper | Body |
|--------|------|
| `__wrap_RhpAssignRefRiscV64` (`__wrap_RhpAssignRefRiscV32` on riscv32) | store `t4` at `t3` |
| `__wrap_RhpCheckedAssignRef` | store `t4` at `t3` |
| `__wrap_RhpByRefAssignRef` | load from `t5`, store at `t3`, then advance `t5` and `t3` by one reference |
| `__wrap_RhpAssignRef` | store `a1` at `a0` (the C convention) |

**The single-assign helpers keep `t3`.** .NET 10's `WriteBarriers.S`
incremented `t3` by 8; .NET 11 keeps it, and the JIT reuses `t3` after the
call. A stale increment makes the next reuse land 8 bytes high: for
example, `Dictionary<__Canon,TValue>.TryInsert` stores the key through this
helper and then writes the value at `0x10(t3)`, so the dictionary loses
every value. Only the ByRef helper advances its registers.

One source serves both widths. The assembler runs without the C
preprocessor, so the riscv32 build selects `lw`/`sw` and a 4-byte reference
with `--defsym BFLAT_RV32=1`.

## tls — minimal thread-local storage {#tls}
{: #tls }

**File:** `modules/tls/module.c`

A static 100 KiB buffer plays the role of TLS. On first access we copy
`.tdata` into it, zero `.tbss`, and return its address. There is one
thread, so there is only ever one TLS block. Calls to `__tls_get_addr`,
`__init_tls`, `__init_tp`, and `__copy_tls` are wrapped to use this
buffer instead of the dynamic-loader logic in musl.

The RISC-V psABI biases a DTPREL offset by `-0x800` (musl's `DTP_OFFSET`).
Thus `__wrap___tls_get_addr` adds `0x800` to the offset of its argument.

## nofp — floating-point runtime stubs {#nofp}
{: #nofp }

**File:** `modules/nofp/module.c` · linked in the `early` group

A `--wrap` stub for each transcendental libm function that the runtime
references (`sin`, `pow`, `exp`, `log`, …; 38 names). The runtime's math
helpers reference them, but on the zkVM those paths do not run. The bundled
musl is built for `rv64im`, so its implementations would link and run: the
trap is policy. A transcendental has no place in a proof, and a guest that
reaches one has a bug.

Every stub **traps**: the body calls a `noreturn` helper that exits with
status 255. The policy lives in one function (`nofp_trap`).

The module does not stub these:

- The soft-float builtins (`__adddf3`, `__muldf3`, …). They are the
  floating-point arithmetic of the guest, and `libgcc.a` (on riscv32, the
  compiler-rt builtins) defines them.
- The exact operations: `ceil`, `floor`, `modf`, `sqrt`, `fma`, `scalbn`,
  `fmod` and their float forms. Each has one correctly rounded IEEE 754
  result, like `+` and `/`, and CoreLib calls them for ordinary work:
  double formatting, `DateTime.Add*`, `HashHelpers.IsPrime`, and the `%`
  operator on `double`.

One exception, deliberately not a trap: `__wrap_asprintf` returns `-1`.
It is referenced only by the cgroup parsing that pal already stubs out,
and `-1` is the documented failure result its callers handle — so if that
path is ever reached it degrades instead of aborting.

## eh — synthetic program headers for the unwinder {#eh}
{: #eh }

**File:** `modules/eh/module.c`

The runtime's unwinder locates its DWARF tables through
`dl_iterate_phdr`: it wants a `PT_LOAD` covering the queried PC and a
`PT_GNU_EH_FRAME` over `.eh_frame_hdr`. A zkVM image has no program headers
to walk — the zkVM materialises memory from segments and jumps to the entry
point, so there is no loader, no auxv, and the ELF header is not mapped.

libunwind reads only what the callback hands it, so this module wraps
`dl_iterate_phdr` and describes the image from linker-script symbols: one
`PT_LOAD` over the executable range (`__image_text_start` …
`__image_text_end`, which the script extends across `__managedcode` and
`__unbox`) and one `PT_GNU_EH_FRAME` over `.eh_frame_hdr`. The image is not
position independent, so `dlpi_addr` is 0 and the vaddrs are absolute. When
`.eh_frame_hdr` is empty — a build without unwind tables — only the load
segment is reported and the lookup fails cleanly rather than decoding a
stripped range.

Its contracts are proved with Frama-C; see
[Fuzzing and machine-checked proofs](verification.md#fuzzing-and-machine-checked-proofs).

## rng_stupid — deterministic PRNG {#rng-stupid}
{: #rng-stupid }

**File:** `modules/rng_stupid/module.c`

A linear-congruential PRNG seeded with `0x34095153`. Wraps:

- `minipal_get_cryptographically_secure_random_bytes` (returns 0 on success)
- `minipal_get_non_cryptographically_secure_random_bytes` — feeds the
  hash seeds (Marvin, `HashCode`), so it matters for determinism even
  though nothing here is cryptographic
- `CryptoNative_GetRandomBytes` (returns 1 on success — the opposite
  convention from the minipal pair, matching each caller's expectation)
- `CryptoNative_EnsureOpenSslInitialized` (returns 0; there is no OpenSSL)

zkVMs cannot consult `/dev/urandom`. A truly random number would also
make the proof non-deterministic. The PRNG produces the same bytes for
the same execution, which is exactly what proving requires; whether the
caller's algorithm tolerates non-cryptographic randomness is the
caller's problem.

## gs_cookie — neutralised stack cookie {#gs-cookie}
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

## stdcppshim — C++ allocator shims {#stdcppshim}
{: #stdcppshim }

**File:** `modules/stdcppshim/module.cpp`

`operator new(size_t)` and `operator new[](size_t)`, each forwarded to
`malloc`; they never return null. The .NET runtime's GC code is C++ and
uses `new` in a few places; without these shims we'd need to link a full
libc++. On riscv32 the module also defines `__cxa_pure_virtual`, the
vtable slot of a pure virtual function (uGC's interfaces), which exits with
status 255: the riscv64 layout gets it from its libsupc++, and the riscv32
layout has no C++ runtime library.

## rust_sys — Rust compatibility layer {#rust-sys}
{: #rust-sys }

**File:** `modules/rust_sys/module.c`

A single function: `__wrap_sys_alloc_aligned` forwards to our bump
allocator. Some Rust libraries used in adjacent precompile binaries call
it; including the wrapper unconditionally costs nothing.

## ugc-zero — minimal GC {#ugc-zero}
{: #ugc-zero }

**Pulled from:** the uGC release archives, which `build.sh modules` checks
against the signature of the release (see
[Package signatures](signing.md)) and unpacks:

| Arch | Release | Archive | Directory |
|------|---------|---------|-----------|
| riscv64 | v1.0.13 | `ugc-riscv64.tar.gz` | `modules/ugc-zero/release/` |
| riscv32 | v1.0.15 | `ugc-riscv32.tar.gz` | `modules/ugc-zero/release-riscv32/` |

v1.0.15 is the first release whose riscv32 objects place each object
8-byte aligned, as the riscv32 runtime sizes them. The upstream source
lives in [`NethermindEth/uGC`](https://github.com/NethermindEth/uGC).

A complete drop-in for the .NET GC: `uGC.cpp`, `uGCHandleManager.cpp`,
`uGCHandleStore.cpp`, `uGCHeap.cpp`, `ugc_core.c`, `ugc_zalloc.c`. It
implements the GC interface but never collects — every allocation goes
straight to the underlying bump allocator. For the proof workload this is
acceptable because each execution is short and the heap is sized to hold
its working set in full. `--wrap=GC_Initialize` and `--wrap=GC_VersionInfo`
route the runtime's GC discovery into this shim. The manifest also sets
`JitZkNoGcMustInit=1`: uGC never scans a stack, so the JIT need not zero
the GC slots of a frame.

---

## guest_subst — ILC-stage substitutions {#guest-subst}
{: #guest-subst }

The odd one out: it ships no object file. `guest_subst` carries the data
that changes *managed* code before it is ever compiled to RISC-V —
[`substitutions.xml`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/modules/guest_subst/substitutions.xml),
an ILLink substitutions file, two feature switches, and the
`il_float_allow` list of `--error-on-float`. There are two reasons for its
content:

- The guest has one hart. The substitutions stub the thread-pool tuning and
  the processor queries.
- The guest has no OS data. `System.Globalization.Invariant` and
  `System.TimeZoneInfo.Invariant` are on: a zkVM has no ICU data and no
  zoneinfo files, and a proof must not depend on them.

Floating point is not a reason: soft-float arithmetic and the exact libm
operations run. All five platforms require the module. The file is
copied into the layout and consumed by the driver at guest-build time; the
mechanism is described under
[Stage 1.5](architecture.md#stage-15--ilc-stage-substitutions).

It is grouped with the modules because it answers the same kind of
constraint in the same spirit — replace, don't patch — but it acts one
stage earlier, on IL rather than on symbols.

## Build flow for modules

`build.sh modules <arch>` runs `bflat-modules plan --arch <arch>` (in
`src/bflat-modules/`), which reads the `bflat-manifest.json` of every
directory under `src/bflat/modules/` and keeps the sources and downloads
whose `when` matches the architecture. For each module, `build.sh` then:

1. Downloads the files that the manifest's `external` section names: a
   release archive (`repo` + `tag` + `release`), checked against the
   signature of the release and unpacked into `dir`.
2. Compiles each `.c` file in `sources`, and each `.cpp` file with
   `clang++` and the same flags, with the common flags of the architecture.
3. Assembles each `.S` file.

| | riscv64 | riscv32 |
|-|---------|---------|
| C and C++ | `clang --target=riscv64-linux-gnu -march=rv64imad -mabi=lp64 -mcmodel=medany -flto=full -funified-lto -O3`, against the `riscv64-linux-gnu` sysroot | `clang --target=riscv32-unknown-linux-musl -march=rv32im -mabi=ilp32 -mcmodel=medany -O2 -ffunction-sections -fdata-sections -fno-stack-protector`, against `RV32_SYSROOT` (the `crossrootfs-musl-riscv32` asset of the dotnet-riscv release, with its libc++ headers). No LTO. |
| Assembly | `riscv64-linux-gnu-as --march=rv64ima --mabi=lp64` | `clang --target=riscv32-unknown-elf -march=rv32im -mabi=ilp32 -Wa,--defsym,BFLAT_RV32=1` |
| Output | `module.o` | `module.rv32.o` |

There is no riscv32 GCC, so clang compiles and assembles everything for
riscv32.

`-mabi=lp64` (and `ilp32`) gives each object the soft-float marker in its
ELF header, the same as the runtime and musl in the layout. bflat links the
layout as it is and changes no file: `bflat-modules check-abi` checks every
object of the layout when it is made (refer to the
[`codegen` section](manifest.md#codegen-instruction-set-abi-and-compiler-options)
of the manifest format).

The bflat build then runs `bflat-modules pack`, which copies each manifest
and the files it names into `lib/modules/<name>/` in the compiler layout.
