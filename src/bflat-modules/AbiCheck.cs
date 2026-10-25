// bflat C# compiler
// Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
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
using System.Text;

// The float-ABI check of the riscv64 layout. The layout (the dotnet-riscv
// runtime, musl, libgcc, libatomic) is built rv64im for the lp64 soft-float
// ABI, and bflat links it as it is: ld.lld refuses to mix objects with
// different float ABIs. This check finds a wrong file when the layout is
// made, not in the first guest build.
//
// It reads the ELF header of each .o file and of each member of each .a
// file. A RISC-V ELF object must have EF_RISCV_FLOAT_ABI_SOFT. Files that
// are not RISC-V ELF (LLVM bitcode, the archive index) are skipped.
internal static class AbiCheck
{
    private const int ElfMachineOffset = 0x12;
    private const int ElfFlagsOffset = 0x30;
    private const ushort EmRiscV = 243;
    private const uint EfRiscVFloatAbi = 0x6;

    private static readonly string[] AbiNames = { "lp64 (soft)", "lp64f (single)", "lp64d (double)", "lp64q (quad)" };

    /// <summary>Returns the number of files with a float ABI that is not soft, and prints each one.</summary>
    public static int CheckDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            throw new ManifestException($"{dir}: the directory does not exist");

        int objects = 0;
        var wrong = new List<string>();
        foreach (string path in Directory.EnumerateFiles(dir))
        {
            if (path.EndsWith(".o", StringComparison.Ordinal))
                CheckObject(File.ReadAllBytes(path), Path.GetFileName(path), wrong, ref objects);
            else if (path.EndsWith(".a", StringComparison.Ordinal))
                CheckArchive(path, wrong, ref objects);
        }

        foreach (string w in wrong)
            Console.Error.WriteLine($"bflat-modules: {w}");
        if (wrong.Count == 0)
            Console.WriteLine($"{dir}: {objects} RISC-V objects, all lp64 (soft-float)");
        return wrong.Count;
    }

    private static void CheckObject(ReadOnlySpan<byte> elf, string name, List<string> wrong, ref int objects)
    {
        // ELF64, little-endian, RISC-V.
        if (elf.Length < ElfFlagsOffset + 4
            || elf[0] != 0x7f || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F'
            || elf[4] != 2 || elf[5] != 1
            || BitConverter.ToUInt16(elf.Slice(ElfMachineOffset, 2)) != EmRiscV)
            return;

        objects++;
        uint abi = (BitConverter.ToUInt32(elf.Slice(ElfFlagsOffset, 4)) & EfRiscVFloatAbi) >> 1;
        if (abi != 0)
            wrong.Add($"{name}: the float ABI is {AbiNames[abi]}, not lp64 (soft)");
    }

    // The common ar format: "!<arch>\n", then for each member a 60-byte
    // header (the size is decimal text at bytes 48..57) and the data, padded
    // to an even offset. GNU long names are in the "//" member.
    private static void CheckArchive(string path, List<string> wrong, ref int objects)
    {
        byte[] ar = File.ReadAllBytes(path);
        if (ar.Length < 8 || Encoding.ASCII.GetString(ar, 0, 8) != "!<arch>\n")
            throw new ManifestException($"{path}: not an ar archive");

        string longNames = "";
        int pos = 8;
        while (pos + 60 <= ar.Length)
        {
            string id = Encoding.ASCII.GetString(ar, pos, 16).TrimEnd();
            if (!int.TryParse(Encoding.ASCII.GetString(ar, pos + 48, 10).Trim(), out int size)
                || size < 0 || pos + 60 + size > ar.Length)
                throw new ManifestException($"{path}: a member header at offset {pos} is not valid");

            int data = pos + 60;
            if (id == "//")
                longNames = Encoding.ASCII.GetString(ar, data, size);
            else if (id != "/" && id != "/SYM64/")
                CheckObject(ar.AsSpan(data, size), $"{Path.GetFileName(path)}({MemberName(id, longNames)})", wrong, ref objects);

            pos = data + size + (size & 1);
        }
    }

    private static string MemberName(string id, string longNames)
    {
        if (id.Length > 1 && id[0] == '/' && int.TryParse(id.AsSpan(1), out int offset) && offset < longNames.Length)
        {
            int end = longNames.IndexOf('\n', offset);
            return longNames[offset..(end < 0 ? longNames.Length : end)].TrimEnd('/');
        }
        return id.TrimEnd('/');
    }
}
