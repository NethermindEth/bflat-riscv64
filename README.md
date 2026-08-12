# Bflat

[![Build RISC-V64](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml/badge.svg)](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml)
[![ZK Tests](https://zk-testing.nethermind.dev/api/v2/projects/1/badge)](https://zk-testing.nethermind.dev/v2/dashboard?search=&project=1)

Nethermind's Bflat turns C# into fully static RISC-V64 binaries that run
inside zkVMs. It is a fork of [bflat](https://github.com/bflattened/bflat) by
[MichalStrehovsky](https://github.com/MichalStrehovsky), and it is used to
build [StatelessExecutor](https://github.com/NethermindEth/nethermind) for
RISC-V64.

Bflat is a **compiler driver**, not a compiler: it contains no code generator
of its own. What it does is drive Microsoft's toolchain end to end and adapt
the result to a target that toolchain does not know about:

| Stage | Who does the work | What Bflat adds |
|-------|-------------------|-----------------|
| C# → IL | Roslyn (`Microsoft.CodeAnalysis.CSharp`) | in-process invocation, no project system |
| IL → native | Microsoft's ILCompiler (NativeAOT) | zkVM substitutions and C# snippets, target/ABI selection |
| link | lld | module injection, `--wrap` redirection, linker scripts |
| post-link | — | ELF postprocessing for the zkVM loader |

## Design principle: stock .NET, no patches

**The main aim is to keep .NET free of patches.** Every adaptation that a
zkVM target needs is applied *around* the runtime rather than inside it, so
the toolchain tracks upstream .NET instead of forking it. Concretely, an
adaptation belongs to the earliest of these layers that can express it:

1. **ILC stage — substitutions and snippets.** Managed method bodies are
   replaced through an ILLink substitutions file
   (`modules/zisk_subst/zisk.substitutions.xml`) and whole-body C# snippets
   (`zisk.snippets.cs`) compiled against the guest's own reference set. This
   is how floating point is eliminated from CoreLib paths that would
   otherwise emit F/D instructions on an FPU-less target.
2. **Link stage — modules and `--wrap`.** Native objects are injected into
   the link and existing symbols are redirected to them (see
   [Modules](#modules)). Nothing in the runtime source changes; the linker
   simply resolves a call somewhere else.
3. **Post-link — ELF postprocessing.** Section attributes are fixed up for
   the Zisk loader.

What remains inside .NET is deliberately small and tracked. The
[runtime repository](https://github.com/NethermindEth/dotnet-riscv) is moving
from an open-ended patch queue to a per-version *fixup* set organised in
profiles: `minimal` carries correctness fixes for riscv64 code generation and
nothing else, while an optional `perf` profile adds code-quality work on top.
The minimal profile is currently four fixups for each of .NET 10 and .NET 11.

Four upstreamable fixups per .NET line, carried for one reason only: the
zkVM target. Nothing else justifies touching .NET.

## Motivation

[Original bflat](https://github.com/bflattened/bflat) builds only dynamically
linked binaries. This fork produces fully static ones with no dependency on a
host operating system at all — which is what a zkVM guest has to be. The same
binaries also run under user-mode QEMU or on native RISC-V64 Linux, which is
what makes them debuggable.

## Supported zkVMs

Two flavours, selected by `--libc`:

- **riscv64 + [zisk](https://github.com/0xPolygonHermez/zisk)** — runs natively
  inside Zisk. Invoke with `--os linux --arch riscv64 --libc zisk`.
- **riscv64 + zisk_sim** — runs under user-mode QEMU or native RISC-V64 Linux.
  Carries almost every module and adaptation Zisk needs, except support for
  **precompiles**. Invoke with `--os linux --arch riscv64 --libc zisk_sim`.

Bflat itself builds against .NET 10 or .NET 11; see
[BUILDING.md](BUILDING.md) for the version and variant matrix.

## Design choices

### Target ISA

zkVMs offer a limited subset of instructions. They typically do not provide a
full riscv64 computing environment, so most cannot run Linux. The agreed
target excludes compressed instructions and floating point — a limitation
that drives everything below.

### Toolchain and ABI

A typical GCC-based Linux riscv64 cross toolchain is used. The code Bflat
compiles and links targets `rv64ima` with the soft-float `lp64` ABI — the
modules are built `-mabi=lp64`, and floating point is removed by
construction (see the `nofp` module and the ILC substitutions) rather than
left to the ABI to police.

Not every input agrees yet: the C runtime objects from the distribution feed
are built for `rv64gc` and still advertise a double-float ABI, so Bflat
normalizes the ELF ABI markers at build time to let `ld.lld` mix them. That
is a workaround; rebuilding those artifacts for the target ISA is the real
fix, and it is in progress.

### Runtime

Bflat uses a [custom runtime build](https://github.com/NethermindEth/dotnet-riscv)
based on musl, produced from the upstream .NET VMR with the small fixup set
described above. The build is published as a release and downloaded
automatically by the Bflat build; see the runtime repository for how to
produce one.

### Operating system

Linux is the target operating system. The reason is pragmatic: Linux has an
enormous library ecosystem, and treating all of it as alien would throw away
more than it buys. So the standard Linux toolchain and libraries are used,
based on [musl](https://git.musl-libc.org/cgit/musl) instead of glibc, with a
significant number of libraries phased out through entry-point wrappers.

The musl side comes from an Alpine cross rootfs built by upstream .NET's
own `eng/common/cross/build-rootfs.sh`; there is no rebuilt distribution of
our own. The one exception is musl itself: Alpine's feed builds it for
`rv64gc`, so it is rebuilt from the same aport for `rv64im` and overwrites
the stock `libc.a` and `crt` objects in the rootfs.

### Modules

A module is a native object (plus optional linker script and parameters)
injected into the guest link, with existing symbols redirected to it via
`--wrap`. Modules are selected automatically from the target `libc`, `arch`
and `os`.

| Module | Description |
|--------|-------------|
| eh | Synthetic ELF program headers, so the runtime's unwinder can find its tables in an image with no loader |
| gs_cookie | Pins the stack-cookie symbol to a constant (a zkVM has no entropy and no page protection) |
| nofp | Traps soft-float builtins and the libm surface, so stray floating point fails loudly instead of returning garbage |
| pal | Replaces operating-system calls with deterministic stubs; hosts the bump allocator and an FP-free `vfprintf` |
| rhp | Link-time replacements for internal .NET runtime functions (write barriers, fail-fast, FP-carrying collection helpers) |
| rhp_native | Assembly replacements for riscv64 runtime helpers (GC reference-assignment barriers) |
| rng_stupid | Deterministic pseudo-random generator — a zkVM guest must be reproducible |
| rust_sys | Trivial Rust compatibility layer |
| security-stub | Stubs for the GSSAPI surface of the .NET runtime |
| stdcppshim | Replacements for C++ allocators |
| tls | Simple TLS implementation that does not rely on the ELF format |
| ubootstrap | Bootstrap re-implementation for riscv64 |
| ugc-zero | Wrapper module for garbage collection |
| zisk_subst | ILC-stage substitutions and C# snippets (no native object) |
| zkvm_zisk | Entry point, snapshot-restore trampoline and linker script for Zisk |
| zkvm_zisk_sim | Entry point and linker script for the Zisk simulator |

Every function in the C modules carries a Frama-C (ACSL) contract, and the
C, C++ and assembly modules are covered by unit tests that execute on the
real ISA under qemu. The allocator is additionally fuzzed, and its bounds
properties are machine-checked with CBMC. See [BUILDING.md](BUILDING.md).
(`ugc-zero` is prebuilt elsewhere and is tested in its own repository.)

### Postprocessing

Zisk linking includes additional postprocessing to prepare the final binary:
section attributes are fixed up for the Zisk loader and regions the prover
would otherwise account for are trimmed.

### External libraries

Bflat can link against NuGet packages, for example
[bflat-libziskos](https://github.com/NethermindEth/bflat-libziskos). This is
how existing .NET projects integrate with Bflat.

Packages are named on the command line with
`--extlib <path>:<version>`, where `<path>` is a link to a GitHub release (in
which case Bflat looks for the `nupkg` among the attachments), a link to the
nupkg itself, or a local path to it.

Every such package must carry a `*.bflat.manifest` file in its zip root:

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

Matching is driven by the `builds` array: `arch`, `os` and `libc` must match
the target platform. On a match, `static_lib` is linked into the binary and
`dotnet_lib` is compiled into it. Both paths are relative to the manifest.

## Building

See [BUILDING.md](BUILDING.md) for the build, the .NET version and variant
matrix, and the test, fuzzing and verification workflows.

Note that `compiler` appears in the names of published artifacts
(`bflat.compiler.*.nupkg`, `bflat-compiler-native-*.zip`) and in the MSBuild
targets that fetch them. Those are historical artifact names on the release
side, not a claim about what this repository builds.

## License

Nethermind Bflat follows the original GNU Affero GPL v3 license used by the
original bflat.

## Contributing

Contributions are welcome! Please open an issue or a pull request on GitHub.
