#!/bin/bash
# Unit tests for the native modules in src/bflat/modules.
#
# Everything is cross-compiled for riscv64 (glibc, static) and executed
# under qemu-user, so the tests run the module code on the REAL guest ISA -
# including the raw ZisK exit ecall, which qemu maps to Linux __NR_exit
# (both are 93 on riscv64).
#
# Requirements: gcc-riscv64-linux-gnu, g++-riscv64-linux-gnu,
#               qemu-user-static (or qemu-user / qemu-user-hwe).
#
# TEST_SKIP_FAULTS=1 skips the designed-fault (SIGSEGV) tests: qemu-user
# under nested emulation (Docker Rosetta on Apple silicon) hangs on guest
# faults instead of delivering the signal. CI on real x86_64 runs them.
#
# Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)

set -u

TESTS_DIR="$(cd "$(dirname "$0")" ; pwd -P)"
MOD_DIR="$(dirname "${TESTS_DIR}")"
OUT_DIR="${TESTS_DIR}/out"

CC="${CC:-riscv64-linux-gnu-gcc}"
CXX="${CXX:-riscv64-linux-gnu-g++}"
# Same toolchain prefix as CC; used to rename noos's symbols (see below).
NM="${NM:-${CC%gcc}nm}"
OBJCOPY="${OBJCOPY:-${CC%gcc}objcopy}"

# Native riscv64 needs no emulator; anywhere else pick a qemu-user binary.
if [ "$(uname -m)" = "riscv64" ] ; then
	RUN=""
elif command -v qemu-riscv64-static > /dev/null 2>&1 ; then
	RUN="qemu-riscv64-static"
elif command -v qemu-riscv64 > /dev/null 2>&1 ; then
	RUN="qemu-riscv64"
else
	echo "error: need qemu-riscv64-static (apt install qemu-user-static)" >&2
	exit 1
fi

command -v "${CC}" > /dev/null 2>&1 || {
	echo "error: ${CC} not found (apt install gcc-riscv64-linux-gnu)" >&2
	exit 1
}

# -static -no-pie: deterministic addresses (the tls/pal tests use --defsym
# absolute symbols) and no runtime library needs under qemu.
CFLAGS="-O1 -g -static -no-pie -std=gnu2x -I${TESTS_DIR} -I${OUT_DIR}"
CXXFLAGS="-O1 -g -static -no-pie -I${TESTS_DIR} -I${OUT_DIR}"

mkdir -p "${OUT_DIR}"
failures=0

# Per-suite wall-clock cap. Every suite finishes in seconds; the cap is
# there so a hang fails the run instead of sitting until the CI job's own
# timeout. Guest faults are the realistic hang: qemu-user under nested
# emulation (Docker Rosetta) never delivers them, and TEST_SKIP_FAULTS
# exists for that - but a cap means an unexpected hang anywhere still
# reports rather than stalls.
SUITE_TIMEOUT="${SUITE_TIMEOUT:-300}"

run_one()
{
	local name="$1" ; shift
	if ! "$@" -o "${OUT_DIR}/${name}" ; then
		echo "BUILD FAIL: ${name}"
		failures=$((failures + 1))
		return
	fi
	timeout -s KILL "${SUITE_TIMEOUT}" ${RUN} "${OUT_DIR}/${name}"
	local rc=$?
	if [ "${rc}" = "137" ] ; then
		echo "TIMEOUT (${SUITE_TIMEOUT}s): ${name}"
		failures=$((failures + 1))
	elif [ "${rc}" != "0" ] ; then
		failures=$((failures + 1))
	fi
}

# nofp's stub list is generated from the module source, so a stub added to
# the module cannot silently miss test coverage.
grep -E '^NOFP_STUB\(' "${MOD_DIR}/nofp/module.c" \
	| sed -E 's/NOFP_STUB\(([A-Za-z0-9_]+)\).*/X(\1)/' \
	> "${OUT_DIR}/nofp_list.h"
grep -E '^NOFP_WRAP_STUB\(' "${MOD_DIR}/nofp/module.c" \
	| sed -E 's/NOFP_WRAP_STUB\(([A-Za-z0-9_]+)\).*/X(__wrap_\1)/' \
	>> "${OUT_DIR}/nofp_list.h"

run_one test_gs_cookie \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_gs_cookie.c" \
	"${MOD_DIR}/gs_cookie/module.c"

run_one test_rng_stupid \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_rng_stupid.c" \
	"${MOD_DIR}/rng_stupid/module.c"

