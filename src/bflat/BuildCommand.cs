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
    // Self-check used by bflat's own build (see VerifyZkvmSubstitutions in
    // bflat.csproj): resolve the zkVM body substitutions against the CoreLib
    // in the layout and stop before code generation. Same code path as a real
    // build, so it cannot drift from what a guest build would resolve.
    private static Option<bool> VerifySubstitutionsOption = new Option<bool>("--verify-substitutions", "Resolve zkVM body substitutions against the target CoreLib and exit");

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

    private static Option<string> TargetLibcOption = new Option<string>("--libc", "Target libc (Windows: shcrt|none, Linux: glibc|bionic|musl|zisk|zisk_sim|sp1|openvm)");

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
            VerifySubstitutionsOption,
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
    /// Reads one options section of a module's params.yml (packed into the
    /// layout as &lt;module&gt;.params.yml) and yields the entries whose
    /// applicability conditions hold. An entry is "- &lt;kind&gt;: &lt;value&gt;"
    /// optionally followed by conditions on the same list item:
    ///   libc: zisk            (scalar)
    ///   libc: [zisk, musl]    (flow list)
    ///   arch: riscv64
    ///   os:   linux
    ///   eh:   unwind          (or failfast; see --remove-eh)
    /// Conditions are AND-ed; a missing condition does not constrain. The
    /// parser is deliberately minimal (no YAML dependency) and fails loudly
    /// on anything it does not understand.
    /// </summary>
    IEnumerable<KeyValuePair<string, string>> ReadModuleParams(string paramsPath,
        string section, string libc, TargetArchitecture arch, TargetOS os)
    {
        var entries = new List<KeyValuePair<string, string>>();
        if (!File.Exists(paramsPath))
            return entries;

        string libcName = (libc ?? "").ToLowerInvariant();
        string archName = arch.ToString().ToLowerInvariant();
        string osName = os.ToString().ToLowerInvariant();
        string ehName = _removeEh ? "failfast" : "unwind";

        string currentSection = null;
        string pendingKind = null;
        string pendingValue = null;
        bool pendingApplies = true;

        void Flush()
        {
            if (pendingKind != null && pendingApplies)
                entries.Add(new KeyValuePair<string, string>(pendingKind, pendingValue));
            pendingKind = null;
            pendingValue = null;
            pendingApplies = true;
        }

        bool ConditionHolds(string key, string val)
        {
            string actual = key switch
            {
                "libc" => libcName,
                "arch" => archName,
                "os" => osName,
                "eh" => ehName,
                _ => throw new Exception($"{paramsPath}: unknown condition '{key}'"),
            };
            foreach (string candidate in val.Trim().TrimStart('[').TrimEnd(']').Split(','))
            {
                if (candidate.Trim().ToLowerInvariant() == actual)
                    return true;
            }
            return false;
        }

        foreach (string rawLine in File.ReadAllLines(paramsPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('-'))
            {
                Flush();
                if (currentSection != section)
                    continue;
                int c = line.IndexOf(':');
                if (c <= 1)
                    throw new Exception($"{paramsPath}: unsupported entry '{line}' in section '{currentSection}'");
                pendingKind = line.Substring(1, c - 1).Trim();
                pendingValue = line.Substring(c + 1).Trim();
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
                throw new Exception($"{paramsPath}: unsupported line '{line}'");
            string k = line.Substring(0, colon).Trim();
            string rest = line.Substring(colon + 1).Trim();

            // Condition attached to the current list entry.
            if (pendingKind != null && (k == "libc" || k == "arch" || k == "os" || k == "eh"))
            {
                pendingApplies &= ConditionHolds(k, rest);
                continue;
            }

            // "ld:"/"ilc:" open a section; any "key: value" pair
            // (repo:, tag:, file:, ...) or another header closes it.
            Flush();
            currentSection = rest.Length == 0 ? k : null;
        }
        Flush();
        return entries;
    }

    /// <summary>
    /// Appends the linker options a module declares in the options.ld section
    /// of its params.yml. Every entry must be "- value: &lt;flag&gt;".
    /// </summary>
    void AppendModuleParams(StringBuilder ldArgs, string libPath, string moduleName,
        string libc, TargetArchitecture arch, TargetOS os)
    {
        string paramsPath = Path.Combine(libPath, moduleName + ".params.yml");
        foreach (var entry in ReadModuleParams(paramsPath, "ld", libc, arch, os))
        {
            if (entry.Key != "value")
                throw new Exception($"{paramsPath}: unsupported ld entry kind '{entry.Key}'");
            if (entry.Value.Length > 0)
                ldArgs.Append(entry.Value + " ");
        }
    }

    /// <summary>
    /// Collects the ILC-stage files (substitutions XML / body-substitution
    /// C# snippets) declared by any module's params.yml options.ilc section:
    ///   - substitutions: file.xml
    ///   - snippets: file.cs
    /// File paths are relative to the layout directory. Same per-entry
    /// libc/arch/os conditions as options.ld.
    /// </summary>
    (List<string> Substitutions, List<string> Snippets) CollectModuleIlcFiles(
        string libPath, string libc, TargetArchitecture arch, TargetOS os)
    {
        var substitutions = new List<string>();
        var snippets = new List<string>();
        if (!Directory.Exists(libPath))
            return (substitutions, snippets);

        foreach (string paramsPath in Directory.GetFiles(libPath, "*.params.yml").OrderBy(p => p))
        {
            foreach (var entry in ReadModuleParams(paramsPath, "ilc", libc, arch, os))
            {
                string fullPath = Path.Combine(libPath, entry.Value);
                if (!File.Exists(fullPath))
                    throw new Exception($"{paramsPath}: ilc file '{entry.Value}' not found in layout");
                switch (entry.Key)
                {
                    case "substitutions": substitutions.Add(fullPath); break;
                    case "snippets": snippets.Add(fullPath); break;
                    default:
                        throw new Exception($"{paramsPath}: unsupported ilc entry kind '{entry.Key}'");
                }
            }
        }
        return (substitutions, snippets);
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

    void PatchRiscvAbiStaticLib(string libPath, bool verbose)
    {
        if (verbose)
            Console.WriteLine($"Patching RISC-V ABI in static library (in place): {libPath}");

        if (!File.Exists(libPath))
        {
            if (verbose)
                Console.WriteLine($"Warning: {libPath} not found, skipping ABI patch");
            return;
        }

        // Patch the float-ABI marker of every ELF member in place by walking the ar
        // structure. Extract-and-repack (ar x / ar rcs) is unsafe here: musl's
        // libc.a has multiple members that share a basename (e.g. free.lo), and
        // extraction by name overwrites the earlier one on disk, silently dropping
        // its symbols (this is why "free" went missing). Rewriting bytes in place
        // preserves every member and the archive symbol index.
        using var fs = new FileStream(libPath, FileMode.Open, FileAccess.ReadWrite);

        byte[] magic = new byte[8];
        if (fs.Read(magic, 0, 8) != 8 || System.Text.Encoding.ASCII.GetString(magic) != "!<arch>\n")
        {
            if (verbose)
                Console.WriteLine($"Warning: {libPath} is not an ar archive, skipping ABI patch");
            return;
        }

        int patched = 0;
        byte[] header = new byte[60];
        while (fs.Position + 60 <= fs.Length)
        {
            if (fs.Read(header, 0, 60) != 60)
                break;

            // Member size is a decimal ASCII string at bytes 48..57.
            if (!long.TryParse(System.Text.Encoding.ASCII.GetString(header, 48, 10).Trim(), out long size))
                break;

            long dataPos = fs.Position;

            // e_flags lives at offset 0x30 of the ELF header; patch it only for ELF
            // members whose marker is hard-float (4) or hard-float+compressed (5).
            // The armap/extended-name members are not ELF and are skipped.
            if (size > 0x34)
            {
                byte[] ident = new byte[4];
                // A short read would leave `ident` holding stale bytes and the
                // magic test could then misfire on a truncated member; treat
                // "fewer than 4 bytes available" as "not an ELF member".
                if (fs.ReadAtLeast(ident, 4, throwOnEndOfStream: false) == 4 &&
                    ident[0] == 0x7f && ident[1] == (byte)'E' && ident[2] == (byte)'L' && ident[3] == (byte)'F')
                {
                    fs.Seek(dataPos + 0x30, SeekOrigin.Begin);
                    int b = fs.ReadByte();
                    if (b == 4 || b == 5)
                    {
                        fs.Seek(dataPos + 0x30, SeekOrigin.Begin);
                        fs.WriteByte(0);
                        patched++;
                    }
                }
            }

            // Advance to the next member; member data is padded to an even offset.
            long next = dataPos + size;
            if ((next & 1) == 1)
                next++;
            fs.Seek(next, SeekOrigin.Begin);
        }

        if (verbose)
            Console.WriteLine($"Patched {patched} ELF member(s) in {libPath}");
    }

    // Normalize the RISC-V float-ABI marker of every prebuilt .NET runtime blob
    // that the zisk/zisk_sim link pulls in, to soft-float (lp64). The zkVM stack
    // is linked soft-float (crt1.o/crti.o/crtn.o/libc.a are patched above), but
    // the runtime blobs (bootstrapper, WorkstationGC, PAL, minipal, ...) ship
    // with the hard-float (lp64d) marker in some blob releases, and ld.lld
    // rejects them with "different floating-point ABI from crt1.o". This flips
    // only the ELF marker byte in place (see PatchRiscvAbi / PatchRiscvAbiStaticLib);
    // it does NOT touch instructions, so it is safe only because the blobs
    // contain no hardware FP (the guest is FP-free). Idempotent and tolerant of
    // missing files.
    void PatchRiscvAbiRuntimeBlobs(string libDir, bool verbose)
    {
        string[] objects =
        {
            "libbootstrapper.o", "libbootstrapperdll.o",
        };
        string[] archives =
        {
            "libSystem.Native.a", "libatomic.a", "libeventpipe-disabled.a",
            "libaotminipal.a", "libstandalonegc-disabled.a", "libstdc++compat.a",
            "libRuntime.WorkstationGC.a", "libSystem.IO.Compression.Native.a",
            "libSystem.Security.Cryptography.Native.OpenSsl.a",
            "libSystem.Globalization.Native.a",
        };
        foreach (string o in objects)
        {
            string path = Path.Combine(libDir, o);
            if (File.Exists(path))
                PatchRiscvAbi(path);
        }
        foreach (string a in archives)
            PatchRiscvAbiStaticLib(Path.Combine(libDir, a), verbose);
    }


    /// <summary>
    /// --remove-eh: drop the unwind tables and fail fast on throw instead of
    /// dispatching. Read by the params.yml "eh:" condition, which is evaluated
    /// far from the parse result, so it is latched here.
    /// </summary>
    private bool _removeEh;

    /// <summary>
    /// The zkVM targets: OS-less guests linked from the modules in
    /// src/bflat/modules against a prover's fixed memory map instead of
    /// against a kernel. They share the entire zkVM link - the same native
    /// modules, the same soft-float musl, the same ISA gates and managed
    /// substitutions - and differ only in the entry point and linker script,
    /// which live in the target's own layout directory (see ZkvmLibPath).
    /// </summary>
    private static bool IsZkvm(string libc) =>
        libc == "zisk" || libc == "zisk_sim" || libc == "sp1" || libc == "openvm";

    /// <summary>
    /// Layout directory holding a zkVM target's own script.ld and
    /// entrypoint.o. Named after the libc, so lib/linux/riscv64/&lt;libc&gt;.
    /// Everything else the target links comes from the ZisK directory, which
    /// is where bflat.csproj lays the shared module objects down.
    /// </summary>
    private static string ZkvmLibPath(string homePath, string libc) =>
        Path.Combine(homePath, "lib", "linux", "riscv64", libc);

    public override int Handle(ParseResult result)
    {
        _removeEh = result.GetValueForOption(RemoveEhOption);
        bool nooptimize = result.GetValueForOption(DisableOptimizationOption);
        bool optimizeSpace = result.GetValueForOption(OptimizeSizeOption);
        bool optimizeTime = result.GetValueForOption(OptimizeSpeedOption);
        string homePath = CommonOptions.HomePath;
        string ziskLibPath = Path.Combine(homePath, "lib", "linux", "riscv64", "zisk");

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
        // Guest-visible compilation symbol, so user code and the module
        // snippets can tell the targets apart. zisk_sim deliberately gets
        // none, as before: it is a debugging layout, not a proof target.
        //
        // Spelled as an if-chain rather than a switch expression on purpose:
        // CA1508 mis-reads a switch expression over string constants as
        // proving the subject non-null, and then calls every later null check
        // on libc dead code.
        string zkvmDefine = null;
        if (libc == "zisk")
            zkvmDefine = "ZKVM_ZISK";
        else if (libc == "sp1")
            zkvmDefine = "ZKVM_SP1";
        else if (libc == "openvm")
            zkvmDefine = "ZKVM_OPENVM";
        if (zkvmDefine != null)
        {
            var definesList = new List<string>(defines ?? Array.Empty<string>());
            definesList.Add(zkvmDefine);
            defines = definesList.ToArray();
        }
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

        // Handle extlib resolution synchronously - after we know target arch/os/libc
        string[] extLibSpecs = result.GetValueForOption(ExtLibOption);
        List<string> downloadedLibPaths = new List<string>();
        var extLibWrapSymbols = new List<string>();
        bool verbose = result.GetValueForOption(CommonOptions.VerbosityOption);

        var referenceList = new List<string>(references ?? Array.Empty<string>());

        if (extLibSpecs != null && extLibSpecs.Length > 0)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "bflat-extlibs");

            foreach (var spec in extLibSpecs)
            {
                try
                {
                    ExtLibResolver.Result extLibResult = ExtLibResolver.Resolve(
                        spec, tempDir, verbose, targetArchitecture, targetOS, libc).GetAwaiter().GetResult();

                    if (extLibResult.StaticLibPath != null)
                    {
                        downloadedLibPaths.Add(extLibResult.StaticLibPath);

                        // Patch RISC-V ABI if needed
                        if (targetArchitecture == TargetArchitecture.RiscV64)
                            PatchRiscvAbiStaticLib(extLibResult.StaticLibPath, verbose);
                    }

                    if (extLibResult.DotnetLibPath != null)
                    {
                        referenceList.Add(extLibResult.DotnetLibPath);
                        if (verbose)
                            Console.WriteLine($"Added external dotnet reference: {extLibResult.DotnetLibPath}");
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
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error resolving external library '{spec}': {ex.Message}");
                    return 1;
                }
            }
        }

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

        CustomILProvider customIlProvider = new CustomILProvider(ilProviderOld, typeSystemContext,
            isZkvmTarget: IsZkvm(libc));
        ILProvider ilProvider = customIlProvider;

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
                var tmpLibc = libc;
                if (IsZkvm(libc))
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

        var inputFilePaths = new Dictionary<string, string>
        {
            [compiledModuleName] = compiledModulePath,
        };

        // zkVM body-substitution machinery, declared by modules in their
        // params.yml options.ilc sections (e.g. zisk_subst): substitutions XML
        // for ILC and C# snippet sources compiled against the same references,
        // with accessibility checks ignored so they can touch CoreLib
        // internals/privates; the resulting module is registered so the type
        // system resolves its methods.
        const string snippetsModuleName = "__ZiskSnippets";
        string snippetsModulePath = null;
        List<string> moduleSubstitutionFiles = new List<string>();
        if (IsZkvm(libc))
        {
            (moduleSubstitutionFiles, List<string> snippetFiles) =
                CollectModuleIlcFiles(ziskLibPath, libc, targetArchitecture, targetOS);
            // Compile against the IMPLEMENTATION assemblies (referenceFilePaths),
            // not the public ref assemblies (references): the snippet needs the
            // private nested types (Random.CompatSeedImpl, ...) that ref
            // assemblies strip out.
            if (snippetFiles.Count > 0)
                snippetsModulePath = ZkvmSubstitutions.CompileSnippets(snippetFiles, referenceFilePaths.Values.ToArray(), defines, langVersion: result.GetValueForOption(CommonOptions.LangVersionOption));
            if (snippetsModulePath != null)
                inputFilePaths[snippetsModuleName] = snippetsModulePath;
        }

        typeSystemContext.InputFilePaths = inputFilePaths;
        typeSystemContext.ReferenceFilePaths = referenceFilePaths;

        typeSystemContext.SetSystemModule(typeSystemContext.GetModuleForSimpleName(systemModuleName));

        //ilProvider.TypeContext = typeSystemContext;
        EcmaModule compiledAssembly = typeSystemContext.GetModuleForSimpleName(compiledModuleName);

        if (snippetsModulePath != null)
        {
            // Deliberately NOT wrapped in a catch. Every one of these
            // substitutions removes floating point from a body that would
            // otherwise reach an FPU-less target; "apply what we can and warn"
            // produces a binary that looks built and dies in the emulator on
            // an F/D opcode. If the snippet set no longer matches the CoreLib
            // in front of us, the honest outcome is no binary at all.
            EcmaModule snippetsModule = typeSystemContext.GetModuleForSimpleName(snippetsModuleName);
            customIlProvider.BodySubstitutions = ZkvmSubstitutions.BuildBodySubstitutions(typeSystemContext, snippetsModule);
            Console.WriteLine($"zkVM: applied {customIlProvider.BodySubstitutions.Count} C#-snippet body substitution(s)");

            if (result.GetValueForOption(VerifySubstitutionsOption))
            {
                Console.WriteLine(
                    $"zkVM: substitutions verified for libc={libc} against " +
                    $"{typeSystemContext.SystemModule.Assembly.GetName().Name}");
                return 0;
            }
        }
        else if (result.GetValueForOption(VerifySubstitutionsOption))
        {
            // Refuse to report success for a target that has no substitutions
            // to check - otherwise a typo in the verification step's arguments
            // would look like a passing check.
            Console.Error.WriteLine(
                $"Error: --verify-substitutions requires a zkVM --libc " +
                $"(zisk, zisk_sim, sp1 or openvm; got '{libc ?? "none"}')");
            return 1;
        }

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

        // Bindings packages (--extlib: libziskos, libsp1, libopenvm) declare their
        // precompiles as [DllImport("__Internal")]. Direct P/Invoke turns those
        // into ordinary calls to the statically linked symbols instead of a
        // lookup through a loader the guest does not have.
        if (IsZkvm(libc))
        {
            directPinvokes.Add("__Internal");
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

        bool disableGlobalization = result.GetValueForOption(NoGlobalizationOption) || libc == "bionic" || libc == "musl" || IsZkvm(libc);
        if (disableGlobalization)
        {
            featureSwitches.Add("System.Globalization.Invariant", true);
        }

        if (IsZkvm(libc))
        {
            // Invariant timezone (UTC only): the deterministic zkVM guest has no
            // timezone database, and the [FeatureSwitchDefinition] on
            // TimeZoneInfo.Invariant lets ILC fold and trim the timezone-data
            // loading paths (which also carry floating-point transition math).
            featureSwitches.Add("System.TimeZoneInfo.Invariant", true);
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

        // User-provided ILLink.Substitutions XML (same format and wiring as ilc's
        // --substitution): method body stubs/removals and static field value
        // substitutions, constant-folded with branch elimination before scanning,
        // so code guarded by a substituted value is trimmed from the image.
        BodyAndFieldSubstitutions substitutions = default;
        IReadOnlyDictionary<ModuleDesc, IReadOnlySet<string>> resourceBlocks = default;

        // Built-in substitutions for the zkVM targets, declared by modules
        // (zisk_subst's zisk.substitutions.xml): thread-pool tuning and similar
        // machinery that is provably dead on a single-threaded guest but drags
        // floating-point code into the rv64ima image. Applied before user
        // files; the parser rejects duplicate method entries, so user files
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
        if (optimizationMode != OptimizationMode.None)
        {
            backendOptions.Add("JitObjectStackAllocation=1");

            // zkVM RyuJIT codegen tuning, fixed into bflat. RyuJIT parses these
            // integer values as HEXADECIMAL with no 0x prefix
            // (JitConfigProvider.getIntConfigValue uses NumberStyles.AllowHexSpecifier),
            // so "2000" == 0x2000 == 8192.

            // Max stack-allocatable object size (knob default 528 / 0x210).
            // Lifting the in-loop heap restriction needs runtime patch
            // 25_stackalloc_aggressive_riscv64.patch.
            backendOptions.Add("JitObjectStackAllocationSize=2000"); // 0x2000 = 8192

            // Inlining caps, raised moderately. Stays on ExtendedDefaultPolicy
            // (weighs code growth) rather than JitAggressiveInlining, which
            // overflows the fixed ZisK ROM. Lower MaxIL if ROM overflows.
            backendOptions.Add("JitExtDefaultPolicyMaxIL=200"); // 0x200 = 512 (default 0x80 = 128) max inlinee IL
            backendOptions.Add("JitExtDefaultPolicyMaxBB=10");  // 0x10  = 16  (default 7)         max inlinee basic blocks

            // Elide RA spill/reload + frame in leaf methods. RyuJIT riscv64 uses
            // REG_RA as a hardcoded scratch for branch/compare constants, far-jump
            // targets and 64-bit mul-high, so patch 31 refuses to elide methods
            // whose LIR contains those shapes (GT_JCMP / GT_LT/LE/GT/GE / GT_MULHI)
            // or use FP. Needs runtime patches 23+31.
            //
            // zkVM targets only. Patch 23 states the precondition: sound on a
            // single-threaded deterministic guest with no GC thread suspension and
            // no return-address hijacking. A riscv64+musl/glibc process is neither,
            // so it keeps the standard prologue.
            if (IsZkvm(libc))
            {
                backendOptions.Add("RiscV64ElideLeafRaSave=1");
            }
        }

        // Lower constant-size SpanHelpers.SequenceEqual to the inline
        // `csrrs rd, 0x814, src ; addi x0, dst, count` idiom that the ZisK ROM
        // transpiler folds into one dma_xmemcmp step (patch 30).
        //
        // REAL ZISK ONLY - not the zkVM family. Nothing else understands the pair:
        // a real riscv64 CPU (so also zisk_sim, which runs under QEMU or on
        // hardware) executes a CSR access to 0x814 followed by an addi to x0, and
        // SP1 and OpenVM do not know the idiom either. The knob defaults to 0 in
        // the JIT, so every other target simply keeps the call.
        if (libc == "zisk" && optimizationMode != OptimizationMode.None)
        {
            backendOptions.Add("JitRiscV64DmaCompare=1");
        }

        // zkVM ISA gate: the ZisK proof target is rv64ima with NO compressed (C)
        // extension, and a single-threaded guest needs no hardware atomics. Turn
        // the JIT off both so the whole managed .text stays 32-bit and lock-free
        // (RyuJIT otherwise emits c.add/c.mv in switch dispatch, and lr/sc + amo*
        // for Interlocked). Only for the zkVM targets - a real riscv64+musl
        // target wants C and A. ZisK decodes rv64imafd, SP1 riscv64im and
        // OpenVM rv64im, so none of them needs either extension. Requires the
        // cross-JIT built with dotnet-riscv fixup 24/25 (EnableRiscV64Compressed
        // / EnableRiscV64Atomic); an unpatched JIT ignores unknown knobs.
        // Verified whole-image with --error-on-compressed / --error-on-atomic.
        if (IsZkvm(libc))
        {
            backendOptions.Add("EnableRiscV64Compressed=0");
            backendOptions.Add("EnableRiscV64Atomic=0");
        }

        // zkVM per-site inline allocation (dotnet-riscv fixup 26). The JIT
        // replaces the object-allocation helper call with an inline bump on a
        // cell at a FIXED address, emitted as a bare constant with no
        // relocation, and bounds-checks it against the heap floor in the cell 8
        // bytes below. The address is this knob: it MUST equal g_zk_bump_ptr in
        // the target's script.ld, and it is the only thing that turns the
        // transform on - fixup 26 defaults it to 0 and then inlines nothing, so
        // a plain riscv64+musl/glibc build is never given an absolute guest
        // address to write to. Hex, no 0x prefix (ILC's JitConfigProvider parses
        // with NumberStyles.AllowHexSpecifier); the JIT reads the low 32 bits
        // zero-extended, so a value above int.MaxValue round-trips. A JIT
        // without fixup 26 ignores the unknown knob and keeps the helper call.
        // Top of the ZisK RAM window (0xa0020000 + 0x1FFD0000 - 8). Every zkVM
        // target mirrors that map, so they all share one address: the ZisK
        // simulator by construction, SP1 because its space runs to 2^48, and
        // OpenVM since its guest memory became the full 4 GiB u32 range
        // (MEM_BITS 29 -> 32 on the develop-v2.1.0 line). While OpenVM capped
        // out at 512 MiB it needed an address of its own.
        string zkBumpAddr = IsZkvm(libc) ? "bffefff8" : null;
        if (zkBumpAddr != null)
        {
            backendOptions.Add($"JitZkBumpAddr={zkBumpAddr}");
        }

        // Unaligned access. dotnet-riscv fixup 35 (JitNoUnalignedAccess) defaults the
        // knob ON, expanding every access the front end flagged unaligned
        // (Unsafe.Read/WriteUnaligned, the `unaligned.` IL prefix, unrolled
        // cpblk/initblk) into a byte-wise lbu/slli/or - sb/srli sequence. That is only
        // needed by an executor that asserts addr % width == 0, and it costs roughly
        // a quarter of the executed instructions on a guest, so the value is always
        // passed explicitly rather than left to the knob's default.
        //
        // ZisK does not assert alignment (its emulator runs and its prover verifies
        // wide unaligned accesses), so it gets wide accesses unless
        // --no-unaligned-access asks otherwise. SP1 and OpenVM DO assert it and fail
        // the execution on a violation - SP1 raises InvalidMemoryAccess for any LH/LW/
        // LD/SH/SW/SD off its natural boundary (crates/core/executor/src/vm.rs), and
        // OpenVM's load/store chip only accepts the aligned shift amounts
        // (extensions/riscv/circuit/src/loadstore/execution.rs) - so they are always
        // built byte-wise. A JIT built without fixup 35 ignores the unknown knob.
        if (targetArchitecture == TargetArchitecture.RiscV64)
        {
            bool noUnaligned = result.GetValueForOption(NoUnalignedAccessOption)
                || libc == "sp1" || libc == "openvm";
            backendOptions.Add(noUnaligned
                ? "JitNoUnalignedAccess=1"
                : "JitNoUnalignedAccess=0");
        }

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

            if (IsZkvm(libc))
            {
                // Codegen-only: rewrite the ConcurrentUnifier growth ratio to
                // integer math AFTER scanning. Relies on RewrittenMethodIL
                // correctly forwarding GetMethodILDefinition for this shared
                // generic method (without which the generic dictionary layout
                // is corrupted). See UnifierResizeILProvider.
                ilProvider = new UnifierResizeILProvider(ilProvider);
            }

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
        string patchedFilePath = Path.ChangeExtension(outputFilePath, ".patched");

        PerfWatch compileWatch = new PerfWatch("Native compile");
        CompilationResults compilationResults = compilation.Compile(objectFilePath, ObjectDumper.Compose(dumpers));
        compileWatch.Complete();

        // --error-on-float: fail the build if any EMITTED method's (post-
        // substitution) IL still carries floating point. Scanning only the methods
        // that were actually compiled (CompiledMethodBodies) - not every method the
        // compiler merely queried - keeps preinit-folded cctors and dead generic
        // instantiations from raising false alarms. Reported by managed method name.
        if (result.GetValueForOption(ErrorOnFloatOption))
        {
            var offenders = new SortedDictionary<string, string>(StringComparer.Ordinal);
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

                if (ZkvmSubstitutions.IsKnownDeadFloatMethod(emitted))
                {
                    // The conv is real IL but sits in a block the JIT proves dead
                    // (a disabled-feature guard reached through a local, which the
                    // IL-level substitution cannot fold). Surfaced as a warning so
                    // it stays auditable rather than silently ignored.
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
                else if (libc == "musl" || IsZkvm(libc))
                {
                    ldArgs.Append("-static ");
                    if (IsZkvm(libc))
                    {
                        ldArgs.Append($"\"{ziskLibPath}/crt1.o\" ");
                        PatchRiscvAbi(ziskLibPath + "/crt1.o");
                    }
                    else
                        ldArgs.Append($"\"{firstLib}/crt1.o\" ");
                    ldArgs.Append($"\"{firstLib}/crti.o\" ");
                    if (IsZkvm(libc))
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

            if (libc != "bionic" && libc != "musl" && !IsZkvm(libc))
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
                    // The prebuilt .NET runtime blobs (bootstrapper, WorkstationGC,
                    // PAL, minipal, libatomic, ...) can ship with the hard-float
                    // (lp64d) marker; normalize them all to soft-float so ld.lld
                    // accepts them against the soft-float crt1.o.
                    if (IsZkvm(libc))
                        PatchRiscvAbiRuntimeBlobs(firstLib, verbose);
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
            if (libc != "musl" && !IsZkvm(libc))
            {
                ldArgs.Append("-lc -lgcc ");
            }

            if (libc != "bionic" && libc != "musl" && !IsZkvm(libc))
            {
                ldArgs.Append("-lrt --as-needed -lgcc_s --no-as-needed ");
                if (!result.GetValueForOption(CommonOptions.NoPthreadOption))
                    ldArgs.Append("-lpthread ");
            }
            else if (libc == "musl" || IsZkvm(libc))
            {
                /* nothread: single-hart replacements for musl's locking and
                 * thread primitives, which are where its lr/sc/amo* live. The
                 * bundled musl is built rv64ima, which is right for ZisK
                 * (rv64imafd) and wrong for SP1 and OpenVM (rv64im): SP1's
                 * loader panics on the first instruction it cannot decode, and
                 * OpenVM turns it into a trap.
                 *
                 * This works by DEFINITION, not by --wrap: a wrap would leave
                 * musl's members - and their atomics - in the image. So the
                 * object MUST precede libc.a here. The linker extracts an
                 * archive member only to resolve a symbol that is still
                 * undefined at the point it reaches the archive, so with these
                 * definitions already in hand it never takes musl's.
                 *
                 * ZisK is deliberately excluded and keeps musl's real
                 * primitives: its decoder is happy with the A extension. */
                if (libc == "sp1" || libc == "openvm")
                    ldArgs.Append($"\"{Path.Combine(ziskLibPath, "nothread.o")}\" ");
                ldArgs.Append($"\"{firstLib}/libc.a\" ");
                // The zkVM stack is linked with the soft-float (lp64) ABI
                // marker (see PatchRiscvAbi on crt1.o/crti.o above). The
                // bundled musl libc.a still carries the hard-float (lp64d)
                // marker, so normalize it too or ld.lld rejects every member
                // with "different floating-point ABI from crt1.o".
                if (IsZkvm(libc))
                    PatchRiscvAbiStaticLib(firstLib + "/libc.a", verbose);
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
            else if (libc == "musl" || IsZkvm(libc))
            {
                ldArgs.Append($"\"{firstLib}/crtn.o\" ");
                // Same soft-float marker normalization as crt1.o/crti.o.
                if (IsZkvm(libc))
                    PatchRiscvAbi(firstLib + "/crtn.o");
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

            // Add --wrap flags for symbols requested by external libraries
            foreach (var sym in extLibWrapSymbols)
            {
                ldArgs.Append($"--wrap={sym} ");
            }

            if (libc == "musl")
            {
                /* hack, no fp must be built properly */
                ldArgs.Append($"\"{Path.Combine(firstLib, "nofp.o")}\" ");
            }


            if (IsZkvm(libc))
            {
                /* Entry point + linker script for the target being built;
                 * everything below comes from the shared ZisK directory. */
                string zkvmLibPath = ZkvmLibPath(homePath, libc);
                /* Memory map: one linker script per zkVM target, each
                 * describing that prover's fixed address space. */
                ldArgs.Append($"-T\"{Path.Combine(zkvmLibPath, "script.ld")}\" ");
                /* Entry point. zisk_sim reuses ZisK's - it differs only in
                 * the memory map - while every other target brings its own,
                 * because _start has to speak that VM's halt protocol. */
                string entryLibPath = libc == "zisk_sim" ? ziskLibPath : zkvmLibPath;
                ldArgs.Append($"\"{Path.Combine(entryLibPath, "entrypoint.o")}\" ");
                /* nofp: FP trap stubs; the math-symbol wrap surface is
                 * declared in nofp.params.yml. (The musl target links the
                 * object without these wraps.) */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "nofp.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "nofp", libc, targetArchitecture, targetOS);
                ldArgs.Append($"--whole-archive ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "ubootstrap.o")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "stdcppshim.o")}\" ");
                /* rhp: the wrap surface is declared in the module's
                 * module_params.yml (packed as rhp.params.yml). What is NOT
                 * wrapped anymore, and why the originals work on .NET 10 +
                 * uGC alloc-context budgets: allocation helpers (upstream
                 * riscv64 AllocFast.S + GcAllocInternal), thread statics
                 * (plain TLS field + managed jagged arrays), the Lock family
                 * incl. DeadlockAwareAcquire (truthful IsHeldByCurrentThread
                 * breaks recursive cctor cycles), CheckCastAny, cgroup
                 * initializers, GetDefaultLocaleName, and friends. */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rhp.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "rhp", libc, targetArchitecture, targetOS);


                /* gs_cookie */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "gs_cookie.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "gs_cookie", libc, targetArchitecture, targetOS);

                /* rhp_native: write barriers reduced to the bare store. */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rhp_native.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "rhp_native", libc, targetArchitecture, targetOS);

                /* pal */
                /* pal: syscall surface, bump allocator, FP-free printf/scanf
                 * and the zisk exit protocol. The wrap surface is declared in
                 * the module's module_params.yml (packed as pal.params.yml). */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "pal.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "pal", libc, targetArchitecture, targetOS);

                /* eh: synthetic program headers so libunwind can find
                 * .eh_frame_hdr in an image no loader ever mapped. Dropped,
                 * with the unwind tables themselves, under --remove-eh. */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "eh.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "eh", libc, targetArchitecture, targetOS);

                /* tls */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "tls.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "tls", libc, targetArchitecture, targetOS);
                ldArgs.Append($"--no-whole-archive ");

                /* rng */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rng_stupid.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "rng_stupid", libc, targetArchitecture, targetOS);

                /* rust_sys */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "rust_sys.o")}\" ");
                AppendModuleParams(ldArgs, ziskLibPath, "rust_sys", libc, targetArchitecture, targetOS);

                /* ugc */
                AppendModuleParams(ldArgs, ziskLibPath, "ugc-zero", libc, targetArchitecture, targetOS);
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGC.cpp.obj")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGCHandleManager.cpp.obj")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGCHandleStore.cpp.obj")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "uGCHeap.cpp.obj")}\" ");
                /* uGC v1.0.7+: the C++ wrappers delegate to the formally
                 * verified C core shipped as separate objects. */
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "ugc_core.c.obj")}\" ");
                ldArgs.Append($"\"{Path.Combine(ziskLibPath, "ugc_zalloc.c.obj")}\" ");
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

        if (libc == "zisk" && exitCode == 0)
        {
            /* patch_elf's --remove-eh drops .eh_frame/.eh_frame_hdr/
             * .dotnet_eh_table - exactly what the unwinder reads. Keep them
             * unless the build asked to trade exception handling for size. */
            var patchElfArgs = _removeEh
                ? " --fix-init-array --fix-tdata --remove-eh --trim-bss "
                : " --fix-init-array --fix-tdata --trim-bss ";
            if (verbose)
                patchElfArgs += "--print-fn-boundaries ";

            int patchExitCode = RunCommand(patchElfPath,
                outputFilePath + " " + patchedFilePath +
                patchElfArgs,
                printCommands);
        }

        // Exact whole-image ISA verification: decode the linked binary and fail
        // the build if it carries an instruction class the target cannot run.
        // patch_elf (zisk) does not touch .text, so outputFilePath's code is the
        // same the guest executes.
        if (exitCode == 0
            && (result.GetValueForOption(ErrorOnFloatBinaryOption)
                || result.GetValueForOption(ErrorOnCompressedOption)
                || result.GetValueForOption(ErrorOnAtomicOption)))
        {
            int isaRc = IsaVerifier.Verify(outputFilePath,
                result.GetValueForOption(ErrorOnFloatBinaryOption),
                result.GetValueForOption(ErrorOnCompressedOption),
                result.GetValueForOption(ErrorOnAtomicOption));
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
