---
layout: default
title: Manifest format (draft)
eyebrow: Draft specification
lead: >
  One JSON format describes all the parts that bflat links into a guest in
  addition to the stock NativeAOT runtime. The format covers the modules in
  this repository, the bflat-* packages, and the platforms (zkVM targets)
  that these packages define.
---

# bflat manifest, schema 2 (draft)

Status: **draft**. bflat does not use this format yet.

## Terms

| Term | Meaning |
|------|---------|
| guest | The program that bflat builds. |
| manifest | A JSON file in the format that this page describes. |
| module | A directory in `src/bflat/modules/`. A module has one manifest. |
| package | A bflat-* repository (for example bflat-sp1) and its release. A package has one or more manifests. |
| platform | A manifest that has a `platform` object. A platform is a target that the user can select, for example SP1. |
| layout | The directory tree of an installed bflat. |
| condition | An object that tells when a manifest item applies. |

## Formats that this format replaces

Schema 2 replaces two formats:

1. `module_params.yml` in each module. Two parsers read this file today:
   `yq` in `build.sh`, and the parser `BuildCommand.ReadModuleParams`.
2. `bflat-manifest.json` (`*.bflat.manifest`) in the bflat-* packages.
   `ExtLibResolver` reads this file. It has only four fields:
   `static_lib`, `dotnet_lib`, `dotnet_assemblyname` and `wrap_symbols`.

## Goals

1. **A manifest declares all the parts of its module or package.** These
   parts are: sources, objects, link position, wraps, linker scripts, entry
   points, ILC inputs, codegen options, post-link steps, image checks, tests
   and external downloads. `BuildCommand.cs`, `bflat.csproj` and `build.sh`
   must not contain data about a specific module or target.
2. **bflat does not know the name of a zkVM.** The bflat-* package of a
   target (ZisK, SP1, OpenVM) contains the platform manifest of that target.
   bflat knows only the format and the stock NativeAOT targets.
3. **One parser reads the format.** bflat reads manifests with
   `System.Text.Json`. The build scripts get data from bflat. They do not
   read JSON themselves.
