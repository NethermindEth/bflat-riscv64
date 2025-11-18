/**
 * @file
 * @brief Redhawk Platform native (re)-implementation - write without
 *        write barrier
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
.globl __wrap_RhpAssignRefRiscV64
.type  __wrap_RhpAssignRefRiscV64, @function
__wrap_RhpAssignRefRiscV64:
    sd   t4, 0(t3)
    addi t3, t3, 8
    ret
