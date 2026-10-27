/**
 * @file
 * @brief ZKVM Airbender implementation linker script
 *
 * Airbender reads instructions from a ROM at address 0 and starts with RAM
 * zeroed: the image is flat (objcopy -O binary), so everything initialised
 * is loaded in ROM, and whatever must run from RAM is copied there by _start
 * (module.S) from one contiguous block.
 *
 * Airbender v3's ROM is 4 MiB (common_constants/src/rom.rs ROM_BYTE_SIZE);
 * earlier versions have 2 MiB (circuit_defs/<machine>/src/lib.rs
 * MAX_ROM_SIZE), and the simulator does not bound it. AIRBENDER_ROM_SIZE
 * defaults to the v3 limit so an image that would not prove fails to link.
 *
 * v3 also proves only the unsigned RV32M instructions. The JIT leaves the
 * signed ones out of managed code (JitRiscVUnsignedMulDiv); for the native
 * code, scripts/airbender_patch.py moves each one into a trampoline in the
 * area reserved at the end of .text.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
OUTPUT_FORMAT("elf32-littleriscv")
OUTPUT_ARCH("riscv")
ENTRY(_start)


MEMORY {
  /* RAM ends at 1 GiB, the address space the prover traces. Its origin
   * follows the ROM so an oversized image can still be simulated. */
  rom   (rx)  : ORIGIN = 0, LENGTH = 1024M
  ram   (rw)  : ORIGIN = 64M, LENGTH = 960M
}

PHDRS {
  text PT_LOAD FLAGS(5);
  rodata PT_LOAD FLAGS(4);
  data PT_LOAD FLAGS(6);
  tls PT_TLS;
}

SECTIONS
{
  .text : {
    __image_text_start = .;
    KEEP(*(.text.init))
    . = ALIGN(4);
    *(.text._start)
    *(.text.__managed__Main)
    *(.text.*__managed__Main*)
    . = ALIGN(4);

    __start___managedcode = .;
    KEEP(*(__managedcode))
    __stop___managedcode  = .;
    . = ALIGN(4);

    __start___unbox = .;
    KEEP(*(__unbox))
    __stop___unbox  = .;

    *(.text .text.*)
    /* The native code is placed last, so all of it is within a jump of here. */
    . = ALIGN(4);
    __signed_tramp_start = .;
    . += 2048;
    __signed_tramp_end = .;
    . = ALIGN(16);
    __image_text_end = .;
  } >rom :text

  .rodata ALIGN(8) : {
    *(.srodata .srodata.*)
    *(.rodata .rodata.*)
  } >rom :rodata

  /* No unwind tables (see /DISCARD/ below): pal/eh's synthetic program
   * headers describe an empty .eh_frame_hdr. */
  __eh_frame_hdr_start = .;
  __eh_frame_hdr_end = .;

  /* Everything initialised that runs from RAM, as one block _start copies:
   * keep every PROGBITS section between __ram_init_start and __ram_init_end,
   * and the TLS template last so .tbss (NOBITS) follows it directly. */
  .modules ALIGN(16) : {
    __ram_init_start = .;
    __start___modules = .;
    KEEP(*(__modules))
    __stop___modules  = .;
  } >ram AT>rom :data

  .init_array ALIGN(8) : {
    PROVIDE_HIDDEN(__init_array_start = .);
    KEEP(*(SORT_BY_INIT_PRIORITY(.init_array.*)))
    KEEP(*(.init_array))
    PROVIDE_HIDDEN(__init_array_end = .);
  } >ram AT>rom :data

  .fini_array ALIGN(8) : {
    PROVIDE_HIDDEN(__fini_array_start = .);
    KEEP(*(SORT_BY_INIT_PRIORITY(.fini_array.*)))
    KEEP(*(.fini_array))
    PROVIDE_HIDDEN(__fini_array_end = .);
  } >ram AT>rom :data

  .got ALIGN(8) : {
    *(.got.plt)
    *(.got .got.*)
  } >ram AT>rom :data

  .data ALIGN(64) : {
    __data_start = .;
    /* An opaque DSO token for __cxa_atexit; see the SP1 script. */
    PROVIDE_HIDDEN(__dso_handle = .);
    . += 8;
    *(.data .data.* .sdata .sdata.*)
    _data_end = .;
  } >ram AT>rom :data

  .tdata ALIGN(8) : {
    __tdata_start = .;
    *(.tdata .tdata.* .gnu.linkonce.td.*)
    __tdata_end = .;
    . = ALIGN(4);
    __ram_init_end = .;
  } >ram AT>rom :data :tls

  __ram_init_load = LOADADDR(.modules);
  /* The TLS module copies the template from here; its RAM copy is the
   * one _start made, so either address holds the same bytes. */
  __tdata_load = __tdata_start;
  __tdata_len = SIZEOF(.tdata);

  .tbss (NOLOAD) : {
    __tbss_start = .;
    *(.tbss .tbss.* .gnu.linkonce.tb.*)
    __tbss_end = .;
  } >ram :tls

  __tbss_len = SIZEOF(.tbss);

  .bss (NOLOAD) : ALIGN(8) {
    PROVIDE(_bss_start = .);
    *(.bss .bss.* .sbss .sbss.* COMMON .scommon);
    . = ALIGN(4);
    PROVIDE(_bss_end = .);
  } >ram

  . = ALIGN(8);
  PROVIDE(_global_pointer = .);

  /* The stack sits above .bss, growing down towards it; the heap takes the
   * rest of RAM above the stack. */
  . = ALIGN(16);
  PROVIDE(_init_stack_bottom = .);
  . += 16M;
  PROVIDE(_init_stack_top = .);
  PROVIDE(_kernel_heap_bottom = .);
  /* The bump pointer and heap floor cells pal/module.c shares with any
   * JIT-emitted inline allocation, at the top of RAM. */
  PROVIDE(g_zk_bump_ptr     = ORIGIN(ram) + LENGTH(ram) - 8);
  PROVIDE(g_zk_heap_floor   = ORIGIN(ram) + LENGTH(ram) - 16);
  PROVIDE(_kernel_heap_top  = ORIGIN(ram) + LENGTH(ram) - 24);
  PROVIDE(_kernel_heap_size = _kernel_heap_top - _kernel_heap_bottom);

  _end = .;

  /* The ROM image ends with the load copy of the RAM block. */
  __rom_image_end = __ram_init_load + (__ram_init_end - __ram_init_start);
  ASSERT(__rom_image_end <= (DEFINED(AIRBENDER_ROM_SIZE) ? AIRBENDER_ROM_SIZE : 4M),
         "image does not fit Airbender's ROM; link with --defsym=AIRBENDER_ROM_SIZE=<bytes> to simulate it anyway")

  /* The unwind tables would take about half a MiB of a guest's ROM; an
   * Airbender image routes every exception to the guest's ZkvmThrow handler
   * instead (bflat links it as under --remove-eh). */
  /DISCARD/ : {
    *(.eh_frame_hdr)
    *(.eh_frame .eh_frame.*)
    *(.dotnet_eh_table)
    *(.debug*)
    *(.comment)
    *(.riscv.attributes)
  }
}
