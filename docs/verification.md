---
layout: default
title: Verification
eyebrow: How we know it works
lead: >
  A zkVM guest fails late and quietly: a stray floating-point instruction
  or a silently dropped substitution shows up as a rejected opcode in the
  prover, far from the change that caused it. So the checks are layered to
  fail as early as possible — from contracts on individual C functions up
  to an end-to-end proof.
prev: /build/
---

<div style="background: var(--bg-elev-1); border: 1px solid var(--border); border-radius: var(--radius-lg); padding: 24px; margin: 16px 0 32px;">
  {% include verify-flow.html %}
</div>

## The layers

**CI** is a job in this repository's
[`build-riscv64.yml`](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml),
which runs on every push to `master` and on every pull request (see
[CI](#ci) below). **Build** means it happens while building bflat or a
guest. **bflat-ts** is the test suite on the zk-testing host: CI starts it
for a pull request, and it also runs on each change of bflat-ts itself.

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
| **Inlined-wrap check** | Build (optimized) | Every `--wrap` target that is a managed method the scanner reached is still emitted, so calls to it remain for the linker to divert | A wrapped method inlined into all its callers, which silently bypasses the wrapper |
| **`--wrap-check`** | Build, opt-in | Every `--wrap=` that the link gets names a symbol that exists | A wrap left behind after a rename, which links but diverts nothing |
| **`--error-on-float`** | Build, opt-in | No emitted method's *IL* contains a floating-point conversion | FP reaching the image through a `conv.r8` |
| **ISA verification** | Build, opt-in or by the platform | The *linked binary* carries no F/D, compressed or atomic instruction | Everything the IL scan cannot see, including native objects |

The FP and ISA gates are flags you pass. A platform can also turn the ISA
gates on for every build of it (its `checks` section): the Airbender
platform does.

## CI

`build-riscv64.yml` has these jobs. All of them fail the workflow when they
fail, except where the table says otherwise.

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
   `ghcr.io/nethermindeth/bflat-riscv64-11:pr-<number>`, and gives the jobs
   below the image by digest. A new push to the pull request thus does not
   change the image of a run in progress.
2. **bflat-ts (simple)** starts the workflow `compiler-tests.yml` of
   bflat-ts with that image, the `simple` test and `require_pass`
   (`.github/scripts/run-bflat-ts.sh`), and waits for it. The run of
   bflat-ts is linked in the summary of the job.
3. When `simple` passes, **bflat-ts** starts the full suite the same way.

The runs belong to bflat-ts: it holds the secrets of zk-testing, and this
repository starts its workflow with the token `BFLAT_TS_TOKEN` (Actions:
read and write on bflat-ts). zk-testing runs one bflat-ts run at a time, so
a run can wait in its queue. A full run takes about two hours, and the
`simple` test goes first so that a broken image fails in minutes. A pull
request from a fork gets no token: it pushes no image and runs no bflat-ts.

## Contracts on the native modules

Every function in the C modules under `src/bflat/modules/` carries a
[Frama-C](https://frama-c.com/) (ACSL) contract stating what it may touch
and what it guarantees:

```console
$ python3 src/bflat/scripts/check_acsl.py
module            defined  annotated  status
eh                      4          4  ok
pal                    47         47  ok
rhp                    16         16  ok
...
```

Annotations inside macro bodies — nofp generates its trap stubs from a
macro — are only visible to Frama-C when the preprocessor keeps comments
through expansion, so the checker passes `-cpp-extra-args=-CC`. C++ modules
carry ACSL++ annotations as documentation (plain Frama-C does not parse
C++), and assembly modules are out of scope.

## Unit tests on the real ISA

The suites in `src/bflat/modules/tests/` are cross-compiled for riscv64 and
executed under qemu-user, so module code runs on the guest ISA rather than
a host approximation. That is what makes some of them possible at all:

- **The terminators are tested for real.** The tests define the platform
  function `zkvm_platform_exit` as the exit of a forked child, so
  `__wrap_exit(42)` genuinely terminates that child with status 42.
- **Assembly modules are covered.** A small shim marshals the `t3/t4/t5`
  register convention that the C ABI cannot express, so the GC write
  barriers are tested directly — including their `t3` contract (the
  single-assign helpers keep `t3`, as .NET 11 wants).

```console
$ ./src/bflat/modules/tests/run_tests.sh
pal                137 passed, 0 failed
rhp_native          26 passed, 0 failed
...
module unit tests: all suites passed
```

nofp's stub list is generated from the module source at test-build time, so
a stub added to the module cannot silently miss coverage.

## Fuzzing and machine-checked proofs

Two components take untrusted input and deserve more than examples: pal's
hand-written `vfprintf` and the bump allocator. Both have libFuzzer targets
built for the host with ASan and UBSan, and the allocator target drives
random malloc/realloc/calloc/aligned_alloc/mark/reset sequences with
unconstrained 64-bit sizes, asserting the invariants after every step.

Two properties are proved rather than sampled:

- **The allocator's bounds**, with [CBMC](https://www.cbmc.diffblue.com/),
  for all sizes and reachable states. The harness includes the real
  `pal/module.c` with its heap-window symbols re-pointed at a local array.
  The proof is mutation-tested: weakening the bounds check makes CBMC report
  the wild write, so a passing run is not vacuous.
- **The `eh` module**, with Frama-C. WP discharges every contract of the
  code that builds the synthetic program headers, runtime-error guards
  included; Eva then runs the module end to end — dispatcher and all — in
  both shapes an image can have, unwind tables kept and stripped, and must
  report zero alarms. The split exists because WP cannot reason about a call
  through a caller-supplied function pointer, which is all the dispatcher
  does once the headers are built; Eva resolves that pointer against the
  driver's callback.

## Keeping the substitutions honest

The [ILC-stage substitutions](architecture.md#stage-15--ilc-stage-substitutions)
are the layer most exposed to runtime drift, and the failure is silent by
nature: if a substituted CoreLib method is renamed or re-signatured, the
entry stops matching and the original body stays in the image.

**ILLink substitution entries only warn.** A `<method signature=…>` that
matches nothing produces `IL2009: Could not find method …` and the build
continues, with the original left in place. The signature is
rendered by ILC and that rendering differs between .NET majors — .NET 11
spells a method's own generic parameters by name
(``ValueListBuilder`1<TChar>&``), earlier ILCs leave them empty — so an
entry written for one major is inert on the other. Read the build output for
IL2009.

## Floating-point gates

There are two gates because one is not enough:

- **`--error-on-float`** scans every *emitted* method's IL for floating-point
  conversions. Methods that carry a conversion inside a block the target
  provably never runs are listed explicitly and reported as warnings.
- **`--error-on-float-binary`, `--error-on-compressed`, `--error-on-atomic`**
  decode the *linked binary* and fail on any F/D, compressed or atomic
  instruction, naming the offending symbol.

The binary gates are the load-bearing ones. The IL scan cannot see a
comparison — `ceq` over two `float64`s carries no `conv.r8`, so
`Double.CompareTo`/`Equals` lowers to `feq.d`/`flt.d` with a clean IL report
— and it cannot see native code at all, since the runtime's own objects, the
GC and llvm-libunwind among them, are compiled outside ILC.

## Analyzers on the driver

The C# driver builds with every .NET analyzer enabled (`AnalysisMode=All`)
with `-warnaserror`. `.editorconfig` decides
which rules are errors, which stay warnings and which are off, each
exemption with a reason next to it. The rules kept as errors are defect
classes — dead conditions, leaked disposables, inexact reads, ignored
results — rather than formatting preferences on code inherited from
upstream.

## Guests that run

CI executes guests in two places. The Airbender job runs the riscv32 test
guests on the Airbender v3 simulator. bflat-ts runs everything else on the
zk-testing host: its tests build with the image of bflat and run on each
zkVM (ZisK, SP1, OpenVM and the simulator target), and they include the guest of
Nethermind's
[StatelessExecutor](https://github.com/NethermindEth/nethermind) — an
Ethereum state-transition function in production C#. Results land on the
[zk-testing dashboard](https://zk-testing.nethermind.dev/v2/dashboard?search=&project=1),
which tracks:

<dl class="kv">
  <dt>Pass or fail</dt><dd>Whether each test built, ran and produced the
    output that it expects, or failed to load, crashed, or produced other
    output.</dd>
  <dt>Proof timing</dt><dd>How long a prover takes on the workload. A
    sudden regression usually means a runtime shim started doing more work
    — for example the allocator hitting a path that allocates far more.</dd>
  <dt>Binary size</dt><dd>The size of the postprocessed ELF, which tracks
    the cost in prover steps and leads the timing metric.</dd>
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

If `--libc zisk_sim` works and the equivalent `--libc zisk` build fails
inside Zisk, the difference is almost always the post-link step of the
`zisk` platform. The next step is `--print-fn-boundaries` of
`patch_elf.py`: the `zisk` platform passes it when the build is verbose
(`--verbose`), and it prints the start and end of each function.
