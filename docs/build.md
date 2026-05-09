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
$ ./build.sh bflat   riscv64    # build the bflat compiler driver
$ ./build.sh layouts riscv64    # produce the redistributable layouts
```

`build.sh` wraps `dotnet build` and the cross-compilation of every C /
C++ / asm module under `src/bflat/modules/`. You need:

- The .NET SDK (10.0+).
- `riscv64-linux-gnu-gcc`, `g++`, `as`, and `objdump` (Ubuntu's
  cross-toolchain or the equivalent).
- A NuGet config in `src/bflat/` that authenticates against the
  `bflattened` GitHub package registry — see
  [BUILDING.md](https://github.com/NethermindEth/bflat-riscv64/blob/master/BUILDING.md)
  for how to mint a PAT.
- Python 3 with `lief` and `pyelftools` for the postprocessor.

A Dockerfile (`Dockerfile.build`) bundles all of this; run
`./build_docker_image.sh` once to build it, then `./docker_shell.sh` to
get a shell with the toolchain in place.

## Build a C# program for Zisk

```console
$ bflat build hello.cs --os linux --libc zisk
```

Optional but useful flags:

| Flag | What it does |
|------|--------------|
| `--arch riscv64` | Set explicitly; otherwise inferred from `--libc` |
| `--no-stacktrace-data` | Drop textual stack-trace tables. Saves significant binary size. |
| `--no-globalization` | Forced on for `zisk` / `zisk_sim`; listed for clarity. |
| `-Os` / `-Ot` | Optimise for size or speed. zkVMs reward size — every prover-step counts. |
| `--mstat` | Emit MSTAT and DGML files for `dotnet-stat` size analysis. |
| `--symchart` | After linking, run `readelf` and produce an HTML symbol-size chart. |
| `-x` | Print the compiler and linker commands as they run. |

The output is a single ELF file. For `--libc zisk`, that file is the
postprocessed binary ready for Zisk; for `--libc zisk_sim`, it runs
under `qemu-riscv64` or natively on RISC-V64 Linux.

## Run a built binary

```console
# Native RISC-V64 host
$ ./hello

# x86 host with QEMU user-mode
$ qemu-riscv64 ./hello

# Inside Zisk (refer to the Zisk repo for the prover invocation)
$ ziskemu --rom ./hello
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

Every package must contain a `*.bflat.manifest` JSON file at its zip root:

```json
{
  "name": "libziskos",
  "package_version": "1.0.0",
  "builds": [
    {
      "arch":   "riscv64",
      "os":     "linux",
      "libc":   "zisk",
      "static_lib":          "runtimes/linux-riscv64/native/libziskos.a",
      "dotnet_lib":          "lib/net10.0/Nethermind.ZiskBindings.dll",
      "dotnet_assemblyname": "Nethermind.ZiskBindings"
    }
  ]
}
```

bflat picks the entry whose `arch / os / libc` triple matches the build
target. The static library is added to the link line; the .NET assembly
is referenced and AOT-compiled along with the user code. Paths are
relative to the manifest.

The canonical example is
[`bflat-libziskos`](https://github.com/NethermindEth/bflat-libziskos),
which exposes Zisk's precompile API to managed code.

## Targeting the simulator

```console
$ bflat build app.cs --os linux --libc zisk_sim
```

The build produces a fully static RISC-V64 ELF that uses the same
runtime, the same allocator, the same TLS shim — but skips the ELF
postprocessor and links a slightly looser linker script. Use it when
you need to debug a problem under GDB without provisioning a real Zisk
environment.

## Known limitations

- **Multi-threading.** There is exactly one thread of execution. Locks
  are no-ops, `pthread_create` returns success without doing anything.
  Code that relies on parallel progress will deadlock or misbehave.
- **Filesystem and console.** `open`, `__stdio_write`, and `__stdio_read`
  all return failure. Programs must do their I/O through whatever the
  zkVM provides (in Zisk's case, the precompile API).
- **Time and randomness are deterministic.** `clock_gettime` returns
  `-1`.
