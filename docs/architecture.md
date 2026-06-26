---
layout: default
title: Architecture
eyebrow: How it works
lead: >
  The full path from a C# source file to a Zisk-ready ELF, stage by stage,
  with the responsible source files called out at every step.
prev: /runtime/
next: /modules/
---

<div style="background: var(--bg-elev-1); border: 1px solid var(--border); border-radius: var(--radius-lg); padding: 24px; margin: 16px 0 32px;">
  {% include pipeline-diagram.html %}
</div>

Everything is orchestrated by `BuildCommand.cs` in `src/bflat/`. The
extra steps unique to this fork only run when `--libc zisk` or
`--libc zisk_sim` is passed.

## Stage 1 — Microsoft's NativeAOT (ILC) emits an object file

ILC is the .NET NativeAOT compiler — the same one shipped by Microsoft
with the standard .NET SDK. bflat embeds Roslyn for parsing and then
hands the IL to ILC; both pieces are stock, unmodified upstream. Using
Microsoft's compiler directly means the C#-to-native translation is
covered by the same testing the rest of the .NET ecosystem benefits
from — we inherit the safety, correctness, and portability of the
mainline toolchain rather than maintaining our own AOT.

The output is a single RISC-V64 ELF relocatable that contains:

- compiled managed code (`__managedcode` section);
- type system metadata;
- module initialisation tables;
- references to runtime symbols (GC, exception handling, dispatch, …).

When the target is `zisk` or `zisk_sim`, ILC is told:

| Switch | Effect |
|--------|--------|
| `--no-globalization` | Forces invariant culture; lets the rng/security/locale stubs answer "yes, I'm en_US" |
| `--no-pie` (implicit for zisk) | Position-independent code is incompatible with the fixed memory layout |
| `--feature *` | Various opt-outs that prune reflection-heavy code paths |

