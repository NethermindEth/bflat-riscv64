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
RISC-V ELF** that needs no operating system, no dynamic libraries, and
nothing installed on the target. The same source compiles without changes
for:

- a **zkVM prover**, where the binary is executed and proven. Five
  platforms are supported: ZisK (`zisk`), SP1 (`sp1`) and OpenVM (`openvm`)
  on riscv64, and ZKsync Airbender (`airbender`) on riscv32. For Airbender,
  the build also writes the flat ROM images that it loads;
- **`qemu-riscv64`** or any RISC-V64 Linux host, for debugging
  (`zisk_sim`);
- real RISC-V64 hardware.

One command — `bflat build` — does it end to end. bflat also keeps the
stock linux-x64 target, an ordinary NativeAOT program. [Targets](targets.md)
describes each platform.

## Why the extra stages exist

Stock .NET NativeAOT already compiles C# to a native binary, but that binary
assumes a kernel, dynamic linking, floating-point hardware, atomics and
compressed instructions — none of which a zkVM provides.

bflat is a **compiler driver**: it contains no code generator of its own.
Roslyn turns C# into IL, Microsoft's ILCompiler turns IL into a RISC-V
object, `lld` links it — all stock. What bflat adds around them is
**ILC-stage substitutions** for the single hart, **RyuJIT options** that
keep the code inside the ISA of the zkVM, a **link step** that swaps
unsupported OS and runtime calls for zkVM-safe ones, **post-link steps**
that satisfy the loader of the prover, and **target-specific memory
layouts**. Those stages run only for a platform build — `--libc zisk`,
`zisk_sim`, `sp1`, `openvm` or `airbender`; for every other target bflat
behaves like upstream. bflat knows none of these targets by name: each is a
platform that its bflat-* package defines (bflat-libziskos, bflat-sp1,
bflat-openvm, bflat-airbender), and `--extlib` gives the package. The
[manifest format](manifest.md) page describes what a platform declares.

Keeping them outside .NET is the point: the runtime stays an official build
— upstream sources, upstream build system, plus a set of riscv fixes meant
to be upstreamed (see [Runtime](runtime.md)) — so a version bump is a
rebuild instead of a merge. What goes through the pipeline is ordinary C#:
exceptions, allocation, interface dispatch and generics all work, and the
same source builds for the prover, QEMU and real hardware.

## How it works, at a glance

| Stage | What happens | Unique to this fork? |
|-------|--------------|----------------------|
| **1 · Compile** | Microsoft's stock NativeAOT (ILC) compiles C# to a RISC-V object, with the RyuJIT options of the platform | No — upstream, plus the riscv fixups of the runtime |
| **1.5 · Substitute** | The platform's modules give ILC its single-hart ILLink substitutions and feature switches (`guest_subst`) | Yes |
| **2 · Link** | `ld.lld` statically links it, swapping unsupported OS/runtime calls for the [link-time modules](modules.md) | Yes |
| **3 · Post-link** | The platform's post-link steps rewrite the ELF in place, or write the flat images, so that the loader of the prover accepts it (`zisk`, `sp1`, `airbender`) | Yes |
| **4 · Check** | The ISA checks decode the linked image: no F, D, C or A instruction (opt-in; on by default for `airbender`) | Yes |
| **5 · Boot & run** | A tiny assembly entry point starts the runtime and calls `Main` — no kernel underneath | Yes |

The driver lives in `src/bflat/`: `BuildCommand.cs` has the options, and
its nested `Build` class runs one build in stages (`BuildCommand.Build.cs`:
the target and the packages; `BuildCommand.Compile.cs`: Roslyn and ILC;
`BuildCommand.Link.cs`: the linker, the post-link steps and the image
checks), and `BflatManifest.cs` reads the platform and module manifests,
which hold every zkVM decision. `IsaVerifier.cs` decodes the linked image
(ELF64 and ELF32) for the ISA checks. `ILFloatScanner.cs` is the IL check of
`--error-on-float`; the methods that it reports only as a warning are in
`checks.il_float_allow` of `guest_subst`.

