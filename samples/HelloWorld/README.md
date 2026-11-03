# Hello world sample

A basic hello world. With an argument, it prints `Hello <argument>!`.

Build it as a zkVM guest for QEMU (the `zisk_sim` platform keeps the stdio of
the host), and run it:

```console
$ bflat build hello.cs --arch riscv64 --os linux --libc zisk_sim --no-pie --no-pthread \
    --extlib https://github.com/NethermindEth/bflat-libziskos:v1.4.0-alpha.153
$ qemu-riscv64 ./hello
Hello world!
```

A guest has no command line: its entry point gives `argv = {"app"}`, so
`args` is empty.

As an ordinary x64 Linux program:

```console
$ bflat build hello.cs --arch x64 --os linux
$ ./hello you
Hello you!
```
