/**
 * @file
 * @brief Redhawk Platform (re)-implementation - for neglecting some functions
 *        that don't work well under zkVMs.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

/* RhpPInvoke / RhpPInvokeReturn build and tear down a PInvokeTransitionFrame
 * so the GC can scan/suspend a thread that has entered native code. The
 * zkVM guest is single-threaded, uGC never collects (so threads are never
 * suspended and the frame is never scanned), and RhpThrowEx fails fast
 * instead of unwinding (so the frame is never walked for EH). The whole
 * transition is therefore dead weight. */
/*@ assigns \nothing; */
void
__wrap_RhpPInvoke(void *pFrame)
{
    (void)pFrame;
}

/*@ assigns \nothing; */
void
__wrap_RhpPInvokeReturn(void *pFrame)
{
    (void)pFrame;
}

/* Bulk reference copy. uGC has no write barrier, so this is just a move.
 * memmove resolves to the libziskos DMA-accelerated wrapper. */
/*@ requires len == 0 ||
        (\valid((char *)dest + (0 .. len - 1)) &&
         \valid_read((char *)src + (0 .. len - 1)));
    assigns ((char *)dest)[0 .. len - 1];
    ensures \forall integer i; 0 <= i < len ==>
        ((char *)dest)[i] == \old(((char *)src)[i]);
*/
void
__wrap_RhBulkMoveWithWriteBarrier(void *dest, void *src, size_t len)
{
    memmove(dest, src, len);
}

/* Allocation helpers (RhpNewFast, RhpNewObject, RhpNewPtrArrayFast,
 * RhpNewArrayFast, RhNewString) are no longer wrapped: upstream .NET 10
 * ships riscv64 AllocFast.S whose inline bump on the thread's
 * ee_alloc_context works once uGC hands out an allocation budget
 * (uGCHeap::Alloc refill quantum), and the native slow path
 * (GCHelpers.cpp: GcAllocInternal) already performs the Array.MaxLength
 * and overflow checks the old wraps reimplemented - with the MethodTable
 * layout owned by the runtime instead of hand-copied offsets here. */

/* No CheckCastAny cache-bypass anymore: the cast cache runs on Interlocked
 * ops and statics, both functional now. Likewise UInt32ToDecStr's
 * small-number string cache (lazy statics), Thread::IsDetached (trivial
 * field read), WaitForForegroundThreads (returns immediately with zero
 * foreground threads), the cgroup initializers (their /proc,/sys parses
 * no-op against pal's stubbed open()), Environment's NonGC static base
 * (its cctor lost the cgroup double math to the ProcessorCount=1
 * substitution) and GetDefaultLocaleName (unreachable under the invariant
 * globalization forced by pal's getenv) all run their original code. */

/*@ assigns \nothing; */
void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventPipeEventProvider__Register()
{
}

/*@ assigns \nothing; */
void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventSource__InitializeDefaultEventSources()
{
}

/* The thread-statics emulation (ThreadStaticStorageLite, the keyed slot
 * store, __wrap_RhGetThreadStaticStorage and
 * __wrap_..ThreadStatics__GetUninlinedThreadStaticBaseForType) is gone: the
 * original path works now that allocation does. Native
 * RhGetThreadStaticStorage just returns &pThread->m_pThreadLocalStatics
 * (thread.cpp) via the same TLS mechanism AllocFast.S uses, and the managed
 * GetUninlinedThreadStaticBaseForType (ThreadStatics.cs) only builds jagged
 * object[][] storage with RhNewObject - no locks, no syscalls. The old wrap
 * predated working managed allocation under zkVM. */

/* The Lock family (Enter, EnterAndGetCurrentThreadId, TryEnterSlow_0,
 * Exit_0/Exit_1/ExitAll, get_IsHeldByCurrentThread), the TypeLoader lock
 * assertion bypass and the C-side DeadlockAwareAcquire cctor tracking are
 * gone: with real thread statics restored, System.Threading.Lock works as
 * designed on the single-threaded guest. The uncontended CAS fast path
 * always succeeds, the blocking slow path is unreachable (no second thread
 * can ever hold a lock), and ClassConstructorRunner's own
 * DeadlockAwareAcquire breaks recursive cctor cycles through the
 * now-truthful Lock.IsHeldByCurrentThread - the exact mechanism our list of
 * active cctor contexts used to emulate. */

