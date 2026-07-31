/**
 * @file
 * @brief Unit tests for _zkvm_restore in zkvm_zisk/module.S - the preinit
 *        snapshot trampoline that either cold-boots or reloads x1..x31
 *        from the baked blob and resumes at the captured PC.
 *
 * Both paths are exercised for real: the blob lives in .rodata, so the
 * test mprotect()s its page writable, fills it the way `bflat rebake`
 * would, and jumps in. The landing pad (asm, in restore_pad.S) records
 * the registers it was resumed with, so the offsets - regs[i] at
 * base + 16 + i*8 - and the x5/x6 endgame are checked byte for byte
 * rather than by inspection.
 *
 * _zkvm_restore clobbers every register including sp/gp/tp and never
 * returns, so each case runs in a forked child.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

#include "common.h"

#define ZKSP 0x5A4B5350u
#define BLOB_REGS 16 /* byte offset of regs[0] inside the blob */

extern uint8_t __zkvm_snapshot[];
extern void _zkvm_restore(void);

/* Landing pad: stores the registers it was entered with into
 * restore_seen[] and _exit(RESUMED). */
extern void restore_pad(void);
uint64_t restore_seen[32];

/* Guest stack / gp for the cold path, which falls through to _start. */
uint8_t zkvm_test_stack[64 * 1024] __attribute__((aligned(16)));
uint8_t zkvm_test_gp[4096] __attribute__((aligned(16)));

#define COLD 30    /* _start was reached (cold boot) */
#define RESUMED 31 /* landing pad was reached (warm resume) */

/* Cold path lands in _start -> __libc_start_main; stub it to report. */
int uBootstrap_main(int argc, char *argv[])
{
    (void)argc;
    (void)argv;
    _exit(98);
}

/* Renamed by objcopy in run_tests.sh; see test_zkvm_start.c. */
int test_libc_start_main(int (*main_fn)(int, char **), long argc,
                         char **argv, void (*init)(void), void (*fini)(void),
                         void (*rtld_fini)(void), void *stack_end)
{
    (void)main_fn;
    (void)argc;
    (void)argv;
    (void)init;
    (void)fini;
    (void)rtld_fini;
    (void)stack_end;
    _exit(COLD);
}

static void make_blob_writable(void)
{
    long pg = sysconf(_SC_PAGESIZE);
    uintptr_t base = (uintptr_t)__zkvm_snapshot & ~(uintptr_t)(pg - 1);
    /* The blob is 4096 bytes and may straddle a page boundary. */
    if (mprotect((void *)base, (size_t)pg * 2, PROT_READ | PROT_WRITE) != 0)
        _exit(97);
}

/* Fill the blob the way `bflat rebake` does: magic, captured pc, x0..x31. */
static void bake(uint64_t pc, const uint64_t regs[32])
{
    make_blob_writable();
    memset(__zkvm_snapshot, 0, 272);
    *(uint32_t *)(__zkvm_snapshot + 0) = ZKSP;
    *(uint64_t *)(__zkvm_snapshot + 8) = pc;
    for (int i = 0; i < 32; i++)
        *(uint64_t *)(__zkvm_snapshot + BLOB_REGS + i * 8) = regs[i];
}

int main(void)
{
    /* --- cold boot: unpatched blob (magic 0) tail-jumps to _start --- */
    EXPECT_EXIT(COLD, {
        make_blob_writable();
        memset(__zkvm_snapshot, 0, 272);
        _zkvm_restore();
    });

    /* --- a non-ZKSP magic is also a cold boot, not a wild jump --- */
    EXPECT_EXIT(COLD, {
        make_blob_writable();
        memset(__zkvm_snapshot, 0, 272);
        *(uint32_t *)__zkvm_snapshot = 0xDEADBEEF;
        /* a plausible-looking PC that must NOT be taken */
        *(uint64_t *)(__zkvm_snapshot + 8) = (uint64_t)&restore_pad;
        _zkvm_restore();
    });

    /* --- warm resume: every register comes back and the pad is entered
     * at the captured PC. x2 (sp) gets a real stack so the pad can run;
     * x5/x6 are the trampoline's own scratch and are excluded below. --- */
    EXPECT_EXIT(RESUMED, {
        uint64_t regs[32];
        for (int i = 0; i < 32; i++)
            regs[i] = 0xAA00000000000000ULL | (uint64_t)i;
        regs[2] = (uint64_t)(zkvm_test_stack + sizeof(zkvm_test_stack) - 64);
        regs[10] = (uint64_t)restore_seen; /* a0: where the pad writes */
        bake((uint64_t)&restore_pad, regs);
        _zkvm_restore();
    });

    /* The child above exits inside the pad, so the parent cannot read
     * restore_seen through shared memory. Re-run the same resume with the
     * recording buffer in a MAP_SHARED page and inspect it here. */
    {
        uint64_t *shared = mmap(NULL, 4096, PROT_READ | PROT_WRITE,
                                MAP_SHARED | MAP_ANONYMOUS, -1, 0);
        CHECK(shared != MAP_FAILED);
        if (shared != MAP_FAILED) {
            memset(shared, 0, 4096);
            uint64_t regs[32];
            for (int i = 0; i < 32; i++)
                regs[i] = 0xAA00000000000000ULL | (uint64_t)i;
            regs[2] =
                (uint64_t)(zkvm_test_stack + sizeof(zkvm_test_stack) - 64);
            regs[10] = (uint64_t)shared;

            pid_t pid = fork();
            if (pid == 0) {
                bake((uint64_t)&restore_pad, regs);
                _zkvm_restore();
                _exit(96);
            }
            int st;
            waitpid(pid, &st, 0);
            CHECK(WIFEXITED(st) && WEXITSTATUS(st) == RESUMED);

            /* Every register except x6 must come back exactly as baked -
             * x2 (sp) and x10 (a0) included, since the test chose their
             * blob values. */
            for (int i = 1; i < 32; i++) {
                if (i == 6)
                    continue;
                if (shared[i] != regs[i]) {
                    t_fail++;
                    fprintf(stderr,
                            "FAIL x%d: got 0x%016llx, want 0x%016llx\n", i,
                            (unsigned long long)shared[i],
                            (unsigned long long)regs[i]);
                } else {
                    t_pass++;
                }
            }
            CHECK(shared[0] == 0); /* x0 stays hardwired zero */
            /* x5 (t0) holds the blob base until the very end, then is
             * reloaded from regs[5] - covered by the sweep above. x6 (t1)
             * is the one documented casualty: it carries the target PC
             * into the jump, so it lands holding the captured PC rather
             * than regs[6]. */
            CHECK(shared[6] == (uint64_t)&restore_pad);
            CHECK(shared[6] != regs[6]);
            munmap(shared, 4096);
        }
    }

    TEST_MAIN_END("zkvm_restore");
}
