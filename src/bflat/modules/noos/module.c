/**
 * @file
 * @brief Cuts the OS surface the .NET runtime's PAL reaches for.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */

/*
 * A guest links one object it never asked for: the runtime's Unix PAL. The
 * chain is short and unavoidable -
 *
 *   Program.o -> RhpEHEnumInitFromStackFrameIterator -> EHHelpers.cpp.o
 *             -> RhFailFast()                        -> PalUnix.cpp.o
 *
 * - and PalUnix comes in whole, so every libc call anywhere inside it goes
 * live, together with libSystem.Native's pal_io/pal_threading. Between them
 * they reach 150 libc entry points and drag 96 members of musl's libc.a into
 * the image: crash dumps (fork, execv, pipe, waitpid), dynamic loading
 * (dlopen, dladdr), CPU counting (fopen -> fscanf -> vfscanf -> __intscan),
 * cgroup probing, directory walking, signals. A zkVM guest can service none
 * of it: one hart, no processes, no filesystem, no dynamic loader.
 *
 * Displacing them is worth doing on its own - it is dead weight in a proof -
 * and it became load-bearing when the p4 blob set started shipping musl built
 * with the C extension, which none of these VMs decode.
 *
 * TWO BEHAVIOURS, and the split is the design:
 *
 *   - Operations that cannot be honoured here terminate with status 253,
 *     the way nofp (255) and nothread (254) do. Returning a plausible-looking
 *     failure instead would let the guest carry on with a wrong answer, which
 *     for a proving system is the worst outcome; a distinct exit code says
 *     what actually happened.
 *   - The string helpers the same objects call ARE serviced, because they are
 *     on live paths. They are ordinary implementations, small and cold.
 *
 * HOW IT TAKES EFFECT: by definition, not by --wrap. A wrap redirects the
 * call but leaves musl's member - and its instructions - in the image, which
 * is exactly what the PAL's existing wraps in modules/pal do. The linker
 * extracts a member only for a symbol still undefined when it reaches the
 * archive, so this object is linked ahead of libc.a and the members never
 * come in at all. Where pal already wraps a name, both live happily: the call
 * goes to pal's __wrap_, and the definition here is what keeps musl out.
 */
extern void exit(int status) __attribute__((noreturn));

/* 255 is nofp (floating point reached), 254 is nothread (tried to block),
 * 253 is this one (asked the operating system for something). */
#define NOOS_EXIT_STATUS 253

/* pal's guest console, when the target has one. Weak: noos.o is linked and
 * unit tested without pal, and ZisK's console is a no-op device. */
extern int zkvm_console_write(int fd, const char *buf, int len) __attribute__((weak));

/*@ // Single abort policy for every unsupported operation: name the call,
    // then terminate with status 253, never return. exit() resolves to the
    // PAL's __wrap_exit - the target's real termination sequence - in the
    // zkVM link.
    assigns \nothing;
    ensures \false;
    exits \exit_status == NOOS_EXIT_STATUS;
*/
__attribute__((noreturn, noinline, cold))
static void
noos_trap(const char *what)
{
    /* Worth the handful of instructions on a cold path: without it the guest
     * dies with a bare 253 and finding which of ~150 calls it was means
     * bisecting the program. OpenVM makes that worse - its TERMINATE takes an
     * immediate, so every non-zero status arrives at the host as 1. */
    if (zkvm_console_write && what != 0)
    {
        int len = 0;

        while (what[len] != '\0')
            len++;

        zkvm_console_write(2, "noos: unsupported OS call: ", 27);
        zkvm_console_write(2, what, len);
        zkvm_console_write(2, "\n", 1);
    }

    exit(NOOS_EXIT_STATUS);
}

/*
 * The ACSL contract rides inside the macro so every expansion carries one.
 * NOTE: Frama-C only sees annotations inside macro bodies with
 * comment-preserving preprocessing (-cpp-extra-args="-CC").
 *
 * Six ignored pointer parameters: the RISC-V ABI passes the first eight
 * integer arguments in a0..a7, so a definition with more parameters than the
 * caller passes reads registers the caller did not set - harmless, because no
 * body below looks at them. One macro per behaviour beats one per arity.
 */
