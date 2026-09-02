/**
 * @file
 * @brief ZKVM SP1 implementation.
 *
 * SP1 (succinctlabs/sp1 v6+) proves the riscv64im-succinct-zkvm-elf target:
 * RV64IM, lp64, no A and no C extension. The loader
 * (crates/core/executor/src/disassembler/elf.rs) reads PT_LOAD program
 * headers only, takes the entry from e_entry, and does NOT set up sp -
 * _start must point it at the stack itself, exactly as SP1's own Rust
 * entrypoint does (crates/zkvm/entrypoint/src/lib.rs).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
.option norelax

/* Marker read (as a weak symbol reference) by pal/module.c and rhp/module.c
 * to pick SP1's syscall protocol - halt via t0 = HALT, console writes via
 * t0 = WRITE - out of the shared objects those modules are built as. The
 * value is never dereferenced, only tested against NULL. */
.global __zkvm_target_sp1
.set    __zkvm_target_sp1, 1

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
    # pal/module.c wraps into the full SP1 halt protocol. Kept as a
    # backstop - a bare HALT ecall (syscall id in t0, exit code in a0).
    li      t0, 0            # SP1 HALT
    li      a0, 0
    ecall
1:  j       1b               # loop forever
    .cfi_endproc
