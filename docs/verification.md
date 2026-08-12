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

Where each one runs matters, so the table says so explicitly. **CI** means a
job in this repository's
[`build-riscv64.yml`](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml),
which fires on every push and pull request. **Build** means it is part of
building bflat or a guest, wherever that happens. **Pipeline** means the
separate zk-testing pipeline, which is not driven from this repository.

| Layer | Runs in | What it proves | What it catches |
|-------|---------|----------------|-----------------|
| **ACSL contracts** | CI | Every function in the C modules carries a formal contract and parses under Frama-C | A new function landing without a specification; a module that stops parsing |
| **Module unit tests** | CI | Each module function behaves as specified, executing on the real ISA under qemu | Wrong return values, off-by-one stores, broken register contracts |
| **Fuzzing** | CI | The two input-driven components survive arbitrary input under ASan/UBSan | Memory errors and arithmetic UB in the printf parser and the allocator |
| **CBMC proof** | CI | The allocator's bounds properties hold for *all* 64-bit sizes | Pointer arithmetic that wraps for inputs no test would think to try |
| **C# analyzers** | CI | The driver compiles clean under every .NET analyzer, on both .NET versions | Dead conditions, leaked handles, ignored results |
| **Build & smoke** | CI | The driver, all modules and the Docker images build; a guest links for both zkVM targets | Renamed musl symbols, unresolved wraps, broken postprocessing |
| **Substitution check** | Build | Every C#-snippet body substitution still resolves against the runtime's CoreLib | A runtime bump that silently un-does floating-point removal |
| **`--error-on-float`** | Build, opt-in | No emitted method's *IL* contains a floating-point conversion | FP reaching the image through a `conv.r8` |
| **ISA verification** | Build, opt-in | The *linked binary* carries no F/D, compressed or atomic instruction | Everything the IL scan cannot see, including native objects |
| **Sample regression** | Pipeline | The samples still produce their known-good output | Behavioural drift in the runtime shims |
| **End-to-end proof** | Pipeline | A real workload proves correctly inside Zisk | Wrong output, load failures, proof-time regressions |

Two things that are easy to assume and are *not* true: the repository's CI
does not execute a built guest — the smoke step links one for each zkVM
target and checks the ELF, nothing more — and the FP and ISA gates are flags
you pass, not something CI turns on for you.

## Contracts on the native modules

