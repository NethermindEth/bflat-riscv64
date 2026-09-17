/**
 * @file
 * @brief Unit tests for rng_stupid/module.c. The RNG is deterministic BY
 *        DESIGN (zkVM guests must be reproducible); the tests pin the
 *        exact LCG stream, not just its shape.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include "common.h"

extern int
__wrap_minipal_get_cryptographically_secure_random_bytes(unsigned char *buffer,
                                                         int bufferLength);
extern void
__wrap_minipal_get_non_cryptographically_secure_random_bytes(
    unsigned char *buffer, int bufferLength);
extern int __wrap_CryptoNative_EnsureOpenSslInitialized(void);
extern int __wrap_CryptoNative_GetRandomBytes(unsigned char *buffer,
                                              int length);

/* Reference model of the module's LCG (same seed, same constants). */
static unsigned long ref_next = 0x34095153;
static unsigned char ref_byte(void)
{
    ref_next = ref_next * 1103515243 + 12345;
    return (unsigned char)(((ref_next / 65536) % 32768) % 0x100);
}

int main(void)
{
    CHECK(__wrap_CryptoNative_EnsureOpenSslInitialized() == 0);

    /* minipal entry point: fills the buffer with the pinned stream. */
    {
        unsigned char buf[64];
        memset(buf, 0xEE, sizeof(buf));
        CHECK(__wrap_minipal_get_cryptographically_secure_random_bytes(
                  buf, sizeof(buf)) == 0);
        int match = 1;
        for (unsigned i = 0; i < sizeof(buf); i++)
            match &= (buf[i] == ref_byte());
        CHECK(match);
    }

    /* CryptoNative entry point continues the SAME stream (shared state)
     * and reports success as 1. */
    {
        unsigned char buf[32];
        CHECK(__wrap_CryptoNative_GetRandomBytes(buf, sizeof(buf)) == 1);
        int match = 1;
        for (unsigned i = 0; i < sizeof(buf); i++)
            match &= (buf[i] == ref_byte());
        CHECK(match);
    }

    /* The non-cryptographic entry point is the one that matters most for a
     * proof: upstream XORs srand48(time(NULL)) into it, and it feeds
     * Marvin.GenerateSeed and HashCode.s_seed, so a stray wall-clock byte
     * there reorders every hash-ordered collection. It must be the same
     * deterministic stream, taken from the same shared state. */
    {
        unsigned char buf[48];
        memset(buf, 0xEE, sizeof(buf));
        __wrap_minipal_get_non_cryptographically_secure_random_bytes(
            buf, sizeof(buf));
        int match = 1;
        for (unsigned i = 0; i < sizeof(buf); i++)
            match &= (buf[i] == ref_byte());
        CHECK(match);
    }

    /* Two runs from the same point in the stream agree with each other -
     * the property a guest replaying the same proof depends on. */
    {
        unsigned char a[16], b[16];
        __wrap_minipal_get_non_cryptographically_secure_random_bytes(
            a, sizeof(a));
        for (unsigned i = 0; i < sizeof(a); i++)
            CHECK(a[i] == ref_byte());
        __wrap_minipal_get_non_cryptographically_secure_random_bytes(
            b, sizeof(b));
        int differs = 0;
        for (unsigned i = 0; i < sizeof(b); i++) {
            differs |= (b[i] != a[i]);
            CHECK(b[i] == ref_byte());
        }
        CHECK(differs); /* the stream advances rather than repeating */
    }

    /* Zero-length requests succeed and write nothing. */
    {
        unsigned char guard[4] = { 1, 2, 3, 4 };
        CHECK(__wrap_minipal_get_cryptographically_secure_random_bytes(guard,
                                                                       0)
              == 0);
        CHECK(__wrap_CryptoNative_GetRandomBytes(guard, 0) == 1);
        __wrap_minipal_get_non_cryptographically_secure_random_bytes(guard, 0);
        CHECK(guard[0] == 1 && guard[3] == 4);
    }

    TEST_MAIN_END("rng_stupid");
}
