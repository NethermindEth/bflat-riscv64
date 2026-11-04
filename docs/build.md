---
layout: default
title: Build & use
eyebrow: Hands-on
lead: >
  How to get bflat, how to build a guest for each zkVM, and how to pull in
  the bflat-* packages that define the zkVM platforms.
prev: /signing/
next: /verification/
---

## Get bflat

The easy way is the image. It has bflat, the .NET 11 SDK, lld and
llvm-objcopy:

| Image | Runtime release | Targets |
|-------|-----------------|---------|
| `nethermindeth/bflat-riscv64-11` | `11.0.100-upstream-perf.r400` | riscv64 zkVM platforms, linux-x64 |
| `nethermindeth/bflat-riscv64-11-riscv32` | `11.0.100-upstream-perf.r423` | the same, plus the riscv32 platform (Airbender) |
| `ghcr.io/nethermindeth/bflat-riscv64-11:pr-<number>` | as the default image | the default image of a pull request, for the bflat-ts runs |

The two .NET 11 images use different runtime releases. Each release is a
separate build of the dotnet-riscv patch series (see
[Runtime](runtime.md)). `upstream-perf` is the name of the patch profile;
the runtime is not a Microsoft build. The images `bflat-riscv64`,
`bflat-riscv64-min` and `bflat-riscv64-11-min` on Docker Hub are older
builds and get no updates.

```console
$ docker run --rm --platform linux/amd64 -v "$PWD:/w" -w /w \
    nethermindeth/bflat-riscv64-11 bflat --info
```

`bflat --info` shows the bundled runtime release and the SHA-256 of each
signing key that the binary trusts.

To build bflat from source, build the modules first, then bflat:

```console
$ ./build.sh modules riscv64    # every link-time module, and the uGC release
$ ./build.sh bflat   riscv64    # the compiler driver
$ ./build.sh layouts riscv64    # the bflat-compiled layouts, in layouts/
```

