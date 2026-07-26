/**
 * @file
 * @brief Trivial implementation of Rust compatibility layer.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <stdint.h>

void *__libc_malloc_impl(unsigned long n);

void *__wrap_sys_alloc_aligned(int bytes, int align) {
    unsigned long n = (unsigned long)bytes;
    unsigned long a = (unsigned long)align;

    /* The bump allocator only guarantees 8-byte alignment, so the old stub
     * silently under-aligned any larger request (Rust passes a power-of-two
     * align for over-aligned types). Honor it by over-allocating and rounding
     * the payload up. The allocator never frees (free/dealloc are no-ops), so
     * returning a shifted pointer is safe. */
    if (a <= 8u)
        return __libc_malloc_impl(n);

    uintptr_t raw = (uintptr_t)__libc_malloc_impl(n + a);
    if (raw == 0)
        return 0;

    return (void *)((raw + (a - 1u)) & ~(uintptr_t)(a - 1u));
}
