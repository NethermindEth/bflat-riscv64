#!/bin/bash
# CBMC proof of the bump-allocator bounds properties (see verify_alloc.c).
#
# --bounds-check/--pointer-check turn the header write into a checked
# operation: the historic pointer-underflow shows up as a bounds violation
# here, not just a failed functional assert. Unsigned wrap checks stay OFF
# on purpose - the allocator detects overflow BY wrapping (guards like
# `req + 8 < req`), which is defined C the proof must not flag.
#
# Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)

set -u

VERIFY_DIR="$(cd "$(dirname "$0")" ; pwd -P)"
CBMC="${CBMC:-cbmc}"

command -v "${CBMC}" > /dev/null 2>&1 || {
	echo "error: cbmc not found (apt install cbmc)" >&2
	exit 1
}

CHECKS="--bounds-check --pointer-check --pointer-overflow-check --div-by-zero-check"

failures=0
for fn in harness_malloc harness_aligned ; do
	echo "== cbmc ${fn}"
	# Capture, then print: piping into tail would hide cbmc's exit code
	# behind tail's, turning proof failures into green runs.
	log="$(mktemp)"
	if ${CBMC} "${VERIFY_DIR}/verify_alloc.c" --function "${fn}" \
			${CHECKS} > "${log}" 2>&1 ; then
		grep -E "VERIFICATION" "${log}" || tail -3 "${log}"
	else
		echo "PROOF FAILED (${fn}):"
		grep -E "FAILURE|VERIFICATION|error" "${log}" | head -20
		failures=$((failures + 1))
	fi
	rm -f "${log}"
done

if [ "${failures}" != "0" ] ; then
	echo "cbmc: ${failures} harness(es) FAILED"
	exit 1
fi
echo "cbmc: all allocator proofs hold"
exit 0
