/**
 * @file
 * @brief Eva driver for the eh module - absence of runtime errors.
 *
 * The image symbols the module reads are linker-provided, so the driver
 * re-points them at local arrays and then exercises both shapes of the
 * answer: an image whose .eh_frame_hdr survived the link (two program
 * headers) and one where it did not (one program header).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>
#include <stddef.h>

/* Stand-ins for the linker script's image bounds. Addresses are all that
 * matter to the module; the contents are never read by it. */
static const char eva_image[4096];
static const char eva_eh_frame_hdr[256];

#define ZK_EH_IMAGE_SYMBOLS_DEFINED 1
#define __image_text_start   (eva_image)
#define __image_text_end     (eva_image + sizeof(eva_image))
#define __eh_frame_hdr_start (eva_eh_frame_hdr)
#define __eh_frame_hdr_end   (eva_eh_frame_hdr + sizeof(eva_eh_frame_hdr))

#include "../module.c"

/* Stands in for libunwind's findUnwindSectionsByPhdr: reads the block the
 * way the unwinder does and reports "found". */
static int
eva_callback(zk_dl_phdr_info *info, size_t size, void *data)
{
    (void)size;
    (void)data;

    uint16_t n = info->dlpi_phnum;
    uint64_t base = info->dlpi_addr;

    /*@ assert n >= 1; */
    for (uint16_t i = 0; i < n; i++)
    {
        uint64_t begin = base + info->dlpi_phdr[i].p_vaddr;
        uint64_t end = begin + info->dlpi_phdr[i].p_memsz;

        if (info->dlpi_phdr[i].p_type == ZK_PT_GNU_EH_FRAME && end > begin)
            return 1;
    }

    return 0;
}

/* The other verdict: a callback that rejects the object, so the "not found"
 * path through the dispatcher is analyzed too. */
static int
eva_callback_reject(zk_dl_phdr_info *info, size_t size, void *data)
{
    (void)info;
    (void)size;
    (void)data;

    return 0;
}

int
main(void)
{
    /* Whole path, as the runtime walks it at code-manager registration. */
    int found = __wrap_dl_iterate_phdr(&eva_callback, (void *)0);
    int rejected = __wrap_dl_iterate_phdr(&eva_callback_reject, (void *)0);

    /*@ assert found == 1; */
    /*@ assert rejected == 0; */
    /*@ assert zk_eh_phdrs[0].p_type == ZK_PT_LOAD; */
    /*@ assert zk_eh_phdrs[0].p_memsz == sizeof(eva_image); */
    /*@ assert zk_eh_phdrs[1].p_type == ZK_PT_GNU_EH_FRAME; */
    /*@ assert zk_eh_phdrs[1].p_memsz == sizeof(eva_eh_frame_hdr); */

    /* The stripped-unwind-tables shape: one header, no index to decode. */
    zk_elf64_phdr stripped[2];
    uint16_t n = zk_eh_fill_phdrs(stripped, 0x80000000u, 0x1000u, 0, 0);

    /*@ assert n == 1; */
    /*@ assert stripped[0].p_type == ZK_PT_LOAD; */

    /* Degenerate bounds must clamp, never wrap. */
    uint64_t reversed = zk_eh_extent(0x2000u, 0x1000u);
    uint64_t empty = zk_eh_extent(0x1000u, 0x1000u);
    uint64_t normal = zk_eh_extent(0x1000u, 0x1008u);

    /*@ assert reversed == 0; */
    /*@ assert empty == 0; */
    /*@ assert normal == 8; */

    return 0;
}
