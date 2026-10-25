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
    private static Option<bool> ErrorOnFloatOption = new Option<bool>("--error-on-float", "Scan the compiled (post-substitution) IL and fail the build if any method still carries floating point (float/double). Intended for no-FPU targets such as zisk.");
    private static Option<bool> ErrorOnFloatBinaryOption = new Option<bool>("--error-on-float-binary", "Scan the LINKED binary's code and fail the build if any RISC-V floating-point (F/D) instruction is present. Exact whole-image check, complements the IL-level --error-on-float.");
    private static Option<bool> ErrorOnCompressedOption = new Option<bool>("--error-on-compressed", "Scan the linked binary's code and fail the build if any RISC-V compressed (C-extension, 16-bit) instruction is present. Intended for targets that only decode the base 32-bit encoding, such as zisk.");
    private static Option<bool> ErrorOnAtomicOption = new Option<bool>("--error-on-atomic", "Scan the linked binary's code and fail the build if any RISC-V atomic (A-extension: lr/sc/amo*) instruction is present.");
    private static Option<bool> RemoveEhOption = new Option<bool>("--remove-eh", "Strip the DWARF unwind tables (.eh_frame, .eh_frame_hdr, .dotnet_eh_table) from the linked zkVM image and fail fast on throw instead of dispatching the exception: a throw exits the guest, no catch or finally runs. Saves image size. By default the tables are kept and managed exception handling works.");
    private static Option<bool> NoUnalignedAccessOption = new Option<bool>("--no-unaligned-access", "Expand every memory access flagged unaligned into a naturally-aligned byte-wise sequence (RISC-V 64 only). For executors that assert addr % width == 0; costs code size and speed. By default wide unaligned loads/stores are emitted.");
    private static Option<string[]> LdFlagsOption = new Option<string[]>(new string[] { "--ldflags" }, "Arguments to pass to the linker");
    private static Option<string[]> CodegenOptOption = new Option<string[]>("--codegenopt", "Define a RyuJIT codegen option (Name=Value, integers in hex)");
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

    private static Option<string> TargetLibcOption = new Option<string>("--libc", "Target libc (Windows: shcrt|none, Linux: glibc|bionic|musl), or the --libc alias of a platform that an --extlib package defines");

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

    private static Option<bool> AllowUnsignedOption = new Option<bool>("--allow-unsigned", "Accept an --extlib package that is not signed (a bad signature is always an error)");
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
            CodegenOptOption,
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
            AllowUnsignedOption,
            SymChartOption,
            WrapCheckOption,
            ErrorOnFloatOption,
            ErrorOnFloatBinaryOption,
            ErrorOnCompressedOption,
            ErrorOnAtomicOption,
            NoUnalignedAccessOption,
            RemoveEhOption,
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

    /// <summary>
    /// --remove-eh: drop the unwind tables and fail fast on throw instead of
    /// dispatching. Manifest conditions see it as "eh": "failfast".
    /// </summary>
    private bool _removeEh;

    /// <summary>
    /// The libcs of the stock NativeAOT targets. Any other --libc value must
    /// be the alias of a platform (docs/manifest.md): a zkVM target, whose
    /// manifest ships in its --extlib package.
    /// </summary>
    private static readonly string[] StockLibcs = { "glibc", "musl", "bionic", "shcrt", "none" };

    /// <summary>
    /// Lists the wrapped symbols that name a managed method the scanner
    /// reached but codegen never emitted.
    /// </summary>
    /// <remarks>
    /// A linker wrap only redirects calls. A managed method that every caller inlined
    /// leaves no call behind, so its wrapper is never linked and the managed
    /// body runs instead, with nothing reporting it: that is how constant
    /// ILLink stubs of HashHelpers.IsPrime silently replaced the integer
    /// reimplementation they were stubbed for. A method inlined by only some
    /// of its callers is still emitted and is not reported.
    /// </remarks>
    private static List<string> FindInlinedWrapTargets(IReadOnlyCollection<string> wrapTargets, ILScanResults scanResults,
        CompilationResults compilationResults, NameMangler nameMangler)
    {
        var inlined = new List<string>();
        if (wrapTargets.Count == 0)
            return inlined;

        var emitted = new HashSet<MethodDesc>(compilationResults.CompiledMethodBodies);
        foreach (MethodDesc reached in scanResults.CompiledMethodBodies)
        {
            if (emitted.Contains(reached))
                continue;
            string symbol = nameMangler.GetMangledMethodName(reached).ToString();
            if (wrapTargets.Contains(symbol))
                inlined.Add($"{symbol} ({reached})");
        }
        inlined.Sort(StringComparer.Ordinal);
        return inlined;
    }

    public override int Handle(ParseResult result)
    {
        _removeEh = result.GetValueForOption(RemoveEhOption);
        bool nooptimize = result.GetValueForOption(DisableOptimizationOption);
        bool optimizeSpace = result.GetValueForOption(OptimizeSizeOption);
        bool optimizeTime = result.GetValueForOption(OptimizeSpeedOption);
        string homePath = CommonOptions.HomePath;

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
        bool verbose = result.GetValueForOption(CommonOptions.VerbosityOption);
        // What the bundled compiler package can do, for "compiler" conditions.
        var compilerCapabilities = new HashSet<string>(StringComparer.Ordinal);
#if RISCV64_ZB_LOWERING_FIXES
        compilerCapabilities.Add("zb-lowering-fixes");
#endif
        var buildTarget = new ManifestTarget
        {
            Arch = ExtLibResolver.ArchName(targetArchitecture),
            Os = ExtLibResolver.OsName(targetOS),
            Libc = libc,
            Eh = _removeEh ? "failfast" : "unwind",
            Stdlib = stdlibName,
            Optimize = optimizationMode != OptimizationMode.None,
            Verbose = verbose,
            Compiler = compilerCapabilities,
        };

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
                return 1;
            }
        }

        Dictionary<string, BflatManifest> modules = ManifestLink.LoadModules(Path.Combine(homePath, "lib", "modules"));
        BflatManifest platform = libc == null ? null
            : ManifestLink.FindPlatform(modules.Values.Concat(packages.SelectMany(p => p.Manifests)), libc);
        if (platform == null && libc != null && Array.IndexOf(StockLibcs, libc) < 0)
        {
            Console.Error.WriteLine(
                $"Error: --libc {libc} is not a stock libc ({string.Join(", ", StockLibcs)}), " +
                "and no --extlib package defines a platform with this alias. " +
                "A zkVM target needs its package, for example --extlib <its .nupkg or .bflat.manifest>.");
            return 1;
        }

        List<string> downloadedLibPaths = new List<string>();
        var extLibWrapSymbols = new List<string>();
        var referenceList = new List<string>(references ?? Array.Empty<string>());
        ManifestLink platformLink = null;
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
            return 1;
        }

        // The libc whose sysroot, crt and runtime files the target uses.
        string linkLibc = platformLink?.Target.Libc ?? libc;

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

        // The instruction set of riscv64. The layout (the dotnet-riscv
        // runtime, musl, libgcc) is built rv64im for the lp64 soft-float ABI,
        // so a riscv64 target has no F and D: without them ilc and the JIT
        // use the lp64 calling convention and soft-float lowering, and ilc
        // writes the lp64 float-ABI marker that the layout has. A platform
        // gives the rest of its instruction set (codegen.isa): a zkVM guest
        // also drops C and A, which stops compressed and atomic emission
        // through the same mechanism as the EnableRiscV64* options. Only the
        // extensions that this compiler package models can be named (.NET 10
        // knows no Zbs/Zicond), so each one is probed.
        if (targetArchitecture == TargetArchitecture.RiscV64)
        {
            string targetIsa = "-f,-d";
            if (platformLink != null && Environment.GetEnvironmentVariable("BFLAT_NO_ZK_ISA_REDUCTION") != "1")
            {
                string platformIsa = platformLink.Isa(
                    ext => new InstructionSetSupportBuilder(targetArchitecture).AddSupportedInstructionSet(ext));
                if (platformIsa.Length > 0)
                    targetIsa += "," + platformIsa;
            }
            isaArg = string.IsNullOrEmpty(isaArg) ? targetIsa : isaArg + "," + targetIsa;
        }

        InstructionSetSupport instructionSetSupport = Helpers.ConfigureInstructionSetSupport(isaArg, maxVectorTBitWidth: 0, isVectorTOptimistic: false, targetArchitecture, tsTargetOs,
                "Unrecognized instruction set {0}", "Unsupported combination of instruction sets: {0}/{1}", logger,
                optimizingForSize: optimizationMode == OptimizationMode.PreferSize);

        // The ABI is a property of the target, not of the instruction set:
        // the compiler takes the lp64 calling convention, the JIT flag and
        // the ELF float-ABI marker from TargetAbi. A riscv64 target without F
        // or D is lp64; the compiler refuses lp64 with F or D.
        bool softFloat = targetArchitecture == TargetArchitecture.RiscV64
            && (!instructionSetSupport.IsInstructionSetSupported(Internal.JitInterface.InstructionSet.RiscV64_F)
                || !instructionSetSupport.IsInstructionSetSupported(Internal.JitInterface.InstructionSet.RiscV64_D));

        var simdVectorLength = instructionSetSupport.GetVectorTSimdVector();
        var targetAbi = softFloat ? TargetAbi.NativeAotRiscV64SoftFloat : TargetAbi.NativeAot;
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

        ILProvider ilProvider = new CustomILProvider(ilProviderOld, typeSystemContext);

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
                currentLibPath = Path.Combine(currentLibPath, linkLibc ?? "glibc");
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

        var inputFilePaths = new Dictionary<string, string>
        {
            [compiledModuleName] = compiledModulePath,
        };

        // Substitutions XML for ILC, declared by modules in the ilc section of
        // their manifests (e.g. guest_subst).
        List<string> moduleSubstitutionFiles = platformLink?.Files(m => m.IlcSubstitutions) ?? new List<string>();

        typeSystemContext.InputFilePaths = inputFilePaths;
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
#if NET11_0_OR_GREATER
        TypeMapManager typeMapManager = new UsageBasedTypeMapManager(TypeMapMetadata.CreateFromAssembly((EcmaAssembly)compiledAssembly, typeSystemContext.GeneratedAssembly, TypeMapAssemblyTargetsMode.Traverse));
