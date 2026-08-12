/**
 * @file
 * @brief EH support - synthetic program headers for the NativeAOT unwinder
 *
 * UnixNativeCodeManager caches the DWARF unwind sections once, at code
 * manager registration, through libunwind's findUnwindSections. On Linux
 * that path is dl_iterate_phdr: it walks the loader's program headers
 * looking for a PT_LOAD covering the queried PC and a PT_GNU_EH_FRAME
 * carrying .eh_frame_hdr. The guest has neither. ZisK loads no program
 * headers at all - it materializes memory from segments and jumps to the
 * entry point - so there is no loader state, no auxv, and the ELF header
 * is not even mapped (the first PT_LOAD starts at file offset 0x1000).
 * musl's dl_iterate_phdr walks its `dso` list, empty in a static guest,
 * and reports nothing; every FindMethodInfo then fails and a throw
 * fail-fasts before the handler search.
 *
 * Nothing in libunwind requires the headers to be real: it reads only what
 * the callback hands it. This module describes the image from linker-script
 * symbols instead - one PT_LOAD over the executable range (including
 * __managedcode and __unbox) and one PT_GNU_EH_FRAME over .eh_frame_hdr.
 * The image is not position independent, so dlpi_addr is 0 and the vaddrs
 * are already absolute.
 *
 * Not linked when the build drops its EH data (--remove-eh, module_params.yml):
 * musl's stub stays and the runtime fails fast on throw.
 *
 * Verified by verify/run_wp.sh: WP proves the contracts below (functional
 * properties plus RTE) for everything that builds the answer, and Eva
 * proves the whole module free of runtime errors end to end, including the
 * dispatcher's indirect call, which it resolves against the driver's
 * callback. WP alone cannot reason about a caller-supplied function
 * pointer, which is why the two analyses are split this way.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>
#include <stddef.h>

/* Verification harnesses (verify/) re-point the image window at local
 * arrays by defining these as macros before #include-ing this file. */
#ifndef ZK_EH_IMAGE_SYMBOLS_DEFINED
/* Executable range, from the module's linker script. Covers .text plus the
 * __managedcode/__unbox sections: every PC the unwinder resolves is in it. */
extern const char __image_text_start[];
extern const char __image_text_end[];
/* .eh_frame_hdr, the FDE binary-search index. Empty when the link dropped
 * the unwind tables. */
extern const char __eh_frame_hdr_start[];
extern const char __eh_frame_hdr_end[];
#endif

#define ZK_PT_LOAD         1u
#define ZK_PT_GNU_EH_FRAME 0x6474e550u
#define ZK_PF_X            1u
#define ZK_PF_R            4u

/* ELF64 program header, as the unwinder expects to read it. */
typedef struct {
    uint32_t p_type;
    uint32_t p_flags;
    uint64_t p_offset;
    uint64_t p_vaddr;
    uint64_t p_paddr;
    uint64_t p_filesz;
    uint64_t p_memsz;
    uint64_t p_align;
} zk_elf64_phdr;

/* Layout-compatible with glibc/musl struct dl_phdr_info. libunwind reads
 * dlpi_addr, dlpi_name, dlpi_phdr and dlpi_phnum only; the trailing members
 * exist so the size handed to the callback is honest. */
typedef struct {
    uint64_t             dlpi_addr;
    const char          *dlpi_name;
    const zk_elf64_phdr *dlpi_phdr;
    uint16_t             dlpi_phnum;
    unsigned long long   dlpi_adds;
    unsigned long long   dlpi_subs;
    size_t               dlpi_tls_modid;
    void                *dlpi_tls_data;
} zk_dl_phdr_info;

typedef int (*zk_phdr_cb)(zk_dl_phdr_info *info, size_t size, void *data);

/* Filled per call rather than statically initialized: the bounds are
 * link-time addresses, not constant expressions. The guest is single
 * threaded, so one shared buffer is enough. */
static zk_elf64_phdr zk_eh_phdrs[2];

