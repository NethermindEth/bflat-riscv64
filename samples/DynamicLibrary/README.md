# Shared library sample

This sample is from upstream bflat. Only its linux-x64 form works in this
fork: a zkVM guest is one static image and cannot load a library, and the
Windows targets are not built.

To build the shared library:

```console
$ bflat build library.cs --arch x64 --os linux
```

bflat makes a library because the source has no `Main`. You can also give
`--target`. The result is `library.so`.

To build a program that loads the library with `dlopen` and `dlsym`, and
calls it:

```console
$ bflat build libraryconsumer.cs --arch x64 --os linux
$ ./libraryconsumer
```

To bind to the library statically, give `-i:library`:

```console
$ bflat build libraryconsumer.cs --arch x64 --os linux -i:library
```

The link then fails, because the linker does not know where the symbol is.
Give the library to the linker with `--ldflags`.
