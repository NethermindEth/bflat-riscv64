---
layout: default
title: Modules
eyebrow: Link-time adaptations
lead: >
  Why a zkVM needs these link-time modules, what each one replaces and which
  constraint it answers. The implementation detail of each module is at the
  end.
prev: /architecture/
next: /targets/
---

## What they are

Each module is a small object file in C, C++ or assembly. The linker pulls it
into the final binary. The module overrides a specific symbol with
`--wrap=` or with an earlier definition. Together, the modules replace the
parts of .NET, musl and compiler-RT that a zkVM cannot honour. The modules
change no runtime source. What they cannot do is in the patch series of
dotnet-riscv (see [Runtime](runtime.md)).

A zkVM gives a guest much less than a Linux host:

- no kernel, so no syscalls, files, threads, signals or clock;
- no floating-point hardware;
- no compressed instructions;
- no randomness;
- a requirement that each run is bit-for-bit reproducible.

Each module closes one of those gaps.

The modules are generic. All five zkVM platforms link the same set, and no
module contains code for one zkVM. The parts that are specific to a zkVM
(the entry point, the memory map, the exit and the console) are in its
platform. Refer to [Targets](targets.md).

| Module | What it provides | Constraint it answers | Linked on |
|--------|------------------|-----------------------|-----------|
| [ubootstrap](#ubootstrap) | Runtime entry point: starts .NET and calls `Main` | No glibc-style startup / OS loader | all platforms |
| [noos](#noos) | Displaces the musl members that the Unix PAL of the runtime pulls in; the start-up without musl | No kernel, no processes, no filesystem, no loader | `noos` flag: sp1, openvm, airbender |
| [nothread](#nothread) | Single-hart locks, waits and thread primitives | One hart; nothing can wake a blocking call | `no-atomics` flag: sp1, openvm, airbender |
| [pal](#pal) | env, scheduling, files, time, memory, clean exit | No OS to answer syscalls | all platforms |
| [rhp](#rhp) | Allocation, dispatch, exception/exit handling | Single-threaded, never-collecting runtime | all platforms |
| [rhp_native](#rhp-native) | GC reference-assignment helpers (asm) | No write barrier | all platforms |
| [eh](#eh) | Synthetic program headers for the unwinder | No loader, so nothing maps the real ones | all platforms |
| [tls](#tls) | A single thread-local block | One thread, no dynamic loader | all platforms |
| [nofp](#nofp) | Transcendental libm entry points that trap | A transcendental has no place in a proof | all platforms |
| [guest_subst](#guest-subst) | ILC-stage substitutions and feature switches (data, no object file) | One hart; no OS data | all platforms |
| [rng_stupid](#rng-stupid) | Deterministic PRNG | No `/dev/urandom`; proofs must reproduce | all platforms |
| [gs_cookie](#gs-cookie) | Stack cookie set to a constant | No clock for entropy, no page protection | all platforms |
| [stdcppshim](#stdcppshim) | `operator new` / `new[]`, `__cxa_pure_virtual` | The C++ code of the runtime needs them without libc++ | all platforms |
| [rust_sys](#rust-sys) | `sys_alloc_aligned` | Interop with adjacent Rust precompiles | all platforms |
| [ugc-zero](#ugc-zero) | A GC that allocates but never collects | Short-lived proof workloads | all platforms |

## Results

The modules change no runtime source. Each adaptation is an
object that the linker pulls in. `guest_subst` is the one exception: it is
data that the driver applies one stage earlier. The same set runs
Nethermind's
[StatelessExecutor](https://github.com/NethermindEth/nethermind) end to end
through the ZisK, SP1 and OpenVM provers, and on Airbender.

These modules have the most checks in the repository: contracts, unit tests
on the real ISA, fuzzing and machine-checked proofs. Refer to
[Verification](verification.md).

---

## Under the hood

The rest of this page is developer reference. It gives the wrapped symbols,
the data structures and the assembly of each module. The
[architecture page](architecture.md#stage-2--the-link-command) shows the
position of these objects in the link line.

The modules are in `src/bflat/modules/`. Each module has a
`module.c`/`module.cpp`/`module.S` source and a `bflat-manifest.json`. The
manifest declares the sources, the objects, their position in the link and
the linker switches (mostly `--wrap=` declarations).
`build.sh modules riscv64` compiles `module.o`.
`build.sh modules riscv32` compiles `module.rv32.o` from the same source. The manifest selects one of them by
`arch`. The [manifest format](manifest.md) page describes the file. The
sections below follow approximately the order in which the modules act at
run time.

## ubootstrap — runtime entry point
{: #ubootstrap }

**File:** `modules/ubootstrap/module.cpp`

A minimal re-implementation of the .NET NativeAOT bootstrap. It defines
`uBootstrap_main`, which does these steps:

1. Initialises the runtime (`RhInitialize`).
2. Registers the OS module. It gives the runtime the start and end of the
   `__managedcode` and `__unbox` sections that the linker defines. It also
   gives the classlib callback table (failfast, exception helpers, etc.).
3. Calls each module initialiser through `InitializeModules`.
4. Jumps into `__managed__Main`, the C# entry point that AOT emits.

It passes a fake argv, `["app"]`, because a zkVM has no command line.

## noos — cutting the OS surface the PAL reaches for
{: #noos }

**File:** `modules/noos/module.c` · linked in the `pre-libc` group on the
platforms with the `noos` flag

Each guest links one object that it does not ask for: the Unix PAL of the
runtime. The chain is short, and the guest cannot avoid it:

1. `RhpEHEnumInitFromStackFrameIterator` pulls in `EHHelpers.cpp.o`.
2. Its `RhFailFast` pulls in `PalUnix.cpp.o`.
3. `PalUnix` comes in whole. Thus each libc call in it goes live, together
   with `pal_io` and `pal_threading` of `libSystem.Native`.

Together they reach about 150 libc entry points. They pull 96 members of the
`libc.a` of musl into the image:

- crash dumps (`fork`, `execv`, `pipe`, `waitpid`);
- dynamic loading;
- CPU counting (`fopen` → `fscanf` → `__intscan`);
- cgroup probing, directory walking and signals.

One hart with no processes, no filesystem and no loader cannot service any
of these. In a proof, all of it is dead weight.

**Two behaviours.** The module puts each call in one of two groups:

- An operation that the guest cannot honour terminates with status **253**.
  `nofp` uses 255 and `nothread` uses 254 in the same way. A failure value
  that looks correct lets the guest continue and produce a wrong answer.
  For a proving system, that is the worst result.
- The string helpers that the same objects call are serviced with ordinary
  small implementations, because they are on live paths.

Some calls between these two groups get an honest answer where the answer
is known and harmless:

- `sigemptyset`/`sigaddset` on a 128-byte mask;
- `__libc_current_sigrtmin` and `__sched_cpucount`;
- `mprotect` returns success, because there is nothing to protect;
- `statfs`, `dladdr` and `fopen` return failure.

**It acts by definition, and not by `--wrap`.** A wrap sends the call to a
different function, but the member of musl and its instructions stay in the
image. The linker extracts an archive member only for a symbol that is
still undefined when the linker reaches the archive. This object is linked
before `libc.a`, so the members never come in. Where `pal` already wraps a
name, the two work together: the call goes to the `__wrap_` of `pal`, and
the definition here keeps musl out.

**It also has the .NET start-up for the platforms without the start-up of
musl.** The start-up has two halves with one body:

- `noos_start_main(argc, argv)` is the `noreturn` replacement for the
  `__libc_start_main` of musl. The `_start` of OpenVM and Airbender calls
  it.
- `noos_main(argc, argv)` is the half that returns. SP1 calls it through
  `__wrap_main`, because SP1 halts with the return value of `main`.

Both set the thread pointer, run `.init_array` and enter `uBootstrap_main`.

## nothread — one hart, said explicitly
{: #nothread }

**File:** `modules/nothread/module.c` · linked in the `pre-libc` group on
the platforms with the `no-atomics` flag

A zkVM guest has no threads: no thread-creation instruction, no interrupts,
one hart. This module replaces the locking, waiting and thread primitives
of musl with implementations for one hart. Thus no lock, futex or thread
path of musl is in the image.

- The acquire and release primitives do nothing and report success. With
  no second agent, this is the correct implementation.
- The primitives that **block** fail fast with status **254**. Nothing can
  wake them. A return (with `ETIMEDOUT`, for example) puts the caller in a
  loop on a condition that nothing can change.

Like `noos`, it acts by definition. It is linked before `libc.a` and
defines each global of each musl member that it displaces. Some of its
functions (`__membarrier_init`, `__do_cleanup_push`) are never called. They
prevent the link from pulling in `pthread_create.lo` for them.

The module also kept the `lr`/`sc`/`amo*` instructions of an rv64ima musl
out of the image. The musl of the layout is now rv64im and has no A
instructions. The module stays because it states what a lock and a wait
mean on one hart. The ZisK platforms do not set `no-atomics`. They keep the
primitives of musl, which also work on one hart.

## Platforms — entry point and memory map
{: #zkvm-zisk }

The entry points, the linker scripts and the exit, console and syscall
code of each zkVM are not bflat modules. They are the platforms in the
bflat-* packages. [Targets](targets.md) describes each one:
[zisk](targets.md#zisk), [zisk_sim](targets.md#zisk_sim),
[sp1](targets.md#sp1), [openvm](targets.md#openvm) and
[airbender](targets.md#airbender).
{: #zkvm-sp1 }

## pal — platform abstraction layer
{: #pal }

**File:** `modules/pal/module.c` ·
[symbols](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/modules/pal/bflat-manifest.json)

This module has the largest behaviour surface. It wraps 40 musl and runtime
symbols that .NET calls during startup or at run time:

| Wrapped symbol | What we return |
|----------------|----------------|
| `getenv` | `"1"` for three CoreLib settings (invariant globalization, predefined cultures only, the `ArrayPool` partition count), `NULL` otherwise |
| `getcwd` | `/` |
| `getpid`, `getegid`, `geteuid` | `1` |
| `sched_getaffinity`, `sched_getcpu` | Always CPU 0 |
| `sysconf` | Hard-coded answers (CPU count = 1, page size = 4 KiB, …) |
| `open` | Failure (`-1`): there is no filesystem |
| `__stdio_write` | Only with the `no-console` flag: the `zkvm_platform_write` of the platform |
| `clock_gettime`, `__clock_gettime` | `-1`: time is not deterministic, so CoreLib must use defaults |
| `pthread_create`, `pthread_sigmask` | No-ops |
| `mmap`, `munmap`, `mlock`, `munlock`, `mlockall`, `munlockall` | mmap goes to the bump allocator; lock calls are no-ops |
| `__libc_malloc_impl`, `__libc_realloc`, `__libc_free`, `calloc`, `__malloc_allzerop`, `aligned_alloc`, `posix_memalign`, `memalign` | A downward bump allocator on the heap symbols of the linker script |
| `signal`, `sigaction`, `sched_yield` | No-ops |
| `PalGetMaximumStackBounds` | The stack of the linker script. The real one aborts under static musl on RISC-V (`mremap` `EINVAL`). |
| `vfprintf`, `vfscanf`, `__isoc99_vfscanf`, `__floatscan` | A small `vfprintf`, so the `vfprintf` of musl, with its floating point, stays out of the link; the scanners consume nothing and convert nothing |
| `syscall` | Whitelist: `0x11b` → 0; all other numbers → the `zkvm_platform_syscall` of the platform, or `-1` without one |
| `exit`, `_Exit`, `abort` | The exit sequence of the platform, `zkvm_platform_exit`, through `zkvm_raw_exit` |

**The bump allocator.** It grows downward from `_kernel_heap_top`. It
stores an 8-byte size header before each allocation, and it never frees.
This is sufficient for a managed runtime that has its own GC on top (refer
to the `ugc-zero` module below). It also removes the `mallocng` of musl,
which is large and uses syscalls. Managed allocation gets to the allocator
in two ways:

- the inline bump that the JIT emits on the platforms with `JitZkBumpAddr`;
- the allocation helpers of [rhp](#rhp).

The bump pointer is in a **fixed-address cell**, and not in a `static`
variable. The cell is the top 8 bytes of RAM (`g_zk_bump_ptr`), which the
linker script gives. Thus the inline allocation that the JIT emits can use
the cell by a constant address. It shares the same pointer with this C
allocator. zkVM RAM is zero at boot. Thus the cell starts at `0`, and the
allocator sets it to `_kernel_heap_top` on first use. The cell below it,
`g_zk_heap_floor`, holds the lowest address that the heap can reach. `pal`
writes it once from an `.init_array` constructor.
[Targets](targets.md#summary) lists the address for each platform.

**Clean termination.** The `exit`/`_Exit` of musl issue `exit_group` (94).
No zkVM accepts it as the end of the program. ZisK, for example, accepts
only an `ecall` with `a7 == 93` as "program end", and otherwise the run
stops "not completed". Thus `pal` wraps all three terminators and calls the
`zkvm_platform_exit` of the platform. `abort` exits with `134` (128 +
SIGABRT). The exit sequence of each zkVM is in the entry point of its
platform.

**Allocation that must not fail.** `zkvm_alloc_or_abort(bytes, align)`
allocates from the bump heap. When the heap is full, it ends the guest.
It is for a runtime that cannot handle `NULL` from its allocator. The ZisK
platform binds the ziskos Rust allocator (`inline_bump_alloc_aligned`) to
it.

## rhp — Redhawk Platform shims
{: #rhp }

**File:** `modules/rhp/module.c`

Replacements for parts of the .NET runtime (19 wraps). The module has these
responsibilities:

1. **P/Invoke transitions.** `RhpPInvoke`/`RhpReversePInvoke` and their
   return halves do nothing. The frames that they make park a thread at a
   GC rendezvous. In a single-threaded, never-collecting guest, that
   rendezvous never comes.
2. **Allocation.** `RhpNewFast`, `RhpNewArrayFast` and
   `RhpNewPtrArrayFast` bump the allocation context of the thread in C and
   write the MethodTable. When the budget is empty, the original helper
   runs and fills it again from the heap (`uGCHeap::Alloc`).
   The module also wraps `RhAllocateNewObject` and `RhAllocateNewArray`.
3. **Subsystem stubs.** EventPipe registration. On the platforms with the
   `no-console` flag, which have no terminal, also the console
   initialisation (`InitializeTerminalAndSignalHandling`,
   `SetTerminalInvalidationHandler`), `SystemNative_Write`, `FStat`, `Dup`
   and `IsATty`.
4. **Write barrier.** `RhBulkMoveWithWriteBarrier` is a plain `memmove`.
   uGC never scans, so there is nothing to record.
5. **Fail-fast.** `FailFast`. In a build without unwind tables, also
   `RhpThrowEx` (refer to the next section).

### Managed exceptions without unwind tables

`--remove-eh`, or a platform with `"unwind": false` (Airbender), removes
the unwind tables. Then the runtime cannot dispatch a `throw`. Thus `rhp`
wraps `RhpThrowEx` (`"when": {"eh": "failfast"}`), and the guest exits on
a throw. The default build dispatches exceptions normally, through the
[eh module](#eh), and this path does not apply.

The JIT lowers a managed `throw` to `CORINFO_HELP_THROW`. That helper
calls `RhpThrowEx` with the exception object in `a0`. The wrapper gives
that object to a **weak** `ZkvmThrow` symbol:

```c
extern void ZkvmThrow(void *exceptionObj) __attribute__((weak));

void __wrap_RhpThrowEx(void *exceptionObj)
{
    if (ZkvmThrow != NULL) { ZkvmThrow(exceptionObj); return; }
    exit(1);
}
```

A program can export `ZkvmThrow` through
`[UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]`. Then it gets full
control of the throw, and it receives the live `Exception` reference. The
`a0` pointer *is* the managed object reference. A program that does not
export it also links. The weak reference stays null, and the wrapper calls
`exit(1)`. No `catch` or `finally` runs on this path, because the guest
ends. `FailFast` has a message string and no exception object. Thus it
stays on the plain `exit(1)` path and does not go through `ZkvmThrow`.
Refer to the
[ExceptionHandler sample](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples/ExceptionHandler).

`RhpReversePInvoke` and `RhpReversePInvokeReturn` do nothing, so that the
throw path can enter the handler. The real CoreLib transition attaches the
thread and parks it at a GC-safe point. That is necessary only for a
native→managed boundary that the thread enters in preemptive mode.
`__wrap_RhpThrowEx` enters a managed handler (an `[UnmanagedCallersOnly]`
method) when the thread is already cooperative. There, the real transition
waits in a loop for a GC rendezvous. In the single-threaded,
never-collecting zkVM, that rendezvous never comes.

## rhp_native — assembly reference-assignment helpers
{: #rhp-native }

**File:** `modules/rhp_native/module.S`

uGC has no card table and never collects, so no GC write barrier has work
to do. Four reference-assignment helpers become the bare store, with no
heap-bounds check and no card mark:

| Helper | Body |
|--------|------|
| `__wrap_RhpAssignRefRiscV64` (`__wrap_RhpAssignRefRiscV32` on riscv32) | store `t4` at `t3` |
| `__wrap_RhpCheckedAssignRef` | store `t4` at `t3` |
| `__wrap_RhpByRefAssignRef` | load from `t5`, store at `t3`, then advance `t5` and `t3` by one reference |
| `__wrap_RhpAssignRef` | store `a1` at `a0` (the C convention) |

**The single-assign helpers keep `t3`.** The `WriteBarriers.S` of .NET 10
incremented `t3` by 8. .NET 11 keeps it, and the JIT uses `t3` again after
the call. A stale increment moves the next use 8 bytes too high. For
example, `Dictionary<__Canon,TValue>.TryInsert` stores the key through this
helper and then writes the value at `0x10(t3)`. Thus the dictionary loses
each value. Only the ByRef helper advances its registers.

One source serves both widths. The assembler runs without the C
preprocessor. Thus the riscv32 build selects `lw`/`sw` and a 4-byte
reference with `--defsym BFLAT_RV32=1`.

## tls — minimal thread-local storage
{: #tls }

**File:** `modules/tls/module.c`

A static 100 KiB buffer is the TLS. On first access, the module copies
`.tdata` into it, sets `.tbss` to zero, and returns its address. There is
one thread, so there is only one TLS block. The module wraps
`__tls_get_addr`, `__init_tls`, `__init_tp` and `__copy_tls`. They use this
buffer in place of the dynamic-loader logic of musl.

The RISC-V psABI biases a DTPREL offset by `-0x800` (the `DTP_OFFSET` of
musl). Thus `__wrap___tls_get_addr` adds `0x800` to the offset of its
argument.

## nofp — floating-point runtime stubs
{: #nofp }

**File:** `modules/nofp/module.c` · linked in the `early` group

A `--wrap` stub for each transcendental libm function that the runtime
references (`sin`, `pow`, `exp`, `log`, …; 38 names). The math helpers of
the runtime reference them, but those paths do not run on the zkVM. The
bundled musl is built for `rv64im`, so its implementations can link and
run. The trap is a policy decision: a transcendental has no place in a
proof, and a guest that gets to one has a bug.

Each stub **traps**. The body calls a `noreturn` helper that exits with
status 255. The policy is in one function (`nofp_trap`).

The module does not stub these:

- The soft-float builtins (`__adddf3`, `__muldf3`, …). They are the
  floating-point arithmetic of the guest. `libgcc.a` (on riscv32, the
  compiler-rt builtins) defines them.
- The exact operations: `ceil`, `floor`, `modf`, `sqrt`, `fma`, `scalbn`,
  `fmod` and their float forms. Each has one correctly rounded IEEE 754
  result, like `+` and `/`. CoreLib calls them for ordinary work: double
  formatting, `DateTime.Add*`, `HashHelpers.IsPrime`, and the `%` operator
  on `double`.

One wrap does not trap: `__wrap_asprintf` returns `-1`. Only the cgroup
parsing references it, and pal already stubs that parsing. `-1` is the
documented failure result, and its callers handle it. Thus, if the guest
ever gets to that path, it continues with less function and does not abort.

## eh — synthetic program headers for the unwinder
{: #eh }

**File:** `modules/eh/module.c`

The unwinder of the runtime finds its DWARF tables through
`dl_iterate_phdr`. It needs a `PT_LOAD` that covers the queried PC, and a
`PT_GNU_EH_FRAME` over `.eh_frame_hdr`. A zkVM image has no program headers
to walk. The zkVM makes memory from the segments and jumps to the entry
point. Thus there is no loader and no auxv, and the ELF header is not in
memory.

libunwind reads only what the callback gives it. Thus this module wraps
`dl_iterate_phdr` and describes the image from linker-script symbols:

- one `PT_LOAD` over the executable range (`__image_text_start` …
  `__image_text_end`; the script extends that range across `__managedcode`
  and `__unbox`);
- one `PT_GNU_EH_FRAME` over `.eh_frame_hdr`.

The image is not position independent, so `dlpi_addr` is 0 and the vaddrs
are absolute. A build without unwind tables has an empty `.eh_frame_hdr`.
Then the module reports only the load segment, and the lookup fails
cleanly. It does not decode a stripped range.

Frama-C proves its contracts. Refer to
[Fuzzing and machine-checked proofs](verification.md#fuzzing-and-machine-checked-proofs).

## rng_stupid — deterministic PRNG
{: #rng-stupid }

**File:** `modules/rng_stupid/module.c`

A linear-congruential PRNG with the seed `0x34095153`. It wraps:

- `minipal_get_cryptographically_secure_random_bytes` (returns 0 on success)
- `minipal_get_non_cryptographically_secure_random_bytes`. It gives the
  hash seeds (Marvin, `HashCode`). Thus it is necessary for determinism,
  although nothing here is cryptographic.
- `CryptoNative_GetRandomBytes` (returns 1 on success, the opposite
  convention from the minipal pair; each value matches what its caller
  expects)
- `CryptoNative_EnsureOpenSslInitialized` (returns 0; there is no OpenSSL)

A zkVM cannot read `/dev/urandom`. A random number also makes the proof
non-deterministic. The PRNG gives the same bytes for the same execution,
and proving requires this. The caller must make sure that its algorithm
accepts non-cryptographic randomness.

## gs_cookie — neutralised stack cookie
{: #gs-cookie }

**File:** `modules/gs_cookie/module.c`

One line: `__wrap___security_cookie = 0`, in `.data`, bound through
`--wrap=__security_cookie`.

Upstream .NET uses a GS cookie (stack canary) to find buffer overruns. The
JIT copies a process-global `__security_cookie` into each guarded frame and
checks it again on return. The runtime sets that global **once at
startup**, from a timer (`minipal_lowres_ticks`), in a read-only page. A
zkVM supports neither half:

- **No entropy.** There is no clock. A cookie from a timer is either
  constant (thus no protection) or different on each run. A different value
  on each run makes the proof non-reproducible.
- **No page protection.** `mprotect` / `PalVirtualProtect` does nothing in
  the [pal](#pal) layer. A read-only `.rodata` cookie also conflicts with
  the code/data-split layout that the postprocessor manages.

Thus the module sets the cookie to the constant `0`, and the check of the
JIT always passes. This **disables the stack canary**. We accept this for a
single-threaded, deterministic guest with no untrusted boundary in the
process. The symbol is in `.data`, so it also stays out of the read-only
segment that the postprocessor changes.

Two paths get to this symbol:

- With `--stdlib dotnet`, the JIT still emits the check and binds it to
  this wrapped `0`.
- For zerolib builds, bflat tells ILC to emit no GS cookies
  (`SettingsTunnel.EmitGSCookies = false`). ILC then puts a constant into
  the code and emits no reference.

## stdcppshim — C++ allocator shims
{: #stdcppshim }

**File:** `modules/stdcppshim/module.cpp`

`operator new(size_t)` and `operator new[](size_t)`. Each one calls
`malloc`, and neither returns null. The GC code of the .NET runtime is C++
and uses `new` in some places. Without these shims, we must link a full
libc++. On riscv32, the module also defines `__cxa_pure_virtual`, the
vtable slot of a pure virtual function (the interfaces of uGC). It exits
with status 255. The riscv64 layout gets it from its libsupc++, and the
riscv32 layout has no C++ runtime library.

## rust_sys — Rust compatibility layer
{: #rust-sys }

**File:** `modules/rust_sys/module.c`

A single function: `__wrap_sys_alloc_aligned` calls our bump allocator.
Some Rust libraries in adjacent precompile binaries call it. The wrapper is
always in the link, and it costs nothing.

## ugc-zero — minimal GC
{: #ugc-zero }

**Pulled from:** the uGC release archives. `build.sh modules` checks them
against the signature of the release (refer to
[Package signatures](signing.md)) and unpacks them:

| Arch | Release | Archive | Directory |
|------|---------|---------|-----------|
| riscv64 | v1.0.13 | `ugc-riscv64.tar.gz` | `modules/ugc-zero/release/` |
| riscv32 | v1.0.15 | `ugc-riscv32.tar.gz` | `modules/ugc-zero/release-riscv32/` |

v1.0.15 is the first release whose riscv32 objects align each object to 8
bytes, as the riscv32 runtime sizes them. The upstream source is in
[`NethermindEth/uGC`](https://github.com/NethermindEth/uGC).

A full replacement for the .NET GC: `uGC.cpp`, `uGCHandleManager.cpp`,
`uGCHandleStore.cpp`, `uGCHeap.cpp`, `ugc_core.c`, `ugc_zalloc.c`. It
implements the GC interface but never collects. Each allocation goes
directly to the bump allocator below it. For the proof workload, this is
acceptable: each execution is short, and the heap can hold all of its
working set. `--wrap=GC_Initialize` and `--wrap=GC_VersionInfo` send the GC
discovery of the runtime to this shim. The manifest also sets
`JitZkNoGcMustInit=1`. uGC never scans a stack, so the JIT does not have to
set the GC slots of a frame to zero.

---

## guest_subst — ILC-stage substitutions
{: #guest-subst }

This module has no object file. `guest_subst` has the data that changes
*managed* code before the compiler makes RISC-V code from it:

- [`substitutions.xml`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/modules/guest_subst/substitutions.xml),
  an ILLink substitutions file;
- two feature switches;
- the `il_float_allow` list of `--error-on-float`.

Its content has two reasons:

- The guest has one hart. The substitutions stub the thread-pool tuning and
  the processor queries.
- The guest has no OS data. `System.Globalization.Invariant` and
  `System.TimeZoneInfo.Invariant` are on. A zkVM has no ICU data and no
  zoneinfo files, and a proof must not depend on them.

Floating point is not a reason: soft-float arithmetic and the exact libm
operations run. All five platforms require the module. The build copies
the file into the layout, and the driver reads it when it builds a guest.
[Stage 1.5](architecture.md#stage-15--ilc-stage-substitutions) describes
the mechanism.

It is in the modules because it answers the same type of constraint in the
same way: it replaces, and it does not patch. It acts one stage earlier,
on IL and not on symbols.

## Build flow for modules

`build.sh modules <arch>` runs `bflat-modules plan --arch <arch>` (in
`src/bflat-modules/`). That command reads the `bflat-manifest.json` of each
directory in `src/bflat/modules/`. It keeps the sources and downloads whose
`when` matches the architecture. Then, for each module, `build.sh` does
these steps:

1. Downloads the files that the `external` section of the manifest names:
   a release archive (`repo` + `tag` + `release`). It checks the archive
   against the signature of the release and unpacks it into `dir`.
2. Compiles each `.c` file in `sources` with the common flags of the
   architecture. It compiles each `.cpp` file with `clang++` and the same
   flags.
3. Assembles each `.S` file.

| | riscv64 | riscv32 |
|-|---------|---------|
| C and C++ | `clang --target=riscv64-linux-gnu -march=rv64imad -mabi=lp64 -mcmodel=medany -flto=full -funified-lto -O3`, against the `riscv64-linux-gnu` sysroot | `clang --target=riscv32-unknown-linux-musl -march=rv32im -mabi=ilp32 -mcmodel=medany -O2 -ffunction-sections -fdata-sections -fno-stack-protector`, against `RV32_SYSROOT` (the `crossrootfs-musl-riscv32` asset of the dotnet-riscv release, with its libc++ headers). No LTO. |
| Assembly | `riscv64-linux-gnu-as --march=rv64ima --mabi=lp64` | `clang --target=riscv32-unknown-elf -march=rv32im -mabi=ilp32 -Wa,--defsym,BFLAT_RV32=1` |
| Output | `module.o` | `module.rv32.o` |

There is no riscv32 GCC. Thus clang compiles and assembles all riscv32
code.

`-mabi=lp64` (and `ilp32`) gives each object the soft-float marker in its
ELF header, the same as the runtime and musl in the layout. bflat links the
layout as it is and changes no file. `bflat-modules check-abi` checks each
object of the layout when the build makes the layout (refer to the
[`codegen` section](manifest.md#codegen-instruction-set-abi-and-compiler-options)
of the manifest format).

Then the bflat build runs `bflat-modules pack`. It copies each manifest and
the files that it names into `lib/modules/<name>/` in the compiler layout.
