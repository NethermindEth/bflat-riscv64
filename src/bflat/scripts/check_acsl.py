#!/usr/bin/env python3
"""ACSL contract gate for src/bflat/modules.

Two checks over every C module (module.c):
  1. Parse: the file, including its ACSL annotations, must go through
     Frama-C without errors. Annotations living inside macro bodies (nofp)
     require comment-preserving preprocessing, hence -cpp-extra-args=-CC.
  2. Coverage: every DEFINED function must carry an ACSL contract. The
     defined-function list comes from `frama-c -metrics`; a function counts
     as annotated when its name appears in the normalized `-print` output
     directly after a spec block (`*/` ... name `(` with no statement
     boundary in between).

C++ modules (Frama-C proper does not parse C++) and assembly modules are
out of scope; they are listed in SKIPPED for visibility.

Local run (needs docker):
  docker run --rm --platform linux/amd64 -v "$PWD:/w" -w /w debian:bookworm \
    bash -c "apt-get update -qq && apt-get install -y -qq frama-c-base gcc \
             python3 >/dev/null && python3 src/bflat/scripts/check_acsl.py"
"""

import re
import subprocess
import sys
from pathlib import Path

MODULES_DIR = Path(__file__).resolve().parent.parent / "modules"

FRAMA_C_ARGS = ["-cpp-extra-args=-CC", "-machdep", "gcc_x86_64"]

# Modules Frama-C cannot process: C++ (needs Frama-Clang) and assembly.
SKIPPED = {
    "stdcppshim": "C++ (ACSL++ annotations are documentation-only)",
    "ubootstrap": "C++ (ACSL++ annotations are documentation-only)",
    "rhp_native": "assembly",
    "zkvm_zisk": "assembly",
    "zkvm_zisk_sim": "assembly",
    "zisk_subst": "no native sources (ILC substitutions module)",
    "ugc-zero": "prebuilt objects only",
}


def run_frama_c(extra: list, source: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["frama-c", *FRAMA_C_ARGS, *extra, str(source)],
        capture_output=True,
        text=True,
    )


def defined_functions(source: Path) -> list:
    """Names of functions DEFINED in the file, from frama-c -metrics."""
    proc = run_frama_c(["-metrics"], source)
    if proc.returncode != 0:
        raise RuntimeError(f"frama-c -metrics failed:\n{proc.stdout}{proc.stderr}")
    out = proc.stdout
    m = re.search(
        r"Defined functions \((\d+)\)\s*\n\s*=+\s*\n(.*?)\n\s*Specified-only functions",
        out,
        re.S,
    )
    if not m:
        raise RuntimeError(f"cannot find 'Defined functions' in metrics:\n{out}")
    count = int(m.group(1))
    if count == 0:
        return []
    names = []
    for chunk in m.group(2).split(";"):
        chunk = chunk.strip()
        nm = re.match(r"([A-Za-z_]\w*)", chunk)
        if nm:
            names.append(nm.group(1))
    if len(names) != count:
        raise RuntimeError(
            f"metrics parse mismatch: header says {count} functions, "
            f"parsed {len(names)}: {names}"
        )
    return names


def annotated_functions(source: Path, names: list) -> set:
    """Subset of `names` whose definition/declaration in the normalized AST
    is directly preceded by a spec block."""
    proc = run_frama_c(["-print"], source)
    if proc.returncode != 0:
        raise RuntimeError(f"frama-c -print failed:\n{proc.stdout}{proc.stderr}")
    text = proc.stdout
    annotated = set()
    for name in names:
        # `*/` followed by the function header with no ';', '{', '}' between:
        # whitespace, return type, storage class and attributes are allowed.
        # The printer may parenthesize an attributed declarator, e.g.
        # `( __attribute__((__noinline__)) nofp_trap)(void)`, hence the
        # optional `)` between the name and its parameter list.
        if re.search(r"\*/[^;{}]*?\b" + re.escape(name) + r"\s*\)?\s*\(", text):
            annotated.add(name)
    return annotated


def main() -> int:
    failures = []
    print(f"{'module':<16} {'defined':>8} {'annotated':>10}  status")
    print("-" * 50)

    for moddir in sorted(MODULES_DIR.iterdir()):
        if not moddir.is_dir():
            continue
        name = moddir.name
        if name in SKIPPED:
            print(f"{name:<16} {'-':>8} {'-':>10}  skipped ({SKIPPED[name]})")
            continue
        source = moddir / "module.c"
        if not source.exists():
            print(f"{name:<16} {'-':>8} {'-':>10}  skipped (no module.c)")
            continue

        # 1. Parse gate.
        proc = run_frama_c([], source)
        errors = [
            ln
            for ln in (proc.stdout + proc.stderr).splitlines()
            if re.search(r"error|fatal|abort", ln, re.I)
        ]
        if proc.returncode != 0 or errors:
            failures.append(f"{name}: Frama-C parse failed")
            detail = "\n    ".join(errors[:10]) or proc.stderr.strip()[:500]
            print(f"{name:<16} {'-':>8} {'-':>10}  PARSE FAIL\n    {detail}")
            continue

        # 2. Coverage gate.
        defined = defined_functions(source)
        annotated = annotated_functions(source, defined)
        missing = sorted(set(defined) - annotated)
        status = "ok" if not missing else "MISSING CONTRACTS"
        print(f"{name:<16} {len(defined):>8} {len(annotated):>10}  {status}")
        if missing:
            failures.append(f"{name}: functions without ACSL contracts: {', '.join(missing)}")
            for fn in missing:
                print(f"    missing: {fn}")

    print("-" * 50)
    if failures:
        print("FAILED:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("All modules pass the ACSL gate.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
