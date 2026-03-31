#!/bin/bash
export TOP_DIR="$(cd "$(dirname "$(which "$0")")" ; pwd -P)"

# Unroll the base folder
base_folder="${TOP_DIR}"
cur="$(pwd)"
while true ; do
    tmp="$(pwd)"
    if [ "${tmp}" == "/" ] ; then
        exit 1
    fi
    cd ..
    real="$(realpath ${tmp})"
    if [ "${real}" == "${TOP_DIR}" ] ; then
        base_folder="${tmp}"
        break
    fi
done

echo Base folder: ${base_folder}

pushd "${base_folder}"
docker build --platform linux/amd64 -f Dockerfile.build -t bflat-riscv64 .
popd

docker run --platform linux/amd64 -w "${cur}" -v "${base_folder}:${base_folder}" -it bflat-riscv64 /bin/bash
