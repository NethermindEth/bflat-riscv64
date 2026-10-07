#!/usr/bin/env bash
# OpenVM and ZisK execute misaligned loads and stores natively, so Unsafe.ReadUnaligned must stay
# one `ld`; SP1 rejects them, so its build must assemble the word from bytes.
set -euo pipefail

test_dir="$(cd "$(dirname "$0")" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

status=0
for libc in zisk openvm sp1; do
    bflat build --arch riscv64 --os linux --stdlib dotnet --libc "$libc" \
        --no-pie --no-pthread -Ot -o "$work_dir/$libc.elf" "$test_dir/unaligned-access.cs"
    llvm-objdump -d --no-show-raw-insn "$work_dir/$libc.elf" > "$work_dir/$libc.dis"
    body="$(awk '/^[0-9a-f]+ <.*ReadWord.*>:$/ { on = 1; next } on && /^$/ { exit } on' "$work_dir/$libc.dis")"
    [ -n "$body" ] || { echo "$libc: ReadWord not found" >&2; exit 1; }
    byte_loads="$(grep -c -w lbu <<< "$body" || true)"
    if [ "$libc" = sp1 ]; then
        expected="byte-wise"; [ "$byte_loads" -gt 0 ] || status=1
    else
        expected="wide"; { [ "$byte_loads" -eq 0 ] && grep -q -w ld <<< "$body"; } || status=1
    fi
    echo "$libc: $byte_loads lbu, expected $expected"
done
exit $status
