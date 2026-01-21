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

/*
 * RhpCidResolve trampoline
 *
 * This wrapper is intended to bypass the CachedInterfaceDispatch fast path and
 * route interface dispatch resolution through a non-cached resolver.
 */
.globl __wrap_RhpCidResolve
.type  __wrap_RhpCidResolve, @function
__wrap_RhpCidResolve:
    /* Tail-call into the C helper. Arguments already in a0/a1. */
    tail __rhp_cid_resolve_nocache
