# Sokol graphics and audio samples

These samples are from upstream bflat. They use the
[Sokol](https://github.com/floooh/sokol) 3D and audio APIs through the C#
bindings at https://github.com/MichalStrehovsky/sokol-csharp.

**This fork does not build them.** Upstream bflat builds them as Windows
programs (`--target:winexe`) with the Sokol libraries in its layout. This
fork has no Windows target and no Sokol libraries, and a zkVM guest has no
graphics or audio. The sources stay here only as a reference to upstream.
