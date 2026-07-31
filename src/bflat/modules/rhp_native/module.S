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
 * dead. These reference-assignment helpers are reduced to the bare store,
 * dropping the heap-bounds check and card-mark of the originals.
 * Convention: t3 = destination, t4 = value, t5 = source (byref copy).
 *
 * The single-assign helpers' t3 post-increment is CONTRACT-VERSIONED. .NET 10's
 * riscv64 WriteBarriers.S documents "On exit: t3 incremented by 8" and the JIT
 * consumes that; .NET 11 (preview7) changed the contract to "t3 preserved" and
 * the JIT now REUSES t3 after the call. A stale +8 under .NET 11 makes the very
 * next reuse land 8 bytes high - e.g. Dictionary`2<__Canon,TValue>.TryInsert
 * stores the key via this helper then writes `entry.value` at 0x10(t3): with
 * the increment the value lands in the next entry's slot and the dictionary
 * silently loses every value. BFLAT_DOTNET is passed by build.sh (--defsym);
 * default to 10 so a bare assemble keeps the legacy increment.
 */
.ifndef BFLAT_DOTNET
.set BFLAT_DOTNET, 10
.endif

.globl __wrap_RhpAssignRefRiscV64
.type  __wrap_RhpAssignRefRiscV64, @function
__wrap_RhpAssignRefRiscV64:
    sd   t4, 0(t3)
.if BFLAT_DOTNET < 11
    addi t3, t3, 8              /* .NET 10 contract: caller expects t3 += 8 */
.endif
    ret

.globl __wrap_RhpCheckedAssignRef
.type  __wrap_RhpCheckedAssignRef, @function
__wrap_RhpCheckedAssignRef:
    sd   t4, 0(t3)
.if BFLAT_DOTNET < 11
    addi t3, t3, 8              /* .NET 10 contract: caller expects t3 += 8 */
.endif
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
