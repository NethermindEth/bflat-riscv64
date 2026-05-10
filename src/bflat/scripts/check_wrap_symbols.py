#!/usr/bin/python3
"""
Verify that every --wrap=SYMBOL flag passed to the linker resolves to a symbol
that actually appears in one of the input object files / static archives.

Invocation: pass the full ld argument list (the same string given to the
linker). The script picks every --wrap=SYMBOL token plus every positional
argument that looks like an object file or static archive (.o, .obj, .a) and
asks `nm` for the union of symbols across those inputs. Any --wrap target
that is not present in that set is reported as "bad" on stdout. A non-empty
list of bad symbols yields a non-zero exit, which the caller turns into a
build failure.

Optional flags accepted *before* the ld arguments:
  --nm <path>   nm-like tool to use (default: search riscv64-linux-gnu-nm,
                llvm-nm, nm in $PATH).
  --            end-of-options separator; everything after is treated as ld args.
"""

import os
import subprocess
import sys


def find_nm():
    candidates = ["riscv64-linux-gnu-nm", "llvm-nm", "nm"]
    for nm in candidates:
        try:
            r = subprocess.run(
                [nm, "--version"], capture_output=True, text=True, check=False
            )
            if r.returncode == 0:
                return nm
        except FileNotFoundError:
            continue
    return None


def collect_symbols(nm_bin, paths):
    syms = set()
    for p in paths:
        try:
            r = subprocess.run(
                [nm_bin, "--no-sort", "--print-armap", p],
                capture_output=True, text=True, check=False,
            )
        except Exception as e:
            print(f"WARN: cannot run nm on {p}: {e}", file=sys.stderr)
            continue
        if r.returncode != 0:
            print(f"WARN: nm failed on {p}: {r.stderr.strip()}", file=sys.stderr)
            continue
        for ln in r.stdout.splitlines():
            ln = ln.rstrip()
            if not ln or ln.endswith(":"):
                continue
            if ln.startswith("Archive map") or ln.startswith("Archive index"):
                continue
            parts = ln.split()
            if not parts:
                continue
            # --print-armap line: "<symbol> in <member>"
            if len(parts) >= 3 and parts[1] == "in":
                syms.add(parts[0])
                continue
            # standard nm line:
            #   "<addr> <type> <name>"   defined
            #   "        U <name>"       undefined reference (still a known symbol)
            syms.add(parts[-1])
    return syms


def parse_args(argv):
    nm_bin = None
    rest = []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--nm" and i + 1 < len(argv):
            nm_bin = argv[i + 1]
            i += 2
            continue
        if a == "--":
            rest.extend(argv[i + 1 :])
            break
        rest.append(a)
        i += 1
    return nm_bin, rest


def main():
    nm_bin, raw = parse_args(sys.argv[1:])

    wrap_syms = []
    inputs = []
    for tok in raw:
        t = tok.strip().strip('"').strip("'")
        if not t:
            continue
        if t.startswith("--wrap="):
            wrap_syms.append(t[len("--wrap="):])
        elif t.lower().endswith((".o", ".obj", ".a")):
            if os.path.isfile(t):
                inputs.append(t)
            else:
                print(f"WARN: input not on disk: {t}", file=sys.stderr)

    if not wrap_syms:
        print("check_wrap_symbols: no --wrap= flags, nothing to check.")
        return 0

    nm_bin = nm_bin or find_nm()
    if nm_bin is None:
        print(
            "ERROR: no usable nm binary found "
            "(tried riscv64-linux-gnu-nm, llvm-nm, nm)",
            file=sys.stderr,
        )
        return 2

    if not inputs:
        print(
            "ERROR: no input object files / archives found in linker arguments",
            file=sys.stderr,
        )
        return 2

    known = collect_symbols(nm_bin, inputs)
    seen = set()
    missing = []
    for s in wrap_syms:
        if s in seen:
            continue
        seen.add(s)
        if s not in known:
            missing.append(s)

    if missing:
        print("ERROR: --wrap= targets not found in any linker input:")
        for s in missing:
            print(f"  {s}")
        return 1

    print(
        f"check_wrap_symbols: all {len(seen)} --wrap= targets "
        "resolve to real symbols."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
