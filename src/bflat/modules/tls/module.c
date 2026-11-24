/**
 * @file
 * @brief Simple TLS implementation that doesn't do a lot of ELF magic and can
 *        work with zkVMs.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>
#include <stddef.h>
#include <string.h>

/* Constants from musl. Weird, in fact must be inferred automatically. */
#define HEAP_SIZE       (100 * 1024)
#define PTHREAD_SIZE    (200)
#define TLS_SIZE        (2 * sizeof(void *) + PTHREAD_SIZE)
#define TLS_CNT         (1)
#define TLS_ALIGN       (4)

/*
 * The RISC-V psABI biases a DTPREL offset by -0x800 (musl's DTP_OFFSET), so
 * the offset of a __tls_get_addr argument is that of the variable less
 * 0x800. The module is built for RISC-V only.
 */
#define DTP_OFFSET      ((size_t)0x800)

static uint8_t  tls_storage_static[HEAP_SIZE]
    __attribute__((aligned(64)));

/*
 * tls_base points to the actual TLS data area (after pthread data).
 * tp points to the pthread data area (start of our TLS block).
 *
 * These are initialized lazily on first access to ensure they're valid
 * even if __tls_get_addr is called before __init_tls.
 */
static uint8_t *tls_base = NULL;
static uint8_t *tp = NULL;
static volatile int tls_initialized = 0;

extern uint8_t  __tdata_load[] __attribute__((weak));
extern const char __tdata_len[] __attribute__((weak));
extern const char __tbss_len[] __attribute__((weak));

/*
 * The link hands over the .tdata image and the two lengths as symbol
 * addresses. Under Frama-C the three are plain constants instead: WP has no
 * logic for a pointer-to-integer cast, and the values are the link's either
 * way. The compiled code reads the symbols, as the #else branch spells.
 */
#ifdef __FRAMAC__
extern uint8_t *const tls_link_tdata;
extern const uintptr_t tls_link_tdata_len;
extern const uintptr_t tls_link_tbss_len;
#else
#define tls_link_tdata     (__tdata_load)
#define tls_link_tdata_len ((uintptr_t)__tdata_len)
#define tls_link_tbss_len  ((uintptr_t)__tbss_len)
#endif

/* The thread pointer register is not C-visible memory; this ghost variable
 * stands for it. */
//@ ghost void *tls_thread_pointer;

/*@ // Loads tp, the thread pointer register, with its argument.
    terminates \true;
    exits \false;
    assigns tls_thread_pointer \from tp;
    ensures tls_thread_pointer == tp;
*/
extern int __set_thread_area(void *tp);

#ifdef __FRAMAC__
/*@ // The compiler builtin: an illegal instruction, never returns.
    terminates \false;
    assigns \nothing;
    ensures \false;
*/
extern void __builtin_trap(void);
#endif

/*@ // Stops the guest: an image whose TLS does not fit the static block
    // cannot run correctly.
    terminates \false;
    assigns \nothing;
    ensures \false;
*/
__attribute__((noreturn, noinline, cold))
static void
tls_layout_trap(void)
{
    __builtin_trap();
}

/*@ // What the link provides: .tdata and .tbss fit in the block after the
    // pthread area, and the .tdata image, wherever it exists, is readable
    // and lies outside the block.
    predicate tls_layout =
        tls_link_tdata_len + tls_link_tbss_len <= (100 * 1024) - 200 &&
        (tls_link_tdata_len != 0 && tls_link_tdata != \null ==>
            \valid_read(tls_link_tdata + (0 .. tls_link_tdata_len - 1)) &&
            \separated(tls_link_tdata + (0 .. tls_link_tdata_len - 1),
                       &tls_storage_static[0 .. (100 * 1024) - 1]));

    // The pointers are either unset or point into the static block, the
    // flag is 0 or 1, and once the block is initialised both are set.
    predicate tls_state =
        (tls_initialized == 0 || tls_initialized == 1) &&
        (tls_base == \null || tls_base == &tls_storage_static[200]) &&
        (tls_initialized != 0 ==>
            tp == &tls_storage_static[0] && tls_base == &tls_storage_static[200]);
*/

/*
 * Internal function to ensure TLS is initialized.
 * Can be called multiple times safely.
 * This function is designed to be safe to call from any context.
 */
