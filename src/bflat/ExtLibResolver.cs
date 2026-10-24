// bflat C# compiler
// Copyright (C) 2021-2022 Demerzel Solutions Limited (Nethermind)
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
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Internal.TypeSystem;

// Resolves --extlib specs into their manifests, and the manifests into
// concrete file paths.
//
// Supported spec formats:
//   1. https://github.com/owner/repo:tag   – GitHub release containing a single .nupkg
//   2. path or URL ending in .nupkg        – extract the package and read every *.bflat.manifest inside
//   3. path or URL ending in .bflat.manifest – use the manifest directly; for a
//      local path, every other *.bflat.manifest in its directory too, so a
//      package directory (an extracted nupkg, a package build's output/) works
//      through any one of its manifests
//
// A package can hold several manifests: for example the platform of a zkVM
// and the platform of its simulator. BuildCommand selects among them.
//
// In every case, file paths in the manifest are resolved relative to the
// MANIFEST FILE'S OWN DIRECTORY (not the nupkg root). A package ships the
// manifest next to the library it points at, so bare sibling names work both
// when the package is extracted and when the manifest is used standalone.
//
// The manifest is schema 2 (docs/manifest.md) when it has a "schema" key; it
// can then be a platform, and in a platform link it takes part like any
// module. With a stock target a package can give objects, link.wrap and
// ilc.references only. Without "schema" it is schema 1:
// {
//   "name": "...",
//   "builds": [
//     {
//       "arch": "riscv64",
//       "os": "linux",
//       "libc": "zisk",
//       "static_lib": "libziskos.a",                    <- relative to manifest dir
//       "dotnet_lib": "Nethermind.ZiskBindings.dll",    <- relative to manifest dir
//       "dotnet_assemblyname": "Nethermind.ZiskBindings",
//       "wrap_symbols": ["memcpy", "memset", "memmove", "memcmp"]    <- optional
//     }
//   ]
// }
internal static class ExtLibResolver
{
    // Resolved output of a single --extlib spec.
    internal sealed class Result
    {
        // Absolute paths to the native objects and static libraries (.a), in link order.
        public List<string> StaticLibPaths { get; } = new List<string>();

        // Absolute paths to the .NET reference assemblies (.dll).
        public List<string> DotnetLibPaths { get; } = new List<string>();

        // Symbols to wrap via the linker --wrap flag, or empty if none.
        public List<string> WrapSymbols { get; } = new List<string>();
    }

    // The names that manifests use for an architecture and an OS.
    internal static string ArchName(TargetArchitecture arch) => arch switch
    {
        TargetArchitecture.X64     => "x64",
        TargetArchitecture.X86     => "x86",
        TargetArchitecture.ARM64   => "arm64",
        TargetArchitecture.RiscV64 => "riscv64",
        _ => throw new Exception($"Unsupported architecture: {arch}")
    };

    internal static string OsName(TargetOS os) => os switch
    {
        TargetOS.Windows => "windows",
        TargetOS.Linux   => "linux",
        TargetOS.UEFI    => "uefi",
        _ => throw new Exception($"Unsupported OS: {os}")
    };

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    // Resolve a single --extlib spec and return the paths of its manifests.
    // tempDir is used for downloads and nupkg extractions.
    public static async Task<List<string>> ResolveManifests(string spec, string tempDir, bool verbose)
    {
        Directory.CreateDirectory(tempDir);

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "bflat-compiler");

        // Case 1: GitHub repo:version
        if (IsGitHubRepoWithVersion(spec))
            return await ResolveFromGitHubRelease(spec, tempDir, verbose, httpClient);

        // Case 2: .nupkg (URL or local path)
        if (spec.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            string nupkgPath = await EnsureLocalFile(spec, tempDir, verbose, httpClient);
            string extractDir = ExtractNupkg(nupkgPath, verbose);
            return FindManifestsInDirectory(extractDir);
        }

