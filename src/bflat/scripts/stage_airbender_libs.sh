#!/bin/bash
# Lays out the rv32 libraries the Airbender target links, for bflat.csproj's
# AirbenderLibsDir, until the dotnet-riscv releases carry them:
#
#   stage_airbender_libs.sh <runtime> <framework> <sysroot> <ugc.o> <out>
#
#   runtime    a dotnet/runtime tree built for linux.riscv32.Release (clr.aot and
#              libs.native): the NativeAOT SDK and libSystem.Native
#   framework  a directory of framework IL assemblies of the same version, e.g.
#              shared/Microsoft.NETCore.App/<version> of the SDK the runtime
#              built with; the NativeAOT SDK's own assemblies take precedence
#   sysroot    the rv32im musl sysroot (dotnet-riscv build_musl_rv32im.sh,
#              build_builtins_rv32im.sh, build_libatomic_rv32im.sh)
#   ugc.o      uGC built for rv32 as one relocatable object
#   out        the directory to lay them out in (replaced)

set -e

if [ "$#" -ne 5 ] ; then
	sed -n '2,17p' "$0" >&2
	exit 1
fi

runtime="$1"
framework="$2"
sysroot="$3"
ugc="$4"
out="$5"

aotsdk="${runtime}/artifacts/bin/coreclr/linux.riscv32.Release/aotsdk"
native="${runtime}/artifacts/obj/coreclr/linux.riscv32.Release/libs-native/System.Native/libSystem.Native.a"

for f in "${aotsdk}/System.Private.CoreLib.dll" "${native}" "${sysroot}/usr/lib/libc.a" "${ugc}" ; do
	[ -f "$f" ] || { echo "error: $f not found" >&2 ; exit 1 ; }
done

rm -rf "${out}"
mkdir -p "${out}"

cp "${framework}"/*.dll "${out}/"
cp "${aotsdk}"/*.dll "${out}/"
cp "${aotsdk}/libbootstrapper.o" "${aotsdk}/libRuntime.WorkstationGC.a" "${aotsdk}/libeventpipe-disabled.a" \
   "${aotsdk}/libaotminipal.a" "${aotsdk}/libstandalonegc-disabled.a" "${aotsdk}/libstdc++compat.a" "${out}/"
cp "${native}" "${out}/"
cp "${sysroot}/usr/lib/libc.a" "${sysroot}/usr/lib/libatomic.a" "${sysroot}/usr/lib/libclang_rt.builtins-riscv32.a" "${out}/"
cp "${ugc}" "${out}/ugc.o"

echo "Staged $(ls "${out}" | wc -l) files in ${out}"
