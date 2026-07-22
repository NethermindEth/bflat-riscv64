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

#pragma warning disable 8509

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
/// MethodIL wrapper returning a patched copy of the inner body's IL stream.
/// Metadata tokens keep resolving through the inner (Ecma) body, so patches
/// may freely reference any token already valid in the owning module; tokens
/// injected by a patch (values chosen outside the module's real token space)
/// are resolved from <paramref name="extraTokens"/> instead.
///
/// GetMethodILDefinition is overridden: for a shared generic instantiation the
/// inner MethodIL is an InstantiatedMethodIL whose definition is the open body,
/// and ILC's generic-dictionary / method-body-folding analysis reaches the
/// method through that open definition. A wrapper that returned `this` (the
/// default) would hand back an instantiated, patched body where the open one is
/// expected, corrupting the generic dictionary layout (observed as a null
/// WeakReference&lt;T&gt; MethodTable when the reflection type unifier grows).
/// The IL bytes are identical between instantiation and definition (only token
/// resolution differs), so the same patched bytes wrap the open definition.
/// </summary>
sealed class PatchedMethodIL : MethodIL
{
    private readonly MethodIL _inner;
    private readonly byte[] _bytes;
    private readonly Dictionary<int, object> _extraTokens;
    private readonly int _extraMaxStack;

    public PatchedMethodIL(MethodIL inner, byte[] bytes, Dictionary<int, object> extraTokens = null, int extraMaxStack = 0)
    {
        _inner = inner;
        _bytes = bytes;
        _extraTokens = extraTokens;
        _extraMaxStack = extraMaxStack;
    }

    public override MethodDesc OwningMethod => _inner.OwningMethod;
    public override int MaxStack => _inner.MaxStack + _extraMaxStack;
    public override bool IsInitLocals => _inner.IsInitLocals;
    public override byte[] GetILBytes() => _bytes;
    public override LocalVariableDefinition[] GetLocals() => _inner.GetLocals();
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
            : new PatchedMethodIL(innerDef, _bytes, _extraTokens, _extraMaxStack);
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
            cont.Name != "Container" ||
            cont.ContainingType is not MetadataType unifier ||
            !unifier.Name.StartsWith("ConcurrentUnifierW") ||
            method.Name != "Resize")
        {
            return body;
        }

        byte[] il = (byte[])body.GetILBytes().Clone();
        // ldloc.0; conv.r8; ldarg.0; ldfld _entries; ldlen; conv.i4; conv.r8;
        // div; ldc.r8 0.75; bge.un.s  ->  06 6C 02 7B ?? ?? ?? ?? 8E 69 6C 5B 23 <0.75> 34
        byte[] pat = { 0x06, 0x6C, 0x02, 0x7B, 0, 0, 0, 0, 0x8E, 0x69, 0x6C, 0x5B,
                       0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE8, 0x3F, 0x34 };
        bool[] mask = new bool[pat.Length];
        for (int i = 0; i < mask.Length; i++) mask[i] = !(i >= 4 && i <= 7);
        int at = ILPatch.FindPattern(il, pat, mask);
        if (at < 0)
            return body;

        byte t0 = il[at + 4], t1 = il[at + 5], t2 = il[at + 6], t3 = il[at + 7];
        int p = at;
        il[p++] = 0x06;                                    // ldloc.0
        il[p++] = 0x6A;                                    // conv.i8
        il[p++] = 0x1A;                                    // ldc.i4.4
        il[p++] = 0x6A;                                    // conv.i8
        il[p++] = 0x5A;                                    // mul   -> live*4
        il[p++] = 0x02;                                    // ldarg.0
        il[p++] = 0x7B; il[p++] = t0; il[p++] = t1; il[p++] = t2; il[p++] = t3; // ldfld _entries
        il[p++] = 0x8E;                                    // ldlen
        il[p++] = 0x69;                                    // conv.i4
        il[p++] = 0x6A;                                    // conv.i8
        il[p++] = 0x19;                                    // ldc.i4.3
        il[p++] = 0x6A;                                    // conv.i8
        il[p++] = 0x5A;                                    // mul   -> len*3
        for (int i = 0; i < 4; i++)
            il[p++] = 0x00;                                // nop
        il[p] = 0x2F;                                      // bge.s (same operand/target)
        // Integer form holds live*4 while computing len*3: one slot deeper than
        // the original double ratio peak.
        return new PatchedMethodIL(body, il, extraMaxStack: 1);
    }
}

static class ILPatch
{
    /// <summary>
    /// Finds the single occurrence of <paramref name="pattern"/> in
    /// <paramref name="haystack"/>; positions where <paramref name="mask"/> is
    /// false match any byte. Returns -1 when absent or ambiguous (more than one
    /// match is treated as not found - safer to leave the IL alone than to
    /// patch the wrong site).
    /// </summary>
    public static int FindPattern(byte[] haystack, byte[] pattern, bool[] mask)
    {
        int found = -1;
        for (int i = 0; i + pattern.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if ((mask == null || mask[j]) && haystack[i + j] != pattern[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                if (found >= 0)
                    return -1;
                found = i;
            }
        }
        return found;
    }
}

class CustomILProvider : ILProvider
{
    private ILProvider inner;
    private bool zkvmTarget;
    public TypeSystemContext TypeContext;

    public CustomILProvider(ILProvider innerProvider, TypeSystemContext typeContext, bool isZkvmTarget = false)
    {
        inner = innerProvider;
        TypeContext = typeContext;
        zkvmTarget = isZkvmTarget;
    }

    public override MethodIL GetMethodIL(MethodDesc method)
    {
        // zkVM (rv64ima) IL replacements. Unlike ILLink substitutions, a raw
        // IL body can return reference types and structs, so this is the layer
        // for replacements the XML cannot express.
        if (zkvmTarget &&
            method.OwningType is MetadataType zt &&
            zt.Namespace == "System.Collections.Frozen" &&
            zt.Name == "LengthBuckets" &&
            method.Name == "CreateLengthBucketsArrayIfAppropriate")
        {
            // Decides (in double ratio math) whether the by-length string
            // lookup optimization pays off. Null = "use the fallback frozen
            // comparer" and is always a correct answer.
            return new ILStubMethodIL(
                method,
                new byte[]
                {
                    (byte)ILOpcode.ldnull,
                    (byte)ILOpcode.ret
                },
                Array.Empty<LocalVariableDefinition>(),
                new object[] { }
            );
        }

        // TimeZoneInfo..cctor initializes s_daylightRuleMarker via
        // DateTime.MinValue.AddMilliseconds(2), whose inlined double scaling is
        // the only FPU code in the body. Patch the 19-byte sequence
        //   ldsflda MinValue; ldc.r8 2.0; call AddMilliseconds
        // into the tick-exact integer construction
        //   ldc.i8 20000; newobj DateTime(int64); nop x5
        // (2 ms = 20_000 ticks). Same stack effect, same length, so branch
        // offsets and the trailing CreateFixedDateRule call are untouched.
        if (zkvmTarget &&
            method.OwningType is MetadataType tzType &&
            tzType.Namespace == "System" &&
            tzType.Name == "TimeZoneInfo" &&
            method.Name == ".cctor")
        {
            MethodIL body = inner.GetMethodIL(method);
            byte[] il = (byte[])body.GetILBytes().Clone();
            // Anchor: ldc.r8 2.0 (23 00 00 00 00 00 00 00 40) preceded by
            // ldsflda (7F + token) and followed by call (28 + token).
            byte[] anchor = { 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40 };
            int at = ILPatch.FindPattern(il, anchor, null);
            if (at >= 5 && il[at - 5] == 0x7F && il[at + 9] == 0x28)
            {
                var int64Type = TypeContext.GetWellKnownType(WellKnownType.Int64);
                MethodDesc dateTimeTicksCtor = null;
                foreach (MethodDesc ctor in ((MetadataType)TypeContext.GetWellKnownType(WellKnownType.Object))
                             .Module.GetType("System", "DateTime").GetMethods())
                {
                    if (ctor.Name == ".ctor" && ctor.Signature.Length == 1 && ctor.Signature[0] == int64Type)
                    {
                        dateTimeTicksCtor = ctor;
                        break;
                    }
                }

                if (dateTimeTicksCtor != null)
                {
                    const int injectedToken = 0x0A7FFFF0;
                    int p = at - 5;
                    il[p++] = 0x21; // ldc.i8
                    long ticks = 2 * 10_000; // 2 ms in ticks
                    for (int i = 0; i < 8; i++)
                        il[p++] = (byte)(ticks >> (8 * i));
                    il[p++] = 0x73; // newobj
                    il[p++] = unchecked((byte)injectedToken);
                    il[p++] = unchecked((byte)(injectedToken >> 8));
                    il[p++] = unchecked((byte)(injectedToken >> 16));
                    il[p++] = unchecked((byte)(injectedToken >> 24));
                    for (int i = 0; i < 5; i++)
                        il[p++] = 0x00; // nop
                    return new PatchedMethodIL(body, il,
                        new Dictionary<int, object> { [injectedToken] = dateTimeTicksCtor });
                }
            }
            return body;
        }

        // NOTE: ConcurrentUnifierW(Keyed)`2.Container.Resize's double growth
        // ratio is handled separately by UnifierResizeILProvider (codegen-only),
        // because it is a SHARED GENERIC body - see that class. Patches HERE run
        // in both scan and codegen and must stay confined to NON-generic bodies.

        // ValueType.RegularGetValueTypeHashCode hashes Single/Double struct
        // fields through HashCode.Add<float/double>, passing the value BY VALUE,
        // so the double/float travels in an FP register (flw/fld here, fmv.x.w/d
        // inside Add<T>) - the last FP in the image. Rewrite each such site to
        // load the raw bits instead (ldind.r4/r8 -> ldind.i4/i8) and hash them
        // via HashCode.Add<int/long>, which is entirely integer. Hashing the
        // bit pattern matches NativeAOT's byte-wise ValueType.Equals for
        // blittable fields, so the hash/equals contract stays consistent.
        // Non-generic method (safe to patch in both phases); Add<int>/Add<long>
        // MethodDescs are constructed from the type system and injected as
        // synthetic tokens (like the DateTime ctor in the TimeZoneInfo patch).
        if (zkvmTarget &&
            method.OwningType is MetadataType vtType &&
            vtType.Namespace == "System" &&
            vtType.Name == "ValueType" &&
            method.Name == "RegularGetValueTypeHashCode")
        {
            MethodIL body = inner.GetMethodIL(method);
            byte[] il = (byte[])body.GetILBytes().Clone();

            MetadataType hashCodeType = (MetadataType)((MetadataType)TypeContext.GetWellKnownType(WellKnownType.Object))
                .Module.GetType("System", "HashCode");
            MethodDesc addOpen = null;
            foreach (MethodDesc mm in hashCodeType.GetMethods())
            {
                if (mm.Name == "Add" && mm.HasInstantiation && mm.Instantiation.Length == 1 && mm.Signature.Length == 1)
                {
                    addOpen = mm;
                    break;
                }
            }

            var extra = new Dictionary<int, object>();
            if (addOpen != null)
            {
                MethodDesc addInt = TypeContext.GetInstantiatedMethod(addOpen,
                    new Instantiation(new TypeDesc[] { TypeContext.GetWellKnownType(WellKnownType.Int32) }));
                MethodDesc addLong = TypeContext.GetInstantiatedMethod(addOpen,
                    new Instantiation(new TypeDesc[] { TypeContext.GetWellKnownType(WellKnownType.Int64) }));
                const int addIntToken = 0x0A7FFFF1;
                const int addLongToken = 0x0A7FFFF2;
                extra[addIntToken] = addInt;
                extra[addLongToken] = addLong;

                // Walk the IL for ldind.r4/r8 (0x4E/0x4F) immediately followed by
                // call (0x28) to HashCode.Add<float/double>, and retarget both.
                for (int i = 0; i + 6 <= il.Length; i++)
                {
                    if ((il[i] == 0x4E || il[i] == 0x4F) && il[i + 1] == 0x28)
                    {
                        int tok = il[i + 2] | (il[i + 3] << 8) | (il[i + 4] << 16) | (il[i + 5] << 24);
                        object callee = body.GetObject(tok, NotFoundBehavior.ReturnNull);
                        if (callee is MethodDesc md && md.GetTypicalMethodDefinition() == addOpen && md.Instantiation.Length == 1)
                        {
                            TypeDesc arg = md.Instantiation[0];
                            bool isDouble = il[i] == 0x4F;
                            il[i] = isDouble ? (byte)0x4C : (byte)0x4A; // ldind.i8 / ldind.i4
                            int newTok = isDouble ? addLongToken : addIntToken;
                            il[i + 2] = (byte)newTok;
                            il[i + 3] = (byte)(newTok >> 8);
                            il[i + 4] = (byte)(newTok >> 16);
                            il[i + 5] = (byte)(newTok >> 24);
                        }
                    }
                }
            }

            return extra.Count > 0 ? new PatchedMethodIL(body, il, extra) : body;
        }

        if (method.OwningType is MetadataType owningType &&
            owningType.Namespace == "System" &&
            owningType.Name == "OutOfMemoryException" &&
            method.Name == "GetDefaultMessage")
        {
            var stringType = TypeContext.GetWellKnownType(WellKnownType.String);
            FieldDesc emptyField = null;

            foreach (var field in stringType.GetFields())
            {
                if (field.Name == "Empty" && field.IsStatic)
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
            owningType2.Namespace == "Internal.JitInterface" &&
            owningType2.Name == "CorInfoImpl" &&
            method.Name == "getAsyncInfo")
        {
            return new ILStubMethodIL(
                method,
                new byte[]
                {
                    (byte)ILOpcode.ret
                },
                Array.Empty<LocalVariableDefinition>(),
                new object[] { }
            );
        }

        return inner.GetMethodIL(method);
    }
}

internal class BuildCommand : CommandBase
{
    private const string DefaultSystemModule = "System.Private.CoreLib";
    private BuildCommand() { }

