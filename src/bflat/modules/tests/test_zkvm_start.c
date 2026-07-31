/**
 * @file
 * @brief Unit tests for the _start bootstrap in zkvm_zisk/module.S and
 *        zkvm_zisk_sim/module.S (run_tests.sh builds one binary per
 *        module; they differ only in the size of argv_vec).
 *
 * _start is the guest's very first instruction: it points gp/sp at the
 * linker-script symbols and hands __libc_start_main the managed entry
 * point plus a synthetic argc/argv ("app"). The linker symbols are
 * supplied here as real arrays via --defsym, and __libc_start_main /
 * uBootstrap_main are stubs that record what they were handed.
 *
 * _start never returns (it ecalls exit), so each case runs in a forked
 * child and the verdict travels back as the exit status.
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

/* uBootstrap_main is only referenced (its ADDRESS is passed on); it must
 * never actually run in this test. */
int uBootstrap_main(int argc, char *argv[])
{
    (void)argc;
    (void)argv;
    _exit(99);
}

/* Stand-in for musl's __libc_start_main: validates the handoff and exits.
 * Signature per the riscv64 call in _start:
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
    if (argc != 1)
        _exit(BAD_ARGC);
    if (argv == NULL || argv[0] == NULL || strcmp(argv[0], "app") != 0)
        _exit(BAD_ARGV0);
    if (argv[1] != NULL) /* argv must be NULL-terminated for the runtime */
        _exit(BAD_ARGV_TERM);

    /* sp was repointed at the guest stack before the call, and a6 carries
     * it through. Accept anything inside the array (the prologue may have
     * pushed a frame). */
    uintptr_t sp = (uintptr_t)stack_end;
    if (sp <= (uintptr_t)zkvm_test_stack ||
        sp > (uintptr_t)zkvm_test_stack + sizeof(zkvm_test_stack))
        _exit(BAD_STACK);

    _exit(OK);
}

int main(void)
{
    /* One shot: _start clobbers sp/gp and never returns, so it only runs
     * in the child. Any verdict other than OK surfaces as its own code. */
    EXPECT_EXIT(OK, zkvm_module_start());

    TEST_MAIN_END(ZKVM_MODULE_NAME);
}
