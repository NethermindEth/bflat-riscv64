/**
 * @file
 * @brief Redhawk Platform (re)-implementation - for neglecting some functions
 *        that don't work well under zkVMs.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define POISONED_POINTER ((void *)0xF)

#define _DEBUG (0)

/* zkVM RAM is zero-initialised and the bump allocator never reuses memory,
 * so a freshly malloc'd block is already all-zero. ZKVM_FAST_ALLOC=1
 * (default) drops the redundant per-object/array memset; set to 0 to
 * restore the explicit zeroing. Kept in sync with pal/module.c. */
#ifndef ZKVM_FAST_ALLOC
#define ZKVM_FAST_ALLOC 1
#endif

/* RhpPInvoke / RhpPInvokeReturn build and tear down a PInvokeTransitionFrame
 * so the GC can scan/suspend a thread that has entered native code. The
 * zkVM guest is single-threaded, uGC never collects (so threads are never
 * suspended and the frame is never scanned), and RhpThrowEx fails fast
 * instead of unwinding (so the frame is never walked for EH). The whole
 * transition is therefore dead weight. ZKVM_STUB_PINVOKE=1 (default)
 * replaces both helpers with no-ops; set to 0 to restore the real ones. */
#ifndef ZKVM_STUB_PINVOKE
#define ZKVM_STUB_PINVOKE 1
#endif

#if ZKVM_STUB_PINVOKE
void
__wrap_RhpPInvoke(void *pFrame)
{
    (void)pFrame;
}

void
__wrap_RhpPInvokeReturn(void *pFrame)
{
    (void)pFrame;
}
#else
extern void __real_RhpPInvoke(void *pFrame);
extern void __real_RhpPInvokeReturn(void *pFrame);

void
__wrap_RhpPInvoke(void *pFrame)
{
    __real_RhpPInvoke(pFrame);
}

void
__wrap_RhpPInvokeReturn(void *pFrame)
{
    __real_RhpPInvokeReturn(pFrame);
}
#endif

/* Bulk reference copy. uGC has no write barrier, so this is just a move.
 * memmove resolves to the libziskos DMA-accelerated wrapper. */
void
__wrap_RhBulkMoveWithWriteBarrier(void *dest, void *src, size_t len)
{
    memmove(dest, src, len);
}

extern void *RhpNewObject(void *methodTable, int allocFlags);
extern void *RhpGcAlloc(void *pEEType, unsigned int uFlags,
    unsigned long numElements, void * pTransitionFrame);


extern void **S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny_NoCacheLookup(
    unsigned int *param_1, unsigned int **param_2);
extern int __real_S_P_CoreLib_System_Threading_ProcessorIdCache__ProcessorNumberSpeedCheck(void);

/* __wrap_RhpNewFast moved to pal/module.c so the downward bump allocator is
 * inlined directly into it (same translation unit as `mem` and the heap
 * bounds): no nested malloc call, single alignment step, leaf function.
 * --wrap=RhpNewFast (rhp/module_params.yml) still redirects callers there. */

void *
__wrap_RhpNewObject(void *methodTable, int allocFlags)
{
    (void)allocFlags;

    const size_t MT_BASE_SIZE_OFFSET = 0x4;
    const size_t OBJ_EETYPE_OFFSET   = 0x0;
    const size_t MIN_OBJECT_SIZE     = 0x18;

    uint32_t baseSize = *(volatile uint32_t *)((uint8_t *)methodTable + MT_BASE_SIZE_OFFSET);
    size_t total = (size_t)baseSize;

    if (total < MIN_OBJECT_SIZE)
        total = MIN_OBJECT_SIZE;

    /* Align allocation size to 8 bytes */
    total = (total + 7u) & ~(size_t)7u;

    void *obj = malloc(total);
#if !ZKVM_FAST_ALLOC
    if (obj) __builtin_memset(obj, 0, total);
#endif
    if (!obj)
        return 0;

    *(void **)((uint8_t *)obj + OBJ_EETYPE_OFFSET) = methodTable;
    return obj;
}

#define OBJ_EETYPE_OFFSET        0x0
#define ARRAY_LENGTH_OFFSET      0x8

#define MT_COMPONENT_SIZE_OFFSET 0x0
#define MT_BASE_SIZE_OFFSET      0x4

#define SZARRAY_BASE_SIZE        0x18
#define STRING_BASE_SIZE         0x16
#define STRING_COMPONENT_SIZE    0x2

static inline size_t
align_up_8(size_t x)
{
    return (x + 7u) & ~(size_t)7u;
}

static inline uint32_t
mt_base_size(void *methodTable)
{
    return *(volatile uint32_t *)((uint8_t *)methodTable + MT_BASE_SIZE_OFFSET);
}

