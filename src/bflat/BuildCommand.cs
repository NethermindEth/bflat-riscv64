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
using System.CommandLine.Parsing;
using System.Runtime.InteropServices;

using System.IO;
using System.Linq;

using ILCompiler;

using Internal.TypeSystem;

internal partial class BuildCommand : CommandBase
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
        ArgumentHelpName = "x86|x64|arm64|riscv64|riscv32"
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

    /// <summary>
    /// The libcs of the stock NativeAOT targets. Any other --libc value must
    /// be the alias of a platform (docs/manifest.md): a zkVM target, whose
    /// manifest ships in its --extlib package.
    /// </summary>
    private static readonly string[] StockLibcs = { "glibc", "musl", "bionic", "shcrt", "none" };

    /// <summary>
    /// RiscV32, resolved by name: only compiler packages built with the
    /// riscv32 target have it (the dotnet-riscv riscv32 fixups), and bflat
    /// still builds against those that do not.
    /// </summary>
    internal static readonly TargetArchitecture? RiscV32 =
        Enum.TryParse("RiscV32", out TargetArchitecture riscV32) ? riscV32 : null;

    internal static bool IsRiscV32(TargetArchitecture architecture) => architecture == RiscV32;

    /// <summary>riscv64 or riscv32: the RISC-V targets, whose layouts are soft-float.</summary>
    internal static bool IsRiscV(TargetArchitecture architecture) =>
        architecture == TargetArchitecture.RiscV64 || IsRiscV32(architecture);

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

    public override int Handle(ParseResult result) => new Build(result).Run();

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
