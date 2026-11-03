---
layout: default
title: Architecture
eyebrow: How it works
lead: >
  What the build pipeline produces, why each stage is necessary, and the
  evidence that it works. The exact mechanics are at the end of the page.
prev: /runtime/
next: /modules/
---

<div style="background: var(--bg-elev-1); border: 1px solid var(--border); border-radius: var(--radius-lg); padding: 24px; margin: 16px 0 32px;">
  {% include pipeline-diagram.html %}
</div>

## What it produces

bflat-riscv64 compiles an ordinary C# program into a **single, fully static
RISC-V ELF**. The ELF needs no operating system, no dynamic libraries and
nothing installed on the target. The same source compiles without changes
for:

- a **zkVM prover**, which executes and proves the binary. bflat supports
  five platforms: ZisK (`zisk`), SP1 (`sp1`) and OpenVM (`openvm`) on
  riscv64, and ZKsync Airbender (`airbender`) on riscv32. For Airbender,
  the build also writes the flat ROM images that Airbender loads;
- **`qemu-riscv64`** or any RISC-V64 Linux host, for debugging
  (`zisk_sim`);
- real RISC-V64 hardware.

One command, `bflat build`, does all the steps. bflat also keeps the stock
linux-x64 target, which builds an ordinary NativeAOT program.
[Targets](targets.md) describes each platform.

## Why the extra stages exist

Stock .NET NativeAOT compiles C# to a native binary. That binary needs a
kernel, dynamic linking, floating-point hardware, atomics and compressed
instructions. A zkVM gives none of them.

bflat is a **compiler driver**. It contains no code generator of its own.
Roslyn compiles C# into IL, Microsoft's ILCompiler compiles IL into a RISC-V
object, and `lld` links it. All three are stock. bflat adds these stages
around them:

- **ILC-stage substitutions** for the single hart;
- **RyuJIT options** that keep the code inside the ISA of the zkVM;
- a **link step** that replaces unsupported OS and runtime calls with
  calls that are safe on a zkVM;
- **post-link steps** that make the image acceptable to the loader of the
  prover;
- **target-specific memory layouts**.

These stages run only for a platform build (`--libc zisk`, `zisk_sim`,
`sp1`, `openvm` or `airbender`). For all other targets, bflat operates as
upstream bflat does. bflat knows none of these targets by name. Each one is
a platform that its bflat-* package defines (bflat-libziskos, bflat-sp1,
bflat-openvm, bflat-airbender), and `--extlib` gives the package. The
[manifest format](manifest.md) page describes what a platform declares.

These stages are outside .NET, so the runtime stays an official build. It
uses the upstream sources, the upstream build system and a set of riscv
fixes that we send upstream (see [Runtime](runtime.md)). Thus a new .NET
version is a rebuild, not a merge. The pipeline takes ordinary C#.
Exceptions, allocation, interface dispatch and generics all work. The same
source builds for the prover, for QEMU and for real hardware.

## How it works, at a glance

| Stage | What happens | Unique to this fork? |
|-------|--------------|----------------------|
| **1 · Compile** | Microsoft's stock NativeAOT (ILC) compiles C# to a RISC-V object, with the RyuJIT options of the platform | No: upstream, plus the riscv fixups of the runtime |
| **1.5 · Substitute** | The platform's modules give ILC its single-hart ILLink substitutions and feature switches (`guest_subst`) | Yes |
| **2 · Link** | `ld.lld` links the object statically and replaces unsupported OS and runtime calls with the [link-time modules](modules.md) | Yes |
| **3 · Post-link** | The post-link steps of the platform change the ELF in place, or write the flat images, so that the loader of the prover accepts it (`zisk`, `sp1`, `airbender`) | Yes |
| **4 · Check** | The ISA checks decode the linked image: no F, D, C or A instruction (opt-in; on by default for `airbender`) | Yes |
| **5 · Boot & run** | A small assembly entry point starts the runtime and calls `Main`. No kernel is under it | Yes |

The driver is in `src/bflat/`:

