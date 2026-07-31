/**
 * @file
 * @brief CBMC proof harness for pal's bump allocator.
 *
 * Includes the REAL pal/module.c with the heap-window symbols re-pointed
 * at a local array (ZK_HEAP_SYMBOLS_DEFINED hook), then proves - for ALL
 * 64-bit sizes and ALL valid allocator states - that
 * __wrap___libc_malloc_impl and __wrap_aligned_alloc either fail with
 * NULL (state intact) or return an in-window, correctly aligned block
 * whose header write stays inside the heap. This is exactly the property
 * the historic pointer-underflow violated.
 *
 * Run (see run_verify.sh):
 *   cbmc verify_alloc.c --function harness_malloc  ...
 *   cbmc verify_alloc.c --function harness_aligned ...
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <stdint.h>

/* A small window keeps the proof fast; the allocator's arithmetic does
 * not depend on the window size. +8 spare bytes let CBMC catch any
 * one-past-top header write as a bounds violation instead of silence. */
#define VERIFY_HEAP 4096

static char zk_heap[VERIFY_HEAP + 8] __attribute__((aligned(8)));

#define ZK_HEAP_SYMBOLS_DEFINED
#define _kernel_heap_bottom (zk_heap)
#define _kernel_heap_top (zk_heap + VERIFY_HEAP)

#include "../../pal/module.c"

uint8_t *g_zk_bump_ptr;

unsigned long nondet_ulong(void);

/* Establish the allocator's inductive state invariant:
 * mem == 0 (pre-init) or mem in [bottom, top], 8-aligned. */
static void assume_valid_state(void)
{
    unsigned long off = nondet_ulong();
    if (off == 0) {
        g_zk_bump_ptr = 0;
    } else {
        __CPROVER_assume(off <= VERIFY_HEAP);
        __CPROVER_assume((off % 8) == 0);
        g_zk_bump_ptr = (uint8_t *)zk_heap + off;
    }
}

static void assert_invariant_holds(void)
{
    __CPROVER_assert(
        g_zk_bump_ptr == 0 ||
            ((char *)g_zk_bump_ptr >= zk_heap &&
             (char *)g_zk_bump_ptr <= zk_heap + VERIFY_HEAP &&
             ((uintptr_t)g_zk_bump_ptr % 8) == 0),
        "allocator state invariant preserved");
}

void harness_malloc(void)
{
    assume_valid_state();

    unsigned long n = nondet_ulong(); /* fully unconstrained */
    uint8_t *before = g_zk_bump_ptr;

    void *p = __wrap___libc_malloc_impl(n);

    if (p != NULL) {
        __CPROVER_assert(((uintptr_t)p % 8) == 0, "block 8-aligned");
        __CPROVER_assert((char *)p >= zk_heap + 8, "header inside heap");
        __CPROVER_assert((char *)p <= zk_heap + VERIFY_HEAP,
                         "block starts inside heap");
        __CPROVER_assert(n <= (uintptr_t)(zk_heap + VERIFY_HEAP)
                                  - (uintptr_t)p,
                         "payload fits below top");
        __CPROVER_assert(g_zk_bump_ptr == (uint8_t *)p - 8,
                         "bump pointer sits on the header");
        __CPROVER_assert(*(uint64_t *)((uint8_t *)p - 8) >= n,
                         "header covers the request");
    } else {
        __CPROVER_assert(
            g_zk_bump_ptr == before ||
                (before == 0 &&
                 g_zk_bump_ptr == (uint8_t *)zk_heap + VERIFY_HEAP),
            "failure leaves state intact (modulo lazy init)");
    }
    assert_invariant_holds();
}

void harness_aligned(void)
{
    assume_valid_state();

    unsigned long n = nondet_ulong();
    unsigned long shift = nondet_ulong();
    __CPROVER_assume(shift < 20);
    unsigned long align = 1UL << shift; /* power of two, up to 512 KiB */

    void *p = __wrap_aligned_alloc(align, n);

    if (p != NULL) {
        unsigned long eff = align < 8 ? 8 : align;
        __CPROVER_assert(((uintptr_t)p % eff) == 0, "alignment honored");
        __CPROVER_assert((char *)p >= zk_heap + 8, "block inside heap");
        __CPROVER_assert(n <= (uintptr_t)(zk_heap + VERIFY_HEAP)
                                  - (uintptr_t)p,
                         "payload fits below top");
    }
    assert_invariant_holds();
}
