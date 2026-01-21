#!/bin/bash
# Build Dockerfile for riscv64

export TOP_DIR="$(cd "$(dirname "$(which "$0")")" ; pwd -P)"

docker build --platform linux/amd64 .

docker_image=$(docker build --platform linux/amd64 -t maximmenshikov/bflat-riscv64-zk -q .)

interactive=""
arg=""
if [ "$1" != "" ] ; then
	interactive="i"
	arg="bflat"
fi

docker run -v $(pwd):$(pwd) -w $(pwd) -${interactive}t ${docker_image} $arg $@
exit $?
