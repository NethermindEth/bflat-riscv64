#!/bin/bash
export TOP_DIR="$(cd "$(dirname "$(which "$0")")" ; pwd -P)"

ver="${1:-v10.0.0.b15}"
file="bflat.compiler.10.0.0.nupkg"

if [ -f "${HOME}/.nuget/packages/bflat.compiler/10.0.0" ] ; then
    rm -rf "${HOME}/.nuget/packages/bflat.compiler/10.0.0"
fi

mkdir -p src/packages
pushd src/packages
    rm "$file"
    wget "https://github.com/NethermindEth/dotnet-riscv/releases/download/$ver/$file"
popd

pushd ${TOP_DIR}/src/bflat
    nuget_config="nuget.config"
    echo "<?xml version=\"1.0\" encoding=\"utf-8\"?>" > $nuget_config
    echo "<configuration>" >> $nuget_config
    echo "    <packageSources>" >> $nuget_config
    echo "        <add key=\"github\" value=\"$(realpath ../packages)\" />" >> $nuget_config
    echo "    </packageSources>" >> $nuget_config
    echo "</configuration>" >> $nuget_config
popd