        // Case 3: .bflat.manifest (URL or local path)
        if (spec.EndsWith(".bflat.manifest", StringComparison.OrdinalIgnoreCase))
        {
            string manifestPath = await EnsureLocalFile(spec, tempDir, verbose, httpClient);
            var manifests = new List<string> { manifestPath };
            if (!IsUrl(spec))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
                foreach (string sibling in FindManifestsInDirectory(dir, recursive: false))
                {
                    if (!string.Equals(Path.GetFullPath(sibling), Path.GetFullPath(manifestPath), StringComparison.Ordinal))
                        manifests.Add(sibling);
                }
            }
            return manifests;
        }

        throw new Exception(
            $"Cannot determine extlib type from '{spec}'. " +
            "Expected: GitHub repo URL with version tag (https://github.com/owner/repo:tag), " +
            "a .nupkg path/URL, or a .bflat.manifest path/URL.");
    }

    // Read the manifests of one package. A schema 2 manifest is returned as
    // it is: its "when" is evaluated later, once the platform is known. A
    // schema 1 manifest becomes its first builds[] entry that matches the
    // target, or nothing.
    public static List<BflatManifest> LoadPackage(List<string> manifestPaths, string arch, string os, string libc)
    {
        var manifests = new List<BflatManifest>();
        foreach (string manifestPath in manifestPaths)
        {
            using JsonDocument doc = ManifestReader.Parse(manifestPath);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new Exception($"Manifest '{manifestPath}' is not a JSON object");

            BflatManifest manifest = root.TryGetProperty("schema", out _)
                ? BflatManifest.ReadSchema2(new ManifestReader(manifestPath), root, manifestPath)
                : BflatManifest.ReadSchema1(manifestPath, root, arch, os, libc);
            if (manifest != null)
                manifests.Add(manifest);
        }
        return manifests;
    }

    // -------------------------------------------------------------------------
    // Detection helpers
    // -------------------------------------------------------------------------

    // Returns true when the spec is a GitHub repo URL with an embedded version tag,
    // e.g. https://github.com/owner/repo:v1.0.0
    private static bool IsGitHubRepoWithVersion(string spec)
    {
        if (!spec.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
            !spec.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            return false;

        // Find the first colon that appears after the scheme's "//"
        int colonPos = spec.IndexOf(':', spec.IndexOf("//") + 2);
        return colonPos > 0;
    }

    private static bool IsUrl(string spec)
        => spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
           spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Download helpers
    // -------------------------------------------------------------------------

    // If spec is a URL, download it into tempDir and return the local path.
    // If spec is already a local path, return it unchanged.
    private static async Task<string> EnsureLocalFile(
        string spec, string tempDir, bool verbose, HttpClient httpClient)
    {
        if (!IsUrl(spec))
            return spec;

        string fileName = Path.GetFileName(new Uri(spec).AbsolutePath);
        if (string.IsNullOrEmpty(fileName))
            fileName = "download";

        string destPath = Path.Combine(tempDir, fileName);

        if (verbose)
            Console.WriteLine($"Downloading {spec}...");

        byte[] bytes = await httpClient.GetByteArrayAsync(spec);
        await File.WriteAllBytesAsync(destPath, bytes);

        if (verbose)
            Console.WriteLine($"Downloaded to {destPath}");

        return destPath;
    }

    // -------------------------------------------------------------------------
    // Case 1: GitHub repo:version
    // -------------------------------------------------------------------------

    private static async Task<List<string>> ResolveFromGitHubRelease(
        string spec, string tempDir, bool verbose,
        HttpClient httpClient)
    {
        // Split "https://github.com/owner/repo:version" at the version colon
        int colonPos = spec.IndexOf(':', spec.IndexOf("//") + 2);
        string version = spec.Substring(colonPos + 1);
        string repoUrl = spec.Substring(0, colonPos);

        string[] parts = repoUrl.Replace("https://", "").Replace("http://", "").Split('/');
        string owner = parts[1];
        string repo = parts[2].TrimEnd('/');

        if (verbose)
            Console.WriteLine($"Fetching release '{version}' for {owner}/{repo}...");

        string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{version}";
        string releaseJson = await httpClient.GetStringAsync(apiUrl);

        using JsonDocument doc = JsonDocument.Parse(releaseJson);
        JsonElement root = doc.RootElement;

        // Find the single .nupkg asset in the release
        string nupkgUrl = null;
        string nupkgName = null;

        if (root.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out JsonElement nameEl)) continue;
                string name = nameEl.GetString();
                if (name == null || !name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) continue;

                if (nupkgUrl != null)
                    throw new Exception(
                        $"Multiple .nupkg files found in release '{version}' of {owner}/{repo}. Expected exactly one.");

                if (asset.TryGetProperty("browser_download_url", out JsonElement urlEl))
                {
                    nupkgUrl = urlEl.GetString();
                    nupkgName = name;
                }
            }
        }

        if (nupkgUrl == null)
            throw new Exception($"No .nupkg file found in release '{version}' of {owner}/{repo}");

        if (verbose)
            Console.WriteLine($"Found nupkg: {nupkgName}");

        // Download the nupkg
        string nupkgPath = Path.Combine(tempDir, $"{owner}_{repo}_{version}_{nupkgName}");

        if (verbose)
            Console.WriteLine($"Downloading {nupkgUrl}...");

        byte[] nupkgBytes = await httpClient.GetByteArrayAsync(nupkgUrl);
        await File.WriteAllBytesAsync(nupkgPath, nupkgBytes);

        if (verbose)
            Console.WriteLine($"Downloaded to {nupkgPath}");

        // Extract and locate the manifest
        string extractDir = ExtractNupkg(nupkgPath, verbose);
        return FindManifestsInDirectory(extractDir);
    }

    // -------------------------------------------------------------------------
    // Case 2: .nupkg helpers
    // -------------------------------------------------------------------------

    // Extract a .nupkg (ZIP) into a sibling "{nupkgPath}.extracted/" directory.
    // Returns the extraction directory.  Re-uses a cached extraction if present.
    private static string ExtractNupkg(string nupkgPath, bool verbose)
    {
        string extractDir = nupkgPath + ".extracted";

        if (Directory.Exists(extractDir))
        {
            if (verbose)
                Console.WriteLine($"Using cached extraction: {extractDir}");
            return extractDir;
        }

        if (verbose)
            Console.WriteLine($"Extracting {nupkgPath}...");

        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(nupkgPath, extractDir, overwriteFiles: true);

        if (verbose)
            Console.WriteLine($"Extracted to {extractDir}");

        return extractDir;
    }

    // Find the *.bflat.manifest files inside a directory tree.
    private static List<string> FindManifestsInDirectory(string dir, bool recursive = true)
    {
        var manifests = Directory.GetFiles(dir, "*.bflat.manifest",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        if (manifests.Length == 0)
            throw new Exception($"No *.bflat.manifest file found in {dir}");

        Array.Sort(manifests, StringComparer.Ordinal);
        return new List<string>(manifests);
    }

    // -------------------------------------------------------------------------
    // Manifest parsing (Cases 2 and 3)
    // -------------------------------------------------------------------------

    // The files that one package manifest gives a stock target, as absolute
    // paths. Paths are resolved relative to the manifest file's own
    // directory. This is the one base that is correct for both consumption
    // modes: an extracted .nupkg ships the manifest next to its libraries
    // (runtimes/<rid>/native/), and a standalone .bflat.manifest is used in
    // place. Resolving a .nupkg against its root instead broke packages whose
    // manifest references sibling files by bare name (e.g. static_lib
    // "libziskos.a").
    public static Result Contributions(BflatManifest manifest, ManifestTarget target, string tempDir, bool verbose)
    {
        string unimplemented = string.Join(", ", manifest.UnimplementedSections(stock: true));
        if (unimplemented.Length > 0)
            throw new Exception($"Manifest '{manifest.Path}': without a platform, a package can give objects, wraps and references only, not: {unimplemented}");

        var result = new Result();

        foreach (ManifestObject obj in manifest.EffectiveObjects())
        {
            if (!ManifestCondition.Applies(obj.When, target))
                continue;
            string absPath = manifest.Resolve(obj.File);
            if (!File.Exists(absPath))
                throw new Exception(
                    $"Static library not found: {absPath} (referenced from manifest '{manifest.Path}')");

            result.StaticLibPaths.Add(absPath);

            if (verbose)
                Console.WriteLine($"Found static library: {absPath}");
        }

        foreach (ManifestWrap wrap in manifest.Wraps)
        {
            if (ManifestCondition.Applies(wrap.When, target))
                result.WrapSymbols.Add(wrap.LinkerSymbol);
        }

        foreach (ManifestItem reference in manifest.IlcReferences)
        {
            if (!ManifestCondition.Applies(reference.When, target))
                continue;
            string absPath = ResolveReference(manifest, reference, tempDir, verbose);
            if (absPath != null)
                result.DotnetLibPaths.Add(absPath);
        }

        return result;
    }

    // The file of one ilc.references item, or null (with a warning under -v)
    // when it is missing. If the file name is not the assembly name, the
    // file is copied so that it is: the compiler uses the file name as the
    // reference identity.
    public static string ResolveReference(BflatManifest manifest, ManifestItem reference, string tempDir, bool verbose)
    {
        string absPath = manifest.Resolve(reference.Value);
        if (!File.Exists(absPath))
        {
            if (verbose)
                Console.WriteLine($"Warning: dotnet library not found: {absPath}, skipping");
            return null;
        }

        string dotnetAssemblyName = reference.Assembly;
        if (!string.IsNullOrEmpty(dotnetAssemblyName))
        {
            string expectedName = dotnetAssemblyName + ".dll";
            if (!Path.GetFileName(absPath).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                string renamedPath = Path.Combine(tempDir, expectedName);
                try
                {
                    Directory.CreateDirectory(tempDir);
                    File.Copy(absPath, renamedPath, overwrite: true);
                    absPath = renamedPath;
                    if (verbose)
                        Console.WriteLine($"Copied dotnet library as '{renamedPath}' to match assembly name");
                }
                catch (Exception ex)
                {
                    if (verbose)
                        Console.WriteLine(
                            $"Warning: Failed to copy dotnet library to '{expectedName}': {ex.Message}");
                }
            }
        }

        if (verbose)
            Console.WriteLine($"Found dotnet library: {absPath}");
        return absPath;
    }
}
