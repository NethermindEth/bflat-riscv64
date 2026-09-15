/**
 * @file
 * @brief Unit tests for the _start bootstrap in the zkvm_* entry modules
 *        (run_tests.sh builds one binary per module).
 *
 * _start is the guest's very first instruction: it points gp/sp at the
 * linker-script symbols and then hands control to the start-up routine
 * the target uses. Three shapes exist and each gets its own build:
 *
 *   ZKVM_ENTRY_LIBC (zisk, zisk_sim)
 *       __libc_start_main(uBootstrap_main, argc, argv, 0, 0, 0, sp)
 *   ZKVM_ENTRY_NOOS (openvm)
 *       noos_start_main(argc, argv) - musl's start-up replaced wholesale
 *   ZKVM_ENTRY_SP1 (sp1)
 *       __start() - SP1's own runtime entry, which initialises the Rust
 *       allocator and the public-values hasher and then calls main();
 *       main is the module's __wrap_main, which calls noos_main(argc, argv)
 *       and RETURNS its value, because SP1 halts with it.
 *
 * The linker symbols _start dereferences are supplied here as real arrays
 * via --defsym, and every routine it calls is a stub that records what it
 * was handed. _start never returns, so those cases run in a forked child
 * and the verdict travels back as the exit status.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>
#include <string.h>

#include "common.h"

/* Guest stack and global-pointer anchor; run_tests.sh --defsym's
 * _init_stack_top / _global_pointer to the ends of these. */
uint8_t zkvm_test_stack[64 * 1024] __attribute__((aligned(16)));
uint8_t zkvm_test_gp[4096] __attribute__((aligned(16)));

/* run_tests.sh objcopy-renames the module's _start and its call to
 * __libc_start_main, which would otherwise collide with crt1.o and libc
 * in this hosted test binary. */
extern void zkvm_module_start(void);
extern int uBootstrap_main(int argc, char *argv[]);

/* Exit codes carrying the verdict out of the child. */
#define OK 0
#define BAD_ENTRY 21
#define BAD_ARGC 22
#define BAD_ARGV0 23
#define BAD_ARGV_TERM 24
#define BAD_STACK 25
#define BAD_GP 26

/* Current sp/gp. _start loads both from the linker symbols, so every
 * start-up stub is entered with them already pointing at the arrays
 * above; reading them back is how the test sees what _start did. */
static inline uintptr_t read_sp(void)
{
    uintptr_t v;
    __asm__ volatile("mv %0, sp" : "=r"(v));
    return v;
}

static inline uintptr_t read_gp(void)
{
    uintptr_t v;
    __asm__ volatile("mv %0, gp" : "=r"(v));
    return v;
}

/* sp must sit inside the array _init_stack_top points past the end of.
 * Accept anything within it: the callee's prologue may have pushed a
 * frame already. */
static int stack_ok(uintptr_t sp)
{
    return sp > (uintptr_t)zkvm_test_stack &&
           sp <= (uintptr_t)zkvm_test_stack + sizeof(zkvm_test_stack);
}

static int gp_ok(uintptr_t gp)
{
    return gp == (uintptr_t)zkvm_test_gp;
}

/* argv as the module builds it: exactly one entry, "app", NULL-terminated
 * for the runtime. */
static int argv_verdict(long argc, char **argv)
{
    if (argc != 1)
        return BAD_ARGC;
    if (argv == NULL || argv[0] == NULL || strcmp(argv[0], "app") != 0)
        return BAD_ARGV0;
    if (argv[1] != NULL)
        return BAD_ARGV_TERM;
    return OK;
}

/* uBootstrap_main is only referenced (its ADDRESS is passed on); it must
 * never actually run in this test. */
int uBootstrap_main(int argc, char *argv[])
{
    (void)argc;
    (void)argv;
    _exit(99);
}

/* Stand-in for musl's __libc_start_main (zisk, zisk_sim): validates the
 * handoff and exits. Signature per the riscv64 call in _start:
 *   a0 = main, a1 = argc, a2 = argv, a3..a5 = 0, a6 = stack end */
