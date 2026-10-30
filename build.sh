#!/bin/bash
# Build script for zk bflat
# Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
#
# Author: Maxim Menshikov <maksim.menshikov@nethermind.io>

export TOP_DIR="$(cd "$(dirname "$(which "$0")")" ; pwd -P)"

function fail()
{
	echo $@ >&2
	exit 1
}

function on_fail()
{
	if [ "$1" != "0" ] ; then
		shift 1
		fail $@
	fi
}

what="$1"
flavor="$2"

if [ "$flavor" == "" ] ; then
	flavor="generic"
fi

cd $TOP_DIR

# build_modules [riscv64|riscv32]
# riscv64 (the default) builds each module's module.o. riscv32 builds its
# module.rv32.o (the sources of the manifests with "arch": "riscv32") for
# rv32im/ilp32, against the rv32 musl sysroot that RV32_SYSROOT names: the
# crossrootfs-musl-riscv32 asset of the dotnet-riscv release, which has the
# libc++ and Linux headers too. There is no riscv32 GCC, so clang compiles and
# assembles it, without LTO.
function build_modules()
{
	local arch="${1:-riscv64}"
	if [ "${arch}" == "riscv32" ] ; then
		build_modules_for riscv32
		return $?
	fi
	build_modules_for riscv64
}

function build_modules_for()
{
	local arch="$1"
	local sysroot="/usr/riscv64-linux-gnu"
	# -mabi=lp64 (soft-float ABI) must match zisk crt1.o / libc.a, which are
	# soft-float. Without this clang defaults to lp64d when -march=rv64imad,
	# and the bitcode metadata clashes with crt1.o at LTO link time.
	# -mcmodel=medany is required because zisk linker-script places .text at
	# 0x80000000 and .data at 0xa0000000, both outside medlow's reach. medany
	# uses pc-relative auipc+addi with ±2GB range, which spans this layout.
	# -funified-lto: another input to the final link is unified-LTO bitcode,
	# which puts lld into unified-LTO mode; modules must match or lld errors
	# with "unified LTO compilation must use compatible bitcode modules".
	local cflags_common="--target=riscv64-linux-gnu --sysroot=${sysroot} -march=rv64imad -mabi=lp64 -mcmodel=medany -flto=full -funified-lto -O3"
	local cxxflags_common=""
	if [ "${arch}" == "riscv32" ] ; then
		[ -d "${RV32_SYSROOT}/usr/include" ] || fail "RV32_SYSROOT must name an rv32 musl sysroot (crossrootfs-musl-riscv32 of the dotnet-riscv release)"
		[ -d "${RV32_SYSROOT}/usr/include/c++/v1" ] || fail "${RV32_SYSROOT} has no libc++ headers (usr/include/c++/v1)"
		sysroot="${RV32_SYSROOT}"
		# The guest image of a riscv32 zkVM is linked without LTO, and every
		# function in its own section, so that --gc-sections keeps the image
		# within the zkVM's ROM.
		cflags_common="--target=riscv32-unknown-linux-musl --sysroot=${sysroot} -march=rv32im -mabi=ilp32 -mcmodel=medany -O2 -ffunction-sections -fdata-sections -fno-stack-protector"
		# libc++'s headers, and no C++ runtime to link: no exceptions, no RTTI.
		cxxflags_common="-nostdinc++ -isystem ${sysroot}/usr/include/c++/v1 -fno-exceptions -fno-rtti"
	fi

	# gcc-riscv64-linux-gnu only ships stubs-lp64d.h (hard-float glibc).
	# With -mabi=lp64, glibc's gnu/stubs.h looks up gnu/stubs-lp64.h, which
	# doesn't exist on Ubuntu. The file is just an empty marker, so create
	# an empty one if missing. Errors are ignored — read-only sysroot is fine
	# as long as the file already exists.
	local stubs_dir="${sysroot}/include/gnu"
	if [ "${arch}" == "riscv64" ] && [ -d "${stubs_dir}" ] && [ ! -f "${stubs_dir}/stubs-lp64.h" ] ; then
		touch "${stubs_dir}/stubs-lp64.h" 2>/dev/null || \
			sudo touch "${stubs_dir}/stubs-lp64.h" 2>/dev/null || true
	fi

	# The module build steps. bflat-modules reads the module manifests
	# (src/bflat/modules/*/bflat-manifest.json) and writes the list of
	# module_* calls; this script owns the toolchain and its flags.
	local mod_name

	function module_begin()
	{
		mod_name="$1"
		echo Building module ${mod_name}
		pushd "$2" > /dev/null
	}

	function module_end()
	{
		popd > /dev/null
	}

	# module_cc <source> <object> [extra flags...]
	# Compile as C (clang + LTO so lld can do cross-module opt).
	function module_cc()
	{
		local src="$1" out="$2"
		shift 2
		clang ${cflags_common} "$@" -c "$src" -o "$out"
		on_fail $? "Failed to compile module ${mod_name} (C)"
	}

	# module_cxx <source> <object> [extra flags...]
	# Compile as C++ (clang + LTO).
	function module_cxx()
	{
		local src="$1" out="$2"
		shift 2
		clang++ ${cflags_common} ${cxxflags_common} "$@" -c "$src" -o "$out"
		on_fail $? "Failed to compile module ${mod_name} (C++)"
	}

	# module_as <source> <object> [extra flags...]
	# Assemble (no LTO - bitcode doesn't apply).
	function module_as()
	{
		local src="$1" out="$2"
		shift 2
		if [ "${arch}" == "riscv32" ] ; then
			# BFLAT_RV32 selects the 32-bit forms of a source that has both.
			clang --target=riscv32-unknown-elf -march=rv32im -mabi=ilp32 -c -x assembler \
				-Wa,--defsym,BFLAT_RV32=1 "$@" "$src" -o "$out"
		else
			riscv64-linux-gnu-as --march=rv64ima --mabi=lp64 "$@" "$src" -o "$out"
		fi
		on_fail $? "Failed to compile module ${mod_name} (Assembly)"
	}

	# module_fetch_release <repo> <tag> <file> <dir>
	# Download a GitHub release archive, check its signature and unpack it
	# into <dir>.
	function module_fetch_release()
	{
		local repo="$1" tag="$2" release_file="$3" target_dir="$4"
		local repo_path="$(echo "$repo" | sed -E 's#https?://github\.com/##' | sed -E 's#\.git$##')"
		local release_url="https://github.com/${repo_path}/releases/download/${tag}"

		mkdir -p "${target_dir}"
		on_fail $? "Failed to create release directory ${target_dir}"

		local tmp_archive="${target_dir}/${release_file}"
		curl -fL -o "${tmp_archive}" "${release_url}/${release_file}"
		on_fail $? "Failed to download release archive ${release_url}/${release_file}"

		module_verify_release "${release_url}" "${tmp_archive}" "${release_file}" "${target_dir}"

		case "${tmp_archive}" in
			*.tar.gz|*.tgz)
				tar -xzf "${tmp_archive}" -C "${target_dir}"
				on_fail $? "Failed to unpack ${tmp_archive}"
				;;
			*.tar)
				tar -xf "${tmp_archive}" -C "${target_dir}"
				on_fail $? "Failed to unpack ${tmp_archive}"
				;;
			*.zip)
				unzip -o "${tmp_archive}" -d "${target_dir}"
				on_fail $? "Failed to unpack ${tmp_archive}"
				;;
			*)
				fail "Unsupported release archive format: ${tmp_archive}"
				;;
		esac
	}

	# module_verify_release <release-url> <file> <asset-name> <dir>
	# Check a downloaded asset against the signed list of its release
	# (docs/signing.md), with the keys in BFLAT_SIGNING_PUBLIC_KEY, before
	# anything unpacks it. Without the keys nothing is checked. A release
	# without a signature is an error unless ALLOW_UNSIGNED_EXTERNAL=true.
	function module_verify_release()
	{
		local release_url="$1" file="$2" asset="$3" dir="$4"
		if [ -z "${BFLAT_SIGNING_PUBLIC_KEY}" ] ; then
			echo "BFLAT_SIGNING_PUBLIC_KEY is not set: ${asset} is not checked"
			return
		fi

		local list="${dir}/bflat-signature.sha256" sig="${dir}/bflat-signature.sha256.sig"
		if ! curl -fsSL -o "${list}" "${release_url}/bflat-signature.sha256" || \
		   ! curl -fsSL -o "${sig}" "${release_url}/bflat-signature.sha256.sig" ; then
			rm -f "${list}" "${sig}"
			if [ "${ALLOW_UNSIGNED_EXTERNAL}" == "true" ] ; then
				echo "ALLOW_UNSIGNED_EXTERNAL=true: ${release_url} is not signed, and ${asset} is used without a check"
				return
			fi
			fail "${release_url} has no bflat-signature.sha256 and bflat-signature.sha256.sig." \
				"Use a signed release, or set ALLOW_UNSIGNED_EXTERNAL=true."
		fi

		dotnet run --project "${TOP_DIR}/src/bflat-modules/bflat-modules.csproj" -c Release --no-build -- \
			verify-release "${list}" "${sig}" "${file}=${asset}"
		on_fail $? "The signature check of ${asset} failed"
	}

	# module_fetch_build <repo> <tag> <dir> <command>...
	# Clone the repository into <dir> (once) and run the build commands in it.
	function module_fetch_build()
	{
		local repo="$1" tag="$2" target_dir="$3"
		shift 3
		if [ ! -d "${target_dir}" ] ; then
			git clone --branch "${tag}" "${repo}" "${target_dir}"
			on_fail $? "Failed to clone repository ${repo} with tag ${tag}"
		fi
		pushd "${target_dir}" > /dev/null
			local cmd
			for cmd in "$@" ; do
				bash -c "${cmd}"
				on_fail $? "Failed to build module ${mod_name}"
			done
		popd > /dev/null
	}

	local plan="${TOP_DIR}/src/bflat-modules/obj/modules.plan.sh"
	mkdir -p "$(dirname "${plan}")"
	dotnet run --project "${TOP_DIR}/src/bflat-modules/bflat-modules.csproj" -c Release -- \
		plan "${TOP_DIR}/src/bflat/modules" "${plan}" --arch "${arch}"
	on_fail $? "Failed to read the module manifests"
	. "${plan}"
}

