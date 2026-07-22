/**
 * @file
 * @brief Floating point neglecting module
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */

/*
 * The supported language subset has no floating point. The RISC-V soft-float
 * compiler-rt builtins (__adddf3, __fixdfsi, ...) must therefore never be
 * reached. Previously these were defined as empty bodies, so a stray FP
 * operation pulled in transitively (e.g. via the BCL) would call one of them,
 * get an undefined register value back, and continue with a SILENTLY WRONG
 * result -- the worst possible failure mode for a proving system.
 *
 * Instead each builtin now terminates loudly: any call exits the program with
 * a distinct non-zero status (255) rather than returning a garbage value, so
 * the run fails visibly instead of producing a successful proof of a wrong
 * computation. exit() is routed through the PAL's --wrap=exit to the zkVM's
 * real termination ecall; in a build without that wrap it still reaches a
 * non-success terminator, never a silent return. To change the abort policy,
 * change nofp_trap() alone.
 */
extern void exit(int status) __attribute__((noreturn));

__attribute__((noreturn, noinline, cold))
static void
nofp_trap(void)
{
    exit(255);
}

/*
 * Each soft-float builtin is declared with its real (void) placeholder
 * prototype, exactly as before, and routed to nofp_trap(). The argument and
 * return registers are irrelevant: the function never returns.
 */
#define NOFP_STUB(name) void name(void) { nofp_trap(); }

NOFP_STUB(__extenddftf2)
NOFP_STUB(__addtf3)
NOFP_STUB(__netf2)
NOFP_STUB(__multf3)
NOFP_STUB(__fixunstfsi)
NOFP_STUB(__floatunsitf)
NOFP_STUB(__subtf3)
NOFP_STUB(__fixtfsi)
NOFP_STUB(__floatsitf)
NOFP_STUB(__eqtf2)
NOFP_STUB(__divtf3)
NOFP_STUB(__letf2)
NOFP_STUB(__trunctfsf2)
NOFP_STUB(__trunctfdf2)
NOFP_STUB(__getf2)
NOFP_STUB(__floatdidf)
NOFP_STUB(__floatunsisf)
NOFP_STUB(__ltdf2)
NOFP_STUB(__gedf2)
NOFP_STUB(__fixdfsi)
NOFP_STUB(__gtdf2)
NOFP_STUB(__floatsisf)
NOFP_STUB(__floatsidf)
NOFP_STUB(__floatunsidf)
NOFP_STUB(__floatundisf)
NOFP_STUB(__muldf3)
NOFP_STUB(__mulsf3)
NOFP_STUB(__floatundidf)
NOFP_STUB(__divdf3)
NOFP_STUB(__subdf3)
NOFP_STUB(__adddf3)
NOFP_STUB(__extendsftf2)
NOFP_STUB(__extendsfdf2)
NOFP_STUB(__eqdf2)
NOFP_STUB(__fixunsdfsi)
NOFP_STUB(__fixunsdfdi)
NOFP_STUB(__divsf3)
NOFP_STUB(__truncdfsf2)
NOFP_STUB(__fixdfdi)
NOFP_STUB(__gtsf2)
NOFP_STUB(__fixunssfsi)
NOFP_STUB(__addsf3)
NOFP_STUB(__nedf2)
NOFP_STUB(__floatdisf)
NOFP_STUB(__fixsfsi)
NOFP_STUB(__fixunssfdi)
NOFP_STUB(__gesf2)
NOFP_STUB(__ledf2)
NOFP_STUB(__ltsf2)
NOFP_STUB(__subsf3)
NOFP_STUB(__eqsf2)
NOFP_STUB(__lesf2)

/*
 * libm surface. The runtime's math helpers (RhpDblPow, RhpDblLog, ... in
 * MathHelpers.cpp) and the allocation-sampling path in GcAllocInternal
 * reference the C math library. Those paths never execute on the zkVM, but
 * with a hard-float libc the mere reference pulls musl's implementations
 * into the link, and their F/D instructions poison the rv64ima .text (the
 * ZisK transpiler rejects them, and they inflate the instruction ROM).
 * Each function is diverted at link time with --wrap=<fn> (see BuildCommand)
 * to a trap stub here, so the musl archive member is never extracted and a
 * stray runtime call fails loudly instead of computing garbage.
 */
#define NOFP_WRAP_STUB(name) void __wrap_##name(void) { nofp_trap(); }

NOFP_WRAP_STUB(acos)
NOFP_WRAP_STUB(acosf)
NOFP_WRAP_STUB(acosh)
NOFP_WRAP_STUB(acoshf)
NOFP_WRAP_STUB(asin)
NOFP_WRAP_STUB(asinf)
NOFP_WRAP_STUB(asinh)
NOFP_WRAP_STUB(asinhf)
NOFP_WRAP_STUB(atan)
NOFP_WRAP_STUB(atanf)
NOFP_WRAP_STUB(atan2)
NOFP_WRAP_STUB(atan2f)
NOFP_WRAP_STUB(atanh)
NOFP_WRAP_STUB(atanhf)
NOFP_WRAP_STUB(cbrt)
NOFP_WRAP_STUB(cbrtf)
NOFP_WRAP_STUB(ceil)
NOFP_WRAP_STUB(ceilf)
NOFP_WRAP_STUB(cos)
NOFP_WRAP_STUB(cosf)
NOFP_WRAP_STUB(cosh)
NOFP_WRAP_STUB(coshf)
NOFP_WRAP_STUB(exp)
NOFP_WRAP_STUB(expf)
NOFP_WRAP_STUB(floor)
NOFP_WRAP_STUB(floorf)
NOFP_WRAP_STUB(fma)
NOFP_WRAP_STUB(fmaf)
NOFP_WRAP_STUB(fmod)
NOFP_WRAP_STUB(fmodf)
NOFP_WRAP_STUB(log)
NOFP_WRAP_STUB(logf)
NOFP_WRAP_STUB(log10)
NOFP_WRAP_STUB(log10f)
NOFP_WRAP_STUB(log2)
NOFP_WRAP_STUB(log2f)
NOFP_WRAP_STUB(modf)
NOFP_WRAP_STUB(modff)
NOFP_WRAP_STUB(pow)
NOFP_WRAP_STUB(powf)
NOFP_WRAP_STUB(sin)
NOFP_WRAP_STUB(sinf)
NOFP_WRAP_STUB(sinh)
NOFP_WRAP_STUB(sinhf)
NOFP_WRAP_STUB(sqrt)
NOFP_WRAP_STUB(sqrtf)
NOFP_WRAP_STUB(tan)
NOFP_WRAP_STUB(tanf)
NOFP_WRAP_STUB(tanh)
NOFP_WRAP_STUB(tanhf)