static inline uint16_t
mt_component_size(void *methodTable)
{
    return *(volatile uint16_t *)((uint8_t *)methodTable + MT_COMPONENT_SIZE_OFFSET);
}

static inline void
init_object_header(void *obj, void *methodTable)
{
    *(void **)((uint8_t *)obj + OBJ_EETYPE_OFFSET) = methodTable;
}

static inline void
init_array_length(void *obj, unsigned long numElements)
{
    *(uint32_t *)((uint8_t *)obj + ARRAY_LENGTH_OFFSET) = (uint32_t)numElements;
}

/* Largest element count an array or string may have. The length is stored in
 * a 32-bit field (init_array_length) and .NET itself rejects anything larger
 * (Array.MaxLength). Upstream NativeAOT's fast allocators bail to a slow path
 * that throws when this is exceeded or when the byte size overflows. The
 * earlier zkVM stubs omitted that guard, so an attacker-influenced element
 * count could wrap size_t and under-allocate, after which the header/length
 * writes land out of bounds. We instead fail loudly. */
#define MAX_ARRAY_LENGTH 0x7FFFFFC7u

__attribute__((noreturn, noinline, cold))
static void
rhp_alloc_overflow(void)
{
    exit(255);
}

/* base + numElements*componentSize, 8-byte aligned, with element-count,
 * multiply and add overflow checks. Terminates instead of returning a
 * wrapped (too-small) size. */
static inline size_t
array_alloc_size(size_t baseSize, unsigned long numElements, size_t componentSize)
{
    size_t bytes;

    if (numElements > MAX_ARRAY_LENGTH)
        rhp_alloc_overflow();
    if (__builtin_mul_overflow((size_t)numElements, componentSize, &bytes))
        rhp_alloc_overflow();
    if (__builtin_add_overflow(bytes, baseSize, &bytes))
        rhp_alloc_overflow();

    return align_up_8(bytes);
}

void *
__wrap_RhpNewPtrArrayFast(void *methodTable, unsigned long numElements)
{
    size_t total = array_alloc_size((size_t)SZARRAY_BASE_SIZE, numElements, 8u);

    void *obj = malloc(total);
#if !ZKVM_FAST_ALLOC
    if (obj) __builtin_memset(obj, 0, total);
#endif
    if (!obj)
        return 0;

    init_object_header(obj, methodTable);
    init_array_length(obj, numElements);
    return obj;
}

void *
__wrap_RhpNewArrayFast(void *methodTable, unsigned long numElements)
{
    size_t comp = (size_t)mt_component_size(methodTable);
    size_t total = array_alloc_size((size_t)SZARRAY_BASE_SIZE, numElements, comp);

    void *obj = malloc(total);
#if !ZKVM_FAST_ALLOC
    if (obj) __builtin_memset(obj, 0, total);
#endif
    if (!obj)
        return 0;

    init_object_header(obj, methodTable);
    init_array_length(obj, numElements);
    return obj;
}

void *
__wrap_RhNewString(void *methodTable, unsigned long numElements)
{
    size_t total = array_alloc_size((size_t)STRING_BASE_SIZE, numElements, (size_t)STRING_COMPONENT_SIZE);

    void *obj = malloc(total);
#if !ZKVM_FAST_ALLOC
    if (obj) __builtin_memset(obj, 0, total);
#endif
    if (!obj)
        return 0;

    init_object_header(obj, methodTable);
    init_array_length(obj, numElements);
    return obj;
}

void **
__wrap_S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny(
    unsigned int *param_1, unsigned int **param_2)
{
    return S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny_NoCacheLookup(
        param_1, param_2);
}

void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventPipeEventProvider__Register()
{
}

void
__wrap_S_P_CoreLib_System_Diagnostics_Tracing_EventSource__InitializeDefaultEventSources()
{
}

int32_t
__wrap_GlobalizationNative_GetDefaultLocaleName(char *value, int valueLength)
{
    value[0] = 'e';
    value[1] = 'n';
    value[2] = '_';
    value[3] = 'U';
    value[4] = 'S';
    value[5] = '\0';
    return 1;
}

int
__wrap_S_P_CoreLib_System_Threading_ProcessorIdCache__ProcessorNumberSpeedCheck(void)
{
    if (getenv("__test_processor_number_speed_check") != NULL)
    {
        return __real_S_P_CoreLib_System_Threading_ProcessorIdCache__ProcessorNumberSpeedCheck();
    }
    /* Stub implementation - original .NET function EH info preserved via reference above */
    return 1;
}

typedef struct ThreadStaticStorageLite
{
    void *pThreadStaticStorageArray; /* offset 0: managed array object reference */
} ThreadStaticStorageLite;

