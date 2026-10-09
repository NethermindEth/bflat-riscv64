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

    /// <summary>Writes il.json (post-substitution IL of every compiled method of
    /// <paramref name="module"/>), a copy of the object file, and the manifest.</summary>
    public static void Write(string dir, EcmaModule module, ILProvider ilProvider,
        IEnumerable<MethodDesc> compiled, string objectFile, string jitPath)
    {
        var methods = new List<Dictionary<string, object>>();
        var unsupported = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (MethodDesc m in compiled.OrderBy(m => m.ToString(), StringComparer.Ordinal))
        {
            if (m.GetTypicalMethodDefinition() is not EcmaMethod em || em.Module != module)
                continue;
            if (m.HasInstantiation || m.OwningType.HasInstantiation)
            {
                unsupported[m.ToString()] = "generic";
                continue;
            }

            MethodIL il;
            try { il = ilProvider.GetMethodIL(m); }
            catch (Exception e) { unsupported[m.ToString()] = e.GetType().Name; continue; }
            if (il == null)
            {
                unsupported[m.ToString()] = "no IL";
                continue;
            }

            byte[] bytes = il.GetILBytes();
            byte[] original = EcmaMethodIL.Create(em).GetILBytes();
            var sig = m.Signature;

            methods.Add(new Dictionary<string, object>
            {
                ["name"] = Name(m),
                ["signature"] = m.ToString(),
                ["token"] = MetadataTokens.GetToken(em.Handle),
                ["static"] = sig.IsStatic,
                ["il"] = Convert.ToHexString(bytes),
                ["initLocals"] = il.IsInitLocals,
                ["ehRegions"] = il.GetExceptionRegions().Length,
                ["args"] = Enumerable.Range(0, sig.Length).Select(i => Ty(sig[i])).ToList(),
                ["locals"] = il.GetLocals().Select(l => Ty(l.Type)).ToList(),
                ["ret"] = sig.ReturnType.IsVoid ? null : Ty(sig.ReturnType),
                // bflat rewrote the body (ZkvmIL, substitutions): what was
                // compiled differs from the assembly
                ["rewritten"] = original == null || !bytes.AsSpan().SequenceEqual(original),
                ["callees"] = Callees(il, bytes),
            });
        }

        File.WriteAllText(Path.Combine(dir, IlFile), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["module"] = module.Assembly.GetName().Name,
            ["methods"] = methods,
            ["skipped"] = unsupported,
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

        Console.WriteLine($"--proof: {methods.Count} method(s) of {module.Assembly.GetName().Name} dumped to {dir}" +
            (unsupported.Count > 0 ? $" ({unsupported.Count} skipped)" : ""));
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

    /// <summary>Storage type in il2rv64's semantics.</summary>
    static string Ty(TypeDesc t) => t.Category switch
    {
        TypeFlags.Boolean or TypeFlags.Byte => "u1",
        TypeFlags.SByte => "i1",
        TypeFlags.Int16 => "i2",
        TypeFlags.UInt16 or TypeFlags.Char => "u2",
        TypeFlags.Int32 or TypeFlags.UInt32 => "i4",
        TypeFlags.Int64 or TypeFlags.UInt64 or TypeFlags.IntPtr or TypeFlags.UIntPtr
            or TypeFlags.Pointer or TypeFlags.FunctionPointer => "i8",
        _ => "unsupported:" + t,
    };

    /// <summary>The callee of every `call` in the body, by the token in the IL.
    /// Operand sizes follow ECMA-335 III.1.2.1.</summary>
    static List<Dictionary<string, object>> Callees(MethodIL il, byte[] b)
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
            if (op == 0x28)
            {
                int token = BitConverter.ToInt32(b, i + 1);
                if (seen.Add(token) && il.GetObject(token) is MethodDesc callee)
                {
                    var sig = callee.Signature;
                    var typical = callee.GetTypicalMethodDefinition() as EcmaMethod;
                    result.Add(new Dictionary<string, object>
                    {
                        ["token"] = token,
                        ["name"] = callee.OwningType is MetadataType ? Name(callee) : callee.ToString(),
                        ["signature"] = callee.ToString(),
                        ["module"] = typical?.Module.Assembly.GetName().Name,
                        ["static"] = sig.IsStatic,
                        ["args"] = Enumerable.Range(0, sig.Length).Select(k => Ty(sig[k])).ToList(),
                        ["ret"] = sig.ReturnType.IsVoid ? null : Ty(sig.ReturnType),
                    });
                }
            }
            i += 1 + size;
        }
        return result;
    }
}