/*@ assigns \nothing;
    ensures \result == 1;
*/
int __wrap_System_Console_Interop_Sys__InitializeTerminalAndSignalHandling(void)
{
    return 1;
}

/*@ assigns \nothing; */
void __wrap_SystemNative_SetTerminalInvalidationHandler(void *param)
{
}

/*@ // The write is swallowed: the guest console is a no-op device, but the
    // caller is told the full buffer was consumed so it does not retry.
    assigns \nothing;
    ensures \result == bufferSize;
*/
int __wrap_SystemNative_Write(int fd, const void* buffer, int bufferSize)
{
    return bufferSize;
}

/* Reverse P/Invoke transition. The real CoreLib RhpReversePInvoke attaches the
 * thread and parks it at a GC-safe point (AttachOrTrapThread2). That only makes
 * sense for a native->managed boundary entered in preemptive mode. When a
 * managed exception handler (an [UnmanagedCallersOnly] method like ZkvmThrow)
 * is entered from __wrap_RhpThrowEx, the thread is ALREADY cooperative, so the
 * real transition spins on a GC rendezvous that never comes in the
 * single-threaded, never-collecting zkVM. No-op it (matches zerolib). */
/*@ assigns \nothing; */
void
__wrap_RhpReversePInvoke(void *pFrame)
{
    (void)pFrame;
}

/*@ assigns \nothing; */
void
__wrap_RhpReversePInvokeReturn(void *pFrame)
{
    (void)pFrame;
}

/* RhpThrowEx receives the managed exception object in a0 (first arg register).
 * Instead of a blind fail-fast, hand that object to a managed handler that the
 * user program may export as [UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")].
 * The reference is weak: programs that don't define ZkvmThrow link fine and
 * fall back to exit(1), so existing binaries keep their old behaviour. A
 * program that does define it takes full control of the throw — the wrapper
 * does not exit, so the handler decides what happens next. */
extern void ZkvmThrow(void *exceptionObj) __attribute__((weak));

/*@ // Two configurations exist. Without a linked ZkvmThrow handler (weak
    // symbol resolves to null) the function never returns and terminates the
    // guest with exit status 1. With a handler, control transfers to managed
    // code whose effects cannot be specified here; the function returns
    // normally only in that configuration. The weak-symbol test is a link-time
    // property, not expressible as an ACSL assumes clause, so only the exit
    // status of the fallback path is stated formally.
    exits \exit_status == 1;
*/
void __wrap_RhpThrowEx(void *exceptionObj)
{
    if (ZkvmThrow != NULL)
    {
        ZkvmThrow(exceptionObj);
        return;
    }
    exit(1);
}

/* FailFast carries a message string (or null), not an exception object, so it
 * keeps the plain fail-fast path rather than routing through ZkvmThrow. */
/*@ assigns \nothing;
    ensures \false;
    exits \exit_status == 1;
*/
void __wrap_S_P_CoreLib_System_RuntimeExceptionHelpers__FailFast(void)
{
    exit(1);
}

/* HashHelpers.IsPrime computes (int)Math.Sqrt(candidate) for the loop bound,
 * which is the only reason dictionary resizing drags F/D instructions into
 * the rv64ima image. This is an exact reimplementation with an integer bound
 * (divisor^2 <= candidate iterates identically): CoreLib only ever calls it
 * with positive candidates from GetPrime. One definition covers both the
 * System.Collections.Concurrent and System.Collections.Immutable copies -
 * their identical bodies are folded into a single symbol by the compiler's
 * method body folding. */
/*@ // Mirrors the managed HashHelpers.IsPrime exactly, including its quirks:
    // odd candidates are "prime" iff no odd divisor d with d*d <= candidate
    // divides them (so 1 and 9-free odd composites below 9 report prime, as
    // upstream does), and the only even prime is 2. GetPrime never passes
    // negative values.
    requires candidate >= 0;
    assigns \nothing;
    ensures \result == 0 || \result == 1;

    behavior odd:
      assumes candidate % 2 == 1;
      ensures \result == 1 <==>
          (\forall integer d;
             3 <= d && d % 2 == 1 && d * d <= candidate ==>
                 candidate % d != 0);

    behavior even:
      assumes candidate % 2 == 0;
      ensures \result == 1 <==> candidate == 2;

    complete behaviors;
    disjoint behaviors;
*/
int
__wrap_System_Collections_Concurrent_System_Collections_HashHelpers__IsPrime(int candidate)
{
    if ((candidate & 1) != 0)
    {
        for (long divisor = 3; divisor * divisor <= candidate; divisor += 2)
        {
            if ((candidate % divisor) == 0)
                return 0;
        }
        return 1;
    }
    return candidate == 2;
}

