/**
 * @file
 * @brief Unit tests for rhp/module.c. Built twice by run_tests.sh:
 *        without WITH_ZKVM_THROW the weak ZkvmThrow is unresolved and
 *        RhpThrowEx must exit(1); with it, the handler receives the
 *        exception object and control returns.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <stddef.h>

#include "common.h"

extern void __wrap_RhpPInvoke(void *pFrame);
extern void __wrap_RhpPInvokeReturn(void *pFrame);
extern void __wrap_RhBulkMoveWithWriteBarrier(void *dest, void *src,
                                              size_t len);
extern void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventPipeEventProvider__Register(
    void);
extern void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventSource__InitializeDefaultEventSources(
    void);
extern int
__wrap_System_Console_Interop_Sys__InitializeTerminalAndSignalHandling(void);
extern void __wrap_SystemNative_SetTerminalInvalidationHandler(void *param);
extern int __wrap_SystemNative_Write(int fd, const void *buffer,
                                     int bufferSize);
extern int __wrap_SystemNative_FStat(long fd, void *output);
extern int __wrap_SystemNative_IsATty(long fd);
extern long __wrap_SystemNative_Dup(long oldfd);
extern void __wrap_RhpReversePInvoke(void *pFrame);
extern void __wrap_RhpReversePInvokeReturn(void *pFrame);
extern void __wrap_RhpThrowEx(void *exceptionObj);
extern void __wrap_S_P_CoreLib_System_RuntimeExceptionHelpers__FailFast(void);
extern int
__wrap_System_Collections_Concurrent_System_Collections_HashHelpers__IsPrime(
    int candidate);
extern int
__wrap_S_P_CoreLib_System_Collections_HashHelpers__IsPrime(int candidate);
extern int
__wrap_System_Collections_Immutable_System_Collections_HashHelpers__IsPrime(
    int candidate);
extern int
__wrap_System_Collections_Immutable_System_Collections_Frozen_FrozenHashTable__CalcNumBuckets(
    void *hashCodesRef, long hashCodesLength, int hashCodesAreUnique);

#define IsPrimeConcurrent \
    __wrap_System_Collections_Concurrent_System_Collections_HashHelpers__IsPrime
#define IsPrimeCoreLib __wrap_S_P_CoreLib_System_Collections_HashHelpers__IsPrime
#define IsPrimeImmutable \
    __wrap_System_Collections_Immutable_System_Collections_HashHelpers__IsPrime
#define CalcNumBuckets \
    __wrap_System_Collections_Immutable_System_Collections_Frozen_FrozenHashTable__CalcNumBuckets

#ifdef WITH_ZKVM_THROW
/* Strong definition resolving rhp's weak reference. */
static void *thrown_obj;
void ZkvmThrow(void *exceptionObj) { thrown_obj = exceptionObj; }
#endif

/* FileStatus as libSystem.Native lays it out (pal_io.h). The module keeps its
 * own copy of this; the test carries a second one on purpose, so a field
 * added on one side without the other is a failing test rather than a silent
 * write past the end of the caller's struct. */
typedef struct {
    int      Flags;
    int      Mode;
    unsigned Uid;
    unsigned Gid;
    long     Size;
    long     ATime, ATimeNsec;
    long     MTime, MTimeNsec;
    long     CTime, CTimeNsec;
    long     BirthTime, BirthTimeNsec;
    long     Dev;
    long     RDev;
    long     Ino;
    unsigned UserFlags;
} test_file_status;

#define TEST_S_IFCHR 0020000

/* Reference model: the managed HashHelpers.IsPrime, quirks included
 * (1 reports prime; even prime is only 2). */
static int ref_is_prime(int candidate)
{
    if (candidate % 2 == 1) {
        for (long d = 3; d * d <= candidate; d += 2)
            if (candidate % d == 0)
                return 0;
        return 1;
    }
    return candidate == 2;
}

