# Interface dispatch sample

A small guest that calls an interface method through `as` casts on two
classes. `Main` returns the sum of the two results (11) as the exit code. It
prints nothing, so it works on each zkVM platform, also on the platforms
without a console.

```console
$ bflat build hello8.cs --arch riscv64 --os linux --libc zisk_sim --no-pie --no-pthread \
    --extlib https://github.com/NethermindEth/bflat-libziskos:v1.4.0-alpha.153
$ qemu-riscv64 ./hello8; echo $?
11
```

For ZisK, use `--libc zisk` and run it with `ziskemu -e ./hello8`.
