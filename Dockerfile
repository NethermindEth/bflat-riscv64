FROM ubuntu:25.04

COPY src/bflat/bin/Debug/net10.0 /share/bflat

RUN apt-get update && apt-get install -y wget libicu-dev gcc-riscv64-linux-gnu

RUN wget https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100-preview.3.25201.16/dotnet-sdk-10.0.100-preview.3.25201.16-linux-x64.tar.gz

COPY ./src/bflat/bin/Debug/net10.0/libc_nonshared.a /lib/riscv64-linux-gnu/
COPY ./src/bflat/bin/Debug/net10.0/libc.so.6 /lib/riscv64-linux-gnu/
COPY ./src/bflat/bin/Debug/net10.0/ld-linux-riscv64-lp64d.so.1 /lib/

ENV HOME=/root
RUN mkdir -p $HOME/dotnet && tar zxf dotnet-sdk-10.0.100-preview.3.25201.16-linux-x64.tar.gz -C $HOME/dotnet
ENV DOTNET_ROOT=$HOME/dotnet
ENV PATH=$PATH:$HOME/dotnet
ENV PATH="$PATH:/share/bflat"
