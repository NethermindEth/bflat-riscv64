/**
 * @file
 * @brief Unit tests for nofp/module.c: every libm wrap must terminate with
 *        status 255 (loud failure instead of silent garbage), and
 *        __wrap_asprintf must report plain failure without trapping.
 *
 * The stub list is generated from module.c by run_tests.sh (nofp_list.h),
 * so a stub added to the module without a test here is impossible. That also
 * makes the list the place to state what must NOT be there: the soft-float
 * builtins (__adddf3 and the other 51) are the soft-float implementation, not
 * a mistake to trap, so every entry has to be a libm --wrap= diversion.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include "common.h"

#define X(name) extern void name(void);
#include "nofp_list.h"
#undef X

extern int __wrap_asprintf(void);

typedef void (*stub_fn)(void);

static const struct {
    const char *name;
    stub_fn fn;
} stubs[] = {
#define X(name) { #name, name },
#include "nofp_list.h"
#undef X
};

int main(void)
{
    unsigned n = sizeof(stubs) / sizeof(stubs[0]);
    CHECK(n >= 50); /* the generated list really covers the module */

    /*
     * Nothing but libm may trap. A soft-float builtin stubbed here would kill
     * every guest that touches a double at its first operation, and it would
     * do so silently as far as this test is concerned - the stub would pass
     * the exit-255 check below like any other.
     */
    for (unsigned i = 0; i < n; i++)
        CHECK(strncmp(stubs[i].name, "__wrap_", 7) == 0);

    for (unsigned i = 0; i < n; i++) {
        pid_t pid = fork();
        if (pid == 0) {
            stubs[i].fn();
            _exit(111); /* must never return */
        }
        int st;
        waitpid(pid, &st, 0);
        if (WIFEXITED(st) && WEXITSTATUS(st) == 255) {
            t_pass++;
        } else {
            t_fail++;
            fprintf(stderr, "FAIL: %s: expected exit 255, got %s %d\n",
                    stubs[i].name,
                    WIFEXITED(st) ? "exit" : "signal",
                    WIFEXITED(st) ? WEXITSTATUS(st) : WTERMSIG(st));
        }
    }

    /* asprintf is deliberately NOT a trap: plain failure. */
    CHECK(__wrap_asprintf() == -1);

    TEST_MAIN_END("nofp");
}
