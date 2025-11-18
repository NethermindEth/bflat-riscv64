FROM ubuntu:24.04

COPY src/bflat/bin/Debug/net10.0 /share/bflat

RUN apt-get update && apt-get install -y wget libicu-dev gcc-riscv64-linux-gnu python3 python3-pip

RUN wget https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-linux-x64.tar.gz

RUN pip3 install lief pyelftools --break-system-packages
#COPY ./src/bflat/bin/Debug/net10.0/lib/linux/riscv64/musl/libc.a /lib/riscv64-linux-gnu/
#COPY ./src/bflat/bin/Debug/net10.0/libc.so.6 /lib/riscv64-linux-gnu/
#COPY ./src/bflat/bin/Debug/net10.0/ld-linux-riscv64-lp64d.so.1 /lib/

ENV HOME=/root
RUN mkdir -p $HOME/dotnet && tar zxf dotnet-sdk-10.0.100-linux-x64.tar.gz -C $HOME/dotnet
ENV DOTNET_ROOT=$HOME/dotnet
ENV PATH=$PATH:$HOME/dotnet
ENV PATH="$PATH:/share/bflat"
