/**
 * @file
 * @brief Eva driver for the eh module, stripped-unwind-tables shape.
 *
 * Same driver as eva_main.c, but the image reports an empty .eh_frame_hdr -
 * what a --remove-eh link leaves behind. The module must then describe the
 * load segment alone, so the unwinder's lookup fails cleanly instead of
 * decoding whatever occupies the range.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <inttypes.h>
#include <stddef.h>

static const char eva_image[4096];
static const char eva_eh_frame_hdr[256];

#define ZK_EH_IMAGE_SYMBOLS_DEFINED 1
#define __image_text_start   (eva_image)
#define __image_text_end     (eva_image + sizeof(eva_image))
/* Empty extent: start == end, as an emitted-but-stripped section looks. */
#define __eh_frame_hdr_start (eva_eh_frame_hdr)
#define __eh_frame_hdr_end   (eva_eh_frame_hdr)

#include "../module.c"

/* Same shape as libunwind's findUnwindSectionsByPhdr: reports "found" only
 * when a non-empty unwind index is present. */
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

int
main(void)
{
    int found = __wrap_dl_iterate_phdr(&eva_callback, (void *)0);

    /* No unwind index in the image: the lookup must fail, and it must fail
     * because the module reported one header, not because it pointed the
     * unwinder at a stripped range. */
    /*@ assert found == 0; */
    /*@ assert zk_eh_phdrs[0].p_type == ZK_PT_LOAD; */
    /*@ assert zk_eh_phdrs[0].p_memsz == sizeof(eva_image); */

    return 0;
}
