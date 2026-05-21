/**
 * @file
 * @brief Redhawk Platform native (re)-implementation - write without
 *        write barrier
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
/*
 * uGC has no card table and never collects, so every GC write barrier is
 * dead. These reference-assignment helpers are reduced to the bare store
 * (plus the pointer post-increment the RISC-V runtime convention expects),
 * dropping the heap-bounds check and card-mark of the originals.
 * Convention: t3 = destination, t4 = value, t5 = source (byref copy).
 */
.globl __wrap_RhpAssignRefRiscV64
.type  __wrap_RhpAssignRefRiscV64, @function
__wrap_RhpAssignRefRiscV64:
    sd   t4, 0(t3)
    addi t3, t3, 8
    ret

.globl __wrap_RhpCheckedAssignRef
.type  __wrap_RhpCheckedAssignRef, @function
__wrap_RhpCheckedAssignRef:
    sd   t4, 0(t3)
    addi t3, t3, 8
    ret

.globl __wrap_RhpByRefAssignRef
.type  __wrap_RhpByRefAssignRef, @function
__wrap_RhpByRefAssignRef:
    ld   t4, 0(t5)
    addi t5, t5, 8
    sd   t4, 0(t3)
    addi t3, t3, 8
    ret

.globl __wrap_RhpAssignRef
.type  __wrap_RhpAssignRef, @function
__wrap_RhpAssignRef:
    sd   a1, 0(a0)
    ret