    private static Option<bool> RootDefaultAssemblies = new Option<bool>("--root-default-assemblies", "Root default assemblies");
    private static Option<bool> NoReflectionOption = new Option<bool>("--no-reflection", "Disable support for reflection");
    private static Option<bool> NoStackTraceDataOption = new Option<bool>("--no-stacktrace-data", "Disable support for textual stack traces");
    private static Option<bool> NoGlobalizationOption = new Option<bool>("--no-globalization", "Disable support for globalization (use invariant mode)");
    private static Option<bool> NoExceptionMessagesOption = new Option<bool>("--no-exception-messages", "Disable exception messages");
    private static Option<bool> NoPieOption = new Option<bool>("--no-pie", "Do not generate position independent executable");

    private static Option<bool> NoLinkOption = new Option<bool>("-c", "Produce object file, but don't run linker");
    private static Option<bool> MstatOption = new Option<bool>("--mstat", "Produce MSTAT and DGML files for size analysis");
    private static Option<bool> SymChartOption = new Option<bool>("--symchart", "Run readelf after linking and generate an HTML symbol-size chart");
    private static Option<bool> WrapCheckOption = new Option<bool>("--wrap-check", "Verify every --wrap= linker flag points to a real symbol; fails the build if any is missing");
    private static Option<string[]> LdFlagsOption = new Option<string[]>(new string[] { "--ldflags" }, "Arguments to pass to the linker");
    private static Option<string[]> MibcOption = new Option<string[]>(new string[] { "--mibc" }, "MIBC profile file(s) for profile-guided optimization");
    private static Option<bool> PrintCommandsOption = new Option<bool>("-x", "Print the commands");

    private static Option<bool> SeparateSymbolsOption = new Option<bool>("--separate-symbols", "Separate debugging symbols (Linux)");

    private static Option<string[]> DirectPInvokesOption = new Option<string[]>("-i", "Bind to entrypoint statically")
    {
        ArgumentHelpName = "library|library!function"
    };

    private static Option<bool> OptimizeSizeOption = new Option<bool>(new string[] { "-Os", "--optimize-space" }, "Favor code space when optimizing");
    private static Option<bool> OptimizeSpeedOption = new Option<bool>(new string[] { "-Ot", "--optimize-time" }, "Favor code speed when optimizing");
    private static Option<bool> DisableOptimizationOption = new Option<bool>(new string[] { "-O0", "--no-optimization" }, "Disable optimizations");
    private static Option<bool> LtoOption = new Option<bool>("--lto", "Enable link-time optimization (passes --lto=full --lto-O3 to lld; only effective for native libs built with -flto)");

    private static Option<string> TargetArchitectureOption = new Option<string>("--arch", "Target architecture")
    {
        ArgumentHelpName = "x86|x64|arm64|riscv64"
    };
    private static Option<string> TargetOSOption = new Option<string>("--os", "Target operating system")
    {
        ArgumentHelpName = "linux|windows|uefi"
    };
    private static Option<string> TargetIsaOption = new Option<string>("-m", "Target instruction set extensions")
    {
        ArgumentHelpName = "{isa1}[,{isaN}]|native"
    };

    private static Option<string> TargetLibcOption = new Option<string>("--libc", "Target libc (Windows: shcrt|none, Linux: glibc|bionic|musl|zisk|zisk_sim)");

    private static Option<string> MapFileOption = new Option<string>("--map", "Generate an object map file")
    {
        ArgumentHelpName = "file",
    };

    private static Option<string[]> FeatureSwitchOption = new Option<string[]>("--feature", "Set feature switch value")
    {
        ArgumentHelpName = "Feature=[true|false]",
    };

    private static Option<string[]> SubstitutionFilePathsOption = new Option<string[]>("--substitution", "ILLink.Substitutions file(s) to apply during compilation")
    {
        ArgumentHelpName = "file.xml",
    };

    private static Option<string[]> ExtLibOption = new Option<string[]>("--extlib", "Link external library: repo:version (GitHub release with single .nupkg), path/URL to .nupkg, or path/URL to .bflat.manifest")
    {
        ArgumentHelpName = "repo:version|pkg.nupkg|pkg.bflat.manifest"
    };

    public static Command Create()
    {
        var command = new Command("build", "Compiles the specified C# source files into native code")
        {
            CommonOptions.InputFilesArgument,
            CommonOptions.DefinedSymbolsOption,
            CommonOptions.ReferencesOption,
            CommonOptions.NoStdLibRefsOption,
            CommonOptions.TargetOption,
            CommonOptions.OutputOption,
            NoLinkOption,
            LdFlagsOption,
            MibcOption,
            PrintCommandsOption,
            TargetArchitectureOption,
            TargetOSOption,
            TargetIsaOption,
            TargetLibcOption,
            OptimizeSizeOption,
            OptimizeSpeedOption,
            DisableOptimizationOption,
            LtoOption,
            NoReflectionOption,
            NoStackTraceDataOption,
            NoGlobalizationOption,
            NoExceptionMessagesOption,
            NoPieOption,
            SeparateSymbolsOption,
            CommonOptions.NoDebugInfoOption,
            MapFileOption,
            MstatOption,
            DirectPInvokesOption,
            FeatureSwitchOption,
            SubstitutionFilePathsOption,
            CommonOptions.ResourceOption,
            CommonOptions.StdLibOption,
            CommonOptions.DeterministicOption,
            CommonOptions.NoPthreadOption,
            CommonOptions.VerbosityOption,
            CommonOptions.LangVersionOption,
            CommonOptions.ExtraLd,
            CommonOptions.KeepObjectOption,
            ExtLibOption,
            SymChartOption,
            WrapCheckOption,
        };
        command.Handler = new BuildCommand();

        return command;
    }

    static IEnumerable<string> EnumerateExpandedDirectories(string paths, string pattern)
    {
        string[] split = paths.Split(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':');
        foreach (var dir in split)
        {
            foreach (var file in Directory.GetFiles(dir, pattern))
            {
                yield return file;
            }
        }
    }

    void PatchRiscvAbi(string path)
    {
        const long offset = 0x30;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        int b = fs.ReadByte();
        if (b == 4 || b == 5)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.WriteByte(0);
        }
        fs.Close();
    }

    void PatchRiscvAbiStaticLib(string libPath, bool verbose)
    {
        if (verbose)
            Console.WriteLine($"Patching RISC-V ABI in static library (in place): {libPath}");

        if (!File.Exists(libPath))
        {
            if (verbose)
                Console.WriteLine($"Warning: {libPath} not found, skipping ABI patch");
            return;
        }

        // Patch the float-ABI marker of every ELF member in place by walking the ar
        // structure. Extract-and-repack (ar x / ar rcs) is unsafe here: musl's
        // libc.a has multiple members that share a basename (e.g. free.lo), and
        // extraction by name overwrites the earlier one on disk, silently dropping
        // its symbols (this is why "free" went missing). Rewriting bytes in place
        // preserves every member and the archive symbol index.
        using var fs = new FileStream(libPath, FileMode.Open, FileAccess.ReadWrite);

        byte[] magic = new byte[8];
        if (fs.Read(magic, 0, 8) != 8 || System.Text.Encoding.ASCII.GetString(magic) != "!<arch>\n")
        {
            if (verbose)
                Console.WriteLine($"Warning: {libPath} is not an ar archive, skipping ABI patch");
            return;
        }

        int patched = 0;
        byte[] header = new byte[60];
        while (fs.Position + 60 <= fs.Length)
        {
            if (fs.Read(header, 0, 60) != 60)
                break;

            // Member size is a decimal ASCII string at bytes 48..57.
            if (!long.TryParse(System.Text.Encoding.ASCII.GetString(header, 48, 10).Trim(), out long size))
                break;

            long dataPos = fs.Position;

            // e_flags lives at offset 0x30 of the ELF header; patch it only for ELF
            // members whose marker is hard-float (4) or hard-float+compressed (5).
            // The armap/extended-name members are not ELF and are skipped.
            if (size > 0x34)
            {
                byte[] ident = new byte[4];
                fs.Read(ident, 0, 4);
                if (ident[0] == 0x7f && ident[1] == (byte)'E' && ident[2] == (byte)'L' && ident[3] == (byte)'F')
                {
                    fs.Seek(dataPos + 0x30, SeekOrigin.Begin);
                    int b = fs.ReadByte();
                    if (b == 4 || b == 5)
                    {
                        fs.Seek(dataPos + 0x30, SeekOrigin.Begin);
                        fs.WriteByte(0);
                        patched++;
                    }
                }
            }

            // Advance to the next member; member data is padded to an even offset.
            long next = dataPos + size;
            if ((next & 1) == 1)
                next++;
            fs.Seek(next, SeekOrigin.Begin);
        }

        if (verbose)
            Console.WriteLine($"Patched {patched} ELF member(s) in {libPath}");
    }