#else
        TypeMapManager typeMapManager = new UsageBasedTypeMapManager(TypeMapMetadata.CreateFromAssembly((EcmaAssembly)compiledAssembly, typeSystemContext));
#endif

        compilationRoots.Add(new UnmanagedEntryPointsRootProvider(compiledAssembly));

        if (stdlib == StandardLibType.DotNet)
        {
            compilationRoots.Add(new RuntimeConfigurationRootProvider("g_compilerEmbeddedSettingsBlob", Array.Empty<string>()));
            compilationRoots.Add(new RuntimeConfigurationRootProvider("g_compilerEmbeddedKnobsBlob", Array.Empty<string>()));
            compilationRoots.Add(new ExpectedIsaFeaturesRootProvider(instructionSetSupport));
        }
        else
        {
#if NET11_0_OR_GREATER
            compilationRoots.Add(new GenericRootProvider<object>(null, (_, rooter) => rooter.RootReadOnlyDataBlob(new byte[4], 4, "Trap threads", new Internal.Text.Utf8String("RhpTrapThreads"), exportHidden: true)));
#else
            compilationRoots.Add(new GenericRootProvider<object>(null, (_, rooter) => rooter.RootReadOnlyDataBlob(new byte[4], 4, "Trap threads", "RhpTrapThreads", exportHidden: true)));
#endif
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

        // The platform's direct P/Invoke libraries (ilc.direct_pinvoke), for
        // example "__Internal": a statically linked guest has no loader, so
        // the precompiles that bindings packages declare as
        // [DllImport("__Internal")] become ordinary calls.
        if (platformLink != null)
        {
            foreach (string library in platformLink.Values(m => m.IlcDirectPInvoke))
            {
                if (!directPinvokes.Contains(library))
                    directPinvokes.Add(library);
            }
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

        bool disableGlobalization = result.GetValueForOption(NoGlobalizationOption) || libc == "bionic" || libc == "musl";
        if (disableGlobalization)
        {
            featureSwitches.Add("System.Globalization.Invariant", true);
        }

        if (disableStackTraceData)
        {
            featureSwitches.Add("System.Diagnostics.StackTrace.IsSupported", false);
        }

        // The feature switches of the platform link (ilc.features), for
        // example invariant globalization and timezones. They replace the
        // defaults above; --feature replaces them.
        if (platformLink != null)
        {
            foreach ((string name, bool value) in platformLink.Features)
                featureSwitches[name] = value;
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

        // Built-in substitutions for the zkVM targets, declared by modules
        // (guest_subst's substitutions.xml): thread-pool tuning and similar
        // machinery that is provably dead on a single-threaded guest. Applied
        // before user files; the parser rejects duplicate method entries, so user files
        // extend rather than override this set.
        foreach (string substitutionFile in moduleSubstitutionFiles)
        {
            using FileStream mfs = File.OpenRead(substitutionFile);
            using XmlReader mreader = XmlReader.Create(mfs);
            substitutions.AppendFrom(BodySubstitutionsParser.GetSubstitutions(
                logger, typeSystemContext, mreader,
                substitutionFile, featureSwitches));
        }
        foreach (string substitutionFilePath in result.GetValueForOption(SubstitutionFilePathsOption) ?? Array.Empty<string>())
        {
            using FileStream fs = File.OpenRead(substitutionFilePath);
            using (XmlReader bodyReader = XmlReader.Create(fs))
            {
                substitutions.AppendFrom(BodySubstitutionsParser.GetSubstitutions(
                    logger, typeSystemContext, bodyReader, substitutionFilePath, featureSwitches));
            }

            fs.Seek(0, SeekOrigin.Begin);

            using XmlReader resourceReader = XmlReader.Create(fs);
            resourceBlocks = ManifestResourceBlockingPolicy.UnionBlockings(resourceBlocks,
                ManifestResourceBlockingPolicy.SubstitutionsReader.GetSubstitutions(
                    logger, typeSystemContext, resourceReader, substitutionFilePath, featureSwitches));
        }

        SubstitutionProvider substitutionProvider = new SubstitutionProvider(logger, featureSwitches, substitutions);
        ILProvider unsubstitutedILProvider = ilProvider;
        ilProvider = new SubstitutedILProvider(ilProvider, substitutionProvider, new DevirtualizationManager());

        var stackTracePolicy = !disableStackTraceData ?
#if NET11_0_OR_GREATER
            // Line numbers are a .NET 11 addition; keep the .NET 10 output shape.
            (StackTraceEmissionPolicy)new EcmaMethodStackTraceEmissionPolicy(includeLineNumbers: false) : new NoStackTraceEmissionPolicy();
#else
            (StackTraceEmissionPolicy)new EcmaMethodStackTraceEmissionPolicy() : new NoStackTraceEmissionPolicy();
#endif

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

        // The platform's RyuJIT options (codegen.options): the zkVM codegen
        // fixups and tuning, the ISA gates of the JIT, the inline-allocation
        // cell. A stock target gets the defaults of the JIT. The
        // two alignment options are written below, with the platform's value
        // in place of bflat's default.
        string[] alignmentOptions = { "JitNoUnalignedAccess", "JitRiscV64StrictAlign" };
        if (platformLink != null)
        {
            backendOptions.AddRange(platformLink.CodegenOptions
                .Where(o => !alignmentOptions.Any(n => o.StartsWith(n + "=", StringComparison.OrdinalIgnoreCase))));
        }

        // Unaligned access. dotnet-riscv fixup 35 (JitNoUnalignedAccess) defaults the
        // knob ON, expanding every access the front end flagged unaligned
        // (Unsafe.Read/WriteUnaligned, the `unaligned.` IL prefix, unrolled
        // cpblk/initblk) into a byte-wise lbu/slli/or - sb/srli sequence. That is only
        // needed by an executor that asserts addr % width == 0, and it costs roughly
        // a quarter of the executed instructions on a guest, so the value is always
        // passed explicitly rather than left to the knob's default: 0, unless
        // --no-unaligned-access or the platform asks for 1.
        //
        // Strict alignment (dotnet-riscv fixup: JitRiscV64StrictAlign) goes one step
        // further for an executor that rejects any misaligned access: every scalar
        // load/store and unrolled block copy whose address the JIT cannot prove
        // aligned gets an inline alignment test with a wide fast path and a
        // byte-wise slow path. 0 unless the platform asks for 1. A JIT built without
        // the fixups ignores the unknown knobs.
        if (targetArchitecture == TargetArchitecture.RiscV64)
        {
            bool noUnaligned = result.GetValueForOption(NoUnalignedAccessOption)
                || platformLink?.CodegenOption("JitNoUnalignedAccess") == "1";
            backendOptions.Add(noUnaligned
                ? "JitNoUnalignedAccess=1"
                : "JitNoUnalignedAccess=0");
            backendOptions.Add($"JitRiscV64StrictAlign={platformLink?.CodegenOption("JitRiscV64StrictAlign") ?? "0"}");
        }

        backendOptions.AddRange(result.GetValueForOption(CodegenOptOption));

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

#if !NET11_0_OR_GREATER
            // .NET 11 dropped ILScanResults.GetBodyAndFieldSubstitutions; the
            // pre-scan substitutionProvider is reused as-is (same as upstream ilc).
            substitutions.AppendFrom(scanResults.GetBodyAndFieldSubstitutions());

            substitutionProvider = new SubstitutionProvider(logger, featureSwitches, substitutions);
#endif

            ilProvider = new SubstitutedILProvider(unsubstitutedILProvider, substitutionProvider, devirtualizationManager, metadataManager, scanResults.GetAnalysisCharacteristics());

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

        PerfWatch compileWatch = new PerfWatch("Native compile");
        CompilationResults compilationResults = compilation.Compile(objectFilePath, ObjectDumper.Compose(dumpers));
        compileWatch.Complete();

        // --error-on-float: fail the build if any EMITTED method's (post-
        // substitution) IL still carries floating point. Scanning only the methods
        // that were actually compiled (CompiledMethodBodies) - not every method the
        // compiler merely queried - keeps preinit-folded cctors and dead generic
        // instantiations from raising false alarms. Reported by managed method name.
        if (result.GetValueForOption(ErrorOnFloatOption) || platformLink?.Checks.IlFloat == true)
        {
            var offenders = new SortedDictionary<string, string>(StringComparer.Ordinal);
            List<string> floatAllowed = platformLink?.IlFloatAllowed ?? new List<string>();
            int allowed = 0;
            foreach (MethodDesc emitted in compilationResults.CompiledMethodBodies)
            {
                MethodIL emittedIL;
                try { emittedIL = ilProvider.GetMethodIL(emitted); }
                catch { continue; }
                if (emittedIL == null)
                    continue;
                string reason = ILFloatScanner.Find(emittedIL);
                if (reason == null)
                    continue;

                if (ILFloatScanner.IsAllowed(emitted, floatAllowed))
                {
                    // The conv is real IL but sits in a block the JIT proves dead
                    // (checks.il_float_allow of the platform link). Surfaced as a
                    // warning so it stays auditable rather than silently ignored.
                    Console.WriteLine($"warning: --error-on-float: ignoring known dead FP in {emitted}  [{reason}]");
                    allowed++;
                    continue;
                }
                offenders[emitted.ToString()] = reason;
            }

            if (offenders.Count > 0)
            {
                Console.Error.WriteLine($"error: --error-on-float: {offenders.Count} compiled method(s) carry floating point:");
                foreach (var kv in offenders)
                    Console.Error.WriteLine($"  {kv.Key}  [{kv.Value}]");
                return 1;
            }

            Console.WriteLine($"--error-on-float: OK (no floating point in any compiled method{(allowed > 0 ? $"; {allowed} known-dead site(s) ignored" : "")})");
        }

        string exportsFile = null;
        if (nativeLib)
        {
            exportsFile = Path.ChangeExtension(outputFilePath, targetOS == TargetOS.Windows ? ".def" : ".txt");
            ExportsFileWriter defFileWriter = new ExportsFileWriter(typeSystemContext, exportsFile, []);
            foreach (var compilationRoot in compilationRoots)
            {
                if (compilationRoot is UnmanagedEntryPointsRootProvider provider && !provider.Hidden)
#if NET11_0_OR_GREATER
                    defFileWriter.AddExportedMethods(provider.ExportedMethods, compilationResults);
#else
                    defFileWriter.AddExportedMethods(provider.ExportedMethods);
#endif
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
                else if (linkLibc == "musl")
                {
                    ldArgs.Append("-static ");
                    if (platformLink != null)
                    {
                        // musl's crt1.o with _start renamed (bflat.csproj): the
                        // platform entry point defines _start.
                        ldArgs.Append($"\"{firstLib}/crt1-platform.o\" ");
                    }
                    else
                        ldArgs.Append($"\"{firstLib}/crt1.o\" ");
                    ldArgs.Append($"\"{firstLib}/crti.o\" ");
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

            if (libc != "bionic" && linkLibc != "musl")
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
            if (linkLibc != "musl")
            {
                ldArgs.Append("-lc -lgcc ");
            }

            if (libc != "bionic" && linkLibc != "musl")
            {
                ldArgs.Append("-lrt --as-needed -lgcc_s --no-as-needed ");
                if (!result.GetValueForOption(CommonOptions.NoPthreadOption))
                    ldArgs.Append("-lpthread ");
            }
            else if (linkLibc == "musl")
            {
                /* pre-libc: module objects that work by DEFINITION, not by
                 * a linker wrap, so they MUST precede libc.a (nothread and noos; their
                 * manifests tell why). The linker extracts an archive member
                 * only to resolve a symbol that is still undefined at the point
                 * it reaches the archive, so with these definitions already in
                 * hand it never takes musl's. */
                platformLink?.AppendObjects(ldArgs, ObjectGroup.PreLibc);
                ldArgs.Append($"\"{firstLib}/libc.a\" ");

                /* The soft-float builtins. Without an FPU the compiler lowers
                 * every double operation to a call to __adddf3 and its 51
                 * siblings, and on riscv64 nothing else here defines them:
                 * the runtime's own C++ (the GC's cgroup limits, the yield
                 * normalization) calls them, and so does anything the guest
                 * does with a double. libgcc.a comes last because these are
                 * the leaves - nothing it pulls in refers back to libc. */
                if (softFloat)
                    ldArgs.Append($"\"{firstLib}/libgcc.a\" ");
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
            else if (linkLibc == "musl")
            {
                ldArgs.Append($"\"{firstLib}/crtn.o\" ");
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

            // The wraps that the packages of a stock target give.
            ManifestLink.AppendWraps(ldArgs, extLibWrapSymbols);


            if (platformLink != null)
            {
                /* The rest of the platform link comes from the manifests: the
                 * package objects, the platform's memory map and entry point
                 * (_start speaks that VM's halt protocol), then the module
                 * objects by link group. Each manifest's wraps follow its first
                 * object. "bflat-modules link <layout>/lib/modules <platform
                 * manifest>" shows the result without a guest build. */
                platformLink.LibPath = firstLib;
                platformLink.AppendObjects(ldArgs, ObjectGroup.PreEntry);
                platformLink.AppendEntry(ldArgs);
                platformLink.AppendObjects(ldArgs, ObjectGroup.Early);
                ldArgs.Append("--whole-archive ");
                platformLink.AppendObjects(ldArgs, ObjectGroup.WholeArchive);
                ldArgs.Append("--no-whole-archive ");
                platformLink.AppendObjects(ldArgs, ObjectGroup.Normal);
                platformLink.AppendRemainingOptions(ldArgs);
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

        if (scanResults != null)
        {
            List<string> inlinedWrapTargets = FindInlinedWrapTargets(platformLink?.WrittenWraps ?? extLibWrapSymbols,
                scanResults, compilationResults, ((ILCompiler.Compilation)compilation).NodeFactory.NameMangler);
            if (inlinedWrapTargets.Count > 0)
            {
                Console.Error.WriteLine(
                    $"error: {inlinedWrapTargets.Count} wrapped symbol(s) are managed methods that every caller " +
                    "inlined, so no call is left to redirect and the managed body runs instead of the wrapper. " +
                    "Replace the body itself (e.g. with an ILLink substitution) instead of wrapping it:");
                foreach (string target in inlinedWrapTargets)
                    Console.Error.WriteLine($"  {target}");
                return 1;
            }
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

        // The platform's post-link steps (postlink), in order. Each one
        // rewrites the output in place: what -o names is what the zkVM runs.
        // (The zisk pass used to leave the rewritten image beside the output
        // as <name>.patched, and every consumer had to know which of the two
        // files was the real one; getting it wrong ran the unpatched image,
        // which starts and then misbehaves in ways that look like a compiler
        // bug.) The only tool is patch_elf, which bflat ships.
        if (platformLink != null && exitCode == 0)
        {
            foreach ((string tool, List<string> toolArgs) in platformLink.PostlinkSteps())
            {
                string patchedPath = outputFilePath + ".postlink";
                string joinedArgs = string.Join(" ", toolArgs);
                int patchExitCode = RunCommand(patchElfPath,
                    outputFilePath + " " + patchedPath + " " + joinedArgs + " ",
                    printCommands);
                if (patchExitCode == 0 && File.Exists(patchedPath))
                {
                    File.Move(patchedPath, outputFilePath, true);
                }
                else
                {
                    Console.Error.WriteLine($"error: {tool} {joinedArgs} failed for the {platform.Name} image");
                    exitCode = patchExitCode != 0 ? patchExitCode : 1;
                    break;
                }
            }
        }

        // Exact whole-image ISA verification: decode the linked binary and fail
        // the build if it carries an instruction class the target cannot run.
        // The post-link steps above have already run and rewritten the output
        // in place, so this is the image the guest executes. The platform's
        // checks section turns each check on by default.
        ManifestChecks platformChecks = platformLink?.Checks ?? new ManifestChecks();
        bool errorOnFloatBinary = result.GetValueForOption(ErrorOnFloatBinaryOption) || platformChecks.Float;
        bool errorOnCompressed = result.GetValueForOption(ErrorOnCompressedOption) || platformChecks.Compressed;
        bool errorOnAtomic = result.GetValueForOption(ErrorOnAtomicOption) || platformChecks.Atomic;
        if (exitCode == 0 && (errorOnFloatBinary || errorOnCompressed || errorOnAtomic))
        {
            int isaRc = IsaVerifier.Verify(outputFilePath, errorOnFloatBinary, errorOnCompressed, errorOnAtomic);
            if (isaRc != 0)
                exitCode = isaRc;
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
            exitCode = RunCommand(objcopy, $"--strip-debug --strip-unneeded \"{outputFilePath}\"", printCommands);
            if (exitCode != 0) return exitCode;
            exitCode = RunCommand(objcopy, $"--add-gnu-debuglink=\"{outputFilePath}.dwo\" \"{outputFilePath}\"", printCommands);
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
