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

// Mechanism for the zkVM IL rewrites: the MethodIL wrappers, the
// byte-level IL editor used to build them, the ILProviders that hand ILC a
// rewritten body, and the scanner behind the --error-on-float gate.
//
// This file is deliberately policy-free. WHICH methods get rewritten, and why,
// lives in ZkvmSubstitutions.cs; everything here is the machinery those
// decisions are expressed with.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis;

using ILCompiler;
using ILCompiler.Dataflow;
using ILCompiler.DependencyAnalysis;

using Internal.IL;
using Internal.IL.Stubs;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using ILLink.Shared;
/// <summary>
/// MethodIL wrapper returning a rewritten copy of the inner body's IL stream.
/// Metadata tokens keep resolving through the inner (Ecma) body, so a rewrite
/// may freely reference any token already valid in the owning module; tokens
/// injected by a rewrite (values chosen outside the module's real token space)
/// are resolved from <paramref name="extraTokens"/> instead.
///
/// GetMethodILDefinition is overridden: for a shared generic instantiation the
/// inner MethodIL is an InstantiatedMethodIL whose definition is the open body,
/// and ILC's generic-dictionary / method-body-folding analysis reaches the
/// method through that open definition. A wrapper that returned `this` (the
/// default) would hand back an instantiated, rewritten body where the open one is
/// expected, corrupting the generic dictionary layout (observed as a null
/// WeakReference&lt;T&gt; MethodTable when the reflection type unifier grows).
/// The IL bytes are identical between instantiation and definition (only token
/// resolution differs), so the same rewritten bytes wrap the open definition.
/// </summary>
sealed class RewrittenMethodIL : MethodIL
{
    private readonly MethodIL _inner;
    private readonly byte[] _bytes;
    private readonly Dictionary<int, object> _extraTokens;
    private readonly int _extraMaxStack;
    private readonly LocalVariableDefinition[] _locals;

    public RewrittenMethodIL(MethodIL inner, byte[] bytes, Dictionary<int, object> extraTokens = null, int extraMaxStack = 0, LocalVariableDefinition[] locals = null)
    {
        _inner = inner;
        _bytes = bytes;
        _extraTokens = extraTokens;
        _extraMaxStack = extraMaxStack;
        _locals = locals;
    }

    public override MethodDesc OwningMethod => _inner.OwningMethod;
    public override int MaxStack => _inner.MaxStack + _extraMaxStack;
    public override bool IsInitLocals => _inner.IsInitLocals;
    public override byte[] GetILBytes() => _bytes;
    public override LocalVariableDefinition[] GetLocals() => _locals ?? _inner.GetLocals();
    public override ILExceptionRegion[] GetExceptionRegions() => _inner.GetExceptionRegions();
    public override object GetObject(int token, NotFoundBehavior notFoundBehavior = NotFoundBehavior.Throw)
        => _extraTokens != null && _extraTokens.TryGetValue(token, out object o)
            ? o
            : _inner.GetObject(token, notFoundBehavior);

    public override MethodIL GetMethodILDefinition()
    {
        MethodIL innerDef = _inner.GetMethodILDefinition();
        return innerDef == _inner
            ? this   // already the open definition (e.g. a non-generic method)
            : new RewrittenMethodIL(innerDef, _bytes, _extraTokens, _extraMaxStack, _locals);
    }
}

/// <summary>
/// Instruction-boundary-aware editor over a method body's IL. The surgical zkVM
/// rewrites below express their edits in terms of DECODED instructions - find by
/// opcode/operand, nop whole instructions - instead of raw byte
/// pattern matches and byte pokes. Decoding guarantees a constant that happens to
/// share an opcode's byte value (e.g. a 0x80 inside a ldc.r8 operand) is never
/// mistaken for that opcode, and that an edit never splits an instruction - the
/// class of bug a byte scan invites. Every edit preserves total length, so branch
/// offsets and exception regions in the untouched remainder stay valid with no
/// re-emit needed.
/// </summary>
sealed class ILEditor
{
    private readonly MethodIL _body;
    private readonly byte[] _il;
    private readonly List<int> _offsets = new List<int>();     // instruction starts, ascending
    private readonly List<ILOpcode> _opcodes = new List<ILOpcode>();

    public ILEditor(MethodIL body)
    {
        _body = body;
        _il = (byte[])body.GetILBytes().Clone();
        for (int p = 0; p < _il.Length; )
        {
            ILOpcode op = OpcodeAt(p);
            _offsets.Add(p);
            _opcodes.Add(op);
            p += SizeAt(p, op);
        }
    }

    public int Count => _offsets.Count;
    public int OffsetOf(int index) => _offsets[index];
    public ILOpcode OpcodeOf(int index) => _opcodes[index];

    // 0xFE - the escape byte that introduces a two-byte opcode.
    private const byte TwoByteEscape = (byte)ILOpcode.prefix1;

