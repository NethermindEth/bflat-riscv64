# Bflat

[![Build RISC-V64](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml/badge.svg)](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml)

Nethermind's Bflat is a fork of C# NativeAOT compiler originally developed by [MichalStrehovsky](https://github.com/MichalStrehovsky).

The main feature of Nethermind's Bflat is that it addssupport for RISC-V64 fully static binaries. It is used for building [StatelessExecutor](https://github.com/NethermindEth/nethermind) on RISC-V64.


## Motivation

[Original bflat](https://github.com/bflattened/bflat) builds only dynamically linked binaries. In our fork, we aim to build fully native, fully static binaries for RISC-V64 without a single dependency, even on operating system. But it is still possible to run compiled binaries in user-mode QEMU or in the native RISC-V64 Linux.

However, the main mode is running compiled binaries inside zkVMs.

## Supported zkVMs

Currently, we support two main flavours. 

 - **riscv64** + [zisk](https://github.com/0xPolygonHermez/zisk). These binaries can be run natively inside Zisk. For that, please invoke bflat with `--os linux`, `--libc zisk`.
 - **riscv64** + **zisk_sim**. These binaries can be run in user-mode QEMU or in the native RISC-V64 Linux, however, they carry almost all modules and workaround used for Zisk except, of course, support for **precompiles**. Please invoke bflat with `--os linux`, `--libc zisk_sim`.

## Design choices

### NB

zkVMs offer a limited subset of instructions. They typically don't even provide full riscv64 computing environment, so often they cannot run Linux (although, some may support it). Still, the agreed target doesn't include compressed instruction and floating point support. This is a significant limitation that drives our design choices.

### Compiler and ABI

Typical Linux riscv64 toolchain based on GCC with ABI limitation is used. Note that we use lp64d ABI. This is not typical as zkVMs don't support floating point computations, however, this enhances compatibility with existing toolchains and libraries.

### Runtime

bflat uses a [custom runtime](https://github.com/NethermindEth/dotnet-riscv) based on musl. There are [many patches](https://github.com/NethermindEth/dotnet-riscv/tree/main/patches/bflat-runtime) that help get rid of instructions and dependencies that are not supported in zkVMs. Please refer to instructions in the runtime repository for more information how to build it.

The built runtime is put as a release there and downloaded automatically by the bflat build process.

### Operating system

We use Linux as the target operating system. The main reason is that Linux has the vast number of libraries, and skipping all of them, or even just framing them as alien libraries, is quite meaningless. Instead, we rely on the standard Linux toolchain and libraries, yet we base out code on [musl](https://git.musl-libc.org/cgit/musl) instead of glibc and we phase out significant number of libraries through introduction of main function wrappers.

The distribution of our choice is Alpine Linux. Please refer to our [Alpine Linux repository](https://github.com/NethermindEth/riscv-alpine-build) for more information on how to build and use it.

### Patches

We decided not to replace individual modules altogether, but rather patch them at link-time. This allows us to keep the original source code intact and only modify the build process, resulting in a smaller and more efficient build process.

#### Modules
We provide the following modules:

| Module | Description |
|--------|-------------|
| nofp   | Remove floating point functions if they exist |
| pal    | Replace operating system calls with no-op stubs |
| rhp    | Patch internal dotnet functions for more compatibility |
| rhp_native | Several assembly-based patches to riscv64 functions |
| rng_stupid | Simple implementation of random number generator |
| rust_sys | Trivial Rust compatibility layer |
| security-stub | Stubs for security-related functions in .NET runtime |
| stdcppshim | Replacements for C++ allocators |
| tls | Simple TLS implementation not relying on ELF binary format |
| ubootstrap | Bootstrap re-implementation for riscv64 |
| ugc-zero | Module acting as a wrapper for Garbage Collection |
| zkvm_zisk | Entrypoint and linker scripts for Zisk |
| zkvm_zisk_sim | Entrypoint and linker scripts for Zisk simulator |

These modules are loaded automatically based on target `libc`, `arch` and `os`.

### Postprocessing

Zisk linking includes additional postprocessing steps to generate the final binary. These steps effectively do the following operations:

 - Remove EH information.
 - Get rid of data inside the `.text` section. It is needed because Zisk doesn't support putting data alongside the code, as many riscv64 compilers do. The postprocessing script creates a shadow `.text_overlay` section. In main `.text` section that remains executable, we nullify all gaps between functions. In `.text_overlay` that is kept read-only, we place the original code + data. That means Zisk is able to read these gaps, yet the instruction preprocessor sees only no-op instructions in the code section.

 Note that postprocessing is done through the disassembly (over objdump), comparing EH data and symbol table. Depending on the source of the input module, precision of either of these bits may be lost, therefore, the effort is made on unifying these three sources. Disassembly is the last resort as it is always the most accurate source of information.
 
 ### External libraries
 
 Bflat is able to link against NuGet packages, for example, [bflat-libziskos](https://github.com/NethermindEth/bflat-libziskos). This is quite useful for integrating bflat with existing .NET projects.
 
 These packages have to be reflected in the bflat command line using `--extlib <path>:<version>` parameter, where `<path>` can be either a link to GitHub releases (in that case, bflat will look for the `nupkg` file among attachments), link to nupkg itself, or a local path to the nupkg file.
 
 All NuGet packages have to have `*.bflat.manifest` files in their zip root:
 
 ```json
 {
   "name": "libziskos",
   "package_version": "1.0.0",
   "builds": [
     {
       "arch": "riscv64",
       "os": "linux",
       "libc": "zisk",
       "static_lib": "runtimes/linux-riscv64/native/libziskos.a",
       "dotnet_lib": "lib/net10.0/Nethermind.ZiskBindings.dll",
       "dotnet_assemblyname": "Nethermind.ZiskBindings"
     }
   ]
 }
 ```
 The matching is done by the content of `builds` array (`arch`, `os`, `libc` must match target platform in bflat). If the match is found, the `static_lib` is linked to the target binary, and the `dotnet_lib` is compiled and added to the target binary. Their paths are relative to bflat manifest path.
 
## Known issues

bflat doesn't support Generic Virtual Method dispatch properly, which limits how you can write your C# code.

## License

Nethermind bflat follows original GNU Affero GPL v3 license that was used for the original bflat.

## Contributing

Contributions are welcome! Please open an issue or a pull request on GitHub.
