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

using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;

internal static class CommonOptions
{
    public static Option<string[]> ReferencesOption =
        new Option<string[]>(new string[] { "-r", "--reference" },
            "Additional .NET assemblies to include")
        {
            ArgumentHelpName = "file list"
        };

    public static Option<bool> VerbosityOption =
        new Option<bool>("--verbose",
            "Enable verbose logging");

    public static Option<StandardLibType> StdLibOption =
        new Option<StandardLibType>("--stdlib",
            "C# standard library to use");

    public static Option<bool> DeterministicOption =
        new Option<bool>("--deterministic",
            "Produce deterministic outputs including timestamps");

    public static Option<bool> NoPthreadOption =
        new Option<bool>("--no-pthread",
            "Remove pthread dependency");

    public static Option<string> OutputOption =
        new Option<string>(new string[] { "-o", "--out" },
            "Output file path")
        {
            ArgumentHelpName = "file"
        };

    public static Option<string[]> DefinedSymbolsOption =
        new Option<string[]>(new string[] { "-d", "--define" },
            "Define conditional compilation symbol(s)");

    public static Option<string[]> ExtraLd =
        new Option<string[]>(new string[] { "--extra-ld" },
            "Extra ld inputs/libraries");

    public static Option<string[]> ResourceOption =
    new Option<string[]>(new string[] { "-res", "--resource" },
        "Managed resource to include")
    {
        ArgumentHelpName = "<file>[,<name>[,public|private]]",
    };


    public static Option<BuildTargetType> TargetOption =
        new Option<BuildTargetType>("--target",
            "Build target");

    public static Argument<string[]> InputFilesArgument = new Argument<string[]>() { HelpName = "file list" };

    public static Option<bool> NoDebugInfoOption = new Option<bool>("--no-debug-info", "Disable generation of debug information");

    public static Option<string> LangVersionOption =
        new Option<string>("--langversion",
            "C# language version ('latest', 'default', 'latestmajor', 'preview', or version like '6' or '7.1'");

    public static string GetOutputFileNameWithoutSuffix(string[] inputFileNames)
    {
        string outputFileName;
        if (inputFileNames.Length == 0)
            outputFileName = Path.GetFileName(Directory.GetCurrentDirectory());
        else
            outputFileName = Path.GetFileNameWithoutExtension(inputFileNames[0]);

        outputFileName = Path.Combine(Directory.GetCurrentDirectory(), outputFileName);

        return outputFileName;
    }

    public static string HomePath { get; } = Environment.GetEnvironmentVariable("BFLAT_HOME") ?? AppContext.BaseDirectory;

    public static string[] GetInputFiles(string[] inputFileNames)
    {
        if (inputFileNames.Length > 0)
            return inputFileNames;

        var files = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.cs", SearchOption.AllDirectories);
        var result = new List<string>();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (!fileName.StartsWith("._"))
                result.Add(file);
        }

        return result.ToArray();
    }

    public static string[] GetReferencePaths(string[] referencePaths, StandardLibType stdlib)
    {
        if (stdlib == StandardLibType.None)
            return referencePaths;

        List<string> result = new List<string>(referencePaths);
        string refPath = Path.Combine(HomePath, "ref");
        if (stdlib == StandardLibType.Zero)
        {
            result.Add(Path.Combine(refPath, "zerolib.dll"));
        }
        else
        {
            foreach (var f in Directory.GetFiles(refPath, "*.dll"))
            {
                if (f.EndsWith("zerolib.dll"))
                    continue;
                if (f.EndsWith("KernelTraceControl.dll"))
                    continue;
                if (f.EndsWith("KernelTraceControl.Win61.dll"))
                    continue;
                if (f.EndsWith("msdia140.dll"))
                    continue;
                result.Add(f);
            }
        }
        return result.ToArray();
    }

    public static IEnumerable<ResourceDescription> GetResourceDescriptions(string[] resinfos)
    {
        foreach (var resinfo in resinfos)
        {
            var components = resinfo.Split(',');
            string fileName = components[0];
            string name = Path.GetFileName(fileName);
            if (components.Length > 1)
            {
                name = components[1];
            }
            bool pub = true;
            if (components.Length > 2)
            {
                pub = components[2] != "private";
            }

            yield return new ResourceDescription(name, () => File.OpenRead(fileName), pub);
        }
    }
}

public enum BuildTargetType
{
    Exe = 1,
    WinExe,
    Shared,
}

public enum StandardLibType
{
    DotNet,
    None,
    Zero,
}

public enum TargetOS
{
    Unknown,
    Windows,
    Linux,
    UEFI,
}
