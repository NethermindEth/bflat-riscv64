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

extern const char _kernel_heap_bottom[];
extern const char _kernel_heap_top[];

extern char *
__wrap_getenv(char *)
{
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
void *
__wrap___libc_malloc_impl(unsigned long n)
{
    void    *tmp;
    uint64_t *len;

    if (mem == 0)
        mem = (uint8_t *)(uintptr_t)_kernel_heap_top;

    mem -= n;
    mem -= ((uintptr_t)mem % 8);
    tmp = mem;
    mem -= 8;
    len = (uint64_t *)mem;
    *len = n;
    return tmp;
}

void
__wrap___libc_free(void *mem)
{
}

void *
__wrap___libc_realloc(void *p, unsigned long n)
{
    void    *tmp;
    uint8_t *len;

    if (!p)
        return __wrap___libc_malloc_impl(n);

    len = (p - 8);
    if (*len >= n)
    {
        return mem;
    }

    mem = __wrap___libc_malloc_impl(n);
    memcpy(mem, p, *len);
    return mem;
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
