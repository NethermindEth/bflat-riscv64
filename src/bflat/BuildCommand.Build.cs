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
using System.CommandLine.Parsing;
using System.Runtime.InteropServices;
using System.Text;

using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis;

using ILCompiler;

using Internal.IL;
using Internal.TypeSystem;

internal partial class BuildCommand
{
    /// <summary>
    /// One build: the options, the platform link, and what each stage gives
    /// the next. <see cref="Run"/> runs the stages in order:
    /// <list type="number">
    /// <item><see cref="ReadTarget"/>: the target and the options (this file).</item>
    /// <item><see cref="ResolveExtLibs"/>: the --extlib packages and the platform link (this file).</item>
    /// <item><see cref="ConfigureTarget"/> and <see cref="CompileCSharp"/>: the target details and the IL module (BuildCommand.Compile.cs).</item>
    /// <item><see cref="CompileNative"/>: ILC (BuildCommand.Compile.cs).</item>
    /// <item><see cref="WriteWindowsLinkCommand"/> or <see cref="WriteLinuxLinkCommand"/>, then <see cref="Link"/>:
    /// the linker, the post-link steps and the image checks (BuildCommand.Link.cs).</item>
    /// </list>
    /// </summary>
    private sealed partial class Build
    {
        private readonly ParseResult result;

        // ReadTarget
        private bool nooptimize;
        private string homePath;
        private OptimizationMode optimizationMode;
        private StandardLibType stdlib;
        private string[] userSpecifiedInputFiles;
        private string[] inputFiles;
        private string[] defines;
        private string libc;
        private string[] references;
        private string[] extraLd;
        private TargetOS targetOS;
        private TargetArchitecture targetArchitecture;
        private bool verbose;
        private ManifestTarget buildTarget;

        // ResolveExtLibs
        private BflatManifest platform;
        private List<string> downloadedLibPaths;
        private List<string> extLibWrapSymbols;
        private ManifestLink platformLink;
        private string linkLibc;

        // ConfigureTarget
        private OptimizationLevel optimizationLevel;
        private string userSpecificedOutputFileName;
        private string outputNameWithoutSuffix;
        private bool disableStackTraceData;
        private string systemModuleName;
        private string compiledModuleName;
        private ILProvider ilProviderOld;
        private Logger logger;
        private SharedGenericsMode genericsMode;
        private bool disableReflection;
        private bool supportsReflection;
        private InstructionSetSupport instructionSetSupport;
        private bool softFloat;
        private TargetDetails targetDetails;

        // CompileCSharp
        private BuildTargetType buildTargetType;
        private bool nativeLib;
        private DebugInformationFormat debugInfoFormat;
        private string compiledModulePath;
        private string outputFilePath;

        // CompileNative
        private string libPath;
        private string objectFilePath;
        private string exportsFile;
        private ILScanResults scanResults;
        private CompilationResults compilationResults;
        private ICompilation compilation;

        public Build(ParseResult result)
        {
            this.result = result;
        }

        public int Run()
        {
            ReadTarget();
            if (!ResolveExtLibs())
                return 1;
            ConfigureTarget();
            if (!CompileCSharp())
                return 1;
            if (!CompileNative())
                return 1;
            if (result.GetValueForOption(NoLinkOption))
                return 0;

            if (logger.IsVerbose)
                logger.LogMessage("Running the linker");

            var ldArgs = new StringBuilder();
            if (targetOS is TargetOS.Windows or TargetOS.UEFI)
                WriteWindowsLinkCommand(ldArgs);
            else if (targetOS == TargetOS.Linux)
                WriteLinuxLinkCommand(ldArgs);
            ldArgs.AppendJoin(' ', result.GetValueForOption(LdFlagsOption));

            return Link(ldArgs);
        }

        /// <summary>The target (architecture, OS, libc) and the options that the manifest conditions test.</summary>
        private void ReadTarget()
        {
            bool removeEh = result.GetValueForOption(RemoveEhOption);
            nooptimize = result.GetValueForOption(DisableOptimizationOption);
            bool optimizeSpace = result.GetValueForOption(OptimizeSizeOption);
            bool optimizeTime = result.GetValueForOption(OptimizeSpeedOption);
            homePath = CommonOptions.HomePath;

            optimizationMode = OptimizationMode.Blended;
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

            stdlib = result.GetValueForOption(CommonOptions.StdLibOption);
            userSpecifiedInputFiles = result.GetValueForArgument(CommonOptions.InputFilesArgument);
            inputFiles = CommonOptions.GetInputFiles(userSpecifiedInputFiles);
            defines = result.GetValueForOption(CommonOptions.DefinedSymbolsOption);
            libc = result.GetValueForOption(TargetLibcOption);
            references = CommonOptions.GetReferencePaths(result.GetValueForOption(CommonOptions.ReferencesOption), stdlib,
                result.GetValueForOption(CommonOptions.NoStdLibRefsOption));
            extraLd = result.GetValueForOption(CommonOptions.ExtraLd);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                targetOS = TargetOS.Windows;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                targetOS = TargetOS.Linux;
            else
                throw new NotImplementedException();

            targetArchitecture = RuntimeInformation.ProcessArchitecture switch
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

            // Platforms (docs/manifest.md). A zkVM target is a platform manifest
            // that an --extlib package ships, selected by its --libc alias. The
            // platform gives its entry point, linker script, ISA, ABI, RyuJIT
            // options, defines and post-link steps, and the modules from
            // lib/modules that it requires; each module gives its objects, wraps
            // and ILC inputs. bflat itself knows no zkVM.
            string stdlibName = stdlib switch
            {
                StandardLibType.DotNet => "dotnet",
                StandardLibType.Zero => "zero",
                _ => "none",
            };
            verbose = result.GetValueForOption(CommonOptions.VerbosityOption);
            // What the bundled compiler package can do, for "compiler" conditions.
            var compilerCapabilities = new HashSet<string>(StringComparer.Ordinal);
    #if RISCV64_ZB_LOWERING_FIXES
            compilerCapabilities.Add("zb-lowering-fixes");
    #endif
            buildTarget = new ManifestTarget
            {
                Arch = ExtLibResolver.ArchName(targetArchitecture),
                Os = ExtLibResolver.OsName(targetOS),
                Libc = libc,
                Eh = removeEh ? "failfast" : "unwind",
                Stdlib = stdlibName,
                Optimize = optimizationMode != OptimizationMode.None,
                Verbose = verbose,
                Compiler = compilerCapabilities,
            };
        }