- `BuildCommand.cs` has the options.
- Its nested `Build` class runs one build in stages:
  `BuildCommand.Build.cs` (the target and the packages),
  `BuildCommand.Compile.cs` (Roslyn and ILC) and `BuildCommand.Link.cs`
  (the linker, the post-link steps and the image checks).
- `BflatManifest.cs` reads the platform and module manifests. The manifests
  hold every zkVM decision.
- `IsaVerifier.cs` decodes the linked image (ELF64 and ELF32) for the ISA
  checks.
- `ILFloatScanner.cs` is the IL check of `--error-on-float`. The methods
  that it reports only as a warning are in `checks.il_float_allow` of
  `guest_subst`.

---

## Under the hood

The rest of this page describes each stage: the exact switches, the link
command, the post-link passes and the boot sequence.

## Stage 1 — Microsoft's NativeAOT (ILC) emits an object file

ILC is the .NET NativeAOT compiler. It is the compiler that Microsoft ships
with the standard .NET SDK, built by dotnet-riscv with the riscv fixups.
bflat embeds Roslyn to parse the source, and then gives the IL to ILC.
Microsoft's compiler has the tests of the full .NET ecosystem. Thus bflat
gets the safety, correctness and portability of the mainline toolchain, and
we do not keep an AOT compiler of our own.

The output is one RISC-V ELF relocatable file. It contains:

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

`--no-pie` is also an input of this stage, but no platform sets it. You
must give the flag yourself. Position-independent code does not agree with
the fixed memory layout. The flag changes that layout, so a guest without
it can fail where a guest with it does not. Every guest in this repository
and in the zkVM builds of Nethermind gives the flag explicitly.

