﻿// bflat C# compiler
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
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

using Internal.TypeSystem;

internal class ILBuildCommand : CommandBase
{
    private ILBuildCommand() { }

    private static Option<bool> OptimizeOption = new Option<bool>("-O", "Enable optimizations");

    public static Command Create()
    {
        var command = new Command("build-il", "Compiles the specified C# source files into IL format")
        {
            CommonOptions.InputFilesArgument,
            CommonOptions.DefinedSymbolsOption,
            CommonOptions.ReferencesOption,
            CommonOptions.StdLibOption,
            CommonOptions.DeterministicOption,
            CommonOptions.VerbosityOption,
            CommonOptions.OutputOption,
            CommonOptions.TargetOption,
            CommonOptions.ResourceOption,
            CommonOptions.NoDebugInfoOption,
            CommonOptions.LangVersionOption,
            OptimizeOption,
        };
        command.Handler = new ILBuildCommand();

        return command;
    }

    public override int Handle(ParseResult result)
    {
        string[] userSpecifiedInputFiles = result.GetValueForArgument(CommonOptions.InputFilesArgument);
        string[] inputFiles = CommonOptions.GetInputFiles(userSpecifiedInputFiles);
        string[] defines = result.GetValueForOption(CommonOptions.DefinedSymbolsOption);
        string[] references = CommonOptions.GetReferencePaths(
            result.GetValueForOption(CommonOptions.ReferencesOption),
            result.GetValueForOption(CommonOptions.StdLibOption));

        OptimizationLevel optimizationLevel = result.GetValueForOption(OptimizeOption) ? OptimizationLevel.Release : OptimizationLevel.Debug;

        string userSpecificedOutputFileName = result.GetValueForOption(CommonOptions.OutputOption);
        string outputNameWithoutSuffix =
            userSpecificedOutputFileName != null ? Path.GetFileNameWithoutExtension(userSpecificedOutputFileName) :
            CommonOptions.GetOutputFileNameWithoutSuffix(userSpecifiedInputFiles);

        BuildTargetType buildTargetType = result.GetValueForOption(CommonOptions.TargetOption);
        bool deterministic = result.GetValueForOption(CommonOptions.DeterministicOption);
        CSharpCompilation compilation = CreateCompilation(Path.GetFileName(outputNameWithoutSuffix), inputFiles, references, defines,
            optimizationLevel,
            buildTargetType,
            deterministic,
            result.GetValueForOption(CommonOptions.LangVersionOption));

        DebugInformationFormat debugInfoFormat = result.GetValueForOption(CommonOptions.NoDebugInfoOption)
            ? 0 : DebugInformationFormat.Embedded;
        var emitOptions = new EmitOptions(debugInformationFormat: debugInfoFormat);
        string outputFileName = userSpecificedOutputFileName;
        if (outputFileName == null)
        {
            bool isLibrary = buildTargetType == 0 ?
                compilation.GetEntryPoint(CancellationToken.None) == null :
                buildTargetType == BuildTargetType.Shared;
            string suffix = isLibrary ? ".dll" : ".exe";
            outputFileName = outputNameWithoutSuffix + suffix;
        }

        var resinfos = CommonOptions.GetResourceDescriptions(result.GetValueForOption(CommonOptions.ResourceOption));

        EmitResult compResult;
        using (var fs = File.Create(outputFileName))
        {
            compResult = compilation.Emit(fs, manifestResources: resinfos, options: emitOptions);
        }

        if (!compResult.Success)
        {
            IEnumerable<Diagnostic> failures = compResult.Diagnostics.Where(diagnostic =>
                diagnostic.IsWarningAsError ||
                diagnostic.Severity == DiagnosticSeverity.Error);

            foreach (Diagnostic diagnostic in failures)
            {
                Console.Error.WriteLine(diagnostic.ToString());
            }

            try
            {
                if (File.Exists(outputFileName))
                    File.Delete(outputFileName);
            }
            catch { }

            return 1;
        }

        return 0;
    }

