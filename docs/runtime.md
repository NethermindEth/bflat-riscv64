---
layout: default
title: Runtime — dotnet-riscv
eyebrow: The patched .NET runtime
lead: >
  bflat-riscv64 doesn't ship its own runtime. It downloads one — built by
  the sibling project <a href="https://github.com/NethermindEth/dotnet-riscv">dotnet-riscv</a>
  — that takes upstream .NET and applies a focused set of patches to make
  it survive on a stripped-down RISC-V64 machine.
prev: /alpine/
next: /architecture/
---

## What it is

[`NethermindEth/dotnet-riscv`](https://github.com/NethermindEth/dotnet-riscv)
is the build pipeline that produces the runtime artifacts bflat-riscv64
links against. It does **not** maintain a runtime fork in the usual
sense — there is no parallel source tree to keep up to date.

20 numbered patches touch **70 of the 56,796** files in upstream
[`dotnet/runtime`](https://github.com/dotnet/runtime) (≈ 0.12 %), with
**+493 net lines** out of 11.1 M (≈ 0.004 %). Bumping to a new .NET
version is a rebase, not a fork. Instead, the project:

1. Pulls a specific upstream .NET VMR (`dotnet/dotnet`) at a tagged
   release branch, e.g. `release/10.0.100`.
2. Applies a numbered series of patches under
   [`patches/bflat-runtime/`](https://github.com/NethermindEth/dotnet-riscv/tree/main/patches/bflat-runtime)
   and one patch under
   [`patches/sdk/`](https://github.com/NethermindEth/dotnet-riscv/tree/main/patches/sdk).
3. Builds the runtime against a custom Alpine-based RISC-V64 cross
   rootfs.
4. Packs the results into NuGet-compatible archives that bflat-riscv64's
   `BuildCommand.cs` consumes by URL.

When you run `bflat build` with `--libc zisk` or `--libc zisk_sim`,
that release archive is what gets unpacked into your `lib/<arch>/<os>/<libc>`
directory. The runtime libraries the linker pulls in (`libSystem.Native`,
the CoreLib reference assemblies, the AOT bootstrap objects, the GC
`uGC.cpp.obj` family, …) all originate here.

## Why a patched runtime is necessary

zkVMs constrain the runtime far more aggressively than a normal Linux
RISC-V64 host. Stock .NET assumes:

- the full `rv64gc` ISA (compressed instructions, hardware floating point);
- a kernel underneath it for syscalls, signals, and randomness;
- non-deterministic constructs like security cookies, JIT addresses, and
  the system clock.

Each of those would either crash inside a zkVM or make the proof
non-reproducible. The patches in
[`patches/bflat-runtime/`](https://github.com/NethermindEth/dotnet-riscv/tree/main/patches/bflat-runtime)
remove the assumption rather than add a workaround at the call site.

## The patches, by purpose

There are 20 numbered patches. They divide cleanly into five groups.
The runtime also carries a set of zkVM codegen-tuning patches that pair
with the [RyuJIT knobs](architecture.md#zkvm-ryujit-codegen-knobs) bflat
passes in optimized builds; they are summarised at the end.

### RISC-V64 ISA constraints

| # | Patch | What it changes |
|---|-------|-----------------|
| 11 | `riscv64_uncompress` | Replaces RV64C compressed instruction sequences in CoreCLR's hand-written assembly thunks with full 32-bit encodings. Zisk's prover only knows uncompressed instructions. |
| 13 | `riscv64_unfloat` | Strips floating-point instruction emission from the runtime native build. The `lp64d` calling convention is preserved (matching Alpine and bflat-emitted user code), but the resulting objects are tagged with the `lp64` (soft-float) marker in their ELF e_flags so `ld.lld` accepts them alongside the rest of the stack. |
| 15 | `nofp_jit` | Strips floating-point opcode emission from the RISC-V64 JIT. Even though we run AOT, the JIT codegen path is reused by NativeAOT to lower IL, so any FP it emits would land in the final binary. |
| 14 | `no_jump_tables_riscv64` | One-line change in `jit/lower.cpp`: forces switch lowering to use a sequence of compares-and-branches instead of a jump table. Switch tables would otherwise land inside `.text` as data — exactly what Zisk's instruction preprocessor cannot tolerate. |
| 20 | `splitcodedata` | The structural fix that obviates the postprocessor's old `--split-code-data` step. Adds two new RISC-V64 PC-relative relocation kinds (`IMAGE_REL_RISCV64_PCREL_HI20` / `_LO12_I`) for paired `auipc + addi/ld` sequences and tells the JIT *not* to bundle method `roData` adjacent to hot code. As a result, AOT now emits roData as a separate `MethodReadOnlyDataNode` that lands cleanly in `.rodata` — code stays code, data stays data, no ELF surgery required. |

### NativeAOT / runtime startup

| # | Patch | What it changes |
|---|-------|-----------------|
| 1  | `no_publish` | Skips the publish step in `Subsets.props` so the build produces unpacked artifacts. |
| 2  | `vxsort` | Disables the AVX2-only VXSort GC sort path on architectures that can't compile it. |
| 3  | `native_targets` | Adjusts `Microsoft.NETCore.Native.Unix.targets` so the AOT pipeline picks up the cross-compiled runtime libraries. |
| 4  | `aot_eventing` | CMake fix for the AOT runtime's eventing sources on the cross build. |
| 5  | `private_core_lib` | Tweaks `System.Private.CoreLib` internals to make a couple of types reachable from outside their assembly — bflat's link-time wrappers reference them. |
| 6  | `coreclr_setting_tunnel` | Adds a hook in `CompilerTypeSystemContext` so bflat can pass extra knobs to the underlying ILC. |
| 7  | `coreclr_startup` | Adjusts the NativeAOT runtime startup so it cooperates with `ubootstrap` rather than expecting a glibc-style entry path. |
| 18 | `resolution` | Patches `MetadataVirtualMethodAlgorithm` to nudge virtual-method resolution into a path bflat handles correctly. |

### Zerolib (the minimal core lib)

| # | Patch | What it changes |
|---|-------|-----------------|
| 8 | `zerolib` | Adds the upstream `zerolib` minimal CoreLib variant to the NativeAOT CMake build. |
| 9 | `zerolib_manifest` | Registers the zerolib output in the SFX manifest so it's packaged in `Microsoft.NETCore.App`. |

bflat itself ships [its own `zerolib`](https://github.com/NethermindEth/bflat-riscv64/tree/master/src/zerolib);
these patches make sure the parallel zerolib in the runtime tree builds
against the same configuration.

### Determinism for proofs

| # | Patch | What it changes |
|---|-------|-----------------|
| 16 | `tls_opt` | Disables the TLS-relaxation optimisation for musl + RISC-V64. Cherry-picked from upstream PR #121662. Without it the linker rewrites TLS sequences in a way our minimal TLS shim can't follow. |
| 17 | `bigint` | Tightens `Number.BigInteger.cs` parsing so the result is bit-identical across runs — the upstream code path picks up host-specific buffer sizes. |

### Build infrastructure

| # | Patch | What it changes |
|---|-------|-----------------|
| 10 | `riscv64.patch` | Base RISC-V64 toolchain config in `eng/native/configurecompiler.cmake`. |
| 12 | `alpine_custom` | Allows the upstream `eng/common/cross/build-rootfs.sh` to build against our [custom Alpine variant](alpine.md). The driver script tolerates this patch failing — useful when upstream removes a sentinel comment we keyed off. |

### zkVM codegen tuning

These pair with the [RyuJIT knobs](architecture.md#zkvm-ryujit-codegen-knobs)
bflat sets in optimized builds: the knob turns a behaviour on, the patch
makes it safe on the ZisK target.

| Pairs with knob | What it changes |
|-----------------|-----------------|
| `JitObjectStackAllocationSize` | Lifts the in-loop heap restriction so larger objects can be stack-allocated. |
| `JitRiscV64DmaCompare` | Lets the JIT lower constant-size `SpanHelpers.SequenceEqual` to the `csrs 0x814 / addi` idiom that ZisK folds into one `dma_xmemcmp` step. ZisK-only — a plain riscv64 CPU would mis-execute it. |
| `RiscV64ElideLeafRaSave` | Enables and guards leaf RA elision: RyuJIT riscv64 uses `REG_RA` as a hardcoded scratch (branch/compare constants, far-jump targets, 64-bit mul-high), so leaf methods whose LIR contains those shapes (`GT_JCMP`, comparisons, `GT_MULHI`) or use FP are not elided. |

Plus one SDK-side patch:

| # | Patch | What it changes |
|---|-------|-----------------|
| — | `patches/sdk/crossgen2.patch` | Tweaks the SDK's `crossgen2` invocation so cross-building under the bflat layout produces the right artifacts. |

## The build pipeline

The repository top level is a numbered series of shell scripts. Each
one performs a self-contained packaging step and emits a tarball into
`output/`.

| Step | Script | Output |
|------|--------|--------|
| 0 | `00_build_rootfs.sh` | Custom Alpine RISC-V64 cross rootfs (used by every later script) |
| 1 | `01_pack_compiler_linux.sh` | x64-Linux–hosted bflat compiler binary |
| 2 | `02_pack_crossrootfs.sh` | Compressed cross rootfs as a release artifact |
| 3 | `03_pack_gnu_libs.sh` | Patched GNU runtime libraries needed at link time |
| 4 | `04_pack_libs.sh` | Built CoreCLR runtime libraries (`libSystem.Native`, etc.) |
| 6 | `06_pack_refs.sh` | Reference assemblies that bflat consumes |
| 7 | `07_pack_bflat_libs_linux.sh` | bflat-side static libraries (`uGC.cpp.obj`, the AOT bootstrap, …) |
| 8 | `08_pack_bflat_compiler_nupkg.sh` | bflat compiler packed as a NuGet `.nupkg` |
| 9 | `09_pack_bflat_compiler_native_linux.sh` | Native-RISC-V64–hosted bflat compiler |
| ⋯ | `xx_pack_whole_source.sh` | Source archive of the full patched tree |

`patch_runtime.sh` and `patch_alpine.sh` are the entry points that
apply the patch series in order. They are invoked by the numbered
scripts; they fail loudly on any patch except `12_alpine_custom`,
which is allowed to be a no-op when upstream Alpine drifts.

The whole pipeline runs in CI under
[`build.yml`](https://github.com/NethermindEth/dotnet-riscv/actions/workflows/build.yml).
Successful runs cut a GitHub release whose tag matches the bflat
release that consumes it.

## How the artifact reaches bflat-riscv64

bflat-riscv64 doesn't fetch the runtime ad-hoc. Each version of bflat
embeds the URL of a specific dotnet-riscv release in its build scripts.
At build time:

1. The bflat layout build downloads the matching release archives.
2. Their contents land in `lib/<arch>/<os>/<libc>` next to the bflat
   binary.
3. When you run `bflat build`, those files are what `BuildCommand.cs`
   feeds to the linker — the `<runtime libraries from dotnet-riscv>`
   placeholder in [the link command](architecture.md#stage-2--the-link-command).

Bumping the runtime is therefore a two-repository operation: tag a new
dotnet-riscv release, then update the URL constants in bflat-riscv64's
build scripts to point at the new tag.

## License

dotnet-riscv ships under the MIT license. Patches the project carries
remain under their original authors' licenses (most of upstream .NET is
also MIT). See the project's
[`LICENSE.md`](https://github.com/NethermindEth/dotnet-riscv/blob/main/LICENSE.md).
