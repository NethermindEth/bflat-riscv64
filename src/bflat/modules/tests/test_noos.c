/**
 * @file
 * @brief Unit tests for noos/module.c.
 *
 * The module defines plain libc names - strlen, fork, fprintf - because its
 * job is to displace musl's. Linking that straight into a test would displace
 * the harness's libc too, and noos's fprintf is a trap, so the first failing
 * CHECK would terminate the process instead of reporting. run_tests.sh
 * therefore renames every symbol the module DEFINES to noos_t_*, leaving its
 * undefined references (exit, zkvm_console_write) alone, and this file calls
 * the renamed copies.
 *
 * Two halves: the answers the runtime's start-up path needs (RhInitialize
 * reaches sigemptyset, statfs, mprotect and dladdr before Main), and the
 * string and integer-parsing code the module has to provide for real because
 * half a musl member cannot be displaced. The traps are checked in a forked
 * child - status 253, and the name of the call on the console, which is the
 * only thing that tells a guest's author WHICH of ~150 calls it hit.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <limits.h>
#include <sys/mman.h>

#include "common.h"

typedef unsigned long noos_size_t;

/* Start-up answers. */
extern int   noos_t_sigemptyset(void *set);
extern int   noos_t_sigaddset(void *set, int sig);
extern int   noos_t___libc_current_sigrtmin(void);
extern int   noos_t_statfs(const char *path, void *buf);
extern int   noos_t_mprotect(void *addr, unsigned long len, int prot);
extern int   noos_t_dladdr(const void *addr, void *info);
extern int   noos_t___sched_cpucount(unsigned long size, const void *set);
extern void *noos_t_fopen(const char *path, const char *mode);

/* Strings. */
extern noos_size_t noos_t_strlen(const char *s);
extern int         noos_t_strcmp(const char *a, const char *b);
extern int         noos_t_strncmp(const char *a, const char *b, noos_size_t n);
extern int         noos_t_strcasecmp(const char *a, const char *b);
extern char       *noos_t_strcpy(char *dst, const char *src);
extern char       *noos_t_strncpy(char *dst, const char *src, noos_size_t n);
extern char       *noos_t_strcat(char *dst, const char *src);
extern char       *noos_t_strncat(char *dst, const char *src, noos_size_t n);
extern char       *noos_t_strchr(const char *s, int c);
extern char       *noos_t_strrchr(const char *s, int c);
extern char       *noos_t_strstr(const char *haystack, const char *needle);
extern char       *noos_t_strtok_r(char *s, const char *sep, char **save);
extern int         noos_t_bcmp(const void *a, const void *b, noos_size_t n);
extern int         noos_t_atoi(const char *s);

/* Integer parsing. */
extern long               noos_t_strtol(const char *s, char **end, int base);
extern long long          noos_t_strtoll(const char *s, char **end, int base);
extern unsigned long      noos_t_strtoul(const char *s, char **end, int base);
extern unsigned long long noos_t_strtoull(const char *s, char **end, int base);
extern long noos_t___strtol_internal(const char *s, char **end, int base,
                                     int group);
extern unsigned long noos_t___strtoul_internal(const char *s, char **end,
                                               int base, int group);

/* Traps, a sample across the families the module covers. */
extern long noos_t_fork(void *a, void *b, void *c, void *d, void *e, void *f);
extern long noos_t_dlopen(void *a, void *b, void *c, void *d, void *e, void *f);
extern long noos_t_fcntl(void *a, void *b, void *c, void *d, void *e, void *f);
extern long noos_t_stat(void *a, void *b, void *c, void *d, void *e, void *f);
extern long noos_t_uname(void *a, void *b, void *c, void *d, void *e, void *f);
extern int  noos_t_fprintf(void *stream, const char *fmt, ...);
extern char *noos_t_strerror(int errnum);

/*
 * The trap writes the call's name through this hook, which the module
 * declares weak and the zkVM targets define. Capturing it in shared memory
 * is what lets the parent read what the child printed before it died.
 */
