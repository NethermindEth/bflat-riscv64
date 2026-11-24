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
 * memmove resolves to the memmove of the link (a package can give an
 * accelerated one, for example the DMA wrapper of libziskos). */
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
    /* An empty move may come with pointers that are not dereferenceable
     * (an empty span's null reference, the end of an array), which memmove
     * must not be given. Upstream's RhBulkMoveWithWriteBarrier returns
     * early the same way. */
    if (len != 0)
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
 * with combined_limit at 0 and alloc_ptr one pointer later, an array's length
 * right after its MethodTable pointer, and SZARRAY_BASE_SIZE three pointers. */
typedef struct {
    uint8_t *combined_limit;
    uint8_t *alloc_ptr;
} ee_alloc_context;

#define SZARRAY_BASE_SIZE (3 * sizeof(void *))

/* Every object starts 8-byte aligned, as the guests read array data through
 * 8-byte-aligned pointers. Base sizes are multiples of 8 on 64-bit targets
 * already; on 32-bit ones they are multiples of 4 and are rounded up here, as
 * the riscv32 runtime's own allocation helpers do. */
#if UINTPTR_MAX == 0xffffffffu
#define OBJECT_SIZE(base) (((uintptr_t)(base) + 7) & ~(uintptr_t)7)
#else
#define OBJECT_SIZE(base) ((uintptr_t)(base))
#endif

/* The longest array whose size the bump path computes in a uintptr_t. With
 * 64-bit sizes any valid length fits; with 32-bit ones the component size
 * (up to 0xffff) times the length must stay below 2^32, so longer arrays take
 * the runtime's checked path, as in its own AllocFast.S. */
#if UINTPTR_MAX == 0xffffffffu
#define MAX_BUMP_ARRAY_LENGTH 0x10000u
#else
#define MAX_BUMP_ARRAY_LENGTH 0x7fffffffu
#endif

static ee_alloc_context *alloc_context;

/*@ axiomatic AllocContext {
      // The thread's ee_alloc_context as RhpGetThread returns it: the guest
      // has one thread and one static TLS block, so one fixed address.
      logic ee_alloc_context *thread_alloc_context reads \nothing;
    }

    // The bytes left in a context's budget.
    logic integer budget{L}(ee_alloc_context *c) =
        c->combined_limit - c->alloc_ptr;

    // A context's budget [alloc_ptr, combined_limit) is writable memory
    // apart from the context itself: one block, so both pointers share a
    // base. A fresh context has an empty budget (both pointers null).
    predicate budget_ok{L}(ee_alloc_context *c) =
        \valid(c) &&
        \base_addr(c->alloc_ptr) == \base_addr(c->combined_limit) &&
        c->alloc_ptr <= c->combined_limit &&
        budget(c) <= PTRDIFF_MAX &&
        \valid(c->alloc_ptr + (0 .. budget(c) - 1)) &&
        \separated(c, c->alloc_ptr + (0 .. budget(c) - 1));

    // What the bump paths rely on: the thread's context keeps a valid
    // budget, and the cached pointer is unset or that context.
    predicate alloc_state{L} =
        budget_ok(thread_alloc_context) &&
        (alloc_context == \null || alloc_context == thread_alloc_context);

    // MethodTable fields the helpers read: m_usComponentSize at 0 and
    // m_uBaseSize at 4.
    logic integer component_size{L}(void *mt) = *(const uint16_t *)mt;
    logic integer base_size{L}(void *mt) =
        *(const uint32_t *)((const uint8_t *)mt + 4);

    // The array size the bump paths compute: rounded up to 8 bytes.
    logic integer array_size(integer component, integer length, integer base) =
        (component * length + base + 7) & (uintptr_t)~7;

    // Clearing the low three bits takes at most 7 away. No prover relates
    // a bit mask to magnitude, so this lemma is left out of the proof
    // (rhp/verify/wp.opts); it is plain machine arithmetic.
    lemma round_down_8:
      \forall integer v; 0 <= v <= UINTPTR_MAX ==> v - 7 <= (v & (uintptr_t)~7);
*/

/*@ terminates \true;
    exits \false;
    assigns \nothing;
    ensures \result == thread_alloc_context;
*/
extern ee_alloc_context *RhpGetThread(void);

