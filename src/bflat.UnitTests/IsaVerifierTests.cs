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

public class IsaVerifierTests
{
    private const uint Addi = 0x00000013;       // addi x0, x0, 0
    private const uint AmoAddW = 0x0000202F;    // amoadd.w x0, x0, (x0)
    private const uint Flw = 0x00002007;        // flw f0, 0(x0)
    private const ushort CNop = 0x0001;         // c.nop

    /// <summary>
    /// A little-endian RISC-V executable of the given class with one .text
    /// section at 0x1000 holding <paramref name="code"/>, and a symbol table
    /// whose one function covers the first <paramref name="funcSize"/> bytes:
    /// the rest of .text is data, as NativeAOT interleaves it.
    /// </summary>
    private static byte[] Image(bool is64, byte[] code, uint funcSize)
    {
        const ulong textAddr = 0x1000;
        int ehdr = is64 ? 64 : 52, shdr = is64 ? 64 : 40, sym = is64 ? 24 : 16;
        byte[] strtab = Encoding.ASCII.GetBytes("\0f\0");
        byte[] shstrtab = Encoding.ASCII.GetBytes("\0.text\0.symtab\0.strtab\0.shstrtab\0");
        var symtab = new byte[2 * sym];
        Span<byte> f = symtab.AsSpan(sym);
        BinaryPrimitives.WriteUInt32LittleEndian(f, 1);
        if (is64)
        {
            f[4] = 0x12; // STB_GLOBAL, STT_FUNC
            BinaryPrimitives.WriteUInt16LittleEndian(f.Slice(6), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(f.Slice(8), textAddr);
            BinaryPrimitives.WriteUInt64LittleEndian(f.Slice(16), funcSize);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(4), (uint)textAddr);
            BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(8), funcSize);
            f[12] = 0x12;
            BinaryPrimitives.WriteUInt16LittleEndian(f.Slice(14), 1);
        }

        var body = new List<byte>();
        int Put(byte[] data) { int at = ehdr + body.Count; body.AddRange(data); return at; }
        int text = Put(code), symOff = Put(symtab), strOff = Put(strtab), shstrOff = Put(shstrtab);
        int shoff = ehdr + body.Count;
        var elf = new byte[shoff + 5 * shdr];
        body.CopyTo(elf, ehdr);

        elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = (byte)(is64 ? 2 : 1); elf[5] = 1; elf[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x10), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x12), 243);
        if (is64)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(40), (ulong)shoff);
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(58), (ushort)shdr);
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(60), 5);
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(62), 4);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(32), (uint)shoff);
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(46), (ushort)shdr);
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(48), 5);
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(50), 4);
        }

        // Section headers: null, .text, .symtab, .strtab, .shstrtab.
        void Section(int i, uint name, uint type, ulong flags, ulong addr, int off, int size, uint link, int entsize)
        {
            Span<byte> h = elf.AsSpan(shoff + i * shdr);
            BinaryPrimitives.WriteUInt32LittleEndian(h, name);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), type);
            if (is64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(8), flags);
                BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(16), addr);
                BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(24), (ulong)off);
                BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(32), (ulong)size);
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(40), link);
                BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(56), (ulong)entsize);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(8), (uint)flags);
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(12), (uint)addr);
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(16), (uint)off);
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(20), (uint)size);
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(24), link);
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(36), (uint)entsize);
            }
        }
        Section(1, 1, 1, 0x6, textAddr, text, code.Length, 0, 0);
        Section(2, 7, 2, 0, 0, symOff, symtab.Length, 3, sym);
        Section(3, 15, 3, 0, 0, strOff, strtab.Length, 0, 0);
        Section(4, 23, 3, 0, 0, shstrOff, shstrtab.Length, 0, 0);
        return elf;
    }

    private static byte[] Code(params object[] parcels)
    {
        var code = new List<byte>();
        foreach (object p in parcels)
        {
            if (p is uint w)
                code.AddRange(BitConverter.GetBytes(w));
            else
                code.AddRange(BitConverter.GetBytes((ushort)p));
        }
        return code.ToArray();
    }

    private static int Verify(byte[] image)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, image);
            return IsaVerifier.Verify(path, checkFloat: true, checkCompressed: true, checkAtomic: true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassesBaseInstructions(bool is64)
    {
        Assert.Equal(0, Verify(Image(is64, Code(Addi, Addi), 8)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsAtomicFloatAndCompressed(bool is64)
    {
        Assert.Equal(1, Verify(Image(is64, Code(Addi, AmoAddW), 8)));
        Assert.Equal(1, Verify(Image(is64, Code(Addi, Flw), 8)));
        Assert.Equal(1, Verify(Image(is64, Code(Addi, CNop, CNop), 8)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SkipsDataAfterTheFunction(bool is64)
    {
        // An atomic encoding in .text past the end of the function symbol is data.
        Assert.Equal(0, Verify(Image(is64, Code(Addi, Addi, AmoAddW), 8)));
    }
}