#define CONSOLE_CAP 256
static struct console_capture {
    int  len;
    char buf[CONSOLE_CAP];
} *console;

int zkvm_console_write(int fd, const char *buf, int len)
{
    (void)fd;
    for (int i = 0; i < len && console->len < CONSOLE_CAP - 1; i++)
        console->buf[console->len++] = buf[i];
    console->buf[console->len] = '\0';
    return len;
}

/* The start-up chain noos_main drives: TLS first (a static read through tp
 * would otherwise be a read through zero), then the init array, then the
 * managed entry point. Both live in other modules; the test supplies them and
 * records the order in shared memory, because noos_main runs in a child. */
extern int  noos_t_noos_main(int argc, char *argv[]);
extern void noos_t_noos_start_main(int argc, char *argv[]);

struct startup_trace {
    int tls_inited;
    int main_argc;
    int main_saw_tls;   /* was TLS already up when the entry point ran? */
};
static struct startup_trace *startup;

void __wrap___init_tls(void *p)
{
    (void)p;
    startup->tls_inited = 1;
}

int uBootstrap_main(int argc, char *argv[])
{
    (void)argv;
    startup->main_argc   = argc;
    startup->main_saw_tls = startup->tls_inited;
    return 7;
}

typedef long (*trap_fn)(void *, void *, void *, void *, void *, void *);

/* Runs one trap in a child: it must terminate with 253 and name itself. */
static void check_trap(const char *name, trap_fn fn)
{
    console->len   = 0;
    console->buf[0] = '\0';

    pid_t pid = fork();
    if (pid == 0) {
        fn(NULL, NULL, NULL, NULL, NULL, NULL);
        _exit(111); /* must never return */
    }

    int st = 0;
    waitpid(pid, &st, 0);

    if (WIFEXITED(st) && WEXITSTATUS(st) == 253) {
        t_pass++;
    } else {
        t_fail++;
        fprintf(stderr, "FAIL: %s: expected exit 253, got %s %d\n", name,
                WIFEXITED(st) ? "exit" : "signal",
                WIFEXITED(st) ? WEXITSTATUS(st) : WTERMSIG(st));
    }

    /* "noos: unsupported OS call: <name>" - without the name the guest's
     * author is left bisecting ~150 calls, and on OpenVM the status itself
     * arrives as a bare 1. */
    char want[CONSOLE_CAP];
    snprintf(want, sizeof(want), "noos: unsupported OS call: %s\n", name);
    if (strcmp(console->buf, want) == 0) {
        t_pass++;
    } else {
        t_fail++;
        fprintf(stderr, "FAIL: %s: console said '%s'\n", name, console->buf);
    }
}

