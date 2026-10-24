// bflat C# compiler
// Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
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
using System.IO;
using System.Linq;
using System.Text.Json;

// The bflat manifest, schema 2 (docs/manifest.md). One parser for every
// reader: bflat itself (modules from the layout, platforms, --extlib
// packages) and the bflat-modules tool (module build plan, layout packing).
// It deliberately has no ILCompiler dependency so that the tool can share
// this file.
//
// The parser is strict: an unknown key, a wrong type or a value outside its
// set is an error that names the file and the JSON path. The schema file
// (bflat-manifest.schema.json) states the same rules for editors and CI.

/// <summary>What a condition is evaluated against.</summary>
internal sealed class ManifestTarget
{
    public string Arch { get; init; }
    public string Os { get; init; }
    /// <summary>The base libc: for a platform, its <c>base.libc</c>.</summary>
    public string Libc { get; init; }
    /// <summary>The selected platform's name, or null for a stock target.</summary>
    public string Platform { get; init; }
    public IReadOnlySet<string> Flags { get; init; } = new HashSet<string>();
    public string Eh { get; init; }
    public string Stdlib { get; init; }
    public bool Optimize { get; init; }
}

/// <summary>
/// A <c>when</c> object. All keys must match; a list value matches any of
/// its items.
/// </summary>
internal sealed class ManifestCondition
{
    private readonly Dictionary<string, string[]> _values = new Dictionary<string, string[]>(StringComparer.Ordinal);
    private bool? _optimize;

    public IEnumerable<string> Keys => _optimize.HasValue ? _values.Keys.Append("optimize") : _values.Keys;

    internal static readonly Dictionary<string, string[]> AllowedValues = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["arch"] = new[] { "x64", "x86", "arm64", "riscv64" },
        ["os"] = new[] { "linux", "windows", "uefi" },
        ["libc"] = new[] { "glibc", "musl", "bionic", "shcrt", "none" },
        ["platform"] = null,
        ["flag"] = null,
        ["eh"] = new[] { "unwind", "failfast" },
        ["stdlib"] = new[] { "dotnet", "zero", "none" },
    };

    internal static ManifestCondition Read(ManifestReader r, JsonElement e, string path, bool allowOptimize)
    {
        r.ExpectKind(e, JsonValueKind.Object, path);
        var c = new ManifestCondition();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            string ppath = path + "." + p.Name;
            if (p.Name == "optimize")
            {
                if (!allowOptimize)
                    throw r.Error(ppath, "'optimize' is valid only in codegen.options");
                r.ExpectKind(p.Value, JsonValueKind.True, JsonValueKind.False, ppath);
                c._optimize = p.Value.GetBoolean();
                continue;
            }
            if (!AllowedValues.TryGetValue(p.Name, out string[] allowed))
                throw r.Error(ppath, $"unknown condition key '{p.Name}'");

            string[] values;
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                values = new[] { p.Value.GetString() };
            }
            else if (p.Value.ValueKind == JsonValueKind.Array && p.Name != "eh")
            {
                values = p.Value.EnumerateArray().Select((v, i) => r.String(v, $"{ppath}[{i}]")).ToArray();
                if (values.Length == 0)
                    throw r.Error(ppath, "an empty list never matches");
            }
            else
            {
                throw r.Error(ppath, p.Name == "eh" ? "expected a string" : "expected a string or a list of strings");
            }

            if (allowed != null)
            {
                foreach (string v in values)
                {
                    if (Array.IndexOf(allowed, v) < 0)
                        throw r.Error(ppath, $"'{v}' is not one of: {string.Join(", ", allowed)}");
                }
            }
            c._values[p.Name] = values;
        }
        if (c._values.Count == 0 && !c._optimize.HasValue)
            throw r.Error(path, "an empty condition; leave 'when' out instead");
        return c;
    }

    public bool Holds(ManifestTarget t)
    {
        foreach (KeyValuePair<string, string[]> kv in _values)
        {
            bool match = kv.Key switch
            {
                "arch" => kv.Value.Contains(t.Arch),
                "os" => kv.Value.Contains(t.Os),
                "libc" => kv.Value.Contains(t.Libc),
                "platform" => t.Platform != null && kv.Value.Contains(t.Platform),
                "flag" => kv.Value.Any(t.Flags.Contains),
                "eh" => kv.Value.Contains(t.Eh),
                "stdlib" => kv.Value.Contains(t.Stdlib),
                _ => throw new InvalidOperationException(kv.Key),
            };
            if (!match)
                return false;
        }
        return !_optimize.HasValue || _optimize.Value == t.Optimize;
    }

    /// <summary>Holds when <paramref name="c"/> is absent or holds.</summary>
    public static bool Applies(ManifestCondition c, ManifestTarget t) => c == null || c.Holds(t);
}

internal enum ObjectGroup
{
    PreLibc,
    PreEntry,
    Early,
    WholeArchive,
    Normal,
}

internal sealed class ManifestSource
{
    public string File { get; init; }
    public string[] CFlags { get; init; } = Array.Empty<string>();
    public string[] CxxFlags { get; init; } = Array.Empty<string>();
    public string[] AsFlags { get; init; } = Array.Empty<string>();
    public string Output { get; init; }
    public ManifestCondition When { get; init; }
}

