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
 * Displacing them is worth doing on its own: it is dead weight in a proof.
 * (It was also load-bearing while the layout's musl was built with the C
 * extension, which none of these VMs decode; that musl is now rv64im.)
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
#ifdef __FRAMAC__
/* The ACSL string theory the contracts below are written in (strlen,
 * valid_string, valid_read_string). Logic only: the
 * header declares no C function, so nothing here is displaced by it. */
#include "__fc_string_axiomatic.h"
#endif

/*@ terminates \true;
    assigns \nothing;
    ensures \false;
    exits \exit_status == status;
*/
extern void exit(int status) __attribute__((noreturn));

/* 255 is nofp (floating point reached), 254 is nothread (tried to block),
 * 253 is this one (asked the operating system for something). */
#define NOOS_EXIT_STATUS 253

/* pal's guest console, when the target has one. Weak: noos.o is linked and
 * unit tested without pal, and ZisK's console is a no-op device. */
/*@ // pal's contract for the same function.
    requires len >= 0;
    requires \valid_read(buf + (0 .. len - 1));
    terminates \true;
    exits \false;
    assigns \nothing;
    ensures \result == len;
*/
extern int zkvm_console_write(int fd, const char *buf, int len) __attribute__((weak));

/*@ // A name with a NUL at offset n, n within an int, readable up to and
    // including that NUL. The offset is a ghost argument: every caller
    // passes a string literal (its own name) and sizeof(__func__) - 1, so
    // the literal's contents (-wp-literals) discharge it directly.
    predicate noos_name{L}(char *s, integer n) =
      0 <= n <= 2147483647 && \valid_read(s + (0 .. n)) && s[n] == '\0';
*/

