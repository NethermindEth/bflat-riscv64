/**
 * @file
 * @brief Unit tests for pal/module.c (every extern function; the static
 *        helpers align_down_8_uintptr / zkvm_emit / zkvm_raw_exit are
 *        exercised through their only callers).
 *
 * Link requirements (see run_tests.sh):
 *   - _kernel_heap_bottom is defined here as a 1 MiB array;
 *     _kernel_heap_top is aliased to its end via
 *     -Wl,--defsym,_kernel_heap_top=_kernel_heap_bottom+1048576
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <inttypes.h>
#include <stdarg.h>
#include <stdio.h>
#include <time.h>

#include "common.h"

#define HEAP_SIZE (1024 * 1024)

/* Linker-script symbols of the real image, provided by the test. */
char _kernel_heap_bottom[HEAP_SIZE] __attribute__((aligned(4096)));

/* Fixed bump-pointer cell (real image: top 8 bytes of RAM). */
uint8_t *g_zk_bump_ptr = 0;

/* __real_syscall sentinel: proves argument forwarding. */
static long real_syscall_last_number;
static long real_syscall_last_arg1;
long __real_syscall(long number, ...)
{
    va_list ap;
    va_start(ap, number);
    real_syscall_last_number = number;
    real_syscall_last_arg1 = va_arg(ap, long);
    va_end(ap);
    return 4242;
}

/* --- module under test ------------------------------------------------ */
extern char *__wrap_getenv(char *var);
extern char *__wrap_getcwd(char *buf, int size);
extern int __wrap_getpid(void);
extern int __wrap_getegid(void);
extern int __wrap_geteuid(void);
extern int __wrap_sched_getaffinity(int pid, int cpusetsize, void *mask);
extern int __wrap_sched_getcpu(void);
extern int __wrap_open(const char *path, int flags, int mode);
extern void *zk_heap_mark(void);
extern void zk_heap_reset(void *m);
extern void *__wrap___libc_malloc_impl(unsigned long n);
extern void __wrap___libc_free(void *p);
extern void *__wrap___libc_realloc(void *p, unsigned long n);
extern void *__wrap_calloc(unsigned long nmemb, unsigned long size);
extern int __wrap_pthread_create(void *a, void *b, void *c, void *d);
extern int __wrap_pthread_sigmask(int how, void *set, void *oldset);
extern int __wrap__Z24PalGetMaximumStackBoundsPPvS0_(void **base, void **limit);
extern int __wrap___clock_gettime(int clk, void *ts);
extern int __wrap_clock_gettime(int clk, void *ts);
extern int __wrap___malloc_allzerop(void *p);
extern void *__wrap_mmap(void *addr, int length, int prot, int flags, int fd,
                         int offset);
extern int __wrap_munmap(void *addr, int length);
extern int __wrap_mlock(const void *addr, int len);
extern int __wrap_munlock(const void *addr, int len);
extern int __wrap_mlockall(int flags);
extern int __wrap_munlockall(void);
extern int __wrap_sched_yield(void);
extern int __wrap_sigaction(int signum, void *act, void *oldact);
extern void *__wrap_signal(int signum, void *handler);
extern int __wrap_vfprintf(FILE *f, const char *fmt, va_list ap);
extern long __wrap_syscall(long number, ...);
extern void __wrap_exit(int code);
extern void __wrap__Exit(int code);
extern void __wrap_abort(void);
extern int RhIsGCBridgeActive(void);
extern int __wrap___stdio_write(int fd, const void *buf, int count);
extern int __wrap_sysconf(int n);
extern void *__wrap_aligned_alloc(unsigned long align, unsigned long size);
extern int __wrap_posix_memalign(void **out, unsigned long align,
                                 unsigned long size);
extern void *__wrap_memalign(unsigned long align, unsigned long size);
extern void *__wrap_inline_bump_alloc_aligned(uint32_t bytes, uint32_t align);
extern int __wrap_vfscanf(void *stream, const char *fmt, void *ap);
extern int __wrap___isoc99_vfscanf(void *stream, const char *fmt, void *ap);
extern long double __wrap___floatscan(void *f, int prec, int pok);

extern const char _kernel_heap_top[];

static uintptr_t heap_top(void) { return (uintptr_t)_kernel_heap_top; }
static uintptr_t heap_bottom(void) { return (uintptr_t)_kernel_heap_bottom; }

/* Variadic front-end for __wrap_vfprintf, mirroring fprintf. */
static int call_vf(FILE *f, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int r = __wrap_vfprintf(f, fmt, ap);
    va_end(ap);
    return r;
}

static void check_vf(const char *want, const char *fmt, ...)
{
    char buf[256];
    memset(buf, 0, sizeof(buf));
    FILE *f = fmemopen(buf, sizeof(buf), "w");
    va_list ap;
    va_start(ap, fmt);
    int r = __wrap_vfprintf(f, fmt, ap);
    va_end(ap);
    fclose(f);
    CHECK(r == (int)strlen(want));
    CHECK_STR_EQ(buf, want);
}

int main(void)
{
    MARK("stubs");
    /* --- trivial identity/stub returns --- */
    CHECK(__wrap_getpid() == 1);
    CHECK(__wrap_getegid() == 1);
    CHECK(__wrap_geteuid() == 1);
    CHECK(__wrap_sched_getcpu() == 0);
    CHECK(__wrap_open("/etc/passwd", 0, 0) == -1);
    CHECK(__wrap___clock_gettime(0, NULL) == -1);
    CHECK(__wrap_clock_gettime(0, NULL) == -1);
    CHECK(__wrap___malloc_allzerop(NULL) == 0);
    CHECK(__wrap_munmap(NULL, 4096) == 0);
    CHECK(__wrap_mlock(NULL, 1) == 0);
    CHECK(__wrap_munlock(NULL, 1) == 0);
    CHECK(__wrap_mlockall(0) == 0);
    CHECK(__wrap_munlockall() == 0);
    CHECK(__wrap_sched_yield() == 0);
    CHECK(__wrap_sigaction(2, NULL, NULL) == 0);
    CHECK(__wrap_signal(2, NULL) == NULL);
    CHECK(__wrap_pthread_create(NULL, NULL, NULL, NULL) == 0);
    CHECK(__wrap_pthread_sigmask(0, NULL, NULL) == 0);
    CHECK(RhIsGCBridgeActive() == 0);
    CHECK(__wrap___stdio_write(1, "x", 1) == -1);
    CHECK(__wrap_vfscanf(NULL, "%d", NULL) == -1);
    CHECK(__wrap___isoc99_vfscanf(NULL, "%d", NULL) == -1);
    CHECK(__wrap___floatscan(NULL, 2, 0) == 0);

    MARK("getenv");
    /* --- getenv: exactly three switches exist --- */
    CHECK_STR_EQ(__wrap_getenv("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"), "1");
    CHECK_STR_EQ(
        __wrap_getenv("DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY"),
        "1");
    CHECK_STR_EQ(
        __wrap_getenv(
            "DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXPARTITIONCOUNT"),
        "1");
    CHECK(__wrap_getenv("PATH") == NULL);
    CHECK(__wrap_getenv("DOTNET_TieredCompilation") == NULL);

    MARK("getcwd");
    /* --- getcwd --- */
    {
        char buf[8] = "zzzzzzz";
        CHECK(__wrap_getcwd(buf, sizeof(buf)) == buf);
        CHECK_STR_EQ(buf, "/");
    }

    MARK("affinity");
    /* --- sched_getaffinity: CPU 0 only, rest of the mask zeroed --- */
    {
        unsigned char mask[16];
        memset(mask, 0xAA, sizeof(mask));
        CHECK(__wrap_sched_getaffinity(1, sizeof(mask), mask) == 0);
        CHECK(mask[0] == 0x01);
        int rest_zero = 1;
        for (unsigned i = 1; i < sizeof(mask); i++)
            rest_zero &= (mask[i] == 0);
        CHECK(rest_zero);
        CHECK(__wrap_sched_getaffinity(1, 0, mask) == 0); /* len 0 is safe */
    }

    MARK("sysconf");
    /* --- sysconf table --- */
    CHECK(__wrap_sysconf(1) == 100);
    CHECK(__wrap_sysconf(2) == 100);
    CHECK(__wrap_sysconf(30) == 4096);
    CHECK(__wrap_sysconf(83) == 1);
    CHECK(__wrap_sysconf(84) == 1);
    CHECK(__wrap_sysconf(85) == 65536);
    CHECK(__wrap_sysconf(9999) == 0);

    MARK("malloc");
    /* --- bump allocator --- */
    {
        CHECK(g_zk_bump_ptr == 0); /* lazily initialised on first use */
        void *a = __wrap___libc_malloc_impl(17);
        CHECK(a != NULL);
        CHECK(((uintptr_t)a % 8) == 0);
        CHECK((uintptr_t)a >= heap_bottom() + 8);
        CHECK((uintptr_t)a + 17 <= heap_top());
        /* size header below the block records the 8-aligned request */
        CHECK(*(uint64_t *)((uint8_t *)a - 8) == 24);
        CHECK(g_zk_bump_ptr == (uint8_t *)a - 8);

        void *b = __wrap___libc_malloc_impl(1);
        CHECK(b != NULL && (uintptr_t)b < (uintptr_t)a); /* grows down */

        /* mark/reset drop everything allocated in between */
        void *mark = zk_heap_mark();
        CHECK(mark == (void *)g_zk_bump_ptr);
        void *c = __wrap___libc_malloc_impl(1000);
        CHECK(c != NULL && zk_heap_mark() != mark);
        zk_heap_reset(mark);
        CHECK(zk_heap_mark() == mark);
        zk_heap_reset(NULL); /* no-op */
        CHECK(zk_heap_mark() == mark);

        /* free is a no-op */
        __wrap___libc_free(b);
        CHECK(zk_heap_mark() == mark);

        /* exhaustion: over-sized request fails, heap stays usable.
         * 2*HEAP_SIZE is the pointer-underflow regression: the naive
         * `mem - req < bottom` check wrapped and wrote a wild header. */
        CHECK(__wrap___libc_malloc_impl(2 * HEAP_SIZE) == NULL);
        CHECK(__wrap___libc_malloc_impl(~0UL) == NULL);      /* n+7 wrap */
        CHECK(__wrap___libc_malloc_impl(~0UL - 8) == NULL);  /* +hdr wrap */
        CHECK(__wrap___libc_malloc_impl(64) != NULL);
    }

    MARK("realloc");
    /* --- realloc --- */
    {
        char *p = __wrap___libc_realloc(NULL, 10); /* fresh */
        CHECK(p != NULL);
        memcpy(p, "0123456789", 10);
        /* header rounded 10 up to 16, so 12 still fits in place */
        CHECK(__wrap___libc_realloc(p, 12) == p);
        char *q = __wrap___libc_realloc(p, 100); /* must move and copy */
        CHECK(q != NULL && q != p);
        CHECK(memcmp(q, "0123456789", 10) == 0);
    }

    MARK("calloc");
    /* --- calloc: zeroed by construction, overflow-checked --- */
    {
        unsigned char *p = __wrap_calloc(16, 4);
        CHECK(p != NULL);
        int all_zero = 1;
        for (int i = 0; i < 64; i++)
            all_zero &= (p[i] == 0);
        CHECK(all_zero);
        CHECK(__wrap_calloc(0x8000000000000000UL, 3) == NULL); /* overflow */
        CHECK(__wrap_calloc(0, 8) != NULL);
    }

    MARK("mmap");
    /* --- mmap degrades to bump allocation --- */
    {
        void *p = __wrap_mmap(NULL, 4096, 3, 0x22, -1, 0);
        CHECK(p != NULL && ((uintptr_t)p % 8) == 0);
        CHECK((uintptr_t)p >= heap_bottom() && (uintptr_t)p < heap_top());
    }

    MARK("aligned");
    /* --- aligned allocation family --- */
    {
        void *p = __wrap_aligned_alloc(64, 100);
        CHECK(p != NULL && ((uintptr_t)p % 64) == 0);
        p = __wrap_aligned_alloc(4096, 10);
        CHECK(p != NULL && ((uintptr_t)p % 4096) == 0);
        p = __wrap_aligned_alloc(8, 10); /* small align: plain malloc path */
        CHECK(p != NULL && ((uintptr_t)p % 8) == 0);
        CHECK(__wrap_aligned_alloc(16, 2 * HEAP_SIZE) == NULL);
        CHECK(__wrap_aligned_alloc(4096, ~0UL - 16) == NULL); /* size+align
                                                                 wrap */

        void *out = (void *)0x1;
        CHECK(__wrap_posix_memalign(&out, 128, 50) == 0);
        CHECK(out != NULL && ((uintptr_t)out % 128) == 0);
        void *keep = (void *)0x1;
        CHECK(__wrap_posix_memalign(&keep, 16, 2 * HEAP_SIZE) == 12);
        CHECK(keep == (void *)0x1); /* untouched on ENOMEM */

        p = __wrap_memalign(256, 40);
        CHECK(p != NULL && ((uintptr_t)p % 256) == 0);
        p = __wrap_inline_bump_alloc_aligned(40, 32);
        CHECK(p != NULL && ((uintptr_t)p % 32) == 0);
    }

    MARK("stackbounds");
    /* --- stack bounds: page-aligned base, 8 MiB window, returns 1 --- */
    {
        void *base = NULL, *limit = NULL;
        CHECK(__wrap__Z24PalGetMaximumStackBoundsPPvS0_(&base, &limit) == 1);
        CHECK(((uintptr_t)base % 4096) == 0);
        CHECK((uintptr_t)base - (uintptr_t)limit == 8u * 1024u * 1024u);
    }

    MARK("vfprintf");
    /* --- vfprintf: the FP-free formatter --- */
    check_vf("hello", "hello");
    check_vf("a=42", "a=%d", 42);
    check_vf("-7", "%d", -7);
    check_vf("2147483647", "%d", 2147483647);
    check_vf("-2147483648", "%d", (int)-2147483648);
    check_vf("18446744073709551615", "%llu", ~0ULL);
    check_vf("ff", "%x", 255);
    check_vf("FF", "%X", 255);
    check_vf("0xff", "%#x", 255);
    check_vf("755", "%o", 0755);
    check_vf("0x1234", "%p", (void *)0x1234);
    check_vf("%", "%%");
    check_vf("c", "%c", 'c');
    check_vf("str", "%s", "str");
    check_vf("(null)", "%s", (char *)NULL);
    check_vf("ab", "%.2s", "abcdef");
    check_vf("   42", "%5d", 42);
    check_vf("00042", "%05d", 42);
    check_vf("42   ", "%-5d", 42);
    check_vf("+42", "%+d", 42);
    check_vf("<float>", "%f", 3.14);       /* consumed, never formatted */
    check_vf("x=<float>!", "x=%g!", 2.71); /* stream continues after it */
    check_vf("12345678901234", "%zu", (size_t)12345678901234ULL);
    {
        /* fputc side effects flow through the real stream */
        char buf[64];
        memset(buf, 0, sizeof(buf));
        FILE *f = fmemopen(buf, sizeof(buf), "w");
        CHECK(call_vf(f, "%s %d", "n", 5) == 3);
        fclose(f);
        CHECK_STR_EQ(buf, "n 5");
    }

    MARK("syscall");
    /* --- syscall: 0x11b swallowed, everything else forwarded --- */
    CHECK(__wrap_syscall(0x11b, 1L, 2L, 3L, 4L, 5L, 6L) == 0);
    CHECK(__wrap_syscall(999L, 77L, 0L, 0L, 0L, 0L, 0L) == 4242);
    CHECK(real_syscall_last_number == 999);
    CHECK(real_syscall_last_arg1 == 77);

    /* --- terminators: the ZisK exit ecall (a7 == 93) is also Linux
     * riscv64 __NR_exit, so under qemu-user the child genuinely exits
     * with the requested status. --- */
    MARK("exit-family");
    EXPECT_EXIT(42, __wrap_exit(42));
    EXPECT_EXIT(7, __wrap__Exit(7));
    EXPECT_EXIT(134, __wrap_abort());

    TEST_MAIN_END("pal");
}