internal sealed class ManifestObject
{
    public string File { get; init; }
    public ObjectGroup Group { get; init; } = ObjectGroup.Normal;
    /// <summary>True when the manifest gives <c>group</c> or <c>order</c>.</summary>
    public bool Placed { get; init; }
    public int Order { get; init; } = 100;
    public ManifestCondition When { get; init; }
}

internal sealed class ManifestWrap
{
    /// <summary>The readable name, for messages.</summary>
    public string Symbol { get; init; }
    /// <summary>The linker symbol: <c>mangled</c> when given, else <c>symbol</c>.</summary>
    public string LinkerSymbol { get; init; }
    public ManifestCondition When { get; init; }
}

/// <summary>A file or a value, with its own condition.</summary>
internal sealed class ManifestItem
{
    public string Value { get; init; }
    public string Assembly { get; init; }
    public ManifestCondition When { get; init; }
}

internal sealed class ManifestExternal
{
    public string Repo { get; init; }
    public string Tag { get; init; }
    public string Release { get; init; }
    public string[] Build { get; init; }
    public string Dir { get; init; }
}

internal sealed class ManifestPlatform
{
    public string LibcAlias { get; init; }
    public string BaseOs { get; init; }
    public string BaseArch { get; init; }
    public string BaseLibc { get; init; }
    public string[] Requires { get; init; } = Array.Empty<string>();
    public string[] Flags { get; init; } = Array.Empty<string>();
    public string LinkerScript { get; init; }
    public string Entry { get; init; }
}

internal sealed class ManifestTest
{
    public string File { get; init; }
    public string Runner { get; init; }
    public string[] CFlags { get; init; } = Array.Empty<string>();
    public Dictionary<string, string[]> Matrix { get; init; } = new Dictionary<string, string[]>(StringComparer.Ordinal);
    public ManifestCondition When { get; init; }
}

internal sealed class BflatManifest
{
    public const string FileName = "bflat-manifest.json";

    /// <summary>The manifest file; relative paths in it start at its directory.</summary>
    public string Path { get; init; }
    public string Dir => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
    public int Schema { get; init; }
    public string Name { get; init; }
    public string Version { get; init; }
    public string Description { get; init; }
    public ManifestCondition When { get; init; }

    public List<ManifestSource> Sources { get; } = new List<ManifestSource>();
    /// <summary>Null when the manifest has no <c>objects</c> key: see <see cref="EffectiveObjects"/>.</summary>
    public List<ManifestObject> Objects { get; set; }
    public List<ManifestWrap> Wraps { get; } = new List<ManifestWrap>();
    public List<ManifestItem> LdOptions { get; } = new List<ManifestItem>();

