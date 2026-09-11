---
layout: default
title: Modules
eyebrow: Link-time adaptations
lead: >
  Why a zkVM needs these link-time modules, what each one replaces, and the
  constraint it answers — with the per-module implementation detail at the end.
prev: /architecture/
next: /build/
---

## What they are

Each module is a small, self-contained object file — C, C++, or assembly —
that the linker pulls into the final binary, overriding a specific symbol
via `--wrap=`. Together they replace exactly the parts of .NET, musl and
compiler-RT that a zkVM cannot honour, **without editing a single line of
upstream source**: a .NET version bump is a rebase, not a fork.

A zkVM gives you far less than a Linux host: no kernel (so no syscalls,
files, threads, signals or clock), no floating-point hardware, no compressed
instructions, no randomness, and a requirement that every run be
bit-for-bit reproducible. Each module closes one of those gaps.

| Module | What it provides | Constraint it answers |
|--------|------------------|-----------------------|
| [ubootstrap](#ubootstrap) | Runtime entry point — brings .NET up and calls `Main` | No glibc-style startup / OS loader |
| [noos](#noos) | Displaces the musl members the runtime's Unix PAL drags in | No kernel, no processes, no filesystem, no loader |
| [zkvm_zisk · zkvm_zisk_sim](#zkvm-zisk) | `_start` + the memory layout the prover expects | No kernel; fixed prover memory map |
| [zkvm_sp1 · zkvm_openvm](#zkvm-sp1) | The same, for SP1 and OpenVM | Each VM's own map and halt protocol |
| [pal](#pal) | env, scheduling, files, time, memory, clean exit | No OS to answer syscalls |
| [rhp](#rhp) | Allocation, dispatch, exception/exit handling | Single-threaded, never-collecting runtime |
| [rhp_native](#rhp-native) | GC ref-assign + dispatch trampoline (asm) | No write barrier; bespoke dispatch |
| [eh](#eh) | Synthetic program headers for the unwinder | No loader, so nothing maps the real ones |
| [tls](#tls) | A single thread-local block | One thread, no dynamic loader |
| [nofp](#nofp) | Soft-float and libm entry points that trap loudly | No floating-point hardware |
| [zisk_subst](#zisk-subst) | ILC-stage substitutions and C# snippets — data, not an object file | Managed code that carries FP |
| [rng_stupid](#rng-stupid) | Deterministic PRNG | No `/dev/urandom`; proofs must reproduce |
| [security-stub](#security-stub) | Security/GSS functions return failure | Unused network paths must still link |
| [gs_cookie](#gs-cookie) | Stack cookie pinned to a constant | No clock for entropy, no page protection |
| [stdcppshim](#stdcppshim) | `operator new` / `new[]` | Runtime's C++ needs them without libc++ |
| [rust_sys](#rust-sys) | `sys_alloc_aligned` | Interop with adjacent Rust precompiles |
| [ugc-zero](#ugc-zero) | A GC that allocates but never collects | Short-lived proof workloads |

## Results

No upstream source is modified to make C# run in a zkVM: every adaptation is
an object the linker pulls in, or — for `zisk_subst` — data the driver
applies one stage earlier. The same set carries Nethermind's
[StatelessExecutor](https://github.com/NethermindEth/nethermind) end to end
through a zkVM prover.

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
`module.c`/`module.cpp`/`module.S` source, an optional `module_params.yml`
listing its linker switches (mostly `--wrap=` declarations), and a compiled
`module.o` from `build.sh modules riscv64`. The sections below follow
roughly the order they matter at runtime.

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

## noos — cutting the OS surface the PAL reaches for
{: #noos }

**File:** `modules/noos/module.c`

A guest links one object it never asked for: the runtime's Unix PAL. The
chain is short and unavoidable — `RhpEHEnumInitFromStackFrameIterator` pulls
in `EHHelpers.cpp.o`, whose `RhFailFast` pulls in `PalUnix.cpp.o` — and
`PalUnix` comes in whole, so every libc call anywhere inside it goes live,
together with `libSystem.Native`'s `pal_io`/`pal_threading`. Between them
they reach about 150 libc entry points and drag 96 members of musl's
`libc.a` into the image: crash dumps (`fork`, `execv`, `pipe`, `waitpid`),
dynamic loading, CPU counting (`fopen` → `fscanf` → `__intscan`), cgroup
probing, directory walking, signals. None of it can be serviced on one hart
with no processes, no filesystem and no loader. It is dead weight in a proof
on its own terms, and it became load-bearing once the `p4` blob set started
shipping musl built with the `C` extension, which none of these VMs decode.

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

**It also owns the .NET start-up for the non-Zisk targets**, in two halves
that share one body: `noos_start_main(argc, argv)` is the `noreturn`
replacement for musl's `__libc_start_main`, used by OpenVM's `_start`, and
`noos_main(argc, argv)` is the returning half, used by SP1 through
`__wrap_main` because SP1 halts with `main`'s return value. Both set the
thread pointer, run `.init_array` and enter `uBootstrap_main`.

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

## zkvm_sp1 / zkvm_openvm — entry point and memory map
{: #zkvm-sp1 }

**Files:** `modules/zkvm_sp1/{module.S,script.ld}`, `modules/zkvm_openvm/{module.S,script.ld}`

A `_start` that sets `gp`/`sp`, plus a linker script describing the prover's
address space. Neither VM sets `sp` for the guest, so `_start` has to,
exactly as their own Rust entry points do.

Where the two diverge from the Zisk pair is what `_start` calls next.

*OpenVM* skips musl's start-up entirely and calls **`noos_start_main`**
(`modules/noos`), which sets the thread pointer, runs `.init_array` and
enters `uBootstrap_main`. musl's `__libc_start_main` exists to parse an
auxv this guest does not have, and costs 126 instructions the transpiler
has to accept.

*SP1* must not bypass its own runtime entry: `sp1-zkvm`'s **`__start`**
initialises the Rust allocator and the public-values hasher that
`syscall_write` and `syscall_halt` depend on, and halts through the full
commit protocol with `main`'s return value. So `_start` calls `__start`,
and the .NET side is reached from there through `main` — the module's
`__wrap_main`, which calls `noos_main` (the returning half of
`noos_start_main`) and passes its result back. The `--wrap` is needed
because `libbootstrapper.o` also defines a `main`, the desktop runtime's,
which a zkVM guest never enters.

**The keccak permutation is named three times.** Nethermind's `KeccakHash`
reaches it through `Accelerators.KeccakF`, whose binding carries the ziskos
name `syscall_keccak_f`; SP1 spells the same operation `zkvm_keccak_permute`
and OpenVM `zkvm_keccakf`, and the eth-act standard covers `zkvm_keccak256`
without defining a permutation at all. Rather than make the managed side
target-aware, each entry module carries a one-instruction tail call under the
binding's name, in its own section so `--gc-sections` drops it — and with it
the reference to the bindings library — in a guest built without `--extlib`.

What differs between all four targets is the exit protocol, and that is not
in the entry point: `pal`'s `zkvm_raw_exit` owns it, because `exit`,
`_Exit` and `abort` all funnel through there. `pal.o` is built once and
shared, so each of these modules exports an absolute marker symbol
(`__zkvm_target_sp1`, `__zkvm_target_openvm`) that `pal` and `rhp` pick up
through **weak references**: non-`NULL` only in that target's link, absent
everywhere else, which is why Zisk keeps its original `a7 = 93` path
untouched.

| Aspect | `sp1` | `openvm` |
|--------|-------|----------|
| ISA | RV64IM, lp64 (no A, no C) | RV64IM, lp64 (no A, no C) |
| Image base | ROM `0x7a000000` (length `0x06000000`), RAM `0xa0020000` — see below | ROM `0x80000000`, RAM `0xa0020000` — the Zisk map |
| Address ceiling | 2^48 | 2^48 |
| Stack | `_init_stack_top = 0x78000000`; the whole range below it is reserved for the stack | `_init_stack_top` is placed `0x400000` above `.bss`, so `_end` sits above the stack — OpenVM's bump allocator starts at `_end` and would otherwise hand out stack |
| Bump-pointer cell | `0xbffefff8` — identical to Zisk | `0xbffefff8` — identical to Zisk |
| Exit | `ecall` with the syscall id in **`t0`**: `COMMIT` ×8, `COMMIT_DEFERRED_PROOFS` ×8, then `HALT` with the code in `a0` | `TERMINATE`, a custom-0 instruction (`0x0b`, funct3 0) whose exit code is an **immediate**, so a runtime code collapses to 0 or 1 |
| Console | `WRITE` syscall, caller's descriptor passed through (1 and 2 both reach the host) | `PrintStr` phantom instruction (`0x0b`, funct3 3, imm 1) — one channel, so the descriptor is ignored |
| Post-link | `patch_elf --nop-fences --nop-zero-words` (see below) | none |

**Why SP1's ROM sits at `0x7a000000` and not `0x80000000`.** SP1's native
x86 executor JITs the guest, and `sp1-jit`'s `jump_to_pc`
(`crates/core/jit/src/backends/x86/mod.rs`) narrows the code base with
`self.pc_base as i32`. A base of exactly `0x80000000` wraps negative there,
the jump-table index subtraction turns into an addition, and the first
indirect jump reads far past the table — reported as `invalid memory access
for opcode ld and address 0`, with no hint of the cause. The image therefore
lives below 2^31, and above SP1's `STACK_TOP` (`0x78000000`). Worth reporting
upstream: SP1 silently miscompiles any program based at or above 2^31.

**SP1 also needs a post-link pass.** `patch_elf --nop-fences
--nop-zero-words` rewrites the fences musl emits (SP1 has no `A` extension
and rejects the encoding) and any zero word left in `.text` into `NOP`. Both
rewrites are confined to function bodies taken from the symbol table, and
zero words only in gaps of at most 16 bytes that are entirely zero — that is,
alignment padding. Anything outside a function is counted, reported and left
alone, so a constant the compiler parked in `.text` is never touched.

**Segment flags matter on SP1.** Its loader rejects a segment without
`PF_R` and a segment that is both writable and executable, and it records
`p_flags` as the initial per-page protection. The three loadable segments
are therefore `R+X` / `R` / `R+W`, and the read-only segment is page-aligned
so a page shared with `.text` cannot lose its execute bit.

**Both eagerly decode `.text`.** SP1 transpiles every word of every `PF_X`
segment when the ELF is loaded and panics on one it cannot decode; OpenVM
does the same and fails with `TranspilerError::ParseError`. A zero-filled
alignment gap is an illegal encoding, so both scripts fill `.text` padding
with `NOP` (`=0x13000000`) and keep `.rodata` and the unwind tables in a
separate, non-executable segment.

**Both require natural alignment.** SP1 raises `InvalidMemoryAccess` for any
`LH`/`LW`/`LD`/`SH`/`SW`/`SD` off its boundary, and OpenVM's load/store chip
only accepts aligned shift amounts. `BuildCommand` therefore forces
`JitNoUnalignedAccess=1` for both, the same expansion `--no-unaligned-access`
asks for by hand, **and** `JitRiscV64StrictAlign=1`. The first covers the
accesses the JIT knows are unaligned; the second covers the ones it cannot
know about, where the address is a byref whose alignment is only established
at run time. Lowering proves what it can structurally and leaves the rest
flagged, and codegen emits an `andi`/`bnez` check with the wide access on the
fast path and a byte-wise expansion on the slow one. Without it a mainnet
block took more than 200k misaligned accesses across 44 sites.

**The inline allocator, and why each target names its own cell.** dotnet-riscv
fixup 26 replaces the object-allocation helper call with an inline bump on the
cell, emitted as a bare constant with no relocation. The address is not baked
into the JIT: it comes from `JitZkBumpAddr`, which `BuildCommand` sets per
target. All four currently use `bffefff8`; the knob exists because a VM whose
guest memory ends lower (OpenVM's rv32 line, at `0x20000000`) needs
`1ffefff8` instead.
The knob defaults to 0 and then nothing is inlined, so a plain riscv64 target
(`musl`, `glibc`) is never handed an absolute guest address to write to.

The value must equal `g_zk_bump_ptr` in the target's `script.ld`. The cell 8
bytes below it, `g_zk_heap_floor`, holds the lowest address the heap may reach;
the inline sequence loads it and fail-fasts rather than bumping down into
`.bss`, `.data` and the stack when the heap is exhausted. That guard is a single
`bgeu` to the method's one shared fail-fast block, and the floor load is marked
invariant so it is hoisted out of loops and shared between allocation sites —
one instruction more than an unchecked bump.

`pal` publishes the floor from an `.init_array` constructor, before the runtime
entry point, and never rewrites it. That ordering is what makes the load
invariant; publishing it lazily, or moving the heap floor at runtime, would
break every inline allocation site.

**Status.** Both targets run end to end in their provers. Nethermind's
stateless guest executes all nine mainnet blocks of the `stateless-tests`
suite on each of SP1, OpenVM and Zisk, with matching output; SP1 reports zero
misaligned accesses. `pal`'s `__wrap_syscall` returns `-1` on both targets
rather than issuing an `ecall`, because unlike Zisk, SP1 treats an unknown
syscall id as a hard error and OpenVM has no `ecall` handler at all.

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
and never frees. That is enough to satisfy a managed runtime whose own GC
sits on top — see the `ugc-zero` module below — and it removes any need for
musl's `mallocng`, which is large and uses syscalls. Managed allocation
reaches it indirectly: objects come from the runtime's own riscv64
`AllocFast.S` fast path, whose allocation-context budget `uGCHeap::Alloc`
refills from this heap.

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

## rhp — Redhawk Platform shims
{: #rhp }

**File:** `modules/rhp/module.c`

Replacements for parts of the .NET runtime itself. Responsibilities:

1. **P/Invoke transitions.** `RhpPInvoke`/`RhpReversePInvoke` and their
   return halves become no-ops: the frames they build park a thread at a GC
   rendezvous that never comes in a single-threaded, never-collecting guest.
2. **Subsystem stubs.** EventPipe and EventSource registration, and — on
   the real zkVM, which has no terminal — console initialisation and
   `SystemNative_Write`.
3. **Write barrier.** `RhBulkMoveWithWriteBarrier` is a plain `memmove`;
   uGC never scans, so there is nothing to record.
4. **Integer replacements for FP-carrying helpers.** `HashHelpers.IsPrime`
   (one copy per assembly that embeds the shared source) and
   `FrozenHashTable.CalcNumBuckets`, whose managed bodies are stubbed by the
   ILC substitutions.
5. **Fail-fast.** `FailFast`, and under `--remove-eh` also `RhpThrowEx` —
   see below.

### Managed exceptions under `--remove-eh`

With the unwind tables stripped a `throw` cannot be dispatched, so `rhp`
wraps `RhpThrowEx` and the guest exits on it. (The default build dispatches
exceptions normally; that path runs through the [eh module](#eh) and
nothing here.)

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

A definition for every soft-float compiler-RT helper (`__addsf3`,
`__divdf3`, `__floatsidf`, `__fixunsdfsi`, …) and for the libm surface
the runtime references (`pow`, `sqrt`, `fmod`, …). They exist because a
RISC-V toolchain emits calls to these even when `double` appears only in
code that is never reached; without the module the link fails with
hundreds of unresolved symbols.

Every one of them **traps**: the body calls a `noreturn` helper that exits
with status 255. Empty bodies would let a stray FP call return an undefined
register value and the run continue with a silently wrong result — the worst
failure mode for a proving system. The policy lives in one function
(`nofp_trap`).

One exception, deliberately not a trap: `__wrap_asprintf` returns `-1`.
It is referenced only by the cgroup parsing that pal already stubs out,
and `-1` is the documented failure result its callers handle — so if that
path is ever reached it degrades instead of aborting.

## eh — synthetic program headers for the unwinder
{: #eh }

**File:** `modules/eh/module.c`

The runtime's unwinder locates its DWARF tables through
`dl_iterate_phdr`: it wants a `PT_LOAD` covering the queried PC and a
`PT_GNU_EH_FRAME` over `.eh_frame_hdr`. A zkVM image has no program headers
to walk — ZisK materialises memory from segments and jumps to the entry
point, so there is no loader, no auxv, and the ELF header is not mapped.

libunwind reads only what the callback hands it, so this module wraps
`dl_iterate_phdr` and describes the image from linker-script symbols: one
`PT_LOAD` over the executable range (`__image_text_start` …
`__image_text_end`, which the script extends across `__managedcode` and
`__unbox`) and one `PT_GNU_EH_FRAME` over `.eh_frame_hdr`. The image is not
position independent, so `dlpi_addr` is 0 and the vaddrs are absolute. When
`.eh_frame_hdr` is empty — a `--remove-eh` link — only the load segment is
reported and the lookup fails cleanly rather than decoding a stripped range.

The module is linked with the unwind tables, so `--remove-eh` drops both.
Its contracts are proved with Frama-C; see
[Fuzzing and machine-checked proofs](verification.md).

## rng_stupid — deterministic PRNG
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

## zisk_subst — ILC-stage substitutions
{: #zisk-subst }

The odd one out: it ships no object file. `zisk_subst` carries the data
that removes floating point from *managed* code before it is ever compiled
to RISC-V64 —
[`zisk.substitutions.xml`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/modules/zisk_subst/zisk.substitutions.xml)
(an ILLink substitutions file) and
[`zisk.snippets.cs`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/modules/zisk_subst/zisk.snippets.cs)
(whole-body C# replacements). Both are copied into the layout and consumed
by the driver at guest-build time; the mechanism is described under
[Stage 1.5](architecture.md#stage-15--ilc-stage-substitutions).

It is grouped with the modules because it answers the same kind of
constraint in the same spirit — replace, don't patch — but it acts one
stage earlier, on IL rather than on symbols.

## Build flow for modules

`build.sh modules riscv64` walks every directory under
`src/bflat/modules/` and:

1. Compiles `module.c` with `clang --target=riscv64-linux-gnu
   -march=rv64imad -mabi=lp64 -mcmodel=medany -flto=full -funified-lto`.
2. Assembles `module.S` with `riscv64-linux-gnu-as --march=rv64ima --mabi=lp64`
   — assembly is the one input that does not go through clang, since
   bitcode does not apply to it.
3. Compiles `module.cpp` with `clang++` and the same flags as (1).
4. Patches the resulting object's ABI marker byte to keep the linker
   happy when mixing soft-float-marked and hard-float-marked objects.
5. If `module_params.yml` declares a remote `repo` + `tag` + release
   `file`, downloads the release tarball into the module's `release/`
   directory.

Step 4 — patching offset `0x30` of the ELF e_flags — clears the
hard-float bit so the bflat-side modules carry the `lp64` (soft-float)
marker in their ELF header.

That patch exists because the objects being linked together do not all
agree on their marker. The modules are compiled `-mabi=lp64` and the
runtime's native objects are tagged soft-float, but the C runtime bits
that come from the distribution feed are built for `rv64gc` and still
advertise a double-float ABI. `ld.lld` refuses to mix markers, so bflat
normalizes them. The codegen on either side is unchanged — this is purely
about the bits the linker checks for ABI consistency, and it is a
workaround: the proper fix is for those artifacts to be built for the
target ISA in the first place, which is what the runtime project is
moving to.
