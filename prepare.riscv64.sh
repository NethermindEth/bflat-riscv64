#!/bin/bash
# Prepare nuget configuration for riscv64 build

export TOP_DIR="$(cd "$(dirname "$(which "$0")")" ; pwd -P)"

mkdir -p ${TOP_DIR}/src/packages
pushd ${TOP_DIR}/src/packages
	rm bflat.compiler.10.0.0.nupkg
	wget https://opensource.interpretica.io/bflat/v10.0.0/bflat.compiler.10.0.0.nupkg -o bflat.compiler.10.0.0.nupkg
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