/*@ // Describes the image as at most two program headers: the executable
    // range always, the unwind index only when the link kept it. Every
    // field the unwinder reads is pinned down here.
    requires \valid(phdrs + (0 .. 1));
    assigns phdrs[0 .. 1];
    ensures \result == 1 || \result == 2;
    ensures \result == 2 <==> hdr_size > 0;
    ensures phdrs[0].p_type == ZK_PT_LOAD;
    ensures phdrs[0].p_flags == (ZK_PF_R | ZK_PF_X);
    ensures phdrs[0].p_vaddr == text_start;
    ensures phdrs[0].p_paddr == text_start;
    ensures phdrs[0].p_memsz == text_size;
    ensures phdrs[0].p_filesz == text_size;
    ensures hdr_size > 0 ==> phdrs[1].p_type == ZK_PT_GNU_EH_FRAME;
    ensures hdr_size > 0 ==> phdrs[1].p_flags == ZK_PF_R;
    ensures hdr_size > 0 ==> phdrs[1].p_vaddr == hdr_start;
    ensures hdr_size > 0 ==> phdrs[1].p_paddr == hdr_start;
    ensures hdr_size > 0 ==> phdrs[1].p_memsz == hdr_size;
    ensures hdr_size > 0 ==> phdrs[1].p_filesz == hdr_size;
*/
static uint16_t
zk_eh_fill_phdrs(zk_elf64_phdr *phdrs, uint64_t text_start, uint64_t text_size,
                 uint64_t hdr_start, uint64_t hdr_size)
{
    phdrs[0].p_type = ZK_PT_LOAD;
    phdrs[0].p_flags = ZK_PF_R | ZK_PF_X;
    phdrs[0].p_offset = 0;
    phdrs[0].p_vaddr = text_start;
    phdrs[0].p_paddr = text_start;
    phdrs[0].p_filesz = text_size;
    phdrs[0].p_memsz = text_size;
    phdrs[0].p_align = 8;

    /* An empty .eh_frame_hdr means the image carries no unwind index (the
     * link stripped it): report the load segment alone, so libunwind fails
     * the lookup instead of decoding whatever now occupies the range. */
    if (hdr_size == 0)
        return 1;

    phdrs[1].p_type = ZK_PT_GNU_EH_FRAME;
    phdrs[1].p_flags = ZK_PF_R;
    phdrs[1].p_offset = 0;
    phdrs[1].p_vaddr = hdr_start;
    phdrs[1].p_paddr = hdr_start;
    phdrs[1].p_filesz = hdr_size;
    phdrs[1].p_memsz = hdr_size;
    phdrs[1].p_align = 4;

    return 2;
}

/*@ // Section extent, clamped: a link that emitted the section empty (or,
    // defensively, out of order) yields 0 rather than a wrapped size.
    assigns \nothing;
    ensures \result == (end > start ? end - start : 0);
*/
static uint64_t
zk_eh_extent(uint64_t start, uint64_t end)
{
    if (end <= start)
        return 0;

    return end - start;
}

/*@ // Builds the info block over this module's phdr storage. The image is
    // not position independent, so dlpi_addr is 0 and the vaddrs are
    // absolute; dlpi_phnum matches what the fill reported.
    requires \valid(info);
    assigns *info, zk_eh_phdrs[0 .. 1];
    ensures info->dlpi_addr == 0;
    ensures info->dlpi_phdr == &zk_eh_phdrs[0];
    ensures info->dlpi_phnum == 1 || info->dlpi_phnum == 2;
    ensures info->dlpi_tls_modid == 0;
    ensures zk_eh_phdrs[0].p_type == ZK_PT_LOAD;
*/
static void
zk_eh_build_info(zk_dl_phdr_info *info)
{
    uint64_t text_start = (uint64_t)(uintptr_t)__image_text_start;
    uint64_t text_end = (uint64_t)(uintptr_t)__image_text_end;
    uint64_t hdr_start = (uint64_t)(uintptr_t)__eh_frame_hdr_start;
    uint64_t hdr_end = (uint64_t)(uintptr_t)__eh_frame_hdr_end;

    info->dlpi_phnum = zk_eh_fill_phdrs(zk_eh_phdrs, text_start,
                                        zk_eh_extent(text_start, text_end),
                                        hdr_start,
                                        zk_eh_extent(hdr_start, hdr_end));
    info->dlpi_addr = 0;
    info->dlpi_name = "";
    info->dlpi_phdr = &zk_eh_phdrs[0];
    info->dlpi_adds = 1;
    info->dlpi_subs = 0;
    info->dlpi_tls_modid = 0;
    info->dlpi_tls_data = 0;
}

/*@ // One synthetic object, so the callback runs exactly once and its
    // verdict is the result - what dl_iterate_phdr does when the iteration
    // stops on the first entry.
    requires \valid_function(callback);
    assigns zk_eh_phdrs[0 .. 1];
*/
int
__wrap_dl_iterate_phdr(zk_phdr_cb callback, void *data)
{
    zk_dl_phdr_info info;

    zk_eh_build_info(&info);

    return callback(&info, sizeof(info), data);
}
