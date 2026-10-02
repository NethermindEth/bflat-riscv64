#!/usr/bin/env bash
set -euo pipefail

test_dir="$(cd "$(dirname "$0")" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

bflat build --arch riscv64 --os linux --stdlib dotnet --libc zisk \
    --no-pie --no-pthread -Ot -o "$work_dir/bitmanip.elf" "$test_dir/zisk-bitmanip.cs"
ziskemu -e "$work_dir/bitmanip.elf" -n 1000000 --steps
