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

## Target .NET version

bflat builds for .NET 10 or .NET 11 (default: 11), selected with the
`DotnetVersion` MSBuild property:

```bash
$ dotnet build src/bflat/bflat.csproj -p:DotnetVersion=10
```

or as the fourth argument of `build.sh`:

```bash
$ ./build.sh all riscv64 min 10
```

This picks both the TargetFramework (`net10.0`/`net11.0`) and the bundled
runtime release line; the two build into separate `src/bflat/bin/…/net1X.0`
trees. A .NET 11 SDK can build both flavors (downlevel targeting), so one
install covers everything. The Docker build environment
(`Dockerfile.build`) defaults to an SDK 11 preview; pass
`--build-arg SDK_VERSION=10.0.100` for a pure-.NET-10 environment. To *run*
the `net10.0` build where only the .NET 11 runtime is installed, set
`DOTNET_ROLL_FORWARD=LatestMajor`.

## Build variants

Two variants differ in which runtime release gets bundled:

- `perf` — performance-oriented runtime
- `min` — minimal runtime

The mapping lives in `src/bflat/bflat.variant.props`, and `bflat --info`
prints the bundled version. Select with the `Variant` property (default:
`perf` for .NET 10, `min` for .NET 11):

```bash
$ dotnet build src/bflat/bflat.csproj -p:Variant=min
$ ./build.sh all riscv64 min
```

Within one target .NET version both variants build into the same
`src/bflat/bin/…` tree and overwrite each other; switching variants
re-extracts the runtime artifacts from the download cache, which is keyed
per variant, so nothing is re-downloaded. The Docker image packages
whichever variant was built last for its `DOTNET_VERSION`
(`--build-arg DOTNET_VERSION=10|11`, default 11).

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
express, built twice — once per `BFLAT_DOTNET` contract — so the
version-dependent write-barrier behaviour is pinned from both sides.
`zkvm_zisk` / `zkvm_zisk_sim` `_start` is checked for its handoff to
`__libc_start_main`: managed entry point, `argc == 1`, `argv[0] == "app"`,
NULL terminator, and sp inside the guest stack.

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
