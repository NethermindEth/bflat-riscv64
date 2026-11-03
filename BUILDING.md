# Building bflat from source

Bflat downloads its runtime and blob artifacts from the public releases of
[dotnet-riscv](https://github.com/NethermindEth/dotnet-riscv), and the
`ugc-zero` module from the releases of
[uGC](https://github.com/NethermindEth/uGC). No package authentication is
necessary.

## Prerequisites

- A .NET 11 SDK. The image and CI use `11.0.100-preview.6.26359.118`
  (`SDK_VERSION` in `Dockerfile` and in the workflow).
- `clang`, `clang++` and `lld`. Clang compiles the C and C++ modules for
  `riscv64-linux-gnu` (and for `riscv32-unknown-linux-musl`).
- `gcc-riscv64-linux-gnu` and `binutils-riscv64-linux-gnu`: the sysroot that
  clang compiles against, and the assembler of the riscv64 assembly modules.
- `g++-riscv64-linux-gnu` and `qemu-user-static` (`qemu-user-hwe` on Ubuntu
  26.04) for the module unit tests.
- Python 3 with `lief` and `pyelftools` for `patch_elf.py`.
- `curl` and `xz-utils` for the downloads.

`docker_shell.sh` gives a shell with all of this (see [Docker](#docker)).

## Build order

The bflat build copies the native modules into the compiler layout, so build
the modules first:

```console
$ ./build.sh modules riscv64     # every module.o, and the uGC release
$ ./build.sh bflat riscv64       # dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64
$ ./build.sh layouts riscv64     # the bflat-compiled layouts, in layouts/
$ ./build.sh all riscv64         # the three steps above
```

`./build.sh modules riscv64` reads the module manifests
(`src/bflat/modules/*/bflat-manifest.json`) with `bflat-modules plan`, and
runs the compile steps that it writes. The output of `./build.sh bflat
riscv64` is in `src/bflat/bin/Debug/net11.0`. That is the bflat that the
Docker image copies.

`./build.sh layouts riscv64` builds bflat with bflat. The only host is
linux-glibc-x64: the Windows and arm64 hosts of upstream bflat are off
(`src/bflat/bflat.build.props`).

To compile the managed code only, with no module and no layout:

```console
$ dotnet build src/bflat/bflat.csproj -p:SkipCompilerLayout=true
```

## Target .NET version and runtime

Bflat builds for .NET 11 (`net11.0`) only. It bundles the
`upstream-perf` runtime of dotnet-riscv: the riscv64 fixes that we send
upstream, plus the zkVM code-quality patches. `RuntimeReleaseTag` in
`src/bflat/bflat.variant.props` is the release tag, and `bflat --info`
prints it. The build output and the download cache use the tag as their
key, so a new tag downloads the new release and extracts it again.

| Build | Release tag | Targets in the layout |
|-------|-------------|-----------------------|
| default | `11.0.100-upstream-perf.r400` | linux-musl-riscv64, linux-glibc-x64 |
| `-p:BflatTargetRiscV32=true` | `11.0.100-upstream-perf.r423` | the same, plus linux-musl-riscv32 |

`-p:BflatTargetLinuxX64=false` builds without the linux-x64 target.

The build checks the signature of the release with the public keys in
`BFLAT_SIGNING_PUBLIC_KEY`, and writes the keys into the bflat binary.
`build.sh modules` checks the uGC release in the same way. Without the
variable, nothing is checked and the build says so. See
[Package signatures](docs/signing.md).

## The riscv32 target (Airbender)

`--arch riscv32` builds an RV32IM ilp32 guest for a riscv32 platform:
ZKsync Airbender, in [bflat-airbender](https://github.com/NethermindEth/bflat-airbender).
The target needs the r423 release, which has the riscv32 JIT, the NativeAOT
runtime and the rv32im musl, and the rv32 objects of the modules:

```console
$ ./build.sh modules riscv64
$ RV32_SYSROOT=<sysroot> ./build.sh modules riscv32
$ dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64 -p:BflatTargetRiscV32=true
```

`RV32_SYSROOT` is the rv32im musl sysroot with the libc++ and Linux headers:
the `crossrootfs-musl-riscv32.tar.xz` asset of the r423 release, unpacked.
`./build.sh modules riscv32` writes each `module.rv32.o` with clang
(`-march=rv32im -mabi=ilp32`, no LTO, one section for each function). The
riscv32 flavor builds only modules. The layout keeps the riscv64 target.

A guest:

```console
$ bflat build app.cs --arch riscv32 --os linux --libc airbender --no-pie --no-pthread \
    --extlib <bflat-airbender>/libairbender.bflat.manifest -o app
```

This writes the ELF and, next to it, `app.bin` (the ROM image) and
`app.text` (the instructions). The Airbender tools load these two files. The
platform keeps no unwind tables, so the guest builds as with `--remove-eh`.
`tests/build-airbender.sh` builds the test guests, and
`tests/run-airbender.sh` runs them on the Airbender v3 simulator. See
[Targets](docs/targets.md#airbender).

## Docker

| Script | What it does |
|--------|--------------|
| `docker_shell.sh [<command>]` | Builds `Dockerfile.build` (the build toolchain) as the image `bflat-riscv64`, and opens a shell in it, or runs the command. The repository is mounted at the same path |
| `build_docker_image.sh [<args>]` | Builds `Dockerfile` (the runtime image) as `nethermindeth/bflat-riscv64-test`, then runs `bflat <args>` in it in the current directory |

`Dockerfile` copies `src/bflat/bin/Debug/net11.0`, so run
`./build.sh all riscv64` first. Its last step runs `bflat --info`: an image
that cannot start bflat fails to build.

CI builds two images: `nethermindeth/bflat-riscv64-11` and, with the
riscv32 target, `nethermindeth/bflat-riscv64-11-riscv32`. A manual run of
the workflow with `publish` set pushes them to Docker Hub as `latest` and as
the commit SHA. A pull request from this repository pushes the default image
to GHCR as `ghcr.io/nethermindeth/bflat-riscv64-11:pr-<number>`.

## Checks you can run locally

These are the checks that CI runs, plus the ZisK tests that need `ziskemu`.
[Verification](docs/verification.md) tells what each one is for.

**ACSL contracts.** Each function in the C modules has a Frama-C contract.
The check fails when a module does not parse, or when a defined function has
no contract.

```console
$ docker run --rm --platform linux/amd64 -v "$PWD:/w" -w /w debian:bookworm \
    bash -c "apt-get update -qq && apt-get install -y -qq frama-c-base gcc \
             python3 >/dev/null && python3 src/bflat/scripts/check_acsl.py"
```

The C++ modules have ACSL++ annotations as documentation (Frama-C does not
parse C++). The assembly modules are not in scope.

**Proof of the `eh` module.** WP proves its contracts, and Eva runs it end to
end for both shapes of an image:

```console
$ docker run --rm -v "$PWD":/w -w /w framac/frama-c:30.0 \
    src/bflat/modules/eh/verify/run_wp.sh
```

**C# analyzers.** All .NET analyzers are on (`AnalysisMode=All`), and
`.editorconfig` selects the errors:

```console
$ dotnet build src/bflat/bflat.csproj -t:Rebuild -p:SkipCompilerLayout=true -warnaserror
```

**Module manifests.** The schema file and the parser of bflat must agree:

```console
$ python3 src/bflat/scripts/check_manifests.py          # needs python3-jsonschema
$ dotnet run --project src/bflat-modules -- check src/bflat/modules
```

**bflat unit tests.** They test the manifest parser, the package signatures,
the `--extlib` resolution and the float-ABI check of the layout. They also
test the ISA verifier, for ELF64 and ELF32. They need only the .NET SDK. With `python3`
and `openssl`, they also sign with `sign_package.py` and check that bflat
accepts the result:

```console
$ dotnet test src/bflat.UnitTests
```

`src/bflat.Tests` builds and runs a program with the compiler layout. CI
does not run it.

**Module unit tests.** These are compiled for riscv64 and run under
qemu-user, so the module code runs on the real guest ISA:

```console
$ ./src/bflat/modules/tests/run_tests.sh
```

On Apple silicon (Docker and Rosetta), set `TEST_SKIP_FAULTS=1`. Under
nested emulation, qemu-user stops on the designed-fault tests and does not
deliver the signal.

`rhp_native` is tested through a shim that sets the t3/t4/t5 registers,
which the C ABI cannot express. The test pins the write-barrier contract. The
zkVM entry points are in the platform packages, with their own unit tests
(`tests/platform/run.sh` in each package).

**Fuzzing.** The `vfprintf` of pal and the bump allocator are fuzzed on the
host with ASan and UBSan. The allocator target runs random
malloc/realloc/calloc/aligned_alloc/mark/reset sequences with any 64-bit
size, and checks the invariants after each step.

```console
$ FUZZ_TIME=60 ./src/bflat/modules/tests/fuzz/run_fuzz.sh   # needs clang
```

Git tracks only the seeds in `tests/fuzz/seeds/<target>/`. The corpus that
libFuzzer writes goes into `tests/fuzz/out/`, which git ignores. To keep an
input, copy it into `seeds/`.

**Allocator proof.** CBMC proves the bounds properties for all sizes and all
reachable states:

```console
$ ./src/bflat/modules/tests/verify/run_verify.sh            # needs cbmc
```

The harness includes the real `pal/module.c`, with the heap-window symbols
set to a local array (the `ZK_HEAP_SYMBOLS_DEFINED` hook). The
unsigned-overflow checks are off on purpose: the allocator finds oversized
requests *by* wrap-around (`req + 8 < req`), which is defined C.

**Guest tests.** These need a built bflat (or the image) and the platform
files. `platform/build.sh <dir>` of bflat-libziskos, bflat-sp1 and
bflat-openvm can write all of them into one directory:

```console
$ BFLAT_PLATFORMS=<dir> tests/run-unaligned-access.sh     # CI runs it
$ BFLAT_PLATFORMS=<dir> tests/run-zisk-bitmanip.sh        # needs ziskemu; not in CI
$ BFLAT_PLATFORMS=<dir> tests/run-zisk-hash-helpers.sh    # needs ziskemu; not in CI
$ BFLAT_AIRBENDER=<dir> OUT=<out> tests/build-airbender.sh
$ AIRBENDER_CLI=<cli> GUESTS=<out> tests/run-airbender.sh
```

- `run-unaligned-access.sh` checks that `Unsafe.ReadUnaligned` stays one
  `ld` on zisk and openvm, and that sp1 builds it from bytes.
- `run-zisk-bitmanip.sh` runs, in `ziskemu`, the operand widths that the Zb
  lowering fixes of dotnet-riscv#9 correct.
- `run-zisk-hash-helpers.sh` runs `HashHelpers.IsPrime` and `GetPrime` of
  CoreLib in `ziskemu`. They use `Math.Sqrt`, which is the soft-float musl
  `sqrt` on the guest.
- `build-airbender.sh` and `run-airbender.sh` build the Airbender guests and
  run them on the Airbender v3 simulator (the `cli` of
  matter-labs/zksync-airbender, at `AIRBENDER_COMMIT` in the workflow).

**linux-x64.** With the image, `bflat build --arch x64 --os linux` builds an
x64 program that runs on the host. CI builds and runs one.

## CI

`.github/workflows/build-riscv64.yml` runs on each push and pull request to
master. It runs the checks above. It builds bflat and the image for the
default and the riscv32 SDK, links a guest for each zkVM target, and runs the
Airbender guests. On a pull request from this repository, it runs the
`bflat-ts/simple` test of [bflat-ts](https://github.com/NethermindEth/bflat-ts)
on zk-testing with the image of the pull request. Then it runs the full
suite.
[Verification](docs/verification.md#ci) has the list of jobs.
