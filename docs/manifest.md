---
layout: default
title: Manifest format
eyebrow: Specification
lead: >
  One JSON format describes all the parts that bflat links into a guest in
  addition to the stock NativeAOT runtime. The format covers the modules in
  this repository, the bflat-* packages, and the platforms (zkVM targets).
---

# bflat manifest, schema 2

## Terms

| Term | Meaning |
|------|---------|
| guest | The program that bflat builds. |
| manifest | A JSON file in the format that this page describes. |
| module | A directory in `src/bflat/modules/`. A module has one manifest. |
| package | A bflat-* repository (for example bflat-sp1) and its release. A package has one or more manifests: for example a platform and the bindings of its accelerators. |
| platform | A manifest that has a `platform` object. A platform is a target that the user can select, for example SP1. |
| layout | The directory tree of an installed bflat. |
| condition | An object that tells when a manifest item applies. |

## Status

bflat reads schema 2 for the modules in this repository and for `--extlib`
packages. bflat knows no zkVM: the four zkVM targets are platforms in their
packages. bflat parses and validates all sections. A manifest that uses a
section that bflat does not act on in that kind of build causes an error.
bflat does not ignore the section.

| Section | Platform builds (platform, modules, packages) | Stock targets with `--extlib` |
|---------|-----------------------------------------------|-------------------------------|
| `sources`, `external` | modules: `bflat-modules plan`; packages: not used (the package build owns them) | not used |
| `objects` | yes: all groups | yes, but without `group` and `order` |
| `link.wrap` | yes | yes |
| `link.ld` | yes | no |
| `ilc.substitutions`, `ilc.references` | yes | references only |
| `ilc.defines`, `ilc.features`, `ilc.direct_pinvoke` | yes | no |
| `ilc.sources` | no | no |
| `codegen`, `postlink`, `checks` | yes | no |
| `tests` | validated only | not used |
| `platform` | yes | not used |

## Formats that schema 2 replaced

1. `module_params.yml` in each module. Two parsers read this file: `yq` in
   `build.sh`, and the parser `BuildCommand.ReadModuleParams`. Both are
   gone.
