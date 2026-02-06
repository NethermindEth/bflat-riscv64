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

function build_modules()
{
	pushd ${TOP_DIR}/src/bflat/modules
		for mod in $(ls) ; do
			if [ -d "$mod" ] ; then
				echo Building module $mod
				pushd $mod
					if [ -f module.c ] ; then
						# Compile module as C
						riscv64-linux-gnu-gcc -march=rv64imad -c module.c -o module.o
						on_fail $? "Failed to compile module $mod (C)"
					fi
					if [ -f module.S ] ; then
						# Compile module as assembly
						riscv64-linux-gnu-as --march=rv64ima --mabi=lp64 module.S -o module.o
						on_fail $? "Failed to compile module $mod (Assembly)"
					fi
					if [ -f module.cpp ] ; then
						# Compile module as C++
						riscv64-linux-gnu-g++ -march=rv64imad -c module.cpp -o module.o
						on_fail $? "Failed to compile module $mod (C++)"
					fi
					if [ -f module.o ] ; then
						# Fix up ABI marker
						printf '\x00' | dd of="module.o" bs=1 seek=$((0x30)) count=1 conv=notrunc
					fi
					if [ -f module_params.yml ] ; then
						repo="$(yq -r .options.repo module_params.yml)"
						build="$(yq -r .options.commands.build module_params.yml)"
						if [ "$repo" != "null" ] && [ "$build" != "null" ]; then
							if [ ! -d src ] ; then
								git clone "${repo}" src
								on_fail $? "Failed to clone repository ${repo}"
							fi
							pushd src
								${build}
								on_fail $? "Failed to build module ${module}"
							popd
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
			dotnet build src/bflat/bflat.csproj
			on_fail $? "Failed to build bflat (generic)"
		fi
		if [ "${what}" == "layouts" ] || [ "${what}" == "all" ] ; then
			dotnet build src/bflat/bflat.csproj -t:BuildLayouts -c:Release
			on_fail $? "Failed to build layouts (generic)"
		fi
		;;
	riscv64)
		if [ "${what}" == "modules" ] || [ "${what}" == "all" ] ; then
			build_modules
			on_fail $? "Failed to build modules"
		fi
		if [ "${what}" == "bflat" ] || [ "${what}" == "all" ] ; then
			./update_nupkg.sh
			dotnet build src/bflat/bflat.csproj -p:Flavor=riscv64
			on_fail $? "Failed to build bflat (riscv64)"
		fi
		if [ "${what}" == "zisklib" ] || [ "${what}" == "all" ] ; then
			dotnet build src/zisklib/zisklib.riscv64.csproj -c:Release
			on_fail $? "Failed to build zisklib (generic)"
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
