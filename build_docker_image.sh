#!/bin/bash
# Build Dockerfile for riscv64
# Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
#
# Author: Maxim Menshikov <maksim.menshikov@nethermind.io>
image_name="nethermindeth/bflat-riscv64-test"

export TOP_DIR="$(cd "$(dirname "$(which "$0")")" ; pwd -P)"

docker build --platform linux/amd64 .

docker_image=$(docker build --platform linux/amd64 -t "${image_name}" -q .)

interactive=""
arg=""
if [ "$1" != "" ] ; then
	interactive="i"
	arg="bflat"
fi

docker run -v $(pwd):$(pwd) -w $(pwd) -${interactive}t ${docker_image} $arg $@
exit $?
