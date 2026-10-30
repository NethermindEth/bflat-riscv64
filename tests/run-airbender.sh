#!/usr/bin/env bash
# Runs the guests of tests/build-airbender.sh (in GUESTS) on the Airbender
# simulator (AIRBENDER_CLI: the cli of matter-labs/zksync-airbender, v3) and
# checks what each one did:
#   hello   prints its line through the UART and ends as a success;
#   keccak  publishes keccak256 of its input in x10-x17;
#   fail    ends as a failure, which the simulator rejects.
# The simulator treats any self-jump as the end of a run, and reports the
# cycle limit only in its output, so both are checked here.
set -euo pipefail

cli="${AIRBENDER_CLI:?set AIRBENDER_CLI to the Airbender cli}"
guests="${GUESTS:?set GUESTS to the directory of the guests}"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

# The input as the simulator reads it: the byte length, then the bytes as
# little-endian words, each word as 8 hex digits of its value.
input() {
    python3 -c 'import struct, sys
d = sys.argv[1].encode(); n = len(d); d += bytes(-n % 4)
print("".join(f"{x:08x}" for x in [n, *struct.unpack(f"<{len(d) // 4}I", d)]), end="")' "$1"
}

run() {
    local guest="$1" input_file="$2"
    shift 2
    "$cli" run --bin "$guests/$guest.bin" --text "$guests/$guest.text" --input-file "$input_file" \
        --cycles 2000000000 "$@" > "$work_dir/$guest.log" 2>&1
}

finished() {
    ! grep -q 'did not finish' "$work_dir/$1.log" && grep -q '^Result: ' "$work_dir/$1.log"
}

status=0

input "" > "$work_dir/empty.hex"
if run hello "$work_dir/empty.hex" && finished hello && grep -q 'UART: `Hello from Airbender' "$work_dir/hello.log"; then
    echo "hello: OK ($(grep '^Result: ' "$work_dir/hello.log"))"
else
    echo "hello: FAILED"; cat "$work_dir/hello.log"; status=1
fi

# keccak256("abc"), as the words of x10-x17.
expected="$(python3 -c 'import struct
h = bytes.fromhex("4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45")
print(",".join(str(w) for w in struct.unpack("<8I", h)))')"
input "abc" > "$work_dir/abc.hex"
if run keccak "$work_dir/abc.hex" --expected-results "$expected" && finished keccak; then
    echo "keccak: OK ($(grep '^Result: ' "$work_dir/keccak.log"))"
else
    echo "keccak: FAILED, expected x10-x17 = $expected"; cat "$work_dir/keccak.log"; status=1
fi

if run fail "$work_dir/empty.hex"; then
    echo "fail: FAILED: the run of a guest that returned 3 ended as a success"; cat "$work_dir/fail.log"; status=1
elif grep -q 'Illegal instruction' "$work_dir/fail.log"; then
    echo "fail: OK (rejected as no circuit can prove it)"
else
    echo "fail: FAILED: not the cycle CSR write"; cat "$work_dir/fail.log"; status=1
fi

exit $status