#define NOOS_TRAP(name) \
    /*@ assigns \nothing; ensures \false; \
        exits \exit_status == NOOS_EXIT_STATUS; */ \
    long name(void *a, void *b, void *c, void *d, void *e, void *f) \
    { (void)a; (void)b; (void)c; (void)d; (void)e; (void)f; noos_trap(#name); }

/* --- processes (PalCreateDump: crash dumps) ------------------------------ */
NOOS_TRAP(fork)
NOOS_TRAP(execv)
NOOS_TRAP(waitpid)
NOOS_TRAP(pipe)
NOOS_TRAP(pipe2)
NOOS_TRAP(dup2)
NOOS_TRAP(prctl)

/* --- dynamic loading (PalUnix) ------------------------------------------- */
NOOS_TRAP(dlopen)
NOOS_TRAP(dlsym)
NOOS_TRAP(dl_iterate_phdr)

/* --- signals (PalUnix, UnixSignals) -------------------------------------- */
NOOS_TRAP(pthread_kill)

/* --- filesystem (libSystem.Native pal_io) -------------------------------- */
NOOS_TRAP(access)
NOOS_TRAP(chdir)
NOOS_TRAP(chmod)
NOOS_TRAP(fchmod)
NOOS_TRAP(opendir)
NOOS_TRAP(closedir)
NOOS_TRAP(readdir)
NOOS_TRAP(fallocate)
NOOS_TRAP(flock)
NOOS_TRAP(fcntl)
NOOS_TRAP(fstat)
NOOS_TRAP(fsync)
NOOS_TRAP(ftruncate)
NOOS_TRAP(futimens)
NOOS_TRAP(link)
NOOS_TRAP(lstat)
NOOS_TRAP(mkdir)
NOOS_TRAP(mkdtemp)
NOOS_TRAP(mkfifo)
NOOS_TRAP(mknod)
NOOS_TRAP(mkstemps)
NOOS_TRAP(readlink)
NOOS_TRAP(realpath)
NOOS_TRAP(rename)
NOOS_TRAP(rmdir)
NOOS_TRAP(stat)
NOOS_TRAP(symlink)
NOOS_TRAP(sync)
NOOS_TRAP(unlink)
NOOS_TRAP(sendfile)
NOOS_TRAP(ioctl)
NOOS_TRAP(poll)
NOOS_TRAP(posix_fadvise)
NOOS_TRAP(madvise)
NOOS_TRAP(msync)
NOOS_TRAP(getsockopt)
NOOS_TRAP(uname)

/* --- buffered stdio (PalCreateDump, cpucount, gcenv) --------------------- */
NOOS_TRAP(fclose)
NOOS_TRAP(fputs)
NOOS_TRAP(getline)

/* NOT defined here, though the runtime calls them: sched_setaffinity shares
 * affinity.lo with sched_getaffinity, and inotify_init1 shares inotify.lo
 * with inotify_add_watch. Both members are pulled in for the sibling anyway,
 * so defining one half only collides with the copy already linked. Same rule
 * as in nothread: displace a whole member or leave it alone - which is why
 * the integer-parsing family below is written out in full. */

/* --- host queries nobody can answer here --------------------------------- */
NOOS_TRAP(getrlimit)
NOOS_TRAP(getrusage)
NOOS_TRAP(utimensat)
NOOS_TRAP(clock_nanosleep)
NOOS_TRAP(nanosleep)
NOOS_TRAP(gettimeofday)
NOOS_TRAP(lrand48)
NOOS_TRAP(srand48)
NOOS_TRAP(__riscv_flush_icache)
NOOS_TRAP(pthread_setname_np)
NOOS_TRAP(pthread_getattr_np)
NOOS_TRAP(pthread_attr_getstack)
NOOS_TRAP(gai_strerror)
NOOS_TRAP(strerror_r)
NOOS_TRAP(asprintf)
/* Clang recognises the FILE* trio as builtins and asks for <stdio.h>, which a
 * freestanding module has no business including - the parameter is never
 * dereferenced, only passed. Silenced narrowly rather than globally. */
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wbuiltin-requires-header"

/* Clang knows these five as library functions and warns unless the
 * declaration matches, so they are spelled out rather than macro-expanded.
 * The behaviour is identical - the trap never returns. */
/* fopen is serviced, not trapped: the runtime probes files it can live
 * without (minipal_get_cpu_max_possible_count reads /sys/devices/system/cpu,
 * the GC reads cgroup limits) and treats NULL as "not there". On ZisK the
 * probe fails the same way, through musl's open() and pal's __wrap_open. */
/*@ assigns \nothing; ensures \result == \null; */
void *fopen(const char *path, const char *mode)
{ (void)path; (void)mode; return (void *)0; }

/*@ assigns \nothing; ensures \false; exits \exit_status == NOOS_EXIT_STATUS; */
int fprintf(void *stream, const char *fmt, ...)
{ (void)stream; (void)fmt; noos_trap(__func__); }

/*@ assigns \nothing; ensures \false; exits \exit_status == NOOS_EXIT_STATUS; */
int fscanf(void *stream, const char *fmt, ...)
{ (void)stream; (void)fmt; noos_trap(__func__); }

/*@ assigns \nothing; ensures \false; exits \exit_status == NOOS_EXIT_STATUS; */
char *strerror(int errnum)
{ (void)errnum; noos_trap(__func__); }

#pragma clang diagnostic pop

/* ========================================================================
 * Serviced, not trapped: startup paths the runtime cannot skip.
 *
 * RhInitialize reaches every one of these before Main() - PalInit installs
 * signal handlers (sigemptyset/sigaddset), GCToOSInterface::Initialize
 * probes the CPU count, cgroups (statfs) and NUMA, PalVirtualProtect calls
 * mprotect for the GS cookie page, and the crash-info path asks dladdr.
 * Trapping any of them kills every guest at startup with 253. On ZisK the
 * same calls succeed through musl + pal (or fail softly), so the answers
 * below are the ones the runtime already copes with there: signal sets are
 * plain memory, there is one CPU, no filesystem, no protection and no
 * dynamic loader.
 * ======================================================================== */

/* musl's sigset_t is 128 bytes (1024 bits); the runtime only ever passes
 * its own sigset_t objects, so clearing the whole thing is exact. */
/*@ requires \valid((char *)set + (0 .. 127)); assigns ((char *)set)[0 .. 127];
    ensures \result == 0; */
int
sigemptyset(void *set)
{
    char *p = (char *)set;
    for (int i = 0; i < 128; i++)
        p[i] = 0;
    return 0;
}

/* musl's sigset_t is a bit array, one bit per signal, bit (sig-1) first.
 * Nothing in the guest ever reads the set back - pal's __wrap_sigaction
 * discards it - but a stub that quietly dropped the signal would be a lie
 * the next reader of this file would have to re-derive, and the honest
 * version is four lines. */
/*@ requires 1 <= sig <= 1024;
    requires \valid((unsigned long *)set + ((sig - 1) / 64));
    assigns ((unsigned long *)set)[(sig - 1) / 64];
    ensures \result == 0; */
int
sigaddset(void *set, int sig)
{
    unsigned long *bits = (unsigned long *)set;
    unsigned       n    = (unsigned)(sig - 1);

    bits[n / (8u * sizeof(unsigned long))] |= 1UL << (n % (8u * sizeof(unsigned long)));
    return 0;
}

/* musl: SIGRTMIN + 1 (NPTL reserves 32/33; musl reserves 32..34). */
/*@ assigns \nothing; ensures \result == 35; */
int
__libc_current_sigrtmin(void)
{
    return 35;
}

/* No filesystem to query: fail, the GC then falls back to no cgroup limit. */
/*@ assigns \nothing; ensures \result == -1; */
int
statfs(const char *path, void *buf)
{
    (void)path;
    (void)buf;
    return -1;
}

/* Memory has no protection on a zkVM, so every mprotect trivially holds. */
/*@ assigns \nothing; ensures \result == 0; */
int
mprotect(void *addr, unsigned long len, int prot)
{
    (void)addr;
    (void)len;
    (void)prot;
    return 0;
}

/* No dynamic loader: nothing can be resolved, which is what 0 means here. */
/*@ assigns \nothing; ensures \result == 0; */
int
dladdr(const void *addr, void *info)
{
    (void)addr;
    (void)info;
    return 0;
}

/* Real implementation of musl's helper: the number of CPUs in an affinity
 * mask (pal's __wrap_sched_getaffinity reports exactly one). */
/*@ requires \valid_read((const unsigned char *)set + (0 .. size - 1));
    assigns \nothing; ensures \result >= 0; */
int
__sched_cpucount(unsigned long size, const void *set)
{
    const unsigned char *p = (const unsigned char *)set;
    int n = 0;
    for (unsigned long i = 0; i < size; i++)
        for (unsigned char b = p[i]; b != 0; b &= (unsigned char)(b - 1))
            n++;
    return n;
}

/* ========================================================================
 * Serviced, not trapped.
 *
 * These are on live paths - the runtime compares, copies and parses strings
 * while it is doing perfectly ordinary work - so displacing musl's copies
 * means providing working ones. They are small and cold; the hot memory
 * primitives (memcpy, memset, memmove, memcmp) are deliberately NOT here,
 * because musl's are tuned and a byte loop would cost real cycles in a proof.
 * ======================================================================== */

typedef unsigned long noos_size_t;

/*@ requires \valid_read(s + (0 .. )); assigns \nothing; */
noos_size_t
strlen(const char *s)
{
    const char *p = s;

    while (*p != '\0')
        p++;

    return (noos_size_t)(p - s);
}

/*@ assigns \nothing; ensures \result == 0 || \result < 0 || \result > 0; */
int
strcmp(const char *a, const char *b)
{
    while (*a != '\0' && *a == *b) {
        a++;
        b++;
    }

    return (int)(unsigned char)*a - (int)(unsigned char)*b;
}

/*@ assigns \nothing; */
int
strncmp(const char *a, const char *b, noos_size_t n)
{
    if (n == 0)
        return 0;

    while (--n != 0 && *a != '\0' && *a == *b) {
        a++;
        b++;
    }

    return (int)(unsigned char)*a - (int)(unsigned char)*b;
}

/* Only ASCII matters here: the runtime uses it for environment and config
 * names, and the guest runs with globalization switched off. */
/*@ assigns \nothing; */
int
strcasecmp(const char *a, const char *b)
{
    /*@ assigns \nothing; */
    int ca, cb;

    do {
        ca = (unsigned char)*a++;
        cb = (unsigned char)*b++;
        if (ca >= 'A' && ca <= 'Z')
            ca += 'a' - 'A';
        if (cb >= 'A' && cb <= 'Z')
            cb += 'a' - 'A';
    } while (ca != 0 && ca == cb);

    return ca - cb;
}

/*@ assigns dst[0 .. ]; ensures \result == dst; */
char *
strcpy(char *dst, const char *src)
{
    char *out = dst;

    while ((*out++ = *src++) != '\0')
        ;

    return dst;
}

/* Pads to n with NULs, as the standard requires and callers rely on. */
/*@ assigns dst[0 .. n - 1]; ensures \result == dst; */
char *
strncpy(char *dst, const char *src, noos_size_t n)
{
    noos_size_t i = 0;

    while (i < n && src[i] != '\0') {
        dst[i] = src[i];
        i++;
    }
    while (i < n)
        dst[i++] = '\0';

    return dst;
}

/*@ assigns dst[0 .. ]; ensures \result == dst; */
char *
strcat(char *dst, const char *src)
{
    char *out = dst;

    while (*out != '\0')
        out++;
    while ((*out++ = *src++) != '\0')
        ;

    return dst;
}

/*@ assigns dst[0 .. ]; ensures \result == dst; */
char *
strncat(char *dst, const char *src, noos_size_t n)
{
    char *out = dst;

    while (*out != '\0')
        out++;
    while (n-- != 0 && *src != '\0')
        *out++ = *src++;
    *out = '\0';

    return dst;
}

/*@ assigns \nothing; */
char *
strchr(const char *s, int c)
{
    char want = (char)c;

    for (;; s++) {
        if (*s == want)
            return (char *)s;
        if (*s == '\0')
            return (void *)0;
    }
}

/*@ assigns \nothing; */
char *
strrchr(const char *s, int c)
{
    char want = (char)c;
    const char *found = (void *)0;

    for (;; s++) {
        if (*s == want)
            found = s;
        if (*s == '\0')
            return (char *)found;
    }
}

/* Naive search. The callers are debugger.c.o probing a handful of short
 * strings once, so the quadratic worst case never shows up. */
/*@ assigns \nothing; */
char *
strstr(const char *haystack, const char *needle)
{
    if (*needle == '\0')
        return (char *)haystack;

    for (; *haystack != '\0'; haystack++) {
        const char *h = haystack;
        const char *n = needle;

        while (*n != '\0' && *h == *n) {
            h++;
            n++;
        }
        if (*n == '\0')
            return (char *)haystack;
    }

    return (void *)0;
}

/* Written out rather than delegating to strspn/strcspn, which is how musl
 * does it - delegating would pull those members back in for nothing. */
/*@ assigns *save; */
char *
strtok_r(char *s, const char *sep, char **save)
{
    char *start;

    if (s == (void *)0)
        s = *save;
    if (s == (void *)0)
        return (void *)0;

    /*@ assigns \nothing; */
    while (*s != '\0' && strchr(sep, (unsigned char)*s) != (void *)0)
        s++;
    if (*s == '\0') {
        *save = (void *)0;
        return (void *)0;
    }

    start = s;
    while (*s != '\0' && strchr(sep, (unsigned char)*s) == (void *)0)
        s++;
    if (*s != '\0')
        *s++ = '\0';
    else
        s = (void *)0;

    *save = s;
    return start;
}

/*@ assigns \nothing; ensures \result == 0 || \result != 0; */
int
bcmp(const void *a, const void *b, noos_size_t n)
{
    const unsigned char *x = a;
    const unsigned char *y = b;

    while (n-- != 0) {
        if (*x++ != *y++)
            return 1;
    }

    return 0;
}

/*@ assigns \nothing; */
int
atoi(const char *s)
{
    int sign = 1;
    int value = 0;

    while (*s == ' ' || (*s >= '\t' && *s <= '\r'))
        s++;
    if (*s == '-') {
        sign = -1;
        s++;
    } else if (*s == '+') {
        s++;
    }
    while (*s >= '0' && *s <= '9')
        value = value * 10 + (*s++ - '0');

    return sign * value;
}

/* ---- the strtol family, strtol.lo ---------------------------------------
 * Written out in full - all twelve names the member exports - because half a
 * member cannot be displaced. It earns its place: musl's version routes
 * through __shlim/__shgetc/__intscan, and __intscan alone is the single
 * largest block of compressed code in the image.
 *
 * No errno and no saturation. A value that does not fit is a configuration
 * bug, and the guest has nowhere to report one; the callers here are RhConfig
 * reading runtime knobs and the environment. */

/*@ assigns *end; */
static unsigned long long
noos_strtox(const char *s, char **end, int base, int *negative)
{
    unsigned long long value = 0;
    int neg = 0;

    while (*s == ' ' || (*s >= '\t' && *s <= '\r'))
        s++;
    if (*s == '-') {
        neg = 1;
        s++;
    } else if (*s == '+') {
        s++;
    }

    if ((base == 0 || base == 16) && s[0] == '0'
        && (s[1] == 'x' || s[1] == 'X')) {
        s += 2;
        base = 16;
    } else if (base == 0) {
        base = (s[0] == '0') ? 8 : 10;
    }

    for (;; s++) {
        int digit;

        if (*s >= '0' && *s <= '9')
            digit = *s - '0';
        else if (*s >= 'a' && *s <= 'z')
            digit = *s - 'a' + 10;
        else if (*s >= 'A' && *s <= 'Z')
            digit = *s - 'A' + 10;
        else
            break;

        if (digit >= base)
            break;

        value = value * (unsigned long long)base + (unsigned long long)digit;
    }

    if (end != (void *)0)
        *end = (char *)s;
    if (negative != (void *)0)
        *negative = neg;

    return value;
}

#define NOOS_STRTO_UNSIGNED(name, type) \
    /*@ assigns *end; */ \
    type name(const char *s, char **end, int base) \
    { \
        int neg = 0; \
        unsigned long long v = noos_strtox(s, end, base, &neg); \
        return (type)(neg ? 0ULL - v : v); \
    }

#define NOOS_STRTO_SIGNED(name, type) \
    /*@ assigns *end; */ \
    type name(const char *s, char **end, int base) \
    { \
        int neg = 0; \
        unsigned long long v = noos_strtox(s, end, base, &neg); \
        return neg ? -(type)v : (type)v; \
    }

NOOS_STRTO_SIGNED(strtol, long)
NOOS_STRTO_SIGNED(strtoll, long long)
NOOS_STRTO_SIGNED(strtoimax, long long)
NOOS_STRTO_UNSIGNED(strtoul, unsigned long)
NOOS_STRTO_UNSIGNED(strtoull, unsigned long long)
NOOS_STRTO_UNSIGNED(strtoumax, unsigned long long)

/* musl's own callers reach the internal names; they take an extra "group"
 * flag that only locale-aware parsing uses, and the guest has no locales. */
#define NOOS_STRTO_INTERNAL(name, type, base_fn) \
    /*@ assigns *end; */ \
    type name(const char *s, char **end, int base, int group) \
    { (void)group; return base_fn(s, end, base); }

NOOS_STRTO_INTERNAL(__strtol_internal, long, strtol)
NOOS_STRTO_INTERNAL(__strtoll_internal, long long, strtoll)
NOOS_STRTO_INTERNAL(__strtoimax_internal, long long, strtoimax)
NOOS_STRTO_INTERNAL(__strtoul_internal, unsigned long, strtoul)
NOOS_STRTO_INTERNAL(__strtoull_internal, unsigned long long, strtoull)
NOOS_STRTO_INTERNAL(__strtoumax_internal, unsigned long long, strtoumax)

/* ========================================================================
 * Program startup, replacing musl's __libc_start_main.
 *
 * musl's startup is three functions - __libc_start_main, __init_libc,
 * __libc_start_init - and 126 compressed instructions, almost all of it work
 * a zkVM guest has no use for: parsing auxv, probing page size, deciding
 * whether the process is setuid, wiring up environ. What it does that we
 * cannot skip is exactly two things, and both are done here instead.
 *
 * FIRST, the thread pointer. bflat's tls module initialises its static TLS
 * block lazily, so __tls_get_addr is safe on its own - but the `tp` REGISTER
 * is only ever written by __init_tp, and today the sole caller is musl's
 * startup. Any thread-static reached through the local-exec or initial-exec
 * model goes through tp directly, without calling __tls_get_addr, so leaving
 * tp unset would not fail loudly: it would read whatever address zero plus an
 * offset happens to be. That is the failure mode this whole codebase is built
 * to avoid, so the call is made first, before anything can touch a static.
 *
 * SECOND, .init_array. pal registers a constructor there to publish the heap
 * floor (zk_publish_heap_floor), which the JIT's inline allocator bounds-checks
 * against on every allocation and which its contract says is written exactly
 * once, before any managed code runs. Skipping the array would leave that cell
 * zero and every inline allocation guarded against garbage. C++ static
 * constructors in the runtime land here too.
 *
 * What is deliberately NOT replicated: environ stays whatever musl's
 * definition holds (NULL - pal wraps getenv anyway) and __stack_chk_guard
 * stays zero, which is a weaker canary but a working one. Neither is
 * reachable in a way that matters to a proof.
 * ======================================================================== */

extern int uBootstrap_main(int argc, char *argv[]);

/* bflat's own TLS, from modules/tls. Called by its wrap name because that is
 * what the module defines; musl's __init_tls is diverted onto it. */
extern void *__wrap___init_tls(unsigned long *aux);

/* Placed by each target's linker script, as PROVIDE_HIDDEN. */
extern void (*__init_array_start[])(void);
extern void (*__init_array_end[])(void);

/*@ // Initialises the thread pointer, runs the constructors and calls the
    // guest. Unlike noos_start_main this one RETURNS: the caller decides how
    // the program ends (SP1's own runtime halts with this value).
    //
    // The contract stays weak on purpose. The constructor loop calls through
    // function pointers the linker script collects, and uBootstrap_main enters
    // the managed runtime, so what is assigned is not expressible here; the
    // frames those touch are owned by the runtime, not by this module.
    assigns \nothing;
*/
int
noos_main(int argc, char *argv[])
{
    /* Before anything else: a static read through tp would otherwise be a
     * read through zero. */
    __wrap___init_tls((void *)0);

    for (void (**ctor)(void) = __init_array_start;
         ctor != __init_array_end;
         ctor++)
        (*ctor)();

    return uBootstrap_main(argc, argv);
}

/* Targets whose own runtime wants to own the entry (SP1: its __start
 * initialises the allocator and public-values hasher, calls main and halts)
 * enter through noos_main and return; the rest exit here. */
/*@ // Runs the guest and then terminates - exit() resolves to the PAL's
    // __wrap_exit, the target's real halt sequence, so control never comes
    // back.
    assigns \nothing;
    ensures \false;
*/
__attribute__((noreturn))
void
noos_start_main(int argc, char *argv[])
{
    exit(noos_main(argc, argv));
}
