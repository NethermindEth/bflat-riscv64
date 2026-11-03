---
layout: default
title: Targets
eyebrow: Platforms
lead: >
  The five zkVM platforms that bflat builds for, and the stock linux-x64
  target. For each one: its package, memory map, entry, exit, post-link
  steps, code generation, and how to run a guest.
prev: /modules/
next: /manifest/
---

bflat knows no zkVM. Each zkVM target is a **platform**: a manifest in its
bflat-* package that gives the entry point, the memory map, the instruction
set, the RyuJIT options, the post-link steps and the modules that the guest
needs (refer to the [manifest format](manifest.md#platform)). `--libc`
selects the platform by its alias, and `--extlib` must give the package.

All zkVM platforms link the same [modules](modules.md), the same soft-float
musl and the same never-collecting GC. They differ in what this page
describes.

## Summary

| Platform (`--libc`) | Arch, ABI | Package | ISA | Entry and start-up | Exit | Post-link | Unwind tables |
|---------------------|-----------|---------|-----|--------------------|------|-----------|---------------|
| [`zisk`](#zisk) | riscv64, lp64 | bflat-libziskos | RV64IM; Zba, Zbb, Zbs with a compiler that has `zb-lowering-fixes` | `_start` → musl `__libc_start_main` | `ecall`, `a7 = 93` | `patch_elf` | yes |
| [`zisk_sim`](#zisk_sim) | riscv64, lp64 | bflat-libziskos | RV64IM | the ZisK `_start` | the ZisK exit | none | yes |
| [`sp1`](#sp1) | riscv64, lp64 | bflat-sp1 | RV64IM | `_start` → sp1-zkvm `__start` → `noos_main` | the commits, then `HALT` | `patch_elf` | yes |
| [`openvm`](#openvm) | riscv64, lp64 | bflat-openvm | RV64IM | `_start` → `noos_start_main` | `TERMINATE` | none | yes |
| [`airbender`](#airbender) | riscv32, ilp32 | bflat-airbender | RV32IM, unsigned M only | `_start` → `noos_start_main` | output in `x10`–`x25`, or a cycle-CSR write | `patch_elf`, `flat_image` ×2 | no |
| [`linux-x64`](#linux-x64) | x64 | — (stock) | x86-64 | glibc | `exit` | none | yes |

Every zkVM platform disables C, A, F, D and Zicond (`codegen.isa.disable`)
and sets `EnableRiscV64Compressed=0` and `EnableRiscV64Atomic=0`. The
[ISA checks](verification.md#floating-point-gates) confirm this on the
linked image.

| Platform | Flags | Unaligned access | Bump-pointer cell (`JitZkBumpAddr`) |
|----------|-------|------------------|-------------------------------------|
| `zisk` | `no-console` | wide | `0xbffbfff8` |
| `zisk_sim` | — | wide | `0xbffbfff8` |
| `sp1` | `no-atomics`, `noos`, `no-console` | byte-wise, and a run-time check (`JitNoUnalignedAccess=1`, `JitRiscV64StrictAlign=1`) | `0xbffefff8` |
| `openvm` | `no-atomics`, `noos`, `no-console` | wide | `0xbffefff8` |
| `airbender` | `no-atomics`, `noos`, `no-console` | byte-wise where the JIT knows (`JitNoUnalignedAccess=1`) | none: the guest calls the allocation helper |

The flags select modules: `nothread` links only with `no-atomics`, `noos`
only with `noos`, and the console wraps of `rhp` and `pal` only with
`no-console`. Refer to the [flags table](manifest.md#conditions).

**The bump-pointer cell.** dotnet-riscv fixup 26 replaces the call to the
allocation helper with an inline bump on a cell at a fixed address. The JIT
writes the address as a constant with no relocation. `JitZkBumpAddr` gives
the address, and it must equal `g_zk_bump_ptr` in the linker script. The
cell 8 bytes below it, `g_zk_heap_floor`, holds the lowest address that the
heap can reach. The inline sequence loads the floor and fail-fasts when the
heap is full. `pal` writes the floor once, from an `.init_array`
constructor, before the runtime starts. Thus the JIT can hoist the load out
of loops. Without the option (the default is 0), the JIT inlines nothing:
a stock riscv64 target never writes to an absolute guest address.

**The platform interface.** The modules contain no code for one zkVM. `pal`
calls `zkvm_platform_exit`, `zkvm_platform_write` and
`zkvm_platform_syscall`, which the entry object of each platform defines
(refer to the [platform interface](manifest.md#platform-interface)).

---

## zisk

[ZisK](https://github.com/0xPolygonHermez/zisk) 1.4. The package is
[bflat-libziskos](https://github.com/NethermindEth/bflat-libziskos)
(NuGet: `Nethermind.ZiskOS.Runtime`).

| Item | Value |
|------|-------|
| Platform files | `platform/zisk.bflat.manifest`, `zisk.ld`, `zisk_entry.o` (`entry.S`) |
| Bindings | `libziskos.bflat.manifest` with `libziskos.a`, `"when": {"platform": "zisk"}` |
| Define | `ZKVM_ZISK` |

```console
$ bflat build app.cs --os linux --arch riscv64 --libc zisk --no-pie \
    --extlib path/to/libziskos.bflat.manifest -o app
$ ziskemu -e ./app
```

A manifest path loads every `*.bflat.manifest` in its directory, so the
platform and the bindings come together. A guest that does not use the
accelerators can be built with the platform files alone
(`platform/build.sh` of the package). `ziskemu -e` takes the ELF; `--rom`
takes a ROM that is already converted.

**Memory map** (`zisk.ld`, from `zisk/core/src/mem.rs` of ZisK 1.4):

| Region | Address | Use |
|--------|---------|-----|
| Program ROM | `0x90000000`–`0x97e00000` | `.text`, `.rodata`. The ZisK library ROM starts at `0x97e00000`. |
| Stack | `0xa0000000`, 4 MiB | `_init_stack_top` is at the top |
| SYS, OUTPUT | `0xa0400000`, `0xa0410000` | registers, UART, public output. Nothing is linked here. |
| RAM | `0xa0430000`–`0xbffc0000` | data, `.bss`, heap. It ends below the library RAM that ZisK 1.4 reserves. |
| Cells | `0xbffbfff8`, `0xbffbfff0` | `g_zk_bump_ptr`, `g_zk_heap_floor` |

Both linker scripts put the code that contains the C# entry point near the
start of `.text`, so the call distance stays short for non-PIC near jumps.

**Entry.** `_start` sets `gp` and `sp` and calls musl's
`__libc_start_main(uBootstrap_main, 1, {"app", NULL})`. The `argv_vec` also
gives an empty envp and an auxv with only `AT_NULL`, so musl reads nothing
past it.

**Exit.** ZisK ends a program only on an `ecall` with `a7 = 93`
(`CAUSE_EXIT`). musl's `exit_group` (94) does not count, and the run stops
"not completed". `zkvm_platform_exit` issues the `a7 = 93` ecall.

**Syscalls and console.** ZisK emulates Linux syscalls, so
`zkvm_platform_syscall` tail-calls musl's `syscall`. The platform has the
`no-console` flag and no `zkvm_platform_write`: `Console` output is lost.
Write with `sys_write` of libziskos, which reaches the ZisK UART (the
call behind `Nethermind.Zkvm.Abstractions.IO.PrintLine`).

**Allocator bridge.** The entry object wraps the ziskos Rust allocator
(`__wrap_inline_bump_alloc_aligned`) to `pal`'s `zkvm_alloc_or_abort`. The
Rust allocator gives NULL to `handle_alloc_error`, whose panic handler is
`j .`: the guest would spin to the step limit. The bridge ends the guest.

**Post-link.** `patch_elf --fix-init-array --fix-tdata --trim-bss`, plus
`--remove-eh` when the build has no unwind tables and
`--print-fn-boundaries` with `-v`. Refer to
[Stage 3](architecture.md#stage-3--post-link-steps).

**Code generation**, in optimized builds only:

| Option | Effect |
|--------|--------|
| `JitRiscV64DmaCompare=1` | A constant-size `SequenceEqual` becomes the `csrrs rd, 0x814, src ; addi x0, dst, count` idiom, which the ZisK ROM transpiler folds into one `dma_xmemcmp` step |
| `JitZkCsrPInvokes=…` | The libziskos P/Invokes `syscall_keccak_f` (CSR `0x800`), `syscall_arith256` (`0x801`), `syscall_arith256_mod` (`0x802`), `syscall_sha256_f` (`0x805`), `memmove` and `memcpy` (`0x813`) and `zkvm_jump_dest_bitmap` (`0x81c`) become the single `csrrs` of the precompile, with no register kills |
| `JitZkMemsetCsr=18`, `JitZkMemcpyCsr=10` | Zero fills of 24 bytes and more, and unrolled copies of 16 bytes and more, become the ZisK memset and DMA memcpy idioms |
| `JitInlineAdditionalMultiplier=3`, `JitZkNoPinning`, `JitZkSynthesizeCounts`, `JitZkSpillKeepReg`, `JitZkColdCallSpill` | The zkVM tuning of the inliner and the register allocator |

Since ZisK 1.4, `syscall_sha256_f` takes two operands. The JIT does not
emit that shape yet, so its P/Invoke stays an ordinary call into libziskos.

These idioms are for the ZisK transpiler only. A real RISC-V CPU, SP1 and
OpenVM execute a CSR access and an `addi` to `x0`, so no other platform
sets these options. Their defaults are `0`.

The ZisK transpiler implements Zba, Zbb and Zbs. The platform enables them
only with a compiler package that has the Zb lowering fixes (dotnet-riscv#9),
which bflat reports as the `zb-lowering-fixes` capability. Zicond waits for
a ZisK release that decodes `czero`.

ZisK executes misaligned loads and stores, so the guest gets wide accesses.

`tests/run-zisk-bitmanip.sh`, `tests/run-zisk-hash-helpers.sh` and
`tests/run-unaligned-access.sh` build guests and check this code
generation in the disassembly.

<a id="zisk_sim"></a>

## zisk_sim

The ZisK entry point with a memory map for debugging. The guest runs under
`qemu-riscv64` or on RISC-V64 Linux, so you can use GDB. The platform is in
bflat-libziskos too.

| Item | Value |
|------|-------|
| Platform files | `platform/zisk_sim.bflat.manifest`, `zisk_sim.ld`, and `zisk_entry.o` of `zisk` |
| Bindings | none: the simulator links no libziskos |

```console
$ bflat build app.cs --os linux --arch riscv64 --libc zisk_sim --no-pie \
    --extlib path/to/zisk_sim.bflat.manifest -o app
$ qemu-riscv64 ./app
```

The differences from `zisk`:

- No post-link step: the Linux loader has none of the ZisK loader's rules.
- No `no-console` flag: the guest keeps the host console and host stdio.
- No ZisK idioms (`JitRiscV64DmaCompare`, `JitZkCsrPInvokes`, the memset
  and memcpy CSRs) and no Zb extensions.
- The linker script keeps `.debug*` and the other sections that `zisk.ld`
  discards.

**Why the image starts at `0x50000000`.** `pal/module.c` reaches the heap
symbols (`g_zk_bump_ptr`, `_kernel_heap_top`, `_kernel_heap_bottom`) with
PC-relative `auipc`/`addi` pairs, whose reach is ±2 GiB. From the usual
`0x10000` base, the cell at `0xbffbfff8` is about 2.68 GiB away and
`R_RISCV_PCREL_HI20` overflows. From `0x50000000`, the full
`0x50000000`–`0xbffc0000` span is in reach. The ZisK image avoids this
because its ROM is at `0x90000000`.

**The heap.** `.heap` is a `NOLOAD` segment at `0xa0020000`–`0xbffc0000`.
The Linux loader maps it as zero pages (`p_memsz > p_filesz`), as zkVM RAM
is zero at boot. Thus `g_zk_bump_ptr` starts at 0 and `pal` initializes it
on first use, as on ZisK, and the file stays small. The cells are at the
ZisK addresses, `0xbffbfff8` and `0xbffbfff0`.

If a `zisk_sim` build works and the same `zisk` build fails in ZisK, the
cause is almost always the post-link step of `zisk`. Run
`patch_elf.py --print-fn-boundaries` (or `bflat build -v`) to see the
function boundaries that it uses.

## sp1

[SP1](https://github.com/succinctlabs/sp1) v6,
`riscv64im-succinct-zkvm-elf`. The package is
[bflat-sp1](https://github.com/NethermindEth/bflat-sp1).

| Item | Value |
|------|-------|
| Platform files | `platform/sp1.bflat.manifest`, `sp1.ld`, `sp1_entry.o` (`entry.S`) |
| Bindings | `libsp1.bflat.manifest` with `libsp1.a`, `"when": {"platform": "sp1"}` |
| Define | `ZKVM_SP1` |
| Wraps | `main`, `memcpy`, `memmove`, `memset` |

```console
$ bflat build app.cs --os linux --arch riscv64 --libc sp1 --no-pie \
    --extlib path/to/libsp1.bflat.manifest -o app
```

bflat-sp1 builds the host runner that executes and proves a guest, and
publishes it as a Docker image. bflat-ts runs the SP1 guests with it.

**Memory map** (`sp1.ld`):

| Region | Address | Notes |
|--------|---------|-------|
| ROM | `0x7a000000`, length `0x06000000` | below 2^31, above SP1's `STACK_TOP` |
| Stack | below `_init_stack_top = 0x78000000` | the full range below it |
| RAM | `0xa0020000`–`0xbfff0000` | data, `.bss`, heap |
| Cells | `0xbffefff8`, `0xbffefff0` | `g_zk_bump_ptr`, `g_zk_heap_floor` |
| Address ceiling | 2^48 | |

**Why the ROM is at `0x7a000000`.** SP1's native x86 executor JITs the
guest, and `jump_to_pc` of `sp1-jit` narrows the code base with
`self.pc_base as i32`. A base of `0x80000000` becomes negative, the
jump-table index subtraction becomes an addition, and the first indirect
jump reads far past the table. SP1 reports `invalid memory access for opcode
ld and address 0`, with no hint of the cause. Thus the image is below 2^31.

**Segment flags.** The SP1 loader rejects a segment without `PF_R` and a
segment that is both writable and executable. It records `p_flags` as the
initial protection of each page. The three loadable segments are `R+X`, `R`
and `R+W`, and the read-only segment is page-aligned, so a page that it
shares with `.text` cannot lose its execute bit.

**Entry.** `_start` sets `gp` and `sp` (SP1 does not set `sp` for the
guest) and calls sp1-zkvm's `__start`. The guest must not bypass it:
`__start` initializes the Rust allocator and the public-values hasher that
`syscall_write` and `syscall_halt` need. `__start` then calls `main`, which
is the entry object's `__wrap_main`. It calls `noos_main`, the returning
half of `noos_start_main`, and gives its result back, because SP1 halts
with it. The `--wrap=main` is necessary because `libbootstrapper.o` also
defines `main`, which a zkVM guest never enters.

**Exit.** `zkvm_platform_exit` calls `zkvm_halt` of the bindings library.
Without the bindings, it issues the syscalls itself: `ecall` with the
syscall id in **`t0`**, `COMMIT` ×8, `COMMIT_DEFERRED_PROOFS` ×8, then
`HALT` with the code in `a0`.

**Syscalls and console.** `zkvm_platform_write` uses the `WRITE` syscall
and gives the descriptor of the caller (1 and 2 both reach the host). There
is no `zkvm_platform_syscall`: SP1 stops with an error on a syscall id that
it does not know, so `pal`'s `__wrap_syscall` returns `-1`.

**Alignment.** SP1 raises `InvalidMemoryAccess` for any `LH`, `LW`, `LD`,
`SH`, `SW` or `SD` that is not naturally aligned. The platform sets
`JitNoUnalignedAccess=1`, the byte-wise expansion that
`--no-unaligned-access` gives for any target, and `JitRiscV64StrictAlign=1`.
The first option covers the accesses that the JIT knows are unaligned. The
second covers the accesses whose address is a byref with an alignment that
is known only at run time: codegen emits an `andi`/`bnez` check, with the
wide access on the fast path and the byte-wise one on the slow path.
Without it, one mainnet block made more than 200k misaligned accesses at 44
sites.

**Post-link.** `patch_elf --nop-fences --nop-zero-words`. SP1 has no A
extension and rejects the FENCE encoding that musl emits. ILC pads between
functions with a zero word (no encoding) or `c.nop` (a compressed one).
Both passes change only words inside the function bodies of the symbol
table, and zero words only in gaps of 16 bytes or less that are fully zero.
A word outside a function is counted, reported and not changed.

## openvm

[OpenVM](https://github.com/openvm-org/openvm), the RV64 line (branch
`develop-v2.1.0-rv64`; `main` is RV32 and rejects a 64-bit ELF). The package
is [bflat-openvm](https://github.com/NethermindEth/bflat-openvm).

| Item | Value |
|------|-------|
| Platform files | `platform/openvm.bflat.manifest`, `openvm.ld`, `openvm_entry.o` (`entry.S`) |
| Bindings | `libopenvm.bflat.manifest` with `libopenvm.a`, `"when": {"platform": "openvm"}` |
| Define | `ZKVM_OPENVM` |
| Wraps | `memcpy`, `memmove`, `memset` |

```console
$ bflat build app.cs --os linux --arch riscv64 --libc openvm --no-pie \
    --extlib path/to/libopenvm.bflat.manifest -o app
```

bflat-openvm builds the host runner (`runner/`), with the extension set of
the guests, and publishes it as a Docker image. bflat-ts runs the OpenVM
guests with it.

**Memory map** (`openvm.ld`):

| Region | Address | Notes |
|--------|---------|-------|
| ROM | `0x80000000`, length `0x10000000` | |
| RAM | `0xa0020000`–`0xbfff0000` | data, `.bss`, stack, heap |
| Stack | `0x400000` above `.bss` | `_end` is above the stack. OpenVM's bump allocator starts at `_end` and would otherwise give out the stack. |
| Cells | `0xbffefff8`, `0xbffefff0` | as SP1 |
| Address ceiling | 2^48 | |

**Entry.** `_start` sets `gp` and `sp` and calls `noos_start_main`
([noos](modules.md#noos)), which sets the thread pointer, runs
`.init_array` and enters `uBootstrap_main`. musl's `__libc_start_main`
reads an auxv that this guest does not have, and costs 126 instructions
that the transpiler must accept.

**Exit.** `TERMINATE`, a custom-0 instruction (`0x0b`, funct3 0). Its exit
code is an **immediate**, so a code that is known only at run time becomes
0 or 1.

**Syscalls and console.** `zkvm_platform_write` uses the `PrintStr` phantom
instruction (`0x0b`, funct3 3, imm 1). There is one channel, so the
descriptor is ignored. OpenVM has no `ecall` handler, so there is no
`zkvm_platform_syscall` and `pal`'s `__wrap_syscall` returns `-1`.

**Alignment.** The rv64 load and store adapters of OpenVM accept any
address, and access a second memory block when the access crosses one. The
guest gets wide unaligned accesses, as on ZisK.

**Post-link.** None.

### What sp1 and openvm share

**Eager decoding of `.text`.** SP1 transpiles every word of every `PF_X`
segment when it loads the ELF, and panics on a word that it cannot decode.
OpenVM does the same and fails with `TranspilerError::ParseError`. A
zero-filled alignment gap is not a legal encoding, so both linker scripts
fill `.text` padding with `NOP` (`=0x13000000`) and keep `.rodata` and the
unwind tables in a separate segment that is not executable.

**The keccak permutation has three names.** Nethermind's `KeccakHash`
calls `Accelerators.KeccakF`, whose binding has the ziskos name
`syscall_keccak_f`. SP1 calls the same operation `zkvm_keccak_permute` and
OpenVM `zkvm_keccakf`. Each entry object has a one-instruction tail call
with the binding's name, in its own section. Thus `--gc-sections` removes
it, and its reference to the bindings library, from a guest that is built
without `--extlib`.

**`memcpy`, `memmove` and `memset`.** musl's generic C `memcpy` moves
32-bit words, and neither VM has the ZisK DMA copy. Each entry object
defines `__wrap_memcpy`, `__wrap_memmove` and `__wrap_memset`, which move
64-bit words. SP1 rejects misaligned accesses, so its `memcpy` aligns the
destination and merges a misaligned source from aligned words. OpenVM's
`memcpy` copies unaligned words and ends with the last word of the copy.
`memmove` gives every copy that is safe to run forward to `__wrap_memcpy`.
All three are weak, so a guest that links its own keeps it.

Both platforms run the nine mainnet blocks of Nethermind's
`stateless-tests` suite end to end, with the same output as ZisK. SP1
reports zero misaligned accesses.

## airbender

[ZKsync Airbender](https://github.com/matter-labs/zksync-airbender) v3. It
proves RV32IM with the ilp32 ABI. The package is
[bflat-airbender](https://github.com/NethermindEth/bflat-airbender).

| Item | Value |
|------|-------|
| Platform files | `platform/airbender.bflat.manifest`, `airbender.ld`, `airbender_entry.o` (`entry.S`) |
| Bindings | `libairbender.bflat.manifest` with `libairbender.a`: input and output, Keccak, SHA-256, BN254, BLS12-381 and the other precompiles, on Airbender's delegations |
| Define | `ZKVM_AIRBENDER` |
| Unwind tables | none (`"unwind": false`) |

The target needs a bflat built with the riscv32 target
(`-p:BflatTargetRiscV32=true`, and `./build.sh modules riscv32`; refer to
[BUILDING.md](https://github.com/NethermindEth/bflat-riscv64/blob/master/BUILDING.md)).

```console
$ bflat build app.cs --arch riscv32 --os linux --libc airbender --no-pie --no-pthread \
    --extlib path/to/libairbender.bflat.manifest -o app
$ "$AIRBENDER_CLI" run --bin app.bin --text app.text --input-file input.hex
```

`AIRBENDER_CLI` is the `cli` of zksync-airbender. The input file has the
byte length and then the bytes, as little-endian words in hex
(`tests/run-airbender.sh` shows how to write it).

The build writes three files: the ELF `app`, the flat ROM image `app.bin`,
and `app.text`, the instructions that Airbender decodes before the run.
`tests/build-airbender.sh` builds the test guests and
`tests/run-airbender.sh` runs them on the Airbender CLI. CI does this in the
"Run the Airbender guests" job, at the Airbender commit that
`AIRBENDER_COMMIT` gives.

**Memory map** (`airbender.ld`):

| Region | Address | Notes |
|--------|---------|-------|
| ROM | `0` | the flat image. v3 has 4 MiB (`AIRBENDER_ROM_SIZE`); the link fails if the image does not fit. `--defsym=AIRBENDER_ROM_SIZE=<bytes>` lets a larger image link for the simulator. |
| RAM | `64M`–`1G` | zero at the start. Data, `.bss`, a 16 MiB stack above `.bss`, then the heap. |
| Cells | top of RAM | `g_zk_bump_ptr`, `g_zk_heap_floor`. The platform sets no `JitZkBumpAddr`: the guest keeps the call to the allocation helper. |

**Entry.** Airbender reads instructions from ROM, and RAM starts zeroed.
Thus `_start` copies the initialized data from its load copy in ROM to RAM,
eight words a round. `.bss` needs no clear. Then `_start` calls
`noos_start_main`. A jump to address 0 (an empty code pointer) goes to the
target that `airbender_set_zero_jump_target` sets; before that, it faults.

**Exit.** `zkvm_platform_exit(0)` loads the 16 words of `airbender_output`
into `x10`–`x25` and spins. This is the `EXIT_SEQUENCE` that the Airbender
tools find in the binary. `write_output` of libairbender fills the words. A
code that is not 0 writes the cycle CSR, which no circuit satisfies, so the
run cannot be proven.

**Console.** `zkvm_platform_write` sends the bytes through the quasi-UART of
the non-determinism CSR (`0x7c0`). The prover ignores this output. There is
no `zkvm_platform_syscall`.

**No unwind tables.** The tables would take about half a MiB of the ROM.
The platform sets `"unwind": false`, so bflat builds every guest as with
`--remove-eh`: a `throw` goes to the guest's `ZkvmThrow` handler, or ends
the guest (refer to [rhp](modules.md#rhp)).

**Code generation.**

| Option | Effect |
|--------|--------|
| `JitRiscVUnsignedMulDiv=1` | v3 proves only the unsigned RV32M instructions. The JIT builds `mulh`, `mulhsu`, `div` and `rem` from unsigned ones. |
| `JitNoUnalignedAccess=1`, `JitRiscV64StrictAlign=0` | Airbender traps on a misaligned access. The JIT expands byte-wise the accesses that it knows are unaligned and those whose constant offset breaks the alignment. A run-time check of every access, as on SP1, doubles the code, which the 4 MiB ROM cannot hold. |
| `JitForceInlineMaxIL=200` | An `AggressiveInlining` callee with more than 512 bytes of IL is judged like any other |
| `JitZkHandlerConv`, `JitZkInlineCloneCheapArgs`, `JitZkNoCheapAddCSE`, `JitZkNoCastAwayGcTemp`, `JitZkForwardSubCheap`, `JitZkNoNullChecks`, `JitZkInitLocallocOnly`, `JitZkNewObjInPlace` | Tuning measured on the Nethermind stateless guest. `JitZkNoNullChecks` is sound because the ROM is at address 0 and a read of it never faults. |

The ZisK caps of `JitObjectStackAllocationSize` and `JitExtDefaultPolicyMax*`
stay at their defaults: on rv32 each inlined 64-bit operation takes twice
the code.

**Post-link.**

1. `patch_elf --nop-fences --unsigned-muldiv --unimp-system --unimp-csr 0xc22`.
   The native code has what Airbender cannot run: FENCE, and the signed RV32M
   instructions. The transpiler also decodes the full `.text` and rejects a
   SYSTEM encoding that it does not know: the `ebreak` after a call to a
   noreturn helper, musl's `ecall`s and the read of `vlenb` (`0xc22`) in the
   runtime's libunwind. These become UNIMP (`csrrw x0, cycle, x0`). The
   guest reaches none of them.
2. `flat_image --suffix .bin`: the ROM image.
3. `flat_image --suffix .text --section .text`: the instructions.

**Checks.** The platform turns on `--error-on-float-binary`,
`--error-on-compressed` and `--error-on-atomic` for every guest
(`checks`).

**Link.** There is no riscv32 GCC. The riscv32 layout has the compiler-rt
builtins (`libclang_rt.builtins-riscv32.a`) in place of `libgcc.a`, and the
riscv32 runtime has no OpenSSL and no ICU library.

## linux-x64

bflat keeps the stock NativeAOT targets. A release of dotnet-riscv with the
linux-x64 target (`BflatTargetLinuxX64`, the default; refer to
[the runtime](runtime.md#the-linux-x64-target)) gives an ordinary x64 Linux
program, with the full runtime and no zkVM module:

```console
$ bflat build app.cs --arch x64 --os linux
$ ./app
```

The program is dynamically linked against glibc, as a program of
`dotnet publish -p:PublishAot=true` is. This target uses the x64 JIT and
CoreLib of the same release, because Microsoft's x64 packages do not agree
with the JIT/EE interface of this ILCompiler. CI builds and runs one guest
in each image.

The layout also has the stock linux-musl-riscv64 target (`--arch riscv64
--libc musl`): a static riscv64 program with no platform and no zkVM
module. The other stock targets of upstream bflat (Windows, UEFI, arm64,
bionic, glibc riscv64) are not built (`bflat.build.props`).
