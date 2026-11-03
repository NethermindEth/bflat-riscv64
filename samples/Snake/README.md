# Snake sample

A snake game for the console. The sample is from upstream bflat. It can
build with the .NET standard library or with zerolib, the minimal standard
library (see [`src/zerolib`](../../src/zerolib/README.md)).

In this fork, build it as an x64 Linux program and play it in a terminal:

```console
$ bflat build --arch x64 --os linux
$ ./Snake
```

The game needs a terminal and a keyboard, so it does not run as a zkVM
guest. This fork does not build the Windows and UEFI targets of upstream
bflat, and the zerolib build is not available on linux-x64 (see
[`src/zerolib`](../../src/zerolib/README.md)).
