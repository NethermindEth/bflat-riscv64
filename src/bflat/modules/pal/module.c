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

/* Verification harnesses (tests/verify) re-point the heap window at a
 * local array by defining these as macros before #include-ing this file. */
#ifndef ZK_HEAP_SYMBOLS_DEFINED
extern const char _kernel_heap_bottom[];
extern const char _kernel_heap_top[];
#endif

/*@ // Only three globalization/buffer-pool switches exist in the guest's
    // environment; everything else reads as unset. The returned "1" is a
    // string literal, valid forever.
    requires valid_read_string(var);
    assigns \nothing;
    ensures \result == \null || valid_read_string(\result);
*/
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

/*@ // The guest's cwd is always "/". __size is trusted to hold it.
    requires __size >= 2;
    requires \valid(__buf + (0 .. 1));
    assigns __buf[0 .. 1];
    ensures \result == __buf;
    ensures __buf[0] == '/' && __buf[1] == '\0';
*/
char *
__wrap_getcwd(char *__buf, int __size)
{
    strcpy(__buf, "/");
    return __buf;
}

/*@ assigns \nothing;
    ensures \result == 1;
*/
int
__wrap_getpid()
{
    return 1;
}

/*@ assigns \nothing;
    ensures \result == 1;
*/
int
__wrap_getegid()
{
    return 1;
}

/*@ assigns \nothing;
    ensures \result == 1;
*/
int
__wrap_geteuid()
{
    return 1;
}

/*@ // Single-CPU guest: only bit 0 (CPU 0) of the affinity mask is set.
    requires cpusetsize >= 0;
    requires \valid((unsigned char *)mask + (0 .. cpusetsize - 1));
    assigns ((unsigned char *)mask)[0 .. cpusetsize - 1];
    ensures \result == 0;
    ensures cpusetsize > 0 ==> ((unsigned char *)mask)[0] == 0x01;
    ensures \forall integer i; 1 <= i < cpusetsize ==>
        ((unsigned char *)mask)[i] == 0;
*/
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

/*@ assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_sched_getcpu(void)
{
    return 0;
}

/*@ // No file system in the guest: every open fails.
    assigns \nothing;
    ensures \result == -1;
*/
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

/* Companion cell 8 bytes below the bump pointer (also placed by the linker
 * script), holding the lowest address the heap may occupy. JIT-emitted inline
 * allocation loads it to bounds-check its bump before writing, so it has to be
 * published; it is written together with the bump pointer on the first
 * allocation, so the JIT's single guard can be trusted from the very first
 * managed allocation.
 *
 * CONTRACT WITH THE JIT (dotnet-riscv fixup 26): the value is published ONCE,
 * from .init_array - so before __libc_start_main reaches uBootstrap_main, and
 * therefore before any managed code exists - and is never written again. That
 * is what lets the JIT mark its load invariant and hoist it out of loops,
 * paying for the bounds check only once per method instead of once per
 * allocation. Publishing it later, or moving the heap floor at runtime, would
 * silently break every inline allocation site. */
extern uint8_t *g_zk_heap_floor;

/*@ // Publishes the heap floor for JIT-emitted inline allocation. Runs from
    // .init_array, before the runtime entry point, and exactly once.
    assigns g_zk_heap_floor;
    ensures g_zk_heap_floor == (uint8_t *)_kernel_heap_bottom;
*/
__attribute__((constructor))
static void
zk_publish_heap_floor(void)
{
    g_zk_heap_floor = (uint8_t *)_kernel_heap_bottom;
}

/*@ assigns \nothing;
    ensures \result % 8 == 0;
    ensures \result <= x < \result + 8;
*/
static inline uintptr_t
align_down_8_uintptr(uintptr_t x)
{
    return x & ~(uintptr_t)7;
}

/*
 * Heap mark/reset: used by the preinit warmup to drop ephemeral allocations
 * (block/tx/witness/EvmStack buffers from the warmup Execute) after type
 * loading and dispatch-cell resolution have happened. Caller is responsible
 * for ensuring no live reference points into the released region.
 */
/*@ assigns \nothing;
    ensures \result == (void *)g_zk_bump_ptr;
*/
void *
zk_heap_mark(void)
{
    return (void *)mem;
}

