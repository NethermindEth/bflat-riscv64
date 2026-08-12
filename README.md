# Bflat

[![Build RISC-V64](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml/badge.svg)](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml)
[![ZK Tests](https://zk-testing.nethermind.dev/api/v2/projects/1/badge)](https://zk-testing.nethermind.dev/v2/dashboard?search=&project=1)

Nethermind's Bflat turns C# into fully static RISC-V64 binaries that run
inside zkVMs. It is a fork of [bflat](https://github.com/bflattened/bflat) by
[MichalStrehovsky](https://github.com/MichalStrehovsky) that produces binaries
with no dependency on a host operating system, and it builds
[StatelessExecutor](https://github.com/NethermindEth/nethermind) for RISC-V64.
The same binaries run under user-mode QEMU and on RISC-V64 Linux, which is
what makes them debuggable.

Bflat is a **compiler driver**, not a compiler: it contains no code generator
of its own. It drives Microsoft's toolchain end to end and adapts the result
for a target that toolchain does not know about:

| Stage | Who does the work | What Bflat adds |
|-------|-------------------|-----------------|
| C# → IL | Roslyn (`Microsoft.CodeAnalysis.CSharp`) | in-process invocation, no project system |
| IL → native | Microsoft's ILCompiler (NativeAOT) | zkVM substitutions and C# snippets, target/ABI selection |
| link | lld | module injection, `--wrap` redirection, linker scripts |
| post-link | — | ELF postprocessing for the zkVM loader |

## Design principle: stock .NET, no patches

Every adaptation a zkVM target needs is applied *around* the runtime rather
than inside it, so the toolchain tracks upstream .NET instead of forking it.
An adaptation belongs to the earliest layer that can express it:

1. **ILC stage — substitutions and snippets.** Managed method bodies are
   replaced through an ILLink substitutions file
   (`modules/zisk_subst/zisk.substitutions.xml`) and whole-body C# snippets
   (`zisk.snippets.cs`) compiled against the guest's own reference set. This
   is how floating point leaves CoreLib paths that would otherwise emit F/D
   instructions on an FPU-less target.
2. **Link stage — modules and `--wrap`.** Native objects are injected into
   the link and existing symbols are redirected to them (see
   [Modules](#modules)). Nothing in the runtime source changes; the linker
   resolves a call somewhere else.
3. **Post-link — ELF postprocessing.** Section attributes are fixed up for
   the Zisk loader.

What remains inside .NET is a per-version *fixup* set organised in profiles:
`minimal` carries riscv64 code-generation correctness fixes and nothing else
— four per .NET line, each meant for upstream — while an optional `perf`
profile adds code-quality work on top.

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

zkVMs offer a limited subset of instructions and generally cannot run Linux.
The target excludes compressed instructions and floating point — the
limitation that drives everything below.

### Toolchain and ABI

A GCC-based Linux riscv64 cross toolchain is used. Compiled and linked code
targets `rv64ima` with the soft-float `lp64` ABI: modules are built
`-mabi=lp64`, and floating point is removed by construction (the `nofp`
module and the ILC substitutions) rather than left to the ABI to police.
C runtime objects from the distribution feed are built for `rv64gc` and
still advertise a double-float ABI, so Bflat normalizes the ELF ABI markers
at build time to let `ld.lld` mix them.

### Runtime

Bflat uses a [custom runtime build](https://github.com/NethermindEth/dotnet-riscv)
based on musl, produced from the upstream .NET VMR with the fixup set
described above. It is published as a release and downloaded automatically
by the Bflat build.

### Operating system

Linux is the target: its library ecosystem is too large to treat as alien.
The standard Linux toolchain is used with [musl](https://git.musl-libc.org/cgit/musl)
instead of glibc, and a significant number of libraries are phased out
through entry-point wrappers. The musl side comes from an Alpine cross
rootfs built by upstream .NET's own `eng/common/cross/build-rootfs.sh`;
musl itself is rebuilt from the same aport for `rv64im`, overwriting the
stock `libc.a` and `crt` objects.

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
| zkvm_zisk | Entry point and linker script for Zisk |
| zkvm_zisk_sim | Entry point and linker script for the Zisk simulator |

Every function in the C modules carries a Frama-C (ACSL) contract, and the
C, C++ and assembly modules are unit-tested on the real ISA under qemu. The
allocator is fuzzed and its bounds properties machine-checked with CBMC;
`ugc-zero` is prebuilt and tested in its own repository. See
[BUILDING.md](BUILDING.md).

### Postprocessing

Zisk linking includes additional postprocessing: section attributes are
fixed up for the Zisk loader and regions the prover would otherwise account
for are trimmed.

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

## License

Nethermind Bflat follows the original GNU Affero GPL v3 license used by the
original bflat.

## Contributing

Contributions are welcome! Please open an issue or a pull request on GitHub.
