#!/bin/bash
# libFuzzer smoke run for the pal/rust_sys fuzz targets.
#
# Host-compiled (the modules are plain C; pal's only riscv asm is behind
# an #if defined(__riscv) guard) with ASan+UBSan, non-recoverable: any
# finding aborts the run and fails CI.
#
# FUZZ_TIME (seconds per target, default 60) scales the run.
#
# Corpus layout: seeds/<target> holds a handful of hand-written inputs and
# is READ-ONLY (tracked in git). The corpus libFuzzer grows goes to
# out/corpus-<target>, which is gitignored - libFuzzer writes every new
# interesting unit into its FIRST corpus argument, so pointing that at a
# tracked directory turns one local run into thousands of new files.
#
# Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)

set -u

FUZZ_DIR="$(cd "$(dirname "$0")" ; pwd -P)"
MOD_DIR="$(dirname "$(dirname "${FUZZ_DIR}")")"
OUT_DIR="${FUZZ_DIR}/out"
CC="${CC:-clang}"
FUZZ_TIME="${FUZZ_TIME:-60}"

FLAGS="-O1 -g -fsanitize=fuzzer,address,undefined -fno-sanitize-recover=all"

command -v "${CC}" > /dev/null 2>&1 || {
	echo "error: ${CC} not found" >&2
	exit 1
}

mkdir -p "${OUT_DIR}"
failures=0

build_and_run()
{
	local name="$1" ; shift
	if ! ${CC} ${FLAGS} "$@" -o "${OUT_DIR}/${name}" ; then
		echo "BUILD FAIL: ${name}"
		failures=$((failures + 1))
		return
	fi
	local target="${name#fuzz_}"
	mkdir -p "${OUT_DIR}/corpus-${target}"
	# First dir = writable corpus, second = read-only seeds.
	if ! "${OUT_DIR}/${name}" -max_total_time="${FUZZ_TIME}" \
			-print_final_stats=1 \
			"${OUT_DIR}/corpus-${target}" \
			"${FUZZ_DIR}/seeds/${target}" ; then
		echo "FUZZ FAIL: ${name} (crash artifact in $(pwd))"
		failures=$((failures + 1))
	fi
}

build_and_run fuzz_vfprintf \
	"${FUZZ_DIR}/fuzz_vfprintf.c" "${MOD_DIR}/pal/module.c" \
	-Wl,--defsym,_kernel_heap_top=_kernel_heap_bottom+4096

build_and_run fuzz_alloc \
	"${FUZZ_DIR}/fuzz_alloc.c" "${MOD_DIR}/pal/module.c" \
	"${MOD_DIR}/rust_sys/module.c" \
	-Wl,--defsym,_kernel_heap_top=_kernel_heap_bottom+1048576

echo "--------------------------------------------------"
if [ "${failures}" != "0" ] ; then
	echo "fuzz smoke: ${failures} target(s) FAILED"
	exit 1
fi
echo "fuzz smoke: all targets clean (${FUZZ_TIME}s each)"
exit 0
