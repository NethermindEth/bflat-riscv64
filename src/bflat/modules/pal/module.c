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

#define _DEBUG (0)

/* zkVM RAM is zero-initialised and the downward bump allocator never reuses
 * memory, so a freshly handed-out block is already all-zero — the per-object
 * memset is redundant.
 */
#ifndef ZKVM_FAST_ALLOC
#define ZKVM_FAST_ALLOC 1
#endif

extern const char _kernel_heap_bottom[];
extern const char _kernel_heap_top[];

extern char *
__wrap_getenv(char *var)
{
    if (strcmp(var, "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") == 0 ||
        strcmp(var, "DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY") == 0 ||
        strcmp(var, "DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXPARTITIONCOUNT") == 0)
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
__wrap_sched_getaffinity(int pid, int cpusetsize, void *mask)
{
    /* Zero out the entire buffer so __sched_cpucount doesn't count garbage */
    memset(mask, 0, (size_t)cpusetsize);

    /* Set bit 0 (CPU 0) directly in the raw buffer */
    if (cpusetsize > 0)
        ((unsigned char *)mask)[0] |= 0x01;

    return 0;
}

int
__wrap_sched_getcpu(void)
{
    return 0;
}

int
__wrap_open(const char *path, int flags, int mode)
{
    return -1;
}


/* The downward bump pointer lives in a FIXED-address cell (top 8 bytes of RAM,
 * 0xbffefff8) provided by the linker script (g_zk_bump_ptr), so JIT-emitted
 * inline allocation can reference it by a hardcoded constant address and share
 * it with this C allocator. zkVM RAM is zero at boot, so it starts as 0 and is
 * lazily initialised to _kernel_heap_top on first use, exactly as before. */
extern uint8_t *g_zk_bump_ptr;
#define mem g_zk_bump_ptr

static inline uintptr_t
align_down_8_uintptr(uintptr_t x)
{
    return x & ~(uintptr_t)7;
}

extern uint32_t rhp_tss_counter;

/*
 * Heap mark/reset: used by the preinit warmup to drop ephemeral allocations
 * (block/tx/witness/EvmStack buffers from the warmup Execute) after type
 * loading and dispatch-cell resolution have happened. Caller is responsible
 * for ensuring no live reference points into the released region.
 */
void *
zk_heap_mark(void)
{
    return (void *)mem;
}

void
zk_heap_reset(void *m)
{
    if (m != 0)
        mem = (uint8_t *)m;
}

void *
__wrap___libc_malloc_impl(unsigned long n)
{
#if ZKVM_FAST_ALLOC
    if (mem == 0)
        mem = (uint8_t *)_kernel_heap_top;

    size_t req_aligned = ((size_t)n + 7u) & ~(size_t)7u;
    uintptr_t new_tmp = align_down_8_uintptr((uintptr_t)mem - req_aligned);
    uintptr_t new_len = new_tmp - 8u;

    if (new_len < (uintptr_t)_kernel_heap_bottom)
        return NULL;

    mem = (uint8_t *)new_len;
    *(uint64_t *)new_len = (uint64_t)req_aligned;
    return (void *)new_tmp;
#else
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
#if _DEBUG
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
#endif

    /* Bounds check: if the new pointers go below heap bottom, the heap is
     * exhausted.  Return NULL instead of writing to stack/ROM memory and
     * silently corrupting the caller's saved registers (the historic pc=0
     * crash where ra was zeroed by the subsequent calloc memset). */
    if (new_len_u < bottom || new_tmp_u > top)
    {
#if _DEBUG
        printf("malloc OOM: new_tmp=%#" PRIxPTR " new_len=%#" PRIxPTR
               " (bottom=%#" PRIxPTR " top=%#" PRIxPTR ")\n",
               new_tmp_u, new_len_u, bottom, top);
#endif
        return NULL;
    }

    mem = (uint8_t *)new_len_u;

    /* Store allocation size header */
    *len = (uint64_t)req_aligned;

    /* Return pointer to usable payload */
    return tmp;
#endif
}

/* Tight fixed-size object allocator (the hot path, RhpNewFast).
 *
 * Defined here, in the same translation unit as the bump pointer `mem`, the
 * heap bounds and align_down_8_uintptr, so the downward bump is inlined
 * directly: NO nested malloc call, a SINGLE alignment step, and a leaf body
 * (eligible for frameless-leaf). This mirrors how x64/arm64 get fast
 * allocation - a tight RhpNewFast helper - rather than per-site JIT inlining
 * (which RyuJIT does on no target). --wrap=RhpNewFast (rhp module) redirects
 * managed callers here regardless of which .o defines the symbol. */
void *
__wrap_RhpNewFast(void *methodTable)
{
    const size_t MT_BASE_SIZE_OFFSET = 0x4;
    const size_t MIN_OBJECT_SIZE     = 0x18;

    uint32_t baseSize = *(volatile uint32_t *)((uint8_t *)methodTable + MT_BASE_SIZE_OFFSET);
    size_t   total    = (size_t)baseSize;
    if (total < MIN_OBJECT_SIZE)
        total = MIN_OBJECT_SIZE;

#if ZKVM_FAST_ALLOC
    /* Inlined downward bump (mirrors __wrap___libc_malloc_impl fast path):
     * one alignment, no call. zkVM RAM is zero so no memset is needed. */
    if (mem == 0)
        mem = (uint8_t *)_kernel_heap_top;
    size_t    req        = (total + 7u) & ~(size_t)7u;
    uintptr_t new_tmp    = align_down_8_uintptr((uintptr_t)mem - req);
    uintptr_t new_len    = new_tmp - 8u;
    if (new_len < (uintptr_t)_kernel_heap_bottom)
        return 0;
    mem                  = (uint8_t *)new_len;
    *(uint64_t *)new_len = (uint64_t)req;        /* size header */
    void *obj            = (void *)new_tmp;
#else
    total     = (total + 7u) & ~(size_t)7u;
    void *obj = malloc(total);
    if (obj) __builtin_memset(obj, 0, total);
    if (!obj)
        return 0;
#endif

    *(void **)obj = methodTable;                 /* MethodTable header at offset 0 */
    return obj;
}

void
__wrap___libc_free(void *p)
{
    (void)p;
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
#if _DEBUG
        printf("realloc(p=NULL, n=%lu): delegating to malloc\n", n);
#endif
        return __wrap___libc_malloc_impl(n);
    }

    len = (uint64_t *)((uint8_t *)p - 8u);
#if _DEBUG
    printf("realloc(p=%p, n=%lu): old_len=%" PRIu64 " header@%p\n", p, n, *len, (void *)len);
#endif

    if (*len >= (uint64_t)n)
    {
        /* Existing block is big enough */
        return p;
    }

    tmp = __wrap___libc_malloc_impl(n);
    if (!tmp)
    {
#if _DEBUG
        printf("realloc(p=%p, n=%lu): malloc returned NULL\n", p, n);
#endif
        return 0;
    }

    memcpy(tmp, p, (size_t)*len);
    return tmp;
}