int main(void)
{
    console = mmap(NULL, sizeof(*console), PROT_READ | PROT_WRITE,
                   MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    startup = mmap(NULL, sizeof(*startup), PROT_READ | PROT_WRITE,
                   MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    if (console == MAP_FAILED || startup == MAP_FAILED) {
        fprintf(stderr, "mmap failed\n");
        return 1;
    }

    /* --- the start-up path: every one of these is reached before Main --- */
    {
        unsigned char set[128];
        memset(set, 0xEE, sizeof(set));
        CHECK(noos_t_sigemptyset(set) == 0);
        int cleared = 1;
        for (unsigned i = 0; i < sizeof(set); i++)
            cleared &= (set[i] == 0);
        CHECK(cleared);

        /* musl's sigset_t is a bit array, bit (sig-1) first, so SIGHUP (1)
         * is bit 0 of word 0 and SIGRTMIN+3 (38) is bit 5 of word 0.. */
        unsigned long *bits = (unsigned long *)set;
        CHECK(noos_t_sigaddset(set, 1) == 0);
        CHECK(bits[0] == 1UL);
        CHECK(noos_t_sigaddset(set, 64) == 0); /* last bit of word 0 */
        CHECK(bits[0] == (1UL | (1UL << 63)));
        CHECK(noos_t_sigaddset(set, 65) == 0); /* first bit of word 1 */
        CHECK(bits[1] == 1UL);
        CHECK(bits[0] == (1UL | (1UL << 63))); /* earlier word untouched */
    }

    CHECK(noos_t___libc_current_sigrtmin() == 35); /* musl: SIGRTMIN + 1 */
    CHECK(noos_t_statfs("/sys/fs/cgroup", NULL) == -1); /* no filesystem */
    CHECK(noos_t_mprotect((void *)0x1000, 4096, 7) == 0); /* no protection */
    CHECK(noos_t_dladdr((void *)&main, NULL) == 0);       /* no loader */
    CHECK(noos_t_fopen("/proc/self/maps", "r") == NULL);  /* probed, absent */

    /* __sched_cpucount: the real popcount, over the mask pal hands back. */
    {
        unsigned char mask[16];
        memset(mask, 0, sizeof(mask));
        CHECK(noos_t___sched_cpucount(sizeof(mask), mask) == 0);
        mask[0] = 0x01; /* pal's affinity answer: exactly one CPU */
        CHECK(noos_t___sched_cpucount(sizeof(mask), mask) == 1);
        memset(mask, 0xFF, sizeof(mask));
        CHECK(noos_t___sched_cpucount(sizeof(mask), mask) == 128);
        mask[3] = 0x0F;
        CHECK(noos_t___sched_cpucount(4, mask) == 8 + 8 + 8 + 4);
        CHECK(noos_t___sched_cpucount(0, mask) == 0);
    }

    /* --- strings: on live paths, so they are implemented, not stubbed --- */
    CHECK(noos_t_strlen("") == 0);
    CHECK(noos_t_strlen("hello") == 5);
    CHECK(noos_t_strlen("a\0b") == 1);

    CHECK(noos_t_strcmp("", "") == 0);
    CHECK(noos_t_strcmp("abc", "abc") == 0);
    CHECK(noos_t_strcmp("abc", "abd") < 0);
    CHECK(noos_t_strcmp("abd", "abc") > 0);
    CHECK(noos_t_strcmp("abc", "ab") > 0);
    /* high bytes compare as unsigned: "\x80" is above "\x7f", not below */
    CHECK(noos_t_strcmp("\x80", "\x7f") > 0);

    CHECK(noos_t_strncmp("abcdef", "abcxyz", 3) == 0);
    CHECK(noos_t_strncmp("abcdef", "abcxyz", 4) < 0);
    CHECK(noos_t_strncmp("abc", "abc", 100) == 0);
    CHECK(noos_t_strncmp("x", "y", 0) == 0);

    CHECK(noos_t_strcasecmp("Runtime", "runtime") == 0);
    CHECK(noos_t_strcasecmp("RUNTIME", "runtime") == 0);
    CHECK(noos_t_strcasecmp("runtime", "runtimes") < 0);
    CHECK(noos_t_strcasecmp("b", "A") > 0);

    {
        char buf[16];
        memset(buf, 0xEE, sizeof(buf));
        CHECK(noos_t_strcpy(buf, "abc") == buf);
        CHECK(strcmp(buf, "abc") == 0);
        CHECK((unsigned char)buf[4] == 0xEE); /* nothing beyond the NUL */

        /* strncpy pads to n with NULs and does NOT terminate when it fills */
        memset(buf, 0xEE, sizeof(buf));
        CHECK(noos_t_strncpy(buf, "ab", 6) == buf);
        CHECK(buf[0] == 'a' && buf[1] == 'b');
        CHECK(buf[2] == 0 && buf[3] == 0 && buf[4] == 0 && buf[5] == 0);
        CHECK((unsigned char)buf[6] == 0xEE);
        memset(buf, 0xEE, sizeof(buf));
        CHECK(noos_t_strncpy(buf, "abcdef", 3) == buf);
        CHECK(buf[0] == 'a' && buf[1] == 'b' && buf[2] == 'c');
        CHECK((unsigned char)buf[3] == 0xEE); /* unterminated, by design */

        strcpy(buf, "ab");
        CHECK(noos_t_strcat(buf, "cd") == buf);
        CHECK(strcmp(buf, "abcd") == 0);
        CHECK(noos_t_strcat(buf, "") == buf);
        CHECK(strcmp(buf, "abcd") == 0);

        strcpy(buf, "ab");
        CHECK(noos_t_strncat(buf, "cdef", 2) == buf);
        CHECK(strcmp(buf, "abcd") == 0); /* terminates after n */
        CHECK(noos_t_strncat(buf, "xy", 0) == buf);
        CHECK(strcmp(buf, "abcd") == 0);
    }

    {
        const char *s = "a/b/c";
        CHECK(noos_t_strchr(s, 'a') == s);
        CHECK(noos_t_strchr(s, '/') == s + 1);
        CHECK(noos_t_strrchr(s, '/') == s + 3);
        CHECK(noos_t_strchr(s, 'z') == NULL);
        CHECK(noos_t_strrchr(s, 'z') == NULL);
        /* the NUL is part of the string for both, per C */
        CHECK(noos_t_strchr(s, '\0') == s + 5);
        CHECK(noos_t_strrchr(s, '\0') == s + 5);
        /* the char is taken as a char: 'a' + 256 still matches 'a' */
        CHECK(noos_t_strchr(s, 'a' + 256) == s);
    }

    {
        const char *h = "DOTNET_gcServer";
        CHECK(noos_t_strstr(h, "") == h);       /* empty needle: the start */
        CHECK(noos_t_strstr(h, "DOT") == h);
        CHECK(noos_t_strstr(h, "gc") == h + 7);
        CHECK(noos_t_strstr(h, "gcs") == NULL); /* case matters */
        CHECK(noos_t_strstr(h, "Server") == h + 9);
        CHECK(noos_t_strstr("", "x") == NULL);
        /* a partial match must not stop the scan */
        CHECK(noos_t_strstr("aab", "ab") != NULL);
    }

    {
        char  s[] = "  one,two,,three  ";
        char *save = NULL;
        CHECK_STR_EQ(noos_t_strtok_r(s, " ,", &save), "one");
        CHECK_STR_EQ(noos_t_strtok_r(NULL, " ,", &save), "two");
        CHECK_STR_EQ(noos_t_strtok_r(NULL, " ,", &save), "three");
        CHECK(noos_t_strtok_r(NULL, " ,", &save) == NULL);
        CHECK(noos_t_strtok_r(NULL, " ,", &save) == NULL); /* stays done */

        char only_seps[] = ",,,";
        save = NULL;
        CHECK(noos_t_strtok_r(only_seps, ",", &save) == NULL);

        char empty[] = "";
        save = NULL;
        CHECK(noos_t_strtok_r(empty, ",", &save) == NULL);
    }

    CHECK(noos_t_bcmp("abc", "abc", 3) == 0);
    CHECK(noos_t_bcmp("abc", "abd", 3) != 0);
    CHECK(noos_t_bcmp("abc", "abd", 2) == 0);
    CHECK(noos_t_bcmp("", "", 0) == 0);

    CHECK(noos_t_atoi("0") == 0);
    CHECK(noos_t_atoi("42") == 42);
    CHECK(noos_t_atoi("-42") == -42);
    CHECK(noos_t_atoi("+7") == 7);
    CHECK(noos_t_atoi("  12abc") == 12); /* leading space, trailing junk */
    CHECK(noos_t_atoi("abc") == 0);

    /* --- integer parsing: RhConfig reads the runtime's knobs through it --- */
    {
        char *end;

        CHECK(noos_t_strtol("123", &end, 10) == 123);
        CHECK(*end == '\0');
        CHECK(noos_t_strtol("-123", &end, 10) == -123);
        CHECK(noos_t_strtol("  \t\n+45x", &end, 10) == 45);
        CHECK(*end == 'x');

        /* base 16, with and without the prefix; base 0 sniffs it */
        CHECK(noos_t_strtol("ff", &end, 16) == 255);
        CHECK(noos_t_strtol("0xFF", &end, 16) == 255);
        CHECK(noos_t_strtol("0xff", &end, 0) == 255);
        CHECK(noos_t_strtol("0X10", &end, 0) == 16);
        CHECK(noos_t_strtol("010", &end, 0) == 8);   /* octal by leading 0 */
        CHECK(noos_t_strtol("010", &end, 10) == 10);
        CHECK(noos_t_strtol("zz", &end, 36) == 35 * 36 + 35);

        /* a digit outside the base ends the number rather than consuming it */
        CHECK(noos_t_strtol("129", &end, 8) == 0xA);
        CHECK(*end == '9');
        CHECK(noos_t_strtol("abc", &end, 10) == 0);
        CHECK(end[0] == 'a'); /* nothing consumed */

        /* widths */
        CHECK(noos_t_strtoll("9007199254740993", &end, 10)
              == 9007199254740993LL);
        CHECK(noos_t_strtoul("4294967296", &end, 10) == 4294967296UL);
        CHECK(noos_t_strtoull("18446744073709551615", &end, 10) == ULLONG_MAX);
        /* negation is modular, as the module documents: no errno, no clamp */
        CHECK(noos_t_strtoul("-1", &end, 10) == ULONG_MAX);

        /* the __*_internal names musl's own callers reach, group flag and
         * all, must answer exactly like the public ones */
        CHECK(noos_t___strtol_internal("-77", &end, 10, 0) == -77);
        CHECK(noos_t___strtol_internal("-77", &end, 10, 1) == -77);
        CHECK(noos_t___strtoul_internal("0x20", &end, 0, 0) == 32);

        /* end may be NULL */
        CHECK(noos_t_strtol("5", NULL, 10) == 5);
    }

    /* --- traps: status 253 and the name, on a sample of the families --- */
    check_trap("fork", noos_t_fork);      /* processes */
    check_trap("dlopen", noos_t_dlopen);  /* dynamic loading */
    check_trap("fcntl", noos_t_fcntl);    /* descriptors */
    check_trap("stat", noos_t_stat);      /* filesystem */
    check_trap("uname", noos_t_uname);    /* system information */

    /* fprintf and strerror take their own shapes but share the policy */
    {
        pid_t pid = fork();
        if (pid == 0) {
            noos_t_fprintf(NULL, "%d", 1);
            _exit(111);
        }
        int st = 0;
        waitpid(pid, &st, 0);
        CHECK(WIFEXITED(st) && WEXITSTATUS(st) == 253);

        pid = fork();
        if (pid == 0) {
            noos_t_strerror(2);
            _exit(111);
        }
        waitpid(pid, &st, 0);
        CHECK(WIFEXITED(st) && WEXITSTATUS(st) == 253);
    }

    /* --- start-up: TLS before the entry point, and the status it returns ---
     * Run in a child: noos_main walks the init array, and re-running this
     * process's constructors is not something to do to the test itself. */
    {
        char *argv[] = { (char *)"guest", NULL };

        memset(startup, 0, sizeof(*startup));
        pid_t pid = fork();
        if (pid == 0)
            _exit(noos_t_noos_main(1, argv) & 0x7F);
        int st = 0;
        waitpid(pid, &st, 0);
        CHECK(WIFEXITED(st) && WEXITSTATUS(st) == 7); /* uBootstrap's value */
        CHECK(startup->main_argc == 1);
        CHECK(startup->tls_inited);
        CHECK(startup->main_saw_tls); /* TLS came first, not after */

        /* noos_start_main is the same chain, exiting rather than returning:
         * it is what the openvm entry jumps to and there is nowhere to
         * return to. */
        memset(startup, 0, sizeof(*startup));
        pid = fork();
        if (pid == 0) {
            noos_t_noos_start_main(1, argv);
            _exit(111); /* must never return */
        }
        waitpid(pid, &st, 0);
        CHECK(WIFEXITED(st) && WEXITSTATUS(st) == 7);
    }

    TEST_MAIN_END("noos");
}
