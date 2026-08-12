/**
 * @file
 * @brief Reduced Zisk-like ELF implementation linker script
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
ENTRY(_start)

PHDRS {
    text PT_LOAD FLAGS(5);    /* R+X */
    data PT_LOAD FLAGS(6);    /* R+W */
    rodata PT_LOAD FLAGS(6);
    heap PT_LOAD FLAGS(6);    /* R+W; fixed high RAM window, see .heap below */
    tls PT_TLS;
}

SECTIONS
{
  /* Base the image at 0x50000000, not the usual 0x10000: pal/module.c reaches
   * the heap symbols (g_zk_bump_ptr at 0xBFFEFFF8, _kernel_heap_top/_bottom) with
   * PC-relative auipc/addi pairs, whose reach is +/-2GB. From 0x10000 the fixed
   * cell at 0xBFFEFFF8 is ~2.68GB away and R_RISCV_PCREL_HI20 overflows; 0x50000000
   * keeps the whole 0x50000000..0xBFFF0000 span within +/-2GB. (Real zisk avoids
   * this by placing text in ROM at 0x80000000.) */
  . = 0x50000000;

  .text : ALIGN(16)
  {
    /* Image bounds handed to libunwind by pal/module.c's synthetic
     * PT_LOAD; see zkvm_zisk/script.ld for the full story. __managedcode
     * and __unbox are pulled in explicitly (they would otherwise be placed
     * as orphans AFTER this section) so that every managed PC the unwinder
     * resolves really is inside the segment we describe. */
    __image_text_start = .;
    KEEP(*(.text.init))
    *(.text .text.*)
    . = ALIGN(16);

    __start___managedcode = .;
    KEEP(*(__managedcode))
    __stop___managedcode = .;
    . = ALIGN(4);

    __start___unbox = .;
    KEEP(*(__unbox))
    __stop___unbox = .;
    . = ALIGN(16);

    __image_text_end = .;
  } :text

  .rodata : ALIGN(0x1000)
  {
    *(.rodata .rodata.*)
  } :rodata

  /* DWARF unwind tables, named for the synthetic PT_GNU_EH_FRAME. The sim
   * leg keeps them unconditionally (patch_elf.py runs on zisk only), so the
   * unwinder sees the same layout on both legs. */
  .eh_frame_hdr ALIGN(8) :
  {
    __eh_frame_hdr_start = .;
    KEEP(*(.eh_frame_hdr))
    __eh_frame_hdr_end = .;
  } :rodata

  .eh_frame ALIGN(8) :
  {
    __eh_frame_start = .;
    KEEP(*(.eh_frame .eh_frame.*))
    __eh_frame_end = .;
  } :rodata

  .data : ALIGN(16)
  {
    /* Opaque DSO token for __cxa_atexit registrations from .NET 11 GC
     * static initializers; see zkvm_zisk/script.ld for the full story. */
    PROVIDE_HIDDEN(__dso_handle = .);
    . += 8;
    *(.data .data.*)
    *(.data.__security_cookie)
    *(.rodata)
    *(.rodata.*)
    *(.init_array .init_array.*)
    *(.got .got .got.*)
  } :data

  .bss (NOLOAD) : ALIGN(16)
  {
    *(.bss .bss.* COMMON)
  } :data

  .stack ALIGN(16) :
  {
      _stack_bottom = .;
      . = . + (64 * 1024);
      _init_stack_top = .;
  } :data

  .tdata ALIGN(8) : {
    __tdata_start = .;
    *(.tdata .tdata.* .gnu.linkonce.td.*)
    __tdata_end = .;
  } :tls

  __tdata_load = LOADADDR(.tdata);
  __tdata_len = SIZEOF(.tdata);

  .tbss (NOLOAD) : {
    __tbss_start = .;
    *(.tbss .tbss.* .gnu.linkonce.tb.*)
    __tbss_end = .;
  } :tls

  __tbss_len = SIZEOF(.tbss);

  _global_pointer = .;

  /* zkVM per-site inline allocator: the JIT bakes the bump-pointer cell ADDRESS
   * (0xBFFEFFF8 = top 8 bytes of the real zisk RAM map, see modules/zkvm_zisk/
   * script.ld: ORIGIN(ram)+LENGTH(ram)-8) directly into machine code as an
   * lui/addiw/slli immediate, with no relocation. So zisk_sim must expose
   * g_zk_bump_ptr at that EXACT address, otherwise the first inline allocation
   * dereferences an unmapped page and faults. Mirror the zisk RAM window
   * (0xA0020000..0xC0000000) as its own NOLOAD segment: the Linux loader maps it
   * as zero pages (p_memsz > p_filesz), matching zkVM RAM being zero at boot, so
   * g_zk_bump_ptr starts at 0 and is lazily initialised to _kernel_heap_top on
   * first use exactly as on real zisk. NOLOAD also keeps the binary small (the
   * heap is no longer materialised in the file). */
  . = 0xA0020000;
  .heap (NOLOAD) : ALIGN(16)
  {
    _kernel_heap_bottom = .;
    . = 0xBFFEFFF0;        /* heap top, 16 below RAM end (0xBFFF0000) */
    _kernel_heap_top = .;
    PROVIDE(g_zk_bump_ptr = 0xBFFEFFF8);
  } :heap

  PROVIDE(_kernel_heap_size = _kernel_heap_top - _kernel_heap_bottom);
}
