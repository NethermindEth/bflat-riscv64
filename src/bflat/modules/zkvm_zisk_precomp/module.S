/**
 * @file
 * @brief ZKVM precompiles (CSR-based)
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
.option norelax

.macro ZKVM_CSR_VOID name, csr
    .section .text, "ax", @progbits
    .balign 4
    .global \name
    .type \name, @function
\name:
    .cfi_startproc
    /* leaf: no stack frame */
    csrs    \csr, a0
    ret
    .cfi_endproc
    .size \name, .-\name
.endm

.macro ZKVM_CSR_RET_U64 name, csr
    .section .text, "ax", @progbits
    .balign 4
    .global \name
    .type \name, @function
\name:
    .cfi_startproc
    /* leaf: no stack frame */
    csrrs   a0, \csr, a0
    ret
    .cfi_endproc
    .size \name, .-\name
.endm

/* Exported symbols (one per precompile). */
ZKVM_CSR_VOID     zkvm_keccakf,               0x800
ZKVM_CSR_VOID     zkvm_arith256,              0x801
ZKVM_CSR_VOID     zkvm_arith256_mod,          0x802
ZKVM_CSR_VOID     zkvm_secp256k1_add,         0x803
ZKVM_CSR_VOID     zkvm_secp256k1_dbl,         0x804
ZKVM_CSR_VOID     zkvm_sha256f,               0x805
ZKVM_CSR_VOID     zkvm_bn254_curve_add,       0x806
ZKVM_CSR_VOID     zkvm_bn254_curve_dbl,       0x807
ZKVM_CSR_VOID     zkvm_bn254_complex_add,     0x808
ZKVM_CSR_VOID     zkvm_bn254_complex_sub,     0x809
ZKVM_CSR_VOID     zkvm_bn254_complex_mul,     0x80A
ZKVM_CSR_VOID     zkvm_arith384_mod,          0x80B
ZKVM_CSR_VOID     zkvm_bls12_381_curve_add,   0x80C
ZKVM_CSR_VOID     zkvm_bls12_381_curve_dbl,   0x80D
ZKVM_CSR_VOID     zkvm_bls12_381_complex_add, 0x80E
ZKVM_CSR_VOID     zkvm_bls12_381_complex_sub, 0x80F
ZKVM_CSR_VOID     zkvm_bls12_381_complex_mul, 0x810
ZKVM_CSR_RET_U64  zkvm_add256,                0x811
