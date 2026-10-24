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
//   * whole-body C# snippets (modules/guest_subst/snippets.cs), compiled
//     at guest-build time against the guest's own reference set and mapped
//     onto their targets by BuildBodySubstitutions;
//   * the known-dead floating-point list, which tells the --error-on-float
//     gate that a conversion it found sits in a block the target never runs.
//
// A target that no longer resolves is a hard error, not a skipped entry: the
// original would otherwise stay in the image and stop the guest on a nofp trap
// at run time. The IL machinery this file uses lives in ZkvmIL.cs.

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

            var compilation = CSharpCompilation.Create("__BflatSnippets", trees, metadataReferences, options);
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

    // Maps CoreLib methods to the C# snippet methods (in __BflatSnippets.Snippets)
    // that replace their bodies, by name/signature. Targets absent from the
    // closure (e.g. the Immutable copy of HashHelpers when Immutable is not
    // referenced) are skipped. Every snippet must have an entry here.
    internal static Dictionary<MethodDesc, MethodDesc> BuildBodySubstitutions(CompilerTypeSystemContext ctx, EcmaModule snippetsModule)
    {
        var map = new Dictionary<MethodDesc, MethodDesc>();
        var snippetType = (MetadataType)snippetsModule.GetType("__BflatSnippets", "Snippets");
        var int32 = ctx.GetWellKnownType(WellKnownType.Int32);

        // Every mismatch found is collected and reported together: a CoreLib
        // bump that moves three methods should show all three, not the first.
        var drift = new List<string>();

        MethodDesc Snippet(string name)
        {
            foreach (MethodDesc m in snippetType.GetMethods())
                if (m.Name.StringEquals(name)) return m;
            drift.Add($"snippet '{name}' is missing from the snippet sources");
            return null;
        }

        // A substitution whose target no longer resolves is a SILENT hole: the
        // original body stays in the image and only surfaces much later, when
        // the guest reaches the libm call and stops on the nofp trap. A similar
        // hole already happened once - Number.FormatFloat's signature changed
        // in .NET 11 and its XML entry stopped matching (see the note in
        // substitutions.xml). So a missing target is fatal, and the few
        // genuinely conditional ones must say so.
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

        // System.Collections.HashHelpers.IsPrime (one copy per assembly that
        // compiles the shared source in). A whole body, not a constant stub plus
        // --wrap to C: ILC inlines a constant stub into its callers, which
        // leaves no call to divert.
        // CoreLib always has its copy; the other types resolve only when the
        // guest references their assembly, but once they do the method must.
        var hashHelpersIsPrime = Snippet("HashHelpersIsPrime");
        Add(Method(Type(ctx.SystemModule, "System.Collections", "HashHelpers"), "IsPrime", int32),
            hashHelpersIsPrime, "System.Private.CoreLib HashHelpers.IsPrime(int)");
        foreach (string assembly in new[] { "System.Collections.Concurrent", "System.Collections.Immutable" })
        {
            var hashHelpers = Type(Module(assembly), "System.Collections", "HashHelpers");
            if (hashHelpers != null)
                Add(Method(hashHelpers, "IsPrime", int32), hashHelpersIsPrime,
                    $"{assembly} HashHelpers.IsPrime(int)");
        }

        // System.TimeZoneInfo..cctor: the donor rebuilds the whole cctor with the
        // s_daylightRuleMarker DateTime constructed tick-exactly in integers (the
        // stock body's DateTime.MinValue.AddMilliseconds(2) reaches libm modf).
        // Drift guard: substitute only while the original cctor still stores
        // exactly the five fields the donor initializes - a CoreLib that adds,
        // renames or removes an initializer is refused loudly here instead of
        // silently dropping the new initializer.
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
                // been reviewed against. The original cctor would stop every
                // guest that touches TimeZoneInfo on the modf trap, so this is
                // drift like any other.
                drift.Add(
                    "TimeZoneInfo..cctor stores an unknown field set [" +
                    string.Join(", ", stored) + "]; expected either [" +
                    string.Join(", ", common) + "] or [" +
                    string.Join(", ", withDateBounds) + "]. Add a donor variant " +
                    "for this runtime in snippets.cs");
            }
        }

        // A snippet that nothing maps to is dead weight at best and, at worst,
        // a substitution its author believes is applied.
        var mapped = new HashSet<MethodDesc>(map.Values);
        foreach (MethodDesc m in snippetType.GetMethods())
            if (m is EcmaMethod { IsPublic: true } && !mapped.Contains(m) && !IsDonorVariant(m.Name.ToString()))
                drift.Add($"snippet '{m.Name}' has no target in ZkvmSubstitutions.BuildBodySubstitutions");

        if (drift.Count != 0)
        {
            // Hard failure by design. Each of these substitutions keeps a libm
            // call that nofp traps out of the image; a skipped one leaves the
            // original body in place, and the failure then shows up as a guest
            // that stops with status 255 - far from here, in a guest that looked
            // like it built fine. Refusing to produce that binary is the point.
            throw new InvalidOperationException(
                "zkVM body substitutions no longer match this CoreLib:" +
                Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", drift) + Environment.NewLine +
                "Each entry means the original body would have been kept. Update " +
                "snippets.cs / ZkvmSubstitutions.BuildBodySubstitutions for this " +
                "runtime version.");
        }

        return map;

        // The TimeZoneInfo..cctor donors: exactly one of them applies to a
        // given CoreLib, so the other one is unmapped by design.
        static bool IsDonorVariant(string name) =>
            name == "TimeZoneInfoCctor" || name == "TimeZoneInfoCctorWithDateBounds";
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