    private ILOpcode OpcodeAt(int p)
        => (_il[p] == TwoByteEscape && p + 1 < _il.Length)
            ? (ILOpcode)(((int)ILOpcode.prefix1 << 8) | _il[p + 1])
            : (ILOpcode)_il[p];

    // switch is variable-length; every other normal opcode is sized by ILC's
    // ILOpcode.GetSize(). GetSize() is only called behind IsValid() because it
    // indexes an internal table and faults on the prefix opcodes - which IsValid()
    // rejects - so those (volatile./readonly./tail./constrained./unaligned./no.)
    // are sized here from their ECMA operand instead. Sizing every opcode (rather
    // than stopping at the first prefix) keeps the whole method decoded.
    private int SizeAt(int p, ILOpcode op)
    {
        if (op == ILOpcode.switch_)
        {
            if (p + 5 > _il.Length) return _il.Length - p;
            long n = (uint)(_il[p + 1] | (_il[p + 2] << 8) | (_il[p + 3] << 16) | (_il[p + 4] << 24));
            long sz = 5 + n * 4;
            return sz > _il.Length - p ? _il.Length - p : (int)sz;
        }
        if (op.IsValid())
            return op.GetSize();
        // IsValid() rejects the prefix opcodes; size them = two opcode bytes plus
        // their own operand.
        const int prefixOpcodeBytes = 2;
        switch (op)
        {
            case ILOpcode.constrained: return prefixOpcodeBytes + 4; // + type token
            case ILOpcode.unaligned: return prefixOpcodeBytes + 1;   // + alignment byte
            case ILOpcode.no: return prefixOpcodeBytes + 1;          // + check-skip flags
            default: return _il[p] == TwoByteEscape ? prefixOpcodeBytes : 1; // volatile./readonly./tail.
        }
    }

    private int OperandOffset(int insnOffset) => _il[insnOffset] == TwoByteEscape ? insnOffset + 2 : insnOffset + 1;

    /// <summary>4-byte metadata-token operand of the instruction at the given start offset.</summary>
    public int TokenAt(int insnOffset)
    {
        int o = OperandOffset(insnOffset);
        return _il[o] | (_il[o + 1] << 8) | (_il[o + 2] << 16) | (_il[o + 3] << 24);
    }

    /// <summary>Resolves a token that came from a real instruction operand (never null-throws).</summary>
    public object Resolve(int token) => _body.GetObject(token, NotFoundBehavior.ReturnNull);

    private bool IsBoundary(int offset) => offset == _il.Length || _offsets.BinarySearch(offset) >= 0;

    /// <summary>Nops out whole instructions in [from, to). Both must be instruction boundaries.</summary>
    public bool NopRange(int from, int to)
    {
        if (from > to || !IsBoundary(from) || !IsBoundary(to)) return false;
        for (int p = from; p < to; p++) _il[p] = (byte)ILOpcode.nop; // 0x00
        return true;
    }

    public RewrittenMethodIL ToIL(Dictionary<int, object> extraTokens = null, int extraMaxStack = 0)
        => new RewrittenMethodIL(_body, _il, extraTokens, extraMaxStack);
}

/// <summary>
/// Presents the IL body of one method (<c>source</c>, typically a C# snippet
/// compiled by Roslyn) as the body of another (<c>owner</c>, the method
/// being compiled). Token resolution stays with the source body, so the snippet
/// may freely reference any member visible in the module it was compiled in
/// (which references the same CoreLib). The owner supplies the signature/generic
/// context, so a static snippet <c>f(TSelf self, ...)</c> transplants cleanly
/// onto an instance method with the same argument layout (arg0 = this = self).
/// </summary>
sealed class SubstituteBodyMethodIL : MethodIL
{
    private readonly MethodDesc _owner;
    private readonly MethodIL _source;

    public SubstituteBodyMethodIL(MethodDesc owner, MethodIL source) { _owner = owner; _source = source; }

    public override MethodDesc OwningMethod => _owner;
    public override int MaxStack => _source.MaxStack;
    public override bool IsInitLocals => _source.IsInitLocals;
    public override byte[] GetILBytes() => _source.GetILBytes();
    public override LocalVariableDefinition[] GetLocals() => _source.GetLocals();
    public override ILExceptionRegion[] GetExceptionRegions() => _source.GetExceptionRegions();
    public override object GetObject(int token, NotFoundBehavior notFoundBehavior = NotFoundBehavior.Throw)
        => _source.GetObject(token, notFoundBehavior);
}

class CustomILProvider : ILProvider
{
    private ILProvider inner;
    private bool zkvmTarget;
    public TypeSystemContext TypeContext;

    /// <summary>
    /// Maps a method being compiled to the C#-snippet method whose body replaces
    /// it (see SubstituteBodyMethodIL). Populated after the snippet module is
    /// compiled and loaded. Checked first in GetMethodIL, ahead of the remaining
    /// targeted IL rewrites (TimeZoneInfo..cctor), so a snippet always wins.
    /// </summary>
    public Dictionary<MethodDesc, MethodDesc> BodySubstitutions;

