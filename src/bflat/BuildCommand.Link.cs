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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using System.IO;

using Internal.TypeSystem;

internal partial class BuildCommand
{
    private sealed partial class Build
    {
        /// <summary>The lld-link arguments for a Windows or UEFI target.</summary>
        private void WriteWindowsLinkCommand(StringBuilder ldArgs)
        {
            ldArgs.Append("-flavor link \"");
            ldArgs.Append(objectFilePath);
            ldArgs.Append("\" ");
            ldArgs.AppendFormat("/out:\"{0}\" ", outputFilePath);
            if (result.GetValueForOption(CommonOptions.DeterministicOption))
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

        /// <summary>The ld.lld arguments for a Linux target, with the platform link in its place.</summary>
        private void WriteLinuxLinkCommand(StringBuilder ldArgs)
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
                    ldArgs.Append("-lstdc++compat -lRuntime.WorkstationGC -lSystem.IO.Compression.Native ");
                    // The riscv32 runtime is built without OpenSSL and ICU
                    // (dotnet-riscv's 12_build_runtime_riscv32.sh), so its layout
                    // has neither library; every other layout has both.
                    if (!IsRiscV32(targetArchitecture))
                        ldArgs.Append("-lSystem.Security.Cryptography.Native.OpenSsl ");
                    // The vectorized sort of the GC, which the x64 runtime
                    // calls (as Microsoft.NETCore.Native.Unix.targets links it).
                    if (targetArchitecture == TargetArchitecture.X64)
                        ldArgs.Append("-lRuntime.VxsortEnabled ");
                    if (libc != "bionic" && !IsRiscV32(targetArchitecture))
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
                {
                    // There is no riscv32 GCC: the riscv32 layout has the
                    // compiler-rt builtins (dotnet-riscv's build_builtins_rv32im.sh).
                    string builtins = IsRiscV32(targetArchitecture) ? "libclang_rt.builtins-riscv32.a" : "libgcc.a";
                    ldArgs.Append($"\"{firstLib}/{builtins}\" ");
                }
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

        /// <summary>Checks the wraps, runs the linker and the post-link steps, checks the image, and returns the exit code.</summary>
        private int Link(StringBuilder ldArgs)
        {
            bool printCommands = result.GetValueForOption(PrintCommandsOption);

            string ld = Environment.GetEnvironmentVariable("BFLAT_LD");
            if (ld == null)
            {
                string toolSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

                ld = Path.Combine(homePath, "bin", "lld" + toolSuffix);
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

            string patchElfPath = Path.Combine(homePath, "patch_elf.py");

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
                    if (tool == "flat_image")
                    {
                        int imageExitCode = WriteFlatImage(toolArgs, printCommands);
                        if (imageExitCode != 0)
                        {
                            Console.Error.WriteLine($"error: {tool} {string.Join(" ", toolArgs)} failed for the {platform.Name} image");
                            exitCode = imageExitCode;
                            break;
                        }
                        continue;
                    }

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

        /// <summary>
        /// The flat_image post-link tool: writes <c>&lt;output&gt;&lt;suffix&gt;</c>, the raw
        /// image of the output (llvm-objcopy -O binary), of the named sections
        /// only when <c>--section</c> gives any, padded with zeros to a multiple
        /// of four bytes, as a zkVM that loads it word by word reads it. The
        /// arguments: <c>--suffix &lt;suffix&gt;</c> and <c>--section &lt;name&gt;</c>, which
        /// can repeat.
        /// </summary>
        private int WriteFlatImage(List<string> toolArgs, bool printCommands)
        {
            string suffix = null;
            var sections = new StringBuilder();
            for (int i = 0; i < toolArgs.Count; i++)
            {
                if (toolArgs[i] == "--suffix" && i + 1 < toolArgs.Count)
                    suffix = toolArgs[++i];
                else if (toolArgs[i] == "--section" && i + 1 < toolArgs.Count)
                    sections.Append($"-j {toolArgs[++i]} ");
                else
                {
                    Console.Error.WriteLine($"error: flat_image: unknown argument '{toolArgs[i]}'");
                    return 1;
                }
            }
            if (string.IsNullOrEmpty(suffix) || suffix.Contains('/') || suffix.Contains('\\'))
            {
                Console.Error.WriteLine("error: flat_image needs --suffix <suffix>, a file name suffix");
                return 1;
            }

            string objcopy = Environment.GetEnvironmentVariable("BFLAT_OBJCOPY")
                ?? Path.Combine(homePath, "bin", "llvm-objcopy" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
            string imagePath = outputFilePath + suffix;
            int exitCode = RunCommand(objcopy, $"-O binary {sections}\"{outputFilePath}\" \"{imagePath}\"", printCommands);
            if (exitCode != 0)
                return exitCode;

            long length = new FileInfo(imagePath).Length;
            if (length % 4 != 0)
            {
                using FileStream image = new FileStream(imagePath, FileMode.Append);
                image.Write(new byte[4 - length % 4]);
            }
            return 0;
        }

        private static int RunCommand(string command, string args, bool print)
        {
            if (print)
            {
                Console.WriteLine($"{command} {args}");
            }

            var p = Process.Start(command, args);
            p.WaitForExit();
            return p.ExitCode;
        }
    }
}
