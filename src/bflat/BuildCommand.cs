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

class CustomILProvider : ILProvider
{
    private ILProvider inner;
    public TypeSystemContext TypeContext;

    public CustomILProvider(ILProvider innerProvider, TypeSystemContext typeContext)
    {
        inner = innerProvider;
        TypeContext = typeContext;
    }

    public override MethodIL GetMethodIL(MethodDesc method)
    {
        if (method.OwningType is MetadataType owningType &&
            owningType.Namespace == "System" &&
            owningType.Name == "OutOfMemoryException" &&
            method.Name == "GetDefaultMessage")
        {
            var stringType = TypeContext.GetWellKnownType(WellKnownType.String);
            FieldDesc  emptyField = null;

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
                new object[] {}
            );
        }

        return inner.GetMethodIL(method);
    }
}

internal class BuildCommand : CommandBase
{
    private const string DefaultSystemModule = "System.Private.CoreLib";
    private BuildCommand() { }

    private static Option<bool> NoReflectionOption = new Option<bool>("--no-reflection", "Disable support for reflection");
    private static Option<bool> NoStackTraceDataOption = new Option<bool>("--no-stacktrace-data", "Disable support for textual stack traces");
    private static Option<bool> NoGlobalizationOption = new Option<bool>("--no-globalization", "Disable support for globalization (use invariant mode)");
    private static Option<bool> NoExceptionMessagesOption = new Option<bool>("--no-exception-messages", "Disable exception messages");
    private static Option<bool> NoPieOption = new Option<bool>("--no-pie", "Do not generate position independent executable");

    private static Option<bool> NoLinkOption = new Option<bool>("-c", "Produce object file, but don't run linker");
    private static Option<bool> MstatOption = new Option<bool>("--mstat", "Produce MSTAT and DGML files for size analysis");
    private static Option<string[]> LdFlagsOption = new Option<string[]>(new string[] { "--ldflags" }, "Arguments to pass to the linker");
    private static Option<bool> PrintCommandsOption = new Option<bool>("-x", "Print the commands");

    private static Option<bool> SeparateSymbolsOption = new Option<bool>("--separate-symbols", "Separate debugging symbols (Linux)");

    private static Option<string[]> DirectPInvokesOption = new Option<string[]>("-i", "Bind to entrypoint statically")
    {
        ArgumentHelpName = "library|library!function"
    };

    private static Option<bool> OptimizeSizeOption = new Option<bool>(new string[] { "-Os", "--optimize-space" }, "Favor code space when optimizing");
    private static Option<bool> OptimizeSpeedOption = new Option<bool>(new string[] { "-Ot", "--optimize-time" }, "Favor code speed when optimizing");
    private static Option<bool> DisableOptimizationOption = new Option<bool>(new string[] { "-O0", "--no-optimization" }, "Disable optimizations");

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

    public static Command Create()
    {
        var command = new Command("build", "Compiles the specified C# source files into native code")
        {
            CommonOptions.InputFilesArgument,
            CommonOptions.DefinedSymbolsOption,
            CommonOptions.ReferencesOption,
            CommonOptions.TargetOption,
            CommonOptions.OutputOption,
            NoLinkOption,
            LdFlagsOption,
            PrintCommandsOption,
            TargetArchitectureOption,
            TargetOSOption,
            TargetIsaOption,
            TargetLibcOption,
            OptimizeSizeOption,
            OptimizeSpeedOption,
            DisableOptimizationOption,
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
            CommonOptions.ResourceOption,
            CommonOptions.StdLibOption,
            CommonOptions.DeterministicOption,
            CommonOptions.NoPthreadOption,
            CommonOptions.VerbosityOption,
            CommonOptions.LangVersionOption,
            CommonOptions.ExtraLd,
            CommonOptions.KeepObjectOption,
        };
        command.Handler = new BuildCommand();

        return command;
    }

    private static IEnumerable<string> EnumerateExpandedDirectories(string paths, string pattern)
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