ILC resolves references against the `lib/<os>/<arch>/<libc>` directory of
the runtime. bflat downloads it from the
[dotnet-riscv](https://github.com/NethermindEth/dotnet-riscv) release
that `bflat.variant.props` names.

## Stage 1.5 — ILC-stage substitutions

The target has no FPU, so the guests are soft-float. Without F and D, the
JIT lowers double arithmetic to calls to the compiler-rt builtins. bflat
links `libgcc.a` (on riscv32, `libclang_rt.builtins-riscv32.a`), which
defines them. The bundled musl is built for `rv64im` (`rv32im` on riscv32),
so its libm has no F or D instructions either. The exact operations
(`sqrt`, `ceil`, `floor`, `modf`, `fma`, `fmod`) run. Only the
transcendental functions trap (see [nofp](modules.md#nofp)), and CoreLib
does not call them on its ordinary paths. Thus floating point is not a
reason to replace a method.

The reason to replace a method is the single hart. Some CoreLib work has
no meaning on one hart. Examples are a thread-pool tuner that samples
worker counts, and a processor count that comes from cgroups. bflat does not patch
the runtime for this. It applies an ILLink substitutions file while ILC
compiles: `modules/guest_subst/substitutions.xml` replaces the bodies of
those methods with constants. bflat applies the user `--substitution` files
after it. A user file cannot declare the same method again.

**Design against drift.** A substituted method can get a new name or a new
signature. Then the entry stops matching, and the original body stays in
the image. ILLink gives no error for this. Read the build output for IL2009
(see [verification](verification.md)).

**`--wrap` cannot redirect a constant stub.** ILC inlines the stub into its
callers. Thus no call is left for the linker to divert to a native
replacement, and the constant of the stub runs. An optimized build fails
when a `--wrap` target is a managed method that the scanner reached but
codegen never emitted. A method that ILC inlined into all its callers looks
like that.

### zkVM RyuJIT codegen knobs

The platform manifests give the ISA to ILC (`codegen.isa`) and give RyuJIT
options (`codegen.options`, which `GetBackendOptions` in
`BuildCommand.Compile.cs` reads). `--codegenopt Name=Value` adds or replaces
one option by hand. There are two groups. The ISA gate applies to **every**
build of a platform. Most tuning options apply only to optimized builds
(`"when": {"optimize": true}`).

RyuJIT parses these integer values as **hexadecimal with no `0x` prefix**
(`JitConfigProvider.getIntConfigValue` uses `NumberStyles.AllowHexSpecifier`).
Thus `"2000"` is `0x2000` = 8192. bflat rejects a value with `0x`.

**The ISA gate.** Every zkVM here decodes only the base 32-bit encoding,
and the guest has one hart. Each platform disables C, A, F, D, Zicond, Zba,
Zbb and Zbs (`codegen.isa.disable`). bflat gives the result to ILC as it
does for `-m`. The ZisK transpiler implements Zba, Zbb and Zbs. Thus ZisK
enables them again (`codegen.isa.enable`) when the compiler package has the
`zb-lowering-fixes` capability. Two options make the JIT obey the gate:

| Knob | Value | Effect |
|------|-------|--------|
| `EnableRiscV64Compressed` | `0` | Never emit a compressed (C) encoding. RyuJIT otherwise uses `c.add`/`c.mv` in switch dispatch |
| `EnableRiscV64Atomic` | `0` | Lower `Interlocked` to plain load/modify/store instead of `lr`/`sc` and `amo*` |

Both options need a cross-JIT built with the matching runtime fixups. A JIT
without the fixups ignores options that it does not know. For this reason,
`--error-on-compressed` and `--error-on-atomic` check the result on the
linked binary. Only the platforms get these options, because a real
riscv64+musl target needs C and A.

**Memory access.** bflat writes `JitNoUnalignedAccess` and
`JitRiscV64StrictAlign` for every RISC-V target, with the value `0` by
default. A platform that sets them replaces the default.
`--no-unaligned-access` sets `JitNoUnalignedAccess=1` for any target.

**The options of each platform.** "opt" means optimized builds only.

| Knob | `zisk` | `zisk_sim` | `sp1` | `openvm` | `airbender` | Effect |
|------|:------:|:----------:|:-----:|:--------:|:-----------:|--------|
| `JitZkBumpAddr` | `bffbfff8` | `bffbfff8` | `bffefff8` | `bffefff8` | — | The address of the bump-pointer cell: inline allocation at each site (fixup 26) |
| `JitNoUnalignedAccess` | — | — | `1` | — | `1` | Expand the accesses flagged unaligned byte-wise |
| `JitRiscV64StrictAlign` | — | — | `1` | — | `0` | Check at run time the accesses whose alignment the JIT cannot know |
| `JitRiscVUnsignedMulDiv` | — | — | — | — | `1` | Build the signed RV32M instructions from unsigned ones |
| `JitObjectStackAllocation` | opt `1` | opt `1` | opt `1` | opt `1` | opt `1` | Enable escape-analysis stack allocation |
| `JitObjectStackAllocationSize` | opt `2000` | opt `2000` | opt `2000` | opt `2000` | — | Raise the max stack-allocatable object size to 8192 (default `0x210` = 528). A matching runtime patch removes the in-loop heap restriction |
| `JitExtDefaultPolicyMaxIL`, `…MaxBB` | opt `200`, `10` | opt | opt | opt | — | Inlinees up to 512 bytes of IL and 16 basic blocks (default 128, 7). Stays on `ExtendedDefaultPolicy`, which weighs code growth; `JitAggressiveInlining` overflows the fixed ZisK ROM |
| `RiscV64ElideLeafRaSave` | opt `1` | opt `1` | opt `1` | opt `1` | opt `1` | Elide RA spill/reload + frame in eligible leaf methods. A matching runtime patch refuses methods whose LIR uses `REG_RA` as scratch or uses FP. Sound only on a single-threaded deterministic guest with no GC suspension and no return-address hijacking |
| `JitRiscV64DmaCompare` | opt `1` | — | — | — | — | Constant-size `SequenceEqual` as the ZisK `dma_xmemcmp` idiom |
| `JitZkCsrPInvokes`, `JitZkMemsetCsr`, `JitZkMemcpyCsr` | opt | — | — | — | — | libziskos P/Invokes, zero fills and block copies as the CSR idioms of the ZisK precompiles |
| `JitInlineAdditionalMultiplier` | opt `3` | — | — | — | — | Inline more: code size is free on ZisK |
| `JitZkNoPinning`, `JitZkSynthesizeCounts`, `JitZkSpillKeepReg`, `JitZkColdCallSpill` | opt | — | — | — | opt | Register tuning for a guest with a non-moving GC |
| `JitForceInlineMaxIL` and eight other `JitZk*` | — | — | — | — | opt | Tuning measured on the Nethermind guest; refer to [airbender](targets.md#airbender) |

The `ugc-zero` module adds `JitZkNoGcMustInit=1` on every platform. uGC
never scans a stack, so the JIT does not have to zero a slot only because
the slot holds GC references.

`JitRiscV64DmaCompare` applies to the fewest targets. Only the ZisK ROM
transpiler folds that instruction pair. A real riscv64 CPU executes a CSR
access to `0x814` and then an `addi` to `x0`. `zisk_sim` runs on such a CPU,
under QEMU or on hardware. SP1 and OpenVM do not know the idiom. The option
is `0` by default in the JIT, so every other target keeps the ordinary
call.

The register that receives the result is the `rd` of the `csrrs`. The `rd`
of the `addi` must be `x0`. The mirrored form also transpiles, but through
the branch that ZisK marks as deprecated (`riscv2zisk_context.rs` prints
*"DEPRECATED INSTRUCTION … please update toolchain/zisklibs/ziskos"*). The
encoding above agrees with the reference,
`ziskos/entrypoint/src/dma/memcmp.s`.

These options make the ROM and `.text` larger, and in return give fewer heap
allocations and shorter hot paths. If a workload overflows the fixed ZisK
ROM, the notes in the ZisK platform manifest say which options to decrease
(`JitExtDefaultPolicyMaxIL`, `JitObjectStackAllocationSize`). Airbender has
a 4 MiB ROM, so it keeps the defaults of both.

`bflat-modules link <layout>/lib/modules <platform manifest>` prints the
effective ISA, ABI and options of a platform without a guest build (refer to
[`bflat-modules`](manifest.md#module-build-and-layout-bflat-modules)).

## Stage 2 — The link command

`ld.lld` (the Clang linker, which bflat ships) makes the final ELF. bflat
writes the stock part of the command, and the manifests fill the groups
(refer to the [`objects` section](manifest.md#objects-the-files-to-link)).

- The package (`<pkg>` below) gives the platform part. The bindings
  manifest (`libziskos`) gives its library and wraps. The platform manifest
  (`zisk`) gives the linker script and the entry point.
- The manifests in `lib/modules/` (`<m>` below) give the module part. Each
  module that the platform requires gives its objects, their link group
  and their wraps.

For `--libc zisk`, abridged:

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
<pkg>/libziskos.bflat.manifest` prints the exact result for a platform.
`bflat build -x` prints the command of a real build.

The other platforms differ in these parts:

- **`pre-libc`.** With the `no-atomics` and `noos` flags (sp1, openvm,
  airbender), `<m>/nothread/module.o` and `<m>/noos/module.o` come
  immediately before `libc.a`. They replace musl because they define the
  symbols first. lld takes an archive member only for a symbol that is not
  defined yet, so the musl members do not come in.
- **riscv32.** Airbender links `libclang_rt.builtins-riscv32.a` in place of
  `libgcc.a`, the `module.rv32.o` objects and `release-riscv32/` of uGC.
  It links no OpenSSL or ICU library.
- **Console wraps.** Only the platforms with the `no-console` flag get the
  console wraps of `rhp` and `pal`.

Two mechanisms do most of the work:

- **`--whole-archive`** makes the linker take every object from the listed
  modules, also when nothing references them. This is how the bootstrap
  and TLS code gets into the binary.
- **`--wrap=symbol`** changes every reference to `symbol` into a call to
  `__wrap_symbol`, and keeps the original under the name `__real_symbol`.
  This is how one C function in `pal/module.c` (for example
  `__wrap_getenv`) replaces the musl implementation without a change to
  musl.

The set of wrapped symbols depends on the target and on the build. Each
module declares its own set in its `bflat-manifest.json`, with conditions
on the platform and the build. For example, a build without unwind tables
adds `--wrap=RhpThrowEx`. The [Modules](modules.md) page has the full list,
and the [manifest format](manifest.md) page describes the conditions.
`--wrap-check` makes sure that each `--wrap` names a symbol that exists.

## Stage 3 — Post-link steps

A platform can list post-link steps (its `postlink` section). bflat runs
them in sequence after the link. Each step changes the output in place, so
the file that `-o` names is the file that the zkVM runs. A package cannot
add a tool. bflat has two:

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

Each `patch_elf` pass is a small, self-contained rewrite. It fixes one
known behaviour of a loader or a decoder:

| Pass | ELF | What it does | Why |
|------|-----|--------------|-----|
| `--fix-init-array` | 64 | Forces `.init_array` to `SHT_PROGBITS`, alignment 8 | Otherwise the ZisK loader skips the section and module initialisers never run |
| `--fix-tdata` | 64 | Adds `ALLOC \| WRITE \| TLS` flags, alignment ≥ 8 | Without the TLS bit the loader leaves `.tdata` out; the TLS shim then sees zero bytes |
| `--remove-eh` | 64 | Drops `.dotnet_eh_table`, `.eh_frame_hdr`, `.eh_frame` | Only without unwind tables. The unwinder reads these tables, so this pass gives a smaller image and no exception handling |
| `--trim-bss` | 64 | Removes the `.bss` section header | Linker scripts already provide explicit heap symbols; without `.bss`, the prover does not account for that region |
| `--nop-zero-words` | 64 | Rewrites to NOP the alignment padding between functions in `.text` | ILC pads with a zero word (no encoding) or `c.nop` (compressed); SP1 rejects both when it transpiles the whole segment |
| `--print-fn-boundaries` | 64 | Prints the function boundaries (EH, symbol table, objdump, and the size it uses) | Debugging of the passes |
| `--nop-fences` | 64, 32 | Rewrites every FENCE and FENCE.I in `.text` to NOP | A single hart has no memory order to keep; SP1 maps FENCE to an illegal instruction, and Airbender rejects it |
| `--unsigned-muldiv` | 32 | Moves each signed RV32M instruction (`mulh`, `mulhsu`, `div`, `rem`) of the native code into a trampoline built from unsigned ones, in `__signed_tramp_start`…`__signed_tramp_end` of the linker script | Airbender v3 proves only the unsigned ones |
| `--unimp-system` | 32 | Rewrites to UNIMP (`csrrw x0, cycle, x0`) each SYSTEM instruction with funct3 0 that is code (an `ebreak` after a call to a noreturn helper, an `ecall`) | The Airbender transpiler decodes the whole `.text` and rejects any other SYSTEM encoding |
| `--unimp-csr <n>` | 32 | With `--unimp-system`, also rewrites to UNIMP each access of CSR `<n>` | The decoder does not know it; for example `vlenb` (`0xc22`), which libunwind reads |

An ELF32 image gets only the passes that rewrite instruction words, on the
bytes of the file. The reason is that the flat image of a riscv32 zkVM is
the load segments as linked. `--split-code-data` also exists, but no
platform uses it.

## Stage 4 — ISA checks

`--error-on-float-binary`, `--error-on-compressed` and `--error-on-atomic`
decode the linked image after the post-link steps. Thus they check the
image that the zkVM runs. They fail the build on an F/D, compressed or
atomic instruction, and they name the symbol. A platform can turn them on
for every guest (`checks`). Airbender does this. On the other platforms,
they are flags that you give. Refer to
[Verification](verification.md#floating-point-gates).

## Stage 5 — Boot

When the binary starts (real, simulated or proven), the entry point is
`_start` from the entry object of the platform (`platform/entry.S` in the
package):

1. Set `gp` to `_global_pointer` and `sp` to `_init_stack_top`. The linker
   script gives both. On Airbender, first copy the initialised data from
   its load copy in ROM to RAM.
2. Call the start-up routine of the target with `argc == 1` and
   `argv == {"app", NULL}`:
   - **ZisK, zisk_sim:** `__libc_start_main(uBootstrap_main, …)`, the musl
     routine.
   - **OpenVM, Airbender:** `noos_start_main` (`modules/noos`), which
     replaces the full musl start-up. The musl start-up parses an auxv that
     this guest does not have, and it costs 126 instructions that the
     transpiler must accept.
   - **SP1:** `__start` of sp1-zkvm. Do not bypass it. It initialises the
     Rust allocator and the public-values hasher, which `syscall_write` and
     `syscall_halt` need. Then it calls `main`. Here, `main` is the
     `__wrap_main` of the entry object. It calls `noos_main` (the returning
     half of `noos_start_main`) and gives its result back, because SP1
     halts with that result.
3. `noos_start_main`/`noos_main` (and `__libc_start_main` on ZisK) set the
   thread pointer, run `.init_array` and enter `uBootstrap_main`.
4. `uBootstrap_main` (in `modules/ubootstrap/module.cpp`) calls
   `RhInitialize`, registers the managed-code range, runs all module
   initialisers, and then jumps into `__managed__Main`, which is the C#
   `Main`.

No kernel is under any of this. Each syscall that the runtime can make has
one of three results. A wrap makes it a no-op, a wrap returns a constant,
or the platform's `zkvm_platform_syscall` gets it. Only ZisK and zisk_sim
define that function, and they forward to the musl `syscall`. ZisK emulates
Linux syscalls, and zisk_sim runs on Linux. On SP1, OpenVM and Airbender,
`syscall` returns `-1`. This containment is necessary. SP1 reads the
syscall id from `t0` and stops with an error on an id that it does not
know. OpenVM services no `ecall` at all.

## Exit and exceptions

musl's `exit`/`_Exit`/`abort` use `exit_group` (syscall 94). No zkVM takes
it as the end of the program. Thus `pal` wraps all three and calls the
`zkvm_platform_exit` of the platform (see the [pal module](modules.md#pal)):

| Platform | Exit |
|----------|------|
| `zisk`, `zisk_sim` | `ecall` with `a7 == 93` (ZisK `CAUSE_EXIT`) |
| `sp1` | `zkvm_halt` of the bindings library, or without it the same syscalls: `COMMIT` ×8, `COMMIT_DEFERRED_PROOFS` ×8, then `HALT` with the code |
| `openvm` | `TERMINATE`, whose code is an immediate: 0 or 1 |
| `airbender` | code 0: the 16 output words in `x10`–`x25`, then a self-jump. Other codes: a write of the cycle CSR, which no circuit can prove |

The runtime dispatches managed exceptions. A `throw` walks the stack two
times: first for the filters, then to unwind with the `finally` funclets.
Then it resumes in the `catch`. Filters, rethrow with `InnerException` and
unwinding through many frames operate as on stock .NET. An exception that
nothing catches ends in `FailFast`. To enter a `try` costs nothing at run
time, so a guest that never throws does only one unwind-section lookup at
startup.

The unwinder finds those sections through the [eh module](modules.md#eh).
A zkVM image has no program headers of its own, so the module answers
`dl_iterate_phdr` with synthetic ones.

A build without unwind tables makes the opposite trade. The guest is
smaller, and a `throw` exits the guest. The runtime does not dispatch the
exception. Optionally, a `ZkvmThrow` handler of the program gets the throw
(see the [rhp module](modules.md#rhp) and the
[ExceptionHandler sample](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples/ExceptionHandler)).
`--remove-eh` selects this build. The conditions then see `eh: failfast`,
`rhp` wraps `RhpThrowEx`, and the post-link step of `zisk` removes the
tables. A platform with `"unwind": false` always builds this way. Airbender
is such a platform: its 4 MiB ROM cannot hold about half a MiB of tables,
and its linker script keeps none.
