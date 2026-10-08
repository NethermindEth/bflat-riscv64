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
#include <stdint.h>
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

/* Allocation fast paths without the TLS lookup. Upstream riscv64
 * AllocFast.S finds the thread's ee_alloc_context through __tls_get_addr on
 * every allocation, which also costs each helper a stack frame. The guest
 * has exactly one thread and the tls module keeps one static TLS block, so
 * the context's address never changes: it is looked up once, through the
 * same TLS variable the helpers use (RhpGetThread), and each wrapper then
 * performs the helper's bump with it. Everything off the bump path - no
 * context yet, a length the helper rejects, an exhausted budget - is handed
 * to the original helper with its arguments untouched, so refills (uGCHeap::
 * Alloc), the Array.MaxLength and overflow checks (GcAllocInternal) and the
 * exceptions stay the runtime's.
 *
 * The offsets are AllocFast.S's (AsmOffsets): MethodTable m_usComponentSize
 * at 0 and m_uBaseSize at 4, the ee_alloc_context at the start of the thread
 * with combined_limit at 0 and alloc_ptr at 8, an array's length right after
 * its MethodTable pointer, and SZARRAY_BASE_SIZE 0x18. */
typedef struct {
    uint8_t *combined_limit;
    uint8_t *alloc_ptr;
} ee_alloc_context;

extern ee_alloc_context *RhpGetThread(void);
extern void *__real_RhpNewFast(const void *pEEType);
extern void *__real_RhpNewArrayFast(const void *pEEType, uintptr_t numElements);
extern void *__real_RhpNewPtrArrayFast(const void *pEEType,
                                       uintptr_t numElements);

static ee_alloc_context *alloc_context;

/*@ assigns alloc_context; */
static __attribute__((noinline, cold)) void
bind_alloc_context(void)
{
    alloc_context = RhpGetThread();
}

/* Takes size bytes from the budget into *obj; 0 when they do not fit. */
/*@ requires \valid(ctx) && \valid(obj);
    requires ctx->alloc_ptr <= ctx->combined_limit;

    behavior fits:
      assumes size <= ctx->combined_limit - ctx->alloc_ptr;
      assigns ctx->alloc_ptr, *obj;
      ensures \result == 1;
      ensures *obj == \old(ctx->alloc_ptr);
      ensures ctx->alloc_ptr == \old(ctx->alloc_ptr) + size;

    behavior exhausted:
      assumes size > ctx->combined_limit - ctx->alloc_ptr;
      assigns \nothing;
      ensures \result == 0;

    complete behaviors;
    disjoint behaviors;
*/
static inline int
bump_alloc(ee_alloc_context *ctx, uintptr_t size, uint8_t **obj)
{
    uint8_t *ptr = ctx->alloc_ptr;
    if (size > (uintptr_t)(ctx->combined_limit - ptr))
        return 0;
    ctx->alloc_ptr = ptr + size;
    *obj = ptr;
    return 1;
}

/*@ // Off the bump path the original helper runs; its effects (refills,
    // exceptions) are the runtime's and are not specified here. Either way
    // the object comes back stamped with its MethodTable.
    requires \valid_read((const uint32_t *)((const uint8_t *)pEEType + 4));
    ensures *(const void **)\result == pEEType;
*/
void *
__wrap_RhpNewFast(const void *pEEType)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if (__builtin_expect(ctx == NULL, 0))
        bind_alloc_context();
    else if (bump_alloc(ctx, *(const uint32_t *)((const uint8_t *)pEEType + 4), &obj)) {
        *(const void **)obj = pEEType;
        return obj;
    }
    return __real_RhpNewFast(pEEType);
}

/*@ // Same split as __wrap_RhpNewFast.
    requires \valid_read((const uint16_t *)pEEType);
    ensures *(const void **)\result == pEEType;
*/
void *
__wrap_RhpNewArrayFast(const void *pEEType, uintptr_t numElements)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if (__builtin_expect(ctx == NULL, 0))
        bind_alloc_context();
    else if (numElements <= 0x7fffffff &&
             bump_alloc(ctx, (*(const uint16_t *)pEEType * numElements + 0x18 + 7) & ~(uintptr_t)7, &obj)) {
        ((const void **)obj)[0] = pEEType;
        ((uintptr_t *)obj)[1] = numElements;
        return obj;
    }
    return __real_RhpNewArrayFast(pEEType, numElements);
}