    public List<ManifestItem> IlcSubstitutions { get; } = new List<ManifestItem>();
    public List<ManifestItem> IlcSnippets { get; } = new List<ManifestItem>();
    public List<ManifestItem> IlcSources { get; } = new List<ManifestItem>();
    public List<ManifestItem> IlcReferences { get; } = new List<ManifestItem>();
    public List<ManifestItem> IlcDefines { get; } = new List<ManifestItem>();
    public List<ManifestItem> IlcDirectPInvoke { get; } = new List<ManifestItem>();
    public Dictionary<string, string> IlcFeatures { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Sections parsed and validated, but not acted on yet (see <see cref="UnimplementedSections"/>).</summary>
    public bool HasCodegen { get; set; }
    public bool HasPostlink { get; set; }
    public bool HasChecks { get; set; }

    public List<ManifestTest> Tests { get; } = new List<ManifestTest>();
    public ManifestExternal External { get; set; }
    public ManifestPlatform Platform { get; set; }

    /// <summary>
    /// The objects to link: <c>objects</c> when the manifest has it, else
    /// every output of <c>sources</c> except the platform entry.
    /// </summary>
    public IEnumerable<ManifestObject> EffectiveObjects()
    {
        if (Objects != null)
            return Objects;
        return Sources
            .Select(s => s.Output)
            .Where(o => Platform == null || o != Platform.Entry)
            .Select(o => new ManifestObject { File = o });
    }

    /// <summary>
    /// The schema-2 sections that a manifest can declare but this bflat does
    /// not act on yet. A manifest that uses one is rejected at guest build
    /// time instead of being half applied. A <paramref name="package"/>
    /// (from <c>--extlib</c>) can give objects, wraps and references only.
    /// </summary>
    public IEnumerable<string> UnimplementedSections(bool package)
    {
        if (package)
        {
            if (Platform != null) yield return "platform";
            if (LdOptions.Count > 0) yield return "link.ld";
            if (IlcSubstitutions.Count > 0) yield return "ilc.substitutions";
            if (IlcSnippets.Count > 0) yield return "ilc.snippets";
            if (EffectiveObjects().Any(o => o.Placed)) yield return "objects.group and objects.order";
        }
        else if (IlcReferences.Count > 0)
        {
            yield return "ilc.references";
        }
        if (HasCodegen) yield return "codegen";
        if (HasPostlink) yield return "postlink";
        if (HasChecks) yield return "checks";
        if (IlcSources.Count > 0) yield return "ilc.sources";
        if (IlcDefines.Count > 0) yield return "ilc.defines";
        if (IlcDirectPInvoke.Count > 0) yield return "ilc.direct_pinvoke";
        if (IlcFeatures.Count > 0) yield return "ilc.features";
    }

    public string Resolve(string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public static BflatManifest Load(string path)
    {
        using JsonDocument doc = ManifestReader.Parse(path);
        JsonElement root = doc.RootElement;
        var r = new ManifestReader(path);
        r.ExpectKind(root, JsonValueKind.Object, "$");
        if (!root.TryGetProperty("schema", out _))
            throw r.Error("$", "no 'schema' key: a schema 1 manifest is valid only as an --extlib package");
        return ReadSchema2(r, root, path);
    }

    private static readonly string[] TopKeys =
    {
        "$schema", "schema", "name", "version", "description", "note", "metadata", "when",
        "sources", "objects", "link", "ilc", "codegen", "postlink", "checks", "tests", "external", "platform",
    };

    internal static BflatManifest ReadSchema2(ManifestReader r, JsonElement root, string path)
    {
        r.CheckKeys(root, "$", TopKeys);
        JsonElement schema = root.GetProperty("schema");
        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int schemaVersion) || schemaVersion != 2)
            throw r.Error("$.schema", "this bflat reads schema 2 only");

        var m = new BflatManifest
        {
            Path = path,
            Schema = 2,
            Name = r.Name(r.Required(root, "name", "$"), "$.name"),
            Version = r.OptString(root, "version", "$"),
            Description = r.OptString(root, "description", "$"),
            When = root.TryGetProperty("when", out JsonElement when) ? ManifestCondition.Read(r, when, "$.when", false) : null,
        };
        r.OptString(root, "note", "$");
        if (root.TryGetProperty("metadata", out JsonElement metadata))
        {
            r.ExpectKind(metadata, JsonValueKind.Object, "$.metadata");
            foreach (JsonProperty p in metadata.EnumerateObject())
                r.String(p.Value, "$.metadata." + p.Name);
        }

        if (root.TryGetProperty("sources", out JsonElement sources))
            r.ForEach(sources, "$.sources", (e, p) => m.Sources.Add(ReadSource(r, e, p)));
        if (root.TryGetProperty("objects", out JsonElement objects))
        {
            m.Objects = new List<ManifestObject>();
            r.ForEach(objects, "$.objects", (e, p) => m.Objects.Add(ReadObject(r, e, p)));
        }
        if (root.TryGetProperty("link", out JsonElement link))
            ReadLink(r, link, m);
        if (root.TryGetProperty("ilc", out JsonElement ilc))
            ReadIlc(r, ilc, m);
        if (root.TryGetProperty("codegen", out JsonElement codegen))
        {
            ReadCodegen(r, codegen);
            m.HasCodegen = true;
        }
        if (root.TryGetProperty("postlink", out JsonElement postlink))
        {
            r.ForEach(postlink, "$.postlink", (e, p) => ReadPostlinkStep(r, e, p));
            m.HasPostlink = true;
        }
        if (root.TryGetProperty("checks", out JsonElement checks))
        {
            r.CheckKeys(checks, "$.checks", "float", "compressed", "atomic", "il_float");
            foreach (JsonProperty p in checks.EnumerateObject())
                r.ExpectKind(p.Value, JsonValueKind.True, JsonValueKind.False, "$.checks." + p.Name);
            m.HasChecks = true;
        }
        if (root.TryGetProperty("tests", out JsonElement tests))
            r.ForEach(tests, "$.tests", (e, p) => m.Tests.Add(ReadTest(r, e, p)));
        if (root.TryGetProperty("external", out JsonElement external))
            m.External = ReadExternal(r, external, "$.external");
        if (root.TryGetProperty("platform", out JsonElement platform))
            m.Platform = ReadPlatform(r, platform, "$.platform");

        // Two sources that compile to one object would overwrite each other.
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestSource s in m.Sources)
        {
            if (!outputs.Add(s.Output))
                throw r.Error("$.sources", $"two sources compile to '{s.Output}'; give one an 'output'");
        }
        return m;
    }

    private static readonly string[] SourceExtensions = { ".c", ".cc", ".cpp", ".S", ".s" };

    private static ManifestSource ReadSource(ManifestReader r, JsonElement e, string path)
    {
        r.CheckKeys(e, path, "file", "cflags", "cxxflags", "asflags", "output", "when", "note");
        string file = r.RelPath(r.Required(e, "file", path), path + ".file");
        if (!SourceExtensions.Contains(System.IO.Path.GetExtension(file), StringComparer.Ordinal))
            throw r.Error(path + ".file", $"'{file}' is not a .c, .cc, .cpp, .S or .s file");
        ManifestCondition when = e.TryGetProperty("when", out JsonElement w) ? ManifestCondition.Read(r, w, path + ".when", false) : null;
        if (when != null && when.Keys.Any(k => k != "arch" && k != "os"))
            throw r.Error(path + ".when", "a source condition may test only 'arch' and 'os': the module build knows no guest");
        return new ManifestSource
        {
            File = file,
            CFlags = r.OptStrings(e, "cflags", path),
            CxxFlags = r.OptStrings(e, "cxxflags", path),
            AsFlags = r.OptStrings(e, "asflags", path),
            Output = e.TryGetProperty("output", out JsonElement o)
                ? r.RelPath(o, path + ".output")
                : System.IO.Path.ChangeExtension(file, ".o"),
            When = when,
        };
    }

    private static ManifestObject ReadObject(ManifestReader r, JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.String)
            return new ManifestObject { File = r.RelPath(e, path) };
        r.CheckKeys(e, path, "file", "group", "order", "when", "note");
        var group = ObjectGroup.Normal;
        if (e.TryGetProperty("group", out JsonElement g))
        {
            group = r.String(g, path + ".group") switch
            {
                "pre-libc" => ObjectGroup.PreLibc,
                "pre-entry" => ObjectGroup.PreEntry,
                "early" => ObjectGroup.Early,
                "whole-archive" => ObjectGroup.WholeArchive,
                "normal" => ObjectGroup.Normal,
                string s => throw r.Error(path + ".group", $"'{s}' is not one of: pre-libc, pre-entry, early, whole-archive, normal"),
            };
        }
        int order = 100;
        if (e.TryGetProperty("order", out JsonElement o))
        {
            if (o.ValueKind != JsonValueKind.Number || !o.TryGetInt32(out order) || order < 0 || order > 999)
                throw r.Error(path + ".order", "expected an integer from 0 to 999");
        }
        return new ManifestObject
        {
            File = r.RelPath(r.Required(e, "file", path), path + ".file"),
            Group = group,
            Order = order,
            Placed = e.TryGetProperty("group", out _) || e.TryGetProperty("order", out _),
            When = r.OptWhen(e, path),
        };
    }

    private static void ReadLink(ManifestReader r, JsonElement link, BflatManifest m)
    {
        r.CheckKeys(link, "$.link", "wrap", "ld");
        if (link.TryGetProperty("wrap", out JsonElement wrap))
        {
            r.ForEach(wrap, "$.link.wrap", (e, p) =>
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    string s = r.Symbol(e, p);
                    m.Wraps.Add(new ManifestWrap { Symbol = s, LinkerSymbol = s });
                    return;
                }
                r.CheckKeys(e, p, "symbol", "mangled", "when", "note");
                string symbol = r.Symbol(r.Required(e, "symbol", p), p + ".symbol");
                string mangled = e.TryGetProperty("mangled", out JsonElement mg) ? r.Symbol(mg, p + ".mangled") : symbol;
                m.Wraps.Add(new ManifestWrap { Symbol = symbol, LinkerSymbol = mangled, When = r.OptWhen(e, p) });
            });
        }
        if (link.TryGetProperty("ld", out JsonElement ld))
            r.ForEach(ld, "$.link.ld", (e, p) => m.LdOptions.Add(r.ValueItem(e, p)));
    }

    private static void ReadIlc(ManifestReader r, JsonElement ilc, BflatManifest m)
    {
        r.CheckKeys(ilc, "$.ilc", "substitutions", "snippets", "sources", "references", "defines", "features", "direct_pinvoke");
        if (ilc.TryGetProperty("substitutions", out JsonElement subst))
            r.ForEach(subst, "$.ilc.substitutions", (e, p) => m.IlcSubstitutions.Add(r.FileItem(e, p)));
        if (ilc.TryGetProperty("snippets", out JsonElement snippets))
            r.ForEach(snippets, "$.ilc.snippets", (e, p) => m.IlcSnippets.Add(r.FileItem(e, p)));
        if (ilc.TryGetProperty("sources", out JsonElement sources))
            r.ForEach(sources, "$.ilc.sources", (e, p) => m.IlcSources.Add(r.FileItem(e, p)));
        if (ilc.TryGetProperty("references", out JsonElement refs))
        {
            r.ForEach(refs, "$.ilc.references", (e, p) =>
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    m.IlcReferences.Add(new ManifestItem { Value = r.RelPath(e, p) });
                    return;
                }
                r.CheckKeys(e, p, "file", "assembly", "when", "note");
                m.IlcReferences.Add(new ManifestItem
                {
                    Value = r.RelPath(r.Required(e, "file", p), p + ".file"),
                    Assembly = r.OptString(e, "assembly", p),
                    When = r.OptWhen(e, p),
                });
            });
        }
        if (ilc.TryGetProperty("defines", out JsonElement defines))
            r.ForEach(defines, "$.ilc.defines", (e, p) => m.IlcDefines.Add(r.ValueItem(e, p)));
        if (ilc.TryGetProperty("direct_pinvoke", out JsonElement dp))
            r.ForEach(dp, "$.ilc.direct_pinvoke", (e, p) => m.IlcDirectPInvoke.Add(r.ValueItem(e, p)));
        if (ilc.TryGetProperty("features", out JsonElement features))
        {
            r.ExpectKind(features, JsonValueKind.Object, "$.ilc.features");
            foreach (JsonProperty p in features.EnumerateObject())
            {
                m.IlcFeatures[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    _ => throw r.Error("$.ilc.features." + p.Name, "expected a boolean, a string or a number"),
                };
            }
        }
    }

    private static void ReadCodegen(ManifestReader r, JsonElement codegen)
    {
        r.CheckKeys(codegen, "$.codegen", "isa", "abi", "options");
        if (codegen.TryGetProperty("isa", out JsonElement isa))
        {
            r.CheckKeys(isa, "$.codegen.isa", "enable", "disable");
            r.OptStrings(isa, "enable", "$.codegen.isa");
            r.OptStrings(isa, "disable", "$.codegen.isa");
        }
        if (codegen.TryGetProperty("abi", out JsonElement abi))
        {
            string a = r.String(abi, "$.codegen.abi");
            if (a != "lp64" && a != "lp64d")
                throw r.Error("$.codegen.abi", $"'{a}' is not one of: lp64, lp64d");
        }
        if (codegen.TryGetProperty("options", out JsonElement options))
        {
            r.ForEach(options, "$.codegen.options", (e, p) =>
            {
                JsonElement v = e;
                string vpath = p;
                if (e.ValueKind == JsonValueKind.Object)
                {
                    r.CheckKeys(e, p, "value", "when", "note");
                    v = r.Required(e, "value", p);
                    vpath = p + ".value";
                    if (e.TryGetProperty("when", out JsonElement w))
                        ManifestCondition.Read(r, w, p + ".when", true);
                }
                string s = r.String(v, vpath);
                int eq = s.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                    throw r.Error(vpath, $"'{s}' is not Name=value");
                if (s.AsSpan(eq + 1).StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    throw r.Error(vpath, $"'{s}': RyuJIT reads hex without the 0x prefix");
            });
        }
    }

    private static void ReadPostlinkStep(ManifestReader r, JsonElement e, string path)
    {
        r.CheckKeys(e, path, "tool", "args", "when", "note");
        string tool = r.String(r.Required(e, "tool", path), path + ".tool");
        if (tool != "patch_elf")
            throw r.Error(path + ".tool", $"'{tool}' is not a tool built into bflat (patch_elf)");
        r.OptStrings(e, "args", path);
        r.OptWhen(e, path);
    }

    private static ManifestTest ReadTest(ManifestReader r, JsonElement e, string path)
    {
        r.CheckKeys(e, path, "file", "runner", "cflags", "matrix", "when", "note");
        string runner = r.String(r.Required(e, "runner", path), path + ".runner");
        if (runner != "qemu" && runner != "host")
            throw r.Error(path + ".runner", $"'{runner}' is not one of: qemu, host");
        var test = new ManifestTest
        {
            File = r.RelPath(r.Required(e, "file", path), path + ".file"),
            Runner = runner,
            CFlags = r.OptStrings(e, "cflags", path),
            When = r.OptWhen(e, path),
        };
        if (e.TryGetProperty("matrix", out JsonElement matrix))
        {
            r.ExpectKind(matrix, JsonValueKind.Object, path + ".matrix");
            foreach (JsonProperty p in matrix.EnumerateObject())
            {
                string[] values = r.Strings(p.Value, path + ".matrix." + p.Name);
                if (values.Length == 0)
                    throw r.Error(path + ".matrix." + p.Name, "expected at least one value");
                test.Matrix[p.Name] = values;
            }
        }
        return test;
    }

    private static ManifestExternal ReadExternal(ManifestReader r, JsonElement e, string path)
    {
        r.CheckKeys(e, path, "repo", "tag", "release", "build", "dir", "note");
        string repo = r.String(r.Required(e, "repo", path), path + ".repo");
        if (!repo.StartsWith("https://", StringComparison.Ordinal))
            throw r.Error(path + ".repo", "the repository URL must start with https://");
        bool hasRelease = e.TryGetProperty("release", out JsonElement release);
        bool hasBuild = e.TryGetProperty("build", out JsonElement build);
        if (hasRelease == hasBuild)
            throw r.Error(path, "give exactly one of 'release' and 'build'");
        return new ManifestExternal
        {
            Repo = repo,
            Tag = r.String(r.Required(e, "tag", path), path + ".tag"),
            Release = hasRelease ? r.String(release, path + ".release") : null,
            Build = hasBuild ? r.Strings(build, path + ".build") : null,
            Dir = r.RelPath(r.Required(e, "dir", path), path + ".dir"),
        };
    }

    private static ManifestPlatform ReadPlatform(ManifestReader r, JsonElement e, string path)
    {
        r.CheckKeys(e, path, "aliases", "base", "requires", "flags", "linker_script", "entry", "note");
        string alias = null;
        if (e.TryGetProperty("aliases", out JsonElement aliases))
        {
            r.CheckKeys(aliases, path + ".aliases", "libc");
            alias = r.OptString(aliases, "libc", path + ".aliases");
        }
        JsonElement b = r.Required(e, "base", path);
        r.CheckKeys(b, path + ".base", "os", "arch", "libc");
        string Base(string key)
        {
            string v = r.String(r.Required(b, key, path + ".base"), $"{path}.base.{key}");
            if (Array.IndexOf(ManifestCondition.AllowedValues[key], v) < 0)
                throw r.Error($"{path}.base.{key}", $"'{v}' is not one of: {string.Join(", ", ManifestCondition.AllowedValues[key])}");
            return v;
        }
        string[] requires = r.OptStrings(e, "requires", path);
        for (int i = 0; i < requires.Length; i++)
            r.CheckName(requires[i], $"{path}.requires[{i}]");
        string[] flags = r.OptStrings(e, "flags", path);
        return new ManifestPlatform
        {
            LibcAlias = alias,
            BaseOs = Base("os"),
            BaseArch = Base("arch"),
            BaseLibc = Base("libc"),
            Requires = requires,
            Flags = flags,
            LinkerScript = r.RelPath(r.Required(e, "linker_script", path), path + ".linker_script"),
            Entry = r.RelPath(r.Required(e, "entry", path), path + ".entry"),
        };
    }

    /// <summary>
    /// Reads a schema 1 package manifest (no <c>schema</c> key) and returns
    /// the first <c>builds[]</c> entry that matches, as a schema-2 manifest.
    /// Matching is the same as before schema 2: <c>arch</c> and <c>os</c>
    /// must be equal; <c>libc</c> must be equal when both sides give one.
    /// Unknown keys are allowed, because package builds inject some
    /// (<c>sp1_ref</c>, <c>zisk_ref_hash</c>).
    /// </summary>
    internal static BflatManifest ReadSchema1(string path, JsonElement root, string arch, string os, string libc)
    {
        var m = new BflatManifest
        {
            Path = path,
            Schema = 1,
            Name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : System.IO.Path.GetFileName(path),
            Version = root.TryGetProperty("package_version", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
            Objects = new List<ManifestObject>(),
        };
        if (!root.TryGetProperty("builds", out JsonElement builds) || builds.ValueKind != JsonValueKind.Array)
            return null;

        static string Get(JsonElement e, string key) =>
            e.TryGetProperty(key, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

        foreach (JsonElement build in builds.EnumerateArray())
        {
            if (Get(build, "arch") != arch || Get(build, "os") != os)
                continue;
            string buildLibc = Get(build, "libc");
            if (!string.IsNullOrEmpty(libc) && buildLibc != null && buildLibc != libc)
                continue;

            string staticLib = Get(build, "static_lib");
            string dotnetLib = Get(build, "dotnet_lib");
            if (staticLib != null)
                m.Objects.Add(new ManifestObject { File = staticLib, Group = ObjectGroup.PreEntry });
            if (dotnetLib != null)
                m.IlcReferences.Add(new ManifestItem { Value = dotnetLib, Assembly = Get(build, "dotnet_assemblyname") });
            if (build.TryGetProperty("wrap_symbols", out JsonElement ws) && ws.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement sym in ws.EnumerateArray())
                {
                    string s = sym.ValueKind == JsonValueKind.String ? sym.GetString() : null;
                    if (!string.IsNullOrEmpty(s))
                        m.Wraps.Add(new ManifestWrap { Symbol = s, LinkerSymbol = s });
                }
            }
            return staticLib == null && dotnetLib == null ? null : m;
        }
        return null;
    }
}

/// <summary>JSON access with strict checks and error messages that carry the file and the JSON path.</summary>
internal sealed class ManifestReader
{
    private readonly string _file;

    public ManifestReader(string file) => _file = file;

    public static JsonDocument Parse(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            throw new ManifestException($"{path}: not valid JSON: {ex.Message}");
        }
    }

    public ManifestException Error(string jsonPath, string message) => new ManifestException($"{_file}: {jsonPath}: {message}");

    public void ExpectKind(JsonElement e, JsonValueKind kind, string path)
    {
        if (e.ValueKind != kind)
            throw Error(path, $"expected {Describe(kind)}, found {Describe(e.ValueKind)}");
    }

    public void ExpectKind(JsonElement e, JsonValueKind a, JsonValueKind b, string path)
    {
        if (e.ValueKind != a && e.ValueKind != b)
            throw Error(path, $"expected {(a == JsonValueKind.True ? "a boolean" : Describe(a))}, found {Describe(e.ValueKind)}");
    }

    private static string Describe(JsonValueKind k) => k switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => k.ToString(),
    };

    public void CheckKeys(JsonElement e, string path, params string[] allowed)
    {
        ExpectKind(e, JsonValueKind.Object, path);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (Array.IndexOf(allowed, p.Name) < 0)
                throw Error(path, $"unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)})");
        }
        if (e.TryGetProperty("note", out JsonElement note))
            String(note, path + ".note");
    }

    public JsonElement Required(JsonElement e, string key, string path)
    {
        if (!e.TryGetProperty(key, out JsonElement v))
            throw Error(path, $"missing required key '{key}'");
        return v;
    }

    public string String(JsonElement e, string path)
    {
        ExpectKind(e, JsonValueKind.String, path);
        string s = e.GetString();
        if (s.Length == 0)
            throw Error(path, "an empty string");
        return s;
    }

    public string OptString(JsonElement e, string key, string path) =>
        e.TryGetProperty(key, out JsonElement v) ? String(v, path + "." + key) : null;

    public string[] Strings(JsonElement e, string path)
    {
        ExpectKind(e, JsonValueKind.Array, path);
        return e.EnumerateArray().Select((v, i) => String(v, $"{path}[{i}]")).ToArray();
    }

    public string[] OptStrings(JsonElement e, string key, string path) =>
        e.TryGetProperty(key, out JsonElement v) ? Strings(v, path + "." + key) : Array.Empty<string>();

    public ManifestCondition OptWhen(JsonElement e, string path) =>
        e.TryGetProperty("when", out JsonElement w) ? ManifestCondition.Read(this, w, path + ".when", false) : null;

    public void ForEach(JsonElement list, string path, Action<JsonElement, string> read)
    {
        ExpectKind(list, JsonValueKind.Array, path);
        int i = 0;
        foreach (JsonElement e in list.EnumerateArray())
            read(e, $"{path}[{i++}]");
    }

    public string RelPath(JsonElement e, string path)
    {
        string s = String(e, path);
        if (System.IO.Path.IsPathRooted(s) || s.Contains('\\', StringComparison.Ordinal))
            throw Error(path, $"'{s}' must be a relative path with '/' separators");
        return s;
    }

    public string Name(JsonElement e, string path)
    {
        string s = String(e, path);
        CheckName(s, path);
        return s;
    }

    public void CheckName(string s, string path)
    {
        bool ok = s.Length > 0 && (char.IsAsciiLetterLower(s[0]) || char.IsAsciiDigit(s[0]))
            && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-');
        if (!ok)
            throw Error(path, $"'{s}' is not a valid name (lowercase letters, digits, '_' and '-')");
    }

    public string Symbol(JsonElement e, string path)
    {
        string s = String(e, path);
        bool ok = (char.IsAsciiLetter(s[0]) || s[0] == '_' || s[0] == '.' || s[0] == '$')
            && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '$');
        if (!ok)
            throw Error(path, $"'{s}' is not a linker symbol");
        return s;
    }

    public ManifestItem FileItem(JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.String)
            return new ManifestItem { Value = RelPath(e, path) };
        CheckKeys(e, path, "file", "when", "note");
        return new ManifestItem { Value = RelPath(Required(e, "file", path), path + ".file"), When = OptWhen(e, path) };
    }

    public ManifestItem ValueItem(JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.String)
            return new ManifestItem { Value = String(e, path) };
        CheckKeys(e, path, "value", "when", "note");
        return new ManifestItem { Value = String(Required(e, "value", path), path + ".value"), When = OptWhen(e, path) };
    }
}

