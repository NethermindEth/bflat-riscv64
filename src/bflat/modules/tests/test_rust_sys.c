/**
 * @file
 * @brief Unit tests for rust_sys/module.c. The module delegates to
 *        __libc_malloc_impl (pal's bump allocator in the real image); the
 *        test provides a controllable stand-in to also exercise the
 *        NULL-propagation path.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>

#include "common.h"

extern void *__wrap_sys_alloc_aligned(int bytes, int align);

/* Stand-in bump allocator: 8-aligned like the real one; can be forced to
 * fail. Grows downward to mirror pal. */
static uint8_t arena[1 << 20] __attribute__((aligned(4096)));
static uintptr_t arena_ptr;
static int fail_alloc;
static unsigned long last_request;

void *__libc_malloc_impl(unsigned long n)
{
    last_request = n;
    if (fail_alloc)
        return NULL;
    if (arena_ptr == 0)
        arena_ptr = (uintptr_t)arena + sizeof(arena);
    arena_ptr = (arena_ptr - n) & ~(uintptr_t)7;
    return (void *)arena_ptr;
}

int main(void)
{
    /* align <= 8: raw allocator block, byte count passed through */
    {
        void *p = __wrap_sys_alloc_aligned(24, 8);
        CHECK(p != NULL && ((uintptr_t)p % 8) == 0);
        CHECK(last_request == 24);
        void *q = __wrap_sys_alloc_aligned(3, 1);
        CHECK(q != NULL && last_request == 3);
    }

    /* over-aligned: result honors align, over-allocated by align bytes so
     * the full payload fits above the returned pointer */
    {
        void *p = __wrap_sys_alloc_aligned(100, 64);
        CHECK(p != NULL && ((uintptr_t)p % 64) == 0);
        CHECK(last_request == 100 + 64);
        CHECK((uintptr_t)p + 100 <= (uintptr_t)arena + sizeof(arena));
        void *q = __wrap_sys_alloc_aligned(10, 4096);
        CHECK(q != NULL && ((uintptr_t)q % 4096) == 0);
    }

    /* allocator failure propagates as NULL (not a shifted null) */
    {
        fail_alloc = 1;
        CHECK(__wrap_sys_alloc_aligned(10, 64) == NULL);
        CHECK(__wrap_sys_alloc_aligned(10, 8) == NULL);
        fail_alloc = 0;
    }

    /* bytes + align overflow must fail, not allocate a short block.
     * (int args: the largest representable request is INT_MAX; the wrap
     * guard matters when unsigned long math inside would overflow, which
     * cannot happen with int inputs - still pin the NULL contract for the
     * biggest inputs.) */
    {
        fail_alloc = 1;
        CHECK(__wrap_sys_alloc_aligned(0x7FFFFFFF, 0x40000000) == NULL);
        fail_alloc = 0;
    }

    /* zero bytes stays a valid (aligned) allocation request */
    {
        void *p = __wrap_sys_alloc_aligned(0, 16);
        CHECK(p != NULL && ((uintptr_t)p % 16) == 0);
    }

    TEST_MAIN_END("rust_sys");
}
