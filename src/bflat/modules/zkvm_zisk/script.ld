/**
 * @file
 * @brief ZKVM Zisk implementation linker script
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
OUTPUT_FORMAT("elf64-littleriscv")
OUTPUT_ARCH("riscv")
ENTRY(_start)

MEMORY {
  /* biosrom (xa) : ORIGIN = 0x00001000, LENGTH = 0x1000 */
  rom   (xa) : ORIGIN = 0x80000000, LENGTH = 0x10000000
  /*
   * Zisk RAM map (zisk/core/src/mem.rs, v1.3):
   *   RAM_ADDR            = 0xa0000000   program stack, STACK_SIZE = 4 MiB
   *   SYS_ADDR            = 0xa0400000   registers, UART, DMA params, CSRs
   *   OUTPUT_ADDR         = 0xa0410000   public output, OUTPUT_MAX_SIZE 128 KiB
   *   general-purpose RAM = 0xa0430000
   *   LIB_RAM             = 0xbffc0000   192 KiB, ZisK 1.4 library calls (CSRs 0x850-0x8bf)
   *   FLOAT_LIB_RAM_ADDR  = 0xbfff0000
   *   RAM end             = 0xc0000000
   * The BIOS reads SYS and publishes OUTPUT at exit, so neither the stack nor
   * anything linked here may overlap them. RAM ends below LIB_RAM, which ZisK
   * 1.4 reserves as soon as a program makes a library call, so the heap and the
   * cells at its top never share it.
   */
  stack (wa)  : ORIGIN = 0xa0000000, LENGTH = 0x400000
  ram   (wxa) : ORIGIN = 0xa0430000, LENGTH = 0xbffc0000 - 0xa0430000
}

PHDRS {
  /* bios PT_LOAD FLAGS(1); */
  text PT_LOAD FLAGS(1);
  rodata PT_LOAD FLAGS(4);
  data PT_LOAD FLAGS(6);
  tls PT_TLS;
}