/*@ // Idempotent lazy init of the single static TLS block. The lengths of
    // .tdata/.tbss arrive as link-time symbol ADDRESSES (__tdata_len,
    // __tbss_len); the linker script guarantees tdata+tbss fits
    // HEAP_SIZE - PTHREAD_SIZE, which tls_layout states. The body checks
    // the size as well and traps when a link breaks it.
    requires tls_layout;
    requires tls_state;
    assigns tls_initialized, tp, tls_base,
            tls_storage_static[0 .. (100 * 1024) - 1];
    ensures tls_state;
    ensures tls_initialized == 1;
    ensures tp == &tls_storage_static[0];
    ensures tls_base == &tls_storage_static[200];

    behavior already_initialized:
      assumes tls_initialized != 0;
      assigns \nothing;

    behavior first_call:
      assumes tls_initialized == 0;
      assigns tls_initialized, tp, tls_base,
              tls_storage_static[0 .. (100 * 1024) - 1];
      // .tdata copied in after the pthread area, .tbss zeroed after it.
      ensures tls_link_tdata_len != 0 && tls_link_tdata != \null ==>
          \forall integer i; 0 <= i < tls_link_tdata_len ==>
              tls_storage_static[200 + i] == tls_link_tdata[i];
      // memset is the libc logic predicate: every byte of the range is 0.
      ensures memset((char *)&tls_storage_static[200 + tls_link_tdata_len], 0,
                     tls_link_tbss_len);

    complete behaviors;
    disjoint behaviors;
*/
extern
void
ensure_tls_initialized(void)
{
    /* Quick check without memory barrier for performance */
    if (tls_initialized)
        return;

    /* Set up the pointers first - this is safe even if called concurrently
     * since we're always setting them to the same values */
    tp = tls_storage_static;
    tls_base = tls_storage_static + PTHREAD_SIZE;

    uintptr_t tdata_len = tls_link_tdata_len;
    uintptr_t tbss_len = tls_link_tbss_len;

    /*
     * The linker script keeps .tdata + .tbss within the block; check it here
     * too, so that a different link stops the guest instead of writing past
     * tls_storage_static. Written so that the sum cannot wrap.
     */
    if (tdata_len > HEAP_SIZE - PTHREAD_SIZE ||
        tbss_len > HEAP_SIZE - PTHREAD_SIZE - tdata_len)
        tls_layout_trap();

    /*
     * Copy initial TLS data if present (.tdata section).
     * This contains initialized thread-local variables.
     */
    if (tdata_len != 0 && tls_link_tdata != NULL)
        memcpy(tls_base, tls_link_tdata, tdata_len);

    /*
     * Zero-initialize .tbss area (uninitialized thread-local variables).
     * The storage is already zeroed since it's in .data section, but we
     * do this explicitly for correctness.
     */
    if (tbss_len != 0)
        memset(tls_base + tdata_len, 0, tbss_len);

    /* Mark as initialized */
    tls_initialized = 1;
}

/*@ // Installs p + PTHREAD_SIZE as the thread pointer (musl keeps pthread
    // data below tp). __set_thread_area only writes the tp register, which
    // has no C-visible footprint; tls_thread_pointer models it.
    requires p != \null;
    assigns tls_thread_pointer;
    ensures tls_thread_pointer == (char *)p + 200;
    ensures \result == 0;
*/
uint8_t
__wrap___init_tp(void *p)
{
    __set_thread_area((char *)p + PTHREAD_SIZE);
    return 0;
}

/*@ // There is exactly one thread, so "copying" TLS just hands out the
    // shared static block; mem is ignored.
    requires tls_layout;
    requires tls_state;
    assigns tls_initialized, tp, tls_base,
            tls_storage_static[0 .. (100 * 1024) - 1];
    ensures tls_initialized == 1;
    ensures tls_state;
    ensures \result == tp == &tls_storage_static[0];
*/
uint8_t *
__wrap___copy_tls(uint8_t *mem)
{
    /* Ensure TLS data is initialized */
    ensure_tls_initialized();
    return tp;
}

/*@ // Full startup path: initialise the block and install the thread
    // pointer; the auxv argument is unused. The thread pointer ends up at
    // the TLS data, after the pthread area.
    requires tls_layout;
    requires tls_state;
    assigns tls_initialized, tp, tls_base, tls_thread_pointer,
            tls_storage_static[0 .. (100 * 1024) - 1];
    ensures tls_state;
    ensures tls_initialized == 1;
    ensures tls_thread_pointer == (void *)tls_base;
    ensures tls_base == &tls_storage_static[200];
*/
void
__wrap___init_tls(size_t *aux)
{
    ensure_tls_initialized();
    __wrap___init_tp(__wrap___copy_tls(tp));
}

/* The first lookup may precede __init_tls. Keeping initialization out of line
 * lets subsequent lookups avoid saving registers for memcpy/memset. */
/*@
    requires v == \null || \valid_read(v + (0 .. 1));
    requires tls_layout;
    requires tls_state;
    assigns tls_initialized, tp, tls_base,
            tls_storage_static[0 .. (100 * 1024) - 1];
    ensures tls_state;
    ensures tls_initialized == 1;
    ensures tls_base == &tls_storage_static[200];
    ensures \result == (v == \null ? (void *)tls_base :
                                    (void *)(tls_base + (size_t)(v[1] + DTP_OFFSET)));
*/
static __attribute__((noinline, cold))
void *
tls_get_addr_first(size_t *v)
{
    ensure_tls_initialized();

    if (v != NULL)
        return (void*)(tls_base + (size_t)(v[1] + DTP_OFFSET));
    return tls_base;
}

/*@ // v = {module id, offset}; with one module and one thread the module
    // id is irrelevant and the answer is always tls_base + offset, the
    // offset being biased by -DTP_OFFSET. A null
    // v yields the TLS base itself (non-standard convenience used by the
    // runtime's early probes).
    requires v == \null || \valid_read(v + (0 .. 1));
    requires tls_layout;
    requires tls_state;
    assigns tls_initialized, tp, tls_base,
            tls_storage_static[0 .. (100 * 1024) - 1];
    ensures tls_state;
    ensures tls_base == &tls_storage_static[200];

    behavior indexed:
      assumes v != \null;
      ensures \result == (void *)(tls_base + (size_t)(v[1] + DTP_OFFSET));

    behavior base:
      assumes v == \null;
      ensures \result == (void *)tls_base;

    complete behaviors;
    disjoint behaviors;
*/
void *
__wrap___tls_get_addr(size_t *v)
{
    uint8_t *base = tls_base;
    if (__builtin_expect(base == NULL, 0))
        return tls_get_addr_first(v);

    if (v != NULL)
        return (void*)(base + (size_t)(v[1] + DTP_OFFSET));
    return base;
}
