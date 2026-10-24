#!/usr/bin/env bash
set -euo pipefail

test_dir="$(cd "$(dirname "$0")" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
# bflat knows no zkVM: each target is a platform in its bflat-* package.
# BFLAT_PLATFORMS is a directory with the platform files (platform/build.sh of
# bflat-libziskos, bflat-sp1 and bflat-openvm can all write into one).
platforms="${BFLAT_PLATFORMS:?set BFLAT_PLATFORMS to a directory with the platform manifests}"

bflat build --arch riscv64 --os linux --stdlib dotnet --libc zisk \
    --extlib "$platforms/zisk.bflat.manifest" --no-pie --no-pthread -Ot -o "$work_dir/hash-helpers.elf" "$test_dir/zisk-hash-helpers.cs"
ziskemu -e "$work_dir/hash-helpers.elf" -n 10000000 --steps -o "$work_dir/output.bin"
python3 - "$work_dir/output.bin" <<'PY'
import pathlib
import sys

assert pathlib.Path(sys.argv[1]).read_bytes()[:8] == bytes.fromhex("0100000053534150"), "guest did not report success"
PY
