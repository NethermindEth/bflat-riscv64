/**
 * @file
 * @brief ZKVM OpenVM implementation.
 *
 * OpenVM's RV64 line (openvm-org/openvm, branch develop-v2.1.0-rv64) proves
 * RV64IM with lp64 and no A/C extension. The transpiler
 * (crates/toolchain/transpiler/src/elf.rs) reads PT_LOAD program headers,
 * requires a 64-bit ELF, and does NOT set up sp - _start points it at the
 * stack itself, as OpenVM's own Rust entrypoint does
 * (crates/toolchain/openvm/src/lib.rs).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
.option norelax

/* Marker read (as a weak symbol reference) by pal/module.c and rhp/module.c
 * to pick OpenVM's custom-instruction protocol - TERMINATE and the PrintStr
 * phantom - out of the shared objects those modules are built as. The value
 * is never dereferenced, only tested against NULL. */
.global __zkvm_target_openvm
.set    __zkvm_target_openvm, 1

.section .rodata
    .balign 8
app_str:
    .asciz "app"

    .section .data
    .balign 8
argv_vec:
    .dword app_str
    .dword 0

.section .text.init
.global _start
    .cfi_sections .eh_frame
_start:
    .cfi_startproc
    .cfi_def_cfa sp, 0
    .cfi_undefined ra
    .option push
    .option norelax
    la      gp, _global_pointer
    .option pop
    la      sp, _init_stack_top

    la      a0, uBootstrap_main
    li      a1, 1            # argc = 1
    la      a2, argv_vec     # argv = &argv_vec[0]
    li      a3, 0
    li      a4, 0
    li      a5, 0
    mv      a6, sp
    call    __libc_start_main
    # Unreachable: __libc_start_main exits through musl's exit(), which
    # pal/module.c wraps into the OpenVM TERMINATE instruction. Kept as a
    # backstop - TERMINATE with exit code 0 (custom-0 opcode 0x0b, funct3 0,
    # rd = rs1 = x0, imm = exit code; see openvm_platform::rust_rt).
    .insn   i 0x0b, 0, x0, x0, 0
1:  j       1b               # loop forever
    .cfi_endproc
