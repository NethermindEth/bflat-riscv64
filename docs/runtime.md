---
layout: default
title: Runtime — dotnet-riscv
eyebrow: The .NET runtime build
lead: >
  bflat-riscv64 doesn't ship its own runtime. It downloads an official .NET
  build — produced by the sibling project
  <a href="https://github.com/NethermindEth/dotnet-riscv">dotnet-riscv</a>
  from upstream sources, with upstream's own build system, plus a handful of
  riscv64 fixes we intend to upstream.
prev: /
next: /architecture/
---

## An official build, not a fork

The runtime bflat links against is .NET itself: the upstream VMR
(`dotnet/dotnet`) at a release branch, built by upstream's own build system,
for a target upstream already supports. It is not a re-implementation, not a
vendored subset and not a long-lived fork — the delta against the release
branch is a handful of riscv64 patches, each of which we intend to send
upstream, and after which this project would carry none.

That is possible because every adaptation a zkVM needs is applied *around*
the runtime rather than inside it, which is also what makes bumping .NET a
routine operation instead of a merge exercise.

The three layers that absorb the work all live in bflat, not in the
runtime — see [Architecture](architecture.md):

1. **ILC stage** — managed method bodies are replaced through an
   ILLink substitutions file and whole-body C# snippets. This is where
   floating point is removed from CoreLib paths that would otherwise emit
   F/D instructions.
2. **Link stage** — native [modules](modules.md) are injected and existing
   symbols redirected to them with `--wrap`. No runtime source changes;
   the linker simply resolves a call somewhere else.
3. **Post-link** — the ELF is rewritten for the prover's loader.

An adaptation belongs to the earliest of those layers that can express it.
Only what none of them can reach is allowed to touch .NET itself.

## The patches we carry

Each one is a riscv64 correctness fix that belongs upstream and is written to
be proposed there — not a zkVM-specific hack kept private. They are tracked
as per-version *fixup profiles* rather than an open-ended patch queue, so
what is carried, and against which .NET, is always legible:

<dl class="kv">
  <dt><code>minimal</code></dt>
  <dd>Correctness fixes for riscv64 code generation only — the kind that
    belongs upstream.</dd>
  <dt><code>perf</code></dt>
  <dd>Optional. Adds riscv64 code-quality work on top of
    <code>minimal</code>; pairs with the
    <a href="architecture.md#zkvm-ryujit-codegen-knobs">RyuJIT knobs</a>
    bflat passes in optimized builds.</dd>
</dl>

Profiles are selected when the runtime is built (`patch_runtime.sh
minimal|perf`) and are versioned per .NET major, so a new runtime release
never silently reuses fixups written against the previous one.

