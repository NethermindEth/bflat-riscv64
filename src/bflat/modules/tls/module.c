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
#include <string.h>

/* Constants from musl. Weird, in fact must be inferred automatically. */
#define HEAP_SIZE       (100 * 1024)
#define PTHREAD_SIZE    (200)
#define TLS_SIZE        (2 * sizeof(void *) + PTHREAD_SIZE)
#define TLS_CNT         (1)
#define TLS_ALIGN       (4)

__attribute__((section(".data")))
static uint8_t  tls_storage_static[HEAP_SIZE]
    __attribute__((aligned(64))) = { 0 };

/*
 * tls_base points to the actual TLS data area (after pthread data).
 * tp points to the pthread data area (start of our TLS block).
 *
 * These are initialized lazily on first access to ensure they're valid
 * even if __tls_get_addr is called before __init_tls.
 */
static uint8_t *tls_base = NULL;
static uint8_t *tp = NULL;
static volatile int tls_initialized = 0;

extern uint8_t  __tdata_load[] __attribute__((weak));
extern const char __tdata_len[] __attribute__((weak));
extern const char __tbss_len[] __attribute__((weak));

extern int __set_thread_area(void *tp);

/*
 * Internal function to ensure TLS is initialized.
 * Can be called multiple times safely.
 * This function is designed to be safe to call from any context.
 */
extern
void
ensure_tls_initialized(void)
{
    /* Quick check without memory barrier for performance */
    if (tls_initialized)
        return;

    /* Set up the pointers first - this is safe even if called concurrently
     * since we're always setting them to the same values */
    tp = tls_storage_static;
    tls_base = tls_storage_static + PTHREAD_SIZE;

    uintptr_t tdata_len = (uintptr_t)__tdata_len;
    uintptr_t tbss_len = (uintptr_t)__tbss_len;

    /*
     * Copy initial TLS data if present (.tdata section).
     * This contains initialized thread-local variables.
     */
    if (tdata_len != 0 && __tdata_load != NULL)
        memcpy(tls_base, __tdata_load, tdata_len);

    /*
     * Zero-initialize .tbss area (uninitialized thread-local variables).
     * The storage is already zeroed since it's in .data section, but we
     * do this explicitly for correctness.
     */
    if (tbss_len != 0)
        memset(tls_base + tdata_len, 0, tbss_len);

    /* Mark as initialized */
    tls_initialized = 1;
}

uint8_t
__wrap___init_tp(void *p)
{
    __set_thread_area(p + PTHREAD_SIZE);
    return 0;
}

uint8_t *
__wrap___copy_tls(uint8_t *mem)
{
    /* Ensure TLS data is initialized */
    ensure_tls_initialized();
    return tp;
}

void
__wrap___init_tls(size_t *aux)
{
    ensure_tls_initialized();
    __wrap___init_tp(__wrap___copy_tls(tp));
}

void *
__wrap___tls_get_addr(size_t *v)
{
    if (tls_base == NULL)
        ensure_tls_initialized();

    if (v != NULL)
        return (void*)(tls_base + v[1]);
    return tls_base;
}