/*@ // Single abort policy for every unsupported operation: name the call,
    // then terminate with status 253, never return. exit() resolves to the
    // PAL's __wrap_exit - the target's real termination sequence - in the
    // zkVM link. The name is a string literal at every call site.
    requires what == \null || noos_name(what, n);
    terminates \true;
    assigns \nothing;
    ensures \false;
    exits \exit_status == NOOS_EXIT_STATUS;
*/
__attribute__((noreturn, noinline, cold))
static void
noos_trap(const char *what) /*@ ghost (unsigned long n) */
{
    /* Worth the handful of instructions on a cold path: without it the guest
     * dies with a bare 253 and finding which of ~150 calls it was means
     * bisecting the program. OpenVM makes that worse - its TERMINATE takes an
     * immediate, so every non-zero status arrives at the host as 1. */
    if (zkvm_console_write && what != 0)
    {
        int len = 0;

        /*@ assert valid_read_string(what) && strlen(what) <= 2147483647; */
        /*@ loop invariant 0 <= len <= strlen(what);
            loop assigns len;
            loop variant strlen(what) - len;
        */
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
    { (void)a; (void)b; (void)c; (void)d; (void)e; (void)f; \
      noos_trap(#name) /*@ ghost (sizeof(__func__) - 1) */; }

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
{ (void)stream; (void)fmt; noos_trap(__func__) /*@ ghost (sizeof(__func__) - 1) */; }

/*@ assigns \nothing; ensures \false; exits \exit_status == NOOS_EXIT_STATUS; */
int fscanf(void *stream, const char *fmt, ...)
{ (void)stream; (void)fmt; noos_trap(__func__) /*@ ghost (sizeof(__func__) - 1) */; }

/*@ assigns \nothing; ensures \false; exits \exit_status == NOOS_EXIT_STATUS; */
char *strerror(int errnum)
{ (void)errnum; noos_trap(__func__) /*@ ghost (sizeof(__func__) - 1) */; }

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
/*@ requires \valid((char *)mask + (0 .. 127));
    assigns ((char *)mask)[0 .. 127];
    ensures \forall integer i; 0 <= i < 128 ==> ((char *)mask)[i] == 0;
    ensures \result == 0; */
int
sigemptyset(void *mask)
{
    char *p = (char *)mask;
    /*@ loop invariant 0 <= i <= 128;
        loop invariant \forall integer j; 0 <= j < i ==> p[j] == 0;
        loop assigns i, p[0 .. 127];
        loop variant 128 - i;
    */
    for (int i = 0; i < 128; i++)
        p[i] = 0;
    return 0;
}

/* musl's sigset_t is a bit array, one bit per signal, bit (sig-1) first.
 * Nothing in the guest ever reads the set back - pal's __wrap_sigaction
 * discards it - but a stub that quietly dropped the signal would be a lie
 * the next reader of this file would have to re-derive, and the honest
 * version is four lines. */
#define NOOS_SIGWORD(sig) (((sig) - 1) / (8 * sizeof(unsigned long)))
#define NOOS_SIGBIT(sig)  (((sig) - 1) % (8 * sizeof(unsigned long)))

/*@ requires 1 <= sig <= 1024;
    requires \valid((unsigned long *)mask + NOOS_SIGWORD(sig));
    assigns ((unsigned long *)mask)[NOOS_SIGWORD(sig)];
    ensures ((unsigned long *)mask)[NOOS_SIGWORD(sig)] ==
            (\old(((unsigned long *)mask)[NOOS_SIGWORD(sig)]) | (1 << NOOS_SIGBIT(sig)));
    ensures \result == 0; */
int
sigaddset(void *mask, int sig)
{
    unsigned long *bits = (unsigned long *)mask;
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

/* Bits set in one byte, and in the first k bytes of a mask. The byte count
 * is a macro so that WP sees it unfolded: the two byte facts the counting
 * loop relies on (range, and that b & (b - 1) clears exactly one bit) are
 * closed by enumerating the 256 byte values (-wp-auto wp:range), which
 * needs the bit tests in the open. They are stated as assertions inside
 * the loop, not as global lemmas, so that the bitwise theory stays out of
 * every other goal of the module. */
#define NOOS_BIT(b, i) (((b) >> (i)) & 1)
#define NOOS_POPCOUNT8(b) \
    (NOOS_BIT(b, 0) + NOOS_BIT(b, 1) + NOOS_BIT(b, 2) + NOOS_BIT(b, 3) + \
     NOOS_BIT(b, 4) + NOOS_BIT(b, 5) + NOOS_BIT(b, 6) + NOOS_BIT(b, 7))

/*@ logic integer noos_cpus{L}(unsigned char *p, integer k) =
      k <= 0 ? 0 : noos_cpus(p, k - 1) + NOOS_POPCOUNT8(p[k - 1]);
*/

/* Real implementation of musl's helper: the number of CPUs in an affinity
 * mask (pal's __wrap_sched_getaffinity reports exactly one). */
/*@ requires size <= 268435455;   // 8 * size bits counted in an int
    requires \valid_read((const unsigned char *)mask + (0 .. size - 1));
    assigns \nothing;
    ensures \result == noos_cpus((unsigned char *)mask, size);
    ensures 0 <= \result <= 8 * size; */
int
__sched_cpucount(unsigned long size, const void *mask)
{
    const unsigned char *p = (const unsigned char *)mask;
    int n = 0;
    /*@ loop invariant 0 <= i <= size;
        loop invariant n == noos_cpus((unsigned char *)p, i);
        loop invariant 0 <= n <= 8 * i;
        loop assigns i, n;
        loop variant size - i;
    */
    for (unsigned long i = 0; i < size; i++)
        /*@ loop invariant 0 <= b < 256;
            loop invariant 0 <= NOOS_POPCOUNT8(p[i]) <= 8;
            loop invariant 0 <= NOOS_POPCOUNT8(b);
            loop invariant noos_cpus((unsigned char *)p, i + 1) ==
                           noos_cpus((unsigned char *)p, i) + NOOS_POPCOUNT8(p[i]);
            loop invariant n + NOOS_POPCOUNT8(b) ==
                           noos_cpus((unsigned char *)p, i) + NOOS_POPCOUNT8(p[i]);
            loop assigns b, n;
            loop variant NOOS_POPCOUNT8(b);
        */
        for (unsigned char b = p[i]; b != 0; b &= (unsigned char)(b - 1))
            /*@ assert NOOS_POPCOUNT8(b & (b - 1)) == NOOS_POPCOUNT8(b) - 1; */
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

typedef __SIZE_TYPE__ noos_size_t;  /* size_t, without the libc headers: 32 bits on rv32 */

/*@ // c occurs in s, its terminator included (strchr's notion of "found").
    predicate noos_has_char{L}(char *s, integer c) =
      \exists integer i; 0 <= i <= strlen(s) && s[i] == c;

    // ASCII-only case folding, as strcasecmp applies it.
    logic integer noos_lower(integer c) =
      'A' <= c <= 'Z' ? c + ('a' - 'A') : c;

    // The first n bytes agree, or they agree up to and including a NUL
    // of a that comes before n: strncmp's notion of "equal".
    predicate noos_strn_equal{L}(char *a, char *b, integer n) =
      (\forall integer i; 0 <= i < n ==> a[i] == b[i])
      || (\exists integer k; 0 <= k < n && a[k] == '\0'
          && \forall integer i; 0 <= i <= k ==> a[i] == b[i]);

    // The first n bytes of s are readable, or s is a readable string
    // (possibly shorter): what the bounded string functions read.
    predicate noos_readable_n{L}(char *s, integer n) =
      \valid_read(s + (0 .. n - 1)) || valid_read_string(s);

    // needle occurs in h at offset k.
    predicate noos_match_at{L}(char *h, char *needle, integer k) =
      \forall integer j; 0 <= j < strlen(needle) ==> h[k + j] == needle[j];
*/

/*@ requires valid_read_string(s);
    requires strlen(s) <= (noos_size_t)-1;   // as every object's size
    assigns \nothing;
    ensures \result == strlen(s); */
noos_size_t
strlen(const char *s)
{
    const char *p = s;

    /*@ loop invariant 0 <= p - s <= strlen(s);
        loop invariant \base_addr(p) == \base_addr(s);
        loop assigns p;
        loop variant strlen(s) - (p - s);
    */
    while (*p != '\0')
        p++;
    /*@ assert s[p - s] == '\0'; */
    /*@ assert p - s == strlen(s); */

    return (noos_size_t)(p - s);
}

/*@ requires valid_read_string(a);
    requires valid_read_string(b);
    assigns \nothing;
    ensures \result == 0 <==>
            (strlen(a) == strlen(b)
             && \forall integer i; 0 <= i < strlen(a) ==> a[i] == b[i]); */
int
strcmp(const char *a, const char *b)
{
    /*@ loop invariant 0 <= a - \at(a, Pre) <= strlen(\at(a, Pre));
        loop invariant a - \at(a, Pre) <= strlen(\at(b, Pre));
        loop invariant \base_addr(a) == \base_addr(\at(a, Pre));
        loop invariant b == \at(b, Pre) + (a - \at(a, Pre));
        loop invariant \forall integer i; 0 <= i < a - \at(a, Pre) ==>
                       \at(a, Pre)[i] == \at(b, Pre)[i];
        loop assigns a, b;
        loop variant strlen(\at(a, Pre)) - (a - \at(a, Pre));
    */
    while (*a != '\0' && *a == *b) {
        a++;
        b++;
    }

    return (int)(unsigned char)*a - (int)(unsigned char)*b;
}

/*@ requires noos_readable_n(a, n);
    requires noos_readable_n(b, n);
    assigns \nothing;
    ensures \result == 0 <==> noos_strn_equal(a, b, n); */
int
strncmp(const char *a, const char *b, noos_size_t n)
{
    if (n == 0)
        return 0;

    /*@ loop invariant 0 <= a - \at(a, Pre) < \at(n, Pre);
        loop invariant n == \at(n, Pre) - (a - \at(a, Pre));
        loop invariant \base_addr(a) == \base_addr(\at(a, Pre));
        loop invariant b == \at(b, Pre) + (a - \at(a, Pre));
        loop invariant valid_read_string(\at(a, Pre)) ==>
                       a - \at(a, Pre) <= strlen(\at(a, Pre));
        loop invariant valid_read_string(\at(b, Pre)) ==>
                       a - \at(a, Pre) <= strlen(\at(b, Pre));
        loop invariant \forall integer i; 0 <= i < a - \at(a, Pre) ==>
                       \at(a, Pre)[i] == \at(b, Pre)[i] && \at(a, Pre)[i] != '\0';
        loop assigns a, b, n;
        loop variant n;
    */
    while (--n != 0 && *a != '\0' && *a == *b) {
        a++;
        b++;
    }

    return (int)(unsigned char)*a - (int)(unsigned char)*b;
}

/* Only ASCII matters here: the runtime uses it for environment and config
 * names, and the guest runs with globalization switched off. */
/*@ requires valid_read_string(a);
    requires valid_read_string(b);
    assigns \nothing;
    ensures \result == 0 <==>
            (\forall integer i; 0 <= i <= strlen(a) ==>
             noos_lower((unsigned char)a[i]) == noos_lower((unsigned char)b[i])); */
int
strcasecmp(const char *a, const char *b)
{
    int ca, cb;

    /*@ loop invariant 0 <= a - \at(a, Pre) <= strlen(\at(a, Pre));
        loop invariant a - \at(a, Pre) <= strlen(\at(b, Pre));
        loop invariant \base_addr(a) == \base_addr(\at(a, Pre));
        loop invariant b == \at(b, Pre) + (a - \at(a, Pre));
        loop invariant \forall integer i; 0 <= i < a - \at(a, Pre) ==>
                       \at(a, Pre)[i] != '\0'
                       && noos_lower((unsigned char)\at(a, Pre)[i]) ==
                          noos_lower((unsigned char)\at(b, Pre)[i]);
        loop assigns a, b, ca, cb;
        loop variant strlen(\at(a, Pre)) - (a - \at(a, Pre));
    */
    do {
        ca = (unsigned char)*a++;
        cb = (unsigned char)*b++;
        if (ca >= 'A' && ca <= 'Z')
            ca += 'a' - 'A';
        if (cb >= 'A' && cb <= 'Z')
            cb += 'a' - 'A';
    } while (ca != 0 && ca == cb);
    /*@ assert ca == noos_lower((unsigned char)\at(a, Pre)[a - \at(a, Pre) - 1]); */
    /*@ assert cb == noos_lower((unsigned char)\at(b, Pre)[a - \at(a, Pre) - 1]); */

    return ca - cb;
}

/*@ requires valid_read_string(src);
    requires \valid(dst + (0 .. strlen(src)));
    requires \separated(dst + (0 .. strlen(src)), src + (0 .. strlen(src)));
    assigns dst[0 .. strlen(src)];
    ensures \forall integer i; 0 <= i <= \old(strlen(src)) ==> dst[i] == \old(src[i]);
    ensures \result == dst; */
char *
strcpy(char *dst, const char *src)
{
    char *out = dst;

    /*@ loop invariant 0 <= src - \at(src, Pre) <= \at(strlen(src), Pre);
        loop invariant \base_addr(src) == \base_addr(\at(src, Pre));
        loop invariant out == dst + (src - \at(src, Pre));
        loop invariant \forall integer i; 0 <= i < src - \at(src, Pre) ==>
                       dst[i] == \at(src[i], Pre);
        loop assigns out, src, dst[0 .. \at(strlen(src), Pre)];
        loop variant \at(strlen(src), Pre) - (src - \at(src, Pre));
    */
    while ((*out++ = *src++) != '\0')
        ;
    /*@ assert \let k = src - \at(src, Pre) - 1; \at(src[k], Pre) == '\0'; */
    /*@ assert src - \at(src, Pre) - 1 == \at(strlen(src), Pre); */

    return dst;
}

/* Pads to n with NULs, as the standard requires and callers rely on. */
/*@ requires noos_readable_n(src, n);
    requires \valid(dst + (0 .. n - 1));
    requires \separated(dst + (0 .. n - 1), src + (0 .. n - 1));
    assigns dst[0 .. n - 1];
    ensures \forall integer i; 0 <= i < n ==>
            (\forall integer j; 0 <= j < i ==> \old(src[j]) != '\0') ==>
            dst[i] == \old(src[i]);
    ensures \forall integer i; 0 <= i < n ==>
            (\exists integer j; 0 <= j < i && \old(src[j]) == '\0') ==>
            dst[i] == '\0';
    ensures \result == dst; */
char *
strncpy(char *dst, const char *src, noos_size_t n)
{
    noos_size_t i = 0;

    /*@ loop invariant 0 <= i <= n;
        loop invariant \let k = i;
                       \at(valid_read_string(src) ==> k <= strlen(src), Pre);
        loop invariant \forall integer j; 0 <= j < i ==>
                       dst[j] == \at(src[j], Pre) && \at(src[j], Pre) != '\0';
        loop assigns i, dst[0 .. n - 1];
        loop variant n - i;
    */
    while (i < n && src[i] != '\0') {
        dst[i] = src[i];
        i++;
    }
    /*@ ghost noos_size_t copied = i; */
    /*@ loop invariant copied <= i <= n;
        loop invariant \let c = copied; c < n ==> \at(src[c], Pre) == '\0';
        loop invariant \forall integer j; 0 <= j < copied ==>
                       dst[j] == \at(src[j], Pre) && \at(src[j], Pre) != '\0';
        loop invariant \forall integer j; copied <= j < i ==> dst[j] == '\0';
        loop assigns i, dst[0 .. n - 1];
        loop variant n - i;
    */
    while (i < n)
        dst[i++] = '\0';

    return dst;
}

/*@ requires valid_string(dst);
    requires valid_read_string(src);
    requires \valid(dst + (0 .. strlen(dst) + strlen(src)));
    requires \separated(dst + (0 .. strlen(dst) + strlen(src)),
                        src + (0 .. strlen(src)));
    assigns dst[strlen(dst) .. strlen(dst) + strlen(src)];
    ensures \forall integer i; 0 <= i < \old(strlen(dst)) ==>
            dst[i] == \old(dst[i]);
    ensures \forall integer i; 0 <= i <= \old(strlen(src)) ==>
            dst[\old(strlen(dst)) + i] == \old(src[i]);
    ensures \result == dst; */
char *
strcat(char *dst, const char *src)
{
    char *out = dst;

    /*@ loop invariant 0 <= out - dst <= strlen(dst);
        loop invariant \base_addr(out) == \base_addr(dst);
        loop assigns out;
        loop variant strlen(dst) - (out - dst);
    */
    while (*out != '\0')
        out++;
    /*@ assert dst[out - dst] == '\0'; */
    /*@ assert out - dst == strlen(dst); */
    /*@ ghost char *tail = out; */
    /*@ loop invariant 0 <= src - \at(src, Pre) <= \at(strlen(src), Pre);
        loop invariant \base_addr(src) == \base_addr(\at(src, Pre));
        loop invariant out == tail + (src - \at(src, Pre));
        loop invariant \forall integer i; 0 <= i < src - \at(src, Pre) ==>
                       tail[i] == \at(src[i], Pre);
        loop assigns out, src, tail[0 .. \at(strlen(src), Pre)];
        loop variant \at(strlen(src), Pre) - (src - \at(src, Pre));
    */
    while ((*out++ = *src++) != '\0')
        ;
    /*@ assert \let k = src - \at(src, Pre) - 1; \at(src[k], Pre) == '\0'; */
    /*@ assert src - \at(src, Pre) - 1 == \at(strlen(src), Pre); */

    return dst;
}

/*@ // No byte strncat reads from src lies where it has already written:
    // byte i of src is read after dst[strlen(dst) .. strlen(dst) + i - 1].
    predicate noos_strncat_sep{L}(char *dst, char *src, integer n) =
      \forall integer i; 0 <= i < n
        && (\forall integer j; 0 <= j < i ==> src[j] != '\0') ==>
        \separated(src + i, dst + (strlen(dst) .. strlen(dst) + i - 1));
*/

/*@ // strncat copies c bytes: the first n, or up to the first NUL.
    predicate noos_ncount{L}(char *src, integer n, integer c) =
      0 <= c <= n && (\forall integer j; 0 <= j < c ==> src[j] != '\0')
      && (c < n ==> src[c] == '\0');
*/

/*@ requires valid_string(dst);
    requires noos_readable_n(src, n);
    requires noos_strncat_sep(dst, src, n);
    // dst[strlen(dst) + k] is written when the k bytes before src[k] are
    // not NUL: bytes copied, then the terminator.
    requires \forall integer i; strlen(dst) <= i <= strlen(dst) + n
             && (\forall integer j; 0 <= j < i - strlen(dst) ==> src[j] != '\0') ==>
             \valid(dst + i);
    // c + 1 of these bytes are written, c as in noos_ncount.
    assigns dst[strlen(dst) .. strlen(dst) + n];
    ensures \forall integer i; 0 <= i < \old(strlen(dst)) ==>
            dst[i] == \old(dst[i]);
    ensures \forall integer i; 0 <= i < n
            && (\forall integer j; 0 <= j <= i ==> \old(src[j]) != '\0') ==>
            dst[\old(strlen(dst)) + i] == \old(src[i]);
    ensures \forall integer c; noos_ncount{Pre}(src, n, c) ==>
            dst[\old(strlen(dst)) + c] == '\0';
    ensures \result == dst;
*/
char *
strncat(char *dst, const char *src, noos_size_t n)
{
    char *out = dst;

    /*@ loop invariant 0 <= out - dst <= strlen(dst);
        loop invariant \base_addr(out) == \base_addr(dst);
        loop assigns out;
        loop variant strlen(dst) - (out - dst);
    */
    while (*out != '\0')
        out++;
    /*@ assert dst[out - dst] == '\0'; */
    /*@ assert out - dst == strlen(dst); */
    /*@ ghost char *tail = out; */
    /*@ loop invariant 0 <= src - \at(src, Pre) <= \at(n, Pre);
        loop invariant n == \at(n, Pre) - (src - \at(src, Pre));
        loop invariant \base_addr(src) == \base_addr(\at(src, Pre));
        loop invariant out == tail + (src - \at(src, Pre));
        loop invariant \forall integer i; 0 <= i < src - \at(src, Pre) ==>
                       \at(src[i], Pre) != '\0' && tail[i] == \at(src[i], Pre);
        loop invariant \let k = src - \at(src, Pre);
                       \at(valid_read_string(src) ==> k <= strlen(src), Pre);
        loop invariant \forall integer i;
                       src - \at(src, Pre) <= i < \at(n, Pre)
                       && (\forall integer j; 0 <= j < i ==> \at(src[j], Pre) != '\0') ==>
                       \at(src, Pre)[i] == \at(src[i], Pre);
        loop invariant tail == dst + \at(strlen(dst), Pre);
        loop assigns out, src, n, tail[0 .. \at(n, Pre) - 1];
        loop variant n;
    */
    while (n-- != 0 && *src != '\0')
        *out++ = *src++;
    /*@ assert \let k = src - \at(src, Pre);
               k == \at(n, Pre) || \at(src[k], Pre) == '\0'; */
    /*@ assert \let k = src - \at(src, Pre); noos_ncount{Pre}(\at(src, Pre), \at(n, Pre), k); */
    *out = '\0';

    return dst;
}

/*@ requires valid_read_string(s);
    assigns \nothing;

    behavior found:
      assumes noos_has_char(s, (char)c);
      ensures \base_addr(\result) == \base_addr(s);
      ensures 0 <= \result - s <= strlen(s);
      ensures *\result == (char)c;
      ensures \forall integer i; 0 <= i < \result - s ==> s[i] != (char)c;

    behavior not_found:
      assumes !noos_has_char(s, (char)c);
      ensures \result == \null;

    complete behaviors;
    disjoint behaviors;
*/
char *
strchr(const char *s, int c)
{
    char want = (char)c;

    /*@ loop invariant 0 <= s - \at(s, Pre) <= strlen(\at(s, Pre));
        loop invariant \base_addr(s) == \base_addr(\at(s, Pre));
        loop invariant \at(s, Pre)[s - \at(s, Pre)] == *s;
        loop invariant \forall integer i; 0 <= i < s - \at(s, Pre) ==>
                       \at(s, Pre)[i] != want;
        loop assigns s;
        loop variant strlen(\at(s, Pre)) - (s - \at(s, Pre));
    */
    for (;; s++) {
        if (*s == want)
            return (char *)s;
        if (*s == '\0')
            return (void *)0;
    }
}

/*@ requires valid_read_string(s);
    assigns \nothing;

    behavior found:
      assumes noos_has_char(s, (char)c);
      ensures \base_addr(\result) == \base_addr(s);
      ensures 0 <= \result - s <= strlen(s);
      ensures *\result == (char)c;
      ensures \forall integer i; \result - s < i <= strlen(s) ==> s[i] != (char)c;

    behavior not_found:
      assumes !noos_has_char(s, (char)c);
      ensures \result == \null;

    complete behaviors;
    disjoint behaviors;
*/
char *
strrchr(const char *s, int c)
{
    char want = (char)c;
    const char *found = (void *)0;

    /*@ loop invariant 0 <= s - \at(s, Pre) <= strlen(\at(s, Pre));
        loop invariant \base_addr(s) == \base_addr(\at(s, Pre));
        loop invariant \at(s, Pre)[s - \at(s, Pre)] == *s;
        loop invariant found == \null ==>
                       \forall integer i; 0 <= i < s - \at(s, Pre) ==>
                       \at(s, Pre)[i] != want;
        loop invariant found != \null ==>
                       \base_addr(found) == \base_addr(\at(s, Pre))
                       && 0 <= found - \at(s, Pre) < s - \at(s, Pre)
                       && \at(s, Pre)[found - \at(s, Pre)] == want
                       && \forall integer i; found - \at(s, Pre) < i < s - \at(s, Pre) ==>
                          \at(s, Pre)[i] != want;
        loop assigns s, found;
        loop variant strlen(\at(s, Pre)) - (s - \at(s, Pre));
    */
    for (;; s++) {
        if (*s == want)
            found = s;
        if (*s == '\0')
            return (char *)found;
    }
}

/* Naive search. The callers are debugger.c.o probing a handful of short
 * strings once, so the quadratic worst case never shows up. */
/*@ requires valid_read_string(haystack);
    requires valid_read_string(needle);
    assigns \nothing;

    behavior found:
      assumes \exists integer k; 0 <= k <= strlen(haystack)
              && noos_match_at(haystack, needle, k);
      ensures \base_addr(\result) == \base_addr(haystack);
      ensures 0 <= \result - haystack <= strlen(haystack);
      ensures noos_match_at(haystack, needle, \result - haystack);
      ensures \forall integer k; 0 <= k < \result - haystack ==>
              !noos_match_at(haystack, needle, k);

    behavior not_found:
      assumes \forall integer k; 0 <= k <= strlen(haystack) ==>
              !noos_match_at(haystack, needle, k);
      ensures \result == \null;

    complete behaviors;
    disjoint behaviors;
*/
char *
strstr(const char *haystack, const char *needle)
{
    /*@ assert *needle == '\0' ==> strlen(needle) == 0; */
    /*@ assert *needle == '\0' ==> noos_match_at(haystack, needle, 0); */
    if (*needle == '\0')
        return (char *)haystack;

    /*@ loop invariant 0 <= haystack - \at(haystack, Pre) <= strlen(\at(haystack, Pre));
        loop invariant \base_addr(haystack) == \base_addr(\at(haystack, Pre));
        loop invariant \at(haystack, Pre)[haystack - \at(haystack, Pre)] == *haystack;
        loop invariant \forall integer k; 0 <= k < haystack - \at(haystack, Pre) ==>
                       !noos_match_at(\at(haystack, Pre), needle, k);
        loop assigns haystack;
        loop variant strlen(\at(haystack, Pre)) - (haystack - \at(haystack, Pre));
    */
    for (; *haystack != '\0'; haystack++) {
        const char *h = haystack;
        const char *n = needle;

        /*@ loop invariant 0 <= n - needle <= strlen(needle);
            loop invariant \base_addr(n) == \base_addr(needle);
            loop invariant h == haystack + (n - needle);
            loop invariant (haystack - \at(haystack, Pre)) + (n - needle)
                           <= strlen(\at(haystack, Pre));
            loop invariant needle[n - needle] == *n;
            loop invariant \at(haystack, Pre)[(haystack - \at(haystack, Pre)) + (n - needle)]
                           == *h;
            loop invariant \forall integer j; 0 <= j < n - needle ==>
                           \at(haystack, Pre)[(haystack - \at(haystack, Pre)) + j]
                           == needle[j];
            loop assigns h, n;
            loop variant strlen(needle) - (n - needle);
        */
        while (*n != '\0' && *h == *n) {
            h++;
            n++;
        }
        /*@ assert *n == '\0' ==> n - needle == strlen(needle); */
        /*@ assert *n != '\0' ==> n - needle < strlen(needle); */
        if (*n == '\0')
            /*@ assert noos_match_at(\at(haystack, Pre), needle,
                                     haystack - \at(haystack, Pre)); */
            return (char *)haystack;
    }

    return (void *)0;
}

/*@ // The string strtok_r works on: s, or where the previous call stopped.
    logic char *noos_tok_str{L}(char *s, char **save) = s != \null ? s : *save;
*/

/* Written out rather than delegating to strspn/strcspn, which is how musl
 * does it - delegating would pull those members back in for nothing. */
/*@ requires \valid(save);
    requires valid_read_string(sep);
    requires noos_tok_str(s, save) == \null || valid_string(noos_tok_str(s, save));
    requires \separated(save, sep + (0 .. strlen(sep)));
    requires noos_tok_str(s, save) != \null ==>
             \separated(noos_tok_str(s, save) + (0 .. strlen(noos_tok_str(s, save))),
                        save, sep + (0 .. strlen(sep)));
    assigns *save,
            noos_tok_str(s, save)[0 .. strlen(noos_tok_str(s, save)) - 1];
    ensures \result == \null ==> *save == \null;
    ensures \result != \null ==>
            \base_addr(\result) == \base_addr(noos_tok_str{Pre}(s, save));
    ensures \result != \null ==> valid_string(\result) && strlen(\result) >= 1;
    ensures \result != \null ==>
            \forall integer i; 0 <= i < strlen(\result) ==>
            !noos_has_char{Pre}(sep, \result[i]);
    ensures *save == \null
            || (\base_addr(*save) == \base_addr(noos_tok_str{Pre}(s, save))
                && valid_string(*save)); */
char *
strtok_r(char *s, const char *sep, char **save)
{
    char *start;

    if (s == (void *)0)
        s = *save;
    if (s == (void *)0)
        return (void *)0;

    /*@ ghost char *str = s; */
    /*@ loop invariant 0 <= s - str <= strlen(str);
        loop invariant \base_addr(s) == \base_addr(str);
        loop invariant str[s - str] == *s;
        loop assigns s;
        loop variant strlen(str) - (s - str);
    */
    while (*s != '\0' && strchr(sep, (unsigned char)*s) != (void *)0)
        s++;
    if (*s == '\0') {
        *save = (void *)0;
        return (void *)0;
    }

    start = s;
    /*@ loop invariant 0 <= start - str <= s - str <= strlen(str);
        loop invariant \base_addr(s) == \base_addr(str);
        loop invariant str[s - str] == *s;
        loop invariant \forall integer i; start - str <= i < s - str ==>
                       str[i] != '\0' && !noos_has_char(sep, str[i]);
        loop assigns s;
        loop variant strlen(str) - (s - str);
    */
    while (*s != '\0' && strchr(sep, (unsigned char)*s) == (void *)0)
        s++;
    /*@ ghost char *end = s; */
    /*@ assert start < end; */
    if (*s != '\0')
        *s++ = '\0';
    else
        s = (void *)0;
    /*@ assert str[end - str] == '\0'; */
    /*@ assert \let q = str; str[strlen{Pre}(q)] == '\0'; */
    /*@ assert \forall integer i; start - str <= i < end - str ==>
               \let q = str; str[i] == \at(q[i], Pre); */
    /*@ assert strlen(start) == end - start; */
    /*@ assert valid_string(start); */
    /*@ assert \forall integer i; start - str <= i < end - str ==>
               !noos_has_char{Pre}(sep, str[i]); */
    /*@ assert \forall integer i; 0 <= i < end - start ==>
               start[i] == str[(start - str) + i]; */
    /*@ assert \forall integer i; 0 <= i < end - start ==>
               !noos_has_char{Pre}(sep, start[i]); */
    /*@ assert \let q = str;
               s != \null ==> s == end + 1 && end - str < strlen{Pre}(q); */
    /*@ assert \let q = str;
               s != \null ==> 0 <= strlen(s) <= strlen{Pre}(q) - (s - str); */

    *save = s;
    return start;
}

/*@ requires \valid_read((const unsigned char *)a + (0 .. n - 1));
    requires \valid_read((const unsigned char *)b + (0 .. n - 1));
    assigns \nothing;
    ensures \result == 0 || \result == 1;
    ensures \result == 0 <==>
            (\forall integer i; 0 <= i < n ==>
             ((const unsigned char *)a)[i] == ((const unsigned char *)b)[i]); */
int
bcmp(const void *a, const void *b, noos_size_t n)
{
    const unsigned char *x = a;
    const unsigned char *y = b;

    /*@ loop invariant 0 <= n <= \at(n, Pre);
        loop invariant x == (const unsigned char *)a + (\at(n, Pre) - n);
        loop invariant y == (const unsigned char *)b + (\at(n, Pre) - n);
        loop invariant \forall integer i; 0 <= i < \at(n, Pre) - n ==>
                       ((const unsigned char *)a)[i] == ((const unsigned char *)b)[i];
        loop assigns n, x, y;
        loop variant n;
    */
    while (n-- != 0) {
        if (*x++ != *y++)
            return 1;
    }

    return 0;
}

/*@ predicate noos_isspace(integer c) = c == ' ' || ('\t' <= c <= '\r');
    predicate noos_isdigit(integer c) = '0' <= c <= '9';

    // Value of the k decimal digits at s.
    logic integer noos_dec{L}(char *s, integer k) =
      k <= 0 ? 0 : 10 * noos_dec(s, k - 1) + (s[k - 1] - '0');

    // Digits may start at s + p: only white space before, or white space
    // and one sign.
    predicate noos_num_start{L}(char *s, integer p) =
      (\forall integer j; 0 <= j < p ==> noos_isspace(s[j]))
      || (1 <= p && (s[p - 1] == '+' || s[p - 1] == '-')
          && \forall integer j; 0 <= j < p - 1 ==> noos_isspace(s[j]));

    // The magnitude of the leading decimal number of s is at most lim.
    predicate noos_dec_fits{L}(char *s, integer lim) =
      \forall integer p, q; 0 <= p <= q && noos_num_start(s, p)
        && (\forall integer j; p <= j < q ==> noos_isdigit(s[j]))
        ==> noos_dec(s + p, q - p) <= lim;
*/

/* No saturation, as in the strtol family below, and here the value is
 * accumulated in an int: the magnitude has to fit one. */
/*@ requires valid_read_string(s);
    requires noos_dec_fits(s, 2147483647);
    assigns \nothing; */
int
atoi(const char *s)
{
    int sign = 1;
    int value = 0;

    /*@ loop invariant 0 <= s - \at(s, Pre) <= strlen(\at(s, Pre));
        loop invariant \base_addr(s) == \base_addr(\at(s, Pre));
        loop invariant \forall integer j; 0 <= j < s - \at(s, Pre) ==>
                       noos_isspace(\at(s, Pre)[j]);
        loop assigns s;
        loop variant strlen(\at(s, Pre)) - (s - \at(s, Pre));
    */
    while (*s == ' ' || (*s >= '\t' && *s <= '\r'))
        s++;
    if (*s == '-') {
        sign = -1;
        s++;
    } else if (*s == '+') {
        s++;
    }
    /*@ ghost const char *digits = s; */
    /*@ loop invariant 0 <= digits - \at(s, Pre) <= s - \at(s, Pre) <= strlen(\at(s, Pre));
        loop invariant \base_addr(s) == \base_addr(\at(s, Pre));
        loop invariant \base_addr(digits) == \base_addr(\at(s, Pre));
        loop invariant noos_num_start(\at(s, Pre), digits - \at(s, Pre));
        loop invariant \forall integer j; digits - \at(s, Pre) <= j < s - \at(s, Pre) ==>
                       noos_isdigit(\at(s, Pre)[j]);
        loop invariant value == noos_dec((char *)digits, s - digits);
        loop invariant 0 <= value <= 2147483647;
        loop assigns s, value;
        loop variant strlen(\at(s, Pre)) - (s - \at(s, Pre));
    */
    while (*s >= '0' && *s <= '9')
        /*@ assert \at(s, Pre) + (digits - \at(s, Pre)) == digits
                   && \at(s, Pre)[s - \at(s, Pre)] == *s; */
        /*@ assert noos_dec((char *)digits, (s - digits) + 1) <= 2147483647; */
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

/*@ // Where a parse stops: within the string it was given.
    predicate noos_end_in{L}(char **end, char *s) =
      end != \null ==>
        \base_addr(*end) == \base_addr(s) && 0 <= *end - s <= strlen(s);
*/

/*@ requires valid_read_string(s);
    requires end == \null || \valid(end);
    requires negative == \null || \valid(negative);
    requires \separated(end, negative);
    requires end == \null || \separated(end, s + (0 .. strlen(s)));
    requires negative == \null || \separated(negative, s + (0 .. strlen(s)));
    assigns *end, *negative;
    ensures noos_end_in(end, s);
    ensures negative != \null ==> (*negative == 0 || *negative == 1); */
static unsigned long long
noos_strtox(const char *s, char **end, int base, int *negative)
{
    const char *start = s;
    const char *digits;
    unsigned long long value = 0;
    int neg = 0;
    int prefix = 0;

    /*@ loop invariant 0 <= s - \at(s, Pre) <= strlen(\at(s, Pre));
        loop invariant \base_addr(s) == \base_addr(\at(s, Pre));
        loop assigns s;
        loop variant strlen(\at(s, Pre)) - (s - \at(s, Pre));
    */
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
        prefix = 1;
    } else if (base == 0) {
        base = (s[0] == '0') ? 8 : 10;
    }

    digits = s;
    /*@ loop invariant 0 <= s - \at(s, Pre) <= strlen(\at(s, Pre));
        loop invariant \base_addr(s) == \base_addr(\at(s, Pre));
        loop invariant digits <= s;
        loop assigns s, value;
        loop variant strlen(\at(s, Pre)) - (s - \at(s, Pre));
    */
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

    /* No digits: nothing was converted and *end is the string itself, as ISO
     * C asks - except after a "0x" prefix, where the "0" is the number and
     * the parse ends at the 'x'. */
    if (s == digits) {
        s = prefix ? digits - 1 : start;
        neg = 0;
    }

    if (end != (void *)0)
        *end = (char *)s;
    if (negative != (void *)0)
        *negative = neg;

    return value;
}

/* Contract shared by the six public names and the six internal ones. */
#define NOOS_STRTO_SPEC \
    requires valid_read_string(s); \
    requires end == \null || \valid(end); \
    requires end == \null || \separated(end, s + (0 .. strlen(s))); \
    assigns *end; \
    ensures noos_end_in(end, s);

#define NOOS_STRTO_UNSIGNED(name, type) \
    /*@ NOOS_STRTO_SPEC */ \
    type name(const char *s, char **end, int base) \
    { \
        int neg = 0; \
        unsigned long long v = noos_strtox(s, end, base, &neg); \
        return (type)(neg ? 0ULL - v : v); \
    }

/* Negated in unsigned arithmetic, then converted: the same two's-complement
 * value as negating after the conversion, and defined for the one input
 * where that negation is not ("-9223372036854775808" for a 64-bit type). */
#define NOOS_STRTO_SIGNED(name, type) \
    /*@ NOOS_STRTO_SPEC */ \
    type name(const char *s, char **end, int base) \
    { \
        int neg = 0; \
        unsigned long long v = noos_strtox(s, end, base, &neg); \
        return (type)(neg ? 0ULL - v : v); \
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
    /*@ NOOS_STRTO_SPEC */ \
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
    // frames those touch are owned by the runtime, not by this module. Hence
    // no assigns clause (any memory may change), no promise to return, and
    // the guest may end the program from inside.
    terminates \false;
    exits \true;
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
    // back. No assigns clause, as for noos_main: the guest may write any
    // memory before it ends.
    terminates \false;
    exits \true;
    ensures \false;
*/
__attribute__((noreturn))
void
noos_start_main(int argc, char *argv[])
{
    exit(noos_main(argc, argv));
}