/*@ // Same split as __wrap_RhpNewFast.
    ensures *(const void **)\result == pEEType;
*/
void *
__wrap_RhpNewPtrArrayFast(const void *pEEType, uintptr_t numElements)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if (__builtin_expect(ctx == NULL, 0))
        bind_alloc_context();
    /* AllocFast.S's bound, below which the size cannot overflow. */
    else if (numElements < 0x8000000 && bump_alloc(ctx, numElements * 8 + 0x18, &obj)) {
        ((const void **)obj)[0] = pEEType;
        ((uintptr_t *)obj)[1] = numElements;
        return obj;
    }
    return __real_RhpNewPtrArrayFast(pEEType, numElements);
}

/* The QCalls behind GC static bases (StartupCodeHelpers.InitializeStatics,
 * GC_ALLOC_PINNED_OBJECT_HEAP) and GC.AllocateUninitializedArray
 * (GC_ALLOC_ZEROING_OPTIONAL) reach the GC through the full slow path - mode
 * switch, TLS lookup, RhpGcAlloc, uGCHeap::Alloc - for what is a bump under
 * uGC: it never moves or collects, so a pinned object needs no separate
 * heap, and the context's memory is zeroed already. Other flags, a
 * finalizer or a length past Array.MaxLength take the original path
 * untouched; a request the budget cannot take goes to it without those two
 * flags, so uGC serves it from the context and refills it. */
#define GC_ALLOC_ZEROING_OPTIONAL 16
#define GC_ALLOC_PINNED_OBJECT_HEAP 64
#define MT_HAS_FINALIZER 0x00100000

extern void __real_RhAllocateNewObject(const void *pEEType, uint32_t flags,
                                       void **pResult);
extern void __real_RhAllocateNewArray(const void *pEEType, uint32_t numElements,
                                      uint32_t flags, void **pResult);

/*@ // Off the bump path the original QCall runs, as for __wrap_RhpNewFast.
    requires \valid_read((const uint32_t *)pEEType + (0 .. 1));
    requires \valid(pResult);
    ensures *(const void **)*pResult == pEEType;
*/
void
__wrap_RhAllocateNewObject(const void *pEEType, uint32_t flags, void **pResult)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if ((flags & ~(uint32_t)GC_ALLOC_PINNED_OBJECT_HEAP) == 0 &&
        (*(const uint32_t *)pEEType & MT_HAS_FINALIZER) == 0) {
        if (__builtin_expect(ctx == NULL, 0)) {
            /* InitializeStatics runs before any fast-path allocation. */
            bind_alloc_context();
            ctx = alloc_context;
        }
        if (bump_alloc(ctx, *(const uint32_t *)((const uint8_t *)pEEType + 4), &obj)) {
            *(const void **)obj = pEEType;
            *pResult = obj;
            return;
        }
        /* A pinned request would bypass the context and leave the next
         * one no budget. */
        flags = 0;
    }
    __real_RhAllocateNewObject(pEEType, flags, pResult);
}

/*@ // Same split as __wrap_RhAllocateNewObject.
    requires \valid_read((const uint32_t *)pEEType + (0 .. 1));
    requires \valid(pResult);
    ensures *(const void **)*pResult == pEEType;
*/
void
__wrap_RhAllocateNewArray(const void *pEEType, uint32_t numElements,
                          uint32_t flags, void **pResult)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if ((flags & ~(uint32_t)(GC_ALLOC_ZEROING_OPTIONAL | GC_ALLOC_PINNED_OBJECT_HEAP)) == 0 &&
        numElements <= 0x7FFFFFC7) {
        if (__builtin_expect(ctx == NULL, 0)) {
            bind_alloc_context();
            ctx = alloc_context;
        }
        if (bump_alloc(ctx, (*(const uint16_t *)pEEType * (uintptr_t)numElements +
                             *(const uint32_t *)((const uint8_t *)pEEType + 4) + 7) & ~(uintptr_t)7, &obj)) {
            ((const void **)obj)[0] = pEEType;
            ((uintptr_t *)obj)[1] = numElements;
            *pResult = obj;
            return;
        }
        flags = 0;
    }
    __real_RhAllocateNewArray(pEEType, numElements, flags, pResult);
}

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