Every function in the C modules under `src/bflat/modules/` carries a
[Frama-C](https://frama-c.com/) (ACSL) contract stating what it may touch
and what it guarantees. The CI gate refuses a module that stops parsing or
a defined function without a contract:

```console
$ python3 src/bflat/scripts/check_acsl.py
module            defined  annotated  status
gs_cookie               0          0  ok
nofp                  105        105  ok
pal                    47         47  ok
rhp                    16         16  ok
...
```

One detail worth knowing when editing: annotations inside macro bodies —
nofp generates its 100+ trap stubs from a macro — are only visible to
Frama-C when the preprocessor keeps comments through expansion, so the
checker passes `-cpp-extra-args=-CC`. C++ modules carry ACSL++ annotations
as documentation (plain Frama-C does not parse C++), and assembly modules
are out of scope.

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
built for the host with ASan and UBSan:

```console
$ FUZZ_TIME=60 ./src/bflat/modules/tests/fuzz/run_fuzz.sh
```

The allocator target drives random operation sequences — malloc, realloc,
calloc, aligned_alloc, mark, reset — with unconstrained 64-bit sizes, and
asserts the allocator invariants after every step.

On top of that, `tests/verify/` proves the allocator's bounds properties
with [CBMC](https://www.cbmc.diffblue.com/) for *all* sizes and reachable
states rather than sampled ones. The harness includes the real
`pal/module.c` with its heap-window symbols re-pointed at a local array.
The proof is mutation-tested: restoring a pre-fix bounds check makes CBMC
report the wild write, so a passing run is not vacuous.

The `eh` module — the synthetic program headers that make managed exception
handling work at all — is proved outright rather than sampled:

```console
$ docker run --rm -v "$PWD":/w -w /w framac/frama-c:30.0 \
      src/bflat/modules/eh/verify/run_wp.sh
```

Frama-C/WP discharges every contract of the code that builds the answer,
runtime-error guards included, and Eva then runs the module end to end —
dispatcher and all — in both shapes an image can have, unwind tables kept
and stripped, and must report zero alarms with no logical property left
unknown. The split exists because WP cannot reason about a call through a
caller-supplied function pointer, which is all the dispatcher does once the
block is built; Eva resolves that pointer against the driver's callback and
covers it. CI runs the gate on every push.

## Keeping the substitutions honest

The [ILC-stage substitutions](architecture.md#stage-15--ilc-stage-substitutions)
are the layer most exposed to runtime drift, and the failure is silent by
nature: if a substituted CoreLib method is renamed or re-signatured, the
original FP-carrying body simply stays in the image. That has happened —
`Number.FormatFloat`'s signature changed in .NET 11, the entry stopped
matching, and floating point came back.

The two halves of the substitution machinery are not equally protected, and
the difference is worth knowing before you rely on either.

**C# snippets fail hard.** Their targets are resolved in code
(`ZkvmSubstitutions.BuildBodySubstitutions`), and a target that no longer
matches throws with the full list of mismatches rather than being skipped.
This runs twice: once when bflat is built — the CoreLib guests will use is
already unpacked into the layout — and again on every guest build, which
also covers a layout someone modified by hand.

**ILLink substitution entries only warn.** A `<method signature=…>` that
matches nothing produces `IL2009: Could not find method …` and the build
continues, with the FP-carrying original left in place. Worse, the
signature is rendered by ILC and the rendering differs between .NET majors:
.NET 11 spells a method's own generic parameters by name
(``ValueListBuilder`1<TChar>&``), earlier ILCs leave them empty. An entry
written for one major is therefore silently inert on the other — as
`Number.FormatFloat` is on .NET 10 today. Read the build output for IL2009,
or rely on the gates below rather than on the entry.

That is what the gates are for, and there are two of them because one is not
enough:

- **`--error-on-float`** scans every *emitted* method's IL for floating-point
  conversions. Methods that carry a conversion inside a block the target
  provably never runs are listed explicitly and reported as warnings rather
  than failures.
- **`--error-on-float-binary`, `--error-on-compressed`, `--error-on-atomic`**
  decode the *linked binary* and fail on any F/D, compressed or atomic
  instruction, naming the offending symbol.

The binary gates are the load-bearing ones. The IL scan cannot see a
comparison — `ceq` over two `float64`s carries no `conv.r8` — so
`Double.CompareTo`/`Equals` kept lowering to `feq.d`/`flt.d` while the IL
gate reported a clean image. Nor can it see native code at all: the runtime's
own objects, the GC and llvm-libunwind among them, are compiled outside ILC.
Both classes have shipped FP into a guest; both are caught by decoding what
was actually linked.

## Analyzers on the driver

The C# driver builds with every .NET analyzer enabled (`AnalysisMode=All`)
on both .NET 10 and .NET 11, with `-warnaserror`. `.editorconfig` decides
which rules are errors, which stay warnings, and which are off — each
exemption with a reason next to it. The rule set is tiered rather than
maximal on purpose: at full strictness the codebase reports ~1100
diagnostics, the overwhelming majority formatting preferences on code
inherited from upstream. Escalating those would mean a thousand mechanical
edits and zero bugs found; the rules kept as errors are defect classes —
dead conditions, leaked disposables, inexact reads, ignored results.

## Samples and the end-to-end proof

Both of these live in the zk-testing pipeline rather than in this
repository's workflow — nothing in `build-riscv64.yml` runs a guest, so the
first place a binary is actually *executed* is there.

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

```console
$ ./src/bflat/modules/tests/run_tests.sh        # unit tests (qemu)
$ FUZZ_TIME=60 ./src/bflat/modules/tests/fuzz/run_fuzz.sh
$ ./src/bflat/modules/tests/verify/run_verify.sh   # CBMC proofs
$ python3 src/bflat/scripts/check_acsl.py       # contract gate
```

[BUILDING.md](https://github.com/NethermindEth/bflat-riscv64/blob/master/BUILDING.md)
lists what each needs. Two environment quirks are worth knowing up front:
Frama-C is packaged as `frama-c-base` on Debian bookworm and absent from
recent Ubuntu archives, and on Apple silicon (Docker via Rosetta) the
designed-fault tests need `TEST_SKIP_FAULTS=1` — nested emulation hangs on
guest faults instead of delivering the signal.

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