internal sealed class ManifestException : Exception
{
    public ManifestException() { }
    public ManifestException(string message) : base(message) { }
    public ManifestException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The manifests that take part in one guest link, merged. The load order
/// is the platform, then its <c>requires</c> in list order, then the
/// <c>--extlib</c> packages in command-line order. Within a link group,
/// objects sort by <c>order</c>, then by load order, then by their position
/// in the manifest.
/// </summary>
internal sealed class ManifestLink
{
    public BflatManifest Platform { get; private set; }
    public ManifestTarget Target { get; private set; }
    public List<BflatManifest> Manifests { get; } = new List<BflatManifest>();

    /// <summary>Loads every module manifest in <c>lib/modules</c>, keyed by name.</summary>
    public static Dictionary<string, BflatManifest> LoadModules(string modulesDir)
    {
        var modules = new Dictionary<string, BflatManifest>(StringComparer.Ordinal);
        if (!Directory.Exists(modulesDir))
            return modules;
        foreach (string dir in Directory.GetDirectories(modulesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            string path = Path.Combine(dir, BflatManifest.FileName);
            if (!File.Exists(path))
                continue;
            BflatManifest m = BflatManifest.Load(path);
            if (m.Name != Path.GetFileName(dir))
                throw new ManifestException($"{path}: name '{m.Name}' differs from its directory name");
            modules.Add(m.Name, m);
        }
        return modules;
    }

    /// <summary>The platform whose <c>aliases.libc</c> is <paramref name="libc"/>, or null.</summary>
    public static BflatManifest FindPlatform(Dictionary<string, BflatManifest> modules, string libc)
    {
        BflatManifest[] found = modules.Values.Where(m => m.Platform?.LibcAlias == libc).ToArray();
        if (found.Length > 1)
            throw new ManifestException($"--libc {libc}: more than one platform has this alias: {string.Join(", ", found.Select(m => m.Path))}");
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>What the conditions of a platform link see.</summary>
    public static ManifestTarget TargetFor(BflatManifest platform, string eh, string stdlib, bool optimize) => new ManifestTarget
    {
        Arch = platform.Platform.BaseArch,
        Os = platform.Platform.BaseOs,
        Libc = platform.Platform.BaseLibc,
        Platform = platform.Name,
        Flags = new HashSet<string>(platform.Platform.Flags, StringComparer.Ordinal),
        Eh = eh,
        Stdlib = stdlib,
        Optimize = optimize,
    };

    /// <summary>
    /// The platform and the modules it requires. <c>--extlib</c> packages
    /// are not part of it yet: BuildCommand links them where it always did,
    /// in front of the linker script.
    /// </summary>
    public static ManifestLink Create(BflatManifest platform, Dictionary<string, BflatManifest> modules, ManifestTarget target)
    {
        var link = new ManifestLink { Platform = platform, Target = target };
        link.Add(platform);
        foreach (string name in platform.Platform.Requires)
        {
            if (!modules.TryGetValue(name, out BflatManifest module))
                throw new ManifestException($"{platform.Path}: required module '{name}' is not in the layout");
            if (module.Platform != null)
                throw new ManifestException($"{platform.Path}: required module '{name}' is a platform; one link has one platform");
            link.Add(module);
        }

        foreach (BflatManifest m in link.Manifests)
        {
            string unimplemented = string.Join(", ", m.UnimplementedSections(package: false));
            if (unimplemented.Length > 0)
                throw new ManifestException($"{m.Path}: this bflat does not implement yet: {unimplemented}");
        }
        return link;
    }

    private void Add(BflatManifest m)
    {
        if (Manifests.Any(x => x.Name == m.Name))
            throw new ManifestException($"{m.Path}: a manifest named '{m.Name}' is already part of this link");
        if (ManifestCondition.Applies(m.When, Target))
            Manifests.Add(m);
    }

    public string LinkerScript => Platform.Resolve(Platform.Platform.LinkerScript);
    public string Entry => Platform.Resolve(Platform.Platform.Entry);

    /// <summary>The objects of one link group, in link order, as absolute paths, with the manifest that declares each.</summary>
    public List<(string Path, BflatManifest Owner)> Objects(ObjectGroup group)
    {
        var items = new List<(int Order, int Load, int Pos, string Path, BflatManifest Owner)>();
        for (int load = 0; load < Manifests.Count; load++)
        {
            BflatManifest m = Manifests[load];
            int pos = 0;
            foreach (ManifestObject o in m.EffectiveObjects())
            {
                if (o.Group == group && ManifestCondition.Applies(o.When, Target))
                    items.Add((o.Order, load, pos, m.Resolve(o.File), m));
                pos++;
            }
        }
        return items
            .OrderBy(i => i.Order).ThenBy(i => i.Load).ThenBy(i => i.Pos)
            .Select(i => (i.Path, i.Owner))
            .ToList();
    }

    /// <summary>The <c>--wrap</c> linker symbols of one manifest that apply.</summary>
    public IEnumerable<string> Wraps(BflatManifest m) =>
        m.Wraps.Where(w => ManifestCondition.Applies(w.When, Target)).Select(w => w.LinkerSymbol);

    /// <summary>The raw linker options of one manifest, with <c>{dir}</c> and <c>{libpath}</c> replaced.</summary>
    public IEnumerable<string> LdOptions(BflatManifest m, string libPath) =>
        m.LdOptions.Where(o => ManifestCondition.Applies(o.When, Target))
            .Select(o => o.Value.Replace("{dir}", m.Dir, StringComparison.Ordinal).Replace("{libpath}", libPath, StringComparison.Ordinal));

    // The link command is written in pieces (BuildCommand puts libc.a, crt
    // and the --extlib packages between them). The wraps and ld options of a
    // manifest go directly after its first object, and only once; this keeps
    // the command in the order that it had when the module list was code.
    private readonly HashSet<BflatManifest> _optionsWritten = new HashSet<BflatManifest>();
    private readonly HashSet<string> _wrapsWritten = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The value of <c>{libpath}</c> in <c>link.ld</c>.</summary>
    public string LibPath { get; set; } = "";

    /// <summary>Writes the objects of one group, each followed by its manifest's options.</summary>
    public void AppendObjects(System.Text.StringBuilder ld, ObjectGroup group)
    {
        foreach ((string path, BflatManifest owner) in Objects(group))
        {
            ld.Append($"\"{path}\" ");
            AppendOptions(ld, owner);
        }
    }

    /// <summary>Writes the linker script and the entry object, followed by the platform's options.</summary>
    public void AppendEntry(System.Text.StringBuilder ld)
    {
        ld.Append($"-T\"{LinkerScript}\" ");
        ld.Append($"\"{Entry}\" ");
        AppendOptions(ld, Platform);
    }

    /// <summary>Writes the options of the manifests that had no object to follow.</summary>
    public void AppendRemainingOptions(System.Text.StringBuilder ld)
    {
        foreach (BflatManifest m in Manifests)
            AppendOptions(ld, m);
    }

    private void AppendOptions(System.Text.StringBuilder ld, BflatManifest m)
    {
        if (!_optionsWritten.Add(m))
            return;
        foreach (string symbol in Wraps(m))
        {
            if (_wrapsWritten.Add(symbol))
                ld.Append($"--wrap={symbol} ");
        }
        foreach (string option in LdOptions(m, LibPath))
            ld.Append(option + " ");
    }

    public List<string> Files(Func<BflatManifest, List<ManifestItem>> section)
    {
        var files = new List<string>();
        foreach (BflatManifest m in Manifests)
        {
            foreach (ManifestItem item in section(m))
            {
                if (!ManifestCondition.Applies(item.When, Target))
                    continue;
                string path = m.Resolve(item.Value);
                if (!File.Exists(path))
                    throw new ManifestException($"{m.Path}: '{item.Value}' not found");
                files.Add(path);
            }
        }
        return files;
    }
}
