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
using System.Runtime.InteropServices;
using System.Xml;

using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis;

using ILCompiler;

using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

internal partial class BuildCommand
{
    private sealed partial class Build
    {
        /// <summary>The system module, the logger, the instruction set and the ABI of the target.</summary>
        private void ConfigureTarget()
        {
            optimizationLevel = nooptimize ? OptimizationLevel.Debug : OptimizationLevel.Release;

            userSpecificedOutputFileName = result.GetValueForOption(CommonOptions.OutputOption);
            outputNameWithoutSuffix =
                userSpecificedOutputFileName != null ? Path.GetFileNameWithoutExtension(userSpecificedOutputFileName) :
                CommonOptions.GetOutputFileNameWithoutSuffix(userSpecifiedInputFiles);

            disableStackTraceData = result.GetValueForOption(NoStackTraceDataOption) || stdlib != StandardLibType.DotNet;
            systemModuleName = DefaultSystemModule;
            compiledModuleName = Path.GetFileName(outputNameWithoutSuffix);

            if (stdlib == StandardLibType.None && references.Length == 0)
                systemModuleName = compiledModuleName;
            if (stdlib == StandardLibType.Zero)
                systemModuleName = "zerolib";

            ilProviderOld = new NativeAotILProvider();

            logger = new Logger(
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

            genericsMode = SharedGenericsMode.CanonicalReferenceTypes;

            disableReflection = result.GetValueForOption(NoReflectionOption);
            var tsTargetOs = targetOS switch
            {
                TargetOS.Windows or TargetOS.UEFI => Internal.TypeSystem.TargetOS.Windows,
                TargetOS.Linux => Internal.TypeSystem.TargetOS.Linux,
            };
            supportsReflection = !disableReflection && systemModuleName == DefaultSystemModule;

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

            instructionSetSupport = Helpers.ConfigureInstructionSetSupport(isaArg, maxVectorTBitWidth: 0, isVectorTOptimistic: false, targetArchitecture, tsTargetOs,
                    "Unrecognized instruction set {0}", "Unsupported combination of instruction sets: {0}/{1}", logger,
                    optimizingForSize: optimizationMode == OptimizationMode.PreferSize);

            // The ABI is a property of the target, not of the instruction set:
            // the compiler takes the lp64 calling convention, the JIT flag and
            // the ELF float-ABI marker from TargetAbi. A riscv64 target without F
            // or D is lp64; the compiler refuses lp64 with F or D.
            softFloat = targetArchitecture == TargetArchitecture.RiscV64
                && (!instructionSetSupport.IsInstructionSetSupported(Internal.JitInterface.InstructionSet.RiscV64_F)
                    || !instructionSetSupport.IsInstructionSetSupported(Internal.JitInterface.InstructionSet.RiscV64_D));

            var simdVectorLength = instructionSetSupport.GetVectorTSimdVector();
            var targetAbi = softFloat ? TargetAbi.NativeAotRiscV64SoftFloat : TargetAbi.NativeAot;
            targetDetails = new TargetDetails(targetArchitecture, tsTargetOs, targetAbi, simdVectorLength);
        }

        /// <summary>Compiles the C# sources with Roslyn into a temporary IL module, and picks the output file. False after an error.</summary>
        private bool CompileCSharp()
        {
            var ms = new MemoryStream();

            buildTargetType = result.GetValueForOption(CommonOptions.TargetOption);

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

            debugInfoFormat = result.GetValueForOption(CommonOptions.NoDebugInfoOption)
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

                return false;
            }
            ms.Seek(0, SeekOrigin.Begin);

            // Persist the Roslyn output so the type system can load it through the
            // standard path-based loader (registered in InputFilePaths below). This
            // replaces the in-memory CacheOpenModule hook that required a runtime patch.
            compiledModulePath = Path.GetTempFileName();
            using (var moduleFile = File.Create(compiledModulePath))
                ms.CopyTo(moduleFile);
            ms.Dispose();

            outputFilePath = userSpecificedOutputFileName;
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

            return true;
        }

