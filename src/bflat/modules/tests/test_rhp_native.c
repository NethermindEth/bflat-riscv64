/**
 * @file
 * @brief Unit tests for rhp_native/module.S (the GC write-barrier stubs).
 *
 * Built TWICE by run_tests.sh, once per runtime contract
 * (-DBFLAT_DOTNET=10/11 here, --defsym BFLAT_DOTNET=... for the assembler),
 * because the t3 post-increment is version-dependent: .NET 10's
 * WriteBarriers.S documents "t3 incremented by 8", .NET 11 changed it to
 * "t3 preserved" and the JIT now reuses t3 after the call. A stale +8 under
 * .NET 11 silently corrupts Dictionary inserts, so both contracts are
 * pinned here rather than assumed.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>

#include "common.h"

#ifndef BFLAT_DOTNET
#define BFLAT_DOTNET 10
#endif

#if BFLAT_DOTNET < 11
#define DEST_DELTA 8 /* .NET 10: helper advances t3 past the written slot */
#else
#define DEST_DELTA 0 /* .NET 11: t3 preserved for caller reuse */
#endif

struct tregs {
    uint64_t t3; /* destination */
    uint64_t t4; /* value */
    uint64_t t5; /* source (byref copy) */
};

extern void call_barrier(struct tregs *r, void (*fn)(void));

extern void __wrap_RhpAssignRefRiscV64(void);
extern void __wrap_RhpCheckedAssignRef(void);
extern void __wrap_RhpByRefAssignRef(void);
extern void __wrap_RhpAssignRef(void **dst, void *value);

/* Heap stand-in: a slot array with guards on both sides, so an off-by-one
 * store (the exact failure mode of a wrong t3 delta) is caught. */
static uint64_t heap[8];

static void reset_heap(void)
{
    for (unsigned i = 0; i < 8; i++)
        heap[i] = 0xEEEEEEEEEEEEEEEEULL;
}

/* Shared expectations for the two single-assign helpers. */
static void check_single_assign(void (*fn)(void), const char *name)
{
    reset_heap();
    struct tregs r = {
        .t3 = (uint64_t)&heap[2],
        .t4 = 0xCAFEBABEDEADBEEFULL,
        .t5 = 0x5555555555555555ULL,
    };
    call_barrier(&r, fn);

    /* the value lands in the destination slot ... */
    if (heap[2] != 0xCAFEBABEDEADBEEFULL) {
        t_fail++;
        fprintf(stderr, "FAIL %s: slot = 0x%016llx\n", name,
                (unsigned long long)heap[2]);
    } else {
        t_pass++;
    }
    /* ... and nowhere else */
    CHECK(heap[1] == 0xEEEEEEEEEEEEEEEEULL);
    CHECK(heap[3] == 0xEEEEEEEEEEEEEEEEULL);

    /* versioned t3 contract */
    if (r.t3 != (uint64_t)&heap[2] + DEST_DELTA) {
        t_fail++;
        fprintf(stderr,
                "FAIL %s: t3 delta = %lld, expected %d (BFLAT_DOTNET=%d)\n",
                name, (long long)(r.t3 - (uint64_t)&heap[2]), DEST_DELTA,
                BFLAT_DOTNET);
    } else {
        t_pass++;
    }

    /* value/source registers are left alone */
    CHECK(r.t4 == 0xCAFEBABEDEADBEEFULL);
    CHECK(r.t5 == 0x5555555555555555ULL);
}

int main(void)
{
    check_single_assign(__wrap_RhpAssignRefRiscV64, "RhpAssignRefRiscV64");
    check_single_assign(__wrap_RhpCheckedAssignRef, "RhpCheckedAssignRef");

    /* Byref copy: load from t5, store to t3, both pointers advance by 8.
     * This helper is NOT version-gated - the .NET 11 change covers only the
     * single-assign pair - so the increments are unconditional here. */
    {
        reset_heap();
        heap[5] = 0x0123456789ABCDEFULL; /* source slot */
        struct tregs r = {
            .t3 = (uint64_t)&heap[2],
            .t4 = 0, /* scratch: receives the loaded value */
            .t5 = (uint64_t)&heap[5],
        };
        call_barrier(&r, __wrap_RhpByRefAssignRef);

        CHECK(heap[2] == 0x0123456789ABCDEFULL);
        CHECK(heap[1] == 0xEEEEEEEEEEEEEEEEULL);
        CHECK(heap[3] == 0xEEEEEEEEEEEEEEEEULL);
        CHECK(r.t3 == (uint64_t)&heap[2] + 8);
        CHECK(r.t5 == (uint64_t)&heap[5] + 8);
        CHECK(r.t4 == 0x0123456789ABCDEFULL); /* value passed through t4 */
    }

    /* Walking a run of slots must stay contiguous: the byref helper is used
     * to copy struct fields one word at a time. */
    {
        reset_heap();
        uint64_t src[3] = { 0xA1, 0xB2, 0xC3 };
        struct tregs r = {
            .t3 = (uint64_t)&heap[0],
            .t4 = 0,
            .t5 = (uint64_t)&src[0],
        };
        for (int i = 0; i < 3; i++)
            call_barrier(&r, __wrap_RhpByRefAssignRef);
        CHECK(heap[0] == 0xA1 && heap[1] == 0xB2 && heap[2] == 0xC3);
        CHECK(heap[3] == 0xEEEEEEEEEEEEEEEEULL);
        CHECK(r.t3 == (uint64_t)&heap[3]);
        CHECK(r.t5 == (uint64_t)&src[3]);
    }

    /* Plain C-ABI variant: a0 = destination, a1 = value, no increments. */
    {
        reset_heap();
        void *v = (void *)0x1234567890ABCDEFULL;
        __wrap_RhpAssignRef((void **)&heap[4], v);
        CHECK(heap[4] == 0x1234567890ABCDEFULL);
        CHECK(heap[3] == 0xEEEEEEEEEEEEEEEEULL);
        CHECK(heap[5] == 0xEEEEEEEEEEEEEEEEULL);
        __wrap_RhpAssignRef((void **)&heap[4], NULL); /* null store */
        CHECK(heap[4] == 0);
    }

    TEST_MAIN_END(BFLAT_DOTNET < 11 ? "rhp_native(net10)"
                                    : "rhp_native(net11)");
}
