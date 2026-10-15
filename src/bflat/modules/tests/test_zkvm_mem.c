/**
 * @file
 * @brief Unit tests for the __wrap_memcpy, __wrap_memmove and __wrap_memset
 *        in the zkvm_sp1 / zkvm_openvm entry modules (run_tests.sh builds one
 *        binary per module).
 *
 * The two modules' routines differ only in how they treat alignment - SP1's
 * align the destination and merge a misaligned source from aligned words,
 * OpenVM's use unaligned words and finish with an overlapping edge word - so
 * both face the same exhaustive checks against a byte loop, with guard bytes
 * around the destination and the return value checked:
 *
 * - memcpy, and memmove between distant buffers in both directions: every
 *   source offset 0-15 x destination offset 0-15 x length 0-600.
 * - memmove within one buffer: every source offset 0-15 x displacement
 *   -40..+40 (destination before the source, overlapping either way, after
 *   it) x length 0-300.
 * - memset: every destination offset 0-15 x length 0-600 x six fill values.
 *
 * A second pass puts the operands flush against PROT_NONE pages, so an
 * access outside them - beyond what the aligned words holding them cover -
 * faults instead of passing unnoticed.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>
#include <sys/mman.h>

#include "common.h"

extern void *__wrap_memcpy(void *dst, const void *src, size_t n);
extern void *__wrap_memmove(void *dst, const void *src, size_t n);
extern void *__wrap_memset(void *dst, int c, size_t n);

/* Linked with the whole entry module, whose other routines reference these;
 * none of them runs here. */
void noos_start_main(int argc, char *argv[]) { (void)argc; (void)argv; _exit(99); }
int noos_main(int argc, char *argv[]) { (void)argc; (void)argv; _exit(99); }
void zkvm_keccak_permute(void *state) { (void)state; _exit(99); }
void zkvm_keccakf(void *state) { (void)state; _exit(99); }
uint8_t zkvm_test_stack[16] __attribute__((aligned(16)));
uint8_t zkvm_test_gp[16] __attribute__((aligned(16)));

#define MAX_OFFSET 16
#define MAX_LEN 600
#define GUARD 32
#define BUF (GUARD + MAX_OFFSET + MAX_LEN + GUARD)

#define MAX_DISP 40
#define MAX_MOVE_LEN 300
#define MOVE_BUF (GUARD + MAX_DISP + MAX_OFFSET + MAX_MOVE_LEN + MAX_DISP + GUARD)

static uint8_t src_buf[BUF] __attribute__((aligned(16)));
static uint8_t dst_buf[BUF] __attribute__((aligned(16)));
static uint8_t want_buf[BUF] __attribute__((aligned(16)));
static uint8_t move_buf[MOVE_BUF] __attribute__((aligned(16)));
static uint8_t move_want[MOVE_BUF] __attribute__((aligned(16)));

static long cases;
static long failures;

typedef void *(*copy_fn)(void *dst, const void *src, size_t n);

static void report(const char *what, int so, int dof, long n, long at)
{
    if (++failures <= 5)
        fprintf(stderr, "FAIL %s: src+%d dst+%d n=%ld at %ld\n", what, so,
                dof, n, at);
}

static void finish(const char *name, long cases_before, long failures_before)
{
    if (failures == failures_before)
        t_pass++;
    else
        t_fail++;
    printf("%s: %ld cases, %ld failed\n", name, cases - cases_before,
           failures - failures_before);
}

/* Copies between two distinct buffers. */
static void exhaustive(const char *name, copy_fn copy, uint8_t *from,
                       uint8_t *to)
{
    long cases_before = cases, failures_before = failures;
    for (int i = 0; i < BUF; i++)
        from[i] = (uint8_t)(i * 131 + 7);

    for (int so = 0; so < MAX_OFFSET; so++)
        for (int dof = 0; dof < MAX_OFFSET; dof++)
            for (long n = 0; n <= MAX_LEN; n++) {
                const uint8_t *s = from + GUARD + so;
                uint8_t *d = to + GUARD + dof;
                memset(to, 0xEE, BUF);
                memset(want_buf, 0xEE, BUF);
                for (long i = 0; i < n; i++)
                    want_buf[GUARD + dof + i] = s[i];

                cases++;
                if (copy(d, s, n) != d) {
                    report(name, so, dof, n, -1);
                    continue;
                }
                for (long i = 0; i < BUF; i++)
                    if (to[i] != want_buf[i]) {
                        report(name, so, dof, n, i - GUARD - dof);
                        break;
                    }
            }

    finish(name, cases_before, failures_before);
}

static void memmove_overlap(void)
{
    long cases_before = cases, failures_before = failures;

    for (int so = 0; so < MAX_OFFSET; so++)
        for (int disp = -MAX_DISP; disp <= MAX_DISP; disp++)
            for (long n = 0; n <= MAX_MOVE_LEN; n++) {
                long s = GUARD + MAX_DISP + so;
                long d = s + disp;
                for (int i = 0; i < MOVE_BUF; i++)
                    move_buf[i] = (uint8_t)(i * 131 + 7);
                memcpy(move_want, move_buf, MOVE_BUF);
                for (long i = 0; i < n; i++)
                    move_want[d + i] = move_buf[s + i];

                cases++;
                if (__wrap_memmove(move_buf + d, move_buf + s, n) !=
                    move_buf + d) {
                    report("memmove overlap", so, disp, n, -1);
                    continue;
                }
                for (long i = 0; i < MOVE_BUF; i++)
                    if (move_buf[i] != move_want[i]) {
                        report("memmove overlap", so, disp, n, i - d);
                        break;
                    }
            }

    finish("memmove overlap", cases_before, failures_before);
}