/*@ // The runtime's helpers (AllocFast.S and its slow path into
    // uGCHeap::Alloc). Their footprint - context refills, the GC heap,
    // exceptions - is the runtime's and is left unspecified (no assigns
    // clause). They keep the thread's context valid, cannot reach this
    // file's cached pointer, and return the new object stamped with its
    // MethodTable unless they raise.
    requires budget_ok(thread_alloc_context);
    terminates \true;
    exits \true;
    ensures budget_ok(thread_alloc_context);
    ensures alloc_context == \old(alloc_context);
    ensures *(const void **)\result == pEEType;
*/
extern void *__real_RhpNewFast(const void *pEEType);

/*@ requires budget_ok(thread_alloc_context);
    terminates \true;
    exits \true;
    ensures budget_ok(thread_alloc_context);
    ensures alloc_context == \old(alloc_context);
    ensures *(const void **)\result == pEEType;
*/
extern void *__real_RhpNewArrayFast(const void *pEEType, uintptr_t numElements);

/*@ requires budget_ok(thread_alloc_context);
    terminates \true;
    exits \true;
    ensures budget_ok(thread_alloc_context);
    ensures alloc_context == \old(alloc_context);
    ensures *(const void **)\result == pEEType;
*/
extern void *__real_RhpNewPtrArrayFast(const void *pEEType,
                                       uintptr_t numElements);

/*@ assigns alloc_context;
    ensures alloc_context == thread_alloc_context;
*/
static __attribute__((noinline, cold)) void
bind_alloc_context(void)
{
    alloc_context = RhpGetThread();
}

/* Takes size bytes from the budget into *obj; 0 when they do not fit. */
/*@ requires \valid(ctx) && \valid(obj) && \separated(ctx, obj);
    requires \base_addr(ctx->alloc_ptr) == \base_addr(ctx->combined_limit);
    requires ctx->alloc_ptr <= ctx->combined_limit;
    requires ctx->combined_limit - ctx->alloc_ptr <= PTRDIFF_MAX;

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
      ensures ctx->alloc_ptr == \old(ctx->alloc_ptr);

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
    // the object comes back stamped with its MethodTable, which a base
    // size always has room for.
    requires \valid_read((const uint32_t *)((const uint8_t *)pEEType + 4));
    requires base_size(pEEType) >= sizeof(void *);
    requires alloc_state;
    terminates \true;
    exits \true;
    ensures alloc_state;
    ensures *(const void **)\result == pEEType;

    behavior bump:
      assumes alloc_context != \null;
      assumes OBJECT_SIZE(base_size(pEEType)) <= budget(alloc_context);
      ensures \result == \old(alloc_context->alloc_ptr);
      ensures \valid((uint8_t *)\result +
                     (0 .. OBJECT_SIZE(base_size(pEEType)) - 1));
      ensures alloc_context->alloc_ptr ==
          \old(alloc_context->alloc_ptr) + OBJECT_SIZE(base_size(pEEType));
*/
void *
__wrap_RhpNewFast(const void *pEEType)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if (__builtin_expect(ctx == NULL, 0))
        bind_alloc_context();
    else if (bump_alloc(ctx, OBJECT_SIZE(*(const uint32_t *)((const uint8_t *)pEEType + 4)), &obj)) {
        *(const void **)obj = pEEType;
        return obj;
    }
    return __real_RhpNewFast(pEEType);
}

/*@ // Same split as __wrap_RhpNewFast.
    requires \valid_read((const uint16_t *)pEEType);
    requires alloc_state;
    terminates \true;
    exits \true;
    ensures alloc_state;
    ensures *(const void **)\result == pEEType;

    behavior bump:
      assumes alloc_context != \null;
      assumes numElements <= MAX_BUMP_ARRAY_LENGTH;
      assumes array_size(component_size(pEEType), numElements,
                         SZARRAY_BASE_SIZE) <= budget(alloc_context);
      ensures \result == \old(alloc_context->alloc_ptr);
      ensures \valid((uint8_t *)\result +
                     (0 .. array_size(component_size(pEEType), numElements,
                                      SZARRAY_BASE_SIZE) - 1));
      ensures ((uintptr_t *)\result)[1] == numElements;
      ensures alloc_context->alloc_ptr == \old(alloc_context->alloc_ptr) +
          array_size(component_size(pEEType), numElements, SZARRAY_BASE_SIZE);
*/
void *
__wrap_RhpNewArrayFast(const void *pEEType, uintptr_t numElements)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if (__builtin_expect(ctx == NULL, 0))
        bind_alloc_context();
    else if (numElements <= MAX_BUMP_ARRAY_LENGTH &&
             bump_alloc(ctx, (*(const uint16_t *)pEEType * numElements + SZARRAY_BASE_SIZE + 7) & ~(uintptr_t)7, &obj)) {
        ((const void **)obj)[0] = pEEType;
        ((uintptr_t *)obj)[1] = numElements;
        return obj;
    }
    return __real_RhpNewArrayFast(pEEType, numElements);
}