int test_libc_start_main(int (*main_fn)(int, char **), long argc,
                         char **argv, void (*init)(void), void (*fini)(void),
                         void (*rtld_fini)(void), void *stack_end)
{
    (void)init;
    (void)fini;
    (void)rtld_fini;

    if (main_fn != uBootstrap_main)
        _exit(BAD_ENTRY);
    if (argv_verdict(argc, argv) != OK)
        _exit(argv_verdict(argc, argv));
    if (!gp_ok(read_gp()))
        _exit(BAD_GP);

    /* sp was repointed at the guest stack before the call, and a6 carries
     * it through. */
    if (!stack_ok((uintptr_t)stack_end))
        _exit(BAD_STACK);

    _exit(OK);
}

/* Stand-in for noos_start_main (openvm): the noreturn half of the .NET
 * start-up. _start passes argc/argv and nothing else - sp and gp are read
 * back from the registers. */
void noos_start_main(int argc, char *argv[])
{
    int verdict = argv_verdict(argc, argv);

    if (verdict != OK)
        _exit(verdict);
    if (!gp_ok(read_gp()))
        _exit(BAD_GP);
    if (!stack_ok(read_sp()))
        _exit(BAD_STACK);

    _exit(OK);
}

/* Stand-in for sp1-zkvm's __start: SP1's runtime entry, called by _start
 * with no arguments. It is the allocator/hasher set-up that the guest
 * must not bypass; all the test can check is that _start handed it a
 * working sp/gp. The real one calls main(); the __wrap_main leg below
 * covers that half.
 *
 * Left UNDEFINED in the ZKVM_ENTRY_SP1_NOBIND build, which is the guest
 * built without --extlib: __start lives in the bindings library, so the
 * module's reference to it is weak and _start must fall back to
 * noos_start_main rather than fail the link. That fallback is what this
 * second binary exercises. */
#ifndef ZKVM_ENTRY_SP1_NOBIND
void __start(void)
{
    if (!gp_ok(read_gp()))
        _exit(BAD_GP);
    if (!stack_ok(read_sp()))
        _exit(BAD_STACK);

    _exit(OK);
}
#endif /* !ZKVM_ENTRY_SP1_NOBIND */

/* Stand-in for noos_main (sp1): the RETURNING half of the .NET start-up.
 * Called from the module's __wrap_main, in-process, so it records rather
 * than exits - the test then checks that __wrap_main passed the value
 * back, which is what SP1 halts with. */
#define NOOS_MAIN_RESULT 77
static volatile int noos_main_verdict = -1;

int noos_main(int argc, char *argv[])
{
    noos_main_verdict = argv_verdict(argc, argv);
    return NOOS_MAIN_RESULT;
}

/* The keccak permutation the managed side reaches through the ziskos name
 * syscall_keccak_f; each entry module tail-calls its target's spelling.
 * Both names are defined unconditionally - only the one the module under
 * test references is linked in. */
static void *keccak_state;

void zkvm_keccak_permute(void *state) /* sp1 */
{
    keccak_state = state;
}

void zkvm_keccakf(void *state) /* openvm */
{
    keccak_state = state;
}

#if defined(ZKVM_ENTRY_SP1) || defined(ZKVM_ENTRY_NOOS)
extern void syscall_keccak_f(void *state);
#endif
#ifdef ZKVM_ENTRY_SP1
extern int __wrap_main(void);
#endif

int main(void)
{
    /* One shot: _start clobbers sp/gp and never returns, so it only runs
     * in the child. Any verdict other than OK surfaces as its own code. */
    EXPECT_EXIT(OK, zkvm_module_start());

#ifdef ZKVM_ENTRY_SP1
    /* main() as SP1's __start calls it: no arguments, and the guest's
     * exit code comes back through the return value. */
    CHECK(__wrap_main() == NOOS_MAIN_RESULT);
    CHECK(noos_main_verdict == OK);
#endif

#if defined(ZKVM_ENTRY_SP1) || defined(ZKVM_ENTRY_NOOS)
    /* syscall_keccak_f is a tail call: the state pointer must arrive
     * untouched at the target's own name. */
    {
        uint64_t state[25] = {0};

        keccak_state = NULL;
        syscall_keccak_f(state);
        CHECK(keccak_state == (void *)state);
    }
#endif

    TEST_MAIN_END(ZKVM_MODULE_NAME);
}
