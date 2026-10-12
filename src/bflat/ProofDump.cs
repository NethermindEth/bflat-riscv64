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
using ILCompiler.DependencyAnalysis;
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
    static NodeFactory s_factory;

    public static void Write(string dir, EcmaModule module, ILProvider ilProvider, NameMangler mangler,
        IEnumerable<MethodDesc> compiled, string objectFile, string jitPath, NodeFactory factory)
    {
        s_factory = factory;
        // method body folding: a method whose code is identical to another's
        // has no symbol of its own; its code is at the other one's
        var interner = typeof(NodeFactory).GetProperty("ObjectInterner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance)?.GetValue(factory) as ObjectDataInterner;
        var list = compiled.OrderBy(m => m.ToString(), StringComparer.Ordinal).ToList();
        int compiledCount = list.Count;
        // ids are positions in this list; callees refer to them. Callees
        // without a body of their own (inlined everywhere) are appended, so
        // that the checker has their IL.
        var ids = new Dictionary<MethodDesc, int>();
        for (int i = 0; i < list.Count; i++)
            ids[list[i]] = i;
        // the methods whose address some `ldftn` takes: what a `calli` may reach
        var ftnTargets = new List<MethodDesc>();
        for (int i = 0; i < list.Count; i++)
        {
            MethodIL calleeScan;
            try { calleeScan = ilProvider.GetMethodIL(list[i]); }
            catch (Exception) { continue; }
            if (calleeScan == null)
                continue;
            List<MethodDesc> scanned;
            // a token that does not resolve (missing assembly) leaves the
            // method's callees out; its own entry records the error
            try { scanned = CalleeMethods(calleeScan).ToList(); }
            catch (Exception) { continue; }
            try
            {
                foreach (var (_, t) in Ftns(calleeScan))
                {
                    scanned.Add(t);
                    if (!ftnTargets.Contains(t)) ftnTargets.Add(t);
                }
            }
            catch (Exception) { }
            // the implementations a virtual call may reach are callees too
            // (possibly without code of their own: devirtualized and inlined)
            foreach (MethodDesc callee in scanned.ToList())
                if (callee.IsVirtual && !callee.IsFinal
                    && !(callee.OwningType is MetadataType cot && cot.IsSealed))
                {
                    try { scanned.AddRange(VirtualImpls(callee).Select(x => x.Impl)); }
                    catch (Exception) { }
                }
            foreach (MethodDesc callee in scanned)
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
                ["symbol"] = SafeSymbol(mangler, m),
                ["module"] = (m.GetTypicalMethodDefinition() as EcmaMethod)?.Module.Assembly.GetName().Name,
                ["generic"] = m.HasInstantiation || m.OwningType.HasInstantiation,
                // shared code reads its instantiation from a generic dictionary
                ["shared"] = m.IsSharedByGenericInstantiations,
                // shared code that takes its generic context as a hidden argument
                ["instArg"] = SafeInstArg(m),
                // what RyuJIT's certificate records as the method's hash
                // (CorInfoImpl.getMethodHash)
                ["hash"] = (uint)m.GetHashCode(),
                ["compiled"] = i < compiledCount,
            };
            if (i < compiledCount && interner != null)
            {
                try
                {
                    ISymbolNode body = factory.MethodEntrypoint(m);
                    ISymbolNode canon = interner.GetDeduplicatedSymbol(factory, body);
                    if (!ReferenceEquals(canon, body))
                        e["codeSymbol"] = canon.GetMangledName(mangler);
                }
                catch (Exception) { }
            }
            methods.Add(e);
            try { Describe(m, e); }
            catch (Exception ex) { e["error"] = ex.GetType().Name + ": " + ex.Message; }
        }

        void Describe(MethodDesc m, Dictionary<string, object> e)
        {
            MethodIL il;
            try { il = ilProvider.GetMethodIL(m); }
            catch (Exception ex) { e["error"] = ex.GetType().Name; return; }
            if (il == null)
            {
                e["error"] = "no IL";
                return;
            }

            byte[] bytes = il.GetILBytes();
            var sig = m.Signature;
            e["static"] = sig.IsStatic;
            e["il"] = Convert.ToHexString(bytes);
            e["initLocals"] = il.IsInitLocals;
            e["ehRegions"] = il.GetExceptionRegions().Length;
            if (il.GetExceptionRegions().Length > 0)
                e["eh"] = EhClauses(il, mangler);
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
            {
                c.Remove("target");
                c.Remove("ctargets");
            }
            e["callees"] = callees;
            e["fields"] = Fields(il, bytes, mangler);
            var ftns = Ftns(il).Select(x => new Dictionary<string, object>
            {
                ["token"] = x.Token,
                ["id"] = ids.TryGetValue(x.Target, out int fid) ? fid : null,
                ["name"] = x.Target.OwningType is MetadataType ? Name(x.Target) : x.Target.ToString(),
                ["static"] = x.Target.Signature.IsStatic,
                ["virtual"] = x.Target.IsVirtual,
                // shared code: the pointer is a fat one (entry and generic context)
                ["shared"] = x.Target.IsSharedByGenericInstantiations || x.Target.GetCanonMethodTarget(CanonicalFormKind.Specific) != x.Target,
            }).ToList();
            if (ftns.Count > 0) e["ftns"] = ftns;
            var callis = Callis(il).Select(x => new Dictionary<string, object>
            {
                ["token"] = x.Token,
                ["managed"] = (x.Sig.Flags & MethodSignatureFlags.UnmanagedCallingConventionMask) == 0,
                ["static"] = x.Sig.IsStatic,
                ["args"] = Enumerable.Range(0, x.Sig.Length).Select(k => Ty(x.Sig[k])).ToList(),
                ["ret"] = x.Sig.ReturnType.IsVoid ? null : Ty(x.Sig.ReturnType),
                ["targets"] = ftnTargets.Where(t => SameSig(t.Signature, x.Sig) && ids.ContainsKey(t))
                    .Select(t => ids[t]).ToList(),
            }).ToList();
            if (callis.Count > 0) e["callis"] = callis;
            e["strings"] = Strings(il, bytes, mangler);
            e["types"] = Types(il, bytes, mangler);
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
        TypeFlags.ValueType or TypeFlags.Nullable => StructTy(t),
        TypeFlags.Class or TypeFlags.Interface or TypeFlags.Array or TypeFlags.SzArray => "ref",
        _ => "other:" + t.Category,
    };

    /// <summary>A value type: "struct:size", then ":" and the offsets of its
    /// padding bytes (no instance field, at any depth, covers them).</summary>
    static string StructTy(TypeDesc t)
    {
        int n = t.GetElementSize().AsInt;
        var covered = new bool[n];
        Cover(t, 0, covered);
        var pad = Enumerable.Range(0, n).Where(i => !covered[i]).ToList();
        return pad.Count == 0 ? $"struct:{n}" : $"struct:{n}:{string.Join(",", pad)}";
    }

    static void Cover(TypeDesc t, int at, bool[] covered)
    {
        foreach (var f in t.GetFields())
        {
            if (f.IsStatic)
                continue;
            var ft = f.FieldType;
            int off = at + f.Offset.AsInt;
            if (ft.IsValueType && ft.Category is TypeFlags.ValueType or TypeFlags.Nullable)
            {
                Cover(ft, off, covered);
                continue;
            }
            int size = ft.GetElementSize().AsInt;
            for (int i = off; i < off + size && i < covered.Length; i++)
                covered[i] = true;
        }
    }

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
                        // data the image holds (an RVA field): its read-only symbol
                        if (fd is Internal.TypeSystem.Ecma.EcmaField ef && ef.HasRva && s_factory != null)
                        {
                            try
                            {
                                var rva = s_factory.FieldRvaData(ef);
                                e["rva"] = rva.GetMangledName(mangler);
                            }
                            catch (Exception) { }
                        }
                        // a readonly static of a type initialized at build time:
                        // its value (the JIT folds reads; no storage may exist)
                        if (fd.IsInitOnly && !fd.IsThreadStatic && !fd.HasRva && s_factory != null
                            && fd.OwningType is MetadataType pmt)
                        {
                            try
                            {
                                if (s_factory.PreinitializationManager.IsPreinitialized(pmt))
                                {
                                    var val = s_factory.PreinitializationManager.GetPreinitializationInfo(pmt).GetFieldValue(fd);
                                    var odb = new ObjectDataBuilder(s_factory, false);
                                    odb.RequireInitialAlignment(1);
                                    if (val == null)
                                        odb.EmitZeros(fd.FieldType.IsValueType ? fd.FieldType.GetElementSize().AsInt : 8);
                                    else
                                        val.WriteFieldData(ref odb, s_factory);
                                    var od = odb.ToObjectData();
                                    e["preinit"] = Convert.ToHexString(od.Data);
                                    if (od.Relocs.Length == 1 && od.Relocs[0].Offset == 0)
                                        e["preinitSym"] = ((ISymbolNode)od.Relocs[0].Target).GetMangledName(mangler);
                                    else if (od.Relocs.Length > 0)
                                        e["preinitError"] = "relocs";
                                }
                            }
                            catch (Exception ex) { e["preinitError"] = ex.GetType().Name; }
                        }
                        e["threadStatic"] = fd.IsThreadStatic;
                        e["gc"] = fd.HasGCStaticBase;
                        e["base"] = (fd.HasGCStaticBase
                            ? mangler.NodeMangler.GCStatics(fd.OwningType)
                            : mangler.NodeMangler.NonGCStatics(fd.OwningType)).ToString();
                        // a class constructor the runtime runs on first access
                        // (not preinitialized): the code checks for it
                        try { e["cctor"] = s_factory?.PreinitializationManager.HasLazyStaticConstructor(fd.OwningType) ?? true; }
                        catch (Exception) { e["cctor"] = true; }
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

    /// <summary>Offsets and opcodes of the instructions of a body (two-byte
    /// opcodes as 0xfeXX). Operand sizes follow ECMA-335 III.1.2.1.</summary>
    static IEnumerable<(int Offset, int Op)> Instructions(byte[] b)
    {
        for (int i = 0; i < b.Length;)
        {
            int op = b[i];
            if (op == 0xfe)
            {
                int sub = b[i + 1];
                yield return (i, 0xfe00 | sub);
                i += 2 + sub switch
                {
                    0x09 or 0x0a or 0x0b or 0x0c or 0x0d or 0x0e => 2,
                    0x12 or 0x19 => 1,
                    0x06 or 0x07 or 0x15 or 0x16 or 0x1c => 4,
                    _ => 0,
                };
                continue;
            }
            yield return (i, op);
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
    }

    /// <summary>The string literal of every `ldstr` in the body, by token: the
    /// symbol of the frozen string object ILC emits for it.</summary>
    static List<Dictionary<string, object>> Strings(MethodIL il, byte[] b, NameMangler mangler)
    {
        var result = new List<Dictionary<string, object>>();
        var seen = new HashSet<int>();
        foreach (var (i, op) in Instructions(b))
        {
            if (op != 0x72)
                continue;
            int token = BitConverter.ToInt32(b, i + 1);
            if (seen.Add(token) && il.GetObject(token) is string str)
                result.Add(new Dictionary<string, object>
                {
                    ["token"] = token,
                    ["symbol"] = mangler.CompilationUnitPrefix.ToString() + "__Str_" + mangler.GetMangledStringName(str).ToString(),
                });
        }
        return result;
    }

    /// <summary>The type operand of every newarr/ldelem/stelem/ldelema and
    /// ldobj/stobj/cpobj/initobj/sizeof in the body, by token: its storage type and size, and for newarr the symbol of
    /// the array type's MethodTable (the JIT allocates arrays inline, see
    /// fgRiscV64TryInlineNewArr).</summary>
    static List<Dictionary<string, object>> Types(MethodIL il, byte[] b, NameMangler mangler)
    {
        var result = new List<Dictionary<string, object>>();
        var byToken = new Dictionary<int, Dictionary<string, object>>();
        foreach (var (i, op) in Instructions(b))
        {
            if (op is not (0x8d or 0x8f or 0xa3 or 0xa4 or 0x70 or 0x71 or 0x81 or 0xfe15 or 0xfe1c or 0xd0 or 0x8c
                    or 0x74 or 0x75 or 0xa5))
                continue;
            int token = BitConverter.ToInt32(b, i + (op > 0xff ? 2 : 1));
            if (il.GetObject(token) is not TypeDesc t)
                continue;
            if (!byToken.TryGetValue(token, out var e))
            {
                e = new Dictionary<string, object>
                {
                    ["token"] = token,
                    ["name"] = t.ToString(),
                    ["type"] = Ty(t),
                    ["size"] = t.IsValueType ? t.GetElementSize().AsInt : 8,
                };
                byToken[token] = e;
                result.Add(e);
            }
            if (op == 0x8d && !t.IsCanonicalSubtype(CanonicalFormKind.Any))
                e["arrayMethodTable"] = mangler.NodeMangler.MethodTable(t.MakeArrayType()).ToString();
            // isinst/castclass: the MethodTables of the allocated types an
            // object of which the cast accepts (from the type system)
            if (op is 0x74 or 0x75 or 0xa5 && s_factory != null && !e.ContainsKey("castMethodTables")
                && !t.IsCanonicalSubtype(CanonicalFormKind.Any))
            {
                try
                {
                    var mts = new List<string>();
                    foreach (TypeDesc ct in ConstructedTypes())
                    {
                        try
                        {
                            if (ct.IsInterface || (ct is MetadataType cmt && cmt.IsAbstract)) continue;
                            if (ct.CanCastTo(t)) mts.Add(s_factory.ConstructedTypeSymbol(ct).GetMangledName(mangler));
                        }
                        catch (Exception) { }
                    }
                    e["castMethodTables"] = mts;
                    e["isInterface"] = t.IsInterface;
                }
                catch (Exception ex) { e["castError"] = ex.GetType().Name; }
            }
            // box: whether it allocates (a value type; Nullable<T> boxes to
            // null without a value)
            if (op == 0x8c)
            {
                e["boxValueType"] = t.IsValueType;
                e["boxNullable"] = t.IsNullable;
                // what the JIT's inline allocation of the boxed value reserves
                // (getHeapClassSize: MethodTable and value, at least 0x18,
                // 8-aligned), and the boxed type's MethodTable
                if (t.IsValueType && !t.IsNullable && t is DefType bdt && !t.IsCanonicalSubtype(CanonicalFormKind.Any)
                    && s_factory != null && mangler != null)
                {
                    try
                    {
                        e["boxPayload"] = (Math.Max(bdt.InstanceFieldSize.AsInt + 8, 0x18) + 7) & ~7;
                        e["boxMethodTable"] = s_factory.ConstructedTypeSymbol(t).GetMangledName(mangler);
                    }
                    catch (Exception) { }
                }
            }
            // ldtoken of a type: its MethodTable, for typeof(T) folded by the JIT
            if (op == 0xd0 && !t.IsCanonicalSubtype(CanonicalFormKind.Any))
            {
                e["methodTable"] = mangler.NodeMangler.MethodTable(t).ToString();
                e["isValueType"] = t.IsValueType;
            }
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

    /// <summary>The method of every `ldftn` in the body, by token.</summary>
    static List<(int Token, MethodDesc Target)> Ftns(MethodIL il)
    {
        byte[] b = il.GetILBytes();
        var r = new List<(int, MethodDesc)>();
        if (b == null) return r;
        foreach (var (i, op) in Instructions(b))
            if (op == 0xfe06)
            {
                int tok = BitConverter.ToInt32(b, i + 2);
                if (il.GetObject(tok) is MethodDesc md && !r.Any(x => x.Item1 == tok)) r.Add((tok, md));
            }
        return r;
    }

    /// <summary>The signature of every `calli` in the body, by token.</summary>
    static List<(int Token, MethodSignature Sig)> Callis(MethodIL il)
    {
        byte[] b = il.GetILBytes();
        var r = new List<(int, MethodSignature)>();
        if (b == null) return r;
        foreach (var (i, op) in Instructions(b))
            if (op == 0x29)
            {
                int tok = BitConverter.ToInt32(b, i + 1);
                if (il.GetObject(tok) is MethodSignature sg && !r.Any(x => x.Item1 == tok)) r.Add((tok, sg));
            }
        return r;
    }

    /// <summary>A method's signature is a `calli`'s: same staticness, return
    /// and parameter types (a generic method's instantiation counts as its
    /// signature with the type arguments substituted).</summary>
    static bool SameSig(MethodSignature a, MethodSignature b)
    {
        if (a.IsStatic != b.IsStatic || a.Length != b.Length || a.ReturnType != b.ReturnType) return false;
        for (int k = 0; k < a.Length; k++)
            if (a[k] != b[k]) return false;
        return true;
    }

    /// <summary>The methods the body calls (call, callvirt, newobj).</summary>
    static IEnumerable<MethodDesc> CalleeMethods(MethodIL il)
    {
        byte[] b = il.GetILBytes();
        if (b == null)
            yield break;
        foreach (var c in Callees(il, b, new Dictionary<MethodDesc, int>(), null))
        {
            if (c["target"] is MethodDesc md)
                yield return md;
            if (c.TryGetValue("ctargets", out var ct))
                foreach (MethodDesc t in (List<MethodDesc>)ct)
                    yield return t;
        }
    }

    /// <summary>How `constrained. T callvirt M` resolves: on a value type, to
    /// `T`'s own method (called directly with the pointer as `this`), or by
    /// boxing; on a reference type, by dereferencing the pointer.</summary>
    static void AddConstrained(Dictionary<string, object> entry, MethodIL il, int ctok, MethodDesc callee,
        Dictionary<MethodDesc, int> ids)
    {
        var list = entry.TryGetValue("constrained", out var l) ? (List<Dictionary<string, object>>)l
            : (List<Dictionary<string, object>>)(entry["constrained"] = new List<Dictionary<string, object>>());
        if (list.Any(x => (int)x["ctoken"] == ctok)) return;
        var e = new Dictionary<string, object> { ["ctoken"] = ctok };
        list.Add(e);
        try
        {
            if (il.GetObject(ctok) is not TypeDesc ct) { e["kind"] = "unknown"; return; }
            if (!ct.IsValueType)
            {
                e["kind"] = "deref";
                e["needsDispatch"] = callee.IsVirtual && !callee.IsFinal
                    && !(callee.OwningType is MetadataType ot && ot.IsSealed);
                return;
            }
            MethodDesc impl = callee.OwningType.IsInterface
                ? ct.TryResolveConstraintMethodApprox(callee.OwningType, callee, out _)
                : ct.FindVirtualFunctionTargetMethodOnObjectType(callee);
            if (impl == null || impl.OwningType != ct) { e["kind"] = "box"; return; }
            e["kind"] = "direct";
            e["id"] = ids.TryGetValue(impl, out int iid) ? iid : null;
            var targets = entry.TryGetValue("ctargets", out var t) ? (List<MethodDesc>)t
                : (List<MethodDesc>)(entry["ctargets"] = new List<MethodDesc>());
            targets.Add(impl);
        }
        catch (Exception ex) { e["kind"] = "error:" + ex.GetType().Name; }
    }

    /// <summary>`constrained. T call M` of a static virtual interface method:
    /// the call goes to T's implementation, recorded like a direct
    /// `constrained.` call (per constraint token, so that one method can call
    /// `M` on several types).</summary>
    static void AddStaticVirtual(Dictionary<string, object> entry, MethodIL il, int ctok, MethodDesc decl,
        Dictionary<MethodDesc, int> ids)
    {
        var list = entry.TryGetValue("constrained", out var l) ? (List<Dictionary<string, object>>)l
            : (List<Dictionary<string, object>>)(entry["constrained"] = new List<Dictionary<string, object>>());
        if (list.Any(x => (int)x["ctoken"] == ctok)) return;
        var e = new Dictionary<string, object> { ["ctoken"] = ctok, ["static"] = true };
        list.Add(e);
        entry["dispatch"] = false;
        try
        {
            if (il.GetObject(ctok) is not TypeDesc ct) { e["kind"] = "unknown"; return; }
            MethodDesc impl = null;
            bool rt = false;
            try { impl = ct.TryResolveConstraintMethodApprox(decl.OwningType, decl, out rt); }
            catch (Exception) { impl = null; }
            if (impl == null && !rt)
            {
                impl = ResolveStaticVirtual(ct, decl);
            }
            if (rt || impl == null || impl.IsAbstract || impl.OwningType.IsInterface)
            {
                e["kind"] = rt ? "runtime lookup" : "no implementation";
                return;
            }
            e["kind"] = "direct";
            e["id"] = ids.TryGetValue(impl, out int iid) ? iid : null;
            var targets = entry.TryGetValue("ctargets", out var t) ? (List<MethodDesc>)t
                : (List<MethodDesc>)(entry["ctargets"] = new List<MethodDesc>());
            targets.Add(impl);
        }
        catch (Exception ex) { e["kind"] = "error:" + ex.GetType().Name; }
    }

    /// <summary>The type system's static virtual resolution (its API differs
    /// between versions, hence reflection).</summary>
    static MethodDesc ResolveStaticVirtual(TypeDesc ct, MethodDesc decl)
    {
        try
        {
            object alg = ct.Context.GetType().GetMethod("GetVirtualMethodAlgorithmForType")?.Invoke(ct.Context, new object[] { ct });
            var mi = alg?.GetType().GetMethod("ResolveInterfaceMethodToStaticVirtualMethodOnType");
            if (mi != null && mi.Invoke(alg, new object[] { decl, ct }) is MethodDesc r1) return r1;
            mi = alg?.GetType().GetMethod("ResolveVariantInterfaceMethodToStaticVirtualMethodOnType");
            if (mi != null && mi.Invoke(alg, new object[] { decl, ct }) is MethodDesc r2) return r2;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The IL exception clauses; a `catch` with the MethodTables of
    /// the allocated types it accepts (as for casts).</summary>
    static List<Dictionary<string, object>> EhClauses(MethodIL il, NameMangler mangler)
    {
        var result = new List<Dictionary<string, object>>();
        foreach (ILExceptionRegion r in il.GetExceptionRegions())
        {
            var c = new Dictionary<string, object>
            {
                ["kind"] = r.Kind.ToString().ToLowerInvariant(),
                ["tryOffset"] = r.TryOffset,
                ["tryLength"] = r.TryLength,
                ["handlerOffset"] = r.HandlerOffset,
                ["handlerLength"] = r.HandlerLength,
            };
            if (r.Kind == ILExceptionRegionKind.Filter) c["filterOffset"] = r.FilterOffset;
            if (r.Kind == ILExceptionRegionKind.Catch)
            {
                c["classToken"] = r.ClassToken;
                try
                {
                    if (il.GetObject(r.ClassToken) is TypeDesc t && s_factory != null && mangler != null
                        && !t.IsCanonicalSubtype(CanonicalFormKind.Any))
                    {
                        c["catchType"] = t.ToString();
                        var mts = new List<string>();
                        foreach (TypeDesc ct in ConstructedTypes())
                        {
                            try
                            {
                                if (ct.IsInterface || (ct is MetadataType cmt && cmt.IsAbstract)) continue;
                                if (ct.CanCastTo(t)) mts.Add(s_factory.ConstructedTypeSymbol(ct).GetMangledName(mangler));
                            }
                            catch (Exception) { }
                        }
                        c["catchMethodTables"] = mts;
                    }
                }
                catch (Exception ex) { c["catchError"] = ex.GetType().Name; }
            }
            result.Add(c);
        }
        return result;
    }

    static bool SafeInstArg(MethodDesc m)
    {
        try { return m.IsSharedByGenericInstantiations && m.RequiresInstArg(); }
        catch (Exception) { return true; }
    }

    static string SafeSymbol(NameMangler mangler, MethodDesc m)
    {
        try { return mangler.GetMangledMethodName(m).ToString(); }
        catch (Exception) { return null; }
    }

    static List<TypeDesc> s_constructed;

    /// <summary>Types the program allocates (MetadataManager knows them; the
    /// accessor is internal).</summary>
    static List<TypeDesc> ConstructedTypes()
    {
        if (s_constructed != null) return s_constructed;
        var mm = s_factory.MetadataManager;
        var mi = typeof(MetadataManager).GetMethod("GetTypesWithConstructedEETypes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        s_constructed = ((IEnumerable<TypeDesc>)mi.Invoke(mm, null)).ToList();
        return s_constructed;
    }

    static void AddVirtualTargets(Dictionary<string, object> entry, MethodDesc callee,
        Dictionary<MethodDesc, int> ids, NameMangler mangler)
    {
        int slot = VirtualMethodSlotHelper.GetVirtualMethodSlot(s_factory, callee, callee.OwningType);
        entry["vslot"] = EETypeNode.GetVTableOffset(8) + 8 * slot;
        entry["impls"] = Impls(VirtualImpls(callee), ids, mangler);
    }

    /// <summary>The implementations with the method tables that reach each.</summary>
    static List<Dictionary<string, object>> Impls(List<(TypeDesc Type, MethodDesc Impl)> targets,
        Dictionary<MethodDesc, int> ids, NameMangler mangler)
    {
        var impls = new Dictionary<MethodDesc, List<string>>();
        foreach (var (t, impl) in targets)
        {
            if (!impls.TryGetValue(impl, out var mts)) impls[impl] = mts = new List<string>();
            mts.Add(s_factory.ConstructedTypeSymbol(t).GetMangledName(mangler));
        }
        return impls.Select(kv => new Dictionary<string, object>
        {
            ["id"] = ids.TryGetValue(kv.Key, out int iid) ? iid : null,
            ["symbol"] = SafeSymbol(mangler, kv.Key),
            ["methodTables"] = kv.Value,
        }).ToList();
    }

    /// <summary>Each allocated (non-abstract) type deriving from the callee's
    /// owner, with the method its vtable slot resolves to.</summary>
    static List<(TypeDesc Type, MethodDesc Impl)> VirtualImpls(MethodDesc callee)
    {
        if (callee.OwningType.IsInterface) return InterfaceImpls(callee);
        var result = new List<(TypeDesc, MethodDesc)>();
        foreach (TypeDesc t in ConstructedTypes())
        {
            try
            {
                if (t.IsInterface || (t is MetadataType mt && mt.IsAbstract) || !t.CanCastTo(callee.OwningType)) continue;
                MethodDesc impl = t.FindVirtualFunctionTargetMethodOnObjectType(callee);
                if (impl == null || impl.IsAbstract) continue;
                result.Add((t, impl.GetCanonMethodTarget(CanonicalFormKind.Specific)));
            }
            catch (InvalidCastException)
            {
                // arrays and other non-metadata types: resolved through their base
                MethodDesc impl = (t.BaseType ?? t).FindVirtualFunctionTargetMethodOnObjectType(callee);
                if (impl != null && !impl.IsAbstract)
                    result.Add((t, impl.GetCanonMethodTarget(CanonicalFormKind.Specific)));
            }
        }
        return result;
    }

    /// <summary>Each allocated class implementing the callee's interface, with
    /// the method an interface call on it reaches. Value types (reached
    /// through unboxing stubs), arrays, and default implementations in
    /// generic interfaces (instantiating stubs) are left out: the IL leaves the
    /// model on them.</summary>
    static List<(TypeDesc Type, MethodDesc Impl)> InterfaceImpls(MethodDesc callee)
    {
        var result = new List<(TypeDesc, MethodDesc)>();
        foreach (TypeDesc t in ConstructedTypes())
        {
            try
            {
                if (t.IsInterface || t.IsValueType || !(t is MetadataType mt) || mt.IsAbstract
                    || !t.CanCastTo(callee.OwningType)) continue;
                MethodDesc impl = t.ResolveInterfaceMethodTarget(callee)
                    ?? t.ResolveInterfaceMethodTargetWithVariance(callee);
                if (impl != null)
                    impl = t.FindVirtualFunctionTargetMethodOnObjectType(impl);
                else if (t.ResolveInterfaceMethodToDefaultImplementationOnType(callee, out MethodDesc dim)
                    == DefaultInterfaceMethodResolution.DefaultImplementation && !dim.OwningType.HasInstantiation)
                    impl = dim;
                if (impl == null || impl.IsAbstract || impl.IsGenericMethodDefinition
                    || impl.OwningType.ContainsSignatureVariables(treatGenericParameterLikeSignatureVariable: true))
                    continue;
                result.Add((t, impl.GetCanonMethodTarget(CanonicalFormKind.Specific)));
            }
            catch (Exception) { }
        }
        return result;
    }

    /// <summary>The callee of every `call` in the body, by the token in the IL.
    /// Operand sizes follow ECMA-335 III.1.2.1.</summary>
    static List<Dictionary<string, object>> Callees(MethodIL il, byte[] b, Dictionary<MethodDesc, int> ids,
        NameMangler mangler)
    {
        var result = new List<Dictionary<string, object>>();
        var seen = new HashSet<int>();
        var byToken = new Dictionary<int, Dictionary<string, object>>();
        int? constrainedTok = null;
        for (int i = 0; i < b.Length;)
        {
            int op = b[i];
            if (op == 0xfe)
            {
                int sub = b[i + 1];
                // `constrained. T` applies to the `callvirt` that follows
                constrainedTok = sub == 0x16 ? BitConverter.ToInt32(b, i + 2) : null;
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
                // `callvirt` of a method that is overridable needs dispatch;
                // `call`, or a final method, or one of a sealed type, does not
                bool Dispatch(MethodDesc md) => op == 0x6f && md.IsVirtual && !md.IsFinal
                    && !(md.OwningType is MetadataType ot && ot.IsSealed) && !md.OwningType.IsValueType;
                if (!seen.Add(token))
                {
                    if (byToken.TryGetValue(token, out var prev) && prev["target"] is MethodDesc pm && Dispatch(pm)
                        && constrainedTok == null)
                        prev["dispatch"] = true;
                }
                else if (il.GetObject(token) is MethodDesc callee)
                {
                    var sig = callee.Signature;
                    var entry = new Dictionary<string, object>
                    {
                        ["token"] = token,
                        ["id"] = ids.TryGetValue(callee, out int id) ? id : null,
                        // shared generic code: the canonical method whose code
                        // runs for this instantiation
                        ["canonical"] = callee.GetCanonMethodTarget(CanonicalFormKind.Specific) is var cm
                            && cm != callee && ids.TryGetValue(cm, out int cid) ? cid : null,
                        ["name"] = callee.OwningType is MetadataType ? Name(callee) : callee.ToString(),
                        ["signature"] = callee.ToString(),
                        ["static"] = sig.IsStatic,
                        ["virtual"] = callee.IsVirtual,
                        ["dispatch"] = Dispatch(callee),
                        ["interface"] = callee.OwningType.IsInterface,
                        ["intrinsic"] = callee.IsIntrinsic,
                        ["args"] = Args(callee),
                        ["ret"] = sig.ReturnType.IsVoid ? null : Ty(sig.ReturnType),
                        // for CalleeMethods; not serialized (removed below)
                        ["target"] = callee,
                    };
                    if (Dispatch(callee) && !callee.OwningType.IsInterface && s_factory != null)
                    {
                        // a virtual call: the vtable slot, and the implementation
                        // each constructed type that derives from the owner has
                        try { AddVirtualTargets(entry, callee, ids, mangler); }
                        catch (Exception ex) { entry["virtError"] = ex.GetType().Name; }
                    }
                    if (Dispatch(callee) && callee.OwningType.IsInterface && s_factory != null && mangler != null)
                    {
                        // an interface call (through RhpInterfaceDispatch and a
                        // dispatch cell named after the callee): the
                        // implementation of each constructed class that implements it
                        entry["symbol"] = SafeSymbol(mangler, callee);
                        try { entry["impls"] = Impls(VirtualImpls(callee), ids, mangler); }
                        catch (Exception ex) { entry["virtError"] = ex.GetType().Name; }
                    }
                    // `MethodTable.Of<T>()`, which the JIT folds to the address of
                    // T's MethodTable
                    if (callee.Name.ToString() == "Of" && callee.OwningType is MetadataType mot && mot.Name.ToString() == "MethodTable"
                        && callee.HasInstantiation && s_factory != null && mangler != null)
                    {
                        try { entry["mtOf"] = s_factory.ConstructedTypeSymbol(callee.Instantiation[0]).GetMangledName(mangler); }
                        catch (Exception)
                        {
                            try { entry["mtOf"] = s_factory.NecessaryTypeSymbol(callee.Instantiation[0]).GetMangledName(mangler); }
                            catch (Exception) { }
                        }
                    }
                    // `RuntimeHelpers.IsReferenceOrContainsReferences<T>()`, which the
                    // JIT folds to a constant
                    if (callee.Name.ToString() == "IsReferenceOrContainsReferences" && callee.HasInstantiation
                        && callee.OwningType is MetadataType rot && rot.Name.ToString() == "RuntimeHelpers")
                    {
                        TypeDesc t0 = callee.Instantiation[0];
                        try
                        {
                            entry["constBool"] = t0.IsGCPointer || t0.IsCanonicalSubtype(CanonicalFormKind.Any)
                                || (t0 is DefType dt && dt.ContainsGCPointers);
                        }
                        catch (Exception) { }
                    }
                    if (op == 0x73 && mangler != null && Alloc(callee.OwningType, mangler) is { } alloc)
                        entry["alloc"] = alloc;
                    // newobj of a value type: built in a temporary
                    if (op == 0x73 && callee.OwningType.IsValueType)
                        entry["valueType"] = Ty(callee.OwningType);
                    // sizes of a generic method's type arguments (Unsafe.Add<T> and the like)
                    if (callee.HasInstantiation)
                    {
                        var sizes = new List<int>();
                        foreach (TypeDesc t in callee.Instantiation)
                            sizes.Add(t.IsValueType ? t.GetElementSize().AsInt : 8);
                        entry["instSizes"] = sizes;
                    }
                    if (constrainedTok != null)
                        entry["dispatch"] = false;
                    result.Add(entry);
                    byToken[token] = entry;
                }
                if (op == 0x6f && constrainedTok is int ctok && byToken.TryGetValue(token, out var ce)
                    && ce["target"] is MethodDesc cmd)
                    AddConstrained(ce, il, ctok, cmd, ids);
                // `constrained. T call M` of a static virtual M: T's implementation
                if (op == 0x28 && constrainedTok is int stok && byToken.TryGetValue(token, out var se)
                    && se["target"] is MethodDesc smd && smd.Signature.IsStatic && smd.OwningType.IsInterface)
                    AddStaticVirtual(se, il, stok, smd, ids);
            }
            constrainedTok = null;
            i += 1 + size;
        }
        return result;
    }
}
