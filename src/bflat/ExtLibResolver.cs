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

// Resolves --extlib specs into concrete file paths.
//
// Supported spec formats:
//   1. https://github.com/owner/repo:tag   – GitHub release containing a single .nupkg
//   2. path or URL ending in .nupkg        – extract the package and read *.bflat.manifest inside
//   3. path or URL ending in .bflat.manifest – use the manifest directly (paths relative to manifest dir)
//
// The *.bflat.manifest format is:
// {
//   "name": "...",
//   "builds": [
//     {
//       "arch": "riscv64",
//       "os": "linux",
//       "libc": "zisk",
//       "static_lib": "runtimes/linux-riscv64/native/libziskos.a",   <- relative to nupkg root
//       "dotnet_lib": "lib/net10.0/Nethermind.ZiskBindings.dll",     <- relative to nupkg root
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
        // Absolute path to the native static library (.a), or null if not present.
        public string StaticLibPath { get; set; }

        // Absolute path to the .NET reference assembly (.dll), or null if not present.
        public string DotnetLibPath { get; set; }

        // Symbols to wrap via the linker --wrap flag, or empty if none.
        public List<string> WrapSymbols { get; set; } = new List<string>();
    }

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    // Resolve a single --extlib spec and return the paths to the library files.
    // tempDir is used for downloads and nupkg extractions.
    public static async Task<Result> Resolve(
        string spec, string tempDir, bool verbose,
        TargetArchitecture targetArch, TargetOS targetOS, string libc)
    {
        Directory.CreateDirectory(tempDir);

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "bflat-compiler");

        // Case 1: GitHub repo:version
        if (IsGitHubRepoWithVersion(spec))
            return await ResolveFromGitHubRelease(spec, tempDir, verbose, targetArch, targetOS, libc, httpClient);

        // Case 2: .nupkg (URL or local path)
        if (spec.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            string nupkgPath = await EnsureLocalFile(spec, tempDir, verbose, httpClient);
            string extractDir = ExtractNupkg(nupkgPath, verbose);
            string manifestPath = FindManifestInDirectory(extractDir);
            return ParseManifestAndResolveFiles(manifestPath, verbose, targetArch, targetOS, libc, tempDir, nupkgRoot: extractDir);
        }

        // Case 3: .bflat.manifest (URL or local path)
        if (spec.EndsWith(".bflat.manifest", StringComparison.OrdinalIgnoreCase))
        {
            string manifestPath = await EnsureLocalFile(spec, tempDir, verbose, httpClient);
            return ParseManifestAndResolveFiles(manifestPath, verbose, targetArch, targetOS, libc, tempDir);
        }

        throw new Exception(
            $"Cannot determine extlib type from '{spec}'. " +
            "Expected: GitHub repo URL with version tag (https://github.com/owner/repo:tag), " +
            "a .nupkg path/URL, or a .bflat.manifest path/URL.");
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

    private static async Task<Result> ResolveFromGitHubRelease(
        string spec, string tempDir, bool verbose,
        TargetArchitecture targetArch, TargetOS targetOS, string libc,
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
        string manifestPath = FindManifestInDirectory(extractDir);
        return ParseManifestAndResolveFiles(manifestPath, verbose, targetArch, targetOS, libc, tempDir, nupkgRoot: extractDir);
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

    // Find the single *.bflat.manifest file inside a directory tree.
    private static string FindManifestInDirectory(string dir)
    {
        var manifests = Directory.GetFiles(dir, "*.bflat.manifest", SearchOption.AllDirectories);

        if (manifests.Length == 0)
            throw new Exception($"No *.bflat.manifest file found in {dir}");

        if (manifests.Length > 1)
            throw new Exception(
                $"Multiple *.bflat.manifest files found in {dir}: {string.Join(", ", manifests)}");

        return manifests[0];
    }

    // -------------------------------------------------------------------------
    // Manifest parsing (Cases 2 and 3)
    // -------------------------------------------------------------------------

    // Parse a *.bflat.manifest file, match the build entry against the target,
    // and return absolute paths to static_lib / dotnet_lib.
    // When nupkgRoot is provided (nupkg-based cases), paths in the manifest are resolved
    // relative to the nupkg root directory.  For standalone manifests (nupkgRoot == null),
    // paths are resolved relative to the manifest file's own directory.
    private static Result ParseManifestAndResolveFiles(
        string manifestPath, bool verbose,
        TargetArchitecture targetArch, TargetOS targetOS, string libc,
        string tempDir, string nupkgRoot = null)
    {
        string manifestDir = nupkgRoot ?? Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        string manifestJson = File.ReadAllText(manifestPath);

        using JsonDocument doc = JsonDocument.Parse(manifestJson);
        JsonElement manifestRoot = doc.RootElement;

        string targetArchStr = targetArch switch
        {
            TargetArchitecture.X64    => "x64",
            TargetArchitecture.X86    => "x86",
            TargetArchitecture.ARM64  => "arm64",
            TargetArchitecture.RiscV64 => "riscv64",
            _ => throw new Exception($"Unsupported architecture: {targetArch}")
        };

        string targetOSStr = targetOS switch
        {
            TargetOS.Windows => "windows",
            TargetOS.Linux   => "linux",
            TargetOS.UEFI    => "uefi",
            _ => throw new Exception($"Unsupported OS: {targetOS}")
        };

        string staticLibRel    = null;
        string dotnetLibRel    = null;
        string dotnetAssemblyName = null;
        var wrapSymbols = new List<string>();

        if (manifestRoot.TryGetProperty("builds", out JsonElement builds))
        {
            foreach (JsonElement build in builds.EnumerateArray())
            {
                bool archMatch = build.TryGetProperty("arch", out JsonElement archEl) &&
                                 archEl.GetString() == targetArchStr;
                bool osMatch   = build.TryGetProperty("os",   out JsonElement osEl)   &&
                                 osEl.GetString()   == targetOSStr;

                bool libcMatch = true;
                if (!string.IsNullOrEmpty(libc) && build.TryGetProperty("libc", out JsonElement libcEl))
                    libcMatch = libcEl.GetString() == libc;

                if (archMatch && osMatch && libcMatch)
                {
                    if (build.TryGetProperty("static_lib",         out JsonElement slEl))
                        staticLibRel = slEl.GetString();
                    if (build.TryGetProperty("dotnet_lib",         out JsonElement dlEl))
                        dotnetLibRel = dlEl.GetString();
                    if (build.TryGetProperty("dotnet_assemblyname", out JsonElement danEl))
                        dotnetAssemblyName = danEl.GetString();
                    if (build.TryGetProperty("wrap_symbols",        out JsonElement wsEl) &&
                        wsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement sym in wsEl.EnumerateArray())
                        {
                            string symStr = sym.GetString();
                            if (!string.IsNullOrEmpty(symStr))
                                wrapSymbols.Add(symStr);
                        }
                    }
                    break;
                }
            }
        }

        if (staticLibRel == null && dotnetLibRel == null)
            throw new Exception(
                $"No matching build in manifest '{manifestPath}' " +
                $"for arch={targetArchStr}, os={targetOSStr}, libc={libc ?? "any"}");

        var result = new Result { WrapSymbols = wrapSymbols };

        if (staticLibRel != null)
        {
            string absPath = Path.Combine(manifestDir, staticLibRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absPath))
                throw new Exception(
                    $"Static library not found: {absPath} (referenced from manifest '{manifestPath}')");

            result.StaticLibPath = absPath;

            if (verbose)
                Console.WriteLine($"Found static library: {absPath}");
        }

        if (dotnetLibRel != null)
        {
            string absPath = Path.Combine(manifestDir, dotnetLibRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absPath))
            {
                if (verbose)
                    Console.WriteLine($"Warning: dotnet library not found: {absPath}, skipping");
            }
            else
            {
                // If the DLL filename doesn't already match the assembly name, copy it so it does.
                // The compiler uses the filename as the reference identity.
                if (!string.IsNullOrEmpty(dotnetAssemblyName))
                {
                    string expectedName = dotnetAssemblyName + ".dll";
                    if (!Path.GetFileName(absPath).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    {
                        string renamedPath = Path.Combine(tempDir, expectedName);
                        try
                        {
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

                result.DotnetLibPath = absPath;

                if (verbose)
                    Console.WriteLine($"Found dotnet library: {absPath}");
            }
        }

        return result;
    }
}