case $flavor in
	generic)
		if [ "${what}" == "bflat" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj
			on_fail $? "Failed to build bflat (generic)"
		fi
		if [ "${what}" == "layouts" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -t:BuildLayouts -c:Release
			on_fail $? "Failed to build layouts (generic)"
		fi
		;;
	riscv32)
		# The rv32 modules beside the rv64 ones; bflat itself is built with the
		# riscv64 flavor and -p:BflatTargetRiscV32=true.
		if [ "${what}" == "modules" ] ; then
			build_modules riscv32
			on_fail $? "Failed to build rv32 modules"
		else
			fail "The riscv32 flavor builds only modules (build.sh modules riscv32); build bflat with: dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64 -p:BflatTargetRiscV32=true"
		fi
		;;
	riscv64)
		if [ "${what}" == "modules" ] || [ "${what}" == "all" ] ; then
			build_modules
			on_fail $? "Failed to build modules"
		fi
		if [ "${what}" == "bflat" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64
			on_fail $? "Failed to build bflat (riscv64)"
		fi
		if [ "${what}" == "layouts" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64 -t:BuildLayouts -c:Release
			on_fail $? "Failed to build layouts (riscv64)"
		fi
		;;
	*)
		fail Unsupported flavor: "$flavor"
		;;
esac

exit 0
