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
using System.Text;

// bflat-modules check <modules-dir>
//     Reads every module manifest and checks the files that must exist in
//     the source tree. Needs no toolchain; CI runs it.
//
// bflat-modules plan <modules-dir> <out-file>
//     Writes the module build as shell commands for build.sh to source. The
//     commands call build.sh functions (module_begin, module_cc, ...), so the
//     toolchain and its flags stay in build.sh and the module data stays in
//     the manifests.
//
// bflat-modules pack <modules-dir> <layout-modules-dir>
//     Copies each manifest and the files it names (objects, ILC inputs,
//     linker script, entry) to <layout-modules-dir>/<name>/. Fails if a file
//     is missing. bflat.csproj runs it while it makes the compiler layout.
//
// bflat-modules link <layout-modules-dir> <platform-manifest> [<package-manifest>...]
//                    [--remove-eh] [-O0] [-v] [--compiler <capability>]...
//     Shows what the manifests give a guest build on a platform: the module
//     part of the link command (with <placeholders> for the parts that bflat
//     adds itself), the ISA, the RyuJIT options, the ILC inputs and the
//     post-link steps. It uses the same code as bflat, so it answers "what
//     does this platform build, and in which order" without a guest build.
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["check", string src] => Check(src),
                ["plan", string src, string outFile] => Plan(src, outFile),
                ["pack", string src, string outDir] => Pack(src, outDir),
                ["link", string dir, string platform, .. string[] rest] => Link(dir, platform, rest),
                _ => Usage(),
            };
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine($"bflat-modules: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: bflat-modules check <modules-dir>");
        Console.Error.WriteLine("       bflat-modules plan <modules-dir> <out-file>");
        Console.Error.WriteLine("       bflat-modules pack <modules-dir> <layout-modules-dir>");
        Console.Error.WriteLine("       bflat-modules link <layout-modules-dir> <platform-manifest> [<package-manifest>...]");
        Console.Error.WriteLine("                          [--remove-eh] [-O0] [-v] [--compiler <capability>]...");
        return 2;
    }

    // The module build compiles for this target only. A source condition
    // may test arch and os (see ReadSource); this is what it sees.
    private static readonly ManifestTarget BuildTarget = new ManifestTarget { Arch = "riscv64", Os = "linux" };

    private static int Check(string src)
    {
        Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(src);
        var errors = new List<string>();

        void MustExist(BflatManifest m, string rel, string what)
        {
            if (!File.Exists(m.Resolve(rel)))
                errors.Add($"{m.Path}: {what} '{rel}' not found");
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (BflatManifest m in modules.Values)
        {
            foreach (ManifestSource s in m.Sources)
                MustExist(m, s.File, "source");
            foreach (ManifestItem i in m.IlcSubstitutions.Concat(m.IlcSources))
                MustExist(m, i.Value, "ILC file");
            foreach (ManifestTest t in m.Tests)
                MustExist(m, t.File, "test");
            if (m.Platform == null)
                continue;

            MustExist(m, m.Platform.LinkerScript, "linker script");
            foreach (string name in m.Platform.Requires)
            {
                if (!modules.TryGetValue(name, out BflatManifest required))
                    errors.Add($"{m.Path}: required module '{name}' does not exist");
                else if (required.Platform != null)
                    errors.Add($"{m.Path}: required module '{name}' is a platform");
            }
            if (m.Platform.LibcAlias != null && !aliases.TryAdd(m.Platform.LibcAlias, m.Path))
                errors.Add($"{m.Path}: --libc alias '{m.Platform.LibcAlias}' is also used by {aliases[m.Platform.LibcAlias]}");
        }

        foreach (string e in errors)
            Console.Error.WriteLine(e);
        if (errors.Count > 0)
            return 1;
        Console.WriteLine($"{modules.Count} module manifests: OK");
        return 0;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Args(IEnumerable<string> items) => string.Concat(items.Select(i => " " + Quote(i)));

    private static int Plan(string src, string outFile)
    {
        Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(src);
        var sb = new StringBuilder();
        sb.Append("# Generated by bflat-modules plan from the module manifests. Do not edit.\n");
        sb.Append("# build.sh sources this file; the module_* functions are defined there.\n");
        foreach (BflatManifest m in modules.Values)
        {
            sb.Append('\n');
            sb.Append($"module_begin {Quote(m.Name)} {Quote(m.Dir)}\n");
            if (m.External is ManifestExternal x)
            {
                if (x.Release != null)
                    sb.Append($"module_fetch_release {Quote(x.Repo)} {Quote(x.Tag)} {Quote(x.Release)} {Quote(x.Dir)}\n");
                else
                    sb.Append($"module_fetch_build {Quote(x.Repo)} {Quote(x.Tag)} {Quote(x.Dir)}{Args(x.Build)}\n");
            }
            foreach (ManifestSource s in m.Sources)
            {
                if (!ManifestCondition.Applies(s.When, BuildTarget))
                    continue;
                switch (Path.GetExtension(s.File))
                {
                    case ".c":
                        sb.Append($"module_cc {Quote(s.File)} {Quote(s.Output)}{Args(s.CFlags)}\n");
                        break;
                    case ".cc":
                    case ".cpp":
                        sb.Append($"module_cxx {Quote(s.File)} {Quote(s.Output)}{Args(s.CFlags.Concat(s.CxxFlags))}\n");
                        break;
                    default:
                        sb.Append($"module_as {Quote(s.File)} {Quote(s.Output)}{Args(s.AsFlags)}\n");
                        break;
                }
            }
            sb.Append("module_end\n");
        }
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }

    private static int Pack(string src, string outDir)
    {
        Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(src);
        string srcRoot = Path.GetFullPath(src);

        // Every file each module ships, relative to its own directory. A
        // manifest may name a file of another module ("../other/module.o")
        // only if that module ships it too.
        var shipped = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var crossRefs = new List<(BflatManifest From, string File, string Module, string Rel)>();
        var errors = new List<string>();

        foreach (BflatManifest m in modules.Values)
        {
            var files = new SortedSet<string>(StringComparer.Ordinal) { BflatManifest.FileName };
            IEnumerable<string> named = m.EffectiveObjects().Select(o => o.File)
                .Concat(m.IlcSubstitutions.Concat(m.IlcSources).Concat(m.IlcReferences).Select(i => i.Value));
            if (m.Platform != null)
                named = named.Append(m.Platform.LinkerScript).Append(m.Platform.Entry);

            foreach (string rel in named)
            {
                string full = m.Resolve(rel);
                string relToRoot = Path.GetRelativePath(srcRoot, full).Replace(Path.DirectorySeparatorChar, '/');
                int slash = relToRoot.IndexOf('/', StringComparison.Ordinal);
                if (relToRoot.StartsWith("../", StringComparison.Ordinal) || slash < 0)
                {
                    errors.Add($"{m.Path}: '{rel}' is outside the module directories");
                    continue;
                }
                string owner = relToRoot[..slash];
                string ownerRel = relToRoot[(slash + 1)..];
                if (owner == m.Name)
                {
                    if (File.Exists(full))
                        files.Add(ownerRel);
                    else
                        errors.Add($"{m.Path}: '{rel}' not found (was the module built?)");
                }
                else
                {
                    crossRefs.Add((m, rel, owner, ownerRel));
                }
            }
            shipped[m.Name] = files;
        }

        foreach ((BflatManifest from, string file, string module, string rel) in crossRefs)
        {
            if (!shipped.TryGetValue(module, out SortedSet<string> files) || !files.Contains(rel))
                errors.Add($"{from.Path}: '{file}' is not a file that module '{module}' ships");
        }

        foreach (string e in errors)
            Console.Error.WriteLine(e);
        if (errors.Count > 0)
            return 1;

        // The layout copy is owned by this command: start clean, so a file a
        // manifest no longer names does not linger.
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
        foreach ((string name, SortedSet<string> files) in shipped)
        {
            foreach (string rel in files)
            {
                string dest = Path.Combine(outDir, name, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(Path.Combine(srcRoot, name, rel), dest);
            }
        }
        Console.WriteLine($"Packed {shipped.Count} modules into {outDir}");
        return 0;
    }

    private static int Link(string dir, string platformPath, string[] rest)
    {
        var packages = new List<BflatManifest>();
        var compiler = new HashSet<string>(StringComparer.Ordinal);
        bool removeEh = false, optimize = true, verbose = false;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--remove-eh": removeEh = true; break;
                case "-O0": optimize = false; break;
                case "-v": verbose = true; break;
                case "--compiler" when i + 1 < rest.Length: compiler.Add(rest[++i]); break;
                default: packages.Add(BflatManifest.Load(rest[i])); break;
            }
        }

        Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(dir);
        BflatManifest platform = BflatManifest.Load(platformPath);
        if (platform.Platform == null)
            throw new ManifestException($"{platformPath}: not a platform manifest");
        var build = new ManifestTarget
        {
            Eh = removeEh ? "failfast" : "unwind",
            Stdlib = "dotnet",
            Optimize = optimize,
            Verbose = verbose,
            Compiler = compiler,
        };
        ManifestLink link = ManifestLink.Create(platform, modules, ManifestLink.TargetFor(platform, build), packages);

        // The same sequence as the platform link in BuildCommand.
        var ld = new StringBuilder();
        ld.Append("<crt1.o> <crti.o> <guest.o> <runtime> ");
        link.AppendObjects(ld, ObjectGroup.PreLibc);
        ld.Append("<libc.a> <libgcc.a> <crtn.o> <--extra-ld> ");
        link.AppendObjects(ld, ObjectGroup.PreEntry);
        link.AppendEntry(ld);
        link.AppendObjects(ld, ObjectGroup.Early);
        ld.Append("--whole-archive ");
        link.AppendObjects(ld, ObjectGroup.WholeArchive);
        ld.Append("--no-whole-archive ");
        link.AppendObjects(ld, ObjectGroup.Normal);
        link.AppendRemainingOptions(ld);
        ld.Append("<--ldflags>");

        Console.WriteLine($"platform {platform.Name}: {string.Join(", ", link.Manifests.Select(m => m.Name))}");
        Console.WriteLine($"abi: {link.Abi ?? "-"}");
        Console.WriteLine($"isa: {link.Isa(_ => true)}");
        Console.WriteLine($"codegen options: {string.Join(" ", link.CodegenOptions)}");
        Console.WriteLine($"defines: {string.Join(" ", link.Values(m => m.IlcDefines))}");
        Console.WriteLine($"direct P/Invoke: {string.Join(" ", link.Values(m => m.IlcDirectPInvoke))}");
        Console.WriteLine($"features: {string.Join(" ", link.Features.Select(f => $"{f.Key}={f.Value}"))}");
        Console.WriteLine($"ILC substitutions: {string.Join(" ", link.Files(m => m.IlcSubstitutions))}");
        foreach ((string tool, List<string> args) in link.PostlinkSteps())
            Console.WriteLine($"postlink: {tool} {string.Join(" ", args)}");
        Console.WriteLine($"link: {ld}");
        return 0;
    }
}
