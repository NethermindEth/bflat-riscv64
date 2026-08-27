/**
 * @file
 * @brief Soft-float support module for no-F/D RISC-V targets
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maksim Menshikov <maksim.menshikov@nethermind.io>
 */

/*
 * Counterpart of the nofp module for the soft-float configuration: instead of
 * trapping, floating point WORKS, computed in integer code.
 *
 * The JIT (dotnet-riscv fixup 28) lowers all FP IR into calls that follow the
 * compiler-rt/libgcc soft-float ABI; the builtins themselves are vendored
 * under builtins/ (compiler-rt, Apache-2.0 WITH LLVM-exception) and compiled
 * for rv64im by module_srcs.lst.
 *
 * This file covers what the builtins do not:
 *
 *  - RhpDbl2Lng/RhpDbl2ULng and the int64->FP family: the runtime blobs ship
 *    these compiled for rv64gc (hard-float). They are diverted here with
 *    --wrap (see module_params.yml) so the hard-float archive members never
 *    enter the link; the replacements carry the .NET saturating semantics
 *    (NaN -> 0, out-of-range -> Min/Max) and compile to soft-float calls.
 *
 *  - fmod/fmodf (CORINFO_HELP_DBLREM/FLTREM): musl's members are hard-float,
 *    so the ilc-referenced symbols are wrapped to the vendored musl sources
 *    (MIT), which are pure bit manipulation and carry no FP instruction on
 *    any -march.
 */

#include <stdint.h>

/* Vendored musl fmod/fmodf as the __wrap_ definitions. math.h declarations
 * are renamed along with the definitions by the defines below. The ACSL
 * contracts sit on forward declarations; Frama-C attaches a declaration
 * spec to the (renamed) definitions pulled in by the includes. */

/*@ // IEEE-754 remainder as computed by musl's bit-manipulation fmod:
    // finite arguments with y != 0 produce the exact remainder; NaN
    // operands, infinite x or zero y produce NaN. Pure integer code.
    assigns \nothing;
*/
double __wrap_fmod(double x, double y);

/*@ assigns \nothing; */
float __wrap_fmodf(float x, float y);

#define fmod __wrap_fmod
#include "fmod.c"
#undef fmod

#define fmodf __wrap_fmodf
#include "fmodf.c"
#undef fmodf

/* .NET-semantics FP->int conversions (see nativeaot MathHelpers.cpp).
 * The comparisons compile into calls to the vendored soft-float builtins. */

/*@ // .NET double -> uint64 conversion semantics (MathHelpers.cpp):
    // NaN and negative values saturate to 0, values at or above 2^64
    // saturate to UINT64_MAX, the rest truncate toward zero.
    assigns \nothing;
    behavior nan_or_nonpositive:
      assumes \is_NaN(val) || val <= 0.0;
      ensures \result == 0;
    behavior too_big:
      assumes \is_finite(val) && val >= 18446744073709551616.0;
      ensures \result == UINT64_MAX;
*/
uint64_t
__wrap_RhpDbl2ULng(double val)
{
    const double uint64_max_plus_1 = 4294967296.0 * 4294967296.0;

    return (val > 0) ? ((val >= uint64_max_plus_1) ? UINT64_MAX
                                                   : (uint64_t)val)
                     : 0;
}

/*@ // .NET double -> int64 conversion semantics: NaN maps to 0, values
    // beyond either end of the int64 range saturate to Min/MaxValue, the
    // rest truncate toward zero.
    assigns \nothing;
    behavior nan:
      assumes \is_NaN(val);
      ensures \result == 0;
    behavior too_small:
      assumes \is_finite(val) && val <= -9223372036854775808.0;
      ensures \result == INT64_MIN;
    behavior too_big:
      assumes \is_finite(val) && val >= 9223372036854775808.0;
      ensures \result == INT64_MAX;
*/
int64_t
__wrap_RhpDbl2Lng(double val)
{
    const double int64_min = -2147483648.0 * 4294967296.0;
    const double int64_max = 2147483648.0 * 4294967296.0;

    return (val != val)      ? 0
           : (val <= int64_min) ? INT64_MIN
           : (val >= int64_max) ? INT64_MAX
                                : (int64_t)val;
}

/* int64 -> FP: plain casts, exact via the builtins. */

/*@ // int64 -> double, round to nearest even (soft-float builtin underneath).
    assigns \nothing;
*/
double
__wrap_RhpLng2Dbl(int64_t val)
{
    return (double)val;
}

/*@ // uint64 -> double, round to nearest even (soft-float builtin underneath).
    assigns \nothing;
*/
double
__wrap_RhpULng2Dbl(uint64_t val)
{
    return (double)val;
}

/*@ // int64 -> float, round to nearest even (soft-float builtin underneath).
    assigns \nothing;
*/
float
__wrap_RhpLng2Flt(int64_t val)
{
    return (float)val;
}

/*@ // uint64 -> float, round to nearest even (soft-float builtin underneath).
    assigns \nothing;
*/
float
__wrap_RhpULng2Flt(uint64_t val)
{
    return (float)val;
}