SECTIONS
{
  .text : {
    /* Image bounds handed to libunwind by pal/module.c's synthetic
     * PT_LOAD; every managed PC the unwinder resolves lives in here. */
    __image_text_start = .;

    /* First: Bootstrap code */
    *(.text.init)
    . = ALIGN(4);

    /* Second: Critical entry points (these MUST come early) */
    *(.text._start)
    *(.text.__managed__Main)
    *(.text.*__managed__Main*)
    *(.text.*MainMethodWrapper*)
    *(.text.*Program__Main*)
    . = ALIGN(4);

    /* Fourth: Any managed code sections */
    __start___managedcode = .;
    KEEP(*(__managedcode))
    __stop___managedcode  = .;
    . = ALIGN(4);

    __start___unbox = .;
    KEEP(*(__unbox))
    __stop___unbox  = .;

    /* Third: All other program code */
    *(.text .text.*)
    . = ALIGN(4);

    /* Ensure proper alignment and padding */
    . = ALIGN(16);
    __image_text_end = .;
  } >rom AT>rom :text

  .rodata ALIGN(16) : {
    *(.rodata .rodata.*)
  } >rom AT>rom :rodata

  /* DWARF unwind tables. Placed explicitly (they would otherwise land as
   * orphan sections) so their bounds are named: pal/module.c reports
   * .eh_frame_hdr as a synthetic PT_GNU_EH_FRAME, which is how libunwind
   * finds the FDE binary-search index. Dropped from the image by
   * patch_elf.py only when the build passes --remove-eh. */
  .eh_frame_hdr ALIGN(8) : {
    __eh_frame_hdr_start = .;
    KEEP(*(.eh_frame_hdr))
    __eh_frame_hdr_end = .;
  } >rom AT>rom :rodata

  .eh_frame ALIGN(8) : {
    __eh_frame_start = .;
    KEEP(*(.eh_frame .eh_frame.*))
    __eh_frame_end = .;
  } >rom AT>rom :rodata

  .modules ALIGN(16) : {
    __start___modules = .;
    KEEP(*(__modules))
    __stop___modules  = .;
  } >ram AT>rom :data

  /* .init_array and .got are placed explicitly, ahead of the TLS template
   * below. Left as orphans, ld drops them right after .tbss, and every
   * PROGBITS section that follows a NOBITS one inside a PT_LOAD breaks the
   * segment's `vaddr - offset` congruence: ZisK >= 1.1.0 loads the image from
   * program headers only (transpilers/common/src/elf_extraction.rs) and would
   * then read .got from the wrong file bytes. Keep every PROGBITS section
   * ahead of .tbss/.bss. .init_array must also stay its own output section -
   * scripts/patch_elf.py --fix-init-array looks it up by name. */
  .init_array ALIGN(8) : {
    PROVIDE_HIDDEN(__init_array_start = .);
    KEEP(*(SORT_BY_INIT_PRIORITY(.init_array.*)))
    KEEP(*(.init_array))
    PROVIDE_HIDDEN(__init_array_end = .);
  } >ram AT>ram :data

  .fini_array ALIGN(8) : {
    PROVIDE_HIDDEN(__fini_array_start = .);
    KEEP(*(SORT_BY_INIT_PRIORITY(.fini_array.*)))
    KEEP(*(.fini_array))
    PROVIDE_HIDDEN(__fini_array_end = .);
  } >ram AT>ram :data

  .got ALIGN(8) : {
    *(.got.plt)
    *(.got .got.*)
  } >ram AT>ram :data

  .data ALIGN(64) : {
    __data_start = .;
    /* .NET 11 GC (gcenv.unix.cpp static init) registers destructors via
     * __cxa_atexit(fn, arg, &__dso_handle). Nothing defines __dso_handle in
     * this fully custom link (no crtbegin.o), so lld synthesizes it at the
     * image base (VA 0) - out of PCREL_HI20 range (>2GiB) from code at
     * 0x8xxxxxxx. It is only an opaque DSO token, never dereferenced by
     * musl's __cxa_atexit, so any in-range address works; give it 8 bytes
     * of real storage here. */
    PROVIDE_HIDDEN(__dso_handle = .);
    . += 8;
    *(.data .data.* .sdata .sdata.*)
    _data_end = .;
  } >ram AT>ram :data

  __data_load = LOADADDR(.data);

  /* TLS template last among the loaded data: .tdata is the final PROGBITS
   * section, .tbss the first NOBITS one, so the file image stays contiguous
   * (see the note above .init_array). The two must stay adjacent - PT_TLS
   * covers both. */
  .tdata ALIGN(8) : {
    __tdata_start = .;
    *(.tdata .tdata.* .gnu.linkonce.td.*)
    __tdata_end = .;
  } >ram AT>ram :tls

  __tdata_load = LOADADDR(.tdata);
  __tdata_len = SIZEOF(.tdata);

  .tbss (NOLOAD) : {
    __tbss_start = .;
    *(.tbss .tbss.* .gnu.linkonce.tb.*)
    __tbss_end = .;
  } >ram AT>ram :tls

  __tbss_len = SIZEOF(.tbss);

  .bss (NOLOAD) : {
    PROVIDE(_bss_start = .);
    *(.bss .bss.* .sbss .sbss.* COMMON .scommon);
    PROVIDE(_bss_end = .);
  } >ram AT>ram

  . = ALIGN(8);
  PROVIDE(_global_pointer = .);

  . = ALIGN(8);
  /* Stack grows down from _init_stack_top through ZisK's own stack window,
   * whose bottom borders unmapped memory: an overflow faults in the emulator
   * instead of corrupting SYS, OUTPUT or .bss. 4 MiB is all ZisK reserves and
   * several times the deepest stack measured on mainnet blocks. */
  PROVIDE(_init_stack_bottom = ORIGIN(stack));
  PROVIDE(_init_stack_top    = ORIGIN(stack) + LENGTH(stack));
  PROVIDE(_kernel_heap_bottom = .);
  /* zkVM per-site inline allocator: reserve the top 8 bytes of RAM as a
   * FIXED-address cell holding the downward bump pointer, so the JIT can
   * hardcode its address (lui/addi, no relocation) and the C allocator
   * (pal/module.c) + JIT-emitted inline allocation share the same pointer.
   * Heap top is lowered by 24 so neither cell ever overlaps the heap. */
  PROVIDE(g_zk_bump_ptr     = ORIGIN(ram) + LENGTH(ram) - 8);   /* = 0xbffbfff8 */
  /* Companion cell 8 bytes BELOW the bump pointer, holding the lowest address
   * the heap may occupy. JIT-emitted inline allocation (dotnet-riscv fixup 26)
   * loads it and fail-fasts instead of bumping past the bottom of the heap, so
   * the address has to be exactly g_zk_bump_ptr - 8; the heap top drops another
   * 8 bytes to keep both cells clear of it. pal/module.c publishes the value
   * from .init_array, before the runtime entry point, and never rewrites it -
   * that is what lets the JIT hoist the load out of loops and pay for the
   * bounds check once per method rather than once per allocation. */
  PROVIDE(g_zk_heap_floor   = ORIGIN(ram) + LENGTH(ram) - 16);  /* = 0xbffbfff0 */
  PROVIDE(_kernel_heap_top  = ORIGIN(ram) + LENGTH(ram) - 24);
  PROVIDE(_kernel_heap_size = _kernel_heap_top - _kernel_heap_bottom);

  _end = .;

  ASSERT(ORIGIN(stack) >= 0xa0000000 && ORIGIN(stack) + LENGTH(stack) <= 0xa0400000,
         "the stack overlaps the ZisK SYS/OUTPUT area")
  ASSERT(ADDR(.modules) >= 0xa0430000 && _kernel_heap_bottom >= 0xa0430000,
         "data or heap overlaps the ZisK SYS/OUTPUT area")
  ASSERT(_kernel_heap_bottom < _kernel_heap_top, "no room left for the heap")

  /* Discard debug sections */
  /DISCARD/ : {
    *(.debug*)
    *(.comment)
    *(.riscv.attributes)
    /*
    *(.eh_frame_hdr)
    *(.eh_frame .eh_frame.*)
    *(.gcc_except_table .gcc_except_table.*)
    */
    /* *(.init_array) */
    /* *(.tdata .tdata.* .gnu.linkonce.td.*) */
    /* *(.tbss  .tbss.*  .gnu.linkonce.tb.*) */
  }
}