    public CustomILProvider(ILProvider innerProvider, TypeSystemContext typeContext, bool isZkvmTarget = false)
    {
        inner = innerProvider;
        TypeContext = typeContext;
        zkvmTarget = isZkvmTarget;
    }

    public override MethodIL GetMethodIL(MethodDesc method)
    {
        if (BodySubstitutions != null && BodySubstitutions.TryGetValue(method, out MethodDesc snippet))
            return new SubstituteBodyMethodIL(method, inner.GetMethodIL(snippet));

        // System.Number..cctor (.NET 11) builds s_sqrtTable = new SqrtCoefficients[256],
        // where SqrtCoefficients is a {single, single, double} struct; the 256 elements
        // are filled inline with 768 ldc.r4/ldc.r8 constants (the DiyFp128 sqrt
        // polynomial used only by float FORMATTING). The table's sole reader is
        // Number.DiyFp128Sqrt, reachable only through Number.FormatFloat, which
        // guest_subst's substitutions.xml removes - so the table is dead, and every
        // guest that formats a number runs this cctor. Keep the allocation (a zeroed
        // SqrtCoefficients[256] - harmless even if ever indexed) but nop out the
        // constant-filling body between the newarr and the stsfld: the array stays on
        // the stack across the nops, so the store is still balanced. A C# snippet
        // cannot express this deletion. No-op on runtimes without s_sqrtTable (e.g.
        // .NET 10), so the rewrite is safe to always apply.
        if (zkvmTarget &&
            method.OwningType is MetadataType numType &&
            numType.Namespace.StringEquals("System") &&
            numType.Name.StringEquals("Number") &&
            method.Name.StringEquals(".cctor"))
        {
            MethodIL body = inner.GetMethodIL(method);
            var ed = new ILEditor(body);

            // Find `stsfld s_sqrtTable` and the end of the last preceding
            // `newarr SqrtCoefficients`, then nop the constant-filling
            // instructions between them. ILEditor decodes, so GetObject only
            // ever sees a real stsfld/newarr operand - never a 0x80/0x8D byte
            // that merely happens to sit inside a ldc.r4/ldc.r8 constant (which
            // would resolve a malformed handle and throw BadImageFormat). The
            // array stays on the stack across the nops, so the store balances.
            int stPos = -1, naEnd = -1;
            for (int i = 0; i < ed.Count; i++)
            {
                int off = ed.OffsetOf(i);
                ILOpcode op = ed.OpcodeOf(i);
                if (op == ILOpcode.stsfld
                    && ed.Resolve(ed.TokenAt(off)) is FieldDesc fd && fd.Name.StringEquals("s_sqrtTable"))
                {
                    stPos = off;
                    break; // naEnd already holds the end of the last preceding SqrtCoefficients newarr
                }
                if (op == ILOpcode.newarr && i + 1 < ed.Count
                    && ed.Resolve(ed.TokenAt(off)) is MetadataType mt && mt.Name.StringEquals("SqrtCoefficients"))
                    naEnd = ed.OffsetOf(i + 1); // first instruction of the fill
            }

            if (stPos >= 0 && naEnd >= 0 && naEnd <= stPos && ed.NopRange(naEnd, stPos))
                return ed.ToIL();
            return body;
        }

        if (method.OwningType is MetadataType owningType &&
            owningType.Namespace.StringEquals("System") &&
            owningType.Name.StringEquals("OutOfMemoryException") &&
            method.Name.StringEquals("GetDefaultMessage"))
        {
            var stringType = TypeContext.GetWellKnownType(WellKnownType.String);
            FieldDesc emptyField = null;

            foreach (var field in stringType.GetFields())
            {
                if (field.Name.StringEquals("Empty") && field.IsStatic)
                {
                    emptyField = field;
                    break;
                }
            }

            if (emptyField == null)
            {
                throw new Exception("No Empty field found for OutOfMemoryException");
            }

            return new ILStubMethodIL(
                method,
                new byte[]
                {
                    (byte)ILOpcode.ldsfld, 0x01, 0x00, 0x00, 0x00,
                    (byte)ILOpcode.ret
                },
                Array.Empty<LocalVariableDefinition>(),
                new object[] { emptyField }
            );
        }

        if (method.OwningType is MetadataType owningType2 &&
            owningType2.Namespace.StringEquals("Internal.JitInterface") &&
            owningType2.Name.StringEquals("CorInfoImpl") &&
            method.Name.StringEquals("getAsyncInfo"))
        {
            return new ILStubMethodIL(
                method,
                new byte[]
                {
                    (byte)ILOpcode.ret
                },
                Array.Empty<LocalVariableDefinition>(),
                Array.Empty<object>()
            );
        }

        return inner.GetMethodIL(method);
    }
}

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
}