        /// <summary>Compiles the IL module and its references with ILC into the object file. False after an error.</summary>
        private bool CompileNative()
        {
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

            libPath = FindLibPath();
            if (libPath == null)
                return false;

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

            (List<string> directPinvokes, List<string> directPinvokeList) = GetDirectPInvokes();
            PInvokeILEmitterConfiguration pinvokePolicy = new ConfigurablePInvokePolicy(typeSystemContext.Target, directPinvokes, directPinvokeList);

            Dictionary<string, bool> featureSwitches = GetFeatureSwitches();

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
            scanResults = null;
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

            List<string> backendOptions = GetBackendOptions();

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

            compilation = builder.ToCompilation();

            if (logger.IsVerbose)
                logger.LogMessage("Generating native code");
            string mapFileName = result.GetValueForOption(MapFileOption);
            string mstatFileName = result.GetValueForOption(MstatOption) ? Path.ChangeExtension(outputFilePath, ".mstat") : null;

            List<ObjectDumper> dumpers = new List<ObjectDumper>();

            if (mapFileName != null)
                dumpers.Add(new XmlObjectDumper(mapFileName));

            if (mstatFileName != null)
                dumpers.Add(new MstatObjectDumper(mstatFileName, typeSystemContext));

            objectFilePath = Path.ChangeExtension(outputFilePath, targetOS is TargetOS.Windows or TargetOS.UEFI ? ".obj" : ".o");

            PerfWatch compileWatch = new PerfWatch("Native compile");
            compilationResults = compilation.Compile(objectFilePath, ObjectDumper.Compose(dumpers));
            compileWatch.Complete();

            if ((result.GetValueForOption(ErrorOnFloatOption) || platformLink?.Checks.IlFloat == true) && !CheckIlFloat(ilProvider))
                return false;

            exportsFile = null;
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

            return true;
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

        /// <summary>The library path, or null if the target has no directory in the layout.</summary>
        private string FindLibPath()
        {
            string path = Environment.GetEnvironmentVariable("BFLAT_LIB");
            if (path == null)
            {
                char separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

                string currentLibPath = Path.Combine(homePath, "lib");

                path = currentLibPath;

                string osPart = targetOS switch
                {
                    TargetOS.Linux => "linux",
                    TargetOS.Windows => "windows",
                    TargetOS.UEFI => "uefi",
                    _ => throw new Exception(targetOS.ToString()),
                };
                currentLibPath = Path.Combine(currentLibPath, osPart);
                path = currentLibPath + separator + path;

                string archPart = targetArchitecture switch
                {
                    TargetArchitecture.ARM64 => "arm64",
                    TargetArchitecture.X64 => "x64",
                    TargetArchitecture.X86 => "x86",
                    TargetArchitecture.RiscV64 => "riscv64",
                    _ => throw new Exception(targetArchitecture.ToString()),
                };
                currentLibPath = Path.Combine(currentLibPath, archPart);
                path = currentLibPath + separator + path;

                if (targetOS == TargetOS.Linux)
                {
                    currentLibPath = Path.Combine(currentLibPath, linkLibc ?? "glibc");
                    path = currentLibPath + separator + path;
                }

                Console.WriteLine("Library path: " + path);
                if (!Directory.Exists(currentLibPath))
                {
                    Console.Error.WriteLine($"Directory '{currentLibPath}' doesn't exist.");
                    return null;
                }
            }

            return path;
        }

        /// <summary>The libraries that ILC binds statically, and the files that list more of them.</summary>
        private (List<string> Libraries, List<string> Files) GetDirectPInvokes()
        {
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

            return (directPinvokes, directPinvokeList);
        }

        /// <summary>The feature switches: the defaults of bflat, then the platform link (ilc.features), then --feature.</summary>
        private Dictionary<string, bool> GetFeatureSwitches()
        {
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

            return featureSwitches;
        }

        /// <summary>The RyuJIT options: the platform link (codegen.options), the alignment options, then --codegenopt.</summary>
        private List<string> GetBackendOptions()
        {
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

            return backendOptions;
        }

        /// <summary>
        /// --error-on-float: fail the build if any EMITTED method's (post-
        /// substitution) IL still carries floating point. Scanning only the methods
        /// that were actually compiled (CompiledMethodBodies) - not every method the
        /// compiler merely queried - keeps preinit-folded cctors and dead generic
        /// instantiations from raising false alarms. Reported by managed method name.
        /// </summary>
        private bool CheckIlFloat(ILProvider ilProvider)
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
                return false;
            }

            Console.WriteLine($"--error-on-float: OK (no floating point in any compiled method{(allowed > 0 ? $"; {allowed} known-dead site(s) ignored" : "")})");

            return true;
        }
    }
}
