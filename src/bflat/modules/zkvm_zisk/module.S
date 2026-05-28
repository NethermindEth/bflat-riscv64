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

/*
 * Preinit snapshot restore trampoline. This is the ELF entry point (see
 * ENTRY(_zkvm_restore) in script.ld). It inspects the __zkvm_snapshot blob:
 *
 *   - empty (magic == 0): tail-jump to the normal _start, i.e. cold boot.
 *   - populated (magic == ZKSP): reload x1..x31 and jump to the captured PC.
 *
 * The blob is filled in post-link by `bflat rebake`. The warm RAM image is
 * baked as extra writable sections, which the Zisk loader places into RAM, so
 * this trampoline only has to restore the register file.
 *
 * Blob layout (little-endian, __zkvm_snapshot is the base):
 *   +0   magic u32 = 0x5A4B5350 ("ZKSP"), or 0 when unpatched
 *   +4   _pad  u32
 *   +8   pc    u64       captured program counter
 *   +16  regs  u64[32]   captured x0..x31 (x0 slot ignored)
 */
.global _zkvm_restore
_zkvm_restore:
    lla     t0, __zkvm_snapshot      # t0 = blob base (kept until the very end)
    lw      t1, 0(t0)                # t1 = magic (32-bit)
    li      t2, 0x5A4B5350           # "ZKSP"
    bne     t1, t2, .Lzkvm_cold      # no snapshot baked -> cold boot

    /* Restore x1..x31 (regs[i] at base + 16 + i*8). x5 (t0) is the blob base,
     * reloaded last; x6 (t1) carries the target PC for the jump. Both are
     * caller-saved temps, undefined at a function entry, so leaving
     * x6 != regs[6] at the jump is harmless. */
    ld      x1,   24(t0)
    ld      x2,   32(t0)
    ld      x3,   40(t0)
    ld      x4,   48(t0)
    ld      x7,   72(t0)
    ld      x8,   80(t0)
    ld      x9,   88(t0)
    ld      x10,  96(t0)
    ld      x11, 104(t0)
    ld      x12, 112(t0)
    ld      x13, 120(t0)
    ld      x14, 128(t0)
    ld      x15, 136(t0)
    ld      x16, 144(t0)
    ld      x17, 152(t0)
    ld      x18, 160(t0)
    ld      x19, 168(t0)
    ld      x20, 176(t0)
    ld      x21, 184(t0)
    ld      x22, 192(t0)
    ld      x23, 200(t0)
    ld      x24, 208(t0)
    ld      x25, 216(t0)
    ld      x26, 224(t0)
    ld      x27, 232(t0)
    ld      x28, 240(t0)
    ld      x29, 248(t0)
    ld      x30, 256(t0)
    ld      x31, 264(t0)
    ld      t1,   8(t0)              # t1 (x6) = captured PC
    ld      t0,  56(t0)              # t0 (x5) = regs[5]  (frees the base reg)
    jr      t1                       # resume warm at the snapshot PC

.Lzkvm_cold:
    tail    _start                   # unpatched build: normal cold boot

/*
 * Reserved space for the baked register blob, filled post-link by
 * `bflat rebake`. Lives in .rodata (read-only ROM segment). One page is
 * ample: the blob is a fixed 272 bytes (magic + pc + 32 registers).
 */
.section .rodata
    .balign 8
.global __zkvm_snapshot
__zkvm_snapshot:
    .zero 4096