static ThreadStaticStorageLite g_thread_static_storage = { 0 };

/* Optional backing store for other custom thread-static emulation (not returned directly). */
#define THREAD_STATIC_STORAGE_SIZE (256 * 1024)
static uint8_t thread_static_storage[THREAD_STATIC_STORAGE_SIZE]
    __attribute__((aligned(64))) = { 0 };
static uint8_t *thread_static_ptr = thread_static_storage;

/*
 * Wrapper for RhGetThreadStaticStorage - returns a pointer to the storage struct
 * with the managed array reference at offset 0.
 */
void *
__wrap_RhGetThreadStaticStorage(void)
{
    return &g_thread_static_storage;
}

#define TSS_MAX_TYPEMANAGERS 32
#define TSS_MAX_SLOTS        256

typedef struct ThreadStaticsKeyedStore
{
    void   *slots[TSS_MAX_SLOTS];
} ThreadStaticsKeyedStore;

static ThreadStaticsKeyedStore g_tss_by_type_manager[TSS_MAX_TYPEMANAGERS];

static inline uint32_t
tss_read_u32(const void *p)
{
    return *(const volatile uint32_t *)p;
}

static inline size_t
tss_align_up(size_t x, size_t a)
{
    return (x + (a - 1u)) & ~(a - 1u);
}

#define TSS_SLOT_BYTES 256

void *_param_1 = 0;
void *_param_2 = 0;
uint32_t _typeManagerIndex = 0;
uint32_t rhp_tss_counter = 0;

long __wrap_S_P_CoreLib_Internal_Runtime_ThreadStatics__GetUninlinedThreadStaticBaseForType(void *param_1, void *param_2)
{
    /* typeManagerIndex is read from param_1 + 8 (matches lw s3,8(s2) in disassembly) */
    uint32_t typeManagerIndex = 0;
    if (param_1 != NULL)
        typeManagerIndex = tss_read_u32((const uint8_t *)param_1 + 8);

    /* slot index comes in param_2; treat it as a 32-bit signed/unsigned index */
    uintptr_t slotU = (uintptr_t)param_2;
    uint32_t slot = (uint32_t)slotU;

#if _DEBUG
    printf("[TSS] GetTSBase(typeMgr=%u, slot=%u) param_1=%p param_2=%p\n",
           (unsigned)typeManagerIndex, (unsigned)slot, param_1, param_2);
#endif

    if (typeManagerIndex >= TSS_MAX_TYPEMANAGERS || slot >= TSS_MAX_SLOTS)
    {
#if _DEBUG
        printf("[TSS] WARN: out of bounds (typeMgr=%u/%u slot=%u/%u)\n",
               (unsigned)typeManagerIndex, (unsigned)TSS_MAX_TYPEMANAGERS,
               (unsigned)slot, (unsigned)TSS_MAX_SLOTS);
#endif
        return 0;
    }

    if (param_1 != _param_1 || param_2 != _param_2 ||
        typeManagerIndex != _typeManagerIndex)
    {
        rhp_tss_counter = 0;
        _param_1 = param_1;
        _param_2 = param_2;
        _typeManagerIndex = typeManagerIndex;
    }

    rhp_tss_counter++;
#if _DEBUG
    if (rhp_tss_counter > 5000)
    {
        printf("[TSS] WARN: too many calls (typeMgr=%u/%u slot=%u/%u)\n",
               (unsigned)typeManagerIndex, (unsigned)TSS_MAX_TYPEMANAGERS,
               (unsigned)slot, (unsigned)TSS_MAX_SLOTS);
        int *i = NULL;
        *i = 0;
    }
#endif


    void *p = g_tss_by_type_manager[typeManagerIndex].slots[slot];
    if (p != NULL)
        return (long)p;

    /* Allocate from our backing store and zero-init */
    size_t need = tss_align_up((size_t)TSS_SLOT_BYTES, 16);

    if ((size_t)(thread_static_storage + THREAD_STATIC_STORAGE_SIZE - thread_static_ptr) < need)
    {
#if _DEBUG
        printf("[TSS] OOM: backing store exhausted (need=%zu left=%zu)\n",
               need,
               (size_t)(thread_static_storage + THREAD_STATIC_STORAGE_SIZE - thread_static_ptr));
#endif
        return 0;
    }

    p = (void *)thread_static_ptr;
    thread_static_ptr += need;

    memset(p, 0, need);

    g_tss_by_type_manager[typeManagerIndex].slots[slot] = p;
#if _DEBUG
    printf("[TSS] Alloc slot: typeMgr=%u slot=%u -> %p (bytes=%zu) next=%p\n",
           (unsigned)typeManagerIndex, (unsigned)slot, p, need, (void *)thread_static_ptr);
#endif

    return (long)p;
}

