/**
 * @file
 * @brief Trivial RNG implementation with OpenSSL stub.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <stdint.h>

static unsigned long _next = 0x34095153;

/*@ // Deterministic LCG step (deliberately: a zkVM guest must be
    // reproducible, so "cryptographically secure" collapses to a fixed
    // pseudo-random stream). Unsigned overflow wraps mod 2^64 by C
    // semantics.
    assigns _next;
    ensures _next == (unsigned long)(\old(_next) * 1103515243 + 12345);
    ensures 0 <= \result < 32768;
*/
static unsigned long get_val(void)
{
    _next = _next * 1103515243 + 12345;
    return (unsigned long)(_next / 65536) % 32768;
}

/*@ requires bufferLength >= 0;
    requires \valid(buffer + (0 .. bufferLength - 1));
    assigns buffer[0 .. bufferLength - 1], _next;
    ensures \result == 0;
*/
int
__wrap_minipal_get_cryptographically_secure_random_bytes(unsigned char *buffer, int bufferLength)
{
    for (int i = 0; i < bufferLength; i++)
        buffer[i] = (unsigned char)(get_val() % 0x100);
    return 0;
}

/*@ // The runtime's non-cryptographic RNG XORs srand48(time(NULL)) over the
    // secure bytes (minipal/random.c), which reintroduces wall-clock entropy
    // even though the secure path above is deterministic. This is the source
    // feeding Marvin.GenerateSeed (string.GetHashCode) and HashCode.s_seed, so
    // an un-pinned value makes hash-ordered iteration (dictionary/set/frozen)
    // non-reproducible across runs - fatal for a zkVM proof. Divert it to the
    // same deterministic LCG. SystemNative_GetNonCryptographicallySecureRandomBytes
    // reaches this symbol internally, so the single wrap covers both entries.
    requires bufferLength >= 0;
    requires \valid(buffer + (0 .. bufferLength - 1));
    assigns buffer[0 .. bufferLength - 1], _next;
*/
void
__wrap_minipal_get_non_cryptographically_secure_random_bytes(unsigned char *buffer, int bufferLength)
{
    for (int i = 0; i < bufferLength; i++)
        buffer[i] = (unsigned char)(get_val() % 0x100);
}

/*@ // "OpenSSL is ready" - there is no OpenSSL; the RNG entry points above
    // and below are the only consumers.
    assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_CryptoNative_EnsureOpenSslInitialized(void)
{
    return 0;
}

/*@ // 1 == success in the CryptoNative convention (vs 0 above for minipal).
    requires length >= 0;
    requires \valid(buffer + (0 .. length - 1));
    assigns buffer[0 .. length - 1], _next;
    ensures \result == 1;
*/
int
__wrap_CryptoNative_GetRandomBytes(unsigned char *buffer, int length)
{
    for (int i = 0; i < length; i++)
        buffer[i] = (unsigned char)(get_val() % 0x100);
    return 1;
}

#ifdef RNG_STUPID_OPENSSL_ERR
/*
 * The riscv32 runtime ships no System.Security.Cryptography.Native.OpenSsl,
 * yet the managed crypto interop still references its error helpers and
 * CryptoNative_GetMaxMdSize, and the link fails without them. Nothing here
 * ever fails, so there is never an error to report.
 */

/*@ requires isAllocFailure == \null || \valid(isAllocFailure);
    assigns *isAllocFailure;
    ensures isAllocFailure != \null ==> *isAllocFailure == 0;
    ensures \result == 0;
*/
uint64_t
CryptoNative_ErrGetExceptionError(int32_t *isAllocFailure)
{
    if (isAllocFailure != 0)
        *isAllocFailure = 0;
    return 0;
}

/*@ requires len <= 0 || \valid(buf + (0 .. len - 1));
    assigns buf[0];
    ensures len > 0 ==> buf[0] == 0;
*/
void
CryptoNative_ErrErrorStringN(uint64_t e, char *buf, int32_t len)
{
    (void)e;
    if (len > 0)
        buf[0] = 0;
}

/*@ assigns \nothing;
    ensures \result == 64;
*/
int32_t
CryptoNative_GetMaxMdSize(void)
{
    return 64; /* EVP_MAX_MD_SIZE */
}
#endif
