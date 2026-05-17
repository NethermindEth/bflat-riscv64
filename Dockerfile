FROM ubuntu:26.04

RUN apt-get update && apt-get install -y wget libicu-dev gcc-riscv64-linux-gnu llvm clang lld xxd python3 python3-pip

ENV BFLAT_LD=/usr/bin/lld

RUN wget https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-linux-x64.tar.gz

RUN pip3 install lief pyelftools --break-system-packages

# gcc-riscv64-linux-gnu only ships hard-float (lp64d) glibc stubs. We compile
# native modules with -mabi=lp64 (soft-float) to match zisk's crt1.o, so we
# need an empty gnu/stubs-lp64.h marker. The real one is an empty file.
RUN touch /usr/riscv64-linux-gnu/include/gnu/stubs-lp64.h

ENV HOME=/root
RUN mkdir -p $HOME/dotnet && tar zxf dotnet-sdk-10.0.100-linux-x64.tar.gz -C $HOME/dotnet
ENV DOTNET_ROOT=$HOME/dotnet
ENV PATH=$PATH:$HOME/dotnet
ENV PATH="$PATH:/share/bflat"

COPY src/bflat/bin/Debug/net10.0 /share/bflat