static void memset_exhaustive(void)
{
    static const int fills[] = { 0, 1, 0x5a, 0xff, -1, 0x1234 };
    long cases_before = cases, failures_before = failures;

    for (size_t f = 0; f < sizeof(fills) / sizeof(fills[0]); f++)
        for (int dof = 0; dof < MAX_OFFSET; dof++)
            for (long n = 0; n <= MAX_LEN; n++) {
                uint8_t *d = dst_buf + GUARD + dof;
                memset(dst_buf, 0xEE, BUF);
                memset(want_buf, 0xEE, BUF);
                for (long i = 0; i < n; i++)
                    want_buf[GUARD + dof + i] = (uint8_t)fills[f];

                cases++;
                if (__wrap_memset(d, fills[f], n) != d) {
                    report("memset", fills[f], dof, n, -1);
                    continue;
                }
                for (long i = 0; i < BUF; i++)
                    if (dst_buf[i] != want_buf[i]) {
                        report("memset", fills[f], dof, n, i - GUARD - dof);
                        break;
                    }
            }

    finish("memset", cases_before, failures_before);
}

static void fill_page(uint8_t *page, long size)
{
    for (long i = 0; i < size; i++)
        page[i] = (uint8_t)(i * 37 + 11);
}

/* A backward memmove inside the guarded page, its source starting at
 * mid + start: the reference is computed from a copy taken beforehand. */
static void guarded_move(uint8_t *mid, long page, long start, int disp,
                         long n, const char *what, int so)
{
    fill_page(mid, page);
    for (long i = 0; i < n; i++)
        want_buf[i] = mid[start + i];
    __wrap_memmove(mid + start + disp, mid + start, n);
    if (memcmp(mid + start + disp, want_buf, n) != 0)
        report(what, so, disp, n, -1);
}

/* One readable page between two PROT_NONE pages. Each operand sits at the
 * start of the readable page (every offset) or ends at its end (every
 * length, which walks the start through every alignment). */
static void guarded(void)
{
    long page = sysconf(_SC_PAGESIZE);
    uint8_t *map = mmap(NULL, 3 * page, PROT_READ | PROT_WRITE,
                        MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    CHECK(map != MAP_FAILED);
    if (map == MAP_FAILED)
        return;
    uint8_t *mid = map + page;
    fill_page(mid, page);
    CHECK(mprotect(map, page, PROT_NONE) == 0);
    CHECK(mprotect(mid + page, page, PROT_NONE) == 0);

    long before = failures;
    for (int so = 0; so < MAX_OFFSET; so++)
        for (long n = 0; n <= MAX_LEN; n++) {
            const uint8_t *s = mid + so;
            __wrap_memcpy(dst_buf, s, n);
            if (memcmp(dst_buf, s, n) != 0)
                report("guarded memcpy head", so, 0, n, -1);

            s = mid + page - n - so;
            __wrap_memcpy(dst_buf, s, n);
            if (memcmp(dst_buf, s, n) != 0)
                report("guarded memcpy tail", so, 0, n, -1);
        }

    /* A backward move reads below the destination: the source flush against
     * the page's start, or the destination ending at the page's end. */
    for (int so = 0; so < MAX_OFFSET; so++)
        for (int disp = 1; disp <= MAX_OFFSET; disp++)
            for (long n = 0; n <= MAX_MOVE_LEN; n++) {
                guarded_move(mid, page, so, disp, n, "guarded memmove head",
                             so);
                guarded_move(mid, page, page - n - disp - so, disp, n,
                             "guarded memmove tail", so);
            }

    for (int so = 0; so < MAX_OFFSET; so++)
        for (long n = 0; n <= MAX_LEN; n++) {
            uint8_t *d = mid + so;
            __wrap_memset(d, 0x5a, n);
            for (long i = 0; i < n; i++)
                if (d[i] != 0x5a) {
                    report("guarded memset head", so, 0, n, i);
                    break;
                }

            d = mid + page - n - so;
            __wrap_memset(d, 0xa5, n);
            for (long i = 0; i < n; i++)
                if (d[i] != 0xa5) {
                    report("guarded memset tail", so, 0, n, i);
                    break;
                }
        }
    CHECK(failures == before);
    munmap(map, 3 * page);
}

int main(void)
{
    exhaustive("memcpy", __wrap_memcpy, src_buf, dst_buf);
    exhaustive("memmove forward", __wrap_memmove, src_buf, dst_buf);
    exhaustive("memmove reverse", __wrap_memmove, dst_buf, src_buf);
    memmove_overlap();
    memset_exhaustive();
    guarded();
    TEST_MAIN_END("mem(" ZKVM_MODULE_NAME ")");
}