4. **One schema file validates the format.** Editors and CI use it.
5. **bflat continues to read schema 1.** Existing package manifests do not
   need changes. Refer to [Schema 1 compatibility](#schema-1-compatibility).

## File names and locations

| Location | File | Reader |
|----------|------|--------|
| Module, in the source tree | `src/bflat/modules/<name>/bflat-manifest.json` | `bflat modules` (the module build) |
| Module, in the layout | `lib/modules/<name>/bflat-manifest.json`, next to the built files of the module | bflat, when it builds a guest |
| Package, in the source tree | `bflat-manifest.json` at the root of the repository (no change) | the build of the package |
| Package, in a release or nupkg | `<name>.bflat.manifest`, next to its files (no change) | bflat, through `--extlib` |
| Schema | `src/bflat/bflat-manifest.schema.json`; in the layout, `bflat-manifest.schema.json` at the root | editors, CI |

A relative path in a manifest starts at **the directory that contains the
manifest**. `static_lib` and `dotnet_lib` use this rule today. An absolute
path is an error.

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
| `schema` | integer | yes | The value is `2`. bflat reads a manifest without `schema` as schema 1. |
| `name` | string | yes | Lowercase letters, digits, `_` and `-`. The name must be unique in one guest build. The name of a module is the name of its directory. |
| `version` | string | no | For information only. It replaces `package_version` of schema 1. |
| `description` | string | no | One line. bflat shows it in `bflat --info` and in error messages. |
| `when` | [condition](#conditions) | no | If the condition is false, bflat ignores the full manifest. |
| `platform` | [object](#platform) | no | This object makes the manifest a platform. |

All other keys are [sections](#sections). **bflat rejects a key that it does
not know.** Thus a typing error causes an error message. bflat does not
ignore the key.

### Notes in place of comments

JSON has no comments. But the comments in `module_params.yml` explain almost
all `--wrap` entries, and this information is important. Thus each object in
a list can have a `"note"` string. bflat ignores the note during the build.
bflat shows the note when you use `-x`.

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
| `arch` | `x64`, `x86`, `arm64`, `riscv64` | `--arch` |
| `os` | `linux`, `windows`, `uefi` | `--os` |
| `libc` | `glibc`, `musl`, `bionic`, `shcrt`, `none` | `--libc`, or `base.libc` of the selected platform |
| `platform` | the `name` of a platform | the selected platform. The value is empty for a stock target. |
| `flag` | flag names that platforms define | `flags` of the selected platform |
| `eh` | `unwind`, `failfast` (one value only) | `--remove-eh` |
| `stdlib` | `dotnet`, `zero`, `none` | `--stdlib` |
| `optimize` | `true`, `false` | `true` if the optimization level is not `-O0`. Only `codegen.options` can use this key. |

Use `flag` to make a generic module change for a platform. The module does
not name the platform. Example: `nothread` is necessary for each platform
whose decoder does not have the A extension. Such a platform sets
`"flags": ["no-atomics"]`. The objects of the module set
`"when": { "flag": "no-atomics" }`.

A condition on a `platform` name is permitted. But only the package that
owns the platform must use it.

You can put a condition at the top level of a manifest (`when`) and on each
list item. A key that bflat does not know is an error. This is the same as
today.

## Sections

### `sources`: the files to compile

```json
"sources": [
  { "file": "heap.c" },
  { "file": "stdio.c", "cflags": ["-fno-builtin-printf"] },
  { "file": "barriers.S", "asflags": ["--defsym", "EXTRA=1"] },
  { "file": "shim.cpp", "when": { "flag": "cxx-runtime" } }
]
```

| Key | Meaning |
|-----|---------|
| `file` | The source file. The extension sets the language: `.c`, `.cpp` or `.cc`, `.S` or `.s`. |
| `cflags`, `cxxflags`, `asflags` | Flags that the module adds after the common flags. |
| `output` | The name of the object file. The default is `<stem>.o`. For example, `heap.c` gives `heap.o`. |
| `when` | The module build compiles the source only if this condition is true. |

The module build supplies the common flags: target triple, `-march`,
`-mabi`, LTO, `-O3` and `BFLAT_DOTNET`. A module can add flags. A module
must not set the common flags again.

Today each module directory can have only one `module.{c,S,cpp}`, because
two sources give the same `module.o`. Schema 2 removes this limit.

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
| `order` | `100` | The sort key in the group (0 to 999). If two items have the same `order`, bflat sorts them by the `name` of their manifest. |
| `when` | — | bflat links the file only if this condition is true. |

If a manifest does not have `objects`, bflat links all outputs of `sources`
with the default group and the default order.

**Groups.** bflat always writes the link command in this sequence. The
manifests fill only the groups in square brackets:

```
crt1 crti  <guest .o>  <bootstrapper>  <runtime libs>
  [pre-libc]                       ← definitions that must replace libc.a members
  libc.a  libgcc.a  crtn
  <--extra-ld>  [linker_script]  [entry]
  [normal, order < whole-archive block]
  --whole-archive [whole-archive] --no-whole-archive
  [normal, the rest]
  <wraps>  <--ldflags>
```

- `pre-libc` contains the definitions that must come before `libc.a`.
  Today these are `nothread` and `noos`. lld takes a member from an archive
  only for a symbol that is not defined yet. Thus an earlier definition
  stops lld from taking the musl member.
- bflat writes all `whole-archive` objects as one block.

Each position in the link order of today (`BuildCommand.cs:2063-2225`) has
one group. The first implementation must give the same link command as
today. Then the guest images stay identical, byte for byte.

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
  present, it is the linker symbol. `symbol` is then the readable name for
  messages.
- The wraps are data. They are not text in `ld`. Thus `--wrap-check` and the
  check for inlined wrap targets (`FindInlinedWrapTargets`) read the wraps
  directly. These checks do not parse the link command.
- `ld` contains raw linker options for all other cases. bflat replaces two
  placeholders: `{dir}` is the directory of the manifest, and `{libpath}` is
  the library directory of the target. `--extra-ld` uses `{libpath}` today.
- If two manifests declare the same wrap, bflat uses it one time. This is
  not an error.

### `ilc`: the inputs of the ILC stage

```json
"ilc": {
  "substitutions": ["zisk.substitutions.xml"],
  "snippets": ["zisk.snippets.cs"],
  "sources": ["Helpers.cs"],
  "references": [{ "file": "Nethermind.ZiskBindings.dll", "assembly": "Nethermind.ZiskBindings" }],
  "defines": ["ZKVM_ZISK"],
  "features": { "System.TimeZoneInfo.Invariant": true },
  "direct_pinvoke": ["__Internal"]
}
```

| Key | Meaning | Today |
|-----|---------|-------|
| `substitutions` | ILLink substitution XML files. bflat applies them before the `--substitution` files of the user. | `options.ilc.substitutions` |
| `snippets` | C# files. bflat compiles them against the implementation assemblies without access checks. Their method bodies replace CoreLib methods. | `options.ilc.snippets` |
| `sources` | Usual C# files. bflat compiles them into the guest together with the sources of the user. | not possible |
| `references` | Managed assemblies. The guest compiles against them, and bflat links them. Use `assembly` if the file name is not the assembly name. | schema 1 `dotnet_lib`, `dotnet_assemblyname` |
| `defines` | Preprocessor symbols for the guest and for the snippets. | `ZKVM_*` in `BuildCommand.cs:579` |
| `features` | Feature switches. `--feature` on the command line replaces a value. | `BuildCommand.cs:1238-1251` |
| `direct_pinvoke` | The same as `-i`. | `__Internal` in `BuildCommand.cs:1190` |

Each list in `ilc` also accepts the object form. Thus each item can have
its own `when`.

### `codegen`: instruction set, ABI and compiler options

```json
"codegen": {
  "isa": { "disable": ["c", "a", "f", "d", "zba", "zbb", "zbs", "zicond"] },
  "abi": "lp64",
  "options": [
    "EnableRiscV64Compressed=0",
    "EnableRiscV64Atomic=0",
    { "value": "JitZkBumpAddr=bffefff8", "note": "Must equal g_zk_bump_ptr in script.ld" },
    { "value": "RiscV64ElideLeafRaSave=1", "when": { "optimize": true } }
  ]
}
```

- `isa.disable` and `isa.enable` contain extension names. bflat keeps the
  probe of today: if the compiler package does not know a name, bflat skips
  it. Thus one manifest works with different compiler versions. bflat
  applies the `-m` option of the user after the manifest.
- `abi: "lp64"` selects the soft-float ABI
  (`TargetAbi.NativeAotRiscV64SoftFloat`). It also starts the change of the
  ELF float-ABI marker in crt, libc and the runtime files (`PatchRiscvAbi*`).
  This change depends only on the ABI. It does not depend on a zkVM.
- `options` contains RyuJIT options. They are the same as `--codegenopt`.
  Write numbers in hex without `0x`, because RyuJIT reads them in this
  format. The schema rejects a value with `0x`.

If two manifests give different values to the same option, bflat stops with
an error. The `--codegenopt` option of the user replaces the value of all
manifests.

### `postlink`: changes to the linked image

```json
"postlink": [
  { "tool": "patch_elf", "args": ["--fix-init-array", "--fix-tdata", "--trim-bss"] },
  { "tool": "patch_elf", "args": ["--remove-eh"], "when": { "eh": "failfast" } }
]
```

bflat runs the steps in sequence. Each step changes the output file in
place. The zisk and sp1 passes of today work in the same way.

`tool` is the name of a tool that is **built into bflat**. Today the only
tool is `patch_elf`. A package cannot add a tool and cannot run a program.
If a package needs a new step, add the step to bflat.

### `checks`: checks of the full image

```json
"checks": { "float": true, "compressed": true, "atomic": true }
```

These keys set the default value of `--error-on-float-binary`,
`--error-on-compressed` and `--error-on-atomic` for each guest of this
platform. `"il_float": true` sets `--error-on-float`, which checks the IL.

### `tests`: module unit tests

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
| `matrix` | bflat makes one test build for each value. Each key becomes `-D<key>=<value>`. |
| `when` | The test runs only if this condition is true. |

`run_tests.sh` gets its test list from `bflat modules tests`. If a module
has sources but no tests, the test run fails. This replaces the list that
people update by hand today.

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
| `build` | Commands that build the files from source. The commands run in the cloned tree. |
| `dir` | The directory that contains the files after the download. |

Use `release` or `build`. Do not use the two together.

The build downloads the files before bflat looks for `objects`. The
`objects` section lists the downloaded files, as for all other objects.
Thus the six `.obj` names in `bflat.csproj` and `BuildCommand.cs` go away.
Nothing reads the `extern: True` key of today, so schema 2 does not have it.

## Platform

A manifest with a `platform` object is a target that the user can select
(a `ZkvmTarget`). The package that owns the target contains this manifest.
For example, bflat-sp1 contains the SP1 platform.

```json
{
  "schema": 2,
  "name": "sp1",
  "version": "1.0.0-preview.6",
  "platform": {
    "aliases": { "libc": "sp1" },
    "base": { "os": "linux", "arch": "riscv64", "libc": "musl" },
    "requires": ["nofp", "ubootstrap", "stdcppshim", "rhp", "gs_cookie",
                 "rhp_native", "pal", "eh", "tls", "rng_stupid",
                 "rust_sys", "ugc-zero", "nothread", "noos"],
    "flags": ["no-atomics", "noos"],
    "linker_script": "script.ld",
    "entry": "sp1_entry.o"
  },
  "objects":  [{ "file": "libsp1.a" }],
  "ilc":      { "defines": ["ZKVM_SP1"], "direct_pinvoke": ["__Internal"] },
  "codegen":  { "abi": "lp64", "isa": { "disable": ["c","a","f","d","zba","zbb","zbs","zicond"] },
                "options": ["JitZkBumpAddr=bffefff8", "JitNoUnalignedAccess=1",
                            "JitRiscV64StrictAlign=1", "EnableRiscV64Compressed=0",
                            "EnableRiscV64Atomic=0", "JitZkNoGcMustInit=1"] },
  "postlink": [{ "tool": "patch_elf", "args": ["--nop-fences", "--nop-zero-words"] }],
  "checks":   { "float": true, "compressed": true, "atomic": true }
}
```

| Key | Required | Meaning |
|-----|----------|---------|
| `aliases.libc` | no | `--libc <value>` selects this platform. Thus `--libc sp1` continues to work after the target moves into its package. |
| `base` | yes | The stock target that the platform uses for the sysroot, the runtime files and crt. Conditions read `base.libc` as `libc`. |
| `requires` | no | The modules from the layout that the guest needs. bflat uses a module only if a manifest requires it. |
| `flags` | no | Names that modules can test with `when.flag`. |
| `linker_script` | yes | bflat gives it to the linker as `-T`. One link has exactly one linker script. Two platforms, or a platform and `--ldflags -T`, cause an error. |
| `entry` | yes | The object file that defines `_start`. One link has exactly one entry. |

The user selects a platform with `--platform <spec>` or with a `--libc`
alias. bflat looks for the alias in the platforms of the layout and of the
`--extlib` packages. Then bflat loads the manifests in this sequence:

1. The platform manifest.
2. Each module in `requires`.
3. The `--extlib` manifests of the user.

bflat then merges the sections of all these manifests, as the sections
above tell.

### Address constants

The JIT and the linker script must both know the address of the
bump-pointer cell (`JitZkBumpAddr` and `g_zk_bump_ptr`). The platform
manifest is the only source of this address:

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
- **CI.** A CI step validates each manifest in `src/bflat/modules/` and
  each JSON example in this page against the schema file. The bflat-*
  packages use the same step for their manifests.
- **bflat.** bflat does not use a JSON Schema validator. It reads manifests
  into C# types with `System.Text.Json` and
  `JsonUnmappedMemberHandling.Disallow`. Thus bflat also rejects unknown
  keys. bflat checks the rules that a schema cannot express: the files
  exist, the platform has one linker script and one entry, and option
  values do not conflict.
- **Agreement.** A unit test reads all manifests with the bflat parser and
  with the schema. The two results must be the same. Thus the schema and
  the C# types stay in agreement.

## Module layout: `bflat modules`

bflat itself puts the modules into the layout. A `bflat modules` command
reads the manifests. Thus the build has one parser.

| Command | Action |
|---------|--------|
| `bflat modules plan <src>` | Shows the steps to build the modules, one JSON object for each line. A step is `fetch` (from `external`) or `compile` (from `sources`, with the full flags). `build.sh` runs the steps with its clang and does not read manifests. |
| `bflat modules pack <src> <obj> <layout>` | Validates each manifest. Makes sure that each file in `objects`, `ilc`, `platform` and `tests` exists. Copies the manifest and its files to `<layout>/lib/modules/<name>/`. |
| `bflat modules tests <src>` | Shows the test builds for `run_tests.sh`, one JSON object for each line. |

The roughly 50 `<Copy>` items in `bflat.csproj` go away. `bflat.csproj`
calls `bflat modules pack` and copies `lib/modules/` as one tree. To add a
module, a developer adds a directory with a manifest. No other file needs
a change.

## Schema 1 compatibility

bflat reads a manifest without `schema` as schema 1. Each `builds[]` entry
becomes one schema-2 manifest:

| Schema 1 | Schema 2 |
|----------|----------|
| `arch`, `os`, `libc` on a build entry | `when` on the manifest. Here `libc` can also match a platform alias. |
| `static_lib` | `objects: [{ "file": … }]` |
| `dotnet_lib` and `dotnet_assemblyname` | `ilc.references: [{ "file": …, "assembly": … }]` |
| `wrap_symbols` | `link.wrap` |
| `package_version` | `version` |

bflat-libziskos, bflat-sp1 and bflat-openvm continue to work without
changes. They move to schema 2 when they get their platform manifests.

## How the modules of today map to schema 2

| Module | sources | objects and group | link.wrap | Other sections | Platform-specific parts to move out |
|--------|---------|-------------------|-----------|----------------|-------------------------------------|
| nofp | `module.c` | normal, before the whole-archive block | 50 libm and soft-float names | The musl target also uses this module, without wraps. The wraps have a `when`. | — |
| ubootstrap | `module.cpp` | whole-archive | — | `tests` with BFLAT_DOTNET=11 | — |
| stdcppshim | `module.cpp` | whole-archive | — | — | — |
| rhp | `module.c` | whole-archive | 19. One has `eh: failfast`. | — | — |
| gs_cookie | `module.c` | whole-archive | `__security_cookie` | — | — |
| rhp_native | `module.S` | whole-archive | 4 | `asflags` get BFLAT_DOTNET from the module build. | — |
| pal | `module.c` (later, several files) | whole-archive | 53. 14 have `libc` conditions. | — | The ZisK exit ecall, the SP1 syscalls and digest, and the OpenVM and SP1 console move to the `entry` of each package. The `libc` conditions go away. |
| eh | `module.c` | whole-archive | `dl_iterate_phdr` | `when: { eh: unwind }`, after `--remove-eh` really removes the module. Today bflat always links it. | — |
| tls | `module.c` | whole-archive | 4 | — | — |
| rng_stupid | `module.c` | normal | 4 | — | — |
| rust_sys | `module.c` | normal | `sys_alloc_aligned` | — | — |
| ugc-zero | — | normal: 6 `.obj` files from `release/` | 2 | `external` | — |
| nothread | `module.c` | pre-libc, `when: { flag: no-atomics }` | — | — | The C# check `libc == sp1 or openvm` becomes a flag. |
| noos | `module.c` | pre-libc, `when: { flag: noos }` | — | — | The same as nothread. |
| zisk_subst | — | — | — | `ilc.substitutions`, `ilc.snippets` | The ZisK-only snippets move to bflat-libziskos. The parts that all FPU-less targets need stay in a module. |
| zkvm_zisk, zkvm_zisk_sim, zkvm_sp1, zkvm_openvm | `module.S` | — | — | — | They become `platform.entry` and `platform.linker_script` in bflat-libziskos, bflat-sp1 and bflat-openvm. |

This table shows the platform data that leaves `BuildCommand.cs`:

| In `BuildCommand.cs` today | In schema 2 |
|----------------------------|-------------|
| `IsZkvm(libc)` (41 locations) | the selected platform and its `flags` |
| `ZKVM_ZISK`, `ZKVM_SP1`, `ZKVM_OPENVM` | `ilc.defines` |
| ISA reduction; Zba, Zbb and Zbs for ZisK | `codegen.isa` |
| `TargetAbi.NativeAotRiscV64SoftFloat` and the ABI marker change | `codegen.abi: "lp64"` |
| approximately 25 JIT options with `libc` checks (`JitZkBumpAddr`, `JitZkCsrPInvokes`, `JitRiscV64StrictAlign`, …) | `codegen.options` |
| `System.TimeZoneInfo.Invariant`, invariant globalization | `ilc.features` |
| `__Internal` direct P/Invoke | `ilc.direct_pinvoke` |
| `patch_elf` passes for zisk and sp1 | `postlink` |
| `--libc zisk_sim` uses the ZisK entry point | The zisk_sim platform sets `entry` to the file from bflat-libziskos. |
| the fixed link order of the modules | `objects.group` and `objects.order` |

## Decisions

1. **Schema file: yes.** bflat ships `bflat-manifest.schema.json`. Refer to
   [Validation](#validation).
2. **Layout: bflat makes it.** `bflat modules pack` puts the modules into
   the layout. Refer to [Module layout](#module-layout-bflat-modules).
3. **Post-link tools: only built-in tools.** A package cannot add a tool.
   Refer to [`postlink`](#postlink-changes-to-the-linked-image).
