FROM ubuntu:26.04

# DOTNET_VERSION is the only knob that has to be set: it selects which bflat
# build gets packaged (COPY below) AND, through the table here, which SDK is
# installed. The two must agree - an SDK carries only its own major's runtime,
# so a net10.0 bflat on an 11-only image aborts at startup with "framework
# '10.0.0' not found" and the image is useless. That has been shipped twice by
# passing DOTNET_VERSION alone and inheriting the other major's SDK default, so
# the pairing is no longer something the caller has to remember.
#
# SDK_VERSION still overrides the table (patch bumps, previews); when it is set
# its major is checked against DOTNET_VERSION and a mismatch fails the build
# here rather than at bflat's first run.
ARG DOTNET_VERSION=11
ARG SDK_VERSION=
ARG SDK_VERSION_NET10=10.0.300
ARG SDK_VERSION_NET11=11.0.100-preview.6.26359.118

RUN apt-get update && apt-get install -y wget libicu-dev gcc-riscv64-linux-gnu llvm clang lld xxd file python3 python3-pip libarchive-tools

ENV BFLAT_LD=/usr/bin/lld

ENV HOME=/root
# Resolve, verify, download and unpack in one step so the resolved version does
# not have to be threaded through further ARGs.
# bsdtar instead of GNU tar: the glibc 2.43 rebuild of ubuntu:26.04 makes GNU
# tar issue syscalls Rosetta doesn't translate (ENOSYS on nested mkdir) when
# building linux/amd64 images on Apple Silicon.
RUN set -eu; \
    sdk="${SDK_VERSION}"; \
    if [ -z "$sdk" ]; then \
        case "${DOTNET_VERSION}" in \
            10) sdk="${SDK_VERSION_NET10}" ;; \
            11) sdk="${SDK_VERSION_NET11}" ;; \
            *) echo "Dockerfile: no SDK known for DOTNET_VERSION=${DOTNET_VERSION}; pass SDK_VERSION explicitly" >&2; exit 1 ;; \
        esac; \
    fi; \
    case "$sdk" in \
        "${DOTNET_VERSION}".*) ;; \
        *) echo "Dockerfile: SDK_VERSION=$sdk is not a .NET ${DOTNET_VERSION} SDK; the packaged bflat targets net${DOTNET_VERSION}.0 and would not start" >&2; exit 1 ;; \
    esac; \
    echo "Installing .NET SDK $sdk for net${DOTNET_VERSION}.0"; \
    wget -q "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk/dotnet-sdk-$sdk-linux-x64.tar.gz"; \
    mkdir -p "$HOME/dotnet"; \
    bsdtar -xzf "dotnet-sdk-$sdk-linux-x64.tar.gz" -C "$HOME/dotnet"; \
    rm "dotnet-sdk-$sdk-linux-x64.tar.gz"

RUN pip3 install lief pyelftools --break-system-packages

# gcc-riscv64-linux-gnu only ships hard-float (lp64d) glibc stubs. We compile
# native modules with -mabi=lp64 (soft-float) to match zisk's crt1.o, so we
# need an empty gnu/stubs-lp64.h marker. The real one is an empty file.
RUN touch /usr/riscv64-linux-gnu/include/gnu/stubs-lp64.h

ENV DOTNET_ROOT=$HOME/dotnet
ENV PATH=$PATH:$HOME/dotnet
ENV PATH="$PATH:/share/bflat"

# The packaged variant (perf/min) is whichever was built last:
# ./build.sh all riscv64 <variant> <dotnet_version>
COPY src/bflat/bin/Debug/net${DOTNET_VERSION}.0 /share/bflat

# Last line of defence: the checks above reason about arguments, this one about
# what actually landed in the image. bflat --info exercises the real host
# resolver, so anything that would abort at first use fails the build instead.
RUN set -eu; \
    tfm="$(sed -n 's/.*"tfm": *"\([^"]*\)".*/\1/p' /share/bflat/bflat.runtimeconfig.json)"; \
    installed="$(ls "$HOME/dotnet/shared/Microsoft.NETCore.App")"; \
    echo "packaged bflat: $tfm; installed runtime: $installed"; \
    case "$tfm" in \
        "net${DOTNET_VERSION}.0") ;; \
        *) echo "packaged bflat targets $tfm, expected net${DOTNET_VERSION}.0" >&2; exit 1 ;; \
    esac; \
    bflat --info > /dev/null