2. The `bflat-manifest.json` (`*.bflat.manifest`) of schema 1 in the bflat-*
   packages. It had only four fields: `static_lib`, `dotnet_lib`,
   `dotnet_assemblyname` and `wrap_symbols`. bflat continues to read it.
   Refer to [Schema 1 compatibility](#schema-1-compatibility).

## Goals

1. **A manifest declares all the parts of its module or package.** These
   parts are: sources, objects, link position, wraps, linker scripts, entry
   points, ILC inputs, codegen options, post-link steps, image checks, tests
   and external downloads. `BuildCommand.cs`, `bflat.csproj` and `build.sh`
   must not contain data about a specific module or target.
2. **bflat does not know the name of a zkVM.** The bflat-* package of a
   target (ZisK, SP1, OpenVM) contains the platform manifest of that target.
   bflat knows only the format, the generic modules and the stock NativeAOT
   targets.
3. **One parser reads the format.** The parser is `src/bflat/BflatManifest.cs`.
   bflat and the `bflat-modules` program both compile this file. The build
   scripts get data from `bflat-modules`. They do not read JSON themselves.
4. **One schema file validates the format.** Editors and CI use it.
5. **bflat continues to read schema 1.** Existing package manifests do not
   need changes.

## File names and locations

| Location | File | Reader |
|----------|------|--------|
| Module, in the source tree | `src/bflat/modules/<name>/bflat-manifest.json` | `bflat-modules` |
| Module, in the layout | `lib/modules/<name>/bflat-manifest.json`, next to the built files of the module | bflat, when it builds a guest |
| Package, in the source tree | `bflat-manifest.json` (the bindings) at the root of the repository; `platform/<name>.bflat.manifest` (the platforms) | the build of the package |
| Package, in a release or nupkg | `<name>.bflat.manifest` for each manifest, next to its files in one directory | bflat, through `--extlib` |
| Schema | `src/bflat/bflat-manifest.schema.json`; in the layout, `bflat-manifest.schema.json` at the root | editors, CI |

A relative path in a manifest starts at **the directory that contains the
manifest**. A path must use `/` as the separator. An absolute path is an
error.

`--extlib` with a local `.bflat.manifest` path reads **every**
`*.bflat.manifest` in that directory. Thus `--extlib .../libsp1.bflat.manifest`
also loads `sp1.bflat.manifest`, the platform. A `.nupkg` gives all its
manifests.

## Top level

```json
{
  "$schema": "../../bflat-manifest.schema.json",
  "schema": 2,
  "name": "pal",
  "version": "1.0.0",
  "description": "Deterministic OS surface, bump allocator, FP-free stdio",
  "when": { "arch": "riscv64", "os": "linux" },

  "sources":  [ ... ],
  "objects":  [ ... ],
  "link":     { ... },
  "ilc":      { ... },
  "codegen":  { ... },
  "postlink": [ ... ],
  "checks":   { ... },
  "tests":    [ ... ],
  "external": { ... },

  "platform": { ... }
}
```

| Key | Type | Required | Meaning |
|-----|------|----------|---------|
| `$schema` | string | no | The path or URL of the schema file. Editors use it. bflat ignores it. |
| `schema` | integer | yes | The value is `2`. bflat reads a package manifest without `schema` as schema 1. A module manifest must have `schema`. |
| `name` | string | yes | Lowercase letters, digits, `_` and `-`. The name must be unique in one guest build. The name of a module is the name of its directory. A `platform` condition uses this name. |
| `version` | string | no | For information only. It replaces `package_version` of schema 1. |
| `description` | string | no | One line. |
| `note` | string | no | Free text. bflat ignores it. |
| `metadata` | object | no | Strings that a package build adds, for example the upstream commit. bflat ignores them. |
| `when` | [condition](#conditions) | no | If the condition is false, bflat ignores the full manifest. |
| `platform` | [object](#platform) | no | This object makes the manifest a platform. |

All other keys are [sections](#sections). **bflat rejects a key that it does
not know.** Thus a typing error causes an error message. bflat does not
ignore the key. Use `metadata` for data that a build adds.

### Notes in place of comments

JSON has no comments. But the comments in `module_params.yml` explained
almost all `--wrap` entries, and this information is important. Thus each
object in a list can have a `"note"` string. bflat ignores the note.

```json
{ "symbol": "PalGetMaximumStackBounds",
  "mangled": "_Z24PalGetMaximumStackBoundsPPvS0_",
  "note": "The real one aborts under static musl on RISC-V (mremap EINVAL)." }
```

Many list items have a short form: a string. The string is the same as the
object form without `when` and without `note`.

## Conditions

A condition is an object. bflat applies the item only if **all** keys of the
condition match. The value of a key is a string or a list of strings. A list
matches if one of its items matches.

```json
"when": { "arch": "riscv64", "libc": ["musl"], "eh": "failfast" }
```

| Key | Values | Source |
|-----|--------|--------|
| `arch` | `x64`, `x86`, `arm64`, `riscv64` | `--arch`, or `base.arch` of the selected platform |
| `os` | `linux`, `windows`, `uefi` | `--os`, or `base.os` of the selected platform |
| `libc` | `glibc`, `musl`, `bionic`, `shcrt`, `none` | `--libc`, or `base.libc` of the selected platform |
| `platform` | the `name` of a platform manifest, for example `zisk` | the selected platform. A stock target has no platform, so the key never matches. |
| `flag` | flag names that platforms define | `flags` of the selected platform |
| `eh` | `unwind`, `failfast` (one value only) | `--remove-eh` |
| `stdlib` | `dotnet`, `zero`, `none` | `--stdlib` |
| `compiler` | capability names | what the compiler package of this bflat can do. Today the only capability is `zb-lowering-fixes`: the package has the widening-cast and Zb lowering fixes of dotnet-riscv#9 (the .NET 11 perf variant). |
| `optimize` | `true`, `false` | `true` if the optimization level is not `-O0`. Only `codegen.options` and `postlink` can use this key. |
| `verbose` | `true`, `false` | `true` with `-v`. Only `codegen.options` and `postlink` can use this key. |

Use `flag` to make a generic module change for a platform. The module does
not name the platform. Example: `nothread` is linked for each platform
whose decoder does not have the A extension. Such a platform sets
`"flags": ["no-atomics"]`. The object of the module sets
`"when": { "flag": "no-atomics" }`.

The modules of bflat know these flags. The platforms set them:

| Flag | Meaning | Reader | Platforms |
|------|---------|--------|-----------|
| `no-atomics` | The decoder does not have the A extension. | `nothread` | `sp1`, `openvm` |
| `noos` | No OS call can be serviced. | `noos` | `sp1`, `openvm` |
| `no-console` | No host console and no host stdio. | `rhp`, `pal` | `zisk`, `sp1`, `openvm` |

A condition on a `platform` name is permitted. But only the package that
owns the platform must use it: the bindings of bflat-libziskos, for example,
apply with `"when": { "platform": "zisk" }`. The modules of bflat do not use
it.

You can put a condition at the top level of a manifest (`when`) and on each
list item. A key that bflat does not know is an error.

## Sections

### `sources`: the files to compile

```json
"sources": [
  { "file": "heap.c" },
  { "file": "stdio.c", "cflags": ["-fno-builtin-printf"] },
  { "file": "barriers.S", "asflags": ["--defsym", "EXTRA=1"] },
  { "file": "shim.cpp", "when": { "arch": "riscv64" } }
]
```

| Key | Meaning |
|-----|---------|
| `file` | The source file. The extension sets the language: `.c`, `.cpp` or `.cc`, `.S` or `.s`. |
| `cflags`, `cxxflags`, `asflags` | Flags that the module adds after the common flags. A `.cpp` file gets `cflags` and `cxxflags`. |
| `output` | The name of the object file. The default is `<stem>.o`. For example, `heap.c` gives `heap.o`. |
| `when` | The module build compiles the source only if this condition is true. The condition can test only `arch` and `os`, because the module build does not know the guest. |

`build.sh` supplies the common flags: target triple, `-march`, `-mabi`,
LTO, `-O3` and `BFLAT_DOTNET`. A module can add flags. A module must not set
the common flags again.

Two sources must not give the same object file. Use `output` to give one of
them a different name.

### `objects`: the files to link

```json
"objects": [
  { "file": "heap.o", "group": "whole-archive", "order": 60 },
  { "file": "nothread.o", "group": "pre-libc", "when": { "flag": "no-atomics" } },
  "libziskos.a"
]
```

| Key | Default | Meaning |
|-----|---------|---------|
| `file` | — | An object file or an archive. |
| `group` | `normal` | The position in the link command. Refer to the list below. |
| `order` | `100` | The sort key in the group (0 to 999). |
| `when` | — | bflat links the file only if this condition is true. |

If a manifest does not have `objects`, bflat links all outputs of `sources`
with the default group and the default order. The entry object of a
platform is not one of these outputs: bflat links it in its own position.

In a group, bflat sorts the objects by `order`. If two objects have the same
`order`, bflat uses the load order of their manifests (refer to
[Platform](#platform)). Then bflat uses the position in the manifest.

**Groups.** bflat always writes the link command in this sequence. The
manifests fill only the parts in square brackets:

```
crt1 crti  <guest .o>  <bootstrapper>  <runtime libs>
  [pre-libc]              ← definitions that must replace libc.a members
  libc.a  libgcc.a  crtn
  <--extra-ld>  <--extlib objects and wraps>
  [pre-entry]
  [linker_script]  [entry]
  [early]
  --whole-archive [whole-archive] --no-whole-archive
  [normal]
  [wraps of manifests without objects]
  <--ldflags>
```

| Group | Contents |
|-------|----------|
| `pre-libc` | Definitions that must come before `libc.a`: `nothread` and `noos`. lld takes a member from an archive only for a symbol that is not defined yet. Thus an earlier definition stops lld from taking the musl member. |
| `pre-entry` | Objects that must come before the linker script and the entry. |
| `early` | Objects after the entry and before the whole-archive block: `nofp`. |
| `whole-archive` | Objects that lld must link complete. bflat writes them as one block. |
| `normal` | All other objects. |

bflat writes the wraps and the `link.ld` options of a manifest directly
after the first object of that manifest. For the platform, this is the entry
object.

lld applies `--wrap` to the full link. Thus the position of a `--wrap`
between the files has no effect: only the files and their sequence decide
which archive members lld takes. Refer to the verification in
[What left BuildCommand.cs and the modules](#what-left-buildcommandcs-and-the-modules).

### `link`: wraps and other linker options

```json
"link": {
  "wrap": [
    "getenv",
    { "symbol": "RhpThrowEx", "when": { "eh": "failfast" } },
    { "symbol": "PalGetMaximumStackBounds",
      "mangled": "_Z24PalGetMaximumStackBoundsPPvS0_" }
  ],
  "ld": [
    { "value": "--defsym=__zkvm_heap_floor={dir}/floor" }
  ]
}
```

- bflat changes each `wrap` item to `--wrap=<symbol>`. If `mangled` is
  present, it is the linker symbol. `symbol` is then the readable name.
- `ld` contains raw linker options for all other cases. bflat replaces two
  placeholders: `{dir}` is the directory of the manifest, and `{libpath}` is
  the library directory of the target. `--extra-ld` also uses `{libpath}`.
- If two manifests declare the same wrap, bflat writes it one time. This is
  not an error.

### `ilc`: the inputs of the ILC stage

```json
"ilc": {
  "substitutions": ["substitutions.xml"],
  "sources": ["Helpers.cs"],
  "references": [{ "file": "Nethermind.ZiskBindings.dll", "assembly": "Nethermind.ZiskBindings" }],
  "defines": ["ZKVM_ZISK"],
  "features": { "System.TimeZoneInfo.Invariant": true },
  "direct_pinvoke": ["__Internal"]
}
```

| Key | Meaning | Before schema 2 |
|-----|---------|-----------------|
| `substitutions` | ILLink substitution XML files. bflat applies them before the `--substitution` files of the user. | `options.ilc.substitutions` |
| `sources` | Usual C# files. bflat compiles them into the guest together with the sources of the user. | not possible |
| `references` | Managed assemblies. The guest compiles against them, and bflat links them. Use `assembly` if the file name is not the assembly name. | schema 1 `dotnet_lib`, `dotnet_assemblyname` |
| `defines` | Preprocessor symbols for the guest. | `ZKVM_*` in `BuildCommand.cs` |
| `features` | Feature switches. `--feature` on the command line replaces a value. | `BuildCommand.cs` |
| `direct_pinvoke` | The same as `-i`. | `__Internal` in `BuildCommand.cs` |

Each list in `ilc` also accepts the object form. Thus each item can have
its own `when`.

### `codegen`: instruction set, ABI and compiler options

```json
"codegen": {
  "isa": {
    "disable": ["c", "a", "f", "d", "zba", "zbb", "zbs", "zicond"],
    "enable": [{ "value": "zba", "when": { "compiler": "zb-lowering-fixes" } }]
  },
  "abi": "lp64",
  "options": [
    "EnableRiscV64Compressed=0",
    "EnableRiscV64Atomic=0",
    { "value": "JitZkBumpAddr=bffefff8", "note": "Must equal g_zk_bump_ptr in the linker script" },
    { "value": "RiscV64ElideLeafRaSave=1", "when": { "optimize": true } }
  ]
}
```

- `isa.disable` and `isa.enable` contain extension names. Each item can
  have its own `when`. If an extension is in both lists, `enable` wins.
  bflat writes the changes as for `-m`: `-ext` for each disabled extension,
  `ext` for each enabled one, in the sequence of the lists. If the compiler
  package does not know a name, bflat skips it. Thus one manifest works with
  different compiler versions. bflat applies the manifest after the `-m`
  option of the user. `BFLAT_NO_ZK_ISA_REDUCTION=1` turns this off.
- Without F and D, the compiler selects the soft-float ABI itself
  (`TargetAbi.NativeAotRiscV64SoftFloat`). bflat removes F and D from every
  riscv64 target, because the riscv64 layout is lp64: the runtime, musl,
  `libgcc.a` and `libatomic.a` are built rv64im with `-mabi=lp64`. bflat
  links them as they are and changes no file. `bflat-modules check-abi`
  checks the layout when it is made. A package must also ship lp64
  objects; ld.lld refuses an lp64d object. `abi: "lp64"` records the ABI
  of the platform.
- `options` contains RyuJIT options. They are the same as `--codegenopt`.
  Write numbers in hex without `0x`, because RyuJIT reads them in this
  format. bflat and the schema reject a value with `0x`.

If two manifests give different values to the same option, bflat stops with
an error. If they give the same value, bflat writes the option one time. The
`--codegenopt` option of the user comes last, so it replaces the value of
all manifests.

bflat writes two options for every riscv64 target: `JitNoUnalignedAccess`
and `JitRiscV64StrictAlign`, both `0` by default. A platform that gives them
replaces the default (SP1 sets both to `1`). `--no-unaligned-access` sets
`JitNoUnalignedAccess=1` for any target.

### `postlink`: changes to the linked image

```json
"postlink": [
  { "tool": "patch_elf", "args": [
      "--fix-init-array", "--fix-tdata",
      { "value": "--remove-eh", "when": { "eh": "failfast" } },
      "--trim-bss",
      { "value": "--print-fn-boundaries", "when": { "verbose": true } } ] }
]
```

bflat runs the steps in sequence, after the link. Each step changes the
output file in place: what `-o` names is the image that the zkVM runs. An
argument can have its own `when`. If a step fails, the build fails.

`tool` is the name of a tool that is **built into bflat**. The only tool is
`patch_elf`. A package cannot add a tool and cannot run a program. If a
package needs a new step, add the step to bflat.

### `checks`: checks of the full image

```json
"checks": { "float": true, "compressed": true, "atomic": true }
```

These keys turn on `--error-on-float-binary`, `--error-on-compressed` and
`--error-on-atomic` for each guest of this platform. `"il_float": true` turns
on `--error-on-float`, which checks the IL. The platforms of today do not use
this section, so the checks stay opt-in, as before.

### `tests`: module unit tests

Status: validated only. `bflat-modules check` makes sure that each test
file exists. `run_tests.sh` does not read this section yet.

```json
"tests": [
  { "file": "../tests/test_pal.c", "runner": "qemu",
    "cflags": ["-Wl,--allow-multiple-definition"] },
  { "file": "../tests/test_rhp_native.c", "runner": "qemu",
    "matrix": { "BFLAT_DOTNET": ["11"] } }
]
```

| Key | Meaning |
|-----|---------|
| `file` | The test source. |
| `runner` | `qemu` runs the test in `qemu-riscv64`. `host` runs the test on the build machine. |
| `cflags` | Flags in addition to the common test flags. |
| `matrix` | One test build for each value. Each key becomes `-D<key>=<value>`. |
| `when` | The test runs only if this condition is true. |

The plan: `run_tests.sh` gets its test list from `bflat-modules`. If a
module has sources but no tests, the test run fails.

### `external`: files that the build downloads

```json
"external": {
  "repo": "https://github.com/NethermindEth/uGC",
  "tag": "v1.0.7",
  "release": "ugc-riscv64.tar.gz",
  "dir": "release"
}
```

| Key | Meaning |
|-----|---------|
| `repo` | The repository URL. It must start with `https://`. |
| `tag` | The release tag or the git tag. |
| `release` | The name of a release file. The build downloads it and extracts it into `dir`. |
| `build` | Commands that build the files from source. The build clones the repository into `dir` and runs the commands there. |
| `dir` | The directory that contains the files after the download. |

Use `release` or `build`. Do not use the two together.

The build downloads the files before it compiles the sources. The `objects`
section lists the downloaded files, as for all other objects. Thus
`bflat.csproj` and `BuildCommand.cs` do not contain the names of the uGC
objects.

## Platform

A manifest with a `platform` object is a target that the user can select
(a `ZkvmTarget`). The package that owns the target contains it, in
`platform/` of the repository:

| Platform | `--libc` | Package | Files |
|----------|----------|---------|-------|
| `zisk` | `zisk` | bflat-libziskos | `zisk.bflat.manifest`, `zisk_entry.o`, `zisk.ld` |
| `zisk_sim` | `zisk_sim` | bflat-libziskos | `zisk_sim.bflat.manifest`, `zisk_sim.ld`, and `zisk_entry.o` of `zisk` |
| `sp1` | `sp1` | bflat-sp1 | `sp1.bflat.manifest`, `sp1_entry.o`, `sp1.ld` |
| `openvm` | `openvm` | bflat-openvm | `openvm.bflat.manifest`, `openvm_entry.o`, `openvm.ld` |

`platform/build.sh` of each package builds these files. It needs only the
RISC-V binutils. The bindings library of the package (for example
`libsp1.a`) has its own manifest, which applies with
`"when": { "platform": "sp1" }`.

The SP1 platform, shortened:

```json
{
  "schema": 2,
  "name": "sp1",
  "version": "1.0.0",
  "link": { "wrap": ["main", "memcpy", "memmove", "memset"] },
  "ilc": { "defines": ["ZKVM_SP1"] },
  "codegen": {
    "abi": "lp64",
    "isa": { "disable": ["c", "a", "f", "d", "zba", "zbb", "zbs", "zicond"] },
    "options": [
      { "value": "RiscV64ElideLeafRaSave=1", "when": { "optimize": true } },
      "EnableRiscV64Compressed=0", "EnableRiscV64Atomic=0",
      "JitZkBumpAddr=bffefff8", "JitNoUnalignedAccess=1", "JitRiscV64StrictAlign=1"
    ]
  },
  "postlink": [{ "tool": "patch_elf", "args": ["--nop-fences", "--nop-zero-words"] }],
  "platform": {
    "aliases": { "libc": "sp1" },
    "base": { "os": "linux", "arch": "riscv64", "libc": "musl" },
    "requires": ["nofp", "ubootstrap", "stdcppshim", "rhp", "gs_cookie",
                 "rhp_native", "pal", "eh", "tls", "rng_stupid",
                 "rust_sys", "ugc-zero", "nothread", "noos", "guest_subst"],
    "flags": ["no-atomics", "noos", "no-console"],
    "linker_script": "sp1.ld",
    "entry": "sp1_entry.o"
  }
}
```

| Key | Required | Meaning |
|-----|----------|---------|
| `aliases.libc` | no | `--libc <value>` selects this platform. Two platforms must not have the same alias. |
| `base` | yes | The stock target that the platform uses for the sysroot, the runtime files and crt. Conditions read `base.arch`, `base.os` and `base.libc`. bflat links platforms for linux/riscv64 only. |
| `requires` | no | The modules from the layout that the guest needs. bflat uses a module only if the platform requires it. A required module must not be a platform. |
| `flags` | no | Names that modules (and bflat) can test with `when.flag`. |
| `linker_script` | yes | bflat gives it to the linker as `-T`. |
| `entry` | yes | The object file that defines `_start`. bflat links it after the linker script; the platform's wraps and `link.ld` options follow it. |

The user selects a platform with its `--libc` alias:

```console
$ bflat build app.cs --os linux --libc sp1 --extlib path/to/libsp1.bflat.manifest
```

bflat reads the manifests of every `--extlib` package first, and looks for
the alias in them (and in `lib/modules` of the layout). If `--libc` is not a
stock libc and no manifest has the alias, the build stops with an error.
Then bflat loads the manifests in this sequence (the load order):

1. The platform manifest.
2. Each module in `requires`, in the sequence of the list.
3. The manifests of the `--extlib` packages whose `when` holds, in
   command-line order. bflat skips the platforms that are not selected, for
   example `zisk_sim` in a `--libc zisk` build.

Each `--extlib` package must give at least one manifest to the build, as
before schema 2.

For a platform build, bflat links musl's `crt1.o` with `_start` renamed
(`lib/linux/riscv64/musl/crt1-platform.o`), so that the entry of the
platform defines `_start`.

### Platform interface

The modules of bflat are built once and shared by all platforms. Thus they
contain no code for one zkVM. Where a guest needs the zkVM itself, `pal`
calls a function that the entry object of the platform (`platform.entry`)
defines:

| Function | Required | Called for | Without it |
|----------|----------|------------|------------|
| `void zkvm_platform_exit(long code)` | yes; it must not return | `exit`, `_Exit` and `abort` of the guest | the link fails |
| `void zkvm_platform_write(int fd, const char *buf, int len)` | no | console output (`Console`, the trap messages of `noos`, `sys_write`) | the output is lost |
| `long zkvm_platform_syscall(long n, long a1, …, long a6)` | no | a raw `syscall` that `pal` does not answer itself | `syscall` returns `-1` |

The entry object also defines `_start`. The four platforms of today:

| Platform | `zkvm_platform_exit` | `zkvm_platform_write` | `zkvm_platform_syscall` |
|----------|----------------------|-----------------------|-------------------------|
| `zisk`, `zisk_sim` | `ecall` with `a7 = 93` | — | musl's `syscall` (ZisK emulates Linux syscalls) |
| `sp1` | `zkvm_halt` of the bindings library; without it, the commits and `HALT` | the `WRITE` syscall | — |
| `openvm` | `TERMINATE` | `PrintStr` | — |

`pal` also exports functions that a platform can bind its runtime to. Today
there is one: `void *zkvm_alloc_or_abort(uint32_t bytes, uint32_t align)`,
the allocation that ends the guest on exhaustion. The ZisK platform binds the
ziskos Rust allocator to it (`__wrap_inline_bump_alloc_aligned`).

### Address constants

Status: planned.

The JIT and the linker script must both know the address of the
bump-pointer cell (`JitZkBumpAddr` and `g_zk_bump_ptr`). The platform
manifest is to be the only source of this address:

1. bflat reads `JitZkBumpAddr` from `codegen.options`.
2. bflat gives the address to the linker as
   `--defsym=__bflat_zk_bump_addr=0x…`.
3. The `script.ld` of the platform compares the two values:

```ld
ASSERT(g_zk_bump_ptr == __bflat_zk_bump_addr, "JitZkBumpAddr != g_zk_bump_ptr")
```

If the values are different, the link fails.

## Validation

The schema file is `src/bflat/bflat-manifest.schema.json` (JSON Schema
2020-12). It validates schema 2 only. The bflat build copies it to the root
of the layout.

- **Editors.** A manifest can set `"$schema"` to the path of the schema
  file. Then the editor shows errors and completes keys.
- **bflat.** bflat does not use a JSON Schema validator. Its parser
  (`BflatManifest.cs`) reads the JSON with `System.Text.Json` and checks
  each key and each value. Thus bflat also rejects unknown keys. Each error
  message gives the file and the JSON path. bflat also checks the rules
  that a schema cannot express: the files exist, the required modules
  exist, and no two platforms have the same alias.
- **CI.** The "C# analyzers" job runs two checks on the same files:
  - `src/bflat/scripts/check_manifests.py` validates each module manifest
    and each JSON example on this page against the schema file. A bflat-*
    package can give its own manifest as an argument.
  - `bflat-modules check src/bflat/modules` reads each module manifest with
    the bflat parser.

  Thus the schema and the parser stay in agreement.

## Module build and layout: `bflat-modules`

`src/bflat-modules` is a small program that reads the module manifests. It
compiles `BflatManifest.cs`, the parser of bflat. Thus the build has one
parser.

`bflat-modules` is a separate program because it must run before bflat can
run: the bflat build puts the built modules into its layout, and bflat needs
the layout.

| Command | Action |
|---------|--------|
| `bflat-modules check <modules-dir>` | Reads each manifest. Makes sure that the sources, the ILC files, the test files and the linker scripts exist, that each required module exists, and that the `--libc` aliases are unique. Needs no toolchain. |
| `bflat-modules plan <modules-dir> <out-file>` | Writes the module build as shell commands (`module_begin`, `module_fetch_release`, `module_cc`, `module_cxx`, `module_as`, `module_end`). `build.sh modules` runs this command and then executes the file. `build.sh` defines the commands, so the toolchain and its flags stay in `build.sh`. |
| `bflat-modules pack <modules-dir> <layout-modules-dir>` | Makes sure that each file in `objects`, `ilc` and `platform` exists. Copies each manifest and its files to `<layout-modules-dir>/<name>/`. A file of a different module must be a file that this module ships. `bflat.csproj` runs this command when it makes the layout. |
| `bflat-modules link <layout-modules-dir> <platform-manifest> [<package-manifest>...] [--remove-eh] [-O0] [-v] [--compiler <capability>]` | Shows what the manifests give a guest build on a platform: the ISA, the ABI, the RyuJIT options, the defines, the feature switches, the ILC files, the post-link steps and the link command (with placeholders for the parts that bflat adds itself). It uses the same code as bflat. |
| `bflat-modules check-abi <dir>` | Makes sure that each RISC-V object in `<dir>` (each `.o` file and each member of each `.a` file) has the lp64 soft-float ABI. `bflat.csproj` runs this command on `lib/linux/riscv64/musl` when it makes the layout. |

To add a module, add a directory with a manifest and add the name of the
module to `requires` of each platform that needs it (in the platform
packages). No other file of bflat needs a change.

## Schema 1 compatibility

bflat reads a package manifest without `schema` as schema 1. bflat uses the
first `builds[]` entry that matches: `arch` and `os` must be equal, and
`libc` must be equal if both sides give one. Schema 1 permits unknown keys,
because package builds add some (`sp1_ref`, `zisk_ref_hash`).

| Schema 1 | Schema 2 |
|----------|----------|
| `arch`, `os`, `libc` on a build entry | `when` on the manifest. For a platform, use `platform` (for example `"platform": "zisk"`) in place of `libc`. |
| `static_lib` | `objects: ["…"]` |
| `dotnet_lib` and `dotnet_assemblyname` | `ilc.references: [{ "file": …, "assembly": … }]` |
| `wrap_symbols` | `link.wrap` |
| `package_version` | `version` |
| other keys that a build adds | `metadata` |

A schema 1 manifest cannot define a platform. bflat still links it in a
platform build, with its objects in front of the linker script, where they
went before schema 2. A package whose manifests all do not apply causes the
same error as a schema 1 manifest without a matching build.

## How the modules map to schema 2

| Module | sources | objects and group | link.wrap | Other sections | Platform-specific parts to move out |
|--------|---------|-------------------|-----------|----------------|-------------------------------------|
| nofp | `module.c` | early | 38 transcendental libm names and `asprintf` | Only the platforms link it. Before, the stock musl target also linked `module.o`, without wraps; the object has only `__wrap_*` stubs now, so nothing used it. | — |
| ubootstrap | `module.cpp` | whole-archive | — | `ilc.direct_pinvoke` (`__Internal`) | — |
| stdcppshim | `module.cpp` | whole-archive | — | — | — |
| rhp | `module.c` | whole-archive | 19. One has `eh: failfast`. Six have `flag: no-console`. | — | — |
| gs_cookie | `module.c` | whole-archive | `__security_cookie` | — | — |
| rhp_native | `module.S` | whole-archive | 4 | — | — |
| pal | `module.c` | whole-archive | 40. One has `flag: no-console`. | — | — (the exit, the console and raw syscalls go through the [platform interface](#platform-interface)) |
| eh | `module.c` | whole-archive | `dl_iterate_phdr` | — | — |
| tls | `module.c` | whole-archive | 4 | — | — |
| rng_stupid | `module.c` | normal | 4 | — | — |
| rust_sys | `module.c` | normal | `sys_alloc_aligned` | — | — |
| ugc-zero | — | normal: 6 `.obj` files from `release/` | 2 | `external`, `codegen.options` (`JitZkNoGcMustInit=1`) | — |
| nothread | `module.c` | pre-libc, `when: { flag: no-atomics }` | — | — | — |
| noos | `module.c` | pre-libc, `when: { flag: noos }` | — | — | — |
| guest_subst (was zisk_subst) | — | — | — | `ilc.substitutions`, `ilc.features` (invariant globalization, moved from ubootstrap, and invariant timezones) | Nothing in it is for one VM. Only the single-hart substitutions stay; see [Substitutions](#substitutions). |

The four zkvm_* modules are gone: they are the platforms in bflat-libziskos,
bflat-sp1 and bflat-openvm. Their unit tests went with them
(`tests/platform/` of each package).

## What left BuildCommand.cs and the modules

| Before | Now |
|-----------------------------|-----|
| `IsZkvm(libc)` (23 calls) and the `libc == "zisk"`/`"sp1"`/`"openvm"` tests | the selected platform, its `flags` and its `codegen.abi` |
| the fixed link order of the modules | `objects.group` and `objects.order` |
| the entry points and linker scripts in `lib/linux/riscv64/<libc>` | `platform.entry` and `platform.linker_script` in the packages |
| `--libc zisk_sim` uses the ZisK entry point | `entry` of the `zisk_sim` platform |
| the six uGC object names | `objects` of `ugc-zero` |
| `ZKVM_ZISK`, `ZKVM_SP1`, `ZKVM_OPENVM` | `ilc.defines` |
| ISA reduction; Zba, Zbb and Zbs for ZisK (`ZISK_BITMANIP_SUPPORTED`) | `codegen.isa`, with the `zb-lowering-fixes` compiler capability |
| the ABI marker change and `libgcc.a` for the zkVM targets | removed: the layout is lp64, and every riscv64 target links `libgcc.a` |
| approximately 25 JIT options with `libc` checks | `codegen.options` |
| `System.TimeZoneInfo.Invariant`, invariant globalization | `ilc.features` |
| `__Internal` direct P/Invoke | `ilc.direct_pinvoke` |
| `patch_elf` passes for zisk and sp1 | `postlink` |
| the exit, console and syscall code of each zkVM in `pal` (selected by `__zkvm_target_*` marker symbols) | the [platform interface](#platform-interface), defined by the entry of each platform |
| the ziskos allocator bridge in `pal` | `zkvm_alloc_or_abort`, bound by the ZisK entry |

**Verification.** For each platform, the ISA string, the effective RyuJIT
options, the defines, the feature switches, the direct P/Invoke list and the
`patch_elf` arguments are the same as before, in all 16 combinations of
`-O0`, `--remove-eh`, `-v` and the `zb-lowering-fixes` capability. The link
command has the same files in the same sequence and the same set of
`--wrap` options. The sequence of the `--wrap` options is the same, except
for the 13 wraps that moved to the packages (the ZisK accelerator arenas,
and `main`, `memcpy`, `memmove` and `memset` of SP1 and OpenVM): they now
come after the first object of their package. The entry objects that the
packages build have the same code and relocations as before; they no longer
have the local symbol `BFLAT_DOTNET`, which `--discard-all` removed from the
image anyway.

## Substitutions

The `guest_subst` module (before: `zisk_subst`) has only the substitutions
for the single hart: the thread-pool tuner, the gate thread,
`ProcessorIdCache`, `Environment.ProcessorCount`, the `ArrayPool` pressure
test.

Floating point is no longer a reason for a substitution:

- The guests are soft-float, and `libgcc.a` gives the arithmetic.
- `nofp` traps only the transcendental functions (`sin`, `pow`, `exp`,
  `log`, …). It no longer traps the exact operations `ceil`, `floor`,
  `modf`, `sqrt`, `fma` and `scalbn`. Each has one correctly rounded result.
- The musl of the layout is built for `rv64im`. Its libm has no F or D
  instruction, so the image stays free of them.

These substitutions are gone:

| Removed | The code that now runs |
|---------|------------------------|
| `Number.FormatFloat`, `Double`/`Single` `ToString` and `TryFormat` | double and float formatting (`ceil`) |
| `Math.Truncate`, `TimeZoneInfo.TransitionTimeToDateTime`, `AdjustmentRule.HasDaylightSaving` | `DateTime.Add*` and the timezone rules (`modf`) |
| `Array.CopyImplPrimitiveWiden`, `InvokeUtils.ConvertOrWidenPrimitivesEnumsAndPointersIfPossible` | widening to `float` and `double` in `Array.Copy` and reflection |
| `IConvertible.ToInt16`/`ToInt32` of `Double` and `Single`, `Convert.ToInt32(double)` | the conversions |
| `TimeZoneInfo.UtcOffsetOutOfRange`, `AdjustmentRule.ValidateAdjustmentRule` | the range checks |
| the three readers of `float` and `double` constants in the reflection metadata | `ParameterInfo.DefaultValue` and the constant collections |
| all C# snippets: `HashHelpers.IsPrime`, the `TimeZoneInfo` cctor, the compat `Random`, `Hashtable`, `ValueType.GetHashCode`, `Double`/`Single` `Equals(object)` and `CompareTo(object)`, `FrozenHashTable.CalcNumBuckets`, `LengthBuckets` | the upstream bodies |
| the IL rewrites of `Number..cctor` (`s_sqrtTable`), `Number.FormatBigInteger` and `ConcurrentUnifierW.Container.Resize` | the upstream bodies |

The behavior that changes:

- These methods run. Before, the removed ones threw.
- The upstream bodies run, with their exact semantics: `Random(seed).Next(n)`
  scales the sample in `double` as upstream does, `Hashtable` uses the load
  factor that it gets, and a struct with a `-0.0` field has the same hash
  code as one with `+0.0`.
- The image gets the soft-float calls of these methods. Use bflat-ts to
  measure the size and the cycles.

A guest that must have no floating point at all can still use
`--error-on-float` and `--error-on-float-binary`. With the smaller set of
substitutions, these checks find more methods. Such a guest adds its own
`--substitution` file.

The machinery that only the snippets used is gone:

- the `ilc.snippets` manifest key (a manifest that has it is now an error);
- the Roslyn compilation of the snippets and the map of their targets in
  `ZkvmSubstitutions.cs`;
- the `--verify-substitutions` option and the `VerifyZkvmSubstitutions`
  target of `bflat.csproj`;
- the `no-fpu` platform flag. Only the `s_sqrtTable` rewrite read it.

## Decisions

1. **Schema file: yes.** bflat ships `bflat-manifest.schema.json`. Refer to
   [Validation](#validation).
2. **Layout: the bflat code makes it.** `bflat-modules pack` puts the
   modules into the layout with the parser of bflat. Refer to
   [Module build and layout](#module-build-and-layout-bflat-modules).
3. **Post-link tools: only built-in tools.** A package cannot add a tool.
   Refer to [`postlink`](#postlink-changes-to-the-linked-image).
4. **`--extlib` is required for a zkVM target.** bflat ships no platform;
   the platform comes from its package. A guest that does not use the
   accelerators can be built with the platform files alone
   (`platform/build.sh` of the package).