/*@ // Same split as __wrap_RhpNewFast.
    requires alloc_state;
    terminates \true;
    exits \true;
    ensures alloc_state;
    ensures *(const void **)\result == pEEType;

    behavior bump:
      assumes alloc_context != \null;
      assumes numElements < 0x8000000;
      assumes OBJECT_SIZE(numElements * sizeof(void *) + SZARRAY_BASE_SIZE) <=
          budget(alloc_context);
      ensures \result == \old(alloc_context->alloc_ptr);
      ensures \valid((uint8_t *)\result +
                     (0 .. OBJECT_SIZE(numElements * sizeof(void *) +
                                       SZARRAY_BASE_SIZE) - 1));
      ensures ((uintptr_t *)\result)[1] == numElements;
      ensures alloc_context->alloc_ptr == \old(alloc_context->alloc_ptr) +
          OBJECT_SIZE(numElements * sizeof(void *) + SZARRAY_BASE_SIZE);
*/
void *
__wrap_RhpNewPtrArrayFast(const void *pEEType, uintptr_t numElements)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if (__builtin_expect(ctx == NULL, 0))
        bind_alloc_context();
    /* AllocFast.S's bound, below which the size cannot overflow. */
    else if (numElements < 0x8000000 &&
             bump_alloc(ctx, OBJECT_SIZE(numElements * sizeof(void *) + SZARRAY_BASE_SIZE), &obj)) {
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

/*@ // The original QCalls, as for __real_RhpNewFast: unspecified
    // footprint, the thread's context kept valid, the result stamped.
    requires \valid(pResult);
    requires budget_ok(thread_alloc_context);
    terminates \true;
    exits \true;
    ensures budget_ok(thread_alloc_context);
    ensures alloc_context == \old(alloc_context);
    ensures *(const void **)*pResult == pEEType;
*/
extern void __real_RhAllocateNewObject(const void *pEEType, uint32_t flags,
                                       void **pResult);

/*@ requires \valid(pResult);
    requires budget_ok(thread_alloc_context);
    terminates \true;
    exits \true;
    ensures budget_ok(thread_alloc_context);
    ensures alloc_context == \old(alloc_context);
    ensures *(const void **)*pResult == pEEType;
*/
extern void __real_RhAllocateNewArray(const void *pEEType, uint32_t numElements,
                                      uint32_t flags, void **pResult);

/*@ // Off the bump path the original QCall runs, as for __wrap_RhpNewFast.
    // The result slot is the caller's, never inside the free budget.
    requires \valid_read((const uint32_t *)pEEType + (0 .. 1));
    requires \valid_read((const uint32_t *)((const uint8_t *)pEEType + 4));
    requires base_size(pEEType) >= sizeof(void *);
    requires \valid(pResult);
    requires alloc_state;
    requires \separated(pResult, thread_alloc_context);
    requires \separated(pResult, thread_alloc_context->alloc_ptr +
                                  (0 .. budget(thread_alloc_context) - 1));
    terminates \true;
    exits \true;
    ensures alloc_state;
    ensures *(const void **)*pResult == pEEType;

    behavior bump:
      assumes (flags & ~(uint32_t)GC_ALLOC_PINNED_OBJECT_HEAP) == 0;
      assumes (*(const uint32_t *)pEEType & MT_HAS_FINALIZER) == 0;
      assumes OBJECT_SIZE(base_size(pEEType)) <=
          budget(thread_alloc_context);
      ensures alloc_context == thread_alloc_context;
      ensures *pResult == \old(thread_alloc_context->alloc_ptr);
      ensures \valid((uint8_t *)*pResult +
                     (0 .. OBJECT_SIZE(base_size(pEEType)) - 1));
      ensures thread_alloc_context->alloc_ptr ==
          \old(thread_alloc_context->alloc_ptr) +
          OBJECT_SIZE(base_size(pEEType));
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
        if (bump_alloc(ctx, OBJECT_SIZE(*(const uint32_t *)((const uint8_t *)pEEType + 4)), &obj)) {
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

/*@ // Same split as __wrap_RhAllocateNewObject. An array's base size
    // covers its MethodTable pointer and length.
    requires \valid_read((const uint32_t *)pEEType + (0 .. 1));
    requires \valid_read((const uint16_t *)pEEType);
    requires \valid_read((const uint32_t *)((const uint8_t *)pEEType + 4));
    requires base_size(pEEType) >= 2 * sizeof(void *);
    requires \valid(pResult);
    requires alloc_state;
    requires \separated(pResult, thread_alloc_context);
    requires \separated(pResult, thread_alloc_context->alloc_ptr +
                                  (0 .. budget(thread_alloc_context) - 1));
    terminates \true;
    exits \true;
    ensures alloc_state;
    ensures *(const void **)*pResult == pEEType;

    behavior bump:
      assumes (flags & ~(uint32_t)(GC_ALLOC_ZEROING_OPTIONAL |
                                   GC_ALLOC_PINNED_OBJECT_HEAP)) == 0;
      assumes numElements <= 0x7FFFFFC7 && numElements <= MAX_BUMP_ARRAY_LENGTH;
      assumes array_size(component_size(pEEType), numElements,
                         base_size(pEEType)) <= budget(thread_alloc_context);
      ensures alloc_context == thread_alloc_context;
      ensures *pResult == \old(thread_alloc_context->alloc_ptr);
      ensures \valid((uint8_t *)*pResult +
                     (0 .. array_size(component_size(pEEType), numElements,
                                      base_size(pEEType)) - 1));
      ensures ((uintptr_t *)*pResult)[1] == numElements;
      ensures thread_alloc_context->alloc_ptr ==
          \old(thread_alloc_context->alloc_ptr) +
          array_size(component_size(pEEType), numElements,
                     base_size(pEEType));
*/
void
__wrap_RhAllocateNewArray(const void *pEEType, uint32_t numElements,
                          uint32_t flags, void **pResult)
{
    ee_alloc_context *ctx = alloc_context;
    uint8_t *obj;
    if ((flags & ~(uint32_t)(GC_ALLOC_ZEROING_OPTIONAL | GC_ALLOC_PINNED_OBJECT_HEAP)) == 0 &&
        numElements <= 0x7FFFFFC7 && numElements <= MAX_BUMP_ARRAY_LENGTH) {
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

/* pal's guest console, which hands the bytes to the platform (a platform
 * without a host-visible console swallows them; see pal/module.c
 * zkvm_console_write). Weak on purpose: rhp.o is linked (and unit tested)
 * without pal. */
/*@ // pal/module.c's contract.
    requires len >= 0;
    requires \valid_read(buf + (0 .. len - 1));
    terminates \true;
    exits \false;
    assigns \nothing;
    ensures \result == len;
*/
extern int zkvm_console_write(int fd, const char *buf, int len) __attribute__((weak));

/*@ // The buffer is handed to the guest console if the target has one, and
    // swallowed otherwise; either way the caller is told the full buffer
    // was consumed so it does not retry.
    requires bufferSize <= 0 ||
        \valid_read((const char *)buffer + (0 .. bufferSize - 1));
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
    assigns ((char *)output)[0 .. sizeof(rhp_file_status) - 1],
            ((rhp_file_status *)output)->Mode;
    ensures \result == 0 || \result == -1;
    ensures \result == 0 <==> (fd == 0 || fd == 1 || fd == 2);
    ensures \result == 0 ==>
        ((rhp_file_status *)output)->Mode == (RHP_S_IFCHR | 0666);
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

    /*@ loop invariant 0 <= i <= sizeof(rhp_file_status);
        loop invariant \forall integer j; 0 <= j < i ==> ((char *)st)[j] == 0;
        loop assigns i, ((char *)st)[0 .. sizeof(rhp_file_status) - 1];
        loop variant sizeof(rhp_file_status) - i;
    */
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
/*@ // The program's managed handler: its effects are the program's (no
    // assigns clause), and it may end the guest or never return.
    terminates \false;
    exits \true;
*/
extern void ZkvmThrow(void *exceptionObj) __attribute__((weak));

/*@ // Two configurations exist. Without a linked ZkvmThrow handler (weak
    // symbol resolves to null) the function never returns and terminates the
    // guest with exit status 1. With a handler, control transfers to managed
    // code whose effects cannot be specified here; the function returns
    // normally only in that configuration. Frama-C's libc models exit() as
    // not guaranteed to terminate (atexit handlers), and the handler gives
    // no such guarantee either, hence no termination claim.
    terminates \false;
    exits &ZkvmThrow == \null ==> \exit_status == 1;
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
/*@ // exit() never returns; as above, Frama-C's libc gives it no
    // termination guarantee, hence none is claimed here.
    terminates \false;
    assigns \nothing;
    ensures \false;
    exits \exit_status == 1;
*/
void __wrap_S_P_CoreLib_System_RuntimeExceptionHelpers__FailFast(void)
{
    exit(1);
}

