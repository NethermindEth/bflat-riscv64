---
layout: default
title: Architecture
eyebrow: How it works
lead: >
  What the build pipeline produces, why each stage is needed, and the
  evidence it works. The exact mechanics are kept as background at the end.
prev: /runtime/
next: /modules/
---

<div style="background: var(--bg-elev-1); border: 1px solid var(--border); border-radius: var(--radius-lg); padding: 24px; margin: 16px 0 32px;">
  {% include pipeline-diagram.html %}
</div>

## What it produces

bflat-riscv64 turns an ordinary C# program into a **single, fully static
RISC-V64 ELF** that needs no operating system, no dynamic libraries, and
nothing installed on the target. The same source compiles for three
places without changes:

- a **zkVM prover**, where the binary is executed and proven (Zisk is the
  target supported today);
- **`qemu-riscv64`** or any RISC-V64 Linux host, for debugging;
- real RISC-V64 hardware.

One command — `bflat build` — does it end to end.

## Why the extra stages exist

Stock .NET NativeAOT already compiles C# to a native binary, but that binary
assumes a kernel, dynamic linking, floating-point hardware and compressed
instructions — none of which a zkVM provides.

bflat is a **compiler driver**: it contains no code generator of its own.
Roslyn turns C# into IL, Microsoft's ILCompiler turns IL into a RISC-V64
object, `lld` links it — all stock. What bflat adds around them is
**ILC-stage substitutions** that take floating point out of managed bodies,
a **link step** that swaps unsupported OS and runtime calls for zkVM-safe
ones, an **ELF postprocessor** that satisfies the prover's loader, and
**target-specific memory layouts**. Those stages run only for `--libc zisk`
and `--libc zisk_sim`; for every other target bflat behaves like upstream.

Keeping them outside .NET is the point: the runtime stays an official build
— upstream sources, upstream build system, plus a handful of riscv64 fixes
meant to be upstreamed (see [Runtime](runtime.md)) — so a version bump is a
rebuild instead of a merge. What goes through the pipeline is ordinary C#:
exceptions, allocation, interface dispatch and generics all work, and the
same source builds for the prover, QEMU and real hardware.

## How it works, at a glance

| Stage | What happens | Unique to this fork? |
|-------|--------------|----------------------|
| **1 · Compile** | Microsoft's stock NativeAOT (ILC) compiles C# to a RISC-V64 object | No — upstream, unmodified |
| **1.5 · Substitute** | Managed bodies that would emit floating point are replaced at ILC time | Yes |
| **2 · Link** | `ld.lld` statically links it, swapping unsupported OS/runtime calls for the [link-time modules](modules.md) | Yes |
| **3 · Postprocess** | A Python pass rewrites the ELF so the prover's loader accepts it (`zisk` only) | Yes |
| **4 · Boot & run** | A tiny assembly entry point starts the runtime and calls `Main` — no kernel underneath | Yes |

The driver lives in `src/bflat/`: `BuildCommand.cs` orchestrates the
pipeline, `ZkvmIL.cs` holds the IL-rewriting machinery, and
`ZkvmSubstitutions.cs` holds the policy — which methods are replaced, and
on which runtime versions.

---

## Under the hood

The rest of this page walks through each stage — the exact switches, the
link command, the postprocessor passes and the boot sequence.

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
| `--feature *` | Various opt-outs that prune reflection-heavy code paths |

`--no-pie` belongs here too, but it is *not* implied by `--libc zisk`: it is
an ordinary flag you pass. Position-independent code fights the fixed memory
layout, and the flag changes that layout, so a guest built without it can
fail in ways one built with it does not. Every guest in this repository and
in Nethermind's zisk build passes it explicitly.

