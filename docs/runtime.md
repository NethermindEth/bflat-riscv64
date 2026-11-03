---
layout: default
title: Runtime (dotnet-riscv)
eyebrow: The .NET runtime build
lead: >
  bflat-riscv64 has no runtime of its own. It downloads an official .NET
  build from the sibling project
  <a href="https://github.com/NethermindEth/dotnet-riscv">dotnet-riscv</a>.
  That build uses the upstream sources and the upstream build system. It
  adds riscv64 patches for upstream and zkVM code-quality patches.
prev: /
next: /architecture/
---

## An official build of upstream .NET

bflat links against .NET itself: the upstream VMR (`dotnet/dotnet`) at a
release branch. Upstream's own build system builds it, for a target that
upstream supports. The difference from upstream is a set of riscv64 patches:
a series for upstream, and code-quality patches for the zkVM guests on top.

This is possible because bflat applies each zkVM change *around* the
runtime (see [Architecture](architecture.md)):

- ILC-stage substitutions;
- link-time [modules](modules.md);
- post-link changes to the ELF.

A change goes into the earliest of these layers that can express it. Only a
change that none of them can make goes into .NET itself.

## The patches we carry

Each patch is in a *fixup profile* for one .NET version. Thus you can see
which patches apply to which .NET. bflat builds for .NET 11 only, so this
section describes the .NET 11 profiles:

<dl class="kv">
  <dt><code>upstream</code></dt>
  <dd>The numbered patches (<code>NN_*.patch</code>) staged for
    dotnet/runtime: the soft-float <code>lp64</code> ABI, the ISA sets and
    the gates on F, D, C and A emission, and the configurable
    <code>-march</code>/<code>-mabi</code> of the native build. The profile
    stands alone: its patches apply and build without the patches of the
    other profiles.</dd>
  <dt><code>upstream-perf</code></dt>
  <dd><code>upstream</code> plus the <code>perf-*.patch</code> files next to
    it: riscv64 code-quality patches for the zkVM guests. They work with
    the <a href="architecture.md#zkvm-ryujit-codegen-knobs">RyuJIT knobs</a>
    of the platforms. They are for zkVMs only and do not go upstream.
    bflat bundles this profile. The riscv32 target is available only with
    it.</dd>
  <dt><code>minimal</code>, <code>perf</code></dt>
  <dd>The older downstream series: correctness fixes only, and the same with
    code-quality work on top. They stay for comparison runs.</dd>
</dl>

The runtime build selects the profile (`patch_runtime.sh
minimal|perf|upstream|upstream-perf`). The patches have one directory for
each .NET major, so a new runtime release cannot take, by accident, the
fixups for the previous major. With the riscv32 target, the build applies
`fixup/<major>/riscv32/` on top of `upstream-perf`. It adds riscv32 to the
JIT, ILC and the NativeAOT runtime.

