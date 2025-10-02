OUTPUT_FORMAT("elf64-littleriscv")
OUTPUT_ARCH("riscv")
ENTRY(_start)

MEMORY {
  rom   (xa) : ORIGIN = 0x80000000, LENGTH = 0x10000000
  ram   (wxa) : ORIGIN = 0xa0020000, LENGTH = 0xFFE0000
}

PHDRS {
  text PT_LOAD FLAGS(1);
  rodata PT_LOAD FLAGS(4);
  data PT_LOAD FLAGS(6);
  tls PT_TLS;
}

SECTIONS
{
  .text : {
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
    KEEP(*(__managedcode))
    . = ALIGN(4);

    /* Third: All other program code */
    *(.text .text.*)
    . = ALIGN(4);

    /* Ensure proper alignment and padding */
    . = ALIGN(16);
  } >rom AT>rom :text

  .rodata ALIGN(16) : {
    *(.rodata .rodata.*)
  } >rom AT>rom :rodata

  .tdata ALIGN(8) : {
    __tdata_start = .;
    *(.tdata .tdata.* .gnu.linkonce.td.*)
    __tdata_end = .;
  } >rom AT>rom :tls

  .tbss (NOLOAD) : {
    __tbss_start = .;
    *(.tbss .tbss.* .gnu.linkonce.tb.*)
    __tbss_end = .;
  } >ram :tls

  .data ALIGN(16) : {
    __data_start = .;
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
  PROVIDE(_init_stack_top = . + 0x100000);
  PROVIDE(_kernel_heap_bottom = _init_stack_top);
  PROVIDE(_kernel_heap_top = ORIGIN(ram) + LENGTH(ram));
  PROVIDE(_kernel_heap_size = _kernel_heap_top - _kernel_heap_bottom);

  _end = .;
  
  /* Discard debug sections */
  /DISCARD/ : {
    *(.debug*)
    *(.comment)
    *(.riscv.attributes)
    *(.eh_frame_hdr)
    *(.eh_frame .eh_frame.*)
    *(.gcc_except_table .gcc_except_table.*)
    *(.init_array)
  }
}