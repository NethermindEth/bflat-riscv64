.option norelax                # stop linker from C-relaxation
.section .text.init
.global _start
_start:
    .option push
    .option norelax
    la      gp, _global_pointer
    .option pop
    la      sp, _init_stack_top
    call    __managed__Main
    li      a7, 93             # exit syscall
    ecall
1:  j       1b                 # loop forever

# byte output helper for Console.Write on ZisK
.global __zisk_write_byte
__zisk_write_byte:
    li      t0, 0xA0000200     # UART address
    sb      a0, 0(t0)
    ret