/* Each assembly embedding the shared HashHelpers source gets its own copy of
 * IsPrime; with the ILC substitution turning the managed bodies into throw
 * stubs, every copy's callers must be diverted to the C implementation. */
/*@ // Exact alias of the Concurrent copy above; same contract.
    requires candidate >= 0;
    assigns \nothing;
    ensures \result == 0 || \result == 1;

    behavior odd:
      assumes candidate % 2 == 1;
      ensures \result == 1 <==>
          (\forall integer d;
             3 <= d && d % 2 == 1 && d * d <= candidate ==>
                 candidate % d != 0);

    behavior even:
      assumes candidate % 2 == 0;
      ensures \result == 1 <==> candidate == 2;

    complete behaviors;
    disjoint behaviors;
*/
int
__wrap_S_P_CoreLib_System_Collections_HashHelpers__IsPrime(int candidate)
{
    return __wrap_System_Collections_Concurrent_System_Collections_HashHelpers__IsPrime(candidate);
}

/*@ // Exact alias of the Concurrent copy above; same contract.
    requires candidate >= 0;
    assigns \nothing;
    ensures \result == 0 || \result == 1;

    behavior odd:
      assumes candidate % 2 == 1;
      ensures \result == 1 <==>
          (\forall integer d;
             3 <= d && d % 2 == 1 && d * d <= candidate ==>
                 candidate % d != 0);

    behavior even:
      assumes candidate % 2 == 0;
      ensures \result == 1 <==> candidate == 2;

    complete behaviors;
    disjoint behaviors;
*/
int
__wrap_System_Collections_Immutable_System_Collections_HashHelpers__IsPrime(int candidate)
{
    return __wrap_System_Collections_Concurrent_System_Collections_HashHelpers__IsPrime(candidate);
}

/* FrozenHashTable.CalcNumBuckets searches candidate bucket counts and rates
 * them by collision percentage - double math at collection-freeze time. Any
 * positive bucket count is CORRECT (collisions go to chains); only lookup
 * locality differs. The replacement picks the smallest prime >= the entry
 * count from HashHelpers' primes table, which is the classic Dictionary
 * sizing policy. Managed signature: CalcNumBuckets(ReadOnlySpan<int>, bool)
 * -> a0 = data ref, a1 = length, a2 = hashCodesAreUnique (ignored). */
static const int rhp_primes[] = {
    3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293,
    353, 431, 521, 631, 761, 919, 1103, 1327, 1597, 1931, 2333, 2801, 3371,
    4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591, 17519, 21023, 25229,
    30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
    187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403,
    968897, 1162687, 1395263, 1674319, 2009191, 2411033, 2893249, 3471899,
    4166287, 4999559, 5999471, 7199369
};

/*@ // Any positive bucket count is functionally correct (collisions chain);
    // the guarantees that matter to the caller are: the count covers the
    // entry count, is positive, and is odd (never a power of two, so hash
    // distribution is preserved).
    requires 0 <= hashCodesLength <= 0x7FFFFFFF;
    assigns \nothing;
    ensures \result >= hashCodesLength;
    ensures \result >= 3;
    ensures \result % 2 == 1;
*/
int
__wrap_System_Collections_Immutable_System_Collections_Frozen_FrozenHashTable__CalcNumBuckets(
    void *hashCodesRef, long hashCodesLength, int hashCodesAreUnique)
{
    (void)hashCodesRef;
    (void)hashCodesAreUnique;
    for (unsigned i = 0; i < sizeof(rhp_primes) / sizeof(rhp_primes[0]); i++)
    {
        if (rhp_primes[i] >= hashCodesLength)
            return rhp_primes[i];
    }
    /* Beyond the table (7.2M+ entries): any odd count works. */
    return (int)(hashCodesLength | 1);
}

/* LengthBuckets.CreateLengthBucketsArrayIfAppropriate keeps its managed body
 * (5 cold F/D instructions): its int[] return cannot be expressed as an ILC
 * stub value, and body="remove" would mark it no-return, poisoning callers
 * with trap-after-call codegen (learned the hard way). */