    public override int Handle(ParseResult result)
    {
        bool nooptimize = result.GetValueForOption(DisableOptimizationOption);
        bool optimizeSpace = result.GetValueForOption(OptimizeSizeOption);
        bool optimizeTime = result.GetValueForOption(OptimizeSpeedOption);
        string homePath = CommonOptions.HomePath;
        string ziskLibPath = Path.Combine(homePath, "lib", "linux", "riscv64", "zisk");

        OptimizationMode optimizationMode = OptimizationMode.Blended;
        if (optimizeSpace)
        {
            if (optimizeTime)
                Console.WriteLine("Warning: overriding -Ot with -Os");
            optimizationMode = OptimizationMode.PreferSize;
        }
        else if (optimizeTime)
            optimizationMode = OptimizationMode.PreferSpeed;
        else if (nooptimize)
            optimizationMode = OptimizationMode.None;

        StandardLibType stdlib = result.GetValueForOption(CommonOptions.StdLibOption);
        string[] userSpecifiedInputFiles = result.GetValueForArgument(CommonOptions.InputFilesArgument);
        string[] inputFiles = CommonOptions.GetInputFiles(userSpecifiedInputFiles);
        string[] defines = result.GetValueForOption(CommonOptions.DefinedSymbolsOption);
        string libc = result.GetValueForOption(TargetLibcOption);
        if (libc == "zisk")
        {
            var definesList = new List<string>(defines ?? Array.Empty<string>());
            definesList.Add("ZKVM_ZISK");
            defines = definesList.ToArray();
        }
        string[] references = CommonOptions.GetReferencePaths(result.GetValueForOption(CommonOptions.ReferencesOption), stdlib,
            result.GetValueForOption(CommonOptions.NoStdLibRefsOption));
        string[] extraLd = result.GetValueForOption(CommonOptions.ExtraLd);

        TargetOS targetOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            targetOS = TargetOS.Windows;
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            targetOS = TargetOS.Linux;
        else
            throw new NotImplementedException();

        TargetArchitecture targetArchitecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => TargetArchitecture.X64,
            Architecture.Arm64 => TargetArchitecture.ARM64,
            Architecture.RiscV64 => TargetArchitecture.RiscV64,
        };

        string targetArchitectureStr = result.GetValueForOption(TargetArchitectureOption);
        if (targetArchitectureStr != null)
        {
            targetArchitecture = targetArchitectureStr.ToLowerInvariant() switch
            {
                "x64" => TargetArchitecture.X64,
                "arm64" => TargetArchitecture.ARM64,
                "riscv64" => TargetArchitecture.RiscV64,
                "x86" => TargetArchitecture.X86,
                _ => throw new Exception($"Target architecture '{targetArchitectureStr}' is not supported"),
            };
        }
        string targetOSStr = result.GetValueForOption(TargetOSOption);
        if (targetOSStr != null)
        {
            targetOS = targetOSStr.ToLowerInvariant() switch
            {
                "windows" => TargetOS.Windows,
                "linux" => TargetOS.Linux,
                "uefi" => TargetOS.UEFI,
                _ => throw new Exception($"Target OS '{targetOSStr}' is not supported"),
            };
        }

        // Handle extlib resolution synchronously - after we know target arch/os/libc
        string[] extLibSpecs = result.GetValueForOption(ExtLibOption);
        List<string> downloadedLibPaths = new List<string>();
        var extLibWrapSymbols = new List<string>();
        bool verbose = result.GetValueForOption(CommonOptions.VerbosityOption);

        var referenceList = new List<string>(references ?? Array.Empty<string>());

        if (extLibSpecs != null && extLibSpecs.Length > 0)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "bflat-extlibs");

