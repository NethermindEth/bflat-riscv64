/**
 * @file
 * @brief Trivial RNG implementation with OpenSSL stub.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
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
