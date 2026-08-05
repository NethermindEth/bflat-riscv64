# Building bflat from source

You'll need the .NET SDK to build bflat. The shipping binaries of bflat are built with bflat, but the .NET SDK is used for bootstrapping.

Before you can build bflat, you need to make sure you can restore the packages built out of the bflattened/runtime repo. For reasons that escape me, NuGet packages published to the Github registry require authentication. You need a github account and you need to create a PAT token to read packages. Follow the information [here](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

You should end up with a nuget.config file in src/bflat/ that looks roughly like this:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
    <packageSources>
        <add key="github" value="https://nuget.pkg.github.com/bflattened/index.json" />
    </packageSources>
    <packageSourceCredentials>
        <github>
            <add key="Username" value="YOURUSERNAME" />
            <add key="ClearTextPassword" value="YOURPAT" />
        </github>
    </packageSourceCredentials>
</configuration>
```

In retrospect, going with Github packages was a mistake, but I don't have the capacity to redo things that work right now. NuGet.config is in .gitignore so that you don't accidentally check it in. But to be doubly sure, make sure your PAT can only read packages and nothing else. Leaking such PAT would likely cause no damage to most people.

With the package issue out of the way, you can run bflat by executing:

```bash
$ dotnet run --project src/bflat/bflat.csproj
```

from the repo root, or build binaries by running:

```bash
$ dotnet build src/bflat/bflat.csproj
```

This will build/run bflat on top of the official .NET runtime.

To create bflat-compiled versions of bflat, run:

```bash
$ dotnet build src/bflat/bflat.csproj -t:BuildLayouts
```

This will create a `layouts` directory at the repo root and place Linux- and Windows-hosted versions of the bflat compiler built with bflat. These are the bits that are available as prebuilt binaries.

## Target .NET version

bflat can be built for .NET 10 or .NET 11 (default: 11), selected with the
`DotnetVersion` MSBuild property:

```bash
$ dotnet build src/bflat/bflat.csproj -p:DotnetVersion=10
```

or as the fourth argument of `build.sh`:

```bash
$ ./build.sh all riscv64 min 10
```

This picks both the TargetFramework (`net10.0`/`net11.0`) and the bundled
runtime/blob release line. The two versions build into separate
`src/bflat/bin/…/net1X.0` trees, so they don't overwrite each other.

Building `net11.0` requires a .NET 11 SDK; a .NET 11 SDK can also build the
`net10.0` flavor (downlevel targeting), so a single SDK 11 install covers
both. The Docker build environment (`Dockerfile.build`) defaults to an SDK 11
preview; pass `--build-arg SDK_VERSION=10.0.100` for a pure-.NET-10
environment. To *run* the dotnet-hosted `net10.0` build where only the .NET
11 runtime is installed (e.g. the default Docker image), set
`DOTNET_ROLL_FORWARD=LatestMajor`.

## ACSL contracts on native modules

Every function in the C modules under `src/bflat/modules/` carries a
Frama-C (ACSL) contract. CI enforces this with the "ACSL contract gate"
job, which fails when a module stops parsing under Frama-C or a defined
function has no contract. To run the same check locally (needs docker):

```bash
$ docker run --rm --platform linux/amd64 -v "$PWD:/w" -w /w debian:bookworm \
    bash -c "apt-get update -qq && apt-get install -y -qq frama-c-base gcc \
             python3 >/dev/null && python3 src/bflat/scripts/check_acsl.py"
```

Annotations inside macro bodies (nofp's stub generators) are only visible
to Frama-C when the preprocessor keeps comments through expansion; the
checker passes `-cpp-extra-args=-CC` for that. C++ modules (stdcppshim,
ubootstrap) carry ACSL++ annotations as documentation - plain Frama-C does
not parse C++ - and assembly modules are out of scope.

## Module unit tests

`src/bflat/modules/tests/run_tests.sh` unit-tests every function of the
C/C++ modules. The suites are cross-compiled for riscv64 (glibc, static)
and executed under qemu-user, so the module code runs on the real guest
ISA - including the raw ZisK exit ecall, which qemu maps to Linux
`__NR_exit` (both are 93 on riscv64). CI runs this as the "Module unit
tests" job; locally you need `gcc-riscv64-linux-gnu`,
`g++-riscv64-linux-gnu` and `qemu-user-static` (`qemu-user-hwe` on Ubuntu
26.04):

```bash
$ ./src/bflat/modules/tests/run_tests.sh
```

On Apple silicon (Docker + Rosetta) set `TEST_SKIP_FAULTS=1`: qemu-user
under nested emulation hangs on the designed-fault (SIGSEGV) tests instead
of delivering the signal. nofp's stub list is generated from the module
source at test-build time, so new stubs cannot silently miss coverage.

The assembly modules are covered too, since running on the real ISA is
what makes that possible:

- `rhp_native` (GC write barriers) — a small shim (`asm_shim.S`) marshals
  the t3/t4/t5 register convention the C ABI cannot express, and the
  suite is built twice, once per `BFLAT_DOTNET` contract, pinning the
  version-dependent t3 post-increment from both sides.
- `zkvm_zisk` / `zkvm_zisk_sim` `_start` — checks the handoff to
  `__libc_start_main`: managed entry point, `argc == 1`, `argv[0] ==
  "app"`, NULL terminator, sp inside the guest stack.

Two link-level details make this work, both handled by `run_tests.sh`:
the version symbol reaches the assembler via `-Wa,--defsym` and never as
a `-D` (gcc runs `.S` through cpp, which would rewrite the `.ifndef`
directive itself), and the modules' `_start` / `__libc_start_main` are
`objcopy --redefine-sym`'d in a private copy of the object so they do not
collide with crt1.o and libc in a hosted test binary.

## Fuzzing and proofs

Two input-driven parts of the modules get more than example-based tests:
pal's hand-written `vfprintf` parser and the bump-allocator family. Both
are covered by libFuzzer targets built for the HOST with ASan+UBSan (the
only riscv assembly in pal sits behind an `#if defined(__riscv)` guard):

```bash
$ FUZZ_TIME=60 ./src/bflat/modules/tests/fuzz/run_fuzz.sh   # needs clang
```

The allocator target drives random operation sequences (malloc / realloc
/ calloc / aligned_alloc / mark / reset) with unconstrained 64-bit sizes
and asserts the allocator invariants after each step. CI runs a
60-second smoke per target on every PR and uploads `crash-*` artifacts on
failure.

Only the hand-written seeds in `tests/fuzz/seeds/<target>/` are tracked;
they are passed to libFuzzer as a read-only input directory. The corpus
libFuzzer grows lands in the gitignored `tests/fuzz/out/corpus-<target>/`
- its *first* corpus argument is also its output, so pointing it at a
tracked directory turns a single local run into thousands of new files
(a 60-second run here produced ~10 MB). If a run uncovers something worth
keeping, copy that one unit into `seeds/` by hand.

On top of that, `tests/verify/` proves the allocator's bounds properties
with CBMC for *all* sizes and reachable states rather than sampled ones:

```bash
$ ./src/bflat/modules/tests/verify/run_verify.sh            # needs cbmc
```

The harness `#include`s the real `pal/module.c` with the heap-window
symbols re-pointed at a local array (the `ZK_HEAP_SYMBOLS_DEFINED` hook).
Unsigned-overflow checks are deliberately off: the allocator detects
oversized requests *by* wrapping (`req + 8 < req`), which is defined C.
The proof is mutation-tested - restoring the pre-fix pointer-subtraction
bounds check makes CBMC report the wild header write, so a passing run is
not vacuous.

## Build variants

The compiler can be built in two variants that differ in which runtime/blob
release (NethermindEth/dotnet-riscv) gets bundled:

- `perf` — performance-oriented runtime
- `min` — minimal runtime

The exact runtime release each variant maps to is defined in
`src/bflat/bflat.variant.props`; `bflat --info` prints the bundled version.
The variant is selected with the `Variant` MSBuild property (default: `perf`
for .NET 10; `min` for .NET 11, where perf blobs are not published yet):

```bash
$ dotnet build src/bflat/bflat.csproj -p:Variant=min
```

or as the third argument of `build.sh`:

```bash
$ ./build.sh all riscv64 min
```

Within one target .NET version, both variants build into the same
`src/bflat/bin/…` tree, overwriting each other; switching variants
re-extracts the runtime artifacts from the download cache (the downloads
themselves are cached per variant, so nothing is re-downloaded). The Docker
image packages whichever variant was built last for the `DOTNET_VERSION` it
was built with (`--build-arg DOTNET_VERSION=10|11`, default 11).
