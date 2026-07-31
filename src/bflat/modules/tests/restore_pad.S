/**
 * @file
 * @brief Landing pad for the _zkvm_restore warm-resume test.
 *
 * _zkvm_restore ends with `jr t1` to the captured PC; the snapshot under
 * test names this symbol. On entry the register file is whatever the
 * trampoline restored, so the pad must not assume a valid frame.
 *
 * a0 (restored from regs[10]) is the base of a 32-slot buffer. Every store
 * addresses it directly and a0 itself is never modified before the sweep
 * finishes - so slots 5, 6 and 10 hold the values the pad was ENTERED
 * with, which is exactly what the x5/x6 endgame of the trampoline needs
 * checked. Then _exit(31).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
    .globl restore_pad
    .type  restore_pad, @function
restore_pad:
    sd   x0,   0(a0)
    sd   x1,   8(a0)
    sd   x2,  16(a0)
    sd   x3,  24(a0)
    sd   x4,  32(a0)
    sd   x5,  40(a0)
    sd   x6,  48(a0)
    sd   x7,  56(a0)
    sd   x8,  64(a0)
    sd   x9,  72(a0)
    sd   x10, 80(a0)            /* a0 still untouched: its entry value */
    sd   x11, 88(a0)
    sd   x12, 96(a0)
    sd   x13, 104(a0)
    sd   x14, 112(a0)
    sd   x15, 120(a0)
    sd   x16, 128(a0)
    sd   x17, 136(a0)
    sd   x18, 144(a0)
    sd   x19, 152(a0)
    sd   x20, 160(a0)
    sd   x21, 168(a0)
    sd   x22, 176(a0)
    sd   x23, 184(a0)
    sd   x24, 192(a0)
    sd   x25, 200(a0)
    sd   x26, 208(a0)
    sd   x27, 216(a0)
    sd   x28, 224(a0)
    sd   x29, 232(a0)
    sd   x30, 240(a0)
    sd   x31, 248(a0)

    li   a0, 31                 /* RESUMED */
    li   a7, 93                 /* __NR_exit */
    ecall
1:  j    1b
    .size restore_pad, . - restore_pad
