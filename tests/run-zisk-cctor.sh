#!/usr/bin/env bash
set -euo pipefail

test_dir="$(cd "$(dirname "$0")" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

bflat build --arch riscv64 --os linux --stdlib dotnet --libc zisk \
    --no-pie --no-pthread -Ot -o "$work_dir/cctor.elf" "$test_dir/zisk-cctor.cs"
ziskemu -e "$work_dir/cctor.elf" -n 1000000 --steps -o "$work_dir/output.bin"
python3 - "$work_dir/output.bin" <<'PY'
import pathlib
import sys

assert pathlib.Path(sys.argv[1]).read_bytes()[:8] == bytes.fromhex("0100000053534150"), "guest did not report success"
PY