/* pal's guest console. Weak on purpose: rhp.o is linked (and unit tested)
 * without pal, and ZisK - whose console is a no-op device - has no use for
 * it either. Non-NULL only on the targets whose emulator surfaces guest
 * output on the host (SP1, OpenVM); see pal/module.c zkvm_console_write. */
extern int zkvm_console_write(int fd, const char *buf, int len) __attribute__((weak));

/*@ // The buffer is handed to the guest console if the target has one, and
    // swallowed otherwise; either way the caller is told the full buffer
    // was consumed so it does not retry.
    assigns \nothing;
    ensures \result == bufferSize;
*/
int __wrap_SystemNative_Write(int fd, const void* buffer, int bufferSize)
{
    if (zkvm_console_write && bufferSize > 0)
        zkvm_console_write(fd, buffer, bufferSize);
    return bufferSize;
}

/* FileStatus as libSystem.Native defines it (src/native/libs/System.Native/
 * pal_io.h). Only Flags and Mode are read on the path this wrapper exists for,
 * but the whole struct is written so no field is left holding stack garbage. */
typedef struct
{
    int32_t  Flags;
    int32_t  Mode;
    uint32_t Uid;
    uint32_t Gid;
    int64_t  Size;
    int64_t  ATime, ATimeNsec;
    int64_t  MTime, MTimeNsec;
    int64_t  CTime, CTimeNsec;
    int64_t  BirthTime, BirthTimeNsec;
    int64_t  Dev;
    int64_t  RDev;
    int64_t  Ino;
    uint32_t UserFlags;
} rhp_file_status;

#define RHP_S_IFCHR 0020000 /* character device, per <sys/stat.h> */

/*@ // Answers for the three standard descriptors and fails for everything
    // else. The guest has no file system, so any other descriptor is a bug
    // in the caller rather than a question worth answering.
    requires \valid(((char *)output) + (0 .. sizeof(rhp_file_status) - 1));
    assigns ((char *)output)[0 .. sizeof(rhp_file_status) - 1];
    ensures \result == 0 || \result == -1;
*/
int32_t __wrap_SystemNative_FStat(intptr_t fd, void *output)
{
    rhp_file_status *st = (rhp_file_status *)output;

    /* Console.OpenStandardOutput() asks what kind of thing stdout is before it
     * will hand back a stream: UnixConsoleStream's constructor calls FStat and
     * compares the mode against S_IFCHR. Left to noos, the libc fstat under
     * this is an unserviceable OS call and terminates the guest with 253 - so
     * the first Console.Write in a guest killed it, on every target that links
     * noos. A character device is also the honest answer: the zkVM console is
     * a stream of bytes with no size, no seek and no inode.
     *
     * Deliberately NOT delegating to the real SystemNative_FStat for other
     * descriptors: there is nothing underneath it here. */
    if (st == 0)
        return -1;

    for (unsigned i = 0; i < sizeof(rhp_file_status); i++)
        ((char *)st)[i] = 0;

    if (fd != 0 && fd != 1 && fd != 2)
        return -1;

    st->Mode = RHP_S_IFCHR | 0666;
    return 0;
}

/*@ // The zkVM console is never a terminal: saying otherwise sends .NET down
    // the termios and terminfo paths, which are far more OS than this guest
    // has. Answering "redirected" keeps it on the plain-stream path.
    assigns \nothing;
    ensures \result == 0;
*/
int32_t __wrap_SystemNative_IsATty(intptr_t fd)
{
    (void)fd;
    return 0;
}

/*@ // Hands back the descriptor it was given.
    assigns \nothing;
    ensures \result == oldfd;
*/
intptr_t __wrap_SystemNative_Dup(intptr_t oldfd)
{
    /* The real one is fcntl(oldfd, F_DUPFD_CLOEXEC, 0), and fcntl is an
     * unserviceable OS call here - which is what killed the guest on the first
     * Console.OpenStandardOutput(). There is nothing a duplicate would buy in a
     * single-process guest with three fixed descriptors and no exec to survive,
     * so return the same one. .NET itself takes exactly this fallback where
     * F_DUPFD is unavailable (pal_io.c SystemNative_Dup, the WASI branch). */
    return oldfd;
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
