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
variant="$3"

if [ "$flavor" == "" ] ; then
	flavor="generic"
fi

# Compiler variant: which runtime blob release gets bundled.
#   perf -> .b23 (performance-oriented runtime)
#   min  -> .x2  (minimal runtime)
if [ "$variant" == "" ] ; then
	variant="perf"
fi
case $variant in
	perf|min) ;;
	*) fail Unsupported variant: "$variant" ;;
esac

cd $TOP_DIR

function build_modules()
{
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

	# gcc-riscv64-linux-gnu only ships stubs-lp64d.h (hard-float glibc).
	# With -mabi=lp64, glibc's gnu/stubs.h looks up gnu/stubs-lp64.h, which
	# doesn't exist on Ubuntu. The file is just an empty marker, so create
	# an empty one if missing. Errors are ignored — read-only sysroot is fine
	# as long as the file already exists.
	local stubs_dir="${sysroot}/include/gnu"
	if [ -d "${stubs_dir}" ] && [ ! -f "${stubs_dir}/stubs-lp64.h" ] ; then
		touch "${stubs_dir}/stubs-lp64.h" 2>/dev/null || \
			sudo touch "${stubs_dir}/stubs-lp64.h" 2>/dev/null || true
	fi

	pushd ${TOP_DIR}/src/bflat/modules
		for mod in $(ls) ; do
			if [ -d "$mod" ] ; then
				echo Building module $mod
				pushd $mod
					if [ -f module.c ] ; then
						# Compile module as C (clang + LTO so lld can do cross-module opt)
						clang ${cflags_common} -c module.c -o module.o
						on_fail $? "Failed to compile module $mod (C)"
					fi
					if [ -f module.S ] ; then
						# Compile module as assembly (no LTO — bitcode doesn't apply)
						riscv64-linux-gnu-as --march=rv64ima --mabi=lp64 module.S -o module.o
						on_fail $? "Failed to compile module $mod (Assembly)"
					fi
					if [ -f module.cpp ] ; then
						# Compile module as C++ (clang + LTO)
						clang++ ${cflags_common} -c module.cpp -o module.o
						on_fail $? "Failed to compile module $mod (C++)"
					fi
					if [ -f module.o ] ; then
						# Fix up ABI marker — only meaningful for ELF objects;
						# LTO bitcode files are not ELF, ABI is handled at LTO link time.
						magic=$(head -c 4 module.o | xxd -p)
						if [ "$magic" = "7f454c46" ] ; then
							printf '\x00' | dd of="module.o" bs=1 seek=$((0x30)) count=1 conv=notrunc status=none
						fi
					fi
					if [ -f module_params.yml ] ; then
						repo="$(yq -r .options.repo module_params.yml)"
						tag="$(yq -r .options.tag module_params.yml)"
						build="$(yq -r .options.commands.build module_params.yml)"
						release_file="$(yq -r .options.releases.file module_params.yml)"

						# If commands are provided, build from repo
						if [ "$repo" != "null" ] && [ "$build" != "null" ]; then
							if [ ! -d src ] ; then
								if [ "$tag" != "null" ] && [ "$tag" != "" ]; then
									git clone --branch "${tag}" "${repo}" src
									on_fail $? "Failed to clone repository ${repo} with tag ${tag}"
								else
									git clone "${repo}" src
									on_fail $? "Failed to clone repository ${repo}"
								fi
							fi
							pushd src
								bash -c "${build}"
								on_fail $? "Failed to build module ${module}"
							popd
						# Otherwise, if release file is provided, download from release and unpack
						elif [ "$repo" != "null" ] && [ "$tag" != "null" ] && [ "$tag" != "" ] && [ "$release_file" != "null" ] && [ "$release_file" != "" ]; then
							repo_path="$(echo "$repo" | sed -E 's#https?://github\.com/##' | sed -E 's#\.git$##')"
							on_fail $? "Failed to parse GitHub repository path from ${repo}"

							asset_url="https://github.com/${repo_path}/releases/download/${tag}/${release_file}"
							target_dir="${TOP_DIR}/src/bflat/modules/${mod}/release"

							mkdir -p "${target_dir}"
							on_fail $? "Failed to create release directory ${target_dir}"

							tmp_archive="${target_dir}/${release_file}"
							curl -L -o "${tmp_archive}" "${asset_url}"
							on_fail $? "Failed to download release archive ${asset_url}"

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
						fi
					fi
				popd
			fi
		done
	popd
}

case $flavor in
	generic)
		if [ "${what}" == "bflat" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -p:Variant=${variant}
			on_fail $? "Failed to build bflat (generic, ${variant})"
		fi
		if [ "${what}" == "layouts" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -p:Variant=${variant} -t:BuildLayouts -c:Release
			on_fail $? "Failed to build layouts (generic, ${variant})"
		fi
		;;
	riscv64)
		if [ "${what}" == "modules" ] || [ "${what}" == "all" ] ; then
			build_modules
			on_fail $? "Failed to build modules"
		fi
		if [ "${what}" == "bflat" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64 -p:Variant=${variant}
			on_fail $? "Failed to build bflat (riscv64, ${variant})"
		fi
		if [ "${what}" == "layouts" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64 -p:Variant=${variant} -t:BuildLayouts -c:Release
			on_fail $? "Failed to build layouts (riscv64, ${variant})"
		fi
		;;
	*)
		fail Unsupported flavor: "$flavor"
		;;
esac

exit 0
