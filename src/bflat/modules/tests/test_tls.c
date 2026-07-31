/**
 * @file
 * @brief Unit tests for tls/module.c. The .tdata/.tbss section lengths are
 *        link-time symbol ADDRESSES in the real image; run_tests.sh
 *        recreates that with --defsym (__tdata_len=16, __tbss_len=32),
 *        while __tdata_load is a real array defined here.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>
#include <stddef.h>

#include "common.h"

#define PTHREAD_SIZE 200 /* must match tls/module.c */
#define TDATA_LEN 16     /* must match --defsym in run_tests.sh */

/* Initial thread-local data image (real image: loaded .tdata). */
uint8_t __tdata_load[TDATA_LEN] = { 0xC0, 0xFF, 0xEE, 3, 4, 5, 6, 7,
                                    8, 9, 10, 11, 12, 13, 14, 0xAB };

/* musl internal the module installs the thread pointer through. */
static void *set_thread_area_arg;
static int set_thread_area_calls;
int __set_thread_area(void *tp)
{
    set_thread_area_arg = tp;
    set_thread_area_calls++;
    return 0;
}

extern void ensure_tls_initialized(void);
extern uint8_t __wrap___init_tp(void *p);
extern uint8_t *__wrap___copy_tls(uint8_t *mem);
extern void __wrap___init_tls(size_t *aux);
extern void *__wrap___tls_get_addr(size_t *v);

int main(void)
{
    /* Full startup path first: sets up the block and installs tp. */
    __wrap___init_tls(NULL);
    CHECK(set_thread_area_calls == 1);

    uint8_t *tp = __wrap___copy_tls((uint8_t *)0xdead); /* arg is ignored */
    CHECK(tp != NULL);

    /* tp -> pthread area, TLS data PTHREAD_SIZE above it; the installed
     * thread pointer is exactly the TLS data base. */
    uint8_t *base = __wrap___tls_get_addr(NULL);
    CHECK(base == tp + PTHREAD_SIZE);
    CHECK(set_thread_area_arg == base);

    /* .tdata image copied into the block */
    CHECK(memcmp(base, __tdata_load, TDATA_LEN) == 0);

    /* .tbss region above .tdata is zero */
    int tbss_zero = 1;
    for (int i = 0; i < 32; i++)
        tbss_zero &= (base[TDATA_LEN + i] == 0);
    CHECK(tbss_zero);

    /* v = {module id, offset}: module id ignored, offset honored */
    {
        size_t v0[2] = { 1, 0 };
        size_t v5[2] = { 77, 5 };
        CHECK(__wrap___tls_get_addr(v0) == (void *)base);
        CHECK(__wrap___tls_get_addr(v5) == (void *)(base + 5));
        CHECK(*(uint8_t *)__wrap___tls_get_addr(v5) == __tdata_load[5]);
    }

    /* Idempotence: repeated init keeps the same block and does not
     * re-copy over runtime mutations. */
    {
        base[3] = 0x77;
        ensure_tls_initialized();
        CHECK(base[3] == 0x77);
        CHECK(__wrap___copy_tls(NULL) == tp);
    }

    /* __init_tp installs an arbitrary block's TLS base. */
    {
        uint8_t block[PTHREAD_SIZE + 8];
        CHECK(__wrap___init_tp(block) == 0);
        CHECK(set_thread_area_arg == block + PTHREAD_SIZE);
    }

    TEST_MAIN_END("tls");
}
