/**
 * @file
 * @brief libFuzzer target for pal's bump-allocator family (+ rust_sys).
 *
 * The fuzz input is interpreted as a sequence of allocator operations
 * with attacker-controlled sizes/alignments (full 64-bit range - the
 * historic pointer-underflow lived exactly there). After every operation
 * the allocator invariants are asserted:
 *   - a non-NULL result lies inside [heap_bottom+8, heap_top) and honors
 *     the requested alignment;
 *   - the size header below the block covers the request;
 *   - the bump pointer only moves down (except mark/reset) and stays
 *     inside the window;
 *   - realloc preserves the old payload.
 * Violations trap; ASan/UBSan catch wild writes and arithmetic UB.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#define HEAP_SIZE (1u << 20)

char _kernel_heap_bottom[HEAP_SIZE] __attribute__((aligned(4096)));
extern const char _kernel_heap_top[]; /* --defsym'd to bottom + HEAP_SIZE */
uint8_t *g_zk_bump_ptr;
long __real_syscall(long number, ...) { (void)number; return 0; }

extern void *__wrap___libc_malloc_impl(unsigned long n);
extern void *__wrap___libc_realloc(void *p, unsigned long n);
extern void *__wrap_calloc(unsigned long nmemb, unsigned long size);
extern void *__wrap_aligned_alloc(unsigned long align, unsigned long size);
extern int __wrap_posix_memalign(void **out, unsigned long align,
                                 unsigned long size);
extern void *__wrap_memalign(unsigned long align, unsigned long size);
extern void __wrap___libc_free(void *p);
extern void *zk_heap_mark(void);
extern void zk_heap_reset(void *m);
extern void *__wrap_sys_alloc_aligned(int bytes, int align);

/* rust_sys delegates to the unwrapped name. */
void *__libc_malloc_impl(unsigned long n)
{
    return __wrap___libc_malloc_impl(n);
}

#include <stdio.h>

