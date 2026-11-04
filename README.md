# Bflat

[![Build RISC-V64](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml/badge.svg)](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml)
[![ZK Tests](https://zk-testing.nethermind.dev/api/v2/projects/1/badge)](https://zk-testing.nethermind.dev/v2/dashboard?search=&project=1)

Nethermind's Bflat compiles C# into fully static RISC-V binaries that run
inside zkVMs. It is a fork of [bflat](https://github.com/bflattened/bflat) by
[MichalStrehovsky](https://github.com/MichalStrehovsky). A guest needs no
operating system, no dynamic libraries and no file system. Bflat builds the
stateless executor of [Nethermind](https://github.com/NethermindEth/nethermind)
for each zkVM below.

Bflat is a **compiler driver**. It has no code generator of its own. It
drives the .NET toolchain of dotnet-riscv and adapts the result for a target that the
toolchain does not know:

| Stage | Who does the work | What Bflat adds |
|-------|-------------------|-----------------|
| C# → IL | Roslyn | in-process compile, no project system |
| IL → native | the .NET ILCompiler (NativeAOT) and RyuJIT | substitutions, feature switches, the ISA and the RyuJIT options of the platform |
| link | lld | the link-time modules, `--wrap` redirection, the linker script of the platform |
| post-link | `patch_elf.py`, `flat_image` | the changes to the image that the zkVM loader needs |

The documentation site is in [`docs/`](docs/README.md):
[Targets](docs/targets.md) ·
[Architecture](docs/architecture.md) ·
[Modules](docs/modules.md) ·
[Manifest format](docs/manifest.md) ·
[Runtime](docs/runtime.md) ·
[Package signatures](docs/signing.md) ·
[Build & use](docs/build.md) ·
[Verification](docs/verification.md).

## Targets

Bflat itself knows no zkVM. Each zkVM target is a **platform** that a bflat-*
package defines. The platform gives the entry point, the memory map, the ISA,
the ABI, the RyuJIT options and the post-link steps (see the
[manifest format](docs/manifest.md#platform)). `--libc` selects the platform
by its alias, and `--extlib` gives the package.

| Platform | `--arch` / `--libc` | Package | ISA of the guest | Runs on |
|----------|---------------------|---------|------------------|---------|
| [zisk](docs/targets.md#zisk) | `riscv64` / `zisk` | [bflat-libziskos](https://github.com/NethermindEth/bflat-libziskos) (`Nethermind.ZiskOS.Runtime`) | rv64im, plus Zba, Zbb and Zbs | [ZisK](https://github.com/0xPolygonHermez/zisk) (`ziskemu`, the prover) |
| [zisk_sim](docs/targets.md#zisk_sim) | `riscv64` / `zisk_sim` | bflat-libziskos | rv64im | `qemu-riscv64`, RISC-V64 Linux |
| [sp1](docs/targets.md#sp1) | `riscv64` / `sp1` | [bflat-sp1](https://github.com/NethermindEth/bflat-sp1) (`Nethermind.Sp1.Runtime`) | rv64im, aligned accesses only | [SP1](https://github.com/succinctlabs/sp1) v6 |
| [openvm](docs/targets.md#openvm) | `riscv64` / `openvm` | [bflat-openvm](https://github.com/NethermindEth/bflat-openvm) (`Nethermind.OpenVM.Runtime`) | rv64im | [OpenVM](https://github.com/openvm-org/openvm), RV64 line |
| [airbender](docs/targets.md#airbender) | `riscv32` / `airbender` | [bflat-airbender](https://github.com/NethermindEth/bflat-airbender) | rv32im, ilp32 | [ZKsync Airbender](https://github.com/matter-labs/zksync-airbender) v3 |

No guest has compressed (C), atomic (A) or floating-point (F, D)
instructions. Floating point is soft-float. Zba, Zbb and Zbs are on for zisk
only, because the ZisK transpiler decodes them.

Bflat also keeps one ordinary target: [linux-x64](docs/targets.md#linux-x64)
(`--arch x64 --os linux`), a dynamically linked glibc program with the full
runtime and no zkVM module.

Nethermind's stateless guest runs the mainnet blocks of its
`stateless-tests` suite on ZisK, SP1 and OpenVM with the same output.

## Quick start

Use the published image. It has bflat, the .NET 11 SDK and lld:

```console
$ docker run --rm --platform linux/amd64 -v "$PWD:/w" -w /w nethermindeth/bflat-riscv64-11 \
    bflat build hello.cs --arch riscv64 --os linux --libc zisk_sim --no-pie --no-pthread \
    --extlib https://github.com/NethermindEth/bflat-libziskos:v1.4.0-alpha.153
$ qemu-riscv64 ./hello
```

`--extlib <repo>:<tag>` downloads the `.nupkg` of that GitHub release. The
package of bflat-libziskos has the `zisk` and `zisk_sim` platforms. Change
`--libc zisk_sim` to `--libc zisk` to build for ZisK, and run the result with
`ziskemu -e ./hello`. The image for the riscv32 target is
`nethermindeth/bflat-riscv64-11-riscv32`.

To build bflat from source, build the modules first, then bflat:

```console
$ ./build.sh modules riscv64
$ ./build.sh bflat riscv64
```

[BUILDING.md](BUILDING.md) has the prerequisites, the riscv32 target, the
Docker scripts and the checks.

## Design principle: the earliest layer that can make the change

Bflat makes as many adaptations as it can outside the runtime. An
adaptation goes into the earliest layer that can express it:

1. **ILC stage: substitutions and feature switches.** The `guest_subst`
   module gives ILC an ILLink substitutions file and the invariant feature
   switches. They remove CoreLib work that has no meaning on one hart.
2. **RyuJIT options.** The platform disables the ISA extensions that the zkVM
   does not decode, and gives the code-quality options (see
   [Architecture](docs/architecture.md#zkvm-ryujit-codegen-knobs)).
3. **Link stage: modules and `--wrap`.** Native objects go into the link, and
   `--wrap` sends existing symbols to them. The runtime source does not
   change. The linker resolves a call to a different function.
4. **Post-link.** `patch_elf.py` and `flat_image` change the linked image for
   the loader of the zkVM.

The changes that no layer above can make go into the runtime itself.
[dotnet-riscv](https://github.com/NethermindEth/dotnet-riscv) builds the
.NET 11 VMR with its own patch series: 22 patches for the riscv64 base ISA,
soft-float and the lp64 ABI, and 67 patches that change RyuJIT, ILC and the
NativeAOT runtime for zkVM guests. The riscv32 target adds 7 patches more.
Thus bflat uses its own build of .NET, and Microsoft's runtime packages do
not work with it (see [Runtime](docs/runtime.md)). The release that bflat
bundles is in `src/bflat/bflat.variant.props`. Bflat checks its signature
(see [Package signatures](docs/signing.md)).

## Modules

A module is a native object that bflat adds to the guest link. `--wrap`
sends existing symbols to it. Its `bflat-manifest.json`
([format](docs/manifest.md)) declares its sources, objects, wraps and the
conditions for each. A platform lists the modules it requires. A module
applies only when its conditions hold. For example, `noos` and `nothread`
apply only on platforms with the `noos` and `no-atomics` flags (sp1, openvm,
airbender).

| Module | What it does |
|--------|--------------|
| eh | Gives synthetic ELF program headers, so the unwinder of the runtime finds its tables in an image that has no loader |
| gs_cookie | Sets the stack-protector cookie to a constant. A zkVM has no entropy |
| guest_subst | The ILC-stage substitutions and the invariant feature switches. It has no native object |
| nofp | Traps the transcendental libm functions (`sin`, `exp`, `log`, …), so a guest that calls one fails loudly. The exact operations (`sqrt`, `floor`, `fma`, …) run |
| noos | Defines the OS surface that the Unix PAL of the runtime calls, before musl (platforms with the `noos` flag) |
| nothread | Single-hart replacements for the locks and thread primitives of musl (platforms with the `no-atomics` flag) |
| pal | Deterministic stubs for operating-system calls, the bump allocator, a small `vfprintf`, and the exit through the platform |
| rhp | Link-time replacements for runtime helpers: P/Invoke frames, the allocation fast paths, fail-fast, the throw path under `--remove-eh`, the console on platforms without one |
| rhp_native | Assembly GC write barriers. uGC never collects, so each barrier is a plain store |
| rng_stupid | A deterministic pseudo-random generator. A proof must be reproducible |
| rust_sys | Connects the allocator hook of the zkVM Rust runtimes (`sys_alloc_aligned`) to the bump heap |
| stdcppshim | The C++ runtime parts that the NativeAOT runtime needs |
| tls | A small TLS start-up that does not use the ELF machinery of musl |
| ubootstrap | The .NET runtime bootstrap for a guest without an OS |
| ugc-zero | [uGC](https://github.com/NethermindEth/uGC), a GC that never collects, in place of the built-in GC |

Each module builds for riscv64 (`module.o`) and for riscv32
(`module.rv32.o`). Each function in the C modules has a Frama-C (ACSL)
contract. The C, C++ and assembly modules have unit tests that run on the
real ISA under qemu. The allocator is fuzzed, and CBMC proves its bounds.
See [Modules](docs/modules.md) and [Verification](docs/verification.md).

## External libraries

`--extlib` adds a package to the build. It accepts:

- `https://github.com/<owner>/<repo>:<tag>`: a GitHub release with one
  `.nupkg`;
- a path or URL to a `.nupkg`;
- a path or URL to a `.bflat.manifest`. Bflat then reads each
  `*.bflat.manifest` in that directory.

A package has one or more `*.bflat.manifest` files in the
[schema 2 format](docs/manifest.md). A manifest can define a platform, or
add libraries, wraps and ILC references when its `when` conditions hold. A
`.nupkg` must have a valid signature from a key that bflat trusts
(see [Package signatures](docs/signing.md)).

## License

Nethermind Bflat uses the GNU Affero GPL v3 license of the original bflat.

## Contributing

Contributions are welcome. Open an issue or a pull request on GitHub. Each
pull request from this repository also runs the
[bflat-ts](https://github.com/NethermindEth/bflat-ts) suite on zk-testing
with the image of the pull request (see
[Verification](docs/verification.md)).