For the riscv32 target, also build the rv32 modules against the rv32 musl
sysroot, and build bflat with `-p:BflatTargetRiscV32=true`.
[`BUILDING.md`](https://github.com/NethermindEth/bflat-riscv64/blob/master/BUILDING.md)
has the prerequisites, the riscv32 steps and the Docker scripts.
`docker_shell.sh` opens a shell with the build toolchain
(`Dockerfile.build`). `build_docker_image.sh` builds the runtime image
(`Dockerfile`) and runs bflat in it.

## .NET version and runtime

bflat builds for **.NET 11** only. It bundles the `upstream-perf` runtime of
dotnet-riscv, which pairs with the
<a href="architecture.md#zkvm-ryujit-codegen-knobs">RyuJIT knobs</a> of the
platforms. The release tag is in
[`bflat.variant.props`](https://github.com/NethermindEth/bflat-riscv64/blob/master/src/bflat/bflat.variant.props).
The riscv32 build takes a later release that has the riscv32 target. See
[Runtime](runtime.md).

bflat needs a .NET 11 runtime to run. The image runs `bflat --info` as its
last build step, so an image that cannot start bflat does not build.

## Signatures of the downloads

The bflat build checks the signature of the dotnet-riscv release with the
public keys in `BFLAT_SIGNING_PUBLIC_KEY`. `build.sh modules` checks the uGC
release with the same keys. bflat writes these keys into its binary and
checks each `--extlib` `.nupkg` with them. Without the variable, nothing is
checked and the build says so. `-p:AllowUnsignedRuntime=true` and
`ALLOW_UNSIGNED_EXTERNAL=true` accept a release that has no signature. See
[Package signatures](signing.md).

## Build a guest

bflat knows no zkVM by itself. Each zkVM target is a **platform** that a
bflat-* package defines. The platform gives the entry point, the memory map,
the ISA, the ABI, the RyuJIT options and the post-link steps (see the
[manifest format](manifest.md#platform)). `--libc` selects the platform by
its alias, and `--extlib` gives the package. [Targets](targets.md) has the
details of each platform.

```console
$ bflat build app.cs --arch riscv64 --os linux --libc zisk --no-pie --no-pthread \
    --extlib https://github.com/NethermindEth/bflat-libziskos:v1.4.0-alpha.153
```

Give `--arch` each time: without it, bflat builds for the host
architecture. Give `--no-pie` too: position-independent code does not agree
with the fixed memory map. Every guest in this repository, in bflat-ts and in
Nethermind passes both.

| Platform | `--arch` | `--libc` | Package | Post-link steps |
|----------|----------|----------|---------|-----------------|
| ZisK | `riscv64` | `zisk` | [bflat-libziskos](https://github.com/NethermindEth/bflat-libziskos) | `patch_elf`: init array, TLS template, `.bss`; the unwind tables under `--remove-eh` |
| ZisK under QEMU | `riscv64` | `zisk_sim` | bflat-libziskos | none |
| SP1 | `riscv64` | `sp1` | [bflat-sp1](https://github.com/NethermindEth/bflat-sp1) | `patch_elf`: FENCE and zero words to NOP |
| OpenVM | `riscv64` | `openvm` | [bflat-openvm](https://github.com/NethermindEth/bflat-openvm) | none |
| Airbender | `riscv32` | `airbender` | [bflat-airbender](https://github.com/NethermindEth/bflat-airbender) | `patch_elf`: FENCE, signed M, SYSTEM; `flat_image`: `<out>.bin`, `<out>.text` |

The post-link steps change the ELF **in place**: the file that `-o` names is
the file that the zkVM runs. The `flat_image` steps of Airbender write two
more files next to it.

A manifest path makes bflat read every `*.bflat.manifest` in its directory.
The package of bflat-libziskos has the `zisk` and `zisk_sim` platforms and
the bindings (`libziskos.bflat.manifest`). A guest that does not use the
accelerators can use the platform files alone: `platform/build.sh <dir>` of
each package writes them.

### Airbender

The Airbender release has no `.nupkg`. Download its assets into one
directory, or build them with `build/build.sh airbender` of bflat-airbender,
and give the local manifest. Use the riscv32 image:

```console
$ bflat build app.cs --arch riscv32 --os linux --libc airbender --no-pie --no-pthread \
    --extlib <dir>/libairbender.bflat.manifest -o app
$ ls app app.bin app.text
```

The platform keeps no unwind tables, so a `throw` goes to the `ZkvmThrow`
handler of the guest, or ends the run (see
[Exit and exceptions](architecture.md#exit-and-exceptions)).

## Options

The options that matter for a guest:

| Option | What it does |
|--------|--------------|
| `--arch riscv64` / `riscv32` | The target architecture. The default is the host architecture. `riscv32` needs a layout with the riscv32 target |
| `--os linux` | The target OS. Each zkVM platform has the base `linux` + `musl` |
| `--libc <platform>` | The platform alias that an `--extlib` package defines |
| `--extlib <package>` | A package: `https://github.com/<owner>/<repo>:<tag>`, a path or URL to a `.nupkg`, or a path or URL to a `.bflat.manifest` |
| `--allow-unsigned` | Accept an `--extlib` package that has no signature, with a warning. A bad signature is always an error |
| `--no-pie` | Do not make a position-independent executable. Necessary for the fixed memory maps |
| `--no-pthread` | Remove the pthread dependency |
| `--stdlib dotnet` / `zero` / `none` | The C# standard library. The default is `dotnet` |
| `-O`, `-Os`, `-Ot`, `-O0` | Optimize; for size; for speed; no optimization. The RyuJIT tuning knobs of a platform apply only to optimized builds |
| `--no-stacktrace-data` | Remove the textual stack-trace tables. This saves much image size |
| `--no-exception-messages` | Remove the exception messages |
| `--no-reflection` | Disable reflection |
| `--remove-eh` | Remove the DWARF unwind tables: about 100 KiB smaller, but a `throw` ends the guest, and no `catch` or `finally` runs |
| `--no-unaligned-access` | Expand each unaligned access into a byte-wise sequence. The sp1 platform always builds like this |
| `-m <extensions>` | The instruction-set extensions of the target. A platform sets its own ISA in `codegen.isa` |
| `--codegenopt Name=Value` | Give one more RyuJIT option. The values are hexadecimal |
| `--feature Name=Value` | Set a feature switch |
| `--substitution <file>` | Apply one more ILLink substitutions file after the substitutions of `guest_subst` |
| `--lto` | Link with LTO (`--lto=full --lto-O3`). Only native libraries built with `-flto` take part |
| `--wrap-check` | Fail the build if a `--wrap=` target is not a real symbol |
| `--error-on-float` | Fail if the IL of an emitted method has a floating-point conversion. The known dead sites give only a warning |
| `--error-on-float-binary` | Fail if the linked image has an F or D instruction |
| `--error-on-compressed` | Fail if the linked image has a compressed (C) instruction |
| `--error-on-atomic` | Fail if the linked image has an atomic (A) instruction: `lr`, `sc`, `amo*` |
| `--mstat` | Write MSTAT and DGML files for size analysis |
| `--symchart` | After the link, run `readelf` and write an HTML chart of the symbol sizes |
| `--map <file>` | Write an object map file |
| `--keep-object` | Keep the object file that ILC writes |
| `-x` | Show the ILC and linker commands |
| `--verbose` | Show more output. On zisk, this also shows the function boundaries that `patch_elf` finds |

`bflat build --help` shows all the options.

## Run a guest

```console
# zisk_sim: RISC-V64 Linux, or QEMU user mode on another host
$ qemu-riscv64 ./app

# zisk: the ZisK emulator (-e takes the ELF; --rom takes a converted ROM)
$ ziskemu -e ./app

# airbender: the cli of matter-labs/zksync-airbender loads the two flat images
#   (tests/run-airbender.sh shows the input format and how to read the result)
$ cli run --bin ./app.bin --text ./app.text --input-file input.hex --cycles 2000000000
```

SP1 and OpenVM run the guest through their own host tools. The
[bflat-ts](https://github.com/NethermindEth/bflat-ts) suite has a runner for
each zkVM.

## Linking external libraries

A package has one or more `*.bflat.manifest` files in the
[manifest format](manifest.md). The bindings manifest of bflat-libziskos,
for example:

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

A manifest applies when its `when` holds: here, only on the `zisk`
platform. Its static libraries go into the link line, and its
`ilc.references` are compiled with the user code. Paths are relative to the
manifest. bflat also reads the schema 1 format (no `schema` key, one
`builds[]` entry for each arch, os and libc). That format cannot define a
platform.

A `.nupkg` must have a valid signature from a key that bflat trusts. bflat
treats a `.bflat.manifest` from a URL as unsigned. bflat does not check a
local `.bflat.manifest`. See [Package signatures](signing.md).

## Building a linux-x64 program

bflat keeps the ordinary linux-x64 target of NativeAOT. It builds an x64
Linux program with the full runtime and no zkVM module:

```console
$ bflat build app.cs --arch x64 --os linux
$ ./app
```

The program is linked dynamically against glibc, like a program from
`dotnet publish -p:PublishAot=true`. CI builds and runs one in each image.
See [Targets](targets.md#linux-x64).

## Known limitations of a zkVM guest

- **One thread.** There is one hart. `pthread_create` returns success and
  does nothing, and the locks do nothing. Code that needs a second thread to
  make progress stops or behaves incorrectly.
- **No file system.** `open` returns an error. A guest reads its input and
  writes its output through the API of the zkVM (for example the bindings of
  bflat-libziskos).
- **The console.** zisk, sp1, openvm and airbender have the `no-console`
  flag: `Console` writes go nowhere. Use the print function of the platform.
  zisk_sim keeps the stdio of the host.
- **Time and randomness are deterministic.** `clock_gettime` returns `-1`,
  and the `rng_stupid` [module](modules.md)
  gives a fixed sequence. The same build gives the same bytes in each run.
  A proof needs this.
- **Exceptions cover an explicit `throw` only.** `try`/`catch`/`finally`,
  filters and rethrow work. An exception that no code catches ends in
  `FailFast`. A hardware fault, for example a null dereference, does not
  become an exception: no signal handler is installed, and the zkVM raises no
  trap.
