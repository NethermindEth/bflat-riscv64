.section .text.init,"ax",@progbits
.global _start
_start:
    j _start
.section __modules,"aw",@progbits
    .word 0x12345678
.section .data,"aw",@progbits
    .word 0xdeadbeef
.section .tdata,"awT",@progbits
    .word 0xabcdef12