/*@ assigns g_zk_bump_ptr;

    behavior restore:
      assumes m != \null;
      ensures g_zk_bump_ptr == (uint8_t *)m;

    behavior noop:
      assumes m == \null;
      ensures g_zk_bump_ptr == \old(g_zk_bump_ptr);

    complete behaviors;
    disjoint behaviors;
*/
void
zk_heap_reset(void *m)
{
    if (m != 0)
        mem = (uint8_t *)m;
}

/*@ // Downward bump allocation. On success the returned block is 8-byte
    // aligned, lies inside [_kernel_heap_bottom, _kernel_heap_top) and is
    // preceded by an 8-byte size header; the bump pointer moves down past
    // header + payload and never back up. On exhaustion the result is NULL
    // and the bump pointer keeps its (possibly lazily initialised) value.
    // The header store is the only memory write besides the bump pointer;
    // it always lands inside the heap window.
    assigns g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];
    ensures \result == \null ||
        ((uintptr_t)\result % 8 == 0 &&
         (uint8_t *)\result >= (uint8_t *)_kernel_heap_bottom + 8 &&
         (uint8_t *)\result + n <= (uint8_t *)\old(
             g_zk_bump_ptr == \null ? (uint8_t *)_kernel_heap_top
                                    : g_zk_bump_ptr));
    ensures \result != \null ==>
        g_zk_bump_ptr == (uint8_t *)\result - 8;
*/
void *
__wrap___libc_malloc_impl(unsigned long n)
{
    /* Downward bump allocation. zkVM RAM is zero-initialised and the heap
     * never reuses memory, so every handed-out block is already all-zero.
     * Each block is preceded by an 8-byte size header (used by realloc). */
    if (mem == 0)
        mem = (uint8_t *)_kernel_heap_top;

    size_t req_aligned = ((size_t)n + 7u) & ~(size_t)7u;

    /* Bounds check: return NULL instead of writing below the heap and
     * silently corrupting the caller's stack (the historic pc=0 crash).
     * Compared against the remaining space, NOT via the subtracted
     * pointer: for n larger than mem's distance to zero the subtraction
     * would wrap past the top of the address space, sail over the old
     * `new_len < bottom` check and put the size header on a wild address. */
    uintptr_t avail = (uintptr_t)mem - (uintptr_t)_kernel_heap_bottom;
    if (req_aligned < (size_t)n ||      /* n + 7 wrapped */
        req_aligned + 8u < req_aligned || /* + header wrapped */
        req_aligned + 8u > avail)
        return NULL;

    uintptr_t new_tmp = align_down_8_uintptr((uintptr_t)mem - req_aligned);
    uintptr_t new_len = new_tmp - 8u;

    mem = (uint8_t *)new_len;
    *(uint64_t *)new_len = (uint64_t)req_aligned;
    return (void *)new_tmp;
}

/* __wrap_RhpNewFast is gone: object allocation now flows through the
 * runtime's own riscv64 AllocFast.S fast path (inline ee_alloc_context bump,
 * budget refilled by uGCHeap::Alloc) and the GcAllocInternal slow path. The
 * slow path ultimately lands in this file's wrapped malloc, so all managed
 * memory still comes from the same downward bump heap. */

/*@ // The bump heap never reuses memory: free is a no-op by design.
    assigns \nothing;
*/
void
__wrap___libc_free(void *p)
{
    (void)p;
}

/*@ // Grow-only realloc over the bump heap. A non-null p must have been
    // produced by __wrap___libc_malloc_impl, so its 8-byte size header at
    // p-8 is readable. Shrinking or fitting requests return p unchanged;
    // growth allocates a fresh block and copies the old payload.
    requires p == \null || \valid_read((uint64_t *)((uint8_t *)p - 8));
    assigns g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];

    behavior fresh:
      assumes p == \null;
      ensures \result == \null || (uintptr_t)\result % 8 == 0;

    behavior fits:
      assumes p != \null && *((uint64_t *)((uint8_t *)p - 8)) >= n;
      ensures \result == p;

    behavior grow:
      assumes p != \null && *((uint64_t *)((uint8_t *)p - 8)) < n;
      ensures \result == \null || (uintptr_t)\result % 8 == 0;

    complete behaviors;
    disjoint behaviors;
*/
void *
__wrap___libc_realloc(void *p, unsigned long n)
{
    void     *tmp;
    uint64_t *len;

    if (!p)
    {
        return __wrap___libc_malloc_impl(n);
    }

    len = (uint64_t *)((uint8_t *)p - 8u);

    if (*len >= (uint64_t)n)
    {
        /* Existing block is big enough */
        return p;
    }

    tmp = __wrap___libc_malloc_impl(n);
    if (!tmp)
    {
        return 0;
    }

    memcpy(tmp, p, (size_t)*len);
    return tmp;
}

/*
 * Optimized calloc
 */
/*@ // Zeroing is free: zkVM RAM is zero at boot and the heap never reuses
    // memory, so every block from the bump allocator is already all-zero.
    assigns g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];

    behavior overflow:
      assumes nmemb != 0 && nmemb * size > 0xFFFFFFFFFFFFFFFF;
      ensures \result == \null;

    behavior ok:
      assumes nmemb == 0 || nmemb * size <= 0xFFFFFFFFFFFFFFFF;
      ensures \result == \null || (uintptr_t)\result % 8 == 0;

    complete behaviors;
    disjoint behaviors;
*/
void *
__wrap_calloc(unsigned long nmemb, unsigned long size)
{
    size_t total = (size_t)nmemb * (size_t)size;

    if (nmemb != 0 && total / nmemb != size)
        return NULL;

    /* No memset: the bump allocator hands out fresh zero RAM. */
    return __wrap___libc_malloc_impl((unsigned long)total);
}

/*@ // Single-threaded guest: thread creation "succeeds" without creating
    // anything. Callers only check the status.
    assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_pthread_create(void *, void *, void *, void *)
{
    return 0;
}

/*@ assigns \nothing;
    ensures \result == 0;
*/
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
/*@ // Synthetic 8 MiB stack window anchored at the caller's frame: base is
    // the frame address rounded up to a page, limit is 8 MiB below it.
    requires \valid(stack_base) && \valid(stack_limit);
    assigns *stack_base, *stack_limit;
    ensures \result == 1;
    ensures (uintptr_t)*stack_base % 4096 == 0;
    ensures (uintptr_t)*stack_limit ==
        (uintptr_t)*stack_base - 8 * 1024 * 1024;
*/
int
__wrap__Z24PalGetMaximumStackBoundsPPvS0_(void **stack_base, void **stack_limit)
{
    uintptr_t sp = (uintptr_t)__builtin_frame_address(0);

    *stack_base  = (void *)((sp + 4095u) & ~(uintptr_t)4095u);
    *stack_limit = (void *)((uintptr_t)*stack_base - (8u * 1024u * 1024u));
    return 1;
}

/*@ // No clock in the deterministic guest; callers see a failed read and
    // fall back to their zero-time paths.
    assigns \nothing;
    ensures \result == -1;
*/
int
__wrap___clock_gettime(int clk, void *ts)
{
    return -1;
}

/*@ assigns \nothing;
    ensures \result == -1;
*/
int
__wrap_clock_gettime(int clk, void *ts)
{
    return -1;
}

/*@ assigns \nothing;
    ensures \result == 0;
*/
int
__wrap___malloc_allzerop(void *)
{
    return 0;
}

/*@ // Anonymous mappings degrade to bump allocations; every other mmap
    // caller is stubbed out elsewhere. Hint address, protection and flags
    // are ignored.
    assigns g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];
    ensures \result == \null || (uintptr_t)\result % 8 == 0;
*/
void *
__wrap_mmap(void *addr, int length, int prot, int flags,
            int fd, int offset)
{
    return __wrap___libc_malloc_impl(length);
}

/*@ assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_munmap(void *addr, int length)
{
    return 0;
}


/*@ assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_mlock(const void *addr, int len)
{
    return 0;
}

/*@ assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_munlock(const void *addr, int len)
{
    return 0;
}

/*@ assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_mlockall(int flags)
{
    return 0;
}

/*@ assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_munlockall(void)
{
    return 0;
}

/*@ // Nothing to yield to on a single-threaded guest.
    assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_sched_yield(void)
{
    return 0;
}

/*@ // No signals are ever delivered; oldact is deliberately not filled in
    // (no caller in the runtime reads it back).
    assigns \nothing;
    ensures \result == 0;
*/
int
__wrap_sigaction(int signum, void *act, void *oldact)
{
    return 0;
};

/*@ // SIG_DFL (null) is reported as the previous handler.
    assigns \nothing;
    ensures \result == \null;
*/
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

/*@ // Byte-exact emission via fputc. The stream footprint cannot be named
    // here (FILE is opaque in musl's public headers), so no assigns clause
    // is stated; the function writes nothing except through fputc.
    requires n >= 0;
    requires \valid_read(s + (0 .. n - 1));
    ensures \result == n;
*/
static int
zkvm_emit(FILE *f, const char *s, int n)
{
    int i;
    for (i = 0; i < n; i++)
        fputc((unsigned char)s[i], f);
    return n;
}

/*@ // Shallow contract: full printf semantics is out of scope for ACSL
    // (variadic arguments and the opaque FILE stream). The properties that
    // matter to callers: the format string is only read, the character
    // count is non-negative, and no floating-point value is ever touched
    // (float conversions consume an integer register slot and emit
    // "<float>").
    requires valid_read_string(fmt);
    ensures \result >= 0;
*/
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

/*@ // riscv_flush_icache (0x11b / 283) is a no-op on the zkVM (no icache);
    // everything else forwards to musl's real syscall with six argument
    // slots. The forwarded effects cannot be specified here.
    behavior flush_icache:
      assumes number == 0x11b;
      ensures \result == 0;
*/
long
__wrap_syscall(long number, ...)
{
    va_list args;
    long arg1, arg2, arg3, arg4, arg5, arg6;

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

/* Which zkVM this image is being linked for. pal.o is built once and shared
 * by every zkVM target, so the target-specific halt and console protocols are
 * selected at LINK time: each entry-point module (modules/zkvm_<target>/
 * module.S) defines one of these as an absolute symbol, and the weak
 * references below are non-NULL only in that link. No definition at all -
 * ZisK and the ZisK simulator, plus the host test/fuzz builds - means the
 * ZisK protocol, so those targets are bit-for-bit unaffected. The addresses
 * are never dereferenced. */
extern char __zkvm_target_sp1 __attribute__((weak));
extern char __zkvm_target_openvm __attribute__((weak));

/* SP1's own halt, from libzkevm (the zkEVM SDK repackaged as the bflat-sp1
 * bindings library). It commits the RUNNING public-values digest - the hash
 * SP1 maintains over everything written to FD_PUBLIC_VALUES - and only then
 * halts. Weak, because a guest that links no bindings package does not have
 * it; see zkvm_raw_exit for why that case is still correct. */
extern void zkvm_halt(unsigned char exit_code) __attribute__((weak));

#if defined(__riscv)
/* SP1 (sp1/crates/zkvm/entrypoint/src/syscalls/mod.rs): the syscall id goes
 * in t0, arguments in a0.. - NOT the Linux a7 convention. An id SP1 does not
 * know is a hard ExecutionError, so nothing may leak a stray ecall. */
#define SP1_HALT                    0x00
#define SP1_WRITE                   0x02
#define SP1_COMMIT                  0x10
#define SP1_COMMIT_DEFERRED_PROOFS  0x1A

/* SHA-256 of the empty byte string, as the eight little-endian u32 words
 * SP1's syscall_halt commits before halting. SP1 hashes everything the guest
 * writes to FD_PUBLIC_VALUES and commits the digest; this guest never writes
 * to that descriptor (console output goes to fd 1, see zkvm_console_write),
 * so the stream is empty and its digest is constant.
 *
 * It is only ever used when libzkevm is absent, and write_output - the only
 * thing that writes that descriptor - lives in libzkevm, so when this constant
 * is used the stream really is empty. Any future code path that writes public
 * values without libzkevm would invalidate it: the proof would carry a digest
 * that does not match its public values. Execution is unaffected either way;
 * only proving is. */
static const unsigned int sp1_empty_pv_digest[8] = {
    0x42c4b0e3u, 0x141cfc98u, 0xc8f4fb9au, 0x24b96f99u,
    0xe441ae27u, 0x4c939b64u, 0x1b9995a4u, 0x55b85278u
};
#endif /* __riscv */

/* Clean zkVM termination.
 *
 * ZisK only treats an ecall with a7 == 93 (CAUSE_EXIT) as "program end": its
 * trap handler routes that to ROM_EXIT, whose instruction carries the `end`
 * flag the emulator waits for. musl's exit()/_Exit() issue exit_group (94)
 * instead, which ZisK does NOT recognise, so the emulation stops "not
 * completed". Override musl's terminators (via --wrap=exit/_Exit/abort) to
 * emit the real target exit sequence.
 *
 * SP1 halts on its own HALT syscall (id in t0), and expects the public-values
 * and deferred-proof digests committed first. That commit has to hash whatever
 * the guest wrote to FD_PUBLIC_VALUES, so it belongs to whoever owns
 * write_output - libzkevm - and this function hands over to its zkvm_halt when
 * it is linked. The open-coded sequence below is the fallback for a guest
 * built without the bindings package: nothing can have written public values
 * then, because write_output lives in that same library, so the digest of the
 * empty stream is the correct one. OpenVM has no syscalls at all:
 * it ends on the TERMINATE custom instruction (custom-0, funct3 0), whose
 * exit code is an IMMEDIATE - so a runtime code collapses to OpenVM's own two
 * outcomes, 0 for success and 1 for failure (openvm::process::exit/panic). */
/*@ // Terminates the guest with the target's exit sequence. It ends the
    // program at the emulator level - invisible to ACSL, hence
    // ensures \false rather than an exits clause: this function never
    // returns and never reaches C's exit().
    assigns \nothing;
    ensures \false;
*/
__attribute__((noreturn))
static void
zkvm_raw_exit(long code)
{
#if defined(__riscv)
    if (&__zkvm_target_sp1) {
        if (zkvm_halt) {
            zkvm_halt((unsigned char)(code & 0xff));
        }
        /* No bindings package: the public-values stream is provably empty, so
         * the constant digest below is the digest of what was written. */
        for (int i = 0; i < 8; i++) {
            register long t0 __asm__("t0") = SP1_COMMIT;
            register long a0 __asm__("a0") = i;
            register long a1 __asm__("a1") = sp1_empty_pv_digest[i];
            __asm__ volatile("ecall" : : "r"(t0), "r"(a0), "r"(a1) : "memory");
        }
        /* No deferred proofs are verified by this guest, so every word of
         * that digest is zero - but SP1's own entrypoint commits the eight
         * words unconditionally, so do the same. */
        for (int i = 0; i < 8; i++) {
            register long t0 __asm__("t0") = SP1_COMMIT_DEFERRED_PROOFS;
            register long a0 __asm__("a0") = i;
            register long a1 __asm__("a1") = 0;
            __asm__ volatile("ecall" : : "r"(t0), "r"(a0), "r"(a1) : "memory");
        }
        register long t0 __asm__("t0") = SP1_HALT;
        register long a0 __asm__("a0") = code & 0xff;
        __asm__ volatile("ecall" : : "r"(t0), "r"(a0) : "memory");
    } else if (&__zkvm_target_openvm) {
        if (code == 0)
            __asm__ volatile(".insn i 0x0b, 0, x0, x0, 0" : : : "memory");
        else
            __asm__ volatile(".insn i 0x0b, 0, x0, x0, 1" : : : "memory");
    } else {
        register long a0 __asm__("a0") = code;
        register long a7 __asm__("a7") = 93; /* ZisK CAUSE_EXIT */
        __asm__ volatile("ecall" : : "r"(a0), "r"(a7) : "memory");
    }
    for (;;) { } /* the sequence ends the program; loop is just in case */
#else
    /* Host builds (fuzz harnesses) have no zkVM ecall; _Exit keeps the
     * same no-atexit, no-flush termination contract. */
    extern void _Exit(int) __attribute__((noreturn));
    _Exit((int)code);
#endif
}

/* Guest console. ZisK has no terminal device and swallows writes (rhp's
 * __wrap_SystemNative_Write returns "all consumed"); SP1 and OpenVM both
 * surface guest output on the host, so route it there instead. Called
 * through a WEAK reference from rhp/module.c, which is linked and unit
 * tested without pal.
 *
 * SP1: the WRITE syscall, which prints descriptors 1 and 2 on the host and
 * warns about any other (sp1/crates/core/executor/src/minimal/write.rs), so
 * the caller's descriptor is passed straight through.
 * OpenVM: the PrintStr phantom instruction - custom-0, funct3 3, imm 1, with
 * the buffer in the rd field and the length in rs1
 * (openvm/extensions/riscv/guest/src/io.rs raw_print_str_from_bytes). It has
 * a single output channel, so `fd` is ignored there. */
/*@ requires len >= 0;
    requires \valid_read(buf + (0 .. len - 1));
    assigns \nothing;
    ensures \result == len;
*/
int
zkvm_console_write(int fd, const char *buf, int len)
{
#if defined(__riscv)
    if (len > 0 && buf != (void *)0) {
        if (&__zkvm_target_sp1) {
            register long t0 __asm__("t0") = SP1_WRITE;
            register long a0 __asm__("a0") = fd;
            register const void *a1 __asm__("a1") = buf;
            register long a2 __asm__("a2") = len;
            __asm__ volatile("ecall"
                             : : "r"(t0), "r"(a0), "r"(a1), "r"(a2)
                             : "memory");
        } else if (&__zkvm_target_openvm) {
            __asm__ volatile(".insn i 0x0b, 3, %0, %1, 1"
                             : : "r"(buf), "r"(len) : "memory");
        }
    }
#else
    (void)fd;
    (void)buf;
#endif
    return len;
}

/*@ assigns \nothing;
    ensures \false;
*/
__attribute__((noreturn))
void
__wrap_exit(int code)
{
    zkvm_raw_exit(code);
}

/*@ assigns \nothing;
    ensures \false;
*/
__attribute__((noreturn))
void
__wrap__Exit(int code)
{
    zkvm_raw_exit(code);
}

/*@ assigns \nothing;
    ensures \false;
*/
__attribute__((noreturn))
void
__wrap_abort(void)
{
    zkvm_raw_exit(134); /* 128 + SIGABRT, conventional abort exit code */
}

/*@ // uGC has no bridge; the runtime's bridge paths stay dormant.
    assigns \nothing;
    ensures \result == 0;
*/
int RhIsGCBridgeActive(void)
{
    return 0;
}

/*@ assigns \nothing;
    ensures \result == -1;
*/
int
__wrap___stdio_write(int fd, const void *buf, int count)
{
    return -1;
}

/*@ assigns \nothing;

    behavior child_max:
      assumes n == 1;
      ensures \result == 100;
    behavior clk_tck:
      assumes n == 2;
      ensures \result == 100;
    behavior pagesize:
      assumes n == 30;
      ensures \result == 4096;
    behavior nprocessors_conf:
      assumes n == 83;
      ensures \result == 1;
    behavior nprocessors_onln:
      assumes n == 84;
      ensures \result == 1;
    behavior phys_pages:
      assumes n == 85;
      ensures \result == 65536;
    behavior other:
      assumes n != 1 && n != 2 && n != 30 && n != 83 && n != 84 && n != 85;
      ensures \result == 0;

    complete behaviors;
    disjoint behaviors;
*/
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

/*
 * SystemNative_AlignedAlloc (libSystem.Native) and any other over-aligned
 * request tail-call libc aligned_alloc/posix_memalign/memalign. On stock musl
 * (mallocng) those walk in-band chunk metadata that the bump allocator never
 * writes - the meta read lands near null and aborts the guest during runtime
 * startup (ziskemu: "Mem::read section not found addr 0x10"). Route them
 * through the bump heap instead. The heap grows downward and hands out
 * 8-byte-aligned blocks; for a stronger alignment, over-allocate by `align`
 * and round the block's low address up so at least `size` bytes remain above
 * the returned pointer. free() is a no-op, so the lost prefix never matters.
 */
/*@ // Alignments above 8 are honored by over-allocating `align` extra bytes
    // and rounding the block start up, so at least `size` bytes remain above
    // the returned pointer; free() is a no-op, so the lost prefix leaks by
    // design. Callers pass power-of-two alignments (C11 aligned_alloc
    // contract); the rounding mask is only meaningful for those.
    requires align <= 8 || (align & (align - 1)) == 0;
    assigns g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];

    behavior small_align:
      assumes align <= 8;
      ensures \result == \null || (uintptr_t)\result % 8 == 0;

    behavior big_align:
      assumes align > 8;
      ensures \result == \null || (uintptr_t)\result % align == 0;

    complete behaviors;
    disjoint behaviors;
*/
void *
__wrap_aligned_alloc(unsigned long align, unsigned long size)
{
    void     *p;
    uintptr_t a;

    if (align <= 8u)
        return __wrap___libc_malloc_impl(size);

    /* size + align must not wrap: a wrapped total would allocate a tiny
     * block and hand back a pointer with fewer than `size` bytes above it. */
    if (size + align < size)
        return NULL;

    p = __wrap___libc_malloc_impl(size + align);
    if (!p)
        return NULL;

    a = ((uintptr_t)p + (align - 1u)) & ~((uintptr_t)align - 1u);
    return (void *)a;
}

/*@ requires \valid(out);
    requires align <= 8 || (align & (align - 1)) == 0;
    assigns *out, g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];
    ensures \result == 0 || \result == 12;
    ensures \result == 0 ==> *out != \null;
    ensures \result == 12 ==> *out == \old(*out);
*/
int
__wrap_posix_memalign(void **out, unsigned long align, unsigned long size)
{
    void *p = __wrap_aligned_alloc(align, size);

    if (!p)
        return 12; /* ENOMEM */
    *out = p;
    return 0;
}

/*@ requires align <= 8 || (align & (align - 1)) == 0;
    assigns g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];
    ensures \result == \null ||
        (uintptr_t)\result % (align <= 8 ? 8 : align) == 0;
*/
void *
__wrap_memalign(unsigned long align, unsigned long size)
{
    return __wrap_aligned_alloc(align, size);
}

/*@ requires align <= 8 || (align & (align - 1)) == 0;
    assigns g_zk_bump_ptr;
    assigns ((uint8_t *)_kernel_heap_bottom)
        [0 .. (uint8_t *)_kernel_heap_top - (uint8_t *)_kernel_heap_bottom - 1];
    ensures \result == \null ||
        (uintptr_t)\result % (align <= 8 ? 8 : align) == 0;
*/
void *
__wrap_inline_bump_alloc_aligned(uint32_t bytes, uint32_t align)
{
    return __wrap_aligned_alloc(align, bytes);
}

/*
 * musl's scanf/float-parsing cluster (vfscanf.o -> floatscan.o -> fmodl.o)
 * is the only part of the stock hard-float (rv64gc) Alpine libc.a whose
 * members carry real F/D instructions - and ziskemu translates the guest's
 * whole .text, so one fld anywhere kills the image even if unreachable.
 * Wrapping the entry points keeps those archive members out of the link
 * entirely: every reference is redirected here, so the linker never pulls
 * the members in. The only in-image callers are the CGroup probes
 * (InitializeCGroup and friends), which on zisk can never open their
 * /proc//sys inputs anyway - EOF ("matched nothing") is the honest answer.
 */
/*@ // EOF, no conversions performed, nothing consumed or written.
    assigns \nothing;
    ensures \result == -1;
*/
int
__wrap_vfscanf(void *stream, const char *fmt, void *ap)
{
    (void)stream;
    (void)fmt;
    (void)ap;
    return -1; /* EOF: no conversions performed */
}

/*@ assigns \nothing;
    ensures \result == -1;
*/
int
__wrap___isoc99_vfscanf(void *stream, const char *fmt, void *ap)
{
    return __wrap_vfscanf(stream, fmt, ap);
}

/* strtod-family backend; only linked via wrappers that never run on zisk.
 * Returns 0 ("no number parsed"); soft-float long double, no F/D regs. */
/*@ // "No number parsed". The zero is a soft-float long double built in
    // integer registers - no F/D instructions are emitted for it.
    assigns \nothing;
    ensures \result == 0;
*/
long double
__wrap___floatscan(void *f, int prec, int pok)
{
    (void)f;
    (void)prec;
    (void)pok;
    return 0;
}
