---
layout: default
title: Verification
eyebrow: How we know it works
lead: >
  A zkVM guest often fails late. A floating-point instruction or a dropped
  substitution becomes a rejected opcode in the prover, far from the change
  that caused it. Thus the checks run in layers, from contracts on single C
  functions to a full run of the bflat-ts suite, and each layer stops the
  fault as early as it can.
prev: /build/
---

<div class="figure">
  {% include verify-flow.html %}
</div>

## The layers

The "Runs in" column has these values:

- **CI**: a job in
  [`build-riscv64.yml`](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml)
  of this repository. It runs on each push to `master` and on each pull
  request (see [CI](#ci) below).
- **Build**: the build of bflat or of a guest does the check.
- **bflat-ts**: the test suite on the zk-testing host. CI starts it for a
  pull request. It also runs on each change of bflat-ts itself.

| Layer | Runs in | What it proves | What it catches |
|-------|---------|----------------|-----------------|
| **ACSL contracts** | CI | Every function in the C modules carries a formal contract and parses under Frama-C | A new function landing without a specification; a module that stops parsing |
| **`eh` proof** | CI | The unwinder's synthetic program headers are correct, and the module is free of runtime errors | A wrong or unsafe answer to the lookup that managed exception handling depends on |
| **Module unit tests** | CI | Each module function behaves as specified, executing on the real ISA under qemu | Wrong return values, off-by-one stores, broken register contracts |
| **Fuzzing** | CI | The two input-driven components survive arbitrary input under ASan/UBSan | Memory errors and arithmetic UB in the printf parser and the allocator |
| **CBMC proof** | CI | The allocator's bounds properties hold for *all* 64-bit sizes | Pointer arithmetic that wraps for inputs no test would think to try |
| **C# analyzers** | CI | The driver compiles clean under every .NET analyzer | Dead conditions, leaked handles, ignored results |
| **Manifest check** | CI | Every module manifest agrees with the JSON schema and with bflat's own parser (`check_manifests.py`, `bflat-modules check`) | A manifest that one reader accepts and the other refuses |
| **bflat unit tests** | CI | The manifest parser, the package signatures, the `--extlib` resolution, the float-ABI check and the ISA verifier work without a compiler layout (`src/bflat.UnitTests`) | A regression in the driver logic that needs no guest to show |
| **Build & smoke** | CI | The driver, all modules and the Docker images build; a guest links for each riscv64 zkVM target; a linux-x64 program builds and runs | Renamed musl symbols, unresolved wraps, broken postprocessing, a broken stock target |
| **Unaligned-access codegen** | CI | `Unsafe.ReadUnaligned` stays one wide load on ZisK and OpenVM, and is byte-wise on SP1 (`tests/run-unaligned-access.sh`) | A platform that gets the wrong access width for its executor |
| **Airbender run** | CI | The riscv32 guests run on the Airbender v3 simulator: the console, the input and output words, an accelerator, and a failing run (`tests/run-airbender.sh`) | A riscv32 guest that does not load, run or end as expected |
| **bflat-ts** | CI (pull request), bflat-ts | The image of the change builds and runs the bflat-ts tests on each zkVM, Nethermind's guest included | Wrong output, load failures and regressions in a real workload |
| **Package signatures** | Build, guest build | The dotnet-riscv release, the module release archives and each `--extlib` `.nupkg` come from our CI ([details](signing.md)) | A changed or replaced download |
| **Inlined-wrap check** | Build (optimized) | Every `--wrap` target that is a managed method the scanner reached is still emitted, so calls to it remain for the linker to divert | A wrapped method inlined into all its callers, which bypasses the wrapper with no error |
| **`--wrap-check`** | Build, opt-in | Every `--wrap=` that the link gets names a symbol that exists | A wrap left behind after a rename, which links but diverts nothing |
| **`--error-on-float`** | Build, opt-in | No emitted method's *IL* contains a floating-point conversion | FP reaching the image through a `conv.r8` |
| **ISA verification** | Build, opt-in or by the platform | The *linked binary* carries no F/D, compressed or atomic instruction | Everything the IL scan cannot see, including native objects |

The FP and ISA gates are flags that you pass. A platform can also turn on
the ISA gates for each of its builds, in its `checks` section. The
Airbender platform does this.

## CI

`build-riscv64.yml` has these jobs. When a job fails, the workflow fails,
unless the table gives a different rule.

| Job | What it does |
|-----|--------------|
| ACSL contract gate (Frama-C) | `check_acsl.py` on the C modules |
| Formal proof of the eh module (Frama-C WP + Eva) | `eh/verify/run_wp.sh` in the `framac/frama-c` image |
| C# analyzers | Builds the driver with `-warnaserror`, checks the module manifests, runs `src/bflat.UnitTests` |
| Fuzz smoke (vfprintf + allocator) | The libFuzzer targets for a short time; a crash is uploaded as an artifact |
| CBMC allocator proof | `tests/verify/run_verify.sh` |
| Module unit tests (riscv64 under qemu) | `modules/tests/run_tests.sh` |
| Build RISC-V64 on Ubuntu 26.04 | A matrix of two legs, below. Each builds the modules and bflat, builds the Docker image, runs `bflat --info`, the linux-x64 smoke test, a smoke build for `zisk`, `zisk_sim`, `sp1` and `openvm`, and the unaligned-access check |
| Run the Airbender guests | Runs the guests of the riscv32 leg on the Airbender v3 simulator (`AIRBENDER_COMMIT`) |
| bflat-ts (simple) | Pull requests only: the `simple` test of bflat-ts with the image of the pull request. A failed test fails the job |
| bflat-ts | Pull requests only, after `simple` passes: the full bflat-ts suite with the same image. The job fails when the run fails; a failed test shows in the run of bflat-ts, not in this job |

The two legs of the build matrix:

| Leg | Image | Runtime release | Also |
|-----|-------|-----------------|------|
| `default` SDK | `nethermindeth/bflat-riscv64-11` | `11.0.100-upstream-perf.r400` | Pushes the image of a pull request to GHCR |
| `riscv32` SDK | `nethermindeth/bflat-riscv64-11-riscv32` | `11.0.100-upstream-perf.r423` (`BflatTargetRiscV32=true`) | Builds the rv32 modules and the Airbender guests |

The zkVM platforms come from the `main` branch of bflat-libziskos, bflat-sp1,
bflat-openvm and bflat-airbender. A manual run (`workflow_dispatch`) can
take other branches (`packages_ref`, `airbender_ref`) and can publish the
images to Docker Hub (`publish`).

### bflat-ts on a pull request

For a pull request from a branch of this repository:

1. The `default` leg pushes its image to
   `ghcr.io/nethermindeth/bflat-riscv64-11:pr-<number>`. It gives the image
   to the jobs below by digest. Thus a new push to the pull request does
   not change the image of a run in progress.
2. The job "bflat-ts (simple)" starts the workflow `compiler-tests.yml` of
   bflat-ts with that image, the `simple` test and `require_pass`
   (`.github/scripts/run-bflat-ts.sh`). Then it waits for the run. The
   summary of the job has a link to the run of bflat-ts.
3. When `simple` passes, the job "bflat-ts" starts the full suite in the
   same way.

The runs belong to bflat-ts, because bflat-ts has the secrets of
zk-testing. This repository starts the workflow of bflat-ts with the token
`BFLAT_TS_TOKEN` (Actions: read and write on bflat-ts). zk-testing does one
bflat-ts run at a time, so a run can wait in the queue. A full run takes
about two hours. The `simple` test goes first, so a broken image fails in
minutes. A pull request from a fork gets no token. It pushes no image and
runs no bflat-ts.

## Contracts on the native modules

Each function in the C modules under `src/bflat/modules/` has a
[Frama-C](https://frama-c.com/) (ACSL) contract. The contract gives what the
function can change and what it guarantees:

```console
$ python3 src/bflat/scripts/check_acsl.py
module            defined  annotated  status
eh                      4          4  ok
pal                    47         47  ok
rhp                    16         16  ok
...
```

nofp makes its trap stubs from a macro. Frama-C sees the annotations in a
macro body only when the preprocessor keeps the comments in the expansion.
Thus the checker passes `-cpp-extra-args=-CC`. The C++ modules have ACSL++
annotations as documentation, because plain Frama-C does not parse C++. The
checker does not examine the assembly modules.

## Unit tests on the real ISA

The suites in `src/bflat/modules/tests/` are cross-compiled for riscv64.
They run under qemu-user, so the module code runs on the guest ISA. This
makes some tests possible:

- **The exit functions.** The tests define the platform function
  `zkvm_platform_exit` as the exit of a forked child. Thus
  `__wrap_exit(42)` stops that child with status 42.
- **The assembly modules.** A small shim sets up the `t3/t4/t5` register
  convention, which the C ABI cannot express. Thus the tests call the GC
  write barriers directly and check their `t3` contract: the single-assign
  helpers keep `t3`, as .NET 11 requires.

```console
$ ./src/bflat/modules/tests/run_tests.sh
pal                137 passed, 0 failed
rhp_native          26 passed, 0 failed
...
module unit tests: all suites passed
```

The test build makes the list of nofp stubs from the module source. Thus
each new stub in the module gets a test.

## Fuzzing and machine-checked proofs

Two components take untrusted input: the hand-written `vfprintf` of pal
and the bump allocator. Both have libFuzzer targets, built for the host
with ASan and UBSan. The allocator target runs random sequences of
malloc, realloc, calloc, aligned_alloc, mark and reset, with any 64-bit
size. It checks the invariants after each step.

Two properties have a proof for all inputs:

- **The allocator's bounds**, with [CBMC](https://www.cbmc.diffblue.com/),
  for all sizes and reachable states. The harness includes the real
  `pal/module.c`, with its heap-window symbols pointed at a local array.
  A mutation test checks the proof: with a weaker bounds check, CBMC
  reports the wild write. Thus a pass is not vacuous.
- **The `eh` module**, with Frama-C. WP proves each contract of the code
  that builds the synthetic program headers, with the runtime-error
  guards. Then Eva runs all of the module, with the dispatcher, in the two
  shapes of an image: with unwind tables and without them. Eva must report
  zero alarms. WP cannot reason about a call through a function pointer
  from the caller, and the dispatcher only makes that call after it builds
  the headers. Eva resolves the pointer against the callback of the
  driver.

## Keeping the substitutions honest

The [ILC-stage substitutions](architecture.md#stage-15--ilc-stage-substitutions)
change most when the runtime changes, and the failure gives no error.
When a substituted CoreLib method gets a new name or signature, the entry
stops matching. Then the original body stays in the image.

**ILLink substitution entries only warn.** A `<method signature=…>` that
matches nothing gives `IL2009: Could not find method …`. The build
continues, and the original stays in the image. ILC writes the signature,
and the text is different between .NET majors. .NET 11 writes the generic
parameters of a method by name (``ValueListBuilder`1<TChar>&``). Earlier
ILCs leave them empty. Thus an entry for one major has no effect on the
other. Read the build output for IL2009.

## Floating-point gates

There are two gates. Each one finds what the other cannot see:

- **`--error-on-float`** scans the IL of each *emitted* method for
  floating-point conversions. A list names the methods with a conversion
  in a block that the target never runs. These give a warning.
- **`--error-on-float-binary`, `--error-on-compressed`, `--error-on-atomic`**
  decode the *linked binary* and fail on any F/D, compressed or atomic
  instruction, naming the offending symbol.

The binary gates find more. The IL scan cannot see a comparison: `ceq` on
two `float64` values has no `conv.r8`. Thus `Double.CompareTo`/`Equals`
lowers to `feq.d`/`flt.d`, and the IL report is clean. The IL scan also
cannot see native code. The objects of the runtime, the GC and
llvm-libunwind among them, compile outside ILC.

## Analyzers on the driver

The C# driver builds with all .NET analyzers on (`AnalysisMode=All`) and
with `-warnaserror`. `.editorconfig` sets which rules are errors, which are
warnings and which are off. Each exemption has its reason next to it. The
rules that stay errors find defects: dead conditions, leaked disposables,
inexact reads and ignored results. Formatting rules for the code from
upstream do not stay errors.

## Guests that run

CI runs guests in two places:

- The Airbender job runs the riscv32 test guests on the Airbender v3
  simulator.
- bflat-ts runs all other guests on the zk-testing host. Its tests build
  with the image of bflat and run on each zkVM (ZisK, SP1, OpenVM and the
  simulator target). They include the guest of Nethermind's
  [StatelessExecutor](https://github.com/NethermindEth/nethermind), an
  Ethereum state-transition function in production C#.

The results go to the
[zk-testing dashboard](https://zk-testing.nethermind.dev/v2/dashboard?search=&project=1).
It shows:

<dl class="kv">
  <dt>Pass or fail</dt><dd>Whether each test built, ran and gave the
    expected output. Otherwise: it did not load, it crashed, or it gave
    other output.</dd>
  <dt>Proof timing</dt><dd>How long a prover takes on the workload. A
    sudden increase usually means that a runtime shim does more work. For
    example, the allocator takes a path that allocates much more.</dd>
  <dt>Binary size</dt><dd>The size of the ELF after the post-link steps.
    It follows the cost in prover steps, and it changes before the
    timing does.</dd>
</dl>

## Running the checks locally

[BUILDING.md](https://github.com/NethermindEth/bflat-riscv64/blob/master/BUILDING.md)
has the commands and what each one needs.

## When a guest misbehaves

Run the simulator path first:

```console
$ bflat build samples/HelloWorld/hello.cs --os linux --libc zisk_sim \
    --extlib libziskos/zisk_sim.bflat.manifest -x
$ qemu-riscv64 ./hello
Hello world!
```

Sometimes `--libc zisk_sim` works, and the same build with `--libc zisk`
fails in Zisk. Then the cause is almost always the post-link step of the
`zisk` platform. Next, use the `--print-fn-boundaries` option of
`patch_elf.py`. The `zisk` platform passes it in a verbose build
(`--verbose`). It prints the start and end of each function.