        /// <summary>Resolves and checks the --extlib packages, and makes the platform link when --libc names a platform. False after an error.</summary>
        private bool ResolveExtLibs()
        {
            // Handle extlib resolution synchronously - after we know target
            // arch/os/libc. The manifests are read first, because a package can
            // be the platform.
            string[] extLibSpecs = result.GetValueForOption(ExtLibOption) ?? Array.Empty<string>();
            string extLibTempDir = Path.Combine(Path.GetTempPath(), "bflat-extlibs");
            var packages = new List<(string Spec, List<BflatManifest> Manifests)>();
            PackageTrust trust = null;
            foreach (var spec in extLibSpecs)
            {
                try
                {
                    trust ??= PackageTrust.FromBuild(result.GetValueForOption(AllowUnsignedOption));
                    List<string> manifestPaths = ExtLibResolver.ResolveManifests(spec, extLibTempDir, verbose, trust).GetAwaiter().GetResult();
                    packages.Add((spec, ExtLibResolver.LoadPackage(manifestPaths, buildTarget.Arch, buildTarget.Os, libc)));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error resolving external library '{spec}': {ex.Message}");
                    return false;
                }
            }

            Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(Path.Combine(homePath, "lib", "modules"));
            platform = libc == null ? null
                : ManifestLink.FindPlatform(modules.Values.Concat(packages.SelectMany(p => p.Manifests)), libc);
            if (platform == null && libc != null && Array.IndexOf(StockLibcs, libc) < 0)
            {
                Console.Error.WriteLine(
                    $"Error: --libc {libc} is not a stock libc ({string.Join(", ", StockLibcs)}), " +
                    "and no --extlib package defines a platform with this alias. " +
                    "A zkVM target needs its package, for example --extlib <its .nupkg or .bflat.manifest>.");
                return false;
            }

            downloadedLibPaths = new List<string>();
            extLibWrapSymbols = new List<string>();
            var referenceList = new List<string>(references ?? Array.Empty<string>());
            platformLink = null;
            try
            {
                if (platform != null)
                {
                    platformLink = ManifestLink.Create(platform, modules, ManifestLink.TargetFor(platform, buildTarget),
                        packages.SelectMany(p => p.Manifests));
                    foreach ((string spec, List<BflatManifest> manifests) in packages)
                    {
                        if (!manifests.Any(platformLink.Manifests.Contains))
                            throw new Exception($"external library '{spec}' has no manifest for platform '{platform.Name}'");
                    }

                    // The objects, wraps and ld options of every manifest go
                    // into the link below; the references are needed now.
                    foreach ((BflatManifest owner, ManifestItem item) in platformLink.References())
                    {
                        string referencePath = ExtLibResolver.ResolveReference(owner, item, extLibTempDir, verbose);
                        if (referencePath == null)
                            continue;
                        referenceList.Add(referencePath);
                        if (verbose)
                            Console.WriteLine($"Added external dotnet reference: {referencePath}");
                    }

                    defines = (defines ?? Array.Empty<string>()).Concat(platformLink.Values(m => m.IlcDefines)).ToArray();
                }
                else
                {
                    foreach ((string spec, List<BflatManifest> manifests) in packages)
                    {
                        List<BflatManifest> applicable = manifests
                            .Where(m => m.Platform == null && ManifestCondition.Applies(m.When, buildTarget))
                            .ToList();
                        if (applicable.Count == 0)
                            throw new Exception(
                                $"No matching build in external library '{spec}' " +
                                $"for arch={buildTarget.Arch}, os={buildTarget.Os}, libc={libc ?? "any"}");

                        foreach (BflatManifest m in applicable)
                        {
                            ExtLibResolver.Result extLibResult = ExtLibResolver.Contributions(m, buildTarget, extLibTempDir, verbose);

                            downloadedLibPaths.AddRange(extLibResult.StaticLibPaths);

                            foreach (string dotnetLibPath in extLibResult.DotnetLibPaths)
                            {
                                referenceList.Add(dotnetLibPath);
                                if (verbose)
                                    Console.WriteLine($"Added external dotnet reference: {dotnetLibPath}");
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
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return false;
            }

            // The libc whose sysroot, crt and runtime files the target uses.
            linkLibc = platformLink?.Target.Libc ?? libc;

            references = referenceList.ToArray();

            return true;
        }
    }
}
