/**
 * @file
 * @brief PAL implementation - replacement for basic functions that are
 *        needed in the .NET runtime
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>
#include <stdarg.h>
#include <string.h>
#include <stdio.h>

extern const char _kernel_heap_bottom[];
extern const char _kernel_heap_top[];

extern char *
__wrap_getenv(char *var)
{
    if (strcmp(var, "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") == 0 ||
        strcmp(var, "DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY") == 0)
    {
        return "1";
    }

    return 0;
}

char *
__wrap_getcwd(char *__buf, int __size)
{
    strcpy(__buf, "/");
    return __buf;
}

int
__wrap_getpid()
{
    return 1;
}

int
__wrap_getegid()
{
    return 1;
}

int
__wrap_geteuid()
{
    return 1;
}

int
__wrap_sched_getaffinity(int, int, void *)
{
    return 0;
}

int
__wrap_open(const char *path, int flags, int mode)
{
    return -1;
}


static uint8_t *mem = 0;

static inline uintptr_t
align_down_8_uintptr(uintptr_t x)
{
    return x & ~(uintptr_t)7;
}

extern uint32_t rhp_tss_counter;

void *
__wrap___libc_malloc_impl(unsigned long n)
{
    /* NOTE: This allocator is a simple downward bump allocator.
     * It is intentionally verbose for diagnostics. */
    void     *tmp;
    uint64_t *len;

    uint8_t  *saved_mem = mem;
    uintptr_t top = (uintptr_t)_kernel_heap_top;
    uintptr_t bottom = (uintptr_t)_kernel_heap_bottom;

    /* Initialize bump pointer */
    if (mem == 0)
        mem = (uint8_t *)top;

    /* Defensive: ensure we print meaningful diagnostics even if mem is corrupt */
    uintptr_t mem_before = (uintptr_t)mem;

    /* Align requested size to 8 so our "len" header stays aligned */
    size_t req = (size_t)n;
    size_t req_aligned = (req + 7u) & ~(size_t)7u;

    /* Compute new pointer using uintptr_t to avoid UB on pointer underflow */
    uintptr_t new_tmp_u = align_down_8_uintptr(mem_before - (uintptr_t)req_aligned);
    uintptr_t new_len_u = new_tmp_u - 8u;

    tmp = (void *)new_tmp_u;
    len = (uint64_t *)new_len_u;

    rhp_tss_counter = 0;

    /* Emit maximum diagnostics with correct pointer formatting */
    printf(
        "malloc(n=%lu aligned=%zu) mem@%p saved_mem=%p mem_before=%#" PRIxPTR
        " top=%#" PRIxPTR " bottom=%#" PRIxPTR
        " -> tmp=%#" PRIxPTR " len=%#" PRIxPTR
        "\n",
        n,
        req_aligned,
        (void *)&mem,
        (void *)saved_mem,
        mem_before,
        top,
        bottom,
        (uintptr_t)tmp,
        (uintptr_t)len
    );

    /* Basic range diagnostics (do not trap; just log loudly) */
    if (bottom >= top)
    {
        printf("malloc WARN: heap bounds invalid: bottom=%#" PRIxPTR " top=%#" PRIxPTR "\n", bottom, top);
    }
    if (mem_before < bottom || mem_before > top)
    {
        printf("malloc WARN: mem pointer out of range before alloc: mem_before=%#" PRIxPTR " (bottom=%#" PRIxPTR " top=%#" PRIxPTR ")\n",
               mem_before, bottom, top);
    }
    if (new_len_u < bottom || new_tmp_u > top)
    {
        printf("malloc WARN: computed ptrs out of heap range: tmp=%#" PRIxPTR " len=%#" PRIxPTR " (bottom=%#" PRIxPTR " top=%#" PRIxPTR ")\n",
               new_tmp_u, new_len_u, bottom, top);
    }

    /* Commit bump pointer (include optional diagnostic gap) */
    mem = (uint8_t *)new_len_u;
    mem -= 0x40; /* diagnostic gap (keep if you still want it) */

    /* Store allocation size header */
    *len = (uint64_t)req_aligned;

    /* Return pointer to usable payload */
    return tmp;
}

void
__wrap___libc_free(void *mem)
{
    rhp_tss_counter = 0;
}

void *
__wrap___libc_realloc(void *p, unsigned long n)
{
    void     *tmp;
    uint64_t *len;

    rhp_tss_counter = 0;

    if (!p)
    {
        printf("realloc(p=NULL, n=%lu): delegating to malloc\n", n);
        return __wrap___libc_malloc_impl(n);
    }

    len = (uint64_t *)((uint8_t *)p - 8u);

    printf("realloc(p=%p, n=%lu): old_len=%" PRIu64 " header@%p\n", p, n, *len, (void *)len);

    if (*len >= (uint64_t)n)
    {
        /* Existing block is big enough */
        return p;
    }

    tmp = __wrap___libc_malloc_impl(n);
    if (!tmp)
    {
        printf("realloc(p=%p, n=%lu): malloc returned NULL\n", p, n);
        return 0;
    }

    memcpy(tmp, p, (size_t)*len);
    return tmp;
}

int
__wrap_pthread_create(void *, void *, void *, void *)
{
    return 0;
}

int
__wrap_pthread_sigmask(int how, void *set, void *oldset)
{
    return 0;
}

int
__wrap___clock_gettime(int clk, void *ts)
{
    return -1;
}

int
__wrap_clock_gettime(int clk, void *ts)
{
    return -1;
}

int
__wrap___malloc_allzerop(void *)
{
    return 0;
}

void *
__wrap_mmap(void *addr, int length, int prot, int flags,
            int fd, int offset)
{
    return __wrap___libc_malloc_impl(length);
}

int
__wrap_munmap(void *addr, int length)
{
    return 0;
}


int
__wrap_mlock(const void *addr, int len)
{
    return 0;
}

int
__wrap_munlock(const void *addr, int len)
{
    return 0;
}

int
__wrap_mlockall(int flags)
{
    return 0;
}

int
__wrap_munlockall(void)
{
    return 0;
}

int
__wrap_sched_yield(void)
{
    return 0;
}

int
__wrap_sigaction(int signum, void *act, void *oldact)
{
    return 0;
};

void *
__wrap_signal(int signum, void *handler)
{
    return 0;
}

extern long __real_syscall(long number, ...);

long
__wrap_syscall(long number, ...)
{
    va_list args;
    long arg1, arg2, arg3, arg4, arg5, arg6;
    long result;

    va_start(args, number);
    arg1 = va_arg(args, long);
    arg2 = va_arg(args, long);
    arg3 = va_arg(args, long);
    arg4 = va_arg(args, long);
    arg5 = va_arg(args, long);
    arg6 = va_arg(args, long);
    va_end(args);

    switch (number) {
        case 0x11b:
            return 0;

        default:
            return __real_syscall(number, arg1, arg2, arg3, arg4, arg5, arg6);
    }
}

#if 0
int
__wrap___stdio_write(int fd, const void *buf, int count)
{
    return -1;
}
#endif