            foreach (var spec in extLibSpecs)
            {
                try
                {
                    ExtLibResolver.Result extLibResult = ExtLibResolver.Resolve(
                        spec, tempDir, verbose, targetArchitecture, targetOS, libc).GetAwaiter().GetResult();

                    if (extLibResult.StaticLibPath != null)
                    {
                        downloadedLibPaths.Add(extLibResult.StaticLibPath);

                        // Patch RISC-V ABI if needed
                        if (targetArchitecture == TargetArchitecture.RiscV64)
                            PatchRiscvAbiStaticLib(extLibResult.StaticLibPath, verbose);
                    }

                    if (extLibResult.DotnetLibPath != null)
                    {
                        referenceList.Add(extLibResult.DotnetLibPath);
                        if (verbose)
                            Console.WriteLine($"Added external dotnet reference: {extLibResult.DotnetLibPath}");
                    }

                    foreach (var sym in extLibResult.WrapSymbols)
                    {
                        if (!extLibWrapSymbols.Contains(sym))
                        {
                            extLibWrapSymbols.Add(sym);
                            if (verbose)
                                Console.WriteLine($"Will wrap symbol: {sym}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error resolving external library '{spec}': {ex.Message}");
                    return 1;
                }
            }
        }

        references = referenceList.ToArray();

        OptimizationLevel optimizationLevel = nooptimize ? OptimizationLevel.Debug : OptimizationLevel.Release;

        string userSpecificedOutputFileName = result.GetValueForOption(CommonOptions.OutputOption);
        string outputNameWithoutSuffix =
            userSpecificedOutputFileName != null ? Path.GetFileNameWithoutExtension(userSpecificedOutputFileName) :
            CommonOptions.GetOutputFileNameWithoutSuffix(userSpecifiedInputFiles);

        bool disableStackTraceData = result.GetValueForOption(NoStackTraceDataOption) || stdlib != StandardLibType.DotNet;
        string systemModuleName = DefaultSystemModule;
        string compiledModuleName = Path.GetFileName(outputNameWithoutSuffix);

        if (stdlib == StandardLibType.None && references.Length == 0)
            systemModuleName = compiledModuleName;
        if (stdlib == StandardLibType.Zero)
            systemModuleName = "zerolib";

        ILProvider ilProviderOld = new NativeAotILProvider();

        var logger = new Logger(
            Console.Out,
            ilProviderOld,
            verbose,
            Array.Empty<int>(),
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            new Dictionary<int, bool>(),
            false);

        //
        // Initialize type system context
        //

        SharedGenericsMode genericsMode = SharedGenericsMode.CanonicalReferenceTypes;

        bool disableReflection = result.GetValueForOption(NoReflectionOption);
        var tsTargetOs = targetOS switch
        {
            TargetOS.Windows or TargetOS.UEFI => Internal.TypeSystem.TargetOS.Windows,
            TargetOS.Linux => Internal.TypeSystem.TargetOS.Linux,
        };
        bool supportsReflection = !disableReflection && systemModuleName == DefaultSystemModule;

        string isaArg = result.GetValueForOption(TargetIsaOption);
        InstructionSetSupport instructionSetSupport = Helpers.ConfigureInstructionSetSupport(isaArg, maxVectorTBitWidth: 0, isVectorTOptimistic: false, targetArchitecture, tsTargetOs,
                "Unrecognized instruction set {0}", "Unsupported combination of instruction sets: {0}/{1}", logger,
                optimizingForSize: optimizationMode == OptimizationMode.PreferSize);

        var simdVectorLength = instructionSetSupport.GetVectorTSimdVector();
        var targetAbi = TargetAbi.NativeAot;
        var targetDetails = new TargetDetails(targetArchitecture, tsTargetOs, targetAbi, simdVectorLength);
        var ms = new MemoryStream();

        BuildTargetType buildTargetType = result.GetValueForOption(CommonOptions.TargetOption);

#if DEBUG
        Console.Error.WriteLine("Building with the following inputs:");
        foreach (var input in inputFiles)
        {
            Console.Error.WriteLine("Input: " + input);
        }
        foreach (var input in references)
        {
            Console.Error.WriteLine("Reference: " + input);
        }
#endif

        PerfWatch createCompilationWatch = new PerfWatch("Create IL compilation");
        CSharpCompilation sourceCompilation = ILBuildCommand.CreateCompilation(
            compiledModuleName,
            inputFiles,
            references,
            defines,
            optimizationLevel,
            buildTargetType,
            targetArchitecture,
            targetOS,
            result.GetValueForOption(CommonOptions.LangVersionOption));
        createCompilationWatch.Complete();

        bool nativeLib;
        if (buildTargetType == 0)
        {
            PerfWatch getEntryPointWatch = new PerfWatch("GetEntryPoint");
            nativeLib = sourceCompilation.GetEntryPoint(CancellationToken.None) == null;
            getEntryPointWatch.Complete();
            buildTargetType = nativeLib ? BuildTargetType.Shared : BuildTargetType.Exe;
        }
        else
        {
            nativeLib = buildTargetType == BuildTargetType.Shared;
        }

        DebugInformationFormat debugInfoFormat = result.GetValueForOption(CommonOptions.NoDebugInfoOption)
            ? 0 : DebugInformationFormat.Embedded;
        var emitOptions = new EmitOptions(debugInformationFormat: debugInfoFormat);

        PerfWatch emitWatch = new PerfWatch("C# compiler emit");
        var resinfos = CommonOptions.GetResourceDescriptions(result.GetValueForOption(CommonOptions.ResourceOption));
        var compResult = sourceCompilation.Emit(ms, manifestResources: resinfos, options: emitOptions);
        emitWatch.Complete();
        if (!compResult.Success)
        {
            IEnumerable<Diagnostic> failures = compResult.Diagnostics.Where(diagnostic =>
                diagnostic.IsWarningAsError ||
                diagnostic.Severity == DiagnosticSeverity.Error);

            foreach (Diagnostic diagnostic in failures)
            {
                Console.Error.WriteLine(diagnostic.ToString());
            }

            return 1;
        }
        ms.Seek(0, SeekOrigin.Begin);

        // Persist the Roslyn output so the type system can load it through the
        // standard path-based loader (registered in InputFilePaths below). This
        // replaces the in-memory CacheOpenModule hook that required a runtime patch.
        string compiledModulePath = Path.GetTempFileName();
        using (var moduleFile = File.Create(compiledModulePath))
            ms.CopyTo(moduleFile);
        ms.Dispose();

        string outputFilePath = userSpecificedOutputFileName;
        if (outputFilePath == null)
        {
            outputFilePath = outputNameWithoutSuffix;
            if (targetOS == TargetOS.Windows)
            {
                if (buildTargetType is BuildTargetType.Exe or BuildTargetType.WinExe)
                    outputFilePath += ".exe";
                else
                    outputFilePath += ".dll";
            }
            else if (targetOS == TargetOS.UEFI)
            {
                outputFilePath += ".efi";
            }
            else
            {
                if (buildTargetType is not BuildTargetType.Exe and not BuildTargetType.WinExe)
                {
                    outputFilePath += ".so";

                    outputFilePath = Path.Combine(
                        Path.GetDirectoryName(outputFilePath),
                        "lib" + Path.GetFileName(outputFilePath));
                }
            }
        }

        Console.WriteLine("Supports reflection: " + supportsReflection.ToString());
        CompilerTypeSystemContext typeSystemContext =
            new BflatTypeSystemContext(targetDetails, genericsMode, supportsReflection ? DelegateFeature.All : 0);

        ILProvider ilProvider = new CustomILProvider(ilProviderOld, typeSystemContext,
            isZkvmTarget: libc == "zisk" || libc == "zisk_sim");

        var referenceFilePaths = new Dictionary<string, string>();

        foreach (var reference in references)
        {
            referenceFilePaths[Path.GetFileNameWithoutExtension(reference)] = reference;
        }

        if (targetOS == TargetOS.Windows && targetArchitecture == TargetArchitecture.X86)
            libc ??= "none"; // don't have shcrt for Windows x86 because that one's hacked up

        string patchElfPath = Path.Combine(homePath, "patch_elf.py");
        string libPath = Environment.GetEnvironmentVariable("BFLAT_LIB");
        if (libPath == null)
        {
            char separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

            string currentLibPath = Path.Combine(homePath, "lib");

            libPath = currentLibPath;

            string osPart = targetOS switch
            {
                TargetOS.Linux => "linux",
                TargetOS.Windows => "windows",
                TargetOS.UEFI => "uefi",
                _ => throw new Exception(targetOS.ToString()),
            };
            currentLibPath = Path.Combine(currentLibPath, osPart);
            libPath = currentLibPath + separator + libPath;

            string archPart = targetArchitecture switch
            {
                TargetArchitecture.ARM64 => "arm64",
                TargetArchitecture.X64 => "x64",
                TargetArchitecture.X86 => "x86",
                TargetArchitecture.RiscV64 => "riscv64",
                _ => throw new Exception(targetArchitecture.ToString()),
            };
            currentLibPath = Path.Combine(currentLibPath, archPart);
            libPath = currentLibPath + separator + libPath;

            if (targetOS == TargetOS.Linux)
            {
                var tmpLibc = libc;
                if (libc == "zisk" || libc == "zisk_sim")
                    tmpLibc = "musl";
                currentLibPath = Path.Combine(currentLibPath, tmpLibc ?? "glibc");
                libPath = currentLibPath + separator + libPath;
            }

            Console.WriteLine("Library path: " + libPath);
            if (!Directory.Exists(currentLibPath))
            {
                Console.Error.WriteLine($"Directory '{currentLibPath}' doesn't exist.");
                return 1;
            }
        }

        if (stdlib != StandardLibType.None)
        {
            string mask = stdlib == StandardLibType.DotNet ? "*.dll" : "zerolib.dll";

            foreach (var reference in EnumerateExpandedDirectories(libPath, mask))
            {
                string assemblyName = Path.GetFileNameWithoutExtension(reference);
                if (assemblyName.StartsWith("System.Diagnostics"))
                    continue;
                referenceFilePaths[assemblyName] = reference;
#if DEBUG
                Console.WriteLine("Reference file: " + assemblyName + " -> " + reference);
#endif
            }
        }

        typeSystemContext.InputFilePaths = new Dictionary<string, string>
        {
            [compiledModuleName] = compiledModulePath,
        };
        typeSystemContext.ReferenceFilePaths = referenceFilePaths;

        typeSystemContext.SetSystemModule(typeSystemContext.GetModuleForSimpleName(systemModuleName));

        //ilProvider.TypeContext = typeSystemContext;
        EcmaModule compiledAssembly = typeSystemContext.GetModuleForSimpleName(compiledModuleName);

        ilProvider = new HardwareIntrinsicILProvider(
            instructionSetSupport,
            new ExternSymbolMappedField(typeSystemContext.GetWellKnownType(WellKnownType.Int32), "g_cpuFeatures"),
            ilProvider);

        //
        // Initialize compilation group and compilation roots
        //

        List<string> initAssemblies = new List<string> { "System.Private.CoreLib" };


        if (!disableReflection && !disableStackTraceData)
            initAssemblies.Add("System.Private.StackTraceMetadata");

        initAssemblies.Add("System.Private.TypeLoader");

        initAssemblies.Add("System.Console");

        if (!disableReflection)
            initAssemblies.Add("System.Private.Reflection.Execution");
        // else: System.Private.DisabledReflection no longer exists — reflection-free
        // mode was removed from dotnet/runtime in the .NET 8 timeframe. Its module
        // initializer only installed stub reflection callbacks; with the fully
        // blocked metadata policies below there is nothing to initialize, so
        // reflection APIs that reach the uninstalled callbacks fail fast at
        // runtime instead of throwing the polite reflection-disabled exception.

        initAssemblies.Add("mscorlib");
        initAssemblies.Add("System");

        // Build a list of assemblies that have an initializer that needs to run before
        // any user code runs.
        List<ModuleDesc> assembliesWithInitializers = new List<ModuleDesc>();
        if (stdlib == StandardLibType.DotNet)
        {
            foreach (string initAssemblyName in initAssemblies)
            {
                ModuleDesc assembly = typeSystemContext.GetModuleForSimpleName(initAssemblyName);
                assembliesWithInitializers.Add(assembly);
            }
        }

        var libraryInitializers = new LibraryInitializers(typeSystemContext, assembliesWithInitializers);

        List<MethodDesc> initializerList = new List<MethodDesc>(libraryInitializers.LibraryInitializerMethods);

        CompilationModuleGroup compilationGroup;
        List<ICompilationRootProvider> compilationRoots = new List<ICompilationRootProvider>();
        TypeMapManager typeMapManager = new UsageBasedTypeMapManager(TypeMapMetadata.CreateFromAssembly((EcmaAssembly)compiledAssembly, typeSystemContext));

        compilationRoots.Add(new UnmanagedEntryPointsRootProvider(compiledAssembly));

        if (stdlib == StandardLibType.DotNet)
        {
            compilationRoots.Add(new RuntimeConfigurationRootProvider("g_compilerEmbeddedSettingsBlob", Array.Empty<string>()));
            compilationRoots.Add(new RuntimeConfigurationRootProvider("g_compilerEmbeddedKnobsBlob", Array.Empty<string>()));
            compilationRoots.Add(new ExpectedIsaFeaturesRootProvider(instructionSetSupport));
        }
        else
        {
            compilationRoots.Add(new GenericRootProvider<object>(null, (_, rooter) => rooter.RootReadOnlyDataBlob(new byte[4], 4, "Trap threads", "RhpTrapThreads", exportHidden: true)));
        }

        if (!nativeLib)
        {
            compilationRoots.Add(new MainMethodRootProvider(compiledAssembly, initializerList, generateLibraryAndModuleInitializers: true));
        }

        if (compiledAssembly != typeSystemContext.SystemModule)
            compilationRoots.Add(new UnmanagedEntryPointsRootProvider((EcmaModule)typeSystemContext.SystemModule, hidden: true));
        compilationGroup = new SingleFileCompilationModuleGroup();

        if (nativeLib)
        {
            // Set owning module of generated native library startup method to compiler generated module,
            // to ensure the startup method is included in the object file during multimodule mode build
            compilationRoots.Add(new NativeLibraryInitializerRootProvider(typeSystemContext.GeneratedAssembly, initializerList));
        }

        //
        // Compile
        //

        CompilationBuilder builder = new RyuJitCompilationBuilder(typeSystemContext, compilationGroup);

        builder.UseCompilationUnitPrefix("");

        // Profile-guided optimization: feed MIBC profile(s) to the RyuJit compilation.
        string[] mibcFiles = result.GetValueForOption(MibcOption);
        if (mibcFiles != null && mibcFiles.Length > 0)
        {
            ((RyuJitCompilationBuilder)builder).UseProfileData(mibcFiles);
        }

        List<string> directPinvokeList = new List<string>();
        List<string> directPinvokes = new List<string>(result.GetValueForOption(DirectPInvokesOption));
        if (targetOS == TargetOS.Windows)
        {
            directPinvokeList.Add(Path.Combine(homePath, "WindowsAPIs.txt"));
            directPinvokes.Add("System.IO.Compression.Native");
            directPinvokes.Add("System.Globalization.Native");
            directPinvokes.Add("sokol");
            directPinvokes.Add("shell32!CommandLineToArgvW"); // zerolib uses this
        }
        else if (targetOS == TargetOS.Linux)
        {
            directPinvokes.Add("libSystem.Native");
            directPinvokes.Add("libSystem.Globalization.Native");
            directPinvokes.Add("libSystem.IO.Compression.Native");
            directPinvokes.Add("libSystem.Net.Security.Native");
            directPinvokes.Add("libSystem.Security.Cryptography.Native.OpenSsl");
            directPinvokes.Add("libsokol");
        }

        if (libc == "zisk")
        {
            directPinvokes.Add("__Internal");
        }

#if DEBUG
        foreach (var dp in directPinvokes)
        {
            Console.WriteLine("Direct P/Invoke: " + dp);
        }
#endif

        PInvokeILEmitterConfiguration pinvokePolicy = new ConfigurablePInvokePolicy(typeSystemContext.Target, directPinvokes, directPinvokeList);

        var featureSwitches = new Dictionary<string, bool>()
        {
            { "System.Diagnostics.Debugger.IsSupported", false },
            { "System.Diagnostics.Tracing.EventSource.IsSupported", false },
            { "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", false },
            { "System.Resources.ResourceManager.AllowCustomResourceTypes", false },
            { "System.Text.Encoding.EnableUnsafeUTF7Encoding", false },
            { "System.Linq.Expressions.CanEmitObjectArrayDelegate", false },
            { "System.ComponentModel.DefaultValueAttribute.IsSupported", false },
            { "System.ComponentModel.Design.IDesignerHost.IsSupported", false },
            { "System.ComponentModel.TypeConverter.EnableUnsafeBinaryFormatterInDesigntimeLicenseContextSerialization", false },
            { "System.ComponentModel.TypeDescriptor.IsComObjectDescriptorSupported", false },
            { "System.Data.DataSet.XmlSerializationIsSupported", false },
            { "System.Linq.Enumerable.IsSizeOptimized", true },
            { "System.Net.SocketsHttpHandler.Http3Support", false },
            { "System.Reflection.Metadata.MetadataUpdater.IsSupported", false },
            { "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported", false },
            { "System.Runtime.InteropServices.BuiltInComInterop.IsSupported", false },
            { "System.Runtime.InteropServices.EnableConsumingManagedCodeFromNativeHosting", false },
            { "System.Runtime.InteropServices.EnableCppCLIHostActivation", false },
            { "System.Runtime.InteropServices.Marshalling.EnableGeneratedComInterfaceComImportInterop", false },
            { "System.StartupHookProvider.IsSupported", false },
            { "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false },
            { "System.Threading.Thread.EnableAutoreleasePool", false },
            { "System.Threading.ThreadPool.UseWindowsThreadPool", true },
            { "System.Globalization.PredefinedCulturesOnly", true },
        };

        bool disableExceptionMessages = result.GetValueForOption(NoExceptionMessagesOption);
        if (disableExceptionMessages || disableReflection)
        {
            featureSwitches.Add("System.Resources.UseSystemResourceKeys", true);
        }

        bool disableGlobalization = result.GetValueForOption(NoGlobalizationOption) || libc == "bionic" || libc == "musl" || libc == "zisk" || libc == "zisk_sim";
        if (disableGlobalization)
        {
            featureSwitches.Add("System.Globalization.Invariant", true);
        }

        if (libc == "zisk" || libc == "zisk_sim")
        {
            // Invariant timezone (UTC only): the deterministic zkVM guest has no
            // timezone database, and the [FeatureSwitchDefinition] on
            // TimeZoneInfo.Invariant lets ILC fold and trim the timezone-data
            // loading paths (which also carry floating-point transition math).
            featureSwitches.Add("System.TimeZoneInfo.Invariant", true);
        }

        if (disableStackTraceData)
        {
            featureSwitches.Add("System.Diagnostics.StackTrace.IsSupported", false);
        }

        foreach (var featurePair in result.GetValueForOption(FeatureSwitchOption))
        {
            int index = featurePair.IndexOf('=');
            if (index <= 0 || index == featurePair.Length - 1)
                continue;

            string name = featurePair.Substring(0, index);
            bool value = featurePair.Substring(index + 1) != "false";
            featureSwitches[name] = value;
        }

        // User-provided ILLink.Substitutions XML (same format and wiring as ilc's
        // --substitution): method body stubs/removals and static field value
        // substitutions, constant-folded with branch elimination before scanning,
        // so code guarded by a substituted value is trimmed from the image.
        BodyAndFieldSubstitutions substitutions = default;
        IReadOnlyDictionary<ModuleDesc, IReadOnlySet<string>> resourceBlocks = default;

        if (libc == "zisk" || libc == "zisk_sim")
        {
            // Built-in substitutions for the zkVM targets (embedded
            // zisk.substitutions.xml): thread-pool tuning and similar machinery
            // that is provably dead on a single-threaded guest but drags
            // floating-point code into the rv64ima image. Applied before user
            // files; the parser rejects duplicate method entries, so user files
            // extend rather than override this set.
            using Stream ziskSubstitutions =
                typeof(BuildCommand).Assembly.GetManifestResourceStream("zisk.substitutions.xml");
            substitutions.AppendFrom(BodySubstitutionsParser.GetSubstitutions(
                logger, typeSystemContext, XmlReader.Create(ziskSubstitutions),
                "zisk.substitutions.xml", featureSwitches));
        }
        foreach (string substitutionFilePath in result.GetValueForOption(SubstitutionFilePathsOption) ?? Array.Empty<string>())
        {
            using FileStream fs = File.OpenRead(substitutionFilePath);
            substitutions.AppendFrom(BodySubstitutionsParser.GetSubstitutions(
                logger, typeSystemContext, XmlReader.Create(fs), substitutionFilePath, featureSwitches));

            fs.Seek(0, SeekOrigin.Begin);

            resourceBlocks = ManifestResourceBlockingPolicy.UnionBlockings(resourceBlocks,
                ManifestResourceBlockingPolicy.SubstitutionsReader.GetSubstitutions(
                    logger, typeSystemContext, XmlReader.Create(fs), substitutionFilePath, featureSwitches));
        }

        SubstitutionProvider substitutionProvider = new SubstitutionProvider(logger, featureSwitches, substitutions);
        ILProvider unsubstitutedILProvider = ilProvider;
        ilProvider = new SubstitutedILProvider(ilProvider, substitutionProvider, new DevirtualizationManager());

        var stackTracePolicy = !disableStackTraceData ?
            (StackTraceEmissionPolicy)new EcmaMethodStackTraceEmissionPolicy() : new NoStackTraceEmissionPolicy();

        MetadataBlockingPolicy mdBlockingPolicy;
        ManifestResourceBlockingPolicy resBlockingPolicy;
        UsageBasedMetadataGenerationOptions metadataGenerationOptions = default;
        if (supportsReflection)
        {
            mdBlockingPolicy = new NoMetadataBlockingPolicy();

            resBlockingPolicy = new ManifestResourceBlockingPolicy(logger, featureSwitches, resourceBlocks);

            // When reflection is enabled, prefer a "just works" experience by default.
            // This matches the most practical ILCompiler/ilc configurations (scan reflection + keep
            // enough metadata to make common reflection scenarios succeed).
            metadataGenerationOptions |= UsageBasedMetadataGenerationOptions.ReflectionILScanning;
            if (result.GetValueForOption(RootDefaultAssemblies))
            {
                metadataGenerationOptions |= UsageBasedMetadataGenerationOptions.CompleteTypesOnly;
                metadataGenerationOptions |= UsageBasedMetadataGenerationOptions.CreateReflectableArtifacts;
                metadataGenerationOptions |= UsageBasedMetadataGenerationOptions.RootDefaultAssemblies;
            }
        }
        else
        {
            mdBlockingPolicy = new FullyBlockedMetadataBlockingPolicy();
            resBlockingPolicy = new FullyBlockedManifestResourceBlockingPolicy();
        }
        DynamicInvokeThunkGenerationPolicy invokeThunkGenerationPolicy = new DefaultDynamicInvokeThunkGenerationPolicy();

        var compilerGenerateState = new ILCompiler.Dataflow.CompilerGeneratedState(ilProvider, logger, false);
        var flowAnnotations = new ILLink.Shared.TrimAnalysis.FlowAnnotations(logger, ilProvider, compilerGenerateState);

        MetadataManagerOptions metadataOptions = default;
#if false
        if (stdlib == StandardLibType.DotNet)
            metadataOptions |= MetadataManagerOptions.DehydrateData;
#endif
        MetadataManager metadataManager = new UsageBasedMetadataManager(
            compilationGroup,
            typeSystemContext,
            mdBlockingPolicy,
            resBlockingPolicy,
            logFile: null,
            stackTracePolicy,
            invokeThunkGenerationPolicy,
            flowAnnotations,
            metadataGenerationOptions,
            metadataOptions,
            logger,
            featureSwitches,
            rootEntireAssembliesModules: Array.Empty<string>(),
            additionalRootedAssemblies: Array.Empty<string>(),
            trimmedAssemblies: Array.Empty<string>(),
            satelliteAssemblyFilePaths: Array.Empty<string>());

        InteropStateManager interopStateManager = new InteropStateManager(typeSystemContext.GeneratedAssembly);
        InteropStubManager interopStubManager = new UsageBasedInteropStubManager(interopStateManager, pinvokePolicy, logger);

        // We enable scanner for retail builds by default.
        bool useScanner = optimizationMode != OptimizationMode.None;

        // Enable static data preinitialization in optimized builds.
        bool preinitStatics = optimizationMode != OptimizationMode.None;

        TypePreinit.TypePreinitializationPolicy preinitPolicy = preinitStatics ?
                new TypePreinit.TypeLoaderAwarePreinitializationPolicy() : new TypePreinit.DisabledPreinitializationPolicy();

        var preinitManager = new PreinitializationManager(typeSystemContext, compilationGroup, ilProvider, preinitPolicy, new StaticReadOnlyFieldPolicy(), flowAnnotations);

        builder
            .UseILProvider(ilProvider)
            .UsePreinitializationManager(preinitManager)
            .UseTypeMapManager(typeMapManager)
            .UseResilience(true);

        int parallelism = Environment.ProcessorCount;
        ILScanResults scanResults = null;
        if (useScanner)
        {
            if (logger.IsVerbose)
                logger.LogMessage("Scanning input IL");
            ILScannerBuilder scannerBuilder = builder.GetILScannerBuilder()
                .UseCompilationRoots(compilationRoots)
                .UseMetadataManager(metadataManager)
                .UseParallelism(parallelism)
                .UseInteropStubManager(interopStubManager)
                .UseTypeMapManager(typeMapManager)
                .UseLogger(logger);

            string scanDgmlLogFileName = result.GetValueForOption(MstatOption) ? Path.ChangeExtension(outputFilePath, ".scan.dgml.xml") : null;
            if (scanDgmlLogFileName != null)
                scannerBuilder.UseDependencyTracking(DependencyTrackingLevel.First);

            IILScanner scanner = scannerBuilder.ToILScanner();

            PerfWatch scanWatch = new PerfWatch("Scanner");
            scanResults = scanner.Scan();
            scanWatch.Complete();

            if (scanDgmlLogFileName != null)
                scanResults.WriteDependencyLog(scanDgmlLogFileName);

            metadataManager = ((UsageBasedMetadataManager)metadataManager).ToAnalysisBasedMetadataManager();

            interopStubManager = scanResults.GetInteropStubManager(interopStateManager, pinvokePolicy);
        }

        DebugInformationProvider debugInfoProvider =
            debugInfoFormat == 0 ? new NullDebugInformationProvider() : new DebugInformationProvider();

        string dgmlLogFileName = result.GetValueForOption(MstatOption) ? Path.ChangeExtension(outputFilePath, ".codegen.dgml.xml") : null; ;
        DependencyTrackingLevel trackingLevel = dgmlLogFileName == null ?
            DependencyTrackingLevel.None : DependencyTrackingLevel.First;

        MethodBodyFoldingMode foldMethodBodies = (optimizationMode != OptimizationMode.None)
            ? MethodBodyFoldingMode.All
            : MethodBodyFoldingMode.None;

        compilationRoots.Add(metadataManager);
        compilationRoots.Add(interopStubManager);

        var backendOptions = new List<string>();
        if (optimizationMode != OptimizationMode.None)
        {
            backendOptions.Add("JitObjectStackAllocation=1");

            // zkVM RyuJIT codegen tuning, fixed into bflat. RyuJIT parses these
            // integer values as HEXADECIMAL with no 0x prefix
            // (JitConfigProvider.getIntConfigValue uses NumberStyles.AllowHexSpecifier),
            // so "2000" == 0x2000 == 8192.

            // Max stack-allocatable object size (knob default 528 / 0x210).
            // Lifting the in-loop heap restriction needs runtime patch
            // 25_stackalloc_aggressive_riscv64.patch.
            backendOptions.Add("JitObjectStackAllocationSize=2000"); // 0x2000 = 8192

            // Inlining caps, raised moderately. Stays on ExtendedDefaultPolicy
            // (weighs code growth) rather than JitAggressiveInlining, which
            // overflows the fixed ZisK ROM. Lower MaxIL if ROM overflows.
            backendOptions.Add("JitExtDefaultPolicyMaxIL=200"); // 0x200 = 512 (default 0x80 = 128) max inlinee IL
            backendOptions.Add("JitExtDefaultPolicyMaxBB=10");  // 0x10  = 16  (default 7)         max inlinee basic blocks

            // Lower constant-size SpanHelpers.SequenceEqual to the inline
            // `csrs 0x814, src ; addi rd, dst, count` idiom that the ZisK
            // transpiler folds into one dma_xmemcmp step. ZisK-only (a plain
            // riscv64 CPU would mis-execute the addi). Needs runtime patch
            // 30_dma_memcmp_inline_riscv64.
            backendOptions.Add("JitRiscV64DmaCompare=1");

            // Elide RA spill/reload + frame in leaf methods. RyuJIT riscv64 uses
            // REG_RA as a hardcoded scratch for branch/compare constants, far-jump
            // targets and 64-bit mul-high, so patch 31 refuses to elide methods
            // whose LIR contains those shapes (GT_JCMP / GT_LT/LE/GT/GE / GT_MULHI)
            // or use FP. Needs runtime patches 23+31.
            backendOptions.Add("RiscV64ElideLeafRaSave=1");
        }

        builder
            .UseInstructionSetSupport(instructionSetSupport)
            .UseBackendOptions(backendOptions)
            .UseMethodBodyFolding(foldMethodBodies)
            .UseMetadataManager(metadataManager)
            .UseParallelism(parallelism)
            .UseInteropStubManager(interopStubManager)
            .UseLogger(logger)
            .UseDependencyTracking(trackingLevel)
            .UseCompilationRoots(compilationRoots)
            .UseOptimizationMode(optimizationMode)
            .UseDebugInfoProvider(debugInfoProvider);

        if (scanResults != null)
        {
            DevirtualizationManager devirtualizationManager = scanResults.GetDevirtualizationManager();

            builder.UseTypeMapManager(scanResults.GetTypeMapManager());

            substitutions.AppendFrom(scanResults.GetBodyAndFieldSubstitutions());

            substitutionProvider = new SubstitutionProvider(logger, featureSwitches, substitutions);

            ilProvider = new SubstitutedILProvider(unsubstitutedILProvider, substitutionProvider, devirtualizationManager, metadataManager, scanResults.GetAnalysisCharacteristics());

            if (libc == "zisk" || libc == "zisk_sim")
            {
                // Codegen-only: rewrite the ConcurrentUnifier growth ratio to
                // integer math AFTER scanning. Relies on PatchedMethodIL
                // correctly forwarding GetMethodILDefinition for this shared
                // generic method (without which the generic dictionary layout
                // is corrupted). See UnifierResizeILProvider.
                ilProvider = new UnifierResizeILProvider(ilProvider);
            }

            // Use a more precise IL provider that uses whole program analysis for dead branch elimination
            builder.UseILProvider(ilProvider);

            // If we have a scanner, feed the vtable analysis results to the compilation.
            // This could be a command line switch if we really wanted to.
            builder.UseVTableSliceProvider(scanResults.GetVTableLayoutInfo());

            // If we have a scanner, feed the generic dictionary results to the compilation.
            // This could be a command line switch if we really wanted to.
            builder.UseGenericDictionaryLayoutProvider(scanResults.GetDictionaryLayoutInfo());

            // If we have a scanner, we can drive devirtualization using the information
            // we collected at scanning time (effectively sealing unsealed types if possible).
            // This could be a command line switch if we really wanted to.
            builder.UseDevirtualizationManager(devirtualizationManager);

            // If we use the scanner's result, we need to consult it to drive inlining.
            // This prevents e.g. devirtualizing and inlining methods on types that were
            // never actually allocated.
            builder.UseInliningPolicy(scanResults.GetInliningPolicy());

            // Use an error provider that prevents us from re-importing methods that failed
            // to import with an exception during scanning phase. We would see the same failure during
            // compilation, but before RyuJIT gets there, it might ask questions that we don't
            // have answers for because we didn't scan the entire method.
            builder.UseMethodImportationErrorProvider(scanResults.GetMethodImportationErrorProvider());

            // If we're doing preinitialization, use a new preinitialization manager that
            // has the whole program view.
            if (preinitStatics)
            {
                var readOnlyFieldPolicy = scanResults.GetReadOnlyFieldPolicy();
                preinitManager = new PreinitializationManager(typeSystemContext, compilationGroup, ilProvider, scanResults.GetPreinitializationPolicy(),
                    readOnlyFieldPolicy, flowAnnotations);
                builder.UsePreinitializationManager(preinitManager)
                    .UseReadOnlyFieldPolicy(readOnlyFieldPolicy);
            }

            // If we have a scanner, we can inline threadstatics storage using the information
            // we collected at scanning time. Only supported on Linux/Windows x64/ARM64 by RyuJIT;
            // RISC-V (incl. zisk) uses a different TLS model and is not covered.
            if ((targetOS == TargetOS.Linux || targetOS == TargetOS.Windows)
                && (targetArchitecture == TargetArchitecture.X64 || targetArchitecture == TargetArchitecture.ARM64))
            {
                builder.UseInlinedThreadStatics(scanResults.GetInlinedThreadStatics());
            }
        }

        ICompilation compilation = builder.ToCompilation();

        if (logger.IsVerbose)
            logger.LogMessage("Generating native code");
        string mapFileName = result.GetValueForOption(MapFileOption);
        string mstatFileName = result.GetValueForOption(MstatOption) ? Path.ChangeExtension(outputFilePath, ".mstat") : null;

        List<ObjectDumper> dumpers = new List<ObjectDumper>();

        if (mapFileName != null)
            dumpers.Add(new XmlObjectDumper(mapFileName));

        if (mstatFileName != null)
            dumpers.Add(new MstatObjectDumper(mstatFileName, typeSystemContext));

        string objectFilePath = Path.ChangeExtension(outputFilePath, targetOS is TargetOS.Windows or TargetOS.UEFI ? ".obj" : ".o");
        string patchedFilePath = Path.ChangeExtension(outputFilePath, ".patched");

        PerfWatch compileWatch = new PerfWatch("Native compile");
        CompilationResults compilationResults = compilation.Compile(objectFilePath, ObjectDumper.Compose(dumpers));
        compileWatch.Complete();

        string exportsFile = null;
        if (nativeLib)
        {
            exportsFile = Path.ChangeExtension(outputFilePath, targetOS == TargetOS.Windows ? ".def" : ".txt");
            ExportsFileWriter defFileWriter = new ExportsFileWriter(typeSystemContext, exportsFile, []);
            foreach (var compilationRoot in compilationRoots)
            {
                if (compilationRoot is UnmanagedEntryPointsRootProvider provider && !provider.Hidden)
                    defFileWriter.AddExportedMethods(provider.ExportedMethods);
            }

            defFileWriter.EmitExportedMethods();
        }

        typeSystemContext.LogWarnings(logger);

        if (dgmlLogFileName != null)
            compilationResults.WriteDependencyLog(dgmlLogFileName);

        if (debugInfoProvider is IDisposable)
            ((IDisposable)debugInfoProvider).Dispose();

        preinitManager.LogStatistics(logger);

        if (result.GetValueForOption(NoLinkOption))
        {
            return 0;
        }

        //
        // Run the platform linker
        //

        if (targetArchitecture == TargetArchitecture.RiscV64)
        {
            PatchRiscvAbi(objectFilePath);
        }

        if (logger.IsVerbose)
            logger.LogMessage("Running the linker");

        string ld = Environment.GetEnvironmentVariable("BFLAT_LD");
        if (ld == null)
        {
            string toolSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

            ld = Path.Combine(homePath, "bin", "lld" + toolSuffix);
        }

        bool deterministic = result.GetValueForOption(CommonOptions.DeterministicOption);

        var ldArgs = new StringBuilder();

        if (targetOS is TargetOS.Windows or TargetOS.UEFI)
        {
            ldArgs.Append("-flavor link \"");
            ldArgs.Append(objectFilePath);
            ldArgs.Append("\" ");
            ldArgs.AppendFormat("/out:\"{0}\" ", outputFilePath);
            if (deterministic)
                ldArgs.Append("/Brepro ");

            foreach (var lpath in libPath.Split(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':'))
            {
                ldArgs.AppendFormat("/libpath:\"{0}\" ", lpath);
            }

            if (targetOS == TargetOS.UEFI)
                ldArgs.Append("/subsystem:EFI_APPLICATION ");
            else if (buildTargetType == BuildTargetType.Exe)
                ldArgs.Append("/subsystem:console ");
            else if (buildTargetType == BuildTargetType.WinExe)
                ldArgs.Append("/subsystem:windows ");

            if (targetOS == TargetOS.UEFI)
            {
                ldArgs.Append("/entry:EfiMain ");
            }
            else if (buildTargetType is BuildTargetType.Exe or BuildTargetType.WinExe)
            {
                if (stdlib == StandardLibType.DotNet)
                    ldArgs.Append("/entry:wmainCRTStartup bootstrapper.obj ");
                else
                    ldArgs.Append("/entry:__managed__Main ");

                if (result.GetValueForOption(NoPieOption) && targetArchitecture != TargetArchitecture.ARM64)
                    ldArgs.Append("/fixed ");
            }
            else if (buildTargetType is BuildTargetType.Shared)
            {
                ldArgs.Append("/dll ");
                if (stdlib == StandardLibType.DotNet)
                    ldArgs.Append("bootstrapperdll.obj ");
                ldArgs.Append($"/def:\"{exportsFile}\" ");
            }

            ldArgs.Append("/incremental:no ");
            if (debugInfoFormat != 0)
                ldArgs.Append("/debug ");
            if (stdlib == StandardLibType.DotNet)
            {
                ldArgs.Append("Runtime.WorkstationGC.lib System.IO.Compression.Native.Aot.lib System.Globalization.Native.Aot.lib ");
            }
            else
            {
                ldArgs.Append("/merge:.modules=.rdata ");
                ldArgs.Append("/merge:.managedcode=.text ");

                if (stdlib == StandardLibType.Zero)
                {
                    if (targetArchitecture is TargetArchitecture.ARM64 or TargetArchitecture.X86
                        or TargetArchitecture.RiscV64
                        )
                        ldArgs.Append("zerolibnative.obj ");
                }
            }
            if (targetOS == TargetOS.Windows)
            {
                if (targetArchitecture != TargetArchitecture.X86)
                    ldArgs.Append("sokol.lib ");
                ldArgs.Append("advapi32.lib bcrypt.lib crypt32.lib iphlpapi.lib kernel32.lib mswsock.lib ncrypt.lib normaliz.lib  ntdll.lib ole32.lib oleaut32.lib user32.lib version.lib ws2_32.lib shell32.lib Secur32.Lib ");

                if (libc != "none")
                {
                    ldArgs.Append("shcrt.lib ");
                    ldArgs.Append("api-ms-win-crt-conio-l1-1-0.lib api-ms-win-crt-convert-l1-1-0.lib api-ms-win-crt-environment-l1-1-0.lib ");
                    ldArgs.Append("api-ms-win-crt-filesystem-l1-1-0.lib api-ms-win-crt-heap-l1-1-0.lib api-ms-win-crt-locale-l1-1-0.lib ");
                    ldArgs.Append("api-ms-win-crt-multibyte-l1-1-0.lib api-ms-win-crt-math-l1-1-0.lib ");
                    ldArgs.Append("api-ms-win-crt-process-l1-1-0.lib api-ms-win-crt-runtime-l1-1-0.lib api-ms-win-crt-stdio-l1-1-0.lib ");
                    ldArgs.Append("api-ms-win-crt-string-l1-1-0.lib api-ms-win-crt-time-l1-1-0.lib api-ms-win-crt-utility-l1-1-0.lib ");
                }
            }
            ldArgs.Append("/opt:ref,icf /nodefaultlib:libcpmt.lib ");

            if (result.GetValueForOption(LtoOption))
            {
                ldArgs.Append("/ltcg ");
            }

            // Add downloaded external libraries for Windows
            foreach (var extLibPath in downloadedLibPaths)
            {
                ldArgs.Append($"\"{extLibPath}\" ");
            }
        }
        else if (targetOS == TargetOS.Linux)
        {
            ldArgs.Append("-flavor ld ");
            ldArgs.Append("--no-relax ");

            if (result.GetValueForOption(LtoOption))
            {
                ldArgs.Append("--lto=full --lto-O3 ");
            }

            string ziskSimLibPath = Path.Combine(homePath, "lib", "linux", "riscv64", "zisk_sim");

            string firstLib = null;
            foreach (var lpath in libPath.Split(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':'))
            {
                ldArgs.AppendFormat("-L\"{0}\" ", lpath);
                if (firstLib == null)
                    firstLib = lpath;
            }

            ldArgs.Append("-z now -z relro -z noexecstack --hash-style=gnu --eh-frame-hdr -z nostart-stop-gc ");

            if (targetArchitecture == TargetArchitecture.ARM64)
                ldArgs.Append("-EL --fix-cortex-a53-843419 ");

            if (libc == "bionic")
                ldArgs.Append("--warn-shared-textrel -z max-page-size=4096 --enable-new-dtags ");

            if (buildTargetType != BuildTargetType.Shared)
            {
                if (libc == "bionic")
                {
                    ldArgs.Append("-dynamic-linker /system/bin/linker64 ");
                    ldArgs.Append($"\"{firstLib}/crtbegin_dynamic.o\" ");
                }
                else if (libc == "musl" || libc == "zisk" || libc == "zisk_sim")
                {
                    ldArgs.Append("-static ");
                    if (libc == "zisk" || libc == "zisk_sim")
                    {
                        ldArgs.Append($"\"{ziskLibPath}/crt1.o\" ");
                        PatchRiscvAbi(ziskLibPath + "/crt1.o");
                    }
                    else
                        ldArgs.Append($"\"{firstLib}/crt1.o\" ");
                    ldArgs.Append($"\"{firstLib}/crti.o\" ");
                    if (libc == "zisk" || libc == "zisk_sim")
                    {
                        PatchRiscvAbi(firstLib + "/crti.o");
                    }
                }
                else
                {
                    if (targetArchitecture == TargetArchitecture.ARM64)
                        ldArgs.Append("-dynamic-linker /lib/ld-linux-aarch64.so.1 ");
                    else if (targetArchitecture == TargetArchitecture.RiscV64)
                        ldArgs.Append("-dynamic-linker /lib/ld-linux-riscv64-lp64d.so.1 ");
                    else
                        ldArgs.Append("-dynamic-linker /lib64/ld-linux-x86-64.so.2 ");
                    ldArgs.Append($"\"{firstLib}/Scrt1.o\" ");
                }
                if (stdlib != StandardLibType.DotNet)
                    ldArgs.Append("--defsym=main=__managed__Main ");
            }
            else
            {
                if (libc == "bionic")
                {
                    ldArgs.Append($"\"{firstLib}/crtbegin_so.o\" ");
                }
            }

            ldArgs.AppendFormat("-o \"{0}\" ", outputFilePath);

            if (libc != "bionic" && libc != "musl" && libc != "zisk" &&
                libc != "zisk_sim")
            {
                ldArgs.Append($"\"{firstLib}/crti.o\" ");
                ldArgs.Append($"\"{firstLib}/crtbeginS.o\" ");
            }

            ldArgs.Append('"');
            ldArgs.Append(objectFilePath);
            ldArgs.Append('"');
            ldArgs.Append(' ');
            ldArgs.Append("--as-needed --gc-sections ");
            ldArgs.Append("-rpath \"$ORIGIN\" ");

            if (buildTargetType == BuildTargetType.Shared)
            {
                if (stdlib == StandardLibType.DotNet)
                {
                    ldArgs.Append($"\"{firstLib}/libbootstrapperdll.o\" ");
                }

                ldArgs.Append("-shared ");
                ldArgs.Append($"--version-script=\"{exportsFile}\" ");
            }
            else
            {
                if (stdlib == StandardLibType.DotNet)
                    ldArgs.Append($"\"{firstLib}/libbootstrapper.o\" ");

                if (result.GetValueForOption(NoPieOption))
                    ldArgs.Append("--no-pie ");
                else
                    ldArgs.Append("-pie ");
            }

            if (stdlib != StandardLibType.None)
            {
                ldArgs.Append("-lSystem.Native ");
                if (stdlib == StandardLibType.DotNet)
                {
                    ldArgs.Append("-latomic ");
                    // libatomic.a ships with the hard-float (lp64d) marker like
                    // libc.a, so normalize it to soft-float for the zisk stack too,
                    // otherwise ld.lld rejects its members against crt1.o.
                    if (libc == "zisk" || libc == "zisk_sim")
                        PatchRiscvAbiStaticLib(firstLib + "/libatomic.a", verbose);
                    ldArgs.Append("-leventpipe-disabled ");
                    ldArgs.Append("-laotminipal -lstandalonegc-disabled ");
                    ldArgs.Append("-lstdc++compat -lRuntime.WorkstationGC -lSystem.IO.Compression.Native -lSystem.Security.Cryptography.Native.OpenSsl ");
                    if (libc != "bionic")
                        ldArgs.Append("-lSystem.Globalization.Native ");
                }
                else if (stdlib == StandardLibType.Zero)
                {
                    if (targetArchitecture == TargetArchitecture.ARM64 || targetArchitecture == TargetArchitecture.RiscV64)
                        ldArgs.Append($"\"{firstLib}/libzerolibnative.o\" ");
                }
            }

            ldArgs.Append("--as-needed -ldl -lm -lz -z relro -z now --discard-all --gc-sections ");
            if (libc != "musl" && libc != "zisk" && libc != "zisk_sim")
            {
                ldArgs.Append("-lc -lgcc ");
            }

            if (libc != "bionic" && libc != "musl" && libc != "zisk" &&
                libc != "zisk_sim")
            {
                ldArgs.Append("-lrt --as-needed -lgcc_s --no-as-needed ");
                if (!result.GetValueForOption(CommonOptions.NoPthreadOption))
                    ldArgs.Append("-lpthread ");
            }
            else if (libc == "musl" || libc == "zisk" || libc == "zisk_sim")
            {
                ldArgs.Append($"\"{firstLib}/libc.a\" ");
                // The zisk/zisk_sim stack is linked with the soft-float (lp64)
                // ABI marker (see PatchRiscvAbi on crt1.o/crti.o above). The
                // bundled musl libc.a still carries the hard-float (lp64d)
                // marker, so normalize it too or ld.lld rejects every member
                // with "different floating-point ABI from crt1.o".
                if (libc == "zisk" || libc == "zisk_sim")
                    PatchRiscvAbiStaticLib(firstLib + "/libc.a", verbose);
            }

            if (libc == "bionic")
            {
                if (buildTargetType == BuildTargetType.Shared)
                {
                    ldArgs.Append($"\"{firstLib}/crtend_so.o\" ");
                }
                else
                {
                    ldArgs.Append($"\"{firstLib}/crtend_android.o\" ");
                }
            }
            else if (libc == "musl" || libc == "zisk" || libc == "zisk_sim")
            {
                ldArgs.Append($"\"{firstLib}/crtn.o\" ");
                // Same soft-float marker normalization as crt1.o/crti.o.
                if (libc == "zisk" || libc == "zisk_sim")
                    PatchRiscvAbi(firstLib + "/crtn.o");
            }
            else
            {
                ldArgs.Append($"\"{firstLib}/crtendS.o\" ");
                ldArgs.Append($"\"{firstLib}/crtn.o\" ");
            }

            foreach (var ldArg in extraLd)
            {
                ldArgs.Append(ldArg.Replace("{libpath}", firstLib) + " ");
            }

            // Add downloaded external libraries for Linux
            foreach (var extLibPath in downloadedLibPaths)
            {
                ldArgs.Append($"\"{extLibPath}\" ");
            }

            // Add --wrap flags for symbols requested by external libraries
            foreach (var sym in extLibWrapSymbols)
            {
                ldArgs.Append($"--wrap={sym} ");
            }

            if (libc == "musl")
            {
                /* hack, no fp must be built properly */
                ldArgs.Append($"\"{Path.Combine(firstLib, "nofp.o")}\" ");
            }


            if (libc == "zisk" || libc == "zisk_sim")
            {
                /* Zisk */
                if (libc == "zisk")
                {
                    ldArgs.Append($"-T\"{Path.Combine(ziskLibPath, "script.ld")}\" ");
                }
                else
                {
                    ldArgs.Append($"-T\"{Path.Combine(ziskSimLibPath, "script.ld")}\" ");
                }
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "entrypoint.o")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "nofp.o")}\" ");
                ldArgs.Append($"--whole-archive ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "ubootstrap.o")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "stdcppshim.o")}\" ");
                if (libc == "zisk")
                {
                    ldArgs.Append($"--wrap=inline_bump_alloc_aligned ");
                }
                /* rhp */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rhp.o")}\" ");
                ldArgs.Append($"--wrap=RhpNewFast ");
                ldArgs.Append($"--wrap=RhpNewObject ");
                ldArgs.Append($"--wrap=RhpNewPtrArrayFast ");
                ldArgs.Append($"--wrap=RhpNewArrayFast ");
                ldArgs.Append($"--wrap=RhNewString ");
                ldArgs.Append($"--wrap=RhpPInvoke ");
                ldArgs.Append($"--wrap=RhpPInvokeReturn ");
                /* No-op the reverse P/Invoke transition: the real one parks the
                 * thread at a GC-safe point, which deadlocks when a managed
                 * exception handler is entered from __wrap_RhpThrowEx (thread
                 * already cooperative, single-threaded zkVM never rendezvous). */
                ldArgs.Append($"--wrap=RhpReversePInvoke ");
                ldArgs.Append($"--wrap=RhpReversePInvokeReturn ");
                ldArgs.Append($"--wrap=RhBulkMoveWithWriteBarrier ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Diagnostics_Tracing_EventPipeEventProvider__Register ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Diagnostics_Tracing_EventSource__InitializeDefaultEventSources ");
                ldArgs.Append($"--wrap=GlobalizationNative_GetDefaultLocaleName ");
                /* ProcessorNumberSpeedCheck is no longer wrapped here: the
                 * built-in zisk.substitutions.xml stubs it to false at compile
                 * time, which folds the method away entirely - a --wrap against
                 * the vanished symbol would only trip --wrap-check. */
                ldArgs.Append($"--wrap=RhGetThreadStaticStorage ");
                ldArgs.Append($"--wrap=S_P_CoreLib_Internal_Runtime_ThreadStatics__GetUninlinedThreadStaticBaseForType ");
                ldArgs.Append($"--wrap=_Z16InitializeCGroupv ");
                ldArgs.Append($"--wrap=_Z19InitializeCpuCGroupv ");
                ldArgs.Append($"--wrap=__GetNonGCStaticBase_S_P_CoreLib_System_Environment ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Thread__WaitForForegroundThreads ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__Enter ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__EnterAndGetCurrentThreadId ");
                //ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__EnterScope ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__TryEnterSlow_0 ");
                //ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__TryEnter_0 ");
                //ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__TryEnter_Outlined ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__Exit_0 ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__Exit_1 ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__ExitAll ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__get_IsHeldByCurrentThread ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Runtime_CompilerServices_ClassConstructorRunner__DeadlockAwareAcquire ");
                ldArgs.Append($"--wrap=S_P_TypeLoader_Internal_Runtime_TypeLoader_TypeLoaderEnvironment__VerifyTypeLoaderLockHeld ");
                //ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_ManagedThreadId__get_Current ");
                //ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Monitor__Enter ");
                //ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Monitor__Exit ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Number__UInt32ToDecStrForKnownSmallNumber ");
                ldArgs.Append($"--wrap=_ZN6Thread10IsDetachedEv ");
                ldArgs.Append($"--wrap=_Z24PalGetMaximumStackBoundsPPvS0_ ");
                if (libc == "zisk")
                {
                    ldArgs.Append($"--wrap=System_Console_Interop_Sys__InitializeTerminalAndSignalHandling ");
                    ldArgs.Append($"--wrap=SystemNative_SetTerminalInvalidationHandler ");
                    ldArgs.Append($"--wrap=SystemNative_Write ");
                }
                ldArgs.Append($"--wrap=RhpThrowEx ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_RuntimeExceptionHelpers__FailFast ");
                /* Method replacements implemented in the rhp module: the ILC
                 * substitutions in zisk.substitutions.xml turn these managed
                 * bodies into throw stubs (removing their F/D instructions from
                 * the image) and the wraps divert every caller to the exact or
                 * always-valid C reimplementations. All HashHelpers copies must
                 * be wrapped: each embedding assembly compiles its own. */
                ldArgs.Append($"--wrap=System_Collections_Concurrent_System_Collections_HashHelpers__IsPrime ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Collections_HashHelpers__IsPrime ");
                ldArgs.Append($"--wrap=System_Collections_Immutable_System_Collections_HashHelpers__IsPrime ");
                ldArgs.Append($"--wrap=System_Collections_Immutable_System_Collections_Frozen_FrozenHashTable__CalcNumBuckets ");

                /* libm: divert the math surface referenced by the runtime
                 * (MathHelpers.cpp RhpDbl* helpers, GC allocation sampling)
                 * to the nofp trap stubs. Keeps hard-float musl members out
                 * of the link entirely: their F/D instructions would poison
                 * the rv64ima .text, and these paths must never execute on
                 * the zkVM anyway. */
                foreach (string mathSym in new[]
                {
                    "acos", "acosf", "acosh", "acoshf",
                    "asin", "asinf", "asinh", "asinhf",
                    "atan", "atanf", "atan2", "atan2f", "atanh", "atanhf",
                    "cbrt", "cbrtf", "ceil", "ceilf",
                    "cos", "cosf", "cosh", "coshf",
                    "exp", "expf", "floor", "floorf",
                    "fma", "fmaf", "fmod", "fmodf",
                    "log", "logf", "log10", "log10f", "log2", "log2f",
                    "modf", "modff", "pow", "powf",
                    "sin", "sinf", "sinh", "sinhf",
                    "sqrt", "sqrtf", "tan", "tanf", "tanh", "tanhf",
                    "scalbn", // musl fmt_fp dependency, see nofp module
                })
                {
                    ldArgs.Append($"--wrap={mathSym} ");
                }

                /* asprintf is referenced only by the PAL's CGroup CPU-limit
                 * parsing, whose initialization is already stubbed out above
                 * (--wrap=_Z16InitializeCGroupv). Diverting it to a stub that
                 * returns -1 (the documented asprintf failure mode) keeps that
                 * dead path failing gracefully and, more importantly, stops
                 * musl's vasprintf/fmt_fp/scalbn members - the last hard-float
                 * F/D code in the image - from being pulled into the link. */
                ldArgs.Append($"--wrap=asprintf ");

                /* gs_cookie */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "gs_cookie.o")}\" ");
                ldArgs.Append($"--wrap=__security_cookie ");

                /* rhp_native */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rhp_native.o")}\" ");
                ldArgs.Append($"--wrap=RhpAssignRefRiscV64 ");
                ldArgs.Append($"--wrap=RhpCheckedAssignRef ");
                ldArgs.Append($"--wrap=RhpByRefAssignRef ");
                ldArgs.Append($"--wrap=RhpAssignRef ");

                /* pal */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "pal.o")}\" ");
                ldArgs.Append($"--wrap=getenv ");
                ldArgs.Append($"--wrap=getcwd ");
                ldArgs.Append($"--wrap=getpid ");
                ldArgs.Append($"--wrap=getegid ");
                ldArgs.Append($"--wrap=geteuid ");
                ldArgs.Append($"--wrap=sched_getaffinity ");
                ldArgs.Append($"--wrap=sched_getcpu ");
                ldArgs.Append($"--wrap=open ");
                ldArgs.Append($"--wrap=__libc_malloc_impl ");
                ldArgs.Append($"--wrap=__libc_realloc ");
                ldArgs.Append($"--wrap=__libc_free ");
                ldArgs.Append($"--wrap=calloc ");
                ldArgs.Append($"--wrap=pthread_create ");
                ldArgs.Append($"--wrap=pthread_sigmask ");
                ldArgs.Append($"--wrap=__clock_gettime ");
                ldArgs.Append($"--wrap=clock_gettime ");
                ldArgs.Append($"--wrap=__malloc_allzerop ");
                ldArgs.Append($"--wrap=mmap ");
                ldArgs.Append($"--wrap=munmap ");
                ldArgs.Append($"--wrap=mlock ");
                ldArgs.Append($"--wrap=munlock ");
                ldArgs.Append($"--wrap=mlockall ");
                ldArgs.Append($"--wrap=munlockall ");
                ldArgs.Append($"--wrap=sched_yield ");
                ldArgs.Append($"--wrap=sigaction ");
                ldArgs.Append($"--wrap=signal ");
                ldArgs.Append($"--wrap=syscall ");
                ldArgs.Append($"--wrap=sysconf ");
                /* FP-free vfprintf (pal module): keeps musl's vfprintf.o - and
                 * its fmt_fp float formatter, the last F/D code in the image -
                 * out of the link. Every printf/fprintf/snprintf routes here. */
                ldArgs.Append($"--wrap=vfprintf ");
                /* musl exit()/_Exit()/abort() issue exit_group (syscall 94),
                 * which ZisK does not treat as program end. Redirect them to
                 * pal's __wrap_* which emit the real ZisK exit ecall (a7=93). */
                ldArgs.Append($"--wrap=exit ");
                ldArgs.Append($"--wrap=_Exit ");
                ldArgs.Append($"--wrap=abort ");
                if (libc == "zisk")
                {
                    /* Hide write() in Zisk */
                    ldArgs.Append($"--wrap=__stdio_write ");
                }

                /* tls */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "tls.o")}\" ");
                ldArgs.Append($"--wrap=__tls_get_addr ");
                ldArgs.Append($"--wrap=__init_tls ");
                ldArgs.Append($"--wrap=__init_tp ");
                ldArgs.Append($"--wrap=__copy_tls ");
                ldArgs.Append($"--no-whole-archive ");

                /* rng */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rng_stupid.o")}\" ");
                ldArgs.Append($"--wrap=minipal_get_cryptographically_secure_random_bytes ");
                ldArgs.Append($"--wrap=CryptoNative_EnsureOpenSslInitialized ");
                ldArgs.Append($"--wrap=CryptoNative_GetRandomBytes ");

                /* rust_sys */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rust_sys.o")}\" ");
                ldArgs.Append($"--wrap=sys_alloc_aligned ");

                /* ugc */
                ldArgs.Append($"--wrap=GC_Initialize ");
                ldArgs.Append($"--wrap=GC_VersionInfo ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGC.cpp.obj")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGCHandleManager.cpp.obj")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGCHandleStore.cpp.obj")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGCHeap.cpp.obj")}\" ");
            }
        }

        ldArgs.AppendJoin(' ', result.GetValueForOption(LdFlagsOption));

        bool printCommands = result.GetValueForOption(PrintCommandsOption);

        static int RunCommand(string command, string args, bool print)
        {
            if (print)
            {
                Console.WriteLine($"{command} {args}");
            }

            var p = Process.Start(command, args);
            p.WaitForExit();
            return p.ExitCode;
        }

        if (targetOS == TargetOS.Linux && result.GetValueForOption(WrapCheckOption))
        {
            string checkWrapPath = Path.Combine(homePath, "check_wrap_symbols.py");
            int checkExitCode = RunCommand(checkWrapPath, "-- " + ldArgs.ToString(), printCommands);
            if (checkExitCode != 0)
                return checkExitCode;
        }

        PerfWatch linkWatch = new PerfWatch("Link");
        int exitCode = RunCommand(ld, ldArgs.ToString(), printCommands);
        linkWatch.Complete();

        if (libc == "zisk" && exitCode == 0)
        {
            var patchElfArgs = " --fix-init-array --fix-tdata --remove-eh --trim-bss ";
            if (verbose)
                patchElfArgs += "--print-fn-boundaries ";

            int patchExitCode = RunCommand(patchElfPath,
                outputFilePath + " " + patchedFilePath +
                patchElfArgs,
                printCommands);
        }
        if (!result.GetValueForOption(CommonOptions.KeepObjectOption))
        {
            try { File.Delete(objectFilePath); } catch { }
        }

        if (exportsFile != null)
            try { File.Delete(exportsFile); } catch { }

        if (exitCode == 0 && result.GetValueForOption(SymChartOption))
        {
            RunSymbolChart(outputFilePath, homePath, verbose, logger);
        }

        if (exitCode == 0
            && targetOS is not TargetOS.Windows and not TargetOS.UEFI
            && result.GetValueForOption(SeparateSymbolsOption))
        {
            if (logger.IsVerbose)
                logger.LogMessage("Running objcopy");

            string objcopy = Environment.GetEnvironmentVariable("BFLAT_OBJCOPY");
            if (objcopy == null)
            {
                string toolSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
                objcopy = Path.Combine(homePath, "bin", "llvm-objcopy" + toolSuffix);
            }

            PerfWatch objCopyWatch = new PerfWatch("Objcopy");
            exitCode = RunCommand(objcopy, $"--only-keep-debug \"{outputFilePath}\" \"{outputFilePath}.dwo\"", printCommands);
            if (exitCode != 0) return exitCode;
            RunCommand(objcopy, $"--strip-debug --strip-unneeded \"{outputFilePath}\"", printCommands);
            if (exitCode != 0) return exitCode;
            RunCommand(objcopy, $"--add-gnu-debuglink=\"{outputFilePath}.dwo\" \"{outputFilePath}\"", printCommands);
            if (exitCode != 0) return exitCode;
            objCopyWatch.Complete();
        }

        return exitCode;
    }

    private static void RunSymbolChart(string binaryPath, string homePath, bool verbose, Logger logger)
    {
        // ── Locate readelf ────────────────────────────────────────────────
        string readelf = Environment.GetEnvironmentVariable("BFLAT_READELF");
        if (readelf == null)
        {
            string toolSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
            string candidate = Path.Combine(homePath, "bin", "llvm-readelf" + toolSuffix);
            readelf = File.Exists(candidate) ? candidate : "readelf";
        }

        if (verbose)
            logger.LogMessage($"Running readelf on {binaryPath}");

        string readelfOutput;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(readelf, $"-sW \"{binaryPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            readelfOutput = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                string err = proc.StandardError.ReadToEnd().Trim();
                Console.Error.WriteLine($"Warning: readelf exited with code {proc.ExitCode}: {err}");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not run readelf ({readelf}): {ex.Message}");
            return;
        }

        // ── Parse & generate ──────────────────────────────────────────────
        var symbols  = ElfSymbolParser.Parse(readelfOutput);
        string htmlPath = binaryPath + ".symbols.html";

        try
        {
            SymbolChartGenerator.Generate(htmlPath, binaryPath, symbols);
            Console.WriteLine($"Symbol chart: {htmlPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not write symbol chart: {ex.Message}");
        }
    }
}
