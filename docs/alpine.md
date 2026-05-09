---
layout: default
title: Alpine — riscv-alpine-build
eyebrow: The base distribution
lead: >
  A small project that builds the entire Alpine Linux distribution from
  source under the same constraint set our zkVM target requires —
  no compressed instructions, no floating-point instructions emitted —
  while keeping the <code>lp64d</code> ABI for toolchain compatibility.
  It's the rootfs every downstream piece is compiled against.
prev: /
next: /runtime/
---

## What it is

[`NethermindEth/riscv-alpine-build`](https://github.com/NethermindEth/riscv-alpine-build)
takes upstream [`aports`](https://git.alpinelinux.org/aports) at a pinned
commit, applies a focused patch series, and runs Alpine's own
`scripts/bootstrap.sh` to cross-build a complete Alpine Linux for
RISC-V64. The compiler is reconfigured so that no compressed and no
floating-point instructions land in the output, while the ABI stays at
<code>lp64d</code> — the same ABI bflat-emitted user binaries use, so
that everything across the stack (system libs, the .NET runtime, user
code) shares one calling convention.

The output is a working RISC-V64 root filesystem where every binary —
system compiler, libc, kernel headers, system libraries — is free of
the instruction classes Zisk's prover cannot decode.

The repository is intentionally minimal:

```
riscv-alpine-build/
├── Dockerfile          # alpinelinux/build-base + abuild key setup
├── docker.sh           # build the image and drop into a shell
├── dl_aports.sh        # clone aports@06716ff9, apply patches.patch
├── patches.patch       # the entire change set (~320 lines)
├── aports/             # checked-out + patched aports tree (after dl)
└── README.md
```

The whole thing fits in one source patch and three shell scripts.

## Why a custom Alpine?

Stock Alpine for `riscv64` ships with the GCC toolchain configured for
the full `rv64gc` ISA — including compressed (`C`) and floating-point
(`F`/`D`) instructions. That default propagates everywhere: every
package built from `aports` inherits it, producing binaries that use
both classes of instruction.

zkVMs (Zisk in particular) accept neither. We can't simply tell our
*own* binaries to skip them — the system libraries they link against
would still pull them back in. The only way to be sure nothing in the
final RISC-V64 image uses an unsupported instruction is to rebuild
Alpine itself under the constrained subset.

The patch reconfigures GCC so that the system compiler:

- targets a RISC-V64 ISA without compressed or floating-point
  instructions in the emitted code, and
- keeps the `lp64d` ABI for the calling convention.

The two choices are deliberate. Compression and FP are dropped because
Zisk can't prove them. The `lp64d` ABI is kept because it's what
bflat's user binaries also use — having one ABI across the whole stack
means system libraries, the patched .NET runtime, and user code can all
interoperate with no mismatched calling conventions. The fact that
`lp64d` *would* normally pass doubles in FP registers is fine in
practice: code paths that actually use doubles never get reached in our
workloads, and the dotnet-riscv runtime additionally strips FP
instruction emission from the JIT itself.

The remaining APKBUILDs in `patches.patch` exist to fix packages that
broke under the constrained subset, not to add new features.

## What `patches.patch` touches

| File | What changes |
|------|--------------|
| `main/gcc/APKBUILD` | Reconfigures the `riscv64` GCC build to drop compressed and floating-point instruction emission while keeping the `lp64d` ABI (the load-bearing change — every later cross-build inherits this). |
| `main/icu/APKBUILD` | Build flag tweaks for ICU under the constrained ISA. |
| `main/llvm-runtimes/APKBUILD` | Threads explicit `--target` and architecture flags through CFLAGS / CXXFLAGS / ASMFLAGS so the LLVM runtimes (compiler-rt / libunwind / libcxx) inherit the same constraints when cross-compiled. |
| `main/llvm-runtimes/xf_float.patch` *(new)* | Inline patch added to LLVM runtimes — strips a floating-point dependency. |
| `main/libunwind/APKBUILD` | Tweaks libunwind for RISC-V64. |
| `main/libunwind/Riscv.patch` *(new)* | Inline patch making libunwind work without the dropped instructions. |
| `main/openssl/APKBUILD` | Build flag fixes for OpenSSL under the constrained ISA. |
| `main/python3/APKBUILD` | Same — Python's build system needs nudging. |
| `main/e2fsprogs/APKBUILD` | Same — for the userland filesystem tools that go into the image. |
| `main/lttng-ust/APKBUILD` | Same — required because some downstream packages pull it in. |
| `scripts/bootstrap.sh` | Extends Alpine's bootstrap script to also cross-build `icu` (it isn't part of the upstream bootstrap set, but we need it for downstream Nethermind builds). |

That's the whole patch. Less than 320 lines covers a system-wide
rebuild for a different ISA subset.

## Building the image

The build is encapsulated in Docker so you don't pollute your host with
Alpine's `abuild` toolchain:

```console
$ ./docker.sh           # build the image, drop into a shell
$ ./dl_aports.sh        # clone aports@06716ff9 and apply patches.patch
$ ./aports/scripts/bootstrap.sh riscv64
```

`docker.sh` pins to `linux/amd64` because the cross-build host is
intentionally x86; `bootstrap.sh` then does a stage-1 → stage-2 cross
build using GCC reconfigured per the patch above. The resulting `.apk`
packages are dropped under `~/packages/main/` inside the container and
can be assembled into a rootfs tarball via Alpine's standard tools.

## How it reaches bflat-riscv64 and dotnet-riscv

The Alpine rootfs produced here is the cross-compilation environment
for [`dotnet-riscv`](runtime.md). Specifically:

- [`00_build_rootfs.sh`](https://github.com/NethermindEth/dotnet-riscv/blob/main/00_build_rootfs.sh)
  in dotnet-riscv consumes this Alpine as its base (with help from
  `12_alpine_custom.patch` against the dotnet runtime's
  `eng/common/cross/build-rootfs.sh`).
- The `.NET` runtime libraries that result inherit the constrained
  instruction set + `lp64d` ABI from the system compiler and end up in
  dotnet-riscv release archives.
- bflat-riscv64 then statically links those archives into your final
  binary — at no point does a system library snuck in from elsewhere
  override the constrained ISA.

Without this Alpine, every downstream piece would either need its own
cross-toolchain hack (tedious, fragile) or live with floating-point
helpers leaking in from `libgcc` and friends. With it, the constraint
is enforced at the bottom of the stack and everything above just
inherits.

## License

Patches are MIT-licensed; the upstream Alpine `aports` tree is
GPL/MIT-mixed per its individual packages. See the project's
[`LICENSE.md`](https://github.com/NethermindEth/riscv-alpine-build/blob/main/LICENSE.md).
