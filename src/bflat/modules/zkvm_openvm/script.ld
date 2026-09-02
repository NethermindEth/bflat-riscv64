/**
 * @file
 * @brief ZKVM OpenVM implementation linker script
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
OUTPUT_FORMAT("elf64-littleriscv")
OUTPUT_ARCH("riscv")
ENTRY(_start)

MEMORY {
  /*
   * OpenVM memory map, read from the live RV64 line, branch develop-v2.1.0
   * (NOT develop-v2.1.0-rv64, an older snapshot of the same work, and NOT
   * main, which is still RV32 and rejects a 64-bit ELF):
   *   crates/toolchain/platform/src/memory.rs   MEM_BITS = 32, so guest
   *     memory is the whole 4 GiB u32 space; GUEST_MIN_MEM = 0x400,
   *     STACK_TOP = 0x200400, TEXT_START = 0x200800.
   *   crates/vm/src/system/memory/mod.rs        DEFAULT_POINTER_MAX_BITS
   *     = MEM_BITS - log2(u16 cell) = 31 cells, i.e. the same 4 GiB.
   *   crates/toolchain/instructions/src/program.rs  MAX_ALLOWED_PC =
   *     u32::MAX - 3, so any u32 text address is allowed.
   *   crates/toolchain/transpiler/src/elf.rs    Elf::decode(.., max_mem) is
   *     always called with MEM_SIZE, and addresses stay u32.
   *
   * Nothing in the loader enforces OpenVM's own STACK_TOP/TEXT_START - it
   * takes the entry from e_entry and the rest from PT_LOAD - so this mirrors
   * the ZisK map instead. That is what lets the bump cell sit at the same
   * 0xbffefff8 as every other zkVM target: OpenVM used to cap guest memory at
   * 512 MiB, which put that address out of reach and forced a target-specific
   * JitZkBumpAddr; at 4 GiB the special case disappears.
   */
  rom   (rxa) : ORIGIN = 0x80000000, LENGTH = 0x10000000
  ram   (rwxa): ORIGIN = 0xa0020000, LENGTH = 0x1FFD0000
}

PHDRS {
  text PT_LOAD FLAGS(5);    /* R+X */
  rodata PT_LOAD FLAGS(4);  /* R   */
  data PT_LOAD FLAGS(6);    /* R+W */
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
    /* The transpiler decodes every word of every PF_X segment up front and
     * fails the whole build on one it does not recognise
     * (TranspilerError::ParseError). A zero-filled alignment gap is an
     * illegal encoding, so pad with NOP (0x00000013, written low byte first
     * by the linker's big-endian fill pattern). */
  } >rom AT>rom :text =0x13000000

  /* Page-align the read-only segment. The three PT_LOADs share one load-address
   * counter, and lld has to keep each segment's file offset congruent to its
   * virtual address modulo the page size; with .rodata packed to 16 bytes, the
   * adjustment that buys pushed the rodata sections' load addresses into the
   * range already taken by .modules, which is the first section with its
   * address in ram and its contents in rom. The link then failed outright with
   * "load address range overlaps", and only once the image grew enough for the
   * two to meet. SP1's script aligns here for its own reason (per-page
   * protections); this one aligns to keep the segments from colliding. */
  .rodata ALIGN(4096) : {
    *(.rodata .rodata.*)
  } >rom AT>rom :rodata

  /* DWARF unwind tables. Placed explicitly (they would otherwise land as
   * orphan sections) so their bounds are named: pal/module.c reports
   * .eh_frame_hdr as a synthetic PT_GNU_EH_FRAME, which is how libunwind
   * finds the FDE binary-search index. Keeping them OUT of the PF_X segment
   * also keeps them away from the instruction transpiler. */
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
   * segment's `vaddr - offset` congruence - OpenVM loads the image from
   * program headers only and would then read .got from the wrong file
   * bytes. Keep every PROGBITS section ahead of .tbss/.bss. */
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
     * image base (VA 0). It is only an opaque DSO token, never dereferenced
     * by musl's __cxa_atexit, so any in-range address works; give it 8 bytes
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
  /* Stack grows down from _init_stack_top, carved out of RAM above .bss just
   * as on ZisK. With deep NativeAOT call chains (TypeLoader, cctor recursion)
   * 1 MiB overflowed into .bss; 4 MiB gives ample headroom. */
  PROVIDE(_init_stack_top     = . + 0x400000);
  PROVIDE(_kernel_heap_bottom = _init_stack_top);
  /* zkVM per-site inline allocator: reserve the top 8 bytes of RAM as a
   * FIXED-address cell holding the downward bump pointer, so the JIT can
   * emit its address as a constant (lui/addi, no relocation) and the C
   * allocator (pal/module.c) + JIT-emitted inline allocation share the same
   * pointer. Heap top is lowered by 24 so neither cell ever overlaps the heap.
   *
   * Same address as ZisK and SP1. BuildCommand passes it to the JIT as
   * JitZkBumpAddr and the two must stay in step - dotnet-riscv fixup 26 takes
   * the cell address from that knob and inlines nothing when it is 0. */
  PROVIDE(g_zk_bump_ptr     = ORIGIN(ram) + LENGTH(ram) - 8);   /* = 0xbffefff8 */
  /* Companion cell 8 bytes BELOW the bump pointer, holding the lowest address
   * the heap may occupy. JIT-emitted inline allocation (dotnet-riscv fixup 26)
   * loads it and fail-fasts instead of bumping past the bottom of the heap, so
   * the address has to be exactly g_zk_bump_ptr - 8; the heap top drops another
   * 8 bytes to keep both cells clear of it. pal/module.c publishes the value
   * from .init_array, before the runtime entry point, and never rewrites it -
   * that is what lets the JIT hoist the load out of loops and pay for the
   * bounds check once per method rather than once per allocation. */
  PROVIDE(g_zk_heap_floor   = ORIGIN(ram) + LENGTH(ram) - 16);  /* = 0xbffefff0 */
  PROVIDE(_kernel_heap_top  = ORIGIN(ram) + LENGTH(ram) - 24);
  PROVIDE(_kernel_heap_size = _kernel_heap_top - _kernel_heap_bottom);

  _end = .;

  /* Discard debug sections */
  /DISCARD/ : {
    *(.debug*)
    *(.comment)
    *(.riscv.attributes)
  }
}
