/**
 * @file
 * @brief ZKVM SP1 implementation.
 *
 * SP1 (succinctlabs/sp1 v6+) proves the riscv64im-succinct-zkvm-elf target:
 * RV64IM, lp64, no A and no C extension. The loader
 * (crates/core/executor/src/disassembler/elf.rs) reads PT_LOAD program
 * headers only, takes the entry from e_entry, and does NOT set up sp -
 * _start must point it at the stack itself, exactly as SP1's own Rust
 * entrypoint does (crates/zkvm/entrypoint/src/lib.rs).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
.option norelax

/* Marker read (as a weak symbol reference) by pal/module.c and rhp/module.c
 * to pick SP1's syscall protocol - halt via t0 = HALT, console writes via
 * t0 = WRITE - out of the shared objects those modules are built as. The
 * value is never dereferenced, only tested against NULL. */
.global __zkvm_target_sp1
.set    __zkvm_target_sp1, 1

.section .rodata
    .balign 8
app_str:
    .asciz "app"

    .section .data
    .balign 8
argv_vec:
    .dword app_str
    .dword 0

/* sp1-zkvm's runtime entry, or 0 when no bindings library is linked. See the
 * note in _start for why the address is materialised from .data. */
.weak __start
    .balign 8
sp1_start_ptr:
    .dword __start

/* Nethermind's KeccakHash reaches the permutation through
 * Accelerators.KeccakF, whose binding is named for ziskos: syscall_keccak_f.
 * SP1 spells the same operation zkvm_keccak_permute and OpenVM zkvm_keccakf -
 * the eth-act standard covers zkvm_keccak256 and defines no permutation at
 * all, so the name was never agreed on. Rather than make the managed side
 * target-aware, each target's entry-point module carries a tail call under
 * the name the binding uses.
 *
 * In its own section on purpose: --gc-sections then drops it, and the
 * reference to the bindings library with it, in a guest built without
 * --extlib. The ABI is identical everywhere - a0 points at the 25 u64 state
 * words - so this is a jump, not a thunk. */
.section .text.syscall_keccak_f,"ax",@progbits
.global syscall_keccak_f
.type   syscall_keccak_f, @function
syscall_keccak_f:
    .cfi_startproc
    tail    zkvm_keccak_permute
    .cfi_endproc
    .size   syscall_keccak_f, .-syscall_keccak_f

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

    /* Enter through SP1's own runtime entry (sp1-zkvm __start, exported by
     * the bindings library): it initialises the Rust allocator and the
     * public-values hasher that syscall_write / syscall_halt depend on, calls
     * main() and halts with main's return value through the full commit
     * protocol. Bypassing it left both uninitialised: the first Rust
     * allocation in the bindings failed and write_output faulted.
     *
     * WEAK, because a guest built without --extlib links no bindings library
     * and has no __start at all - the sample guests in CI are exactly that.
     * There is nothing to initialise in that case (the Rust allocator and the
     * public-values stream both live in the bindings), so the fallback enters
     * the .NET start-up directly and lets pal's zkvm_raw_exit issue the
     * open-coded commit-and-halt sequence it keeps for this case.
     *
     * The address is taken through a word in .data rather than `la`: a
     * PC-relative reference to an undefined weak symbol resolves to absolute
     * 0 and overflows the auipc/addi pair. */
    la      t0, sp1_start_ptr
    ld      t0, 0(t0)
    beqz    t0, 2f
    jalr    t0
    # Unreachable: __start never returns. Kept as a backstop - a bare HALT
    # ecall (syscall id in t0, exit code in a0).
    li      t0, 0            # SP1 HALT
    li      a0, 0
    ecall
1:  j       1b               # loop forever
2:  /* No bindings library: uBootstrap_main is reached the way OpenVM reaches
     * it, and termination goes through pal. */
    li      a0, 1            # argc = 1
    la      a1, argv_vec     # argv = &argv_vec[0]
    call    noos_start_main
3:  j       3b               # noos_start_main never returns
    .cfi_endproc
    .size   _start, .-_start

/* main(), as SP1's __start calls it: extern "C" fn main() -> i32. The .NET
 * side of the start-up (thread pointer, .init_array, uBootstrap_main) is
 * noos_main, the returning half of noos_start_main. Reached through
 * --wrap=main (pal's module_params.yml, sp1 only): libbootstrapper.o also
 * defines a main, the desktop runtime's, which a zkVM guest never enters. */
.section .text.__wrap_main,"ax",@progbits
.global __wrap_main
.type   __wrap_main, @function
__wrap_main:
    .cfi_startproc
    addi    sp, sp, -16
    .cfi_adjust_cfa_offset 16
    sd      ra, 8(sp)
    .cfi_rel_offset ra, 8
    li      a0, 1            # argc = 1
    la      a1, argv_vec     # argv = &argv_vec[0]
    call    noos_main
    ld      ra, 8(sp)
    addi    sp, sp, 16
    .cfi_adjust_cfa_offset -16
    ret
    .cfi_endproc
    .size   __wrap_main, .-__wrap_main
