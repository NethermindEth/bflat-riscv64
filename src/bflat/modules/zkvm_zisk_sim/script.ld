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
    tls PT_TLS;
}

SECTIONS
{
  . = 0x10000;

  .text : ALIGN(16)
  {
    KEEP(*(.text.init))
    *(.text .text.*)
  } :text

  .rodata : ALIGN(16)
  {
    *(.rodata .rodata.*)
  } :rodata

  .data : ALIGN(16)
  {
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

  .heap ALIGN(16) :
  {
    _kernel_heap_bottom = .;
    . = . + (150 * 1024 * 1024);
    _kernel_heap_top = .;
  } :data

  PROVIDE(_kernel_heap_size = _kernel_heap_top - _kernel_heap_bottom);

  _global_pointer = .;
}
