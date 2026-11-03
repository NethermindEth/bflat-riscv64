# ZeroLib minimal runtime library

ZeroLib is the minimal standard library of upstream bflat
(`--stdlib zero`). It has no garbage collector, no exception handling and no
useful collection types.

Two rules apply to its code:

1. Do not add a public API that .NET does not have. Code that compiles
   against zerolib must also compile against .NET.
2. Do not add an API that allocates and does not return the allocation as
   its result. There is no GC, so each such allocation is a leak.

In this fork, `./build.sh layouts riscv64` compiles zerolib for linux
riscv64 only (`lib/linux/riscv64/zerolib.dll` in the layout). The other
zerolib builds of upstream (x64, arm64, Windows, UEFI) are off in
`src/bflat/bflat.csproj`. The `bflat` that `./build.sh bflat riscv64` makes,
and the Docker image, do not have zerolib. CI does not test it.

To work on it, add a file with a `Main` to this directory and run
`bflat build --stdlib none` here.