References are resolved against the runtime's `lib/<os>/<arch>/<libc>`
directory, downloaded from the
[dotnet-riscv](https://github.com/NethermindEth/dotnet-riscv) release
matching the bflat version and variant.

## Stage 1.5 — ILC-stage substitutions

The target has no FPU, and the prover rejects an F/D opcode wherever it
appears — including in code that never executes, because the transpiler
translates the whole `.text`. Several CoreLib methods carry floating point
for reasons that are meaningless here: a thread-pool tuner sampling worker
counts, `Hashtable`'s `float` load factor, a hash helper bounding a loop
with `Math.Sqrt`. Rather than patch the runtime, those bodies are replaced
while ILC is compiling, by three mechanisms:

| Mechanism | Used for | Lives in |
|-----------|----------|----------|
| **ILLink substitutions** | Methods that fold to a constant or can be removed outright | `modules/zisk_subst/zisk.substitutions.xml` |
| **C# snippets** | Whole-body replacements too complex to express as a constant — the compat PRNG, the `Hashtable` ctor family, `ValueType` hashing | `modules/zisk_subst/zisk.snippets.cs` |
| **Targeted IL rewrites** | The handful of cases where only a subexpression is FP-carrying | `ZkvmIL.cs` |

The snippets are ordinary C#, compiled at guest-build time against the
guest's own reference set with accessibility checks disabled, so a snippet
may name private nested types. Letting Roslyn produce the replacement body
is far less error-prone than hand-emitting IL — and the arithmetic is
written to be exact rather than approximate: `Hashtable`'s `0.72f` load
factor becomes integer `× 72 / 100`, and a `TimeZoneInfo` initializer's
`AddMilliseconds(2)` becomes the tick-exact `20_000`.

**Drift is the failure mode to design against.** If a substituted method is
renamed or re-signatured by a runtime update, the substitution stops
applying and the FP-carrying original stays in the image — this happened
when `Number.FormatFloat`'s signature changed in .NET 11.

The C#-snippet map defends against it directly: every target must resolve or
the build fails with the full list of mismatches. Substitutions whose donor
reproduces an entire method body additionally verify the original's shape —
the `TimeZoneInfo` cctor donor is applied only while the original still
stores exactly the fields it initializes, and there is one donor variant per
known runtime field set.

ILLink entries in the XML have no such guarantee: an unmatched
`<method signature=…>` is an `IL2009` warning, not an error, and the
signature rendering itself differs between .NET majors, so an entry can be
correct for one and inert for the other. Treat the [ISA
gates](verification.md) as the check that actually holds, and read the build
output for IL2009.

### zkVM RyuJIT codegen knobs

`BuildCommand.cs` passes RyuJIT knobs to ILC in two groups, and the
distinction matters: the ISA gate below applies to **every** `zisk` /
`zisk_sim` build, while the tuning knobs apply only to optimized ones
(`-O`, i.e. `optimizationMode != None`).

RyuJIT parses these integer values as **hexadecimal with no `0x` prefix**
(`JitConfigProvider.getIntConfigValue` uses `NumberStyles.AllowHexSpecifier`),
so `"2000"` means `0x2000` = 8192.

**The ISA gate.** ZisK decodes the base 32-bit encoding only and the guest is
single-hart, so two extensions must never appear in generated code:

| Knob | Value | Effect |
|------|-------|--------|
| `EnableRiscV64Compressed` | `0` | Never emit a compressed (C) encoding. RyuJIT otherwise uses `c.add`/`c.mv` in switch dispatch |
| `EnableRiscV64Atomic` | `0` | Lower `Interlocked` to plain load/modify/store instead of `lr`/`sc` and `amo*` |

Both need a cross-JIT built with the matching runtime fixups; an unpatched
JIT ignores knobs it does not know, which is precisely why the result is
verified on the linked binary by `--error-on-compressed` and
`--error-on-atomic` rather than assumed. Only the zkVM targets get them — a
real riscv64+musl target wants C and A.

**The tuning knobs**, in optimized builds:

| Knob | Value | Effect |
|------|-------|--------|
| `JitObjectStackAllocation` | `1` | Enable escape-analysis stack allocation |
| `JitObjectStackAllocationSize` | `2000` (8192) | Raise the max stack-allocatable object size (default `0x210` = 528). The in-loop heap restriction is lifted by a matching runtime patch |
| `JitExtDefaultPolicyMaxIL` | `200` (512) | Max inlinee IL size (default `0x80` = 128). Stays on `ExtendedDefaultPolicy`, which weighs code growth, rather than `JitAggressiveInlining`, which overflows the fixed ZisK ROM |
| `JitExtDefaultPolicyMaxBB` | `10` (16) | Max inlinee basic blocks (default 7) |
| `JitRiscV64DmaCompare` | `1` | Lower constant-size `SpanHelpers.SequenceEqual` to the `csrs 0x814, src ; addi rd, dst, count` idiom that the ZisK transpiler folds into one `dma_xmemcmp` step. ZisK-only, paired with a matching runtime patch |
| `RiscV64ElideLeafRaSave` | `1` | Elide RA spill/reload + frame in eligible leaf methods. A matching runtime patch refuses to elide methods whose LIR uses `REG_RA` as scratch (`GT_JCMP`, comparisons, `GT_MULHI`) or use FP |

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
    <ziskLibPath>/ugc_core.c.obj <ziskLibPath>/ugc_zalloc.c.obj \
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
--fix-init-array  --fix-tdata  --trim-bss     # --remove-eh is opt-in
```

Each pass is a small, self-contained ELF-header rewrite that fixes a
concrete loader behaviour Zisk wouldn't otherwise accept:

| Pass | What it does | Why Zisk needs it |
|------|--------------|-------------------|
| `--fix-init-array` | Forces `.init_array` to `SHT_PROGBITS`, alignment 8 | Otherwise the loader skips the section and module initialisers never run |
| `--fix-tdata` | Adds `ALLOC \| WRITE \| TLS` flags, alignment ≥ 8 | Without TLS bit the loader doesn't include `.tdata` in the program header table; the TLS shim then sees zero bytes |
| `--remove-eh` | Drops `.dotnet_eh_table`, `.eh_frame_hdr`, `.eh_frame` | Opt-in: those tables are what the unwinder reads, so this trades exception handling for image size |
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

Managed exceptions are dispatched by the runtime: a `throw` walks the stack
twice — filters first, then unwinding with `finally` funclets — and resumes
in the `catch`. Filters, rethrow with `InnerException` and multi-frame
unwinding all behave as they do on stock .NET; an exception nobody catches
ends in `FailFast`. Entering a `try` costs nothing at runtime, so a guest
that never throws pays only one unwind-section lookup at startup.

The unwinder finds those sections through the [eh module](modules.md#eh),
which answers `dl_iterate_phdr` with synthetic program headers — a zkVM
image has none of its own.

`--remove-eh` selects the opposite trade: the tables are stripped, the
guest is smaller, and a `throw` exits it instead of being dispatched,
optionally through a program-supplied `ZkvmThrow` handler (see the
[rhp module](modules.md#rhp) and the
[ExceptionHandler sample](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples/ExceptionHandler)).
