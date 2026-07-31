/**
 * @file
 * @brief Minimal unit-test harness for the native modules.
 *
 * Tests are cross-compiled for riscv64 and executed under qemu-user, so
 * even the raw-ecall paths run for real: the ZisK exit ecall (a7 == 93)
 * coincides with Linux riscv64 __NR_exit, so __wrap_exit(42) genuinely
 * terminates the (forked) child with status 42 under qemu.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#ifndef MODULE_TEST_COMMON_H
#define MODULE_TEST_COMMON_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/wait.h>

static int t_pass = 0;
static int t_fail = 0;

/* Progress marker for bisecting hangs: visible with TEST_VERBOSE=1. */
#define MARK(s)                                                         \
    do {                                                                \
        if (getenv("TEST_VERBOSE"))                                     \
            fprintf(stderr, "[%s]\n", s);                               \
    } while (0)

#define CHECK(cond)                                                     \
    do {                                                                \
        if (cond) {                                                     \
            t_pass++;                                                   \
        } else {                                                        \
            t_fail++;                                                   \
            fprintf(stderr, "FAIL %s:%d: %s\n",                         \
                    __FILE__, __LINE__, #cond);                         \
        }                                                               \
    } while (0)

#define CHECK_STR_EQ(got, want)                                         \
    do {                                                                \
        const char *g_ = (got), *w_ = (want);                           \
        if (g_ != NULL && strcmp(g_, w_) == 0) {                        \
            t_pass++;                                                   \
        } else {                                                        \
            t_fail++;                                                   \
            fprintf(stderr, "FAIL %s:%d: got \"%s\", want \"%s\"\n",    \
                    __FILE__, __LINE__, g_ ? g_ : "(null)", w_);        \
        }                                                               \
    } while (0)

/* Run `stmt` in a forked child; pass when the child exits with `code`.
 * The trailing _exit(111) catches "was supposed to never return". */
#define EXPECT_EXIT(code, stmt)                                         \
    do {                                                                \
        pid_t pid_ = fork();                                            \
        if (pid_ == 0) {                                                \
            stmt;                                                       \
            _exit(111);                                                 \
        }                                                               \
        int st_;                                                        \
        waitpid(pid_, &st_, 0);                                         \
        if (WIFEXITED(st_) && WEXITSTATUS(st_) == (code)) {             \
            t_pass++;                                                   \
        } else {                                                        \
            t_fail++;                                                   \
            fprintf(stderr,                                             \
                    "FAIL %s:%d: %s: expected exit %d, got %s %d\n",    \
                    __FILE__, __LINE__, #stmt, (int)(code),             \
                    WIFEXITED(st_) ? "exit" : "signal",                 \
                    WIFEXITED(st_) ? WEXITSTATUS(st_)                   \
                                   : WTERMSIG(st_));                    \
        }                                                               \
    } while (0)

/* Run `stmt` in a forked child; pass when the child dies from `sig`.
 * TEST_SKIP_FAULTS=1 skips these: qemu-user running under nested
 * emulation (Docker Rosetta on Apple silicon) hangs unkillably on guest
 * faults instead of delivering the signal. CI on real x86_64 runs them. */
#define EXPECT_SIGNAL(sig, stmt)                                        \
    do {                                                                \
        if (getenv("TEST_SKIP_FAULTS")) {                               \
            fprintf(stderr, "skip (TEST_SKIP_FAULTS): %s\n", #stmt);    \
            break;                                                      \
        }                                                               \
        pid_t pid_ = fork();                                            \
        if (pid_ == 0) {                                                \
            stmt;                                                       \
            _exit(111);                                                 \
        }                                                               \
        int st_;                                                        \
        waitpid(pid_, &st_, 0);                                         \
        if (WIFSIGNALED(st_) && WTERMSIG(st_) == (sig)) {               \
            t_pass++;                                                   \
        } else {                                                        \
            t_fail++;                                                   \
            fprintf(stderr,                                             \
                    "FAIL %s:%d: %s: expected signal %d, got %s %d\n",  \
                    __FILE__, __LINE__, #stmt, (int)(sig),              \
                    WIFEXITED(st_) ? "exit" : "signal",                 \
                    WIFEXITED(st_) ? WEXITSTATUS(st_)                   \
                                   : WTERMSIG(st_));                    \
        }                                                               \
    } while (0)

#define TEST_MAIN_END(name)                                             \
    do {                                                                \
        printf("%-18s %3d passed, %d failed\n", name, t_pass, t_fail);  \
        return t_fail ? 1 : 0;                                          \
    } while (0)

#endif /* MODULE_TEST_COMMON_H */
