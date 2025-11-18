/**
 * @file
 * @brief ZKVM Zisk implementation.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
.option norelax
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
    #call    tls_init_once
    #mv      tp, a0

    la      a0, uBootstrap_main
    li      a1, 1            # argc = 1
    la      a2, argv_vec     # argv = &argv_vec[0]
    li      a3, 0
    li      a4, 0
    li      a5, 0
    mv      a6, sp
    call    __libc_start_main
    li      a7, 93             # exit syscall
    ecall
1:  j       1b                 # loop forever
    .cfi_endproc
