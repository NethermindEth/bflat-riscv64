/**
 * @file
 * @brief ZKVM OpenVM implementation.
 *
 * OpenVM's RV64 line (openvm-org/openvm, branch develop-v2.1.0-rv64) proves
 * RV64IM with lp64 and no A/C extension. The transpiler
 * (crates/toolchain/transpiler/src/elf.rs) reads PT_LOAD program headers,
 * requires a 64-bit ELF, and does NOT set up sp - _start points it at the
 * stack itself, as OpenVM's own Rust entrypoint does
 * (crates/toolchain/openvm/src/lib.rs).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
.option norelax

/* Marker read (as a weak symbol reference) by pal/module.c and rhp/module.c
 * to pick OpenVM's custom-instruction protocol - TERMINATE and the PrintStr
 * phantom - out of the shared objects those modules are built as. The value
 * is never dereferenced, only tested against NULL. */
.global __zkvm_target_openvm
.set    __zkvm_target_openvm, 1

.section .rodata
    .balign 8
app_str:
    .asciz "app"

    .section .data
    .balign 8
argv_vec:
    .dword app_str
    .dword 0

/* See the note in modules/zkvm_sp1/module.S: the managed binding is named
 * syscall_keccak_f after ziskos, OpenVM spells it zkvm_keccakf, and the
 * standard defines no permutation to agree on. Same section trick, so a guest
 * built without --extlib does not acquire a reference to the bindings
 * library. */
.section .text.syscall_keccak_f,"ax",@progbits
.global syscall_keccak_f
.type   syscall_keccak_f, @function
syscall_keccak_f:
    .cfi_startproc
    tail    zkvm_keccakf
    .cfi_endproc
    .size   syscall_keccak_f, .-syscall_keccak_f


/* OpenVM's custom-0 instructions, kept out of the shared pal/rhp objects so
 * that SP1 images (which decode every word of .text) never carry them.
 * Called from pal/module.c through weak references.
 *
 * zkvm_openvm_terminate(long code): TERMINATE (custom-0, funct3 0). The exit
 * code is an immediate, so a runtime code collapses to OpenVM's two
 * outcomes: 0 success, 1 failure (openvm::process::exit / panic). */
.section .text.zkvm_openvm_terminate,"ax",@progbits
.global zkvm_openvm_terminate
.type   zkvm_openvm_terminate, @function
zkvm_openvm_terminate:
    .cfi_startproc
    beqz    a0, 1f
    .insn   i 0x0b, 0, x0, x0, 1
2:  j       2b
1:  .insn   i 0x0b, 0, x0, x0, 0
3:  j       3b
    .cfi_endproc
    .size   zkvm_openvm_terminate, .-zkvm_openvm_terminate

/* zkvm_openvm_print(const char *buf, long len): the PrintStr phantom -
 * custom-0, funct3 3, imm 1, buffer in rd, length in rs1
 * (openvm/extensions/riscv/guest/src/io.rs raw_print_str_from_bytes). */
.section .text.zkvm_openvm_print,"ax",@progbits
.global zkvm_openvm_print
.type   zkvm_openvm_print, @function
zkvm_openvm_print:
    .cfi_startproc
    .insn   i 0x0b, 3, a0, a1, 1
    ret
    .cfi_endproc
    .size   zkvm_openvm_print, .-zkvm_openvm_print

.section .text.init
.global _start
    .cfi_sections .eh_frame
_start:
    .cfi_startproc
    .cfi_def_cfa sp, 0
    .cfi_undefined ra
    .option push
    .option norelax
    la      gp, _global_pointer
    .option pop
    la      sp, _init_stack_top

    /* noos_start_main replaces musl's __libc_start_main: it initialises the
     * thread pointer, runs .init_array and calls uBootstrap_main. musl's
     * startup exists to parse auxv and set up an environment this guest does
     * not have, and it costs 126 instructions the VM cannot decode. */
    li      a0, 1            # argc = 1
    la      a1, argv_vec     # argv = &argv_vec[0]
    call    noos_start_main
    # Unreachable: __libc_start_main exits through musl's exit(), which
    # pal/module.c wraps into the OpenVM TERMINATE instruction. Kept as a
    # backstop - TERMINATE with exit code 0 (custom-0 opcode 0x0b, funct3 0,
    # rd = rs1 = x0, imm = exit code; see openvm_platform::rust_rt).
    .insn   i 0x0b, 0, x0, x0, 0
1:  j       1b               # loop forever
    .cfi_endproc
