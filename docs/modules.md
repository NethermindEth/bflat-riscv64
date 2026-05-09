---
layout: default
title: Modules
eyebrow: Link-time patches
lead: >
  Each module is a small, self-contained object file that the linker pulls
  into the final binary. Together they replace exactly the parts of the
  .NET runtime, musl, and compiler-RT that a zkVM cannot honour.
prev: /architecture/
next: /build/
---

The modules live under `src/bflat/modules/`. Each one contains:

- a `module.c`, `module.cpp`, or `module.S` source;
- (optionally) a `module_params.yml` listing the linker switches it
  needs (mostly `--wrap=` declarations);
- the compiled `module.o`, produced by `build.sh modules riscv64`.

`BuildCommand.cs` wires these object files into the link line in a
specific order. The list below describes them in roughly the order they
matter at runtime.

---

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
| Memory regions | Split ROM (`0x80000000`, 256 MiB) and RAM (`0xa0020000`, ~256 MiB) | Single segment starting at `0x10000` |
| Entry section | `.text.init` at the head of `.text` | Same |
| Managed-code anchors | `__start___managedcode` / `__stop___managedcode` and the `__unbox` pair | Same |
| Heap | Provided by `_kernel_heap_bottom..._kernel_heap_top` symbols at the tail of RAM | Explicit 150 MiB `.heap` section |
| Discarded sections | `.debug*`, `.comment`, `.riscv.attributes` | (looser — kept for ease of debugging) |

Both linker scripts force code that contains the C# entry point to land
near the start of `.text`, which keeps the call distance short enough
for non-PIC near-jump encodings.

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

The bump allocator deserves a note: it grows downward from
`_kernel_heap_top`, stores an 8-byte size header before each allocation,
and never frees. That is enough to satisfy a managed runtime whose own
GC sits on top — see the `ugc-zero` module below — and it removes any
need for musl's full `mallocng`, which is large and uses syscalls.

## rhp — Redhawk Platform shims
{: #rhp }

**File:** `modules/rhp/module.c`

Patches that target the .NET runtime itself. Two responsibilities:

1. **Object allocators.** `RhpNewFast`, `RhpNewObject`,
   `RhpNewArrayFast`, `RhpNewPtrArrayFast`, and `RhNewString` are
   reimplemented on top of `calloc`. The originals expect a thread-local
   allocation context; in our world there is exactly one thread and a
   bump allocator, so a flat `calloc` is both simpler and provable.
2. **Subsystem stubs.** EventPipe, ProcessorIdCache, default-locale
   queries, type-cast cache lookups, lock acquisition/release,
   thread-static storage, and a custom `RhpCidResolve` that bypasses the
   cached interface-dispatch fast path. Each of these would otherwise
   pull in code that touches signals, threads, or the OS.

The `__rhp_cid_resolve_nocache` function (called via the assembly
trampoline `__wrap_RhpCidResolve` in `rhp_native`) walks a dispatch cell
manually, looks up the interface slot on the object's MethodTable, and
returns the resolved target — replacing the fast-path cache that
NativeAOT normally maintains in writable memory.

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
