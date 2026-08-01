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

// Mechanism for the zisk/zisk_sim IL rewrites: the MethodIL wrappers, the
// byte-level IL editor used to build them, the ILProviders that hand ILC a
// rewritten body, and the scanner behind the --error-on-float gate.
//
// This file is deliberately policy-free. WHICH methods get rewritten, and why,
// lives in ZiskSubstitutions.cs; everything here is the machinery those
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
/// Rewrites the double growth-ratio in ConcurrentUnifierW(Keyed)`2.Container.Resize
/// (live/len &lt; 0.75) into the exactly equivalent integer predicate
/// live*4 &lt; len*3, dropping the only FP instructions from that shared generic
/// method. Applied ONLY to the post-scan (codegen) IL provider: Resize is a
/// shared generic body, so the scanner must see the ORIGINAL IL to compute the
/// correct generic-dictionary dependencies (WeakReference&lt;T&gt; etc.). The
/// rewrite touches no tokens and no generic operations, so scan- and codegen-IL
/// stay dependency-identical - the same split SubstitutedILProvider relies on
/// for dead-branch elimination.
/// </summary>
sealed class UnifierResizeILProvider : ILProvider
{
    private readonly ILProvider _inner;

    public UnifierResizeILProvider(ILProvider inner) => _inner = inner;

    public override MethodIL GetMethodIL(MethodDesc method)
    {
        MethodIL body = _inner.GetMethodIL(method);
        if (body == null ||
            method.OwningType is not MetadataType cont ||
            !cont.Name.StringEquals("Container") ||
            cont.ContainingType is not MetadataType unifier ||
            !unifier.Name.StringStartsWith("ConcurrentUnifierW") ||
            !method.Name.StringEquals("Resize"))
        {
            return body;
        }

        // The FP growth check is the instruction run
        //   ldloc.0; conv.r8; ldarg.0; ldfld _entries; ldlen; conv.i4; conv.r8;
        //   div; ldc.r8 0.75; bge.un.s   (live/len < 0.75 grows the table)
        // Rewrite the compare into the tick-exact integer predicate live*4 < len*3
        // (== live/len < 0.75) and flip the branch to its non-.un form. Same total
        // length and same branch target, so the shared-generic dictionary deps the
        // scanner computed from the original body are untouched.
        var ed = new ILEditor(body);
        ILOpcode[] ratio =
        {
            ILOpcode.ldloc_0, ILOpcode.conv_r8, ILOpcode.ldarg_0, ILOpcode.ldfld,
            ILOpcode.ldlen, ILOpcode.conv_i4, ILOpcode.conv_r8, ILOpcode.div,
            ILOpcode.ldc_r8, ILOpcode.bge_un_s,
        };
        if (!ed.FindSequence(ratio, out int[] at, out _))
            return body;

        int start = at[0];                  // ldloc.0
        int entriesTok = ed.TokenAt(at[3]); // ldfld _entries
        int bge = at[9];                    // bge.un.s opcode byte

        byte[] repl = new ILSnippet()
            .Op(ILOpcode.ldloc_0).Op(ILOpcode.conv_i8).LdcI4(4).Op(ILOpcode.conv_i8).Op(ILOpcode.mul)   // live*4
            .Op(ILOpcode.ldarg_0).Token(ILOpcode.ldfld, entriesTok).Op(ILOpcode.ldlen).Op(ILOpcode.conv_i4)
            .Op(ILOpcode.conv_i8).LdcI4(3).Op(ILOpcode.conv_i8).Op(ILOpcode.mul)                         // len*3
            .ToArray();
        // Replace the double ratio compare [start, bge) with the integer form
        // (Replace nop-pads the slack) and flip bge.un.s -> bge.s in place.
        if (!ed.Replace(start, bge, repl) || !ed.SetOpcode(bge, ILOpcode.bge_s))
            return body;

        // Integer form holds live*4 while computing len*3: one slot deeper than
        // the original double ratio peak.
        return ed.ToIL(extraMaxStack: 1);
    }
}