    public override int Handle(ParseResult result)
    {
        bool nooptimize = result.GetValueForOption(DisableOptimizationOption);
        bool optimizeSpace = result.GetValueForOption(OptimizeSizeOption);
        bool optimizeTime = result.GetValueForOption(OptimizeSpeedOption);

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
        string[] references = CommonOptions.GetReferencePaths(result.GetValueForOption(CommonOptions.ReferencesOption), stdlib);
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

        OptimizationLevel optimizationLevel = nooptimize ? OptimizationLevel.Debug : OptimizationLevel.Release;

        string userSpecificedOutputFileName = result.GetValueForOption(CommonOptions.OutputOption);
        string outputNameWithoutSuffix =
            userSpecificedOutputFileName != null ? Path.GetFileNameWithoutExtension(userSpecificedOutputFileName) :
            CommonOptions.GetOutputFileNameWithoutSuffix(userSpecifiedInputFiles);

        bool verbose = result.GetValueForOption(CommonOptions.VerbosityOption);
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
            new Dictionary<int,bool>(),
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

        if (stdlib != StandardLibType.DotNet)
        {
            SettingsTunnel.EmitGCInfo = false;
            SettingsTunnel.EmitEHInfo = false;
            SettingsTunnel.EmitGSCookies = false;
        }

        CompilerTypeSystemContext typeSystemContext =
            new BflatTypeSystemContext(targetDetails, genericsMode, supportsReflection ? DelegateFeature.All : 0, ms, compiledModuleName);

        ILProvider ilProvider = new CustomILProvider(ilProviderOld, typeSystemContext);

        var referenceFilePaths = new Dictionary<string, string>();

        foreach (var reference in references)
        {
            referenceFilePaths[Path.GetFileNameWithoutExtension(reference)] = reference;
        }

        string libc = result.GetValueForOption(TargetLibcOption);
        if (targetOS == TargetOS.Windows && targetArchitecture == TargetArchitecture.X86)
            libc ??= "none"; // don't have shcrt for Windows x86 because that one's hacked up

        string homePath = CommonOptions.HomePath;
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

        typeSystemContext.InputFilePaths = new Dictionary<string, string>();
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
        else
            initAssemblies.Add("System.Private.DisabledReflection");

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

        BodyAndFieldSubstitutions substitutions = default;
        IReadOnlyDictionary<ModuleDesc, IReadOnlySet<string>> resourceBlocks = default;

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

            metadataGenerationOptions |= UsageBasedMetadataGenerationOptions.ReflectionILScanning;
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
        bool preinitStatics = false; //optimizationMode != OptimizationMode.None;

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
        builder
            .UseInstructionSetSupport(instructionSetSupport)
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

            ilProvider = new SubstitutedILProvider(unsubstitutedILProvider, substitutionProvider, devirtualizationManager, metadataManager);

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
            // we collected at scanning time.
            // Inlined storage implies a single type manager, thus we do not do it in multifile case.
            // This could be a command line switch if we really wanted to.
            //if (libc != "bionic")
            //    builder.UseInlinedThreadStatics(scanResults.GetInlinedThreadStatics());
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
        }
        else if (targetOS == TargetOS.Linux)
        {
            ldArgs.Append("-flavor ld ");

            string ziskLibPath = Path.Combine(homePath, "lib", "linux", "riscv64", "zisk");
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

                if (!result.GetValueForOption(NoPieOption))
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

                /* rhp */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rhp.o")}\" ");
                ldArgs.Append($"--wrap=RhpNewFast ");
                ldArgs.Append($"--wrap=RhpNewObject ");
                ldArgs.Append($"--wrap=RhpNewPtrArrayFast ");
                ldArgs.Append($"--wrap=RhpNewArrayFast ");
                ldArgs.Append($"--wrap=RhNewString ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Runtime_TypeCast__CheckCastAny ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Diagnostics_Tracing_EventPipeEventProvider__Register ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Diagnostics_Tracing_EventSource__InitializeDefaultEventSources ");
                ldArgs.Append($"--wrap=GlobalizationNative_GetDefaultLocaleName ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_ProcessorIdCache__ProcessorNumberSpeedCheck ");
                ldArgs.Append($"--wrap=RhGetThreadStaticStorage ");
                ldArgs.Append($"--wrap=S_P_CoreLib_Internal_Runtime_ThreadStatics__GetUninlinedThreadStaticBaseForType ");
                ldArgs.Append($"--wrap=_Z16InitializeCGroupv ");
                ldArgs.Append($"--wrap=S_P_CoreLib_Internal_Runtime_CompilerHelpers_StartupCodeHelpers__InitializeCommandLineArgs ");
                ldArgs.Append($"--wrap=__GetNonGCStaticBase_S_P_CoreLib_System_Environment ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Thread__WaitForForegroundThreads ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__Enter ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__EnterAndGetCurrentThreadId ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__TryEnterSlow_0 ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__Exit_0 ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__Exit_1 ");
                ldArgs.Append($"--wrap=S_P_CoreLib_System_Threading_Lock__ExitAll ");
                ldArgs.Append($"--wrap=_ZN6Thread10IsDetachedEv ");
                if (libc == "zisk")
                {
                    ldArgs.Append($"--wrap=System_Console_Interop_Sys__InitializeTerminalAndSignalHandling ");
                    ldArgs.Append($"--wrap=SystemNative_SetTerminalInvalidationHandler ");
                    ldArgs.Append($"--wrap=SystemNative_Write ");
                }

                /* rhp_native */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rhp_native.o")}\" ");
                ldArgs.Append($"--wrap=RhpAssignRefRiscV64 ");
                ldArgs.Append($"--wrap=RhpCidResolve ");

                /* pal */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "pal.o")}\" ");
                ldArgs.Append($"--wrap=getenv ");
                ldArgs.Append($"--wrap=getcwd ");
                ldArgs.Append($"--wrap=getpid ");
                ldArgs.Append($"--wrap=getegid ");
                ldArgs.Append($"--wrap=geteuid ");
                ldArgs.Append($"--wrap=sched_getaffinity ");
                ldArgs.Append($"--wrap=open ");
                ldArgs.Append($"--wrap=__libc_malloc_impl ");
                ldArgs.Append($"--wrap=__libc_realloc ");
                ldArgs.Append($"--wrap=__libc_free ");
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

        PerfWatch linkWatch = new PerfWatch("Link");
        int exitCode = RunCommand(ld, ldArgs.ToString(), printCommands);
        linkWatch.Complete();

        if (libc == "zisk" && exitCode == 0)
        {
            int patchExitCode = RunCommand(patchElfPath,
                outputFilePath + " " + patchedFilePath +
                " --fix-init-array --fix-tdata --split-code-data --remove-eh ",
                printCommands);
        }
        if (!result.GetValueForOption(CommonOptions.KeepObjectOption))
        {
            try { File.Delete(objectFilePath); } catch { }
        }

        if (exportsFile != null)
            try { File.Delete(exportsFile); } catch { }

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
}
