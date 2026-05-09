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
        <ziskLibPath>/rhp_native.o \
        --wrap=RhpAssignRefRiscV64 --wrap=RhpCidResolve \
        <ziskLibPath>/pal.o \
        --wrap=getenv --wrap=getcwd ... --wrap=__stdio_write \
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
