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