/// <summary>
/// Instruction-boundary-aware editor over a method body's IL. The surgical zkVM
/// rewrites below express their edits in terms of DECODED instructions - find by
/// opcode/operand, nop or overwrite whole instructions - instead of raw byte
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

    /// <summary>double operand of a ldc.r8 at the given start offset.</summary>
    public double ReadR8At(int insnOffset) => BitConverter.ToDouble(_il, OperandOffset(insnOffset));

    /// <summary>Resolves a token that came from a real instruction operand (never null-throws).</summary>
    public object Resolve(int token) => _body.GetObject(token, NotFoundBehavior.ReturnNull);

    /// <summary>
    /// Finds the unique run of decoded instructions whose opcodes equal <paramref name="seq"/>.
    /// Returns the run's instruction start offsets (one per opcode) and the byte offset
    /// just past the last one, or false if the run is absent or occurs more than once
    /// (ambiguity is refused - safer than rewriting the wrong site).
    /// </summary>
    public bool FindSequence(ILOpcode[] seq, out int[] offsets, out int end)
    {
        offsets = null; end = -1;
        int foundIdx = -1;
        for (int i = 0; i + seq.Length <= _opcodes.Count; i++)
        {
            bool ok = true;
            for (int j = 0; j < seq.Length; j++)
                if (_opcodes[i + j] != seq[j]) { ok = false; break; }
            if (!ok) continue;
            if (foundIdx >= 0) return false; // ambiguous
            foundIdx = i;
        }
        if (foundIdx < 0) return false;
        offsets = new int[seq.Length];
        for (int j = 0; j < seq.Length; j++) offsets[j] = _offsets[foundIdx + j];
        int lastIdx = foundIdx + seq.Length;
        end = lastIdx < _offsets.Count ? _offsets[lastIdx] : _il.Length;
        return true;
    }

    private bool IsBoundary(int offset) => offset == _il.Length || _offsets.BinarySearch(offset) >= 0;

    /// <summary>Nops out whole instructions in [from, to). Both must be instruction boundaries.</summary>
    public bool NopRange(int from, int to)
    {
        if (from > to || !IsBoundary(from) || !IsBoundary(to)) return false;
        for (int p = from; p < to; p++) _il[p] = (byte)ILOpcode.nop; // 0x00
        return true;
    }

    /// <summary>
    /// Replaces the whole instructions in [from, to) with <paramref name="snippet"/>,
    /// padding any leftover bytes up to <paramref name="to"/> with nops. Both ends
    /// must be instruction boundaries and the snippet must fit.
    /// </summary>
    public bool Replace(int from, int to, byte[] snippet)
    {
        if (!IsBoundary(from) || !IsBoundary(to) || snippet.Length > to - from) return false;
        Array.Copy(snippet, 0, _il, from, snippet.Length);
        for (int p = from + snippet.Length; p < to; p++) _il[p] = (byte)ILOpcode.nop;
        return true;
    }

    /// <summary>Replaces one instruction's opcode with a same-size single-byte opcode.</summary>
    public bool SetOpcode(int insnOffset, ILOpcode op)
    {
        if (!IsBoundary(insnOffset) || (int)op > 0xFF) return false;
        _il[insnOffset] = (byte)op;
        return true;
    }

    public RewrittenMethodIL ToIL(Dictionary<int, object> extraTokens = null, int extraMaxStack = 0)
        => new RewrittenMethodIL(_body, _il, extraTokens, extraMaxStack);
}

/// <summary>
/// Builds a short, branch-free run of IL as a byte array from named opcodes and
/// typed operands - so the zkVM rewrites express their integer replacement bodies
/// as readable instructions instead of hand-written opcode bytes. Only the
/// single-byte opcodes and the LdcI4/LdcI8/token operands the rewrites need are
/// supported.
/// </summary>
sealed class ILSnippet
{
    private readonly List<byte> _b = new List<byte>();

    public ILSnippet Op(ILOpcode op)
    {
        if ((int)op > 0xFF) throw new NotSupportedException("ILSnippet emits single-byte opcodes only");
        _b.Add((byte)op);
        return this;
    }

