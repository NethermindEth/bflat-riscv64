/**
 * @file
 * @brief Unit tests for the __wrap_memcpy in the zkvm_sp1 / zkvm_openvm
 *        entry modules (run_tests.sh builds one binary per module).
 *
 * The two routines differ only in how they treat alignment - SP1's aligns
 * the destination and merges a misaligned source from aligned words, OpenVM's
 * copies unaligned words and finishes with the copy's last word - so both
 * face the same exhaustive check against a byte loop: every source offset
 * 0-15 x destination offset 0-15 x length 0-600, with guard bytes around
 * the destination and the return value checked.
 *
 * A second pass puts the source flush against PROT_NONE pages on either
 * side, so a load outside [src, src + n) - beyond what the aligned words
 * holding the source cover - faults instead of passing unnoticed.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>
#include <sys/mman.h>

#include "common.h"

extern void *__wrap_memcpy(void *dst, const void *src, size_t n);

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

static uint8_t src_buf[BUF] __attribute__((aligned(16)));
static uint8_t dst_buf[BUF] __attribute__((aligned(16)));
static uint8_t want_buf[BUF] __attribute__((aligned(16)));

static long cases;
static long failures;

static void report(const char *what, int so, int dof, long n, long at)
{
    if (++failures <= 5)
        fprintf(stderr, "FAIL %s: src+%d dst+%d n=%ld at %ld\n", what, so,
                dof, n, at);
}

static void exhaustive(void)
{
    for (int i = 0; i < BUF; i++)
        src_buf[i] = (uint8_t)(i * 131 + 7);

    for (int so = 0; so < MAX_OFFSET; so++)
        for (int dof = 0; dof < MAX_OFFSET; dof++)
            for (long n = 0; n <= MAX_LEN; n++) {
                const uint8_t *s = src_buf + GUARD + so;
                uint8_t *d = dst_buf + GUARD + dof;
                memset(dst_buf, 0xEE, BUF);
                memset(want_buf, 0xEE, BUF);
                for (long i = 0; i < n; i++)
                    want_buf[GUARD + dof + i] = s[i];

                cases++;
                if (__wrap_memcpy(d, s, n) != d) {
                    report("return value", so, dof, n, -1);
                    continue;
                }
                for (long i = 0; i < BUF; i++)
                    if (dst_buf[i] != want_buf[i]) {
                        report("contents", so, dof, n, i - GUARD - dof);
                        break;
                    }
            }

    if (failures == 0)
        t_pass++;
    else
        t_fail++;
    printf("exhaustive: %ld cases, %ld failed\n", cases, failures);
}

/* One readable page between two PROT_NONE pages. The source sits at the
 * start of the readable page (every offset) or ends at its end (every
 * length, which walks the start through every alignment). */
static void guarded_source(void)
{
    long page = sysconf(_SC_PAGESIZE);
    uint8_t *map = mmap(NULL, 3 * page, PROT_READ | PROT_WRITE,
                        MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    CHECK(map != MAP_FAILED);
    if (map == MAP_FAILED)
        return;
    uint8_t *mid = map + page;
    for (long i = 0; i < page; i++)
        mid[i] = (uint8_t)(i * 37 + 11);
    CHECK(mprotect(map, page, PROT_NONE) == 0);
    CHECK(mprotect(mid + page, page, PROT_NONE) == 0);

    long before = failures;
    for (int so = 0; so < MAX_OFFSET; so++)
        for (long n = 0; n <= MAX_LEN; n++) {
            const uint8_t *s = mid + so;
            __wrap_memcpy(dst_buf, s, n);
            if (memcmp(dst_buf, s, n) != 0)
                report("guarded head", so, 0, n, -1);

            s = mid + page - n - so;
            __wrap_memcpy(dst_buf, s, n);
            if (memcmp(dst_buf, s, n) != 0)
                report("guarded tail", so, 0, n, -1);
        }
    CHECK(failures == before);
    munmap(map, 3 * page);
}

int main(void)
{
    exhaustive();
    guarded_source();
    TEST_MAIN_END("memcpy(" ZKVM_MODULE_NAME ")");
}
