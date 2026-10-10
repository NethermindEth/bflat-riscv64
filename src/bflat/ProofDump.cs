// --proof: the inputs of il2rv64 translation validation.
//
// il2rv64 proves in Lean that the RISC-V code of a program refines its CIL.
// It needs, for the methods it checks:
//
//   * the IL that RyuJIT actually compiled: after bflat's substitutions and
//     IL rewrites, i.e. what the compilation's IL provider hands the JIT. It is
//     dumped here (il.json), with the types the semantics needs and the callee
//     of every `call`;
//   * the certificate hints the JIT writes when `JitIlRvCertFile` is set
//     (jitcert.jsonl). This needs a RyuJIT built with il2rv64's
//     jit/ilrvcert.patch; --jit selects such a JIT if the bundled one lacks it;
//   * the machine code: the object file, copied before linking.
//
// Nothing written here is trusted by the checker: it decodes the IL bytes and
// machine words itself and re-checks every certificate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text.Json;

using ILCompiler;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

static class ProofDump
{
    public const string IlFile = "il.json";
    public const string JitCertFile = "jitcert.jsonl";
    public const string ManifestFile = "proof.json";

    /// <summary>Prepares the proof directory and returns the JIT option that
    /// makes RyuJIT write certificate records into it.</summary>
    public static string Prepare(string dir)
    {
        Directory.CreateDirectory(dir);
        string certs = Path.GetFullPath(Path.Combine(dir, JitCertFile));
        // the JIT appends; start from an empty file
        File.WriteAllText(certs, "");
        return $"JitIlRvCertFile={certs}";
    }

