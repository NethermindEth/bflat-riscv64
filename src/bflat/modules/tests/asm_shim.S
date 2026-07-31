/**
 * @file
 * @brief Register-convention shims for testing assembly modules.
 *
 * The write-barrier helpers in rhp_native take their arguments in t3/t4/t5
 * rather than a0..a7, which the C ABI cannot express. This shim marshals a
 * struct into those registers, calls the routine, and writes the resulting
 * t3/t4/t5 back out so the test can assert on the post-state (the t3
 * post-increment is a versioned part of the contract).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */

/*
 * void call_barrier(struct tregs *r, void (*fn)(void));
 *   a0 = &r (in: t3,t4,t5 to load; out: their values after the call)
 *   a1 = routine under test
 *
 * The routine is held in t6 (x31): the barriers touch only t3/t4/t5 and
 * memory, so t6 and sp survive the call. The struct pointer is spilled
 * anyway - a future barrier revision must not be able to corrupt the
 * harness silently.
 */
    .globl call_barrier
    .type  call_barrier, @function
call_barrier:
    addi sp, sp, -16
    sd   ra, 0(sp)
    sd   a0, 8(sp)

    mv   t6, a1
    ld   t3, 0(a0)
    ld   t4, 8(a0)
    ld   t5, 16(a0)

    jalr t6

    ld   a0, 8(sp)
    sd   t3, 0(a0)
    sd   t4, 8(a0)
    sd   t5, 16(a0)

    ld   ra, 0(sp)
    addi sp, sp, 16
    ret
    .size call_barrier, . - call_barrier
