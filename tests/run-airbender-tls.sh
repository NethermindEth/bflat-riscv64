#!/usr/bin/env bash
set -euo pipefail
test_dir="$(cd "$(dirname "$0")" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

clang --target=riscv32-unknown-elf -march=rv32im -mabi=ilp32 \
    -c "$test_dir/airbender-tls.S" -o "$work_dir/input.o"
"${BFLAT_LD:-ld.lld}" -flavor ld -static --no-relax \
    -T "$test_dir/../src/bflat/modules/zkvm_airbender/script.ld" \
    "$work_dir/input.o" -o "$work_dir/guest"
"${BFLAT_OBJCOPY:-llvm-objcopy}" -O binary "$work_dir/guest" "$work_dir/guest.bin"
python3 - "$work_dir/guest" "$work_dir/guest.bin" <<'PY'
import struct, sys
elf, image = (open(path, 'rb').read() for path in sys.argv[1:])
phoff, = struct.unpack_from('<I', elf, 28)
entsize, count = struct.unpack_from('<HH', elf, 42)
headers = [struct.unpack_from('<8I', elf, phoff + i * entsize) for i in range(count)]
tls, = [h for h in headers if h[0] == 7]
load, = [h for h in headers if h[0] == 1 and h[2] <= tls[2] < h[2] + h[4]]
assert tls[3] == load[3] + tls[2] - load[2], 'TLS is outside the ROM copy'
assert len(image) <= 4 * 1024 * 1024, 'flat image exceeds ROM'
assert struct.unpack_from('<I', image, tls[3])[0] == 0xabcdef12, 'TLS initializer lost'
print('Airbender initialized TLS: passed')
PY