run_one test_rust_sys \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_rust_sys.c" \
	"${MOD_DIR}/rust_sys/module.c"

# rhp twice: RhpThrowEx behaves differently with/without a linked
# ZkvmThrow handler (weak symbol resolution is a link-time property).
run_one test_rhp \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_rhp.c" "${MOD_DIR}/rhp/module.c"
run_one test_rhp_throw \
	${CC} ${CFLAGS} -DWITH_ZKVM_THROW "${TESTS_DIR}/test_rhp.c" \
	"${MOD_DIR}/rhp/module.c"

# tls: the .tdata/.tbss lengths are symbol ADDRESSES in the real link;
# recreate that with absolute --defsym values (must match test_tls.c).
run_one test_tls \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_tls.c" "${MOD_DIR}/tls/module.c" \
	-Wl,--defsym,__tdata_len=16 -Wl,--defsym,__tbss_len=32

# pal: the heap window symbols come from the linker script in the real
# image; here the bottom is a 1 MiB array in the test and the top is
# aliased to its end.
run_one test_pal \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_pal.c" "${MOD_DIR}/pal/module.c" \
	-Wl,--defsym,_kernel_heap_top=_kernel_heap_bottom+1048576 \
	-Wl,--defsym,_init_stack_top=_init_stack_bottom+262144

run_one test_nofp \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_nofp.c" "${MOD_DIR}/nofp/module.c"

# noos: the module defines plain libc names - strlen, fork, fprintf - because
# displacing musl's is its whole job. Linking it into a host test displaces the
# harness's libc too, and noos's fprintf is a trap, so a failing CHECK would
# terminate the process instead of printing which check failed (and the trap
# tests could not fork, because fork is trapped as well).
#
# Rename every symbol the module DEFINES to noos_t_*, leaving its undefined
# references (exit, zkvm_console_write) alone so the test can still satisfy
# them; the test calls the renamed copies. --allow-multiple-definition, the
# trick nothread uses below, cannot help here for the same reason.
if ${CC} ${CFLAGS} -c "${MOD_DIR}/noos/module.c" -o "${OUT_DIR}/noos_raw.o" ; then
	"${NM}" -g --defined-only "${OUT_DIR}/noos_raw.o" \
		| awk '$2 ~ /^[TtDdBbRr]$/ { print $3 " noos_t_" $3 }' \
		> "${OUT_DIR}/noos_syms.txt"
	"${OBJCOPY}" --redefine-syms="${OUT_DIR}/noos_syms.txt" \
		"${OUT_DIR}/noos_raw.o" "${OUT_DIR}/noos_renamed.o"
	run_one test_noos \
		${CC} ${CFLAGS} "${TESTS_DIR}/test_noos.c" "${OUT_DIR}/noos_renamed.o"
else
	echo "BUILD FAIL: test_noos (module.c)"
	failures=$((failures + 1))
fi

# nothread: the module defines pthread_mutex_lock and friends, which glibc
# also defines. --allow-multiple-definition lets the host link pick ours (it
# comes first); on the guest there is no competing definition, because keeping
# musl's members out of the link is the entire point of the module.
run_one test_nothread \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_nothread.c" "${MOD_DIR}/nothread/module.c" \
	-Wl,--allow-multiple-definition

# stdcppshim: --wrap=malloc makes the OOM path reachable on demand.
run_one test_stdcppshim \
	${CXX} ${CXXFLAGS} "${TESTS_DIR}/test_stdcppshim.cpp" \
	"${MOD_DIR}/stdcppshim/module.cpp" -Wl,--wrap=malloc

# The test pins the .NET 11 layout of ubootstrap's classlib table (slot 4 =
# ResolveDispatch).
run_one test_ubootstrap \
	${CXX} ${CXXFLAGS} "${TESTS_DIR}/test_ubootstrap.cpp" \
	"${MOD_DIR}/ubootstrap/module.cpp"

# --- assembly modules -------------------------------------------------

run_one test_rhp_native \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_rhp_native.c" "${TESTS_DIR}/asm_shim.S" \
	"${MOD_DIR}/rhp_native/module.S"

# The zkVM entry points and their tests (test_entry.c, test_mem.c) are in
# the platform packages: tests/platform/run.sh in bflat-libziskos,
# bflat-sp1 and bflat-openvm.

echo "--------------------------------------------------"
if [ "${failures}" != "0" ] ; then
	echo "module unit tests: ${failures} suite(s) FAILED"
	exit 1
fi
echo "module unit tests: all suites passed"
exit 0