    public ILSnippet LdcI4(int v)
    {
        switch (v)
        {
            case -1: return Op(ILOpcode.ldc_i4_m1);
            case 0: return Op(ILOpcode.ldc_i4_0);
            case 1: return Op(ILOpcode.ldc_i4_1);
            case 2: return Op(ILOpcode.ldc_i4_2);
            case 3: return Op(ILOpcode.ldc_i4_3);
            case 4: return Op(ILOpcode.ldc_i4_4);
            case 5: return Op(ILOpcode.ldc_i4_5);
            case 6: return Op(ILOpcode.ldc_i4_6);
            case 7: return Op(ILOpcode.ldc_i4_7);
            case 8: return Op(ILOpcode.ldc_i4_8);
        }
        if (v >= sbyte.MinValue && v <= sbyte.MaxValue) { Op(ILOpcode.ldc_i4_s); _b.Add((byte)(sbyte)v); return this; }
        Op(ILOpcode.ldc_i4); AddInt32(v); return this;
    }

    public ILSnippet LdcI8(long v)
    {
        Op(ILOpcode.ldc_i8);
        for (int i = 0; i < 8; i++) _b.Add((byte)(v >> (8 * i)));
        return this;
    }

    /// <summary>Emits a token-carrying opcode (call/newobj/ldfld/...) with its 4-byte token.</summary>
    public ILSnippet Token(ILOpcode op, int token) { Op(op); AddInt32(token); return this; }

    private void AddInt32(int v) { for (int i = 0; i < 4; i++) _b.Add((byte)(v >> (8 * i))); }

    public byte[] ToArray() => _b.ToArray();
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

        // zkVM (rv64ima) IL rewrites the C# snippets cannot express. Most FP
        // elimination is done by whole-body C# snippets (see the zisk.snippets.cs
        // resource, compiled by ZiskSubstitutions.CompileSnippets and applied through
        // BodySubstitutions above): Hashtable ctors/rehash, ValueType hashing,
        // Random sampling, LengthBuckets and the whole TimeZoneInfo..cctor (a
        // drift-guarded donor - see ZiskSubstitutions) all live there
        // now. What remains here are the two cases a snippet cannot express:
        //   * Number..cctor - a DELETION (drop the dead float-table fill), with
        //     no replacement logic to write in C#;
        //   * ConcurrentUnifierW`2.Container.Resize - a SHARED GENERIC body the
        //     scanner must see in its ORIGINAL form to compute correct generic-
        //     dictionary dependencies, so it is rewritten codegen-only (and
        //     token-free) by UnifierResizeILProvider (snippets apply in BOTH
        //     phases and would introduce a call token the scanner never saw).

        // System.Number..cctor (.NET 11) builds s_sqrtTable = new SqrtCoefficients[256],
        // where SqrtCoefficients is a {single, single, double} struct; the 256 elements
        // are filled inline with 768 ldc.r4/ldc.r8 constants (the DiyFp128 sqrt
        // polynomial used only by float FORMATTING). RyuJIT materializes each float
        // constant with flw/fld + fsw/fsd - this is the dominant FP in a .NET 11 image
        // (~1536 instructions). The table's sole reader is Number.DiyFp128Sqrt, reachable
        // only through Number.FormatFloat, which zisk.substitutions.xml removes - so the
        // table is dead. Keep the allocation (a zeroed SqrtCoefficients[256] - harmless
        // even if ever indexed) but nop out the constant-filling body between the newarr
        // and the stsfld: the array stays on the stack across the nops, so the store is
        // still balanced, and no float constant survives. No-op on runtimes without
        // s_sqrtTable (e.g. .NET 10), so the rewrite is safe to always apply.
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

        // NOTE: ValueType.RegularGetValueTypeHashCode (Single/Double struct fields
        // hashed by value through FP registers), the whole System.Collections.
        // Hashtable ctor/rehash family (the `float _loadFactor` load factor) and
        // System.Random's compat-PRNG double sampling are all eliminated by
        // whole-body C# snippets now (zisk.snippets.cs / BodySubstitutions) rather
        // than hand-emitted IL - Roslyn guarantees each replacement body is valid.

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