void __wrap__Z16InitializeCGroupv(void)
{
}

void __wrap__Z19InitializeCpuCGroupv(void)
{
}

void __wrap___GetNonGCStaticBase_S_P_CoreLib_System_Environment(void)
{
}

void __wrap_S_P_CoreLib_System_Threading_Thread__WaitForForegroundThreads(void)
{
}
int __wrap_S_P_CoreLib_System_Threading_Lock__EnterAndGetCurrentThreadId(void)
{
    return 1;
}

void __wrap_S_P_CoreLib_System_Threading_Lock__Enter(long param_1)
{
}

void *__wrap_S_P_CoreLib_System_Threading_Lock__TryEnterSlow_0(void *param_1, void *param_2)
{
    return param_2;
}

/* Bypass the TypeLoader lock assertion */
void __wrap_S_P_TypeLoader_Internal_Runtime_TypeLoader_TypeLoaderEnvironment__VerifyTypeLoaderLockHeld(void)
{
}

/*
 * Every Lock is conceptually held by the only thread.
 */
int __wrap_S_P_CoreLib_System_Threading_Lock__get_IsHeldByCurrentThread(void *self)
{
    (void)self;
    return 1;
}

/* DeadlockAwareAcquire decides whether to run a cctor body. Real CoreLib uses
 * Lock.IsHeldByCurrentThread to detect recursive entry (A's cctor accesses B,
 * B's cctor accesses A → second call returns 0 and breaks the cycle). Our
 * Lock wraps are no-ops, so the real check is useless and we must track
 * "currently running" cctor contexts ourselves. Without this, recursive cctor
 * chains keep re-running their bodies and blow the stack. */
#define ACTIVE_CCTOR_MAX 4096
static void *g_active_cctors[ACTIVE_CCTOR_MAX];
static int   g_active_cctor_count = 0;

int __wrap_S_P_CoreLib_System_Runtime_CompilerServices_ClassConstructorRunner__DeadlockAwareAcquire(
    void *cctorChain, int idx, void *ctx)
{
    (void)cctorChain;
    (void)idx;

    /* Linear search — list is small in practice (one entry per unique cctor
     * ever touched). Once a cctor completes, the runtime zeroes *ctx and the
     * caller (EnsureClassConstructorRun) short-circuits without ever reaching
     * this wrap again, so we never need to pop. */
    for (int i = 0; i < g_active_cctor_count; i++)
        if (g_active_cctors[i] == ctx)
            return 0; /* recursive — break the cycle */

    if (g_active_cctor_count < ACTIVE_CCTOR_MAX)
        g_active_cctors[g_active_cctor_count++] = ctx;
    return 1;
}

void __wrap_S_P_CoreLib_System_Threading_Lock__Exit_0(void)
{
}

void __wrap_S_P_CoreLib_System_Threading_Lock__Exit_1(void)
{
}

void __wrap_S_P_CoreLib_System_Threading_Lock__ExitAll(void)
{
}

int __wrap__ZN6Thread10IsDetachedEv(void *)
{
    return 0;
}

int __wrap_System_Console_Interop_Sys__InitializeTerminalAndSignalHandling(void)
{
    return 1;
}

void __wrap_SystemNative_SetTerminalInvalidationHandler(void *param)
{
}

int __wrap_SystemNative_Write(int fd, const void* buffer, int bufferSize)
{
    return bufferSize;
}

extern void *S_P_CoreLib_System_Number__UInt32ToDecStr_NoSmallNumberCheck(int value);

void *__wrap_S_P_CoreLib_System_Number__UInt32ToDecStrForKnownSmallNumber(int value)
{
    return S_P_CoreLib_System_Number__UInt32ToDecStr_NoSmallNumberCheck(value);
}

/* Reverse P/Invoke transition. The real CoreLib RhpReversePInvoke attaches the
 * thread and parks it at a GC-safe point (AttachOrTrapThread2). That only makes
 * sense for a native->managed boundary entered in preemptive mode. When a
 * managed exception handler (an [UnmanagedCallersOnly] method like ZkvmThrow)
 * is entered from __wrap_RhpThrowEx, the thread is ALREADY cooperative, so the
 * real transition spins on a GC rendezvous that never comes in the
 * single-threaded, never-collecting zkVM. No-op it (matches zerolib). */
void
__wrap_RhpReversePInvoke(void *pFrame)
{
    (void)pFrame;
}

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
void __wrap_S_P_CoreLib_System_RuntimeExceptionHelpers__FailFast(void)
{
    exit(1);
}