    public static CSharpCompilation CreateCompilation(
        string moduleName,
        string[] inputFiles,
        string[] references,
        string[] userDefines,
        OptimizationLevel optimizationLevel,
        BuildTargetType buildTargetType,
        TargetArchitecture arch,
        TargetOS os,
        string languageVersion)
    {
        List<string> defines = new List<string>(userDefines)
        {
            arch switch
            {
                TargetArchitecture.X86 => "X86",
                TargetArchitecture.X64 => "X64",
                TargetArchitecture.ARM => "ARM",
                TargetArchitecture.ARM64 => "ARM64",
#if NET10_0_OR_GREATER
                TargetArchitecture.RiscV64 => "RISCV64",
#endif
            },
            os switch
            {
                TargetOS.Windows => "WINDOWS",
                TargetOS.Linux => "LINUX",
                TargetOS.UEFI => "UEFI",
            }
        };

        return CreateCompilation(moduleName, inputFiles, references, defines.ToArray(),
            optimizationLevel,
            buildTargetType,
            deterministic: true,
            languageVersion);
    }

    private static CSharpCompilation CreateCompilation(
        string moduleName,
        string[] inputFiles,
        string[] references,
        string[] userDefines,
        OptimizationLevel optimizationLevel,
        BuildTargetType buildTargetType,
        bool deterministic,
        string languageVersion)
    {
        OutputKind? outputKind = buildTargetType switch
        {
            BuildTargetType.Shared => OutputKind.DynamicallyLinkedLibrary,
            BuildTargetType.WinExe => OutputKind.WindowsApplication,
            BuildTargetType.Exe => OutputKind.ConsoleApplication,
            _ => null,
        };

        List<string> defines = new List<string>(userDefines)
        {
            "BFLAT"
        };

        var metadataReferences = new List<MetadataReference>();
        foreach (var reference in references)
            metadataReferences.Add(MetadataReference.CreateFromFile(reference));

        if (!LanguageVersionFacts.TryParse(languageVersion, out LanguageVersion langVer))
        {
            throw new Exception($"Language version '{languageVersion}' not recognized");
        }

        var trees = new List<SyntaxTree>();
        foreach (var sourceFile in inputFiles)
        {
            var st = SourceText.From(File.OpenRead(sourceFile));
            CSharpParseOptions parseOptions = new CSharpParseOptions(
                languageVersion: langVer,
                documentationMode: DocumentationMode.None,
                preprocessorSymbols: defines);
            string path = sourceFile;
            if (!Path.IsPathRooted(sourceFile))
                path = Path.GetFullPath(sourceFile, Directory.GetCurrentDirectory());
            trees.Add(CSharpSyntaxTree.ParseText(st, parseOptions, path));
        }

        if (!outputKind.HasValue)
        {
            foreach (var tree in trees)
            {
                foreach (var descendant in tree.GetRoot().DescendantNodes())
                {
                    if (descendant is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax methodSyntax)
                    {
                        if (methodSyntax.Identifier.Text == "Main" &&
                            methodSyntax.DescendantTokens().Any(x => x.IsKind(SyntaxKind.StaticKeyword)))
                        {
                            int paramCount = methodSyntax.ParameterList.Parameters.Count;
                            if (paramCount == 0 || paramCount == 1)
                            {
                                outputKind = OutputKind.ConsoleApplication;
                            }
                        }
                    }
                    else if (descendant is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax)
                    {
                        outputKind = OutputKind.ConsoleApplication;
                    }
                }
            }
        }

        if (!outputKind.HasValue)
            outputKind = OutputKind.DynamicallyLinkedLibrary;

        var compilationOptions = new CSharpCompilationOptions(
            outputKind.Value,
            allowUnsafe: true,
            optimizationLevel: optimizationLevel,
            deterministic: deterministic,
            metadataImportOptions: MetadataImportOptions.All);
        return CSharpCompilation.Create(moduleName, trees, metadataReferences, compilationOptions);
    }
}
