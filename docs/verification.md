---
layout: default
title: Verification
eyebrow: How we know it works
lead: >
  Every change goes through three independent gates: a source build, a
  sample regression run, and an end-to-end zkVM proof. This page explains
  what each one tests and where to read the results.
prev: /build/
---

<div style="background: var(--bg-elev-1); border: 1px solid var(--border); border-radius: var(--radius-lg); padding: 24px; margin: 16px 0 32px;">
  {% include verify-flow.html %}
</div>

## Three gates, three failure modes

| Gate | What it catches | Where to read it |
|------|-----------------|------------------|
| **Source build** (`build-riscv64.yml`) | Renamed musl/runtime symbols, unresolved wraps, broken module compilation, missing toolchain. | [Actions tab](https://github.com/NethermindEth/bflat-riscv64/actions/workflows/build-riscv64.yml) |
| **Sample regression** | Behavioural drift in the runtime shims — a soft-float helper that suddenly gets reached, an allocator that starts handing out misaligned pointers. | [zk-testing dashboard](https://zk-testing.nethermind.dev) |
| **End-to-end proof** | Real workload regressions: a state-transition function that produces wrong output, a proof that takes 10× longer to generate, a binary that no longer loads in the prover. | [zk-testing dashboard](https://zk-testing.nethermind.dev) project 1 |

The README badges at the top of the repository surface the latest status
of each gate.

## Gate 1 — `build-riscv64.yml`

The GitHub Actions workflow does the same thing `build.sh all riscv64`
does locally:

```yaml
- run: ./build.sh modules riscv64
- run: ./build.sh bflat   riscv64
- run: ./build.sh layouts riscv64
```

It runs in the project's
[`Dockerfile.build`](https://github.com/NethermindEth/bflat-riscv64/blob/master/Dockerfile.build)
image, which contains the full RISC-V64 cross-toolchain, the .NET SDK,
and the Python dependencies for `patch_elf.py` (LIEF and pyelftools).
Each module compiles with its own command line:

- `module.c` → `riscv64-linux-gnu-gcc -march=rv64imad`
- `module.S` → `riscv64-linux-gnu-as --march=rv64ima --mabi=lp64`
- `module.cpp` → `riscv64-linux-gnu-g++ -march=rv64imad`

Failure modes this gate catches:

- A wrap target that no longer exists in upstream musl (the linker
  produces an undefined-symbol error during the layouts build).
- A module that fails to compile because a header changed (immediate
  compiler error).
- A python dependency drift in `patch_elf.py` (caught when the layouts
  pipeline tries to invoke the postprocessor).
- A change to `BuildCommand.cs` that breaks the cross-architecture path
  for x86-hosted builds (the layouts target builds both Linux- and
  Windows-hosted variants).

## Gate 2 — Sample regression

Every directory under [`samples/`](https://github.com/NethermindEth/bflat-riscv64/tree/master/samples)
is built with `--libc zisk_sim` and run under `qemu-riscv64`. Output is
checked against a known-good baseline.

## Gate 3 — End-to-end zkVM proofs

The Nethermind
[StatelessExecutor](https://github.com/NethermindEth/nethermind) — a
real Ethereum state-transition function written in real production C# —
is built with `--libc zisk` and proven inside Zisk on every commit.
Results are pushed to the
[zk-testing dashboard](https://zk-testing.nethermind.dev/v2/dashboard?search=&project=1).

The dashboard surfaces three things:

<dl class="kv">
  <dt>Proof success</dt><dd>A green badge on the README means the latest
    commit produced a valid proof. A red badge means the binary either
    failed to load, crashed during execution, or produced output that
    didn't match the canonical reference.</dd>
  <dt>Proof timing</dt><dd>How long Zisk takes to prove the workload. A
    sudden regression usually means a runtime shim started doing more
    work — for example, the bump allocator hitting a different code path
    that triggers many more allocations.</dd>
  <dt>Binary size</dt><dd>The size of the postprocessed ELF. Tracks the
    cost of the workload in prover steps and is a leading indicator for
    the previous metric.</dd>
</dl>

## Manual smoke tests

Before any release, the maintainers run a short manual checklist that
covers cases CI can't easily exercise:

- Build a fresh layout from a clean checkout in the Docker image.
- Build [`bflat-libziskos`](https://github.com/NethermindEth/bflat-libziskos)
  and link it into a sample with `--extlib`.
- Smoke-test the resulting binary under both `zisk_sim` (QEMU) and the
  Zisk prover.
- Walk a `--print-fn-boundaries` report by eye looking for unexpected
  function shapes (anything where EH and objdump disagree by more than
  a single instruction is investigated).
- Verify the symbol-size HTML chart (`--symchart`) for the Nethermind
  state-transition build, watching for sudden jumps in any single
  module's contribution.

## Reading the regression dashboard

The badge at the top of the README links to the project page on
`zk-testing.nethermind.dev`. The page shows:

- Per-commit proof status (success / failure / not yet attempted).
- A trendline of proof generation time over the last N commits.
- The diff between the current commit's output and the reference.
- Build artifacts: the `.elf`, the `.symchart.html`, and the
  `.print-fn-boundaries.txt` for inspection.

If a commit regresses any gate, the bisect is usually a one-step
operation against the diff in the dashboard. The combination of a
loud, deterministic failure mode (gate 2) and a precise quantitative
signal (gate 3) means even subtle behavioural drifts have a place to
surface.

## When in doubt

Run the simulator path locally:

```console
$ bflat build samples/HelloWorld/hello.cs --os linux --libc zisk_sim -x
$ qemu-riscv64 ./hello
Hello world!
```

If `--libc zisk_sim` works and the equivalent `--libc zisk` build
crashes inside Zisk, the difference is almost always in the
postprocessor — `--print-fn-boundaries` is the next step.