#define REQUIRE(cond)                                                   \
    do {                                                                \
        if (!(cond)) {                                                  \
            fprintf(stderr, "REQUIRE failed at %s:%d: %s\n",            \
                    __FILE__, __LINE__, #cond);                         \
            __builtin_trap();                                           \
        }                                                               \
    } while (0)

static uintptr_t bottom(void) { return (uintptr_t)_kernel_heap_bottom; }
static uintptr_t top(void) { return (uintptr_t)_kernel_heap_top; }

/* Blocks live while they are tracked; mark/reset invalidates them all.
 *
 * `reallocable` marks blocks that came from the plain malloc path, the
 * only ones realloc may be called on: an over-aligned block's returned
 * pointer is shifted UP inside a larger allocation, so its p-8 is payload
 * rather than the size header realloc reads. That is a real precondition
 * of the module (nothing in the image reallocs an aligned_alloc block -
 * SystemNative_AlignedAlloc pairs with AlignedFree), not a defect, so the
 * harness must respect it instead of reporting it. */
#define MAX_LIVE 64
static struct {
    uint8_t *p;
    uint64_t n; /* requested size */
    uint8_t tag;
    uint8_t reallocable;
} live[MAX_LIVE];
static unsigned n_live;

static void check_block(void *p, uint64_t n, unsigned long align)
{
    if (p == NULL)
        return;
    uintptr_t a = (uintptr_t)p;
    REQUIRE(a >= bottom() + 8);
    REQUIRE(a + n <= top());
    REQUIRE(align == 0 || (a % align) == 0);
    REQUIRE(g_zk_bump_ptr == 0 ||
            ((uintptr_t)g_zk_bump_ptr >= bottom() &&
             (uintptr_t)g_zk_bump_ptr <= top()));
}

static void track(void *p, uint64_t n, uint8_t tag, uint8_t reallocable)
{
    if (p == NULL || n_live >= MAX_LIVE || n == 0)
        return;
    /* Pattern the block so realloc copy checks have teeth. */
    uint64_t span = n < 64 ? n : 64;
    memset(p, tag, (size_t)span);
    live[n_live].p = p;
    live[n_live].n = n;
    live[n_live].tag = tag;
    live[n_live].reallocable = reallocable;
    n_live++;
}

/* Index of a reallocable live block at or after `from`, or -1. */
static int pick_reallocable(unsigned from)
{
    for (unsigned k = 0; k < n_live; k++) {
        unsigned i = (from + k) % n_live;
        if (live[i].reallocable)
            return (int)i;
    }
    return -1;
}

static uint64_t take_u64(const uint8_t **d, size_t *left)
{
    uint64_t v = 0;
    size_t k = *left < 8 ? *left : 8;
    memcpy(&v, *d, k);
    *d += k;
    *left -= k;
    return v;
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    /* Fresh allocator state per input (the arena content may persist -
     * the real heap never starts dirty, but the allocator must not care
     * for these invariants). */
    g_zk_bump_ptr = 0;
    n_live = 0;
    void *mark = NULL;

    while (size >= 2) {
        uint8_t op = *data++ % 10;
        uint8_t sel = *data++;
        size -= 2;

        switch (op) {
        case 0: { /* malloc, full 64-bit size range */
            uint64_t n = take_u64(&data, &size);
            uintptr_t before = (uintptr_t)g_zk_bump_ptr;
            void *p = __wrap___libc_malloc_impl(n);
            check_block(p, p ? n : 0, 8);
            if (p) {
                /* header covers the request */
                REQUIRE(*(uint64_t *)((uint8_t *)p - 8) >= n);
                /* bump pointer only moves down */
                REQUIRE(before == 0 ||
                        (uintptr_t)g_zk_bump_ptr <= before);
                track(p, n, sel, 1);
            }
            break;
        }
        case 1: { /* small malloc (keeps sequences alive longer) */
            uint64_t n = sel;
            void *p = __wrap___libc_malloc_impl(n);
            check_block(p, p ? n : 0, 8);
            track(p, n, sel, 1);
            break;
        }
        case 2: { /* realloc a live block (plain-malloc blocks only) */
            uint64_t n = take_u64(&data, &size);
            if (n_live == 0)
                break;
            int pick = pick_reallocable(sel % n_live);
            if (pick < 0)
                break;
            unsigned i = (unsigned)pick;
            uint64_t old_n = live[i].n;
            uint8_t tag = live[i].tag;
            void *q = __wrap___libc_realloc(live[i].p, n);
            check_block(q, q ? n : 0, 8);
            if (q != NULL) {
                /* old payload preserved (the patterned prefix) */
                uint64_t span = old_n < 64 ? old_n : 64;
                if (n < span)
                    span = n;
                for (uint64_t k = 0; k < span; k++)
                    REQUIRE(((uint8_t *)q)[k] == tag);
                /* Re-pattern for the NEW size: an in-place grow within the
                 * header's capacity extends the block with bytes nobody
                 * ever wrote, so the tracked size must never exceed the
                 * patterned span. */
                uint64_t repat = n < 64 ? n : 64;
                memset(q, tag, (size_t)repat);
                live[i].p = q;
                live[i].n = n;
            }
            break;
        }
        case 3: { /* realloc(NULL) == malloc */
            void *p = __wrap___libc_realloc(NULL, sel);
            check_block(p, p ? sel : 0, 8);
            track(p, sel, sel, 1);
            break;
        }
        case 4: { /* calloc with overflow-prone factors */
            uint64_t a = take_u64(&data, &size);
            void *p = __wrap_calloc(a, sel);
            /* NOTE: no zero-content check - calloc's zeroing relies on
             * "zkVM RAM starts zero and the heap never reuses memory",
             * which the recycled fuzz arena deliberately violates. */
            if (p && a != 0 && sel != 0 && a <= (uint64_t)~0UL / sel)
                check_block(p, a * (uint64_t)sel, 8);
            if (a != 0 && sel != 0 && a > (uint64_t)~0UL / sel)
                REQUIRE(p == NULL); /* overflow must fail */
            break;
        }
        case 5: { /* aligned_alloc, power-of-two alignment */
            uint64_t n = take_u64(&data, &size);
            unsigned long align = 1UL << (sel % 16);
            void *p = __wrap_aligned_alloc(align, n);
            check_block(p, p ? n : 0, align);
            track(p, n, sel | 1, 0); /* shifted pointer: never realloc'd */
            break;
        }
        case 6: { /* posix_memalign mirrors aligned_alloc */
            uint64_t n = take_u64(&data, &size);
            unsigned long align = 1UL << (sel % 16);
            void *out = (void *)(uintptr_t)0x1;
            int rc = __wrap_posix_memalign(&out, align, n);
            REQUIRE(rc == 0 || rc == 12);
            if (rc == 0)
                check_block(out, n, align);
            else
                REQUIRE(out == (void *)(uintptr_t)0x1);
            break;
        }
        case 7: { /* rust_sys entry point (int-typed sizes) */
            int n = (int)((sel & 0x0F) * 129);
            int align = 1 << ((sel >> 4) & 0x0F); /* 1 .. 32768 */
            void *p = __wrap_sys_alloc_aligned(n, align);
            check_block(p, p ? (uint64_t)n : 0,
                        align < 8 ? 8 : (unsigned long)align);
            break;
        }
        case 8: { /* mark ... reset drops everything after the mark */
            if (mark == NULL) {
                mark = zk_heap_mark();
            } else {
                zk_heap_reset(mark);
                REQUIRE(zk_heap_mark() == mark);
                mark = NULL;
                n_live = 0; /* all blocks are dead now */
            }
            break;
        }
        case 9: { /* free is a no-op and must not disturb anything */
            uintptr_t before = (uintptr_t)g_zk_bump_ptr;
            __wrap___libc_free(n_live ? live[sel % n_live].p : NULL);
            REQUIRE((uintptr_t)g_zk_bump_ptr == before);
            break;
        }
        }
    }
    return 0;
}