A short list per .NET line, carried for one reason only: the zkVM target
needs riscv64 code generation to be correct today. Nothing else justifies
touching .NET, and every entry is meant to leave this repository by being
accepted upstream rather than by being maintained here. The counts differ
between majors — .NET 11 needs one fixup .NET 10 does not, because only its JIT can
emit compressed instructions in the first place — so the directory, not this
page, is the roster:
[`fixup/<major>/profile/<profile>/`](https://github.com/NethermindEth/dotnet-riscv/tree/feature/minimal/fixup).

### The minimal profile, in full

| Fixup | What it changes | Why it can't move out |
|-------|-----------------|-----------------------|
| `11_riscv64_honor_isa_mode_asm` | Guards CoreCLR's hand-written riscv64 assembly with `#if __riscv_flen != 0` and friends, so it honours the ISA it was configured for | Hand-written `.S` is not reachable from any bflat layer |
| `14_riscv64_honor_isa_mode_atomic_asm` | The same for the atomic sequences (`#ifdef __riscv_atomic`) in exception handling | As above |
| `20_…splitcodedata` | Stops the JIT folding read-only data chunks into the code chunk on riscv64, so data with code pointers lands in `.rodata` | A codegen decision; no post-hoc rewrite is safe |
| `22_riscv64_stubdispatch` (.NET 10) / `22_riscv64_dispatchresolve_tail` (.NET 11) | The `tail` pseudo-instruction expands to `auipc + jalr`, clobbering the dispatch-cell address the resolver needs | Assembly thunk, same reason |
| `24_riscv64_gate_compressed_emission` (.NET 11) | Adds `EnableRiscV64Compressed`, gating the one place the JIT emits a compressed encoding | A JIT decision; .NET 10's RyuJIT has no RVC support to gate |
| `25_riscv64_gate_atomic_emission` | Adds `EnableRiscV64Atomic`; with it off, `Interlocked` lowers to plain load/modify/store | Register lifetimes change with the lowering, so it cannot be a post-hoc rewrite |

This set is worked on actively — fixups land upstream, and concerns move out
into one of bflat's three layers — so any count written here would be stale
before it was useful. What is stable is the *shape*: a per-version profile of
correctness-only fixes, small enough to read in one sitting.

Historically this list was far longer — a numbered series that stripped
compressed instructions and floating point out of the runtime after the
fact. Those concerns are now handled where they belong: the runtime is
*built* for the target ISA and its assembly honours that, while FP
elimination in managed code moved to bflat's ILC stage.

## What the build produces

[`NethermindEth/dotnet-riscv`](https://github.com/NethermindEth/dotnet-riscv)
is the pipeline that produces the artifacts bflat links against. It:

1. Pulls a specific upstream .NET VMR (`dotnet/dotnet`) at a tagged
   release branch — `release/10.0.1xx` or `release/11.0.1xx-preview7`.
2. Applies the fixup profile for that .NET major.
3. Builds against an Alpine RISC-V64 cross rootfs produced by upstream's
   own `eng/common/cross/build-rootfs.sh` — there is no rebuilt
   distribution of our own any more. Two packages are the exception:
   Alpine's feed ships musl and `libatomic` built for `rv64gc`, so both are
   rebuilt for `rv64im` and the stock `libc.a`, `crt` objects and
   `libatomic.a` are overwritten with them. A guest that decodes only the
   base ISA cannot link against compressed or atomic instructions, and no
   marker patching hides that.
4. Packs the results into archives that bflat downloads by URL.

### Keeping the native runtime on the base ISA

The fixups above guard hand-written assembly with `#ifdef __riscv_flen` and
`#ifdef __riscv_atomic`, which only helps if those macros are actually
undefined — that is, if the code is compiled for the base ISA. Nothing in
upstream's CMake sets a `-march` for a riscv64 *cross* target, so the build
would otherwise inherit the toolchain default and the guards would never
fire.

A compiler wrapper (`tools/clang`, pointed at by `CLR_CC`/`CLR_CXX`) supplies
it. Compilations bound for the client — the NativeAOT runtime tree, the GC
and llvm-libunwind objects it pulls in, and `System.Native` — get
`-march=rv64im -mabi=lp64`; everything SDK-side (the CoreCLR PAL, the
savannah libunwind it uses, corehost) keeps the rootfs-native `rv64imafd`,
because it runs on real hardware where atomics matter. Objects are retagged
to the double-float ABI afterwards so `lld` does not refuse to mix them.

Classification is by path, and that is the part to be careful with: a
NativeAOT target whose sources live outside the `nativeaot/` tree is
recognised by its CMake target directory rather than by the source path,
because `make` runs the compiler from inside the target's build directory
and the object path it passes carries no `nativeaot/` component. When that
rule was missing, a minority of objects — the GC, llvm-libunwind, the shared
runtime assembly — were compiled with F/D and A while the rest were clean,
and the resulting guest was rejected by the emulator with an opaque invalid-
instruction panic. If you add a client-bound target, check that it is
classified: build a guest with `--error-on-float-binary --error-on-atomic`,
which names the offending symbol.

| Step | Script | Output |
|------|--------|--------|
| 0 | `00_build_rootfs.sh` | GNU and musl RISC-V64 cross rootfs, both from upstream tooling |
| 1 | `01_pack_compiler_linux.sh` | x64-Linux–hosted bflat driver binary |
| 2 | `02_pack_crossrootfs.sh` | Compressed cross rootfs as a release artifact |
| 3 | `03_pack_gnu_libs.sh` | GNU runtime libraries needed at link time |
| 4 | `04_pack_libs.sh` | Built CoreCLR runtime libraries (`libSystem.Native`, …) |
| 6 | `06_pack_refs.sh` | Reference assemblies bflat consumes |
| 7 | `07_pack_bflat_libs_linux.sh` | bflat-side static libraries (`uGC.cpp.obj`, the AOT bootstrap, …) |
| 8 | `08_pack_bflat_compiler_nupkg.sh` | ILCompiler (the AOT compiler bflat drives) packed as a NuGet `.nupkg` |
| 9 | `09_pack_bflat_compiler_native_linux.sh` | Native-RISC-V64–hosted driver |
| ⋯ | `xx_pack_whole_source.sh` | Source archive of the full tree |

Step numbers are historical and not contiguous — a step that stopped being
needed keeps its number free rather than renumbering the rest.

## How the artifact reaches bflat

bflat does not fetch the runtime ad-hoc. Each build resolves a release tag
from two properties in
[`bflat.variant.props`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/bflat.variant.props):

- **`DotnetVersion`** (`10` or `11`) selects the .NET line;
- **`Variant`** (`perf` or `min`) selects which runtime build gets bundled —
  the performance-oriented one or the minimal one, mirroring the fixup
  profiles of the same names.

Together they form the release tag (`v10.0.0.p3`, `v11.0.0.x8`, …). Treat
`bflat.variant.props` as the source of truth: the exact tags move as
runtime releases are cut, and the blob cache is keyed by them, so switching
variant or .NET version re-downloads rather than reusing a stale layout.

At build time the archives land in `lib/<os>/<arch>/<libc>` next to the
bflat binary; when you run `bflat build`, those files are what the driver
feeds to the linker.

Because the CoreLib that guests compile against is already on disk at that
point, bflat's own build re-checks its
[ILC-stage substitutions](architecture.md#stage-15--ilc-stage-substitutions)
against it — a runtime bump that moves a substituted method fails the build
immediately instead of at the first guest compilation.

## License

dotnet-riscv ships under the MIT license. Fixups carried against upstream
remain under their original authors' licenses (most of .NET is MIT). See
the project's
[`LICENSE.md`](https://github.com/NethermindEth/dotnet-riscv/blob/main/LICENSE.md).
