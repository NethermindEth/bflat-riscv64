.option norelax                # stop linker from C-relaxation
.section .rodata
    .balign 8
app_str:
    .asciz "app"

    .section .data
    .balign 8
argv_vec:
    .dword app_str
    .dword 0

.section .text.init
.global _start
_start:
    .option push
    .option norelax
    la      gp, _global_pointer
    .option pop
    la      sp, _init_stack_top
    li      a0, 1            # argc = 1
    la      a1, argv_vec     # argv = &argv_vec[0]
    call    __managed__Main
    li      a7, 93             # exit syscall
    ecall
1:  j       1b                 # loop forever

