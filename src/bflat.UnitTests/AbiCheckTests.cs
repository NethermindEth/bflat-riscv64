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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Xunit;

namespace Bflat.UnitTests;

public class AbiCheckTests
{
    private const uint Lp64 = 0x0;
    private const uint Lp64Rvc = 0x1;
    private const uint Lp64d = 0x4;

    /// <summary>The 64-byte header of an ELF64 little-endian relocatable object.</summary>
    private static byte[] Elf(ushort machine, uint flags)
    {
        var elf = new byte[64];
        elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 2; elf[5] = 1; elf[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x12), machine);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x30), flags);
        return elf;
    }

    private static byte[] RiscV(uint flags) => Elf(243, flags);

    /// <summary>The 52-byte header of an ELF32 little-endian RISC-V relocatable object (riscv32).</summary>
    private static byte[] RiscV32(uint flags)
    {
        var elf = new byte[52];
        elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 1; elf[5] = 1; elf[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x12), 243);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x24), flags);
        return elf;
    }

    /// <summary>An ar archive with a GNU long-name table, as GNU ar writes it.</summary>
    private static byte[] Archive(params (string Name, byte[] Data)[] members)
    {
        var ar = new MemoryStream();
        void Header(string id, int size)
        {
            string h = id.PadRight(16) + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "644".PadRight(8) + size.ToString().PadRight(10) + "`\n";
            ar.Write(Encoding.ASCII.GetBytes(h));
        }
        void Data(byte[] data)
        {
            ar.Write(data);
            if (data.Length % 2 == 1)
                ar.WriteByte((byte)'\n');
        }

        ar.Write(Encoding.ASCII.GetBytes("!<arch>\n"));
        Header("/", 4);
        Data(new byte[4]);
        var longNames = new StringBuilder();
        var ids = new List<string>();
        foreach ((string name, byte[] _) in members)
        {
            if (name.Length < 15)
            {
                ids.Add(name + "/");
                continue;
            }
            ids.Add("/" + longNames.Length);
            longNames.Append(name).Append("/\n");
        }
        if (longNames.Length > 0)
        {
            byte[] table = Encoding.ASCII.GetBytes(longNames.ToString());
            Header("//", table.Length);
            Data(table);
        }
        for (int i = 0; i < members.Length; i++)
        {
            Header(ids[i], members[i].Data.Length);
            Data(members[i].Data);
        }
        return ar.ToArray();
    }

    [Fact]
    public void ASoftFloatLayoutPasses()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "crt1.o"), RiscV(Lp64));
        File.WriteAllBytes(Path.Combine(dir.Path, "libc.a"), Archive(("a.lo", RiscV(Lp64)), ("compressed_member.lo", RiscV(Lp64Rvc))));
        File.WriteAllBytes(Path.Combine(dir.Path, "README.txt"), new byte[] { 1 });

        Assert.Equal(0, AbiCheck.CheckDirectory(dir.Path));
    }

    [Fact]
    public void ADoubleFloatObjectFails()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "crt1.o"), RiscV(Lp64d));
        File.WriteAllBytes(Path.Combine(dir.Path, "libgc.a"), Archive(
            ("ok.o", RiscV(Lp64)), ("a_long_member_name.o", RiscV(Lp64d)), ("bitcode.o", Encoding.ASCII.GetBytes("BC\xc0\xde"))));

        Assert.Equal(2, AbiCheck.CheckDirectory(dir.Path));
    }

    [Fact]
    public void AnIlp32LayoutPasses()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "crt1.o"), RiscV32(Lp64));
        File.WriteAllBytes(Path.Combine(dir.Path, "libc.a"), Archive(("a.lo", RiscV32(Lp64)), ("b.lo", RiscV32(Lp64))));

        Assert.Equal(0, AbiCheck.CheckDirectory(dir.Path));
    }

    [Fact]
    public void AnIlp32dObjectFails()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "crt1.o"), RiscV32(Lp64d));

        Assert.Equal(1, AbiCheck.CheckDirectory(dir.Path));
    }

    [Fact]
    public void Lp64AndIlp32ObjectsInOneLayoutFail()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "crt1.o"), RiscV32(Lp64));
        File.WriteAllBytes(Path.Combine(dir.Path, "libc.a"), Archive(("a.lo", RiscV(Lp64))));

        Assert.Equal(1, AbiCheck.CheckDirectory(dir.Path));
    }

    [Fact]
    public void AnObjectOfAnotherMachineIsSkipped()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "x64.o"), Elf(62, Lp64d));

        Assert.Equal(0, AbiCheck.CheckDirectory(dir.Path));
    }

    [Fact]
    public void ABrokenArchiveIsAnError()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "bad.a"), Encoding.ASCII.GetBytes("!<arch>\n" + "x.o/".PadRight(48) + "999999".PadRight(10) + "`\n"));

        Assert.Throws<ManifestException>(() => AbiCheck.CheckDirectory(dir.Path));
    }
}
