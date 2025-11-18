/**
 * @file
 * @brief Simple TLS implementation that doesn't do a lot of ELF magic and can
 *        work with zkVMs.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>
#include <stddef.h>

/* Constants from musl. Weird, in fact must be inferred automatically. */
#define HEAP_SIZE       (100 * 1024)
#define PTHREAD_SIZE    (200)
#define TLS_SIZE        (2 * sizeof(void *) + PTHREAD_SIZE)
#define TLS_CNT         (1)
#define TLS_ALIGN       (4)

__attribute__((section(".data")))
static uint8_t  tls_storage_static[HEAP_SIZE]
    __attribute__((aligned(64))) = { 0 };
static uint8_t *tls_base = NULL;
static uint8_t *tp = NULL;
extern uint8_t *__tdata_load;

extern const char __tdata_len[];
extern const char __tbss_len[];

extern int __set_thread_area(void *tp);

uint8_t
__wrap___init_tp(void *p)
{
    __set_thread_area(p + PTHREAD_SIZE);
    return 0;
}

uint8_t *
__wrap___copy_tls(uint8_t *mem)
{
    uintptr_t *dtv;
    uint8_t   *pthread_data;
    uintptr_t  tdata_len = (uintptr_t )__tdata_len;
    uintptr_t  tbss_len = (uintptr_t)__tbss_len;

    tls_base = tls_storage_static;

    pthread_data = tls_storage_static;
    tls_base = pthread_data + PTHREAD_SIZE;
    if (tdata_len != 0)
        memcpy(tls_base, __tdata_load, tdata_len);
    memset(tls_base + tdata_len, 0, tbss_len);
    tp = pthread_data;

    return tp;
}

void
__wrap___init_tls(size_t *aux)
{
    __wrap___init_tp(__wrap___copy_tls(tp));
}

void *
__wrap___tls_get_addr(size_t *v)
{
#if 0
    return (void*)(tls_base + v[1]);
#endif
    return tls_base;
}