bflat also drops in a `CustomILProvider` that intercepts a handful of
methods (e.g. `OutOfMemoryException.GetDefaultMessage`) and replaces
their IL with trivial bodies — these methods would otherwise drag in
globalization tables that we cannot honour. References are resolved
against the runtime's `lib/<arch>/<os>/<libc>` directory, downloaded
from the [dotnet-riscv](https://github.com/NethermindEth/dotnet-riscv)
release matching the bflat version.

### zkVM RyuJIT codegen knobs

In optimized builds (`-O`, i.e. `optimizationMode != None`)
`BuildCommand.cs` also passes a fixed set of RyuJIT tuning knobs to ILC.
RyuJIT parses these integer values as **hexadecimal with no `0x` prefix**
(`JitConfigProvider.getIntConfigValue` uses `NumberStyles.AllowHexSpecifier`),
so `"2000"` means `0x2000` = 8192.

| Knob | Value | Effect |
|------|-------|--------|
| `JitObjectStackAllocation` | `1` | Enable escape-analysis stack allocation |
| `JitObjectStackAllocationSize` | `2000` (8192) | Raise the max stack-allocatable object size (default `0x210` = 528). The in-loop heap restriction is lifted by runtime patch `25_stackalloc_aggressive_riscv64` |
| `JitExtDefaultPolicyMaxIL` | `200` (512) | Max inlinee IL size (default `0x80` = 128). Stays on `ExtendedDefaultPolicy`, which weighs code growth, rather than `JitAggressiveInlining`, which overflows the fixed ZisK ROM |
| `JitExtDefaultPolicyMaxBB` | `10` (16) | Max inlinee basic blocks (default 7) |
| `JitRiscV64DmaCompare` | `1` | Lower constant-size `SpanHelpers.SequenceEqual` to the `csrs 0x814, src ; addi rd, dst, count` idiom that the ZisK transpiler folds into one `dma_xmemcmp` step. ZisK-only — needs runtime patch `30_dma_memcmp_inline_riscv64` |
| `RiscV64ElideLeafRaSave` | `1` | Elide RA spill/reload + frame in eligible leaf methods. Needs runtime patches 23 + 31, which refuse to elide methods whose LIR uses `REG_RA` as scratch (`GT_JCMP`, comparisons, `GT_MULHI`) or use FP |

These knobs trade ROM/`.text` size for fewer heap allocations and tighter
hot paths; the comments in `BuildCommand.cs` note which to lower
(`JitExtDefaultPolicyMaxIL`, `JitObjectStackAllocationSize`) if a workload
overflows the fixed ZisK ROM.

## Stage 2 — The link command

The final ELF is produced by `ld.lld` (Clang's linker, shipped with
bflat). The command line for `--libc zisk` looks roughly like this:

```bash
ld.lld -static -nostdlib -m elf64lriscv \
    -T <ziskLibPath>/script.ld \
    <ziskLibPath>/entrypoint.o \
    <ziskLibPath>/nofp.o \
    --whole-archive \
        <ziskLibPath>/ubootstrap.o \
        <ziskLibPath>/stdcppshim.o \
        --wrap=inline_bump_alloc_aligned \
        <ziskLibPath>/rhp.o \
        --wrap=RhpNewFast --wrap=RhpNewObject ... \
        --wrap=RhpThrowEx \
        --wrap=RhpReversePInvoke --wrap=RhpReversePInvokeReturn ... \
        <ziskLibPath>/rhp_native.o \
        --wrap=RhpAssignRefRiscV64 --wrap=RhpCidResolve \
        <ziskLibPath>/pal.o \
        --wrap=getenv --wrap=getcwd ... --wrap=__stdio_write \
        --wrap=exit --wrap=_Exit --wrap=abort \
        <ziskLibPath>/tls.o \
        --wrap=__tls_get_addr --wrap=__init_tls ... \
    --no-whole-archive \
    <ziskLibPath>/rng_stupid.o \
    --wrap=minipal_get_cryptographically_secure_random_bytes ... \
    <ziskLibPath>/rust_sys.o --wrap=sys_alloc_aligned \
    --wrap=GC_Initialize --wrap=GC_VersionInfo \
    <ziskLibPath>/uGC.cpp.obj <ziskLibPath>/uGCHandleManager.cpp.obj \
    <ziskLibPath>/uGCHandleStore.cpp.obj <ziskLibPath>/uGCHeap.cpp.obj \
    <managedcode.o> \
    <runtime libraries from dotnet-riscv>
```

Two mechanisms are doing all the work:

- **`--whole-archive`** forces the linker to pull in every object from
  the listed modules, even if no one references them. This is how the
  bootstrap and TLS code reaches the binary.
- **`--wrap=symbol`** rewrites every reference to `symbol` into a call to
  `__wrap_symbol`, while preserving the original under the name
  `__real_symbol`. This is how a single C function in `pal/module.c`
  (such as `__wrap_getenv`) replaces musl's implementation without
  touching musl.

The full list of wrapped symbols and the modules that satisfy them is on
the [Modules](modules.md) page.

## Stage 3 — Postprocessing (Zisk only)

For `--libc zisk`, the linked ELF is fed through `scripts/patch_elf.py`
with the following options:

```
--fix-init-array  --fix-tdata  --remove-eh  --trim-bss
```

Each pass is a small, self-contained ELF-header rewrite that fixes a
concrete loader behaviour Zisk wouldn't otherwise accept:

| Pass | What it does | Why Zisk needs it |
|------|--------------|-------------------|
| `--fix-init-array` | Forces `.init_array` to `SHT_PROGBITS`, alignment 8 | Otherwise the loader skips the section and module initialisers never run |
| `--fix-tdata` | Adds `ALLOC \| WRITE \| TLS` flags, alignment ≥ 8 | Without TLS bit the loader doesn't include `.tdata` in the program header table; the TLS shim then sees zero bytes |
| `--remove-eh` | Drops `.dotnet_eh_table`, `.eh_frame_hdr`, `.eh_frame` | We never unwind; throwing trips `__wrap_RhpThrowEx`. The tables are large dead weight |
| `--trim-bss` | Removes the `.bss` section header | Linker scripts already provide explicit heap symbols; trimming `.bss` removes a region the prover would otherwise account for |

For `--libc zisk_sim` the postprocessor is **not** run. The simulator
target is meant to debug under GDB / QEMU on real hardware, where these
loader quirks don't apply.

## Stage 4 — Boot

When the binary starts (real, simulated, or proven), the entry point is
`_start` from `modules/zkvm_zisk{,_sim}/module.S`:

1. Set `gp` to `_global_pointer` and `sp` to `_init_stack_top` (both
   provided by the linker script).
2. Tail-call `__libc_start_main(uBootstrap_main, 1, argv_vec, …)`.
3. `uBootstrap_main` (in `modules/ubootstrap/module.cpp`) calls
   `RhInitialize`, registers the managed-code range, runs all module
   initialisers, then jumps into `__managed__Main` — i.e., the C# `Main`.

There is no kernel underneath any of this. Every syscall the runtime
might make is either wrapped to a no-op, returned as a constant, or (in
`zisk_sim`) routed to musl's real implementation.

## Exit and exceptions

The program ends only when an `ecall` with `a7 == 93` (ZisK `CAUSE_EXIT`)
is issued — musl's `exit`/`_Exit`/`abort` use `exit_group` (syscall 94),
which ZisK does not treat as program end. So `pal` wraps all three to
`zkvm_raw_exit`, which emits the real ZisK exit ecall (see the
[pal module](modules.md#pal)).

A managed `throw` is lowered to `RhpThrowEx`. The `rhp` wrapper hands the
exception object to an optional, **weak** `ZkvmThrow` symbol that a program
may export via `[UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]`; programs
that don't define it fall back to `exit(1)`. So the handler can be entered
from the throw path, `RhpReversePInvoke`/`RhpReversePInvokeReturn` are
no-op'd — the real transition would spin on a GC rendezvous that never
comes in the single-threaded, never-collecting zkVM. See the
[ExceptionHandler sample](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples/ExceptionHandler)
and the [rhp module](modules.md#rhp).
