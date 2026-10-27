FROM ubuntu:26.04

# bflat targets net11.0, so the image needs a .NET 11 SDK: an SDK carries
# only its own major's runtime. SDK_VERSION overrides the default (patch
# bumps, previews); its major is checked here rather than at bflat's first run.
ARG SDK_VERSION=11.0.100-preview.6.26359.118

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
    case "$sdk" in \
        11.*) ;; \
        *) echo "Dockerfile: SDK_VERSION=$sdk is not a .NET 11 SDK; the packaged bflat targets net11.0 and would not start" >&2; exit 1 ;; \
    esac; \
    echo "Installing .NET SDK $sdk"; \
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

# The build of ./build.sh all riscv64
COPY src/bflat/bin/Debug/net11.0 /share/bflat

# Last line of defence: the checks above reason about arguments, this one about
# what actually landed in the image. bflat --info exercises the real host
# resolver, so anything that would abort at first use fails the build instead.
RUN set -eu; \
    tfm="$(sed -n 's/.*"tfm": *"\([^"]*\)".*/\1/p' /share/bflat/bflat.runtimeconfig.json)"; \
    installed="$(ls "$HOME/dotnet/shared/Microsoft.NETCore.App")"; \
    echo "packaged bflat: $tfm; installed runtime: $installed"; \
    case "$tfm" in \
        net11.0) ;; \
        *) echo "packaged bflat targets $tfm, expected net11.0" >&2; exit 1 ;; \
    esac; \
    bflat --info > /dev/null
