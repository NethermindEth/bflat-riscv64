#!/usr/bin/env bash
# Builds the Airbender test guests (tests/airbender-*.cs) into OUT, each as the
# ELF and the flat images Airbender loads (<guest>.bin, <guest>.text).
# BFLAT_AIRBENDER is a directory with the bflat-airbender package: its platform
# and libairbender (build/build.sh airbender, or a release archive).
# tests/run-airbender.sh runs them.
set -euo pipefail

test_dir="$(cd "$(dirname "$0")" && pwd)"
package="${BFLAT_AIRBENDER:?set BFLAT_AIRBENDER to a directory with the bflat-airbender package}"
out="${OUT:?set OUT to the output directory}"
mkdir -p "$out"

for guest in hello keccak fail; do
    bflat build --arch riscv32 --os linux --stdlib dotnet --libc airbender \
        --extlib "$package/libairbender.bflat.manifest" --no-pie --no-pthread \
        -o "$out/$guest" "$test_dir/airbender-$guest.cs"
    for f in "$out/$guest" "$out/$guest.bin" "$out/$guest.text"; do
        [ -s "$f" ] || { echo "$guest: $f was not written" >&2; exit 1; }
    done
    file "$out/$guest" | grep -q 'ELF 32-bit LSB executable, UCB RISC-V' \
        || { echo "$guest: not an RV32 executable: $(file "$out/$guest")" >&2; exit 1; }
    echo "$guest: $(stat -c %s "$out/$guest.bin") bytes of ROM image"
done
