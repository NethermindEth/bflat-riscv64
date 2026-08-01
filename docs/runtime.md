---
layout: default
title: Runtime — dotnet-riscv
eyebrow: The .NET runtime build
lead: >
  bflat-riscv64 doesn't ship its own runtime. It downloads one — built by
  the sibling project <a href="https://github.com/NethermindEth/dotnet-riscv">dotnet-riscv</a>
  — from upstream .NET sources. The guiding aim is that those sources stay
  unpatched.
prev: /
next: /architecture/
---

## The aim: stock .NET

Every adaptation a zkVM needs is applied *around* the runtime rather than
inside it. That is a deliberate constraint, and it is what makes bumping
.NET a routine operation instead of a merge exercise: the toolchain tracks
upstream rather than forking it.

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

## What still touches .NET

A small, tracked set — and the direction of travel is to shrink it. The
runtime repository is moving from an open-ended patch queue to per-version
*fixup profiles*:

<dl class="kv">
  <dt><code>minimal</code></dt>
  <dd>Correctness fixes for riscv64 code generation only — the kind that
    belongs upstream. Four fixups for each of .NET 10 and .NET 11.</dd>
  <dt><code>perf</code></dt>
  <dd>Optional. Adds riscv64 code-quality work on top of
    <code>minimal</code>; pairs with the
    <a href="architecture.md#zkvm-ryujit-codegen-knobs">RyuJIT knobs</a>
    bflat passes in optimized builds.</dd>
</dl>

Profiles are selected when the runtime is built (`patch_runtime.sh
minimal|perf`) and are versioned per .NET major, so a new runtime release
never silently reuses fixups written against the previous one.

Four upstreamable fixups per .NET line, carried for one reason only: the
zkVM target. Nothing else justifies touching .NET.

### The minimal profile, in full

| Fixup | What it changes | Why it can't move out |
|-------|-----------------|-----------------------|
| `11_riscv64_honor_isa_mode_asm` | Guards CoreCLR's hand-written riscv64 assembly with `#if __riscv_flen != 0` and friends, so it honours the ISA it was configured for | Hand-written `.S` is not reachable from any bflat layer |
| `14_riscv64_honor_isa_mode_atomic_asm` | The same for the atomic sequences (`#ifdef __riscv_atomic`) in exception handling | As above |
| `20_…splitcodedata` | Stops the JIT folding read-only data chunks into the code chunk on riscv64, so data with code pointers lands in `.rodata` | A codegen decision; no post-hoc rewrite is safe |
| `22_riscv64_stubdispatch` (.NET 10) / `22_riscv64_dispatchresolve_tail` (.NET 11) | The `tail` pseudo-instruction expands to `auipc + jalr`, clobbering the dispatch-cell address the resolver needs | Assembly thunk, same reason |

To put a size on it — measured on the `feature/minimal` branch at commit
`46e7df4`, 1 August 2026:

| Profile | Fixups | Files touched | Lines |
|---------|--------|---------------|-------|
| .NET 10 `minimal` | 4 | 23 | +372 / −21 |
| .NET 11 `minimal` | 4 | 13 | +141 / −9 |

The commit is named on purpose. This set is worked on actively — a fixup
landing upstream, or a concern moving out into one of bflat's three
layers, changes these numbers — so a figure without a ref is not something
you can check. What is stable is the *shape*: a per-version profile of
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
   release branch — `release/10.0.1xx` or `release/11.0.1xx`.
2. Applies the fixup profile for that .NET major.
3. Builds against an Alpine RISC-V64 cross rootfs produced by upstream's
   own `eng/common/cross/build-rootfs.sh` — there is no rebuilt
   distribution of our own any more. One package is the exception:
   Alpine's feed ships musl built for `rv64gc`, so musl is rebuilt from
   the same aport for `rv64im` and the stock `libc.a` and `crt` objects
   in the rootfs are overwritten with it. A guest that decodes only the
   base ISA cannot link against compressed or atomic instructions, and
   no marker patching hides that.
4. Packs the results into archives that bflat downloads by URL.

| Step | Script | Output |
|------|--------|--------|
| 0 | `00_build_rootfs.sh` | GNU and musl RISC-V64 cross rootfs, both from upstream tooling |
| 1 | `01_pack_compiler_linux.sh` | x64-Linux–hosted bflat driver binary |
| 2 | `02_pack_crossrootfs.sh` | Compressed cross rootfs as a release artifact |
| 3 | `03_pack_gnu_libs.sh` | GNU runtime libraries needed at link time |
| 4 | `04_pack_libs.sh` | Built CoreCLR runtime libraries (`libSystem.Native`, …) |
| 6 | `06_pack_refs.sh` | Reference assemblies bflat consumes |
| 7 | `07_pack_bflat_libs_linux.sh` | bflat-side static libraries (`uGC.cpp.obj`, the AOT bootstrap, …) |
| 8 | `08_pack_bflat_compiler_nupkg.sh` | The driver packed as a NuGet `.nupkg` |
| 9 | `09_pack_bflat_compiler_native_linux.sh` | Native-RISC-V64–hosted driver |
| ⋯ | `xx_pack_whole_source.sh` | Source archive of the full tree |

## How the artifact reaches bflat

bflat does not fetch the runtime ad-hoc. Each build resolves a release tag
from two properties in
[`bflat.variant.props`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/bflat.variant.props):

- **`DotnetVersion`** (`10` or `11`) selects the .NET line;
- **`Variant`** (`perf` or `min`) selects which runtime build gets bundled —
  the performance-oriented one or the minimal one, mirroring the fixup
  profiles of the same names.

Together they form the release tag (`v10.0.0.p1`, `v11.0.0.x3`, …). Treat
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