The directory has the full list of patches:
[`fixup/11/profile/upstream/`](https://github.com/NethermindEth/dotnet-riscv/tree/main/fixup/11/profile/upstream)
(its `README.md` gives the origin and the status of each patch) and
[`fixup/11/riscv32/`](https://github.com/NethermindEth/dotnet-riscv/tree/main/fixup/11/riscv32).

## What the build produces

[`NethermindEth/dotnet-riscv`](https://github.com/NethermindEth/dotnet-riscv)
makes the files that bflat links against. Its workflow
"Build .NET SDK" (`build.yml`) does these steps:

1. Pulls the upstream .NET VMR (`dotnet/dotnet`) at a release branch. The
   releases that bflat uses are built from `release/11.0.1xx`.
2. Applies the fixup profile for that .NET major (`upstream-perf` for
   bflat), and the riscv32 fixups when the riscv32 target is built.
3. Builds against an Alpine RISC-V64 cross rootfs from upstream's own
   `eng/common/cross/build-rootfs.sh`. We do not rebuild a distribution.
   Two packages are the exception. Alpine's feed has musl and `libatomic`
   for `rv64gc`, so the build compiles both again for `rv64im`
   (`build_musl_rv64im.sh`, `build_libatomic_rv64im.sh`). They replace the
   stock `libc.a`, `crt` objects and `libatomic.a`. A guest for the base
   ISA cannot link against compressed or atomic instructions. A change to
   the ABI marker of an object does not remove those instructions.
4. Packs the results into archives, signs them and publishes them as a
   release. bflat downloads the release by URL (see
   [Package signatures](signing.md)).

### Keeping the native runtime on the base ISA

The fixups guard hand-written assembly with `#ifdef __riscv_flen` and
`#ifdef __riscv_atomic`. The guards work only when these macros are not
defined, that is, when the code compiles for the base ISA. Upstream's CMake
sets no `-march` for a riscv64 *cross* target. Thus the build takes the
default of the toolchain, and the guards have no effect.

Patches of the `upstream` profile add two CMake variables. The build sets
them for all of the native build:

```sh
build.sh --arch riscv64 --os linux-musl \
    -cmakeargs "-DCLR_CMAKE_RISCV64_MABI=lp64" \
    -cmakeargs "-DCLR_CMAKE_RISCV64_MARCH=rv64im"
```

All components use one ISA: the NativeAOT runtime, the GC, both
libunwinds, `System.Native` and the SDK side. The CoreCLR PAL uses the
savannah libunwind. A patch lets it build with `__riscv_flen == 0`, so no
component needs a wider ISA. Earlier releases used a compiler wrapper
(`tools/clang`) for this. The CMake variables replace it.

After the build, `check_lp64.sh` makes sure that each riscv64 object has
the `lp64` soft-float ABI. To find an object that has F/D or A code, build
a guest with `--error-on-float-binary --error-on-atomic`. The check gives
the name of the symbol.

| Step | Script | Output |
|------|--------|--------|
| 0 | `00_build_rootfs.sh` | GNU and musl RISC-V64 cross rootfs, both from upstream tooling |
| 1 | `01_pack_compiler_linux.sh` | x64-Linux–hosted bflat driver binary |
| 2 | `02_pack_crossrootfs.sh` | Compressed cross rootfs as a release artifact |
| 3 | `03_pack_gnu_libs.sh` | GNU runtime libraries needed at link time |
| 4 | `04_pack_libs.sh` | Built CoreCLR runtime libraries (`libSystem.Native`, …) |
| 6 | `06_pack_refs.sh` | Reference assemblies bflat consumes |
| 7 | `07_pack_bflat_libs_linux.sh` | The NativeAOT runtime, CoreLib and the framework libraries of the riscv64 target |
| 8 | `08_pack_bflat_compiler_nupkg.sh` | ILCompiler (the AOT compiler bflat drives) packed as a NuGet `.nupkg` |
| 9 | `09_pack_bflat_compiler_native_linux.sh` | Native-RISC-V64–hosted driver |
| 10 | `10_build_runtime_x64.sh` | The x64 JIT and NativeAOT runtime, for the linux-x64 target |
| 11 | `11_pack_x64.sh` | The three assets of the linux-x64 target (see below) |
| 12 | `12_build_runtime_riscv32.sh` | The riscv32 JIT and NativeAOT runtime, for the riscv32 target |
| 13 | `13_pack_riscv32.sh` | The four assets of the riscv32 target (see below) |
| ⋯ | `xx_pack_whole_source.sh` | Source archive of the full tree |

The riscv32 sysroot has its own scripts: `build_musl_rv32im.sh`,
`build_builtins_rv32im.sh` (the compiler-rt builtins, in place of
`libgcc.a`), `build_libatomic_rv32im.sh`, `build_libcxx_headers_rv32.sh` and
`install_linux_headers_rv32.sh`. `check_ilp32.sh` is the ABI gate of that
target.

## How the artifact reaches bflat

bflat downloads one release that a property names. The release tag is
`RuntimeReleaseTag` in
[`bflat.variant.props`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/bflat.variant.props).
The tag is the SDK version of the VMR plus the profile and the run number:

| Build | Tag |
|-------|-----|
| Default | `11.0.100-upstream-perf.r400` |
| `-p:BflatTargetRiscV32=true` | `11.0.100-upstream-perf.r423`, a release of dotnet-riscv `main` that has the riscv32 target |

The tag changes with each new release. The download cache uses the tag as
its key, so a new tag downloads again. The build checks the signature of
the release before it uses it.

The build unpacks the archives into `lib/<os>/<arch>/<libc>` next to the
bflat binary. `bflat build` gives these files to the linker.

The layout build checks the float ABI of each RISC-V object
(`bflat-modules check-abi`): `lp64` in `lib/linux/riscv64/musl` and `ilp32`
in `lib/linux/riscv32/musl`. bflat links these files as they are.

### The linux-x64 target

A release can also have the ordinary linux-x64 target of bflat. The
`runtime-x64` job of dotnet-riscv builds it from dotnet/runtime at the
commit that the VMR pins. It uses the same fixups and the same JIT/EE GUID.
Thus the x64 JIT and CoreLib agree with the ILCompiler of the release:

| Asset | Unpacked into | Contents |
|-------|---------------|----------|
| `bflat-compiler-native-x64-linux-glibc-x64.zip` | the bflat directory | `libclrjit_unix_x64_x64.so` |
| `bflat-libs-linux-glibc-x64.zip` | `lib/linux/x64/glibc` | the NativeAOT runtime, CoreLib, the framework libraries |
| `libs-linux-glibc-x64.zip` | `lib/linux/x64/glibc` | crt objects, libc, libm, libgcc and the other files of the link, from the glibc rootfs of the dotnet/runtime cross-build container |

Microsoft's x64 packages do not work with this ILCompiler, because the
fixups change the JIT/EE interface. bflat uses the target when
`BflatTargetLinuxX64` in `bflat.variant.props` is `true` (the default);
`-p:BflatTargetLinuxX64=false` builds without it.

### The riscv32 target

The `runtime-riscv32` job of dotnet-riscv builds the RV32IM `ilp32` target
of the Airbender platform. It runs only with the `upstream-perf` profile
and the riscv32 option of the workflow. Its assets are:

| Asset | Unpacked into | Contents |
|-------|---------------|----------|
| `bflat-compiler-native-riscv32-linux-glibc-x64.zip` | the bflat directory | `libclrjit_unix_riscv32_x64.so`, the JIT that ILCompiler loads for riscv32 |
| `bflat-libs-linux-musl-riscv32.zip` | `lib/linux/riscv32/musl` | the NativeAOT runtime, CoreLib, `libSystem.Native`, the framework libraries |
| `libs-linux-musl-riscv32.zip` | `lib/linux/riscv32/musl` | crt objects, libc, libatomic and the compiler-rt builtins of the link |
| `crossrootfs-musl-riscv32.tar.xz` | `RV32_SYSROOT` | the whole rv32im sysroot with headers, for the C modules (`build.sh modules riscv32`) |

The pack step fails in these conditions:

- a file that bflat links is missing;
- an object is not `ilp32`, or claims the C extension;
- an object has a compressed, atomic or fence instruction
  (`tools/isa_scan.py`).

bflat adds the target to its layout with `-p:BflatTargetRiscV32=true`. The
layout keeps the riscv64 target too.

## License

dotnet-riscv uses the MIT license. The fixups against upstream keep the
licenses of their original authors (most of .NET is MIT). See the
project's
[`LICENSE.md`](https://github.com/NethermindEth/dotnet-riscv/blob/main/LICENSE.md).
