# Building bflat from source

You need the .NET SDK. The shipping binaries of bflat are built with bflat;
the SDK is used for bootstrapping. Everything bflat downloads — the runtime
and blob artifacts from [dotnet-riscv](https://github.com/NethermindEth/dotnet-riscv)
— comes from public releases, so no package authentication is involved.

Run bflat straight from source:

```bash
$ dotnet run --project src/bflat/bflat.csproj
```

or build it:

```bash
$ dotnet build src/bflat/bflat.csproj
```

Both produce a bflat hosted on the official .NET runtime. To produce the
bflat-compiled, Linux- and Windows-hosted binaries that ship as prebuilt
releases:

```bash
$ dotnet build src/bflat/bflat.csproj -t:BuildLayouts
```

They land in a `layouts` directory at the repo root.

## Target .NET version and runtime

bflat builds for .NET 11 (`net11.0`) and bundles the perf runtime of
dotnet-riscv: the upstream series plus the zkVM code-quality patches (an
`*-upstream-perf` release). The release tag is in
`src/bflat/bflat.variant.props`, and `bflat --info` prints it. The build
needs a .NET 11 SDK; the Docker build environment (`Dockerfile.build`) has
one.

The build output and the download cache are keyed by the release tag, so a
new tag re-extracts the runtime artifacts and downloads only the new
release.

## The riscv32 target (Airbender)

`--arch riscv32` builds an RV32IM ilp32 guest for a riscv32 platform: ZKsync
Airbender, whose platform and runtime library are the package
[bflat-airbender](https://github.com/NethermindEth/bflat-airbender). The
target needs a runtime release with the riscv32 target of dotnet-riscv (its
JIT, the NativeAOT runtime and the rv32im musl), and the modules built for
rv32 as well:

```bash
$ ./build.sh modules riscv64
$ RV32_SYSROOT=<sysroot> ./build.sh modules riscv32
$ dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64 -p:BflatTargetRiscV32=true
```

`RV32_SYSROOT` is the rv32im musl sysroot, with the libc++ and Linux
headers: the `crossrootfs-musl-riscv32.tar.xz` asset of the same release,
unpacked. `BflatTargetRiscV32=true` takes the release that has the riscv32
target (`bflat.variant.props`) and adds `lib/linux/riscv32/musl` and the
riscv32 JIT to the layout. The layout keeps the riscv64 target too.

A guest:

```bash
$ bflat build --arch riscv32 --os linux --libc airbender --no-pie --no-pthread \
    --extlib <bflat-airbender>/libairbender.bflat.manifest -o app app.cs
```

writes the ELF and, beside it, `app.bin` (the ROM image) and `app.text`
(the instructions), which the Airbender tools load. The platform keeps no
unwind tables, so the guest builds as with `--remove-eh`.
`tests/build-airbender.sh` builds the test guests and
`tests/run-airbender.sh` runs them on the Airbender simulator; CI does both
(the `riscv32` build of "Build RISC-V64").

## Checks you can run locally

These are the same gates CI runs. [Verification](docs/verification.md)
explains what each one is for; below is how to run them.

**ACSL contracts.** Every function in the C modules carries a Frama-C
contract; the gate fails when a module stops parsing or a defined function
has none.

```bash
$ docker run --rm --platform linux/amd64 -v "$PWD:/w" -w /w debian:bookworm \
    bash -c "apt-get update -qq && apt-get install -y -qq frama-c-base gcc \
             python3 >/dev/null && python3 src/bflat/scripts/check_acsl.py"
```

C++ modules carry ACSL++ annotations as documentation (plain Frama-C does
not parse C++) and assembly modules are out of scope.

**Proof of the `eh` module.** WP discharges its contracts and Eva runs it
end to end in both image shapes:

```bash
$ docker run --rm -v "$PWD":/w -w /w framac/frama-c:30.0 \
    src/bflat/modules/eh/verify/run_wp.sh
```

**bflat unit tests.** The manifest parser, the package signatures, the
`--extlib` resolution and the float-ABI check of the layout. They need only
the .NET SDK; with `python3` and `openssl` they also sign with
`sign_package.py` and check that bflat accepts the result:

```bash
$ dotnet test src/bflat.UnitTests
```

**Module unit tests.** Cross-compiled for riscv64 and executed under
qemu-user, so module code runs on the real guest ISA — including the raw
ZisK exit ecall. Needs `gcc-riscv64-linux-gnu`, `g++-riscv64-linux-gnu` and
`qemu-user-static` (`qemu-user-hwe` on Ubuntu 26.04):

```bash
$ ./src/bflat/modules/tests/run_tests.sh
```

On Apple silicon (Docker + Rosetta) set `TEST_SKIP_FAULTS=1`: under nested
emulation qemu-user hangs on the designed-fault tests instead of delivering
the signal.

The assembly modules are covered too. `rhp_native` is exercised through a
shim that marshals the t3/t4/t5 register convention the C ABI cannot
express, so the write-barrier contract (`t3` kept, as .NET 11 wants) is
pinned.
The zkVM entry points are not here: they are in the platform packages
(bflat-libziskos, bflat-sp1, bflat-openvm), with their unit tests
(`tests/platform/run.sh` in each package, the same harness under qemu).

**Fuzzing.** pal's hand-written `vfprintf` parser and the bump-allocator
family are fuzzed on the host with ASan+UBSan; the allocator target drives
random malloc/realloc/calloc/aligned_alloc/mark/reset sequences with
unconstrained 64-bit sizes and asserts the invariants after each step.

```bash
$ FUZZ_TIME=60 ./src/bflat/modules/tests/fuzz/run_fuzz.sh   # needs clang
```

Only the hand-written seeds in `tests/fuzz/seeds/<target>/` are tracked and
passed as a read-only input directory; the corpus libFuzzer grows lands in
the gitignored `tests/fuzz/out/`. Worth keeping an input? Copy that single
unit into `seeds/` by hand.

**Allocator proof.** CBMC proves the bounds properties for *all* sizes and
reachable states rather than sampled ones:

```bash
$ ./src/bflat/modules/tests/verify/run_verify.sh            # needs cbmc
```

The harness `#include`s the real `pal/module.c` with the heap-window symbols
re-pointed at a local array (the `ZK_HEAP_SYMBOLS_DEFINED` hook).
Unsigned-overflow checks are deliberately off: the allocator detects
oversized requests *by* wrapping (`req + 8 < req`), which is defined C.