/*
 * Optimized calloc
 */
void *
__wrap_calloc(unsigned long nmemb, unsigned long size)
{
    size_t total = (size_t)nmemb * (size_t)size;

    if (nmemb != 0 && total / nmemb != size)
        return NULL;

    void *p = __wrap___libc_malloc_impl((unsigned long)total);
#if !ZKVM_FAST_ALLOC
    /* Fast path: the bump allocator hands out fresh zero RAM, so the block
     * is already zero. Only the safe path needs to clear it explicitly. */
    if (p)
        __builtin_memset(p, 0, total);
#endif
    return p;
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

/*
 * The real PalGetMaximumStackBounds() calls pthread_getattr_np() +
 * pthread_attr_getstack() to probe the main thread's stack.  Under static musl
 * that path invokes mremap(NULL, 0, 0, 0), which fails with EINVAL on RISC-V
 * Linux and aborts runtime startup before Main().  Stack bounds are not used
 * for anything meaningful in Zisk-targeted binaries, so report a synthetic
 * 8 MiB window anchored at the current frame.
 */
int
__wrap__Z24PalGetMaximumStackBoundsPPvS0_(void **stack_base, void **stack_limit)
{
    uintptr_t sp = (uintptr_t)__builtin_frame_address(0);

    *stack_base  = (void *)((sp + 4095u) & ~(uintptr_t)4095u);
    *stack_limit = (void *)((uintptr_t)*stack_base - (8u * 1024u * 1024u));
    return 1;
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

/*
 * FP-free vfprintf. musl's real vfprintf lives in a translation unit that also
 * defines fmt_fp (the %f/%e/%g/%a float formatter), whose hardware F/D
 * instructions are the last floating-point code in the rv64ima image. Every
 * printf/fprintf/snprintf/vsnprintf routes through vfprintf, so wrapping it
 * (and never referencing __real_vfprintf) keeps that whole object - fmt_fp
 * included - out of the link.
 *
 * This reimplementation covers the conversions the runtime's diagnostic paths
 * use (crash dumps, libunwind logging, allocator warnings): %c %s %d/%i %u
 * %x/%X %o %p %% with l/ll/z/j/t length modifiers and basic width / zero / left
 * / precision handling for strings. Float conversions never occur in the guest;
 * if one is ever passed it is consumed from the va_list (variadic doubles
 * arrive in integer registers under the RISC-V calling convention, so this
 * needs no FP) and emitted as "<float>" rather than formatted.
 */
extern int fputc(int c, FILE *stream);

static int
zkvm_emit(FILE *f, const char *s, int n)
{
    int i;
    for (i = 0; i < n; i++)
        fputc((unsigned char)s[i], f);
    return n;
}

int
__wrap_vfprintf(FILE *f, const char *fmt, va_list ap)
{
    int total = 0;
    const char *p = fmt;

    while (*p) {
        if (*p != '%') {
            fputc((unsigned char)*p++, f);
            total++;
            continue;
        }
        p++; /* skip '%' */

        int left = 0, zero = 0, plus = 0, space = 0, alt = 0;
        for (;; p++) {
            if (*p == '-') left = 1;
            else if (*p == '0') zero = 1;
            else if (*p == '+') plus = 1;
            else if (*p == ' ') space = 1;
            else if (*p == '#') alt = 1;
            else break;
        }

        int width = 0;
        if (*p == '*') { width = va_arg(ap, int); p++; if (width < 0) { left = 1; width = -width; } }
        else while (*p >= '0' && *p <= '9') width = width * 10 + (*p++ - '0');

        int prec = -1;
        if (*p == '.') {
            p++;
            prec = 0;
            if (*p == '*') { prec = va_arg(ap, int); p++; }
            else while (*p >= '0' && *p <= '9') prec = prec * 10 + (*p++ - '0');
        }

        int lng = 0; /* 0=int,1=long,2=long long */
        for (;;) {
            if (*p == 'l') { lng++; p++; }
            else if (*p == 'z' || *p == 'j' || *p == 't') { lng = 2; p++; }
            else if (*p == 'h') { p++; }
            else break;
        }

        char conv = *p ? *p++ : 0;
        char buf[32];
        const char *out = buf;
        int outlen = 0;
        char sign = 0;

        switch (conv) {
            case '%': buf[0] = '%'; outlen = 1; break;
            case 'c': buf[0] = (char)va_arg(ap, int); outlen = 1; break;
            case 's': {
                out = va_arg(ap, const char *);
                if (out == 0) out = "(null)";
                outlen = 0;
                while (out[outlen] && (prec < 0 || outlen < prec)) outlen++;
                break;
            }
            case 'd': case 'i': {
                long long v = (lng >= 2) ? va_arg(ap, long long) : (long long)va_arg(ap, long);
                unsigned long long m;
                if (v < 0) { sign = '-'; m = (unsigned long long)(-(v + 1)) + 1ULL; }
                else { m = (unsigned long long)v; if (plus) sign = '+'; else if (space) sign = ' '; }
                char *e = buf + sizeof(buf); char *b = e;
                do { *--b = (char)('0' + (m % 10)); m /= 10; } while (m);
                out = b; outlen = (int)(e - b);
                break;
            }
            case 'u': case 'x': case 'X': case 'o': case 'p': {
                unsigned long long m;
                int base = 10; const char *digits = "0123456789abcdef";
                if (conv == 'x') base = 16;
                else if (conv == 'X') { base = 16; digits = "0123456789ABCDEF"; }
                else if (conv == 'o') base = 8;
                if (conv == 'p') { base = 16; alt = 1; m = (unsigned long long)(uintptr_t)va_arg(ap, void *); }
                else m = (lng >= 2) ? va_arg(ap, unsigned long long) : (unsigned long long)va_arg(ap, unsigned long);
                char *e = buf + sizeof(buf); char *b = e;
                do { *--b = digits[m % base]; m /= (unsigned)base; } while (m);
                if (alt && base == 16) { *--b = (conv == 'X') ? 'X' : 'x'; *--b = '0'; }
                out = b; outlen = (int)(e - b);
                break;
            }
            case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A':
                (void)va_arg(ap, long long); /* consume the (integer-register) double slot */
                out = "<float>"; outlen = 7;
                break;
            default:
                buf[0] = '%'; buf[1] = conv ? conv : '?'; outlen = 2;
                break;
        }

        int bodylen = outlen + (sign ? 1 : 0);
        int pad = width > bodylen ? width - bodylen : 0;

        if (!left && !zero) { while (pad-- > 0) { fputc(' ', f); total++; } }
        if (sign) { fputc(sign, f); total++; }
        if (!left && zero) { while (pad-- > 0) { fputc('0', f); total++; } }
        total += zkvm_emit(f, out, outlen);
        if (left) { while (pad-- > 0) { fputc(' ', f); total++; } }
    }

    return total;
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

/* Clean zkVM termination. ZisK only treats an ecall with a7 == 93
 * (CAUSE_EXIT) as "program end": its trap handler routes that to ROM_EXIT,
 * whose instruction carries the `end` flag the emulator waits for. musl's
 * exit()/_Exit() issue exit_group (94) instead, which ZisK does NOT recognise,
 * so the emulation stops "not completed". Override musl's terminators (via
 * --wrap=exit/_Exit/abort) to emit the real ZisK exit ecall. */
__attribute__((noreturn))
static void
zkvm_raw_exit(long code)
{
    register long a0 __asm__("a0") = code;
    register long a7 __asm__("a7") = 93; /* ZisK CAUSE_EXIT */
    __asm__ volatile("ecall" : : "r"(a0), "r"(a7) : "memory");
    for (;;) { } /* ecall ends the program; loop is just in case */
}

__attribute__((noreturn))
void
__wrap_exit(int code)
{
    zkvm_raw_exit(code);
}

__attribute__((noreturn))
void
__wrap__Exit(int code)
{
    zkvm_raw_exit(code);
}

__attribute__((noreturn))
void
__wrap_abort(void)
{
    zkvm_raw_exit(134); /* 128 + SIGABRT, conventional abort exit code */
}

int RhIsGCBridgeActive(void)
{
    return 0;
}

int
__wrap___stdio_write(int fd, const void *buf, int count)
{
    return -1;
}

int
__wrap_sysconf(int n)
{
    switch (n) {
    case 1:  /* _SC_CHILD_MAX */
        return 100;
    case 2:  /* _SC_CLK_TCK */
        return 100;
    case 30: /* _SC_PAGESIZE / _SC_PAGE_SIZE */
        return 4096;
    case 83: /* _SC_NPROCESSORS_CONF */
        return 1;
    case 84: /* _SC_NPROCESSORS_ONLN */
        return 1;
    case 85: /* _SC_PHYS_PAGES */
        return 65536;
    default:
        return 0;
    }
}

void *
__wrap_inline_bump_alloc_aligned(uint32_t bytes, uint32_t align)
{
    return __wrap___libc_malloc_impl(bytes);
}
