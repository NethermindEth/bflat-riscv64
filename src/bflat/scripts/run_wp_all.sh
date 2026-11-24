#!/usr/bin/env bash
# Deductive proof of the ACSL contracts of every C module (Frama-C WP).
#
# Each src/bflat/modules/<name>/module.c goes through WP with RTE guards,
# as a whole file. The run fails unless every goal of every module is
# proved. A module can give WP extra options in verify/wp.opts next to its
# module.c (one line; for example a -wp-skip-fct list of functions that
# another analysis covers, with the reason in a comment line above it).
#
# The modules that WP cannot read are listed in SKIPPED below, the same
# ones as in check_acsl.py.
#
# Usage: ./run_wp_all.sh [module...]
#   FRAMAC=<frama-c binary>        (default: frama-c)
#   WP_TIMEOUT=<seconds per goal>  (default: 30)
#
# Locally (needs docker):
#   docker run --rm -v "$PWD":/w -w /w framac/frama-c:30.0 \
#       src/bflat/scripts/run_wp_all.sh
#
# Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)

set -uo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")" ; pwd -P)"
MODULES_DIR="$(dirname "${SCRIPTS_DIR}")/modules"

FRAMAC="${FRAMAC:-frama-c}"
WP_TIMEOUT="${WP_TIMEOUT:-30}"
NPROC="$(nproc 2>/dev/null || echo 4)"

# Modules without C code that WP can read.
SKIPPED="gs_cookie guest_subst rhp_native stdcppshim tests ubootstrap ugc-zero"

command -v "${FRAMAC}" > /dev/null 2>&1 || {
	echo "error: frama-c not found (use the framac/frama-c docker image)" >&2
	exit 1
}

if [ $# -gt 0 ] ; then
	modules="$*"
else
	modules=$(cd "${MODULES_DIR}" && ls -d */ | tr -d /)
fi

fail=0
printf "%-12s %10s  %s\n" module proved status
for m in ${modules} ; do
	case " ${SKIPPED} " in *" ${m} "*) continue ;; esac
	src="${MODULES_DIR}/${m}/module.c"
	[ -f "${src}" ] || continue

	opts=""
	if [ -f "${MODULES_DIR}/${m}/verify/wp.opts" ] ; then
		opts=$(grep -v '^[[:space:]]*#' "${MODULES_DIR}/${m}/verify/wp.opts" | tr '\n' ' ')
	fi

	# shellcheck disable=SC2086 # the options are words on purpose
	out=$("${FRAMAC}" -cpp-extra-args=-CC -machdep gcc_x86_64 \
		-wp -wp-rte -wp-timeout "${WP_TIMEOUT}" -wp-par "${NPROC}" \
		${opts} "${src}" 2>&1)
	rc=$?

	proved=$(echo "${out}" \
		| sed -n 's/.*Proved goals:[[:space:]]*\([0-9][0-9]*\)[[:space:]]*\/[[:space:]]*\([0-9][0-9]*\).*/\1 \2/p' \
		| tail -1)
	ok=${proved%% *}
	total=${proved##* }

	if [ "${rc}" -ne 0 ] || [ -z "${proved}" ] || [ "${ok}" != "${total}" ] || \
	   [ "${total}" = "0" ] ; then
		printf "%-12s %10s  FAILED\n" "${m}" "${ok:-?}/${total:-?}"
		echo "${out}" | grep -E "\[(Timeout|Unknown|Failed)\]|[Ee]rror|aborted" \
			| head -20 | sed 's/^/    /'
		fail=1
	else
		printf "%-12s %10s  ok\n" "${m}" "${ok}/${total}"
	fi
done

exit "${fail}"
