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
which fires on every push and pull request. **Build** means it happens while
building bflat or a guest. **Pipeline** is the separate zk-testing pipeline,
which is not driven from this repository.

| Layer | Runs in | What it proves | What it catches |
|-------|---------|----------------|-----------------|
| **ACSL contracts** | CI | Every function in the C modules carries a formal contract and parses under Frama-C | A new function landing without a specification; a module that stops parsing |
| **`eh` proof** | CI | The unwinder's synthetic program headers are correct, and the module is free of runtime errors | A wrong or unsafe answer to the lookup that managed exception handling depends on |
| **Module unit tests** | CI | Each module function behaves as specified, executing on the real ISA under qemu | Wrong return values, off-by-one stores, broken register contracts |
| **Fuzzing** | CI | The two input-driven components survive arbitrary input under ASan/UBSan | Memory errors and arithmetic UB in the printf parser and the allocator |
| **CBMC proof** | CI | The allocator's bounds properties hold for *all* 64-bit sizes | Pointer arithmetic that wraps for inputs no test would think to try |
| **C# analyzers** | CI | The driver compiles clean under every .NET analyzer, on both .NET versions | Dead conditions, leaked handles, ignored results |
| **Build & smoke** | CI | The driver, all modules and the Docker images build; a guest links for every zkVM target | Renamed musl symbols, unresolved wraps, broken postprocessing |
| **Substitution check** | Build | Every C#-snippet body substitution still resolves against the runtime's CoreLib | A runtime bump that silently un-does floating-point removal |
| **`--error-on-float`** | Build, opt-in | No emitted method's *IL* contains a floating-point conversion | FP reaching the image through a `conv.r8` |
| **ISA verification** | Build, opt-in | The *linked binary* carries no F/D, compressed or atomic instruction | Everything the IL scan cannot see, including native objects |
| **Sample regression** | Pipeline | The samples still produce their known-good output | Behavioural drift in the runtime shims |
| **End-to-end proof** | Pipeline | A real workload proves correctly inside Zisk | Wrong output, load failures, proof-time regressions |

Two things that are easy to assume and are *not* true: this repository's CI
does not execute a built guest — the smoke step links one per zkVM target
and checks the ELF, nothing more — and the FP and ISA gates are flags you
pass, not something CI turns on for you.

## Contracts on the native modules

Every function in the C modules under `src/bflat/modules/` carries a
[Frama-C](https://frama-c.com/) (ACSL) contract stating what it may touch
and what it guarantees:

```console
$ python3 src/bflat/scripts/check_acsl.py
module            defined  annotated  status
eh                      4          4  ok
nofp                  105        105  ok
pal                    47         47  ok
rhp                    16         16  ok
...
```

Annotations inside macro bodies — nofp generates its 100+ trap stubs from a
macro — are only visible to Frama-C when the preprocessor keeps comments
through expansion, so the checker passes `-cpp-extra-args=-CC`. C++ modules
carry ACSL++ annotations as documentation (plain Frama-C does not parse
C++), and assembly modules are out of scope.

## Unit tests on the real ISA

The suites in `src/bflat/modules/tests/` are cross-compiled for riscv64 and
executed under qemu-user, so module code runs on the guest ISA rather than
a host approximation. That is what makes some of them possible at all:

- **The raw ZisK exit ecall is tested for real.** `a7 == 93` is also Linux
  riscv64 `__NR_exit`, so `__wrap_exit(42)` genuinely terminates a forked
  child with status 42.
- **Assembly modules are covered.** A small shim marshals the `t3/t4/t5`
  register convention that the C ABI cannot express, so the GC write
  barriers are tested directly — including their version-dependent `t3`
  post-increment, pinned from both sides.

```console
$ ./src/bflat/modules/tests/run_tests.sh
pal                137 passed, 0 failed
nofp               105 passed, 0 failed
rhp_native(net10)   26 passed, 0 failed
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
entry stops matching and the original FP-carrying body stays in the image.
The two halves of the machinery are not equally protected.

**C# snippets fail hard.** Their targets are resolved in code
(`ZkvmSubstitutions.BuildBodySubstitutions`), and a target that no longer
matches throws with the full list of mismatches rather than being skipped.
This runs when bflat is built and again on every guest build, which also
covers a layout someone modified by hand.

**ILLink substitution entries only warn.** A `<method signature=…>` that
matches nothing produces `IL2009: Could not find method …` and the build
continues, with the FP-carrying original left in place. The signature is
rendered by ILC and that rendering differs between .NET majors — .NET 11
spells a method's own generic parameters by name
(``ValueListBuilder`1<TChar>&``), earlier ILCs leave them empty — so an
entry written for one major is inert on the other. Read the build output for
IL2009, or rely on the gates rather than on the entry.

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
on both .NET 10 and .NET 11, with `-warnaserror`. `.editorconfig` decides
which rules are errors, which stay warnings and which are off, each
exemption with a reason next to it. The rules kept as errors are defect
classes — dead conditions, leaked disposables, inexact reads, ignored
results — rather than formatting preferences on code inherited from
upstream.

## Samples and the end-to-end proof

Both live in the zk-testing pipeline: nothing in `build-riscv64.yml` runs a
guest, so this is the first place a binary is actually *executed*.

The directories under
[`samples/`](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples)
are built with `--libc zisk_sim` and run under `qemu-riscv64`, with output
checked against a known-good baseline.

The real workload is Nethermind's
[StatelessExecutor](https://github.com/NethermindEth/nethermind) — an
Ethereum state-transition function in production C# — built with
`--libc zisk` and proven inside Zisk. Results land on the
[zk-testing dashboard](https://zk-testing.nethermind.dev/v2/dashboard?search=&project=1),
which tracks:

<dl class="kv">
  <dt>Proof success</dt><dd>Whether the latest commit produced a valid
    proof, or the binary failed to load, crashed, or produced output that
    didn't match the reference.</dd>
  <dt>Proof timing</dt><dd>How long Zisk takes to prove the workload. A
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
$ bflat build samples/HelloWorld/hello.cs --os linux --libc zisk_sim -x
$ qemu-riscv64 ./hello
Hello world!
```

If `--libc zisk_sim` works and the equivalent `--libc zisk` build fails
inside Zisk, the difference is almost always the postprocessor —
`--print-fn-boundaries` is the next step.
