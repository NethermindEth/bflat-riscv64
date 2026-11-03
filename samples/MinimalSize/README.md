# Minimal size sample

Shows how to make a small executable with bflat. Compare the size and the
output when you remove some of the options:

```console
$ bflat build minimalsize.cs --arch x64 --os linux --no-reflection --no-stacktrace-data \
    --no-globalization --no-exception-messages -Os --no-pie --separate-symbols
$ ./minimalsize
```

The same options apply to a zkVM guest, where each byte of the image costs
prover steps. Add `--remove-eh` there to remove the unwind tables too (a
`throw` then ends the guest):

```console
$ bflat build minimalsize.cs --arch riscv64 --os linux --libc zisk_sim --no-pie --no-pthread \
    --no-reflection --no-stacktrace-data --no-exception-messages -Os \
    --extlib https://github.com/NethermindEth/bflat-libziskos:v1.4.0-alpha.153
$ qemu-riscv64 ./minimalsize
```
