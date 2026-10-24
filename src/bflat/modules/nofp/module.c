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

/*@ // Single abort policy for every FP stub: terminate with status 255,
    // never return. exit() resolves to the PAL's __wrap_exit (ZisK exit
    // ecall) in the zkVM link.
    assigns \nothing;
    ensures \false;
    exits \exit_status == 255;
*/
__attribute__((noreturn, noinline, cold))
static void
nofp_trap(void)
{
    exit(255);
}

/*
 * SOFT-FLOAT BUILTINS ARE NOT STUBBED, AND MUST NOT BE.
 *
 * __adddf3, __muldf3, __fixdfsi and the rest of that family are not "floating
 * point reached by mistake" - they ARE the soft-float implementation. The
 * runtime's FP helpers are C compiled for an ABI without an FPU, so the
 * compiler lowers every double add in them to a call to one of these, and
 * bflat's own libgcc.a (share/bflat/lib/linux/riscv64/musl) provides all 52 of
 * them for riscv64 - BuildCommand puts it on the zkVM link line after libc.a.
 *
 * They used to be trapped here, which made any guest that does arithmetic on a
 * double die with status 255 on its first operation - the module was written
 * for a guest that touches no floating point at all, and nothing did until the
 * soft-float test group. Because nofp.o is linked ahead of libgcc.a, those
 * stubs also kept the real implementations out of the image entirely.
 *
 * What still traps is the transcendental part of libm below: sin, pow and
 * friends have no business running inside a proof.
 */

/*
 * libm surface. The runtime's math helpers (RhpDblPow, RhpDblLog, ... in
 * MathHelpers.cpp) and the allocation-sampling path in GcAllocInternal
 * reference the C math library. Those paths never execute on the zkVM. The
 * bundled musl is built rv64im, so its implementations would link and run;
 * the trap is policy, not a missing instruction. Each function is diverted
 * at link time with --wrap=<fn> (see the manifest) to a stub here, so the
 * musl member is never extracted and a stray call fails loudly.
 *
 * Only the transcendental functions are here. The exact operations - ceil,
 * floor, modf, sqrt, fma, scalbn, fmod and their float forms - are not: IEEE
 * 754 gives each one a single correctly rounded result, like + and /, and
 * musl computes them with integer arithmetic alone. CoreLib calls them for
 * ordinary work: double formatting (ceil), DateTime.Add* (modf, through
 * Math.Truncate), HashHelpers.IsPrime (sqrt), and the C# % operator on
 * double and float (fmod).
 */
/* Same contract-in-macro arrangement as NOFP_STUB above (needs -CC). */
#define NOFP_WRAP_STUB(name) \
    /*@ assigns \nothing; ensures \false; exits \exit_status == 255; */ \
    void __wrap_##name(void) { nofp_trap(); }

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
NOFP_WRAP_STUB(cos)
NOFP_WRAP_STUB(cosf)
NOFP_WRAP_STUB(cosh)
NOFP_WRAP_STUB(coshf)
NOFP_WRAP_STUB(exp)
NOFP_WRAP_STUB(expf)
NOFP_WRAP_STUB(log)
NOFP_WRAP_STUB(logf)
NOFP_WRAP_STUB(log10)
NOFP_WRAP_STUB(log10f)
NOFP_WRAP_STUB(log2)
NOFP_WRAP_STUB(log2f)
NOFP_WRAP_STUB(pow)
NOFP_WRAP_STUB(powf)
NOFP_WRAP_STUB(sin)
NOFP_WRAP_STUB(sinf)
NOFP_WRAP_STUB(sinh)
NOFP_WRAP_STUB(sinhf)
NOFP_WRAP_STUB(tan)
NOFP_WRAP_STUB(tanf)
NOFP_WRAP_STUB(tanh)
NOFP_WRAP_STUB(tanhf)

/*
 * asprintf: referenced only by the PAL's CGroup CPU-limit parsing, which is
 * dead at runtime (cgroup initialization is stubbed via --wrap). Unlike the
 * math stubs above this one does NOT trap: -1 is the documented asprintf
 * failure result and the cgroup callers handle it, so if the path is ever
 * reached it degrades to "no cgroup limit detected" instead of aborting.
 * Diverting it also keeps musl's vasprintf and the float formatter of
 * vfprintf out of the image.
 */
/*@ // Deliberately NOT a trap: -1 is the documented asprintf failure and
    // the (dead) cgroup callers degrade to "no limit detected".
    assigns \nothing;
    ensures \result == -1;
*/
int __wrap_asprintf(void)
{
    return -1;
}