    /// <summary>Writes il.json (post-substitution IL of every compiled method),
    /// a copy of the object file, and the manifest.</summary>
    public static void Write(string dir, EcmaModule module, ILProvider ilProvider, NameMangler mangler,
        IEnumerable<MethodDesc> compiled, string objectFile, string jitPath)
    {
        var list = compiled.OrderBy(m => m.ToString(), StringComparer.Ordinal).ToList();
        int compiledCount = list.Count;
        // ids are positions in this list; callees refer to them. Callees
        // without a body of their own (inlined everywhere) are appended, so
        // that the checker has their IL.
        var ids = new Dictionary<MethodDesc, int>();
        for (int i = 0; i < list.Count; i++)
            ids[list[i]] = i;
        for (int i = 0; i < list.Count; i++)
        {
            MethodIL calleeScan;
            try { calleeScan = ilProvider.GetMethodIL(list[i]); }
            catch (Exception) { continue; }
            if (calleeScan == null)
                continue;
            foreach (MethodDesc callee in CalleeMethods(calleeScan))
            {
                if (ids.ContainsKey(callee))
                    continue;
                ids[callee] = list.Count;
                list.Add(callee);
            }
        }

        var methods = new List<Dictionary<string, object>>();
        for (int i = 0; i < list.Count; i++)
        {
            MethodDesc m = list[i];
            var e = new Dictionary<string, object>
            {
                ["id"] = i,
                ["name"] = Name(m),
                ["signature"] = m.ToString(),
                ["symbol"] = mangler.GetMangledMethodName(m).ToString(),
                ["module"] = (m.GetTypicalMethodDefinition() as EcmaMethod)?.Module.Assembly.GetName().Name,
                ["generic"] = m.HasInstantiation || m.OwningType.HasInstantiation,
                // shared code reads its instantiation from a generic dictionary
                ["shared"] = m.IsSharedByGenericInstantiations,
                // what RyuJIT's certificate records as the method's hash
                // (CorInfoImpl.getMethodHash)
                ["hash"] = (uint)m.GetHashCode(),
                ["compiled"] = i < compiledCount,
            };
            methods.Add(e);

            MethodIL il;
            try { il = ilProvider.GetMethodIL(m); }
            catch (Exception ex) { e["error"] = ex.GetType().Name; continue; }
            if (il == null)
            {
                e["error"] = "no IL";
                continue;
            }

            byte[] bytes = il.GetILBytes();
            var sig = m.Signature;
            e["static"] = sig.IsStatic;
            e["il"] = Convert.ToHexString(bytes);
            e["initLocals"] = il.IsInitLocals;
            e["ehRegions"] = il.GetExceptionRegions().Length;
            e["args"] = Args(m);
            e["locals"] = il.GetLocals().Select(l => Ty(l.Type)).ToList();
            e["ret"] = sig.ReturnType.IsVoid ? null : Ty(sig.ReturnType);
            if (m.GetTypicalMethodDefinition() is EcmaMethod em)
            {
                e["token"] = MetadataTokens.GetToken(em.Handle);
                // bflat rewrote the body (ZkvmIL, substitutions): what was
                // compiled differs from the assembly
                // (no metadata body: extern, intrinsic, or provided by bflat)
                if (!m.HasInstantiation && !m.OwningType.HasInstantiation)
                    e["rewritten"] = em.MetadataReader.GetMethodDefinition(em.Handle).RelativeVirtualAddress == 0
                        || !bytes.AsSpan().SequenceEqual(EcmaMethodIL.Create(em).GetILBytes());
            }
            var callees = Callees(il, bytes, ids, mangler);
            foreach (var c in callees)
                c.Remove("target");
            e["callees"] = callees;
            e["fields"] = Fields(il, bytes, mangler);
        }

        File.WriteAllText(Path.Combine(dir, IlFile), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["module"] = module.Assembly.GetName().Name,
            ["methods"] = methods,
        }, new JsonSerializerOptions { WriteIndented = true }));

        string objectCopy = Path.GetFileName(objectFile);
        File.Copy(objectFile, Path.Combine(dir, objectCopy), overwrite: true);

        MethodDesc entry = module.EntryPoint;
        File.WriteAllText(Path.Combine(dir, ManifestFile), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["entry"] = entry == null ? null : Name(entry),
            ["il"] = IlFile,
            ["jit"] = JitCertFile,
            ["object"] = objectCopy,
            ["jitPath"] = jitPath,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"--proof: {methods.Count} compiled method(s) dumped to {dir}");
    }

    /// <summary>"Namespace.Type.Method", the name il2rv64 uses.</summary>
    static string Name(MethodDesc m)
    {
        // Name/Namespace are strings before .NET 11 and Utf8Span after
        var t = (MetadataType)m.OwningType;
        string type = t.Name.ToString();
        for (var outer = t.ContainingType; outer != null; outer = outer.ContainingType)
            type = outer.Name.ToString() + "+" + type;
        var top = t;
        while (top.ContainingType != null) top = top.ContainingType;
        string ns = top.Namespace.ToString();
        return (ns.Length > 0 ? ns + "." : "") + type + "." + m.Name.ToString();
    }

    /// <summary>Storage type in il2rv64's semantics; other types are named by
    /// kind so that the checker can say what it does not support.</summary>
    static string Ty(TypeDesc t) => t.Category switch
    {
        TypeFlags.Boolean or TypeFlags.Byte => "u1",
        TypeFlags.SByte => "i1",
        TypeFlags.Int16 => "i2",
        TypeFlags.UInt16 or TypeFlags.Char => "u2",
        TypeFlags.Int32 or TypeFlags.UInt32 => "i4",
        TypeFlags.Int64 or TypeFlags.UInt64 or TypeFlags.IntPtr or TypeFlags.UIntPtr
            or TypeFlags.Pointer or TypeFlags.FunctionPointer => "i8",
        TypeFlags.Single or TypeFlags.Double => "float",
        TypeFlags.ByRef => "byref",
        TypeFlags.Enum => Ty(t.UnderlyingType),
        TypeFlags.ValueType or TypeFlags.Nullable => $"struct:{t.GetElementSize().AsInt}",
        TypeFlags.Class or TypeFlags.Interface or TypeFlags.Array or TypeFlags.SzArray => "ref",
        _ => "other:" + t.Category,
    };

    /// <summary>IL argument types: `this` first for instance methods (an object
    /// reference, or a byref for a value type).</summary>
    static List<string> Args(MethodDesc m)
    {
        var sig = m.Signature;
        var args = new List<string>();
        if (!sig.IsStatic)
            args.Add(m.OwningType.IsValueType ? "byref" : "ref");
        for (int k = 0; k < sig.Length; k++)
            args.Add(Ty(sig[k]));
        return args;
    }

    /// <summary>The field of every ldfld/stfld/ldsfld/stsfld/ldflda/ldsflda in
    /// the body, by the token in the IL: offset from the object reference,
    /// type, and for statics the symbol of the static base plus the offset.</summary>
    static List<Dictionary<string, object>> Fields(MethodIL il, byte[] b, NameMangler mangler)
    {
        var result = new List<Dictionary<string, object>>();
        var seen = new HashSet<int>();
        for (int i = 0; i < b.Length;)
        {
            int op = b[i];
            if (op == 0xfe)
            {
                int sub = b[i + 1];
                i += 2 + sub switch
                {
                    0x09 or 0x0a or 0x0b or 0x0c or 0x0d or 0x0e => 2,
                    0x12 or 0x19 => 1,
                    0x06 or 0x07 or 0x15 or 0x16 or 0x1c => 4,
                    _ => 0,
                };
                continue;
            }
            if (op is >= 0x7b and <= 0x80)
            {
                int token = BitConverter.ToInt32(b, i + 1);
                if (seen.Add(token) && il.GetObject(token) is FieldDesc fd)
                {
                    var e = new Dictionary<string, object>
                    {
                        ["token"] = token,
                        ["name"] = fd.ToString(),
                        ["type"] = Ty(fd.FieldType),
                        ["static"] = fd.IsStatic,
                    };
                    try { e["offset"] = fd.Offset.AsInt; }
                    catch (Exception) { e["offset"] = null; }
                    if (fd.IsStatic)
                    {
                        e["threadStatic"] = fd.IsThreadStatic;
                        e["gc"] = fd.HasGCStaticBase;
                        e["base"] = (fd.HasGCStaticBase
                            ? mangler.NodeMangler.GCStatics(fd.OwningType)
                            : mangler.NodeMangler.NonGCStatics(fd.OwningType)).ToString();
                    }
                    result.Add(e);
                }
            }
            i += 1 + op switch
            {
                0x0e or 0x0f or 0x10 or 0x11 or 0x12 or 0x13 or 0x1f or (>= 0x2b and <= 0x37) or 0xde => 1,
                0x20 or 0x22 or 0x27 or 0x28 or 0x29 or (>= 0x38 and <= 0x44) or 0x6f or 0x70 or 0x71
                    or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or 0x7b or 0x7c or 0x7d or 0x7e or 0x7f
                    or 0x80 or 0x81 or 0x8c or 0x8d or 0x8f or 0xa3 or 0xa4 or 0xa5 or 0xc2 or 0xc6
                    or 0xd0 or 0xdd => 4,
                0x21 or 0x23 => 8,
                0x45 => 4 + 4 * BitConverter.ToInt32(b, i + 1),
                _ => 0,
            };
        }
        return result;
    }

    /// <summary>For `newobj` of a class the JIT allocates inline (the zkVM
    /// bump allocator, helper CORINFO_HELP_NEWSFAST): the payload the JIT
    /// reserves (getHeapClassSize, at least 0x18, 8-aligned) and the symbol of
    /// the MethodTable it stores. Null for any other allocation.</summary>
    static Dictionary<string, object> Alloc(TypeDesc type, NameMangler mangler)
    {
        if (type is not DefType dt || type.IsValueType || type.IsArray || type.IsString
            || type.HasFinalizer || type.RequiresAlign8() || type.IsCanonicalSubtype(CanonicalFormKind.Any))
            return null;
        int size = Math.Max(dt.InstanceByteCount.AsInt, 0x18);
        return new Dictionary<string, object>
        {
            ["payload"] = (size + 7) & ~7,
            ["methodTable"] = mangler.NodeMangler.MethodTable(type).ToString(),
        };
    }

    /// <summary>The methods the body calls (call, callvirt, newobj).</summary>
    static IEnumerable<MethodDesc> CalleeMethods(MethodIL il)
    {
        byte[] b = il.GetILBytes();
        if (b == null)
            yield break;
        foreach (var c in Callees(il, b, new Dictionary<MethodDesc, int>(), null))
            if (c["target"] is MethodDesc md)
                yield return md;
    }

    /// <summary>The callee of every `call` in the body, by the token in the IL.
    /// Operand sizes follow ECMA-335 III.1.2.1.</summary>
    static List<Dictionary<string, object>> Callees(MethodIL il, byte[] b, Dictionary<MethodDesc, int> ids,
        NameMangler mangler)
    {
        var result = new List<Dictionary<string, object>>();
        var seen = new HashSet<int>();
        for (int i = 0; i < b.Length;)
        {
            int op = b[i];
            if (op == 0xfe)
            {
                int sub = b[i + 1];
                i += 2 + sub switch
                {
                    0x09 or 0x0a or 0x0b or 0x0c or 0x0d or 0x0e => 2,
                    0x12 or 0x19 => 1,
                    0x06 or 0x07 or 0x15 or 0x16 or 0x1c => 4,
                    _ => 0,
                };
                continue;
            }
            int size = op switch
            {
                0x0e or 0x0f or 0x10 or 0x11 or 0x12 or 0x13 or 0x1f or (>= 0x2b and <= 0x37) or 0xde => 1,
                0x20 or 0x22 or 0x27 or 0x28 or 0x29 or (>= 0x38 and <= 0x44) or 0x6f or 0x70 or 0x71
                    or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or 0x7b or 0x7c or 0x7d or 0x7e or 0x7f
                    or 0x80 or 0x81 or 0x8c or 0x8d or 0x8f or 0xa3 or 0xa4 or 0xa5 or 0xc2 or 0xc6
                    or 0xd0 or 0xdd => 4,
                0x21 or 0x23 => 8,
                0x45 => 4 + 4 * BitConverter.ToInt32(b, i + 1),
                _ => 0,
            };
            // call, callvirt, newobj, ldftn-free forms: the target method
            if (op is 0x28 or 0x6f or 0x73)
            {
                int token = BitConverter.ToInt32(b, i + 1);
                if (seen.Add(token) && il.GetObject(token) is MethodDesc callee)
                {
                    var sig = callee.Signature;
                    var entry = new Dictionary<string, object>
                    {
                        ["token"] = token,
                        ["id"] = ids.TryGetValue(callee, out int id) ? id : null,
                        ["name"] = callee.OwningType is MetadataType ? Name(callee) : callee.ToString(),
                        ["signature"] = callee.ToString(),
                        ["static"] = sig.IsStatic,
                        ["virtual"] = callee.IsVirtual,
                        ["intrinsic"] = callee.IsIntrinsic,
                        ["args"] = Args(callee),
                        ["ret"] = sig.ReturnType.IsVoid ? null : Ty(sig.ReturnType),
                        // for CalleeMethods; not serialized (removed below)
                        ["target"] = callee,
                    };
                    if (op == 0x73 && mangler != null && Alloc(callee.OwningType, mangler) is { } alloc)
                        entry["alloc"] = alloc;
                    result.Add(entry);
                }
            }
            i += 1 + size;
        }
        return result;
    }
}