---

## Under the hood

The rest of this page walks through each stage — the exact switches, the
link command, the post-link passes and the boot sequence.

## Stage 1 — Microsoft's NativeAOT (ILC) emits an object file

ILC is the .NET NativeAOT compiler — the same one shipped by Microsoft
with the standard .NET SDK, built by dotnet-riscv with the riscv fixups.
bflat embeds Roslyn for parsing and then hands the IL to ILC. Using
Microsoft's compiler directly means the C#-to-native translation is
covered by the same testing the rest of the .NET ecosystem benefits
from — we inherit the safety, correctness, and portability of the
mainline toolchain rather than maintaining our own AOT.

The output is a single RISC-V ELF relocatable that contains:

- compiled managed code (`__managedcode` section);
- type system metadata;
- module initialisation tables;
- references to runtime symbols (GC, exception handling, dispatch, …).

For a platform build, the manifests give ILC its inputs (refer to the
[`ilc` section](manifest.md#ilc-the-inputs-of-the-ilc-stage)):

| Input | Source | Effect |
|-------|--------|--------|
| Feature switches | `ilc.features` of `guest_subst` | `System.Globalization.Invariant` and `System.TimeZoneInfo.Invariant`: no ICU data and no zoneinfo files. A musl target is invariant in any case. |
| Defines | `ilc.defines` of the platform | `ZKVM_ZISK`, `ZKVM_SP1`, `ZKVM_OPENVM`, `ZKVM_AIRBENDER` |
| Direct P/Invoke | `ilc.direct_pinvoke` of `ubootstrap` | `__Internal`, the same as `-i` |
| References | `ilc.references` of a package | managed bindings that the guest compiles against |
| Substitutions | `ilc.substitutions` of `guest_subst` | refer to Stage 1.5 |

`--no-pie` belongs here too, but no platform implies it: it is an
ordinary flag you pass. Position-independent code fights the fixed memory
layout, and the flag changes that layout, so a guest built without it can
fail in ways one built with it does not. Every guest in this repository and
in Nethermind's zkVM builds passes it explicitly.

References are resolved against the runtime's `lib/<os>/<arch>/<libc>`
directory, downloaded from the
[dotnet-riscv](https://github.com/NethermindEth/dotnet-riscv) release
that `bflat.variant.props` names.

## Stage 1.5 — ILC-stage substitutions

The target has no FPU. The guests are soft-float: without F and D the JIT
lowers double arithmetic to calls to the compiler-rt builtins, and bflat
links `libgcc.a` (on riscv32, `libclang_rt.builtins-riscv32.a`), which
defines them. The bundled musl is built for `rv64im` (`rv32im` on riscv32),
so its libm has no F or D instructions either. The exact operations
(`sqrt`, `ceil`, `floor`, `modf`, `fma`, `fmod`) run; only the
transcendental functions trap (see [nofp](modules.md#nofp)), and CoreLib
does not call them on its ordinary paths. So floating point is not a reason
to replace a method.

The reason is the single hart. A thread-pool tuner that samples worker
counts, or a processor count read from cgroups, does work that has no
meaning here. Rather than patch the runtime, bflat applies an ILLink
substitutions file while ILC compiles: `modules/guest_subst/substitutions.xml`
stubs those methods to constants. User `--substitution` files are applied
after it and cannot declare the same method again.

**Drift is the failure mode to design against.** A substituted method that
is renamed or re-signatured stops matching, and the original stays in the
image. ILLink entries in the XML give no error for this; read the build
output for IL2009 (see [verification](verification.md)).

**A constant stub cannot be redirected with `--wrap`.** ILC inlines the
stub into its callers, so no call is left for the linker to divert to a
native replacement and the stub's constant is what runs. Optimized builds
fail when a `--wrap` target is a managed method the scanner reached but
codegen never emitted, which is what an inlined-everywhere method looks
like.

### zkVM RyuJIT codegen knobs

The platform manifests give the ISA to ILC (`codegen.isa`) and RyuJIT
options (`codegen.options`, which `GetBackendOptions` in
`BuildCommand.Compile.cs` reads). `--codegenopt Name=Value` adds or replaces
one by hand. Two groups matter: the ISA gate applies to **every** build of
a platform, while most tuning options apply only to optimized ones
(`"when": {"optimize": true}`).

RyuJIT parses these integer values as **hexadecimal with no `0x` prefix**
(`JitConfigProvider.getIntConfigValue` uses `NumberStyles.AllowHexSpecifier`),
so `"2000"` means `0x2000` = 8192. bflat rejects a value with `0x`.

**The ISA gate.** Every zkVM here decodes the base 32-bit encoding only,
and the guest is single-hart. Each platform disables C, A, F, D and Zicond,
and Zba, Zbb and Zbs (`codegen.isa.disable`), and bflat gives the result to
ILC as for `-m`. ZisK, whose transpiler implements Zba, Zbb and Zbs, turns
them back on (`codegen.isa.enable`) when the compiler package has the
`zb-lowering-fixes` capability. Two options make the JIT obey:

| Knob | Value | Effect |
|------|-------|--------|
| `EnableRiscV64Compressed` | `0` | Never emit a compressed (C) encoding. RyuJIT otherwise uses `c.add`/`c.mv` in switch dispatch |
| `EnableRiscV64Atomic` | `0` | Lower `Interlocked` to plain load/modify/store instead of `lr`/`sc` and `amo*` |

Both need a cross-JIT built with the matching runtime fixups; an unpatched
JIT ignores knobs it does not know, which is precisely why the result is
verified on the linked binary by `--error-on-compressed` and
`--error-on-atomic` rather than assumed. Only the platforms get them — a
real riscv64+musl target wants C and A.

**Memory access.** bflat writes `JitNoUnalignedAccess` and
`JitRiscV64StrictAlign` for every RISC-V target, `0` by default. A platform
that sets them replaces the default; `--no-unaligned-access` sets
`JitNoUnalignedAccess=1` for any target.

**The options of each platform.** "opt" means optimized builds only.

| Knob | `zisk` | `zisk_sim` | `sp1` | `openvm` | `airbender` | Effect |
|------|:------:|:----------:|:-----:|:--------:|:-----------:|--------|
| `JitZkBumpAddr` | `bffbfff8` | `bffbfff8` | `bffefff8` | `bffefff8` | — | The address of the bump-pointer cell: inline allocation at each site (fixup 26) |
| `JitNoUnalignedAccess` | — | — | `1` | — | `1` | Expand the accesses flagged unaligned byte-wise |
| `JitRiscV64StrictAlign` | — | — | `1` | — | `0` | Check at run time the accesses whose alignment the JIT cannot know |
| `JitRiscVUnsignedMulDiv` | — | — | — | — | `1` | Build the signed RV32M instructions from unsigned ones |
| `JitObjectStackAllocation` | opt `1` | opt `1` | opt `1` | opt `1` | opt `1` | Enable escape-analysis stack allocation |
| `JitObjectStackAllocationSize` | opt `2000` | opt `2000` | opt `2000` | opt `2000` | — | Raise the max stack-allocatable object size to 8192 (default `0x210` = 528). A matching runtime patch lifts the in-loop heap restriction |
| `JitExtDefaultPolicyMaxIL`, `…MaxBB` | opt `200`, `10` | opt | opt | opt | — | Inlinees up to 512 bytes of IL and 16 basic blocks (default 128, 7). Stays on `ExtendedDefaultPolicy`, which weighs code growth; `JitAggressiveInlining` overflows the fixed ZisK ROM |
| `RiscV64ElideLeafRaSave` | opt `1` | opt `1` | opt `1` | opt `1` | opt `1` | Elide RA spill/reload + frame in eligible leaf methods. A matching runtime patch refuses methods whose LIR uses `REG_RA` as scratch or uses FP. Sound only on a single-threaded deterministic guest with no GC suspension and no return-address hijacking |
| `JitRiscV64DmaCompare` | opt `1` | — | — | — | — | Constant-size `SequenceEqual` as the ZisK `dma_xmemcmp` idiom |
| `JitZkCsrPInvokes`, `JitZkMemsetCsr`, `JitZkMemcpyCsr` | opt | — | — | — | — | libziskos P/Invokes, zero fills and block copies as the CSR idioms of the ZisK precompiles |
| `JitInlineAdditionalMultiplier` | opt `3` | — | — | — | — | Inline more: code size is free on ZisK |
| `JitZkNoPinning`, `JitZkSynthesizeCounts`, `JitZkSpillKeepReg`, `JitZkColdCallSpill` | opt | — | — | — | opt | Register tuning for a guest with a non-moving GC |
| `JitForceInlineMaxIL` and eight other `JitZk*` | — | — | — | — | opt | Tuning measured on the Nethermind guest; refer to [airbender](targets.md#airbender) |

The `ugc-zero` module adds `JitZkNoGcMustInit=1` on every platform: uGC
never scans a stack, so the JIT need not zero a slot only because it holds
GC references.

`JitRiscV64DmaCompare` is the narrowest of the set: only the ZisK ROM
transpiler folds that instruction pair. A real riscv64 CPU — which is what
`zisk_sim` runs on, under QEMU or on hardware — executes a CSR access to
`0x814` followed by an `addi` to `x0`, and neither SP1 nor OpenVM knows the
idiom. The knob defaults to `0` in the JIT, so every other target keeps the
ordinary call.

The register that receives the result is the **`csrrs`'s** `rd`, and the
`addi`'s `rd` must be `x0`. The mirrored form also transpiles, but through the
branch ZisK marks deprecated (`riscv2zisk_context.rs` prints *"DEPRECATED
INSTRUCTION … please update toolchain/zisklibs/ziskos"*). The encoding above
matches `ziskos/entrypoint/src/dma/memcmp.s`, which is the reference.

These knobs trade ROM/`.text` size for fewer heap allocations and tighter
hot paths; the notes in the ZisK platform manifest say which to lower
(`JitExtDefaultPolicyMaxIL`, `JitObjectStackAllocationSize`) if a workload
overflows the fixed ZisK ROM. Airbender's 4 MiB ROM is why it keeps the
defaults of both.

`bflat-modules link <layout>/lib/modules <platform manifest>` prints the
effective ISA, ABI and options of a platform without a guest build (refer to
[`bflat-modules`](manifest.md#module-build-and-layout-bflat-modules)).

## Stage 2 — The link command

The final ELF is produced by `ld.lld` (Clang's linker, shipped with
bflat). bflat writes the stock part of the command, and the manifests fill
the groups (refer to the [`objects` section](manifest.md#objects-the-files-to-link)).
The platform part comes from the package (`<pkg>` below): the bindings
manifest (`libziskos`) gives its library and wraps, the platform manifest
(`zisk`) the linker script and the entry point. The module part comes from
the manifests in `lib/modules/` (`<m>` below): each module that the
platform requires gives its objects, their link group and their wraps. For
`--libc zisk`, abridged:

```bash
ld.lld -flavor ld --no-relax -L<lib> -z now -z relro -z noexecstack \
    --hash-style=gnu --eh-frame-hdr -z nostart-stop-gc \
    -static <lib>/crt1-platform.o <lib>/crti.o -o app \
    app.o --as-needed --gc-sections <lib>/libbootstrapper.o --no-pie \
    -lSystem.Native -latomic -leventpipe-disabled -laotminipal ... \
    --as-needed -ldl -lm -lz -z relro -z now --discard-all --gc-sections \
    <lib>/libc.a <lib>/libgcc.a <lib>/crtn.o \
    <pkg>/libziskos.a --wrap=memcpy ... \
    -T <pkg>/zisk.ld <pkg>/zisk_entry.o \
    <m>/nofp/module.o --wrap=acos ... --wrap=asprintf \
    --whole-archive \
        <m>/ubootstrap/module.o \
        <m>/stdcppshim/module.o \
        <m>/rhp/module.o --wrap=RhpPInvoke ... --wrap=RhpNewFast ... \
        <m>/gs_cookie/module.o --wrap=__security_cookie \
        <m>/rhp_native/module.o --wrap=RhpAssignRefRiscV64 --wrap=RhpCheckedAssignRef ... \
        <m>/pal/module.o --wrap=getenv ... --wrap=exit --wrap=_Exit --wrap=abort \
        <m>/eh/module.o --wrap=dl_iterate_phdr \
        <m>/tls/module.o --wrap=__tls_get_addr --wrap=__init_tls ... \
    --no-whole-archive \
    <m>/rng_stupid/module.o --wrap=minipal_get_cryptographically_secure_random_bytes ... \
    <m>/rust_sys/module.o --wrap=sys_alloc_aligned \
    <m>/ugc-zero/release/uGC.cpp.obj --wrap=GC_Initialize --wrap=GC_VersionInfo \
    <m>/ugc-zero/release/uGCHandleManager.cpp.obj ... <m>/ugc-zero/release/ugc_zalloc.c.obj
```

`bflat-modules link <layout>/lib/modules <pkg>/zisk.bflat.manifest
<pkg>/libziskos.bflat.manifest` prints the exact result for a platform, and
`bflat build -x` prints the command of a real build.

The other platforms differ in these parts:

- **`pre-libc`.** With the `no-atomics` and `noos` flags (sp1, openvm,
  airbender), `<m>/nothread/module.o` and `<m>/noos/module.o` come just
  before `libc.a`. They act by definition: lld takes an archive member only
  for a symbol that is not defined yet, so musl's members never come in.
- **riscv32.** Airbender links `libclang_rt.builtins-riscv32.a` in place of
  `libgcc.a`, the `module.rv32.o` objects and `release-riscv32/` of uGC,
  and no OpenSSL or ICU library.
- **Console wraps.** Only the platforms with the `no-console` flag get the
  console wraps of `rhp` and `pal`.

Two mechanisms are doing most of the work:

- **`--whole-archive`** forces the linker to pull in every object from
  the listed modules, even if no one references them. This is how the
  bootstrap and TLS code reaches the binary.
- **`--wrap=symbol`** rewrites every reference to `symbol` into a call to
  `__wrap_symbol`, while preserving the original under the name
  `__real_symbol`. This is how a single C function in `pal/module.c`
  (such as `__wrap_getenv`) replaces musl's implementation without
  touching musl.

Which symbols are wrapped depends on the target and on the build: each
module declares its own set in its `bflat-manifest.json`, with conditions
on the platform and the build, so a build without unwind tables, for
instance, adds `--wrap=RhpThrowEx`. The [Modules](modules.md) page has the
full list, and the [manifest format](manifest.md) page describes the
conditions. `--wrap-check` makes sure that each `--wrap` names a symbol
that exists.

## Stage 3 — Post-link steps

A platform can list post-link steps (its `postlink` section); bflat runs
them in sequence, after the link, and each one changes the output in place:
what `-o` names is what the zkVM runs. A package cannot add a tool. bflat
has two:

| Tool | What it does |
|------|--------------|
| `patch_elf` | `scripts/patch_elf.py`, which bflat ships, rewrites the ELF |
| `flat_image` | Writes `<output><suffix>`: the raw image of the output (`llvm-objcopy -O binary`), or of the sections that `--section` names, padded with zeros to a multiple of four bytes |

The steps of each platform:

| Platform | Steps |
|----------|-------|
| `zisk` | `patch_elf --fix-init-array --fix-tdata --trim-bss`, plus `--remove-eh` without unwind tables and `--print-fn-boundaries` with `-v` |
| `zisk_sim`, `openvm` | none |
| `sp1` | `patch_elf --nop-fences --nop-zero-words` |
| `airbender` | `patch_elf --nop-fences --unsigned-muldiv --unimp-system --unimp-csr 0xc22`, then `flat_image --suffix .bin`, then `flat_image --suffix .text --section .text` |

The `patch_elf` passes. Each is a small, self-contained rewrite that fixes
a concrete loader or decoder behaviour:

| Pass | ELF | What it does | Why |
|------|-----|--------------|-----|
| `--fix-init-array` | 64 | Forces `.init_array` to `SHT_PROGBITS`, alignment 8 | Otherwise the ZisK loader skips the section and module initialisers never run |
| `--fix-tdata` | 64 | Adds `ALLOC \| WRITE \| TLS` flags, alignment ≥ 8 | Without the TLS bit the loader leaves `.tdata` out; the TLS shim then sees zero bytes |
| `--remove-eh` | 64 | Drops `.dotnet_eh_table`, `.eh_frame_hdr`, `.eh_frame` | Only without unwind tables: those tables are what the unwinder reads, so this trades exception handling for image size |
| `--trim-bss` | 64 | Removes the `.bss` section header | Linker scripts already provide explicit heap symbols; trimming `.bss` removes a region the prover would otherwise account for |
| `--nop-zero-words` | 64 | Rewrites to NOP the alignment padding between functions in `.text` | ILC pads with a zero word (no encoding) or `c.nop` (compressed); SP1 rejects both when it transpiles the whole segment |
| `--print-fn-boundaries` | 64 | Prints the function boundaries (EH, symbol table, objdump, and the size it uses) | Debugging of the passes |
| `--nop-fences` | 64, 32 | Rewrites every FENCE and FENCE.I in `.text` to NOP | A single hart has no memory order to keep; SP1 maps FENCE to an illegal instruction, and Airbender rejects it |
| `--unsigned-muldiv` | 32 | Moves each signed RV32M instruction (`mulh`, `mulhsu`, `div`, `rem`) of the native code into a trampoline built from unsigned ones, in `__signed_tramp_start`…`__signed_tramp_end` of the linker script | Airbender v3 proves only the unsigned ones |
| `--unimp-system` | 32 | Rewrites to UNIMP (`csrrw x0, cycle, x0`) each SYSTEM instruction with funct3 0 that is code (an `ebreak` after a call to a noreturn helper, an `ecall`) | The Airbender transpiler decodes the whole `.text` and rejects any other SYSTEM encoding |
| `--unimp-csr <n>` | 32 | With `--unimp-system`, also rewrites to UNIMP each access of CSR `<n>` | The decoder does not know it; for example `vlenb` (`0xc22`), which libunwind reads |

An ELF32 image takes only the passes that rewrite instruction words, on
the bytes of the file: the flat image of a riscv32 zkVM is the load
segments as linked. `--split-code-data` also exists, and no platform uses
it.

## Stage 4 — ISA checks

`--error-on-float-binary`, `--error-on-compressed` and `--error-on-atomic`
decode the linked image after the post-link steps, so they check what the
zkVM runs. They fail the build on an F/D, compressed or atomic instruction
and name the symbol. A platform can turn them on for every guest
(`checks`); Airbender does. On the other platforms they are flags you pass.
Refer to [Verification](verification.md#floating-point-gates).

## Stage 5 — Boot

When the binary starts (real, simulated, or proven), the entry point is
`_start` from the platform's entry object — `platform/entry.S` in the
package:

1. Set `gp` to `_global_pointer` and `sp` to `_init_stack_top` (both
   provided by the linker script). On Airbender, copy the initialised data
   from its load copy in ROM to RAM first.
2. Call the target's start-up routine with `argc == 1` and
   `argv == {"app", NULL}`:
   - **ZisK, zisk_sim** — `__libc_start_main(uBootstrap_main, …)`, musl's own.
   - **OpenVM, Airbender** — `noos_start_main` (`modules/noos`), which
     replaces musl's start-up wholesale: it exists to parse an auxv this
     guest does not have, and costs 126 instructions the transpiler would
     have to accept.
   - **SP1** — sp1-zkvm's `__start`, which must not be bypassed: it
     initialises the Rust allocator and the public-values hasher that
     `syscall_write` and `syscall_halt` depend on, then calls `main`. `main`
     here is the entry object's `__wrap_main`, which calls `noos_main` — the
     returning half of `noos_start_main` — and hands its result back, because
     SP1 halts with it.
3. `noos_start_main`/`noos_main` (and `__libc_start_main` on ZisK) set the
   thread pointer, run `.init_array` and enter `uBootstrap_main`.
4. `uBootstrap_main` (in `modules/ubootstrap/module.cpp`) calls
   `RhInitialize`, registers the managed-code range, runs all module
   initialisers, then jumps into `__managed__Main` — i.e., the C# `Main`.

There is no kernel underneath any of this. Every syscall the runtime
might make is wrapped to a no-op, returned as a constant, or given to the
platform's `zkvm_platform_syscall`. Only ZisK and zisk_sim define it, and
they forward to musl's `syscall`: ZisK emulates Linux syscalls, and
zisk_sim runs on Linux. On SP1, OpenVM and Airbender `syscall` returns
`-1`. That containment is not merely a tidiness matter: SP1 reads the
syscall id from `t0` and errors out on one it does not know, and OpenVM
services no `ecall` at all.

## Exit and exceptions

musl's `exit`/`_Exit`/`abort` use `exit_group` (syscall 94), which no zkVM
takes as the end of the program. So `pal` wraps all three and calls the
platform's `zkvm_platform_exit` (see the [pal module](modules.md#pal)):

| Platform | Exit |
|----------|------|
| `zisk`, `zisk_sim` | `ecall` with `a7 == 93` (ZisK `CAUSE_EXIT`) |
| `sp1` | `zkvm_halt` of the bindings library, or without it the same syscalls: `COMMIT` ×8, `COMMIT_DEFERRED_PROOFS` ×8, then `HALT` with the code |
| `openvm` | `TERMINATE`, whose code is an immediate: 0 or 1 |
| `airbender` | code 0: the 16 output words in `x10`–`x25`, then a self-jump. Other codes: a write of the cycle CSR, which no circuit can prove |

Managed exceptions are dispatched by the runtime: a `throw` walks the stack
twice — filters first, then unwinding with `finally` funclets — and resumes
in the `catch`. Filters, rethrow with `InnerException` and multi-frame
unwinding all behave as they do on stock .NET; an exception nobody catches
ends in `FailFast`. Entering a `try` costs nothing at runtime, so a guest
that never throws pays only one unwind-section lookup at startup.

The unwinder finds those sections through the [eh module](modules.md#eh),
which answers `dl_iterate_phdr` with synthetic program headers — a zkVM
image has none of its own.

A build without unwind tables selects the opposite trade: the guest is
smaller, and a `throw` exits it instead of being dispatched, optionally
through a program-supplied `ZkvmThrow` handler (see the
[rhp module](modules.md#rhp) and the
[ExceptionHandler sample](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples/ExceptionHandler)).
`--remove-eh` asks for it: the conditions then see `eh: failfast`, `rhp`
wraps `RhpThrowEx`, and the post-link step of `zisk` strips the tables. A
platform with `"unwind": false` always builds this way: Airbender, whose
4 MiB ROM cannot hold about half a MiB of tables, and whose linker script
keeps none.
