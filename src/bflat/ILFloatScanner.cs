// bflat C# compiler
// Copyright (C) 2021-2022 Michal Strehovsky
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Generic;

using Internal.IL;
using Internal.TypeSystem;

/// <summary>
/// Scans a method's IL for floating point - the back-end for the --error-on-float
/// gate.
///
/// Detection targets ONLY the IL opcodes that actually emit a hardware FP
/// instruction on the soft-float lp64 target: conv.r4/r8/r.un (integer->float =
/// fcvt) and ckfinite. It deliberately does NOT flag ldc.r4/r8, ld/st ind/elem
/// .r4/.r8: those merely move float *bits*, which the backend lowers to ordinary
/// integer loads/stores/immediates when no FPU is present (e.g. Single/Double.
/// GetHashCode reinterpret the bits via SingleToUInt32Bits and emit zero FP, and
/// a double field initialized to a constant is an integer store). Flagging those
/// produced false positives against binaries the emulator accepts as FP-free.
///
/// Known gap: the polymorphic arithmetic opcodes (add/mul/...) are typed by their
/// operands, so a method doing `a*b` on two floats with no conversion carries no
/// FP opcode of its own and is not caught here. In practice float arithmetic is
/// reached through a conversion (any int<->float in the expression) which IS
/// flagged; the emulator run remains the ground-truth backstop for the residual.
///
/// The caller scans ONLY methods that were actually emitted
/// (CompilationResults.CompiledMethodBodies): dead FP branches in shared generics
/// and preinit-folded cctors (Stopwatch..cctor) are never emitted, so they raise
/// no false alarm.
/// </summary>
static class ILFloatScanner
{
    /// <summary>Name of the first FP opcode found in the body, or null.</summary>
    public static string Find(MethodIL il)
    {
        byte[] b = il.GetILBytes();
        if (b == null)
            return null;
        int p = 0;
        while (p < b.Length)
        {
            ILOpcode op = (b[p] == 0xFE && p + 1 < b.Length)
                ? (ILOpcode)(0xFE00 + b[p + 1])
                : (ILOpcode)b[p];

            switch (op)
            {
                case ILOpcode.conv_r4:
                case ILOpcode.conv_r8:
                case ILOpcode.conv_r_un:
                case ILOpcode.ckfinite:
                    return op.ToString();
            }

            // Advance past this instruction so operand bytes are never misread as
            // opcodes. switch is variable-length (uint32 count + that many int32
            // targets); everything else has a fixed size from GetSize.
            if (op == ILOpcode.switch_)
            {
                if (p + 5 > b.Length) break;
                uint n = (uint)(b[p + 1] | (b[p + 2] << 8) | (b[p + 3] << 16) | (b[p + 4] << 24));
                p += 5 + (int)n * 4;
            }
            else if (op.IsValid())
            {
                p += op.GetSize();
            }
            else
            {
                break; // unknown/invalid opcode - stop rather than misparse operands
            }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="method"/> is in <paramref name="allowed"/>
    /// (<c>Namespace.Type.Method</c>, the <c>checks.il_float_allow</c> items of
    /// the platform link). Its floating point is real IL, but in a block that
    /// the JIT removes on the target, so the check reports it as a warning.
    /// The signature is not part of the name.
    /// </summary>
    public static bool IsAllowed(MethodDesc method, IReadOnlyList<string> allowed)
    {
        MethodDesc typical = method.GetTypicalMethodDefinition();
        if (typical.OwningType is not MetadataType mt)
            return false;
        foreach (string entry in allowed)
        {
            int m = entry.LastIndexOf('.');
            int t = entry.LastIndexOf('.', m - 1);
            if (typical.Name.StringEquals(entry[(m + 1)..])
                && mt.Name.StringEquals(entry[(t + 1)..m])
                && mt.Namespace.StringEquals(entry[..t]))
                return true;
        }
        return false;
    }
}
