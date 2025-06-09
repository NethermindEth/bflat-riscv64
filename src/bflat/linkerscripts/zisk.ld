OUTPUT_FORMAT("elf64-littleriscv")
OUTPUT_ARCH("riscv")
ENTRY(_start)

MEMORY {
  rom   (xa) : ORIGIN = 0x80000000, LENGTH = 0x10000000
  ram   (wxa) : ORIGIN = 0xa0020000, LENGTH = 0xFFE0000
}

PHDRS {
  text PT_LOAD FLAGS(5);
  rodata PT_LOAD FLAGS(4);
  data PT_LOAD FLAGS(6);
  bss PT_LOAD FLAGS(6);
}

SECTIONS
{
  .text : { 
    /* First: Bootstrap code */
    *(.text.init)
    . = ALIGN(4);
    
    /* Second: Critical entry points (these MUST come early) */
    *(.text.__managed__Main)
    *(.text.*__managed__Main*)
    *(.text.*MainMethodWrapper*)
    *(.text.*Program__Main*)
    . = ALIGN(4);
    
    /* Third: All other program code */
    *(.text .text.*)
    . = ALIGN(4);
    
    /* Fourth: Any managed code sections */
    KEEP(*(__managedcode))
    . = ALIGN(4);
    
    /* Ensure proper alignment and padding */
    . = ALIGN(16);
  } >rom AT>rom :text

  . = ALIGN(8);
  PROVIDE(_global_pointer = .);
  .rodata : { *(.rodata .rodata.*)} >rom AT>rom :rodata

  .data : { *(.data .data.* .sdata .sdata.*) } >ram AT>ram :data

  .bss : {
    PROVIDE(_bss_start = .);
    *(.bss .bss.*);
    PROVIDE(_bss_end = .);
  } >ram AT>ram :bss

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
  }
}