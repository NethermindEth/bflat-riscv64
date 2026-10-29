/**
 * @file
 * @brief ZKVM Airbender implementation.
 *
 * Airbender (matter-labs/zksync-airbender) proves RV32IM: ilp32, no A, no C,
 * and every load and store naturally aligned. A program is a flat image whose
 * instructions the circuit reads from a ROM at address 0 (2 MiB in every
 * machine configuration); RAM starts zeroed, so nothing initialised can live
 * there until this entry point copies it in. The bindings library
 * (bflat-airbender) owns the rest of the protocol: input through the
 * non-determinism CSR, the published output left in x10-x25 when the run
 * ends, and failure by writing the cycle CSR.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
.option norelax

/* Marker read (as a weak symbol reference) by pal/module.c to pick
 * Airbender's exit and console protocol out of the shared objects. The value
 * is never dereferenced, only tested against NULL. */
.global __zkvm_target_airbender
.set    __zkvm_target_airbender, 1

.section .rodata
    .balign 4
app_str:
    .asciz "app"

    .section .data
    .balign 4
argv_vec:
    .word app_str
    .word 0

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

    /* Initialised RAM: one block linked to run in RAM and loaded in ROM, which
     * the delegations cannot read. Eight words a round, then the rest one by
     * one: without .NET's data rehydration the block is hundreds of KiB. */
    la      t0, __ram_init_load
    la      t1, __ram_init_start
    la      t2, __ram_init_end
    addi    t4, t2, -32
    bgtu    t1, t4, 3f
1:  lw      t3, 0(t0)
    lw      t5, 4(t0)
    lw      t6, 8(t0)
    lw      a2, 12(t0)
    lw      a3, 16(t0)
    lw      a4, 20(t0)
    lw      a5, 24(t0)
    lw      a6, 28(t0)
    sw      t3, 0(t1)
    sw      t5, 4(t1)
    sw      t6, 8(t1)
    sw      a2, 12(t1)
    sw      a3, 16(t1)
    sw      a4, 20(t1)
    sw      a5, 24(t1)
    sw      a6, 28(t1)
    addi    t0, t0, 32
    addi    t1, t1, 32
    bleu    t1, t4, 1b
3:  bgeu    t1, t2, 2f
    lw      t3, 0(t0)
    sw      t3, 0(t1)
    addi    t0, t0, 4
    addi    t1, t1, 4
    j       3b
2:
    /* .bss needs no clearing: RAM starts zeroed, in the circuit (its memory
     * argument fixes the initial value at 0) and in the simulator alike, and
     * nothing runs before this entry point. It is megabytes. */
    /* noos_start_main replaces musl's __libc_start_main: thread pointer,
     * .init_array, then the .NET start-up. It ends through musl's exit(),
     * which pal wraps into airbender_exit. */
    li      a0, 1            # argc = 1
    la      a1, argv_vec     # argv = &argv_vec[0]
    call    noos_start_main
5:  j       5b               # unreachable: noos_start_main never returns
    .cfi_endproc
    .size   _start, .-_start
