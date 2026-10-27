---
layout: default
title: Build & use
eyebrow: Hands-on
lead: >
  Building bflat itself, building your own programs against it, and
  pulling in external NuGet packages such as <code>libziskos</code>.
prev: /modules/
next: /verification/
---

## Build bflat from source

The full path is documented in [`BUILDING.md`](https://github.com/NethermindEth/bflat-riscv64/blob/master/BUILDING.md);
the short version:

```console
$ ./build.sh modules riscv64    # build all link-time modules
$ ./build.sh bflat   riscv64    # build the compiler driver
$ ./build.sh layouts riscv64    # produce the redistributable layouts
```

`build.sh` wraps `dotnet build` and the cross-compilation of every C /
C++ / asm module under `src/bflat/modules/`. You need:

- A .NET 11 SDK — see [.NET version and runtime](#net-version-and-runtime)
  for which one.
- `clang` and `clang++` — they compile the C and C++ modules
  (cross-targeted with `--target=riscv64-linux-gnu`, so no separate cross
  compiler is needed for those).
- The `riscv64-linux-gnu` binutils (`as`, `objcopy`, `objdump`) for the
  assembly modules and the layout steps, plus the `gcc`/`g++` cross
  packages — which supply the sysroot clang compiles against, and are the
  compilers used for the module unit tests.
- Python 3 with `lief` and `pyelftools` for the postprocessor.

A Dockerfile (`Dockerfile.build`) bundles all of this; run
`./build_docker_image.sh` once to build it, then `./docker_shell.sh` to
get a shell with the toolchain in place.

## .NET version and runtime

bflat builds against **.NET 11** and bundles the performance-oriented
runtime of dotnet-riscv (an `*-upstream-perf` release), paired with the
<a href="architecture.md#zkvm-ryujit-codegen-knobs">RyuJIT knobs</a>. The
release tag is in
[`bflat.variant.props`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/bflat.variant.props),
which is the source of truth as release tags move; the download cache is
keyed by that tag.

One Docker image is published: `nethermindeth/bflat-riscv64-11`.

**About the SDK.** bflat needs a .NET 11 SDK to build and a .NET 11
runtime to run. The published image installs a .NET 11 SDK and runs
`bflat --info` on the finished image, so a mismatch fails the image build
instead of the first use.

## Verifying a build

The module test, fuzzing and proof suites are described on the
[Verification](verification.md) page.

The build checks the signature of the dotnet-riscv release that it
downloads, with the public keys in `BFLAT_SIGNING_PUBLIC_KEY`, and writes
these keys into the bflat binary. Without the variable, nothing is checked
and the build says so. Set `-p:AllowUnsignedRuntime=true` to use a release
that has no signature. See [Package signatures](signing.md).

## Build a C# program for Zisk

bflat knows no zkVM by itself. Each zkVM target is a **platform** that its
bflat-* package defines: the entry point, the memory map, the instruction
set, the RyuJIT options and the post-link steps (refer to the
[manifest format](manifest.md#platform)). `--libc` selects the platform by
its alias, and `--extlib` must give the package:

```console
$ bflat build hello.cs --os linux --libc zisk \
    --extlib path/to/libziskos.bflat.manifest
```

The package is
[`bflat-libziskos`](https://github.com/NethermindEth/bflat-libziskos) (NuGet:
`Nethermind.ZiskOS.Runtime`). A manifest path makes bflat read every
`*.bflat.manifest` in its directory: there the platform (`zisk.bflat.manifest`)
and the bindings (`libziskos.bflat.manifest`) come together. A guest that does
not use the accelerators can be built with the platform files alone
(`platform/build.sh` of the package).

Optional but useful flags:

| Flag | What it does |
|------|--------------|
| `--arch riscv64` | Set explicitly; otherwise inferred from `--libc` |
| `--no-stacktrace-data` | Drop textual stack-trace tables. Saves significant binary size. |
| `--remove-eh` | Strip the DWARF unwind tables: ~100 KiB smaller, but a `throw` exits the guest instead of running `catch`/`finally`. |
| `--no-globalization` | Forced on for every zkVM target; listed for clarity. |
| `-Os` / `-Ot` | Optimise for size or speed. zkVMs reward size — every prover-step counts. |
| `--mstat` | Emit MSTAT and DGML files for `dotnet-stat` size analysis. |
| `--symchart` | After linking, run `readelf` and produce an HTML symbol-size chart. |
| `--error-on-float` | Fail the build if any emitted method's IL contains a floating-point conversion. Known-dead sites are listed and reported as warnings. |
| `--error-on-float-binary` | Fail if the *linked binary* contains any F/D instruction. Catches what the IL scan cannot see — comparisons, and native objects. |
| `--error-on-compressed` | Fail if the linked binary contains a compressed (C) instruction. |
| `--error-on-atomic` | Fail if the linked binary contains an atomic (A) instruction: `lr`/`sc`, `amo*`. |
| `--allow-unsigned` | Accept an `--extlib` package that has no signature, with a warning. A bad signature is still an error. |
| `--substitution <file>` | Apply an extra ILLink substitutions file on top of the built-in zkVM set. |
| `-x` | Print the ILC and linker commands as they run. |

The post-link steps of a platform (its `postlink` section) rewrite the ELF
**in place**: what `-o` names is what the zkVM runs, and there is no second
file to choose between. ZisK's step fixes the init array and TLS template and
trims `.bss`; SP1's rewrites the encodings its transpiler rejects (FENCE and
alignment padding). The `zisk_sim` and `openvm` platforms have none.

## Run a built binary

```console
# Native RISC-V64 host
$ ./hello

# x86 host with QEMU user-mode
$ qemu-riscv64 ./hello

# Inside Zisk's emulator, passed with -e
# (--rom takes an already-converted ROM, not an ELF)
$ ziskemu -e ./hello
```

## Linking external libraries via NuGet

bflat understands `--extlib` arguments that point at NuGet packages.
Three forms are accepted:

```console
$ bflat build app.cs --os linux --libc zisk \
    --extlib repo:version          # GitHub release with a single .nupkg attachment
    --extlib path/to/package.nupkg # local nupkg
    --extlib path/to/package.bflat.manifest   # local manifest, sources resolved relatively
```

A package contains one or more `*.bflat.manifest` files in the
[manifest format](manifest.md). A manifest path stands for its whole
directory: bflat reads every `*.bflat.manifest` there. The bindings manifest
of bflat-libziskos, for example:

```json
{
  "schema": 2,
  "name": "libziskos",
  "version": "1.0.0",
  "when": { "platform": "zisk" },
  "objects": [{ "file": "libziskos.a", "group": "pre-entry" }],
  "link": { "wrap": ["memcpy", "memset", "memmove", "memcmp"] }
}
```

A manifest applies when its `when` holds: here only on the `zisk` platform.
Its static libraries are added to the link line, its `ilc.references` are
referenced and AOT-compiled along with the user code. Paths are relative to
the manifest. bflat also reads the format of before (no `schema` key; one
`builds[]` entry per arch/os/libc), which cannot define a platform.

A `.nupkg` must be signed with a key that bflat trusts, and a
`.bflat.manifest` from a URL is treated as unsigned. A local
`.bflat.manifest` is not checked. `--allow-unsigned` accepts an unsigned
package with a warning; a bad signature is always an error. See
[Package signatures](signing.md).

## Targeting the simulator

```console
$ bflat build app.cs --os linux --libc zisk_sim \
    --extlib path/to/zisk_sim.bflat.manifest
```

The `zisk_sim` platform is in bflat-libziskos too. Same runtime, allocator
and TLS shim as the Zisk target, but without the post-link step, without
libziskos and with a slightly looser linker script — the build to reach for
when debugging under GDB.

## Targeting SP1 and OpenVM

```console
$ bflat build app.cs --os linux --libc sp1 --extlib path/to/libsp1.bflat.manifest
$ bflat build app.cs --os linux --libc openvm --extlib path/to/libopenvm.bflat.manifest
```

The platforms are in
[`bflat-sp1`](https://github.com/NethermindEth/bflat-sp1) and
[`bflat-openvm`](https://github.com/NethermindEth/bflat-openvm). Same modules,
allocator and substitutions again; what changes is the entry point, the
memory map and the halt protocol, all in `platform/` of each package. SP1
asserts naturally aligned memory accesses, so its platform always builds
with the byte-wise expansion `--no-unaligned-access` asks for by hand;
OpenVM executes misaligned accesses natively and gets wide ones.

## Building a linux-x64 program

bflat keeps the stock NativeAOT targets. With a release that has the
linux-x64 target (`BflatTargetLinuxX64` in `bflat.variant.props`, see
[the runtime](runtime.md#the-linux-x64-target)), it builds an ordinary
x64 Linux program, with the full runtime and no zkVM module:

```console
$ bflat build app.cs --arch x64 --os linux
$ ./app
```

The program is dynamically linked against glibc, like a program that
`dotnet publish -p:PublishAot=true` makes. CI builds and runs one in the
image of each leg that has the target.

## Known limitations

- **Multi-threading.** There is exactly one thread of execution. Locks
  are no-ops, `pthread_create` returns success without doing anything.
  Code that relies on parallel progress will deadlock or misbehave.
- **Filesystem and console.** `open`, `__stdio_write`, and `__stdio_read`
  all return failure. Programs must do their I/O through whatever the
  zkVM provides (in Zisk's case, the precompile API).
- **Time and randomness are deterministic.** `clock_gettime` returns `-1`,
  and the RNG entry points are answered by a fixed-sequence PRNG (see the
  [rng_stupid module](modules.md#rng-stupid)) — the same build produces the
  same bytes on every run, which is what a reproducible proof requires.
- **Exceptions cover explicit `throw` only.** `try`/`catch`/`finally`,
  filters and rethrow work; an uncaught exception ends in `FailFast`. A
  hardware fault — a null dereference, say — kills the guest with `SIGSEGV`
  instead of surfacing as an exception: nothing installs a signal handler
  and the emulator raises no trap.
