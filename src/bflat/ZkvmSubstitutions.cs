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
// Policy for the zkVM targets: WHICH CoreLib method bodies are
// replaced, by what, and on which runtime versions.
//
// Two mechanisms are driven from here:
//   * whole-body C# snippets (modules/zisk_subst/zisk.snippets.cs), compiled
//     at guest-build time against the guest's own reference set and mapped
//     onto their targets by BuildBodySubstitutions;
//   * the known-dead floating-point list, which tells the --error-on-float
//     gate that a conversion it found sits in a block the target never runs.
//
// A target that no longer resolves is a hard error, not a skipped entry: the
// FP-carrying original would otherwise stay in an FPU-less image and surface
// as a rejected opcode in the emulator. The IL machinery this file uses lives
// in ZkvmIL.cs.

class ZkvmSubstitutions
{

    // Compiles the module-declared snippet sources against the same references
    // with accessibility checks disabled, emits them to a temp module, and
    // returns the path (null on failure - substitution is then simply skipped).
    internal static string CompileSnippets(IReadOnlyList<string> sourcePaths, string[] references, string[] defines, string langVersion)
    {
        try
        {

            if (!LanguageVersionFacts.TryParse(langVersion, out LanguageVersion langVer))
                langVer = LanguageVersion.Latest;

            var parseOptions = new CSharpParseOptions(langVer, DocumentationMode.None,
                preprocessorSymbols: defines ?? Array.Empty<string>());
            var trees = new List<SyntaxTree>();
            foreach (string sourcePath in sourcePaths)
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath), parseOptions, path: sourcePath));

            var metadataReferences = new List<MetadataReference>();
            foreach (var reference in references)
            {
                var mref = MetadataReference.CreateFromFile(reference);
                // Several CoreLib-internal types (HashHelpers, MethodTable,
                // EETypeElementType) are ALSO defined in sibling assemblies
                // (System.Collections.Concurrent/Immutable, System.Private.
                // TypeLoader), so an unqualified reference is ambiguous (CS0433).
                // Expose CoreLib under a `corelib` extern alias (in addition to
                // global) so the snippet can name those types unambiguously.
                if (Path.GetFileNameWithoutExtension(reference).Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))
                    mref = mref.WithAliases(new[] { "global", "corelib" });
                metadataReferences.Add(mref);
            }

            var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Release,
                deterministic: true,
                metadataImportOptions: MetadataImportOptions.All)
                // A snippet method that takes a private nested type as a parameter
                // trips the accessibility-consistency errors (CS0050/CS0051); we
                // are deliberately bypassing accessibility, so suppress them.
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                {
                    ["CS0050"] = ReportDiagnostic.Suppress,
                    ["CS0051"] = ReportDiagnostic.Suppress,
                    ["CS0052"] = ReportDiagnostic.Suppress,
                    ["CS0053"] = ReportDiagnostic.Suppress,
                    ["CS0057"] = ReportDiagnostic.Suppress,
                });

            // Enable BinderFlags.IgnoreAccessibility (internal Roslyn API) so the
            // snippet may reference private nested types and private members of
            // the referenced CoreLib.
            var binderFlagsType = typeof(CSharpCompilation).Assembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
            var withBinderFlags = typeof(CSharpCompilationOptions).GetMethod("WithTopLevelBinderFlags",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (binderFlagsType != null && withBinderFlags != null)
            {
                object ignoreAccessibility = Enum.Parse(binderFlagsType, "IgnoreAccessibility");
                options = (CSharpCompilationOptions)withBinderFlags.Invoke(options, new[] { ignoreAccessibility });
            }

            var compilation = CSharpCompilation.Create("__ZiskSnippets", trees, metadataReferences, options);
            string path = Path.GetTempFileName();
            using (var fs = File.Create(path))
            {
                var emitResult = compilation.Emit(fs);
                if (!emitResult.Success)
                {
                    foreach (var d in emitResult.Diagnostics)
                        if (d.Severity == DiagnosticSeverity.Error)
                            Console.Error.WriteLine("zkVM snippet: " + d);
                    return null;
                }
            }
            return path;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not compile zkVM snippets: {ex.Message}");
            return null;
        }
    }

    // Maps CoreLib methods that carry floating point to the C# snippet methods
    // (in __ZiskSnippets.Snippets) that replace their bodies, by name/signature.
    // Targets absent from the closure (e.g. LengthBuckets when Immutable is not
    // referenced) resolve to null and are simply skipped. Extend as snippets grow.
    internal static Dictionary<MethodDesc, MethodDesc> BuildBodySubstitutions(CompilerTypeSystemContext ctx, EcmaModule snippetsModule)
    {
        var map = new Dictionary<MethodDesc, MethodDesc>();
        var snippetType = (MetadataType)snippetsModule.GetType("__ZiskSnippets", "Snippets");
        var int32 = ctx.GetWellKnownType(WellKnownType.Int32);
        var single = ctx.GetWellKnownType(WellKnownType.Single);

        // Every mismatch found is collected and reported together: a CoreLib
        // bump that moves three methods should show all three, not the first.
        var drift = new List<string>();

        MethodDesc Snippet(string name)
        {
            foreach (MethodDesc m in snippetType.GetMethods())
                if (m.Name.StringEquals(name)) return m;
            drift.Add($"snippet '{name}' is missing from zisk.snippets.cs");
            return null;
        }

        // A substitution whose target no longer resolves is a SILENT hole: the
        // FP-carrying original body stays in the image and only surfaces much
        // later (--error-on-float, or the emulator rejecting an F/D opcode).
        // That already happened once - Number.FormatFloat's signature changed
        // in .NET 11, the entry stopped matching, and floating point came back
        // (see the note in zisk.nofp.substitutions.xml). So a missing target is
        // fatal, and the few genuinely conditional ones must say so.
        void Add(MethodDesc target, MethodDesc snippet, string what)
        {
            if (target == null)
            {
                drift.Add($"{what}: target not found in this CoreLib");
                return;
            }
            if (snippet == null)
                return; // Snippet() already recorded the reason
            map[target] = snippet;
        }

        // Finds the single method of `type` named `name` whose parameter types
        // (excluding `this`) match `sig` exactly; null if the type/method is absent.
        MethodDesc Method(MetadataType type, string name, params TypeDesc[] sig)
        {
            if (type == null) return null;
            foreach (MethodDesc m in type.GetMethods())
            {
                if (!m.Name.StringEquals(name) || m.Signature.Length != sig.Length) continue;
                bool ok = true;
                for (int i = 0; i < sig.Length; i++) ok &= m.Signature[i] == sig[i];
                if (ok) return m;
            }
            return null;
        }
        MetadataType Type(ModuleDesc mod, string ns, string name)
        {
            try { return mod == null ? null : (MetadataType)mod.GetType(ns, name, NotFoundBehavior.ReturnNull); }
            catch { return null; }
        }
        ModuleDesc Module(string simpleName)
        {
            try { return ctx.GetModuleForSimpleName(simpleName); }
            catch { return null; }
        }

        // System.Random legacy compat PRNG (private nested impl types).
        var random = (MetadataType)ctx.SystemModule.GetType("System", "Random");
        var seedImpl = random.GetNestedType("CompatSeedImpl");
        var derivedImpl = random.GetNestedType("CompatDerivedImpl");
        Add(Method(seedImpl, "Next", int32), Snippet("RandomSeedNext1"),
            "Random.CompatSeedImpl.Next(int)");
        Add(Method(seedImpl, "Next", int32, int32), Snippet("RandomSeedNext2"),
            "Random.CompatSeedImpl.Next(int,int)");
        Add(Method(derivedImpl, "Next", int32), Snippet("RandomDerivedNext1"),
            "Random.CompatDerivedImpl.Next(int)");
        Add(Method(derivedImpl, "Next", int32, int32), Snippet("RandomDerivedNext2"),
            "Random.CompatDerivedImpl.Next(int,int)");

        // System.Collections.Hashtable ctor/rehash family (float _loadFactor).
        var htType = Type(ctx.SystemModule, "System.Collections", "Hashtable");
        var ieqCmp = Type(ctx.SystemModule, "System.Collections", "IEqualityComparer");
        if (htType == null)
        {
            drift.Add("System.Collections.Hashtable: type not found in CoreLib");
        }
        else
        {
            Add(Method(htType, ".ctor"), Snippet("HashtableCtor0"),
                "Hashtable..ctor()");
            Add(Method(htType, ".ctor", int32), Snippet("HashtableCtorCap"),
                "Hashtable..ctor(int)");
            Add(Method(htType, ".ctor", int32, single), Snippet("HashtableCtorCapLf"),
                "Hashtable..ctor(int,float)");
            Add(Method(htType, ".ctor", int32, single, ieqCmp), Snippet("HashtableCtorCapLfCmp"),
                "Hashtable..ctor(int,float,IEqualityComparer)");
            Add(Method(htType, ".ctor", ieqCmp), Snippet("HashtableCtorCmp"),
                "Hashtable..ctor(IEqualityComparer)");
            Add(Method(htType, ".ctor", int32, ieqCmp), Snippet("HashtableCtorCapCmp"),
                "Hashtable..ctor(int,IEqualityComparer)");
            Add(Method(htType, "rehash", int32), Snippet("HashtableRehash"),
                "Hashtable.rehash(int)");
        }

        // System.ValueType.GetHashCode helper (Single/Double struct fields).
        // RegularGetValueTypeHashCode has ref/byref params not conveniently
        // nameable as TypeDesc[] here; match by name + parameter count (3) instead.
        var vtType = Type(ctx.SystemModule, "System", "ValueType");
        MethodDesc vtHash = null;
        if (vtType != null)
        {
            foreach (MethodDesc m in vtType.GetMethods())
                if (m.Name.StringEquals("RegularGetValueTypeHashCode") && m.Signature.Length == 3)
                    vtHash = m;
        }
        Add(vtHash, Snippet("ValueTypeRegularHashCode"),
            "ValueType.RegularGetValueTypeHashCode(3 params)");

        // System.Double / System.Single: the object overloads of Equals and
        // CompareTo. They cannot be body="remove"d (see the note in
        // zisk.nofp.substitutions.xml - they are what ValueType.Equals and the
        // generic comparers reach for), and the originals compare in FP
        // registers, so they are the one FP source that survives into an
        // ordinary guest. The snippets redo the same IEEE comparison on the raw
        // bit patterns. The by-value overloads are deliberately NOT listed: see
        // the comment above the snippets.
        var objectType = ctx.GetWellKnownType(WellKnownType.Object);
        var doubleType = Type(ctx.SystemModule, "System", "Double");
        var singleType = Type(ctx.SystemModule, "System", "Single");
        Add(Method(doubleType, "Equals", objectType), Snippet("DoubleEqualsObject"),
            "Double.Equals(object)");
        Add(Method(doubleType, "CompareTo", objectType), Snippet("DoubleCompareToObject"),
            "Double.CompareTo(object)");
        Add(Method(singleType, "Equals", objectType), Snippet("SingleEqualsObject"),
            "Single.Equals(object)");
        Add(Method(singleType, "CompareTo", objectType), Snippet("SingleCompareToObject"),
            "Single.CompareTo(object)");

        // System.Collections.Frozen.LengthBuckets. The whole assembly is absent
        // unless the guest references Immutable, so the TYPE may legitimately
        // not resolve - but once it does, the method must.
        var lbType = Type(Module("System.Collections.Immutable"), "System.Collections.Frozen", "LengthBuckets");
        if (lbType != null)
        {
            MethodDesc lbMethod = null;
            foreach (MethodDesc m in lbType.GetMethods())
                if (m.Name.StringEquals("CreateLengthBucketsArrayIfAppropriate"))
                    lbMethod = m;
            Add(lbMethod, Snippet("LengthBucketsNone"),
                "LengthBuckets.CreateLengthBucketsArrayIfAppropriate");
        }

        // System.TimeZoneInfo..cctor: the donor rebuilds the whole cctor with the
        // s_daylightRuleMarker DateTime constructed tick-exactly in integers (the
        // stock body's DateTime.MinValue.AddMilliseconds(2) is its only FP).
        // Drift guard: substitute only while the original cctor still stores
        // exactly the five fields the donor initializes - a CoreLib that adds,
        // renames or removes an initializer is refused loudly here, leaving the
        // original (FP-carrying) body for --error-on-float / the emulator to
        // reject, instead of silently dropping the new initializer.
        var tzType = Type(ctx.SystemModule, "System", "TimeZoneInfo");
        MethodDesc tzCctor = tzType?.GetStaticConstructor();
        if (tzCctor == null)
        {
            drift.Add("TimeZoneInfo..cctor: not found in this CoreLib");
        }
        else
        {
            // One donor per known CoreLib field set. .NET 11 initializes five
            // statics; .NET 10 also stores the whole-day range bounds
            // (s_maxDateOnly / s_minDateOnly) used by GetUtcOffsetFromUtc.
            // Matching is on the exact SET, so a runtime that adds, renames or
            // drops an initializer picks neither and is reported.
            string[] common =
            {
                "s_utcTimeZone", "s_cachedData", "<Invariant>k__BackingField",
                "s_daylightRuleMarker", "s_ZonesThatUseLocationName",
            };
            string[] withDateBounds =
                common.Concat(new[] { "s_maxDateOnly", "s_minDateOnly" }).ToArray();
            var stored = new HashSet<string>();
            var ed = new ILEditor(EcmaMethodIL.Create((EcmaMethod)tzCctor));
            for (int i = 0; i < ed.Count; i++)
                if (ed.OpcodeOf(i) == ILOpcode.stsfld &&
                    ed.Resolve(ed.TokenAt(ed.OffsetOf(i))) is FieldDesc fd)
                    stored.Add(fd.Name.ToString());

            if (stored.SetEquals(common))
            {
                Add(tzCctor, Snippet("TimeZoneInfoCctor"), "TimeZoneInfo..cctor");
            }
            else if (stored.SetEquals(withDateBounds))
            {
                Add(tzCctor, Snippet("TimeZoneInfoCctorWithDateBounds"),
                    "TimeZoneInfo..cctor (with date bounds)");
            }
            else
            {
                // An unknown field set means a runtime this donor pair has not
                // been reviewed against. Not fatal: CustomILProvider's targeted
                // rewrite replaces just the AddMilliseconds(2) subexpression and
                // does not care about the surrounding initializers, so the FP
                // still goes - and --error-on-float on the guest build is what
                // proves it, rather than a guess here. But it does mean a donor
                // needs writing, so say so loudly.
                Console.Error.WriteLine(
                    "warning: TimeZoneInfo..cctor stores an unknown field set [" +
                    string.Join(", ", stored) + "]; expected either [" +
                    string.Join(", ", common) + "] or [" +
                    string.Join(", ", withDateBounds) + "]. No donor applied - " +
                    "falling back to the targeted IL rewrite. Add a donor variant " +
                    "for this runtime in zisk.snippets.cs.");
            }
        }

        if (drift.Count != 0)
        {
            // Hard failure by design. Each of these substitutions exists to keep
            // floating point out of an FPU-less image; a skipped one leaves the
            // original body in place, and the failure then shows up as an F/D
            // opcode the emulator rejects - far from here, in a guest that looked
            // like it built fine. Refusing to produce that binary is the point.
            throw new InvalidOperationException(
                "zkVM body substitutions no longer match this CoreLib:" +
                Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", drift) + Environment.NewLine +
                "Each entry means the FP-carrying original body would have been " +
                "kept. Update zisk.snippets.cs / ZkvmSubstitutions.BuildBodySubstitutions for " +
                "this runtime version.");
        }

        return map;
    }
    // Methods whose IL genuinely contains an FP conversion, but only inside a
    // block the JIT proves unreachable on this target (a disabled-feature guard
    // reached through a local, which IL-level substitution cannot constant-fold),
    // so no FP instruction is ever emitted. Confirmed against the FP-free emulator.
    // Matched by owning type + name (signature-independent) and reported as a
    // warning by the --error-on-float gate rather than failing the build.
    private static readonly (string typeNamespace, string typeName, string method)[] s_knownDeadFloatMethods =
    {
        // Lock.TryEnterSlow: `double durationNs = (Stopwatch.GetTimestamp()-start)
        // * 1e9 / Stopwatch.Frequency` behind `if (areContentionEventsEnabled)`,
        // where areContentionEventsEnabled = NativeRuntimeEventSource.Log.IsEnabled
        // (...) - always false because EventPipe/EventSource is disabled here.
        ("System.Threading", "Lock", "TryEnterSlow"),
    };

    internal static bool IsKnownDeadFloatMethod(MethodDesc method)
    {
        MethodDesc typical = method.GetTypicalMethodDefinition();
        if (typical.OwningType is not MetadataType mt)
            return false;
        foreach (var (ns, name, m) in s_knownDeadFloatMethods)
            if (typical.Name.StringEquals(m) && mt.Name.StringEquals(name) && mt.Namespace.StringEquals(ns))
                return true;
        return false;
    }
}