int main(void)
{
    /* --- transition no-ops: must be callable with anything --- */
    __wrap_RhpPInvoke(NULL);
    __wrap_RhpPInvokeReturn((void *)0xdead);
    __wrap_RhpReversePInvoke(NULL);
    __wrap_RhpReversePInvokeReturn(NULL);
    __wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventPipeEventProvider__Register();
    __wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventSource__InitializeDefaultEventSources();
    __wrap_SystemNative_SetTerminalInvalidationHandler(NULL);
    t_pass++; /* reached without crashing */

    CHECK(
        __wrap_System_Console_Interop_Sys__InitializeTerminalAndSignalHandling()
        == 1);
    CHECK(__wrap_SystemNative_Write(1, "abc", 3) == 3); /* swallowed fully */
    CHECK(__wrap_SystemNative_Write(1, NULL, 0) == 0);

    /* --- the three answers Console.OpenStandardOutput() needs ---
     * UnixConsoleStream's constructor fstats the descriptor and refuses to
     * hand back a stream unless it is a character device; the stream then
     * asks whether it is a terminal, and Console dups the descriptor. Each
     * of those is an unserviceable OS call under noos, which is what killed
     * a guest on its first Console.Write. */
    {
        test_file_status st;
        for (long fd = 0; fd <= 2; fd++) {
            memset(&st, 0xEE, sizeof(st));
            CHECK(__wrap_SystemNative_FStat(fd, &st) == 0);
            CHECK(st.Mode == (TEST_S_IFCHR | 0666));
            /* the rest of the struct is written, not left as stack garbage:
             * .NET reads Flags and Size off this too */
            CHECK(st.Flags == 0 && st.Size == 0 && st.Ino == 0
                  && st.Dev == 0 && st.UserFlags == 0);
        }

        /* Any other descriptor fails: there is no file system here, so a
         * plausible-looking answer would be a lie the caller acts on. */
        for (long fd = 3; fd <= 5; fd++) {
            memset(&st, 0xEE, sizeof(st));
            CHECK(__wrap_SystemNative_FStat(fd, &st) == -1);
            CHECK(st.Mode == 0 && st.Size == 0); /* cleared even on failure */
        }
        memset(&st, 0xEE, sizeof(st));
        CHECK(__wrap_SystemNative_FStat(-1, &st) == -1);
    }

    /* Never a terminal: the termios and terminfo paths behind a "yes" are
     * far more OS than the guest has. */
    CHECK(__wrap_SystemNative_IsATty(0) == 0);
    CHECK(__wrap_SystemNative_IsATty(1) == 0);
    CHECK(__wrap_SystemNative_IsATty(2) == 0);
    CHECK(__wrap_SystemNative_IsATty(4242) == 0);

    /* Dup hands back what it was given: one process, three fixed
     * descriptors, no exec for a duplicate to survive. */
    CHECK(__wrap_SystemNative_Dup(0) == 0);
    CHECK(__wrap_SystemNative_Dup(1) == 1);
    CHECK(__wrap_SystemNative_Dup(2) == 2);
    CHECK(__wrap_SystemNative_Dup(99) == 99);

    /* --- bulk move: plain memmove semantics, overlaps included --- */
    {
        char buf[16] = "0123456789abcdef";
        char dst[16];
        __wrap_RhBulkMoveWithWriteBarrier(dst, buf, 16);
        CHECK(memcmp(dst, buf, 16) == 0);
        __wrap_RhBulkMoveWithWriteBarrier(buf + 2, buf, 8); /* fwd overlap */
        CHECK(memcmp(buf, "0101234567abcdef", 16) == 0);
        __wrap_RhBulkMoveWithWriteBarrier(dst, dst, 16); /* self-move */
        CHECK(memcmp(dst, "0123456789abcdef", 16) == 0);
        __wrap_RhBulkMoveWithWriteBarrier(dst, buf, 0); /* len 0 */
        CHECK(memcmp(dst, "0123456789abcdef", 16) == 0);
    }

    /* --- IsPrime: exact managed semantics on an exhaustive range --- */
    {
        int agree = 1;
        for (int c = 0; c <= 20000; c++)
            agree &= (IsPrimeConcurrent(c) == ref_is_prime(c));
        CHECK(agree);
        /* the two aliases route to the same implementation */
        int alias_agree = 1;
        for (int c = 0; c <= 2000; c++) {
            alias_agree &= (IsPrimeCoreLib(c) == IsPrimeConcurrent(c));
            alias_agree &= (IsPrimeImmutable(c) == IsPrimeConcurrent(c));
        }
        CHECK(alias_agree);
        /* documented quirks */
        CHECK(IsPrimeConcurrent(1) == 1);
        CHECK(IsPrimeConcurrent(2) == 1);
        CHECK(IsPrimeConcurrent(4) == 0);
        CHECK(IsPrimeConcurrent(9) == 0);
        CHECK(IsPrimeConcurrent(7199369) == 1); /* last table prime */
    }

    /* --- CalcNumBuckets: positive, odd, covers the entry count --- */
    {
        long lens[] = { 0, 1, 2, 3, 4, 7, 8, 100, 1000, 7199369, 7199370,
                        8000001 };
        int ok = 1;
        for (unsigned i = 0; i < sizeof(lens) / sizeof(lens[0]); i++) {
            int r = CalcNumBuckets(NULL, lens[i], 0);
            ok &= (r >= 3);
            ok &= (r >= lens[i]);
            ok &= (r % 2 == 1);
        }
        CHECK(ok);
        CHECK(CalcNumBuckets(NULL, 0, 0) == 3);   /* smallest table prime */
        CHECK(CalcNumBuckets(NULL, 4, 1) == 7);   /* next prime >= 4 */
        CHECK(CalcNumBuckets(NULL, 8000001, 0) == 8000001); /* beyond table:
                                                               len | 1 */
    }

    /* --- throw/fail-fast --- */
#ifdef WITH_ZKVM_THROW
    {
        int obj = 5;
        thrown_obj = NULL;
        __wrap_RhpThrowEx(&obj); /* handler takes over, control returns */
        CHECK(thrown_obj == &obj);
    }
#else
    EXPECT_EXIT(1, __wrap_RhpThrowEx((void *)0x1234)); /* no handler linked */
#endif
    EXPECT_EXIT(
        1, __wrap_S_P_CoreLib_System_RuntimeExceptionHelpers__FailFast());

    TEST_MAIN_END(
#ifdef WITH_ZKVM_THROW
        "rhp(+ZkvmThrow)"
#else
        "rhp"
#endif
    );
}
