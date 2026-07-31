/**
 * @file
 * @brief libFuzzer target for pal's __wrap_vfprintf (the FP-free printf).
 *
 * The fuzz input is the FORMAT STRING - the untrusted axis of a printf
 * implementation. Arguments are a fixed pool of pointers to static
 * strings: on LP64 varargs every conversion reads a 64-bit slot, so a
 * valid char* is safe for every conversion the parser can pick (%s
 * dereferences a real string, %d/%x/%p format the address, %c truncates
 * it, float conversions consume the slot unformatted). Inputs that could
 * consume more slots than the pool provides are rejected up front: each
 * conversion eats at most 3 slots ('%' with '*' width and '*' precision),
 * so #('%') + #('*') <= POOL is a safe bound.
 *
 * Build: clang -fsanitize=fuzzer,address,undefined (see run_fuzz.sh).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern int __wrap_vfprintf(FILE *f, const char *fmt, va_list ap);

/* pal's heap symbols: unused by vfprintf but required to link module.c. */
char _kernel_heap_bottom[4096];
uint8_t *g_zk_bump_ptr;
long __real_syscall(long number, ...) { (void)number; return 0; }

#define POOL 32

static int vf(FILE *f, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int r = __wrap_vfprintf(f, fmt, ap);
    va_end(ap);
    return r;
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    if (size > 512)
        return 0;

    /* Reject inputs that could out-consume the argument pool. */
    size_t slots = 0;
    for (size_t i = 0; i < size; i++)
        if (data[i] == '%' || data[i] == '*')
            slots++;
    if (slots > POOL / 3)
        return 0;

    /* Bound the PADDING WORK, not the parser: a width like %2000000000d
     * makes the pad loop emit two billion characters one fputc at a time.
     * That is ordinary printf semantics (musl behaves the same) rather
     * than a defect, but at ~4 seconds per unit it starves the fuzzer -
     * 15 execs/minute instead of millions. Reject formats whose numeric
     * width/precision runs exceed 4 digits, and reject '*' (its value
     * would come from a pointer-sized slot, i.e. an arbitrary huge int);
     * star widths are covered with controlled values by the unit tests. */
    size_t digits = 0;
    for (size_t i = 0; i < size; i++) {
        if (data[i] == '*')
            return 0;
        if (data[i] >= '0' && data[i] <= '9') {
            if (++digits > 4)
                return 0;
        } else {
            digits = 0;
        }
    }

    /* NUL-terminated, ASan-tracked copy of the format: overreads past the
     * end of the fuzz input become instant reports. */
    char *fmt = malloc(size + 1);
    if (!fmt)
        return 0;
    memcpy(fmt, data, size);
    fmt[size] = '\0';

    FILE *f = fopen("/dev/null", "w");
    if (f) {
        static const char *const s = "fuzz";
        int r = vf(f, fmt, s, s, s, s, s, s, s, s, s, s, s, s, s, s, s, s,
                   s, s, s, s, s, s, s, s, s, s, s, s, s, s, s, s);
        if (r < 0)
            __builtin_trap(); /* contract: never negative */
        fclose(f);
    }

    free(fmt);
    return 0;
}
