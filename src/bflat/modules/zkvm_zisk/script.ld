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
   * Zisk RAM map (zisk/core/src/mem.rs):
   *   RAM_ADDR            = 0xa0000000
   *   AVAILABLE_MEM_ADDR  = 0xa0030000
   *   FLOAT_LIB_RAM_ADDR  = 0xbfff0000
   *   RAM end             = 0xc0000000
   */
  ram   (wxa) : ORIGIN = 0xa0020000, LENGTH = 0x1FFD0000
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

  .bss (NOLOAD) : {
    PROVIDE(_bss_start = .);
    *(.bss .bss.* .sbss .sbss.* COMMON .scommon);
    PROVIDE(_bss_end = .);
  } >ram AT>ram

  . = ALIGN(8);
  PROVIDE(_global_pointer = .);

  . = ALIGN(8);
  /* Stack grows down from _init_stack_top. With deep NativeAOT call chains
   * (TypeLoader, cctor recursion) 1 MiB overflowed into .bss and corrupted
   * static state (notably pal/module.c:mem). 4 MiB gives ample headroom. */
  PROVIDE(_init_stack_top = . + 0x400000);
  PROVIDE(_kernel_heap_bottom = _init_stack_top);
  /* zkVM per-site inline allocator: reserve the top 8 bytes of RAM as a
   * FIXED-address cell holding the downward bump pointer, so the JIT can
   * hardcode its address (lui/addi, no relocation) and the C allocator
   * (pal/module.c) + JIT-emitted inline allocation share the same pointer.
   * Heap top is lowered by 16 so the cell never overlaps the heap. */
  PROVIDE(g_zk_bump_ptr     = ORIGIN(ram) + LENGTH(ram) - 8);   /* = 0xbffefff8 */
  PROVIDE(_kernel_heap_top  = ORIGIN(ram) + LENGTH(ram) - 16);
  PROVIDE(_kernel_heap_size = _kernel_heap_top - _kernel_heap_bottom);

  _end = .;

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