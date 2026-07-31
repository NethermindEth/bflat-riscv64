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

run_one()
{
	local name="$1" ; shift
	if ! "$@" -o "${OUT_DIR}/${name}" ; then
		echo "BUILD FAIL: ${name}"
		failures=$((failures + 1))
		return
	fi
	if ! ${RUN} "${OUT_DIR}/${name}" ; then
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

run_one test_security_stub \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_security_stub.c" \
	"${MOD_DIR}/security-stub/module.c"

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
	-Wl,--defsym,_kernel_heap_top=_kernel_heap_bottom+1048576

run_one test_nofp \
	${CC} ${CFLAGS} "${TESTS_DIR}/test_nofp.c" "${MOD_DIR}/nofp/module.c"

# stdcppshim: --wrap=malloc makes the OOM path reachable on demand.
run_one test_stdcppshim \
	${CXX} ${CXXFLAGS} "${TESTS_DIR}/test_stdcppshim.cpp" \
	"${MOD_DIR}/stdcppshim/module.cpp" -Wl,--wrap=malloc

run_one test_ubootstrap \
	${CXX} ${CXXFLAGS} "${TESTS_DIR}/test_ubootstrap.cpp" \
	"${MOD_DIR}/ubootstrap/module.cpp"

echo "--------------------------------------------------"
if [ "${failures}" != "0" ] ; then
	echo "module unit tests: ${failures} suite(s) FAILED"
	exit 1
fi
echo "module unit tests: all suites passed"
exit 0
