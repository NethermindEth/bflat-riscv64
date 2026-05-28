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

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Text;

// Bakes a ziskemu memory snapshot into a Zisk guest ELF so it restores warm at
// startup instead of cold-booting. The guest must have been built with the
// snapshot restore trampoline (restore.S, ELF entry = _zkvm_restore).
//
// The Zisk loader populates memory from ELF *section* headers, so:
//   1. The captured register file (pc + x0..x31) is written into the small
//      __zkvm_snapshot blob in .rodata; the restore.S trampoline reloads it.
//   2. The captured non-zero RAM pages are appended as fresh writable sections,
//      which the Zisk loader places into RAM at load time. The guest's original
//      writable sections are neutralised (SHF_ALLOC cleared) so their cold
//      values do not override the warm image.
//
// This is the C# port of the standalone rebake.py.
internal class RebakeCommand : CommandBase
{
    private RebakeCommand() { }

    // Magic written by ziskemu's dump_snapshot (snapshot file header).
    private const ulong SnapMagicFile = 0x5041_4E53_4B5A_3256;
    // Magic the restore.S trampoline checks in __zkvm_snapshot ("ZKSP").
    private const uint TrampolineMagic = 0x5A4B5350;
    private const string SnapshotSymbol = "__zkvm_snapshot";
    private const int BlobReserved = 4096;   // restore.S .zero size
    private const int Page = 4096;
    private const ulong RamLo = 0xA0020000;  // guest RAM start (zkvm_zisk script.ld)
    private const ulong RamHi = 0xC0000000;
    private const ulong ShfWrite = 0x1, ShfAlloc = 0x2;
    private const uint ShtProgbits = 1, ShtSymtab = 2;

    private static readonly Argument<string> GuestArgument =
        new Argument<string>("guest-elf")
        { Description = "Guest ELF built with the restore trampoline (entry = _zkvm_restore)." };
    private static readonly Argument<string> SnapshotArgument =
        new Argument<string>("snapshot")
        { Description = "Snapshot captured with ziskemu --snapshot-pc / --snapshot-out." };
    private static readonly Argument<string> OutputArgument =
        new Argument<string>("output-elf")
        { Description = "Path to write the re-baked preinit guest." };

    public static Command Create()
    {
        var command = new Command("rebake",
            "Bakes a ziskemu memory snapshot into a guest ELF so it restores warm at startup")
        {
            GuestArgument,
            SnapshotArgument,
            OutputArgument,
        };
        command.Handler = new RebakeCommand();
        return command;
    }

    private sealed class Section
    {
        public uint Type;
        public ulong Flags, Addr, Offset, Size, Entsize;
        public uint Link;
    }

    private sealed class Run
    {
        public ulong Start;
        public readonly List<byte> Data = new List<byte>();
    }

    public override int Handle(ParseResult result)
    {
        string guestPath = result.GetValueForArgument(GuestArgument);
        string snapshotPath = result.GetValueForArgument(SnapshotArgument);
        string outputPath = result.GetValueForArgument(OutputArgument);

        byte[] snap = File.ReadAllBytes(snapshotPath);
        ParseSnapshot(snap, out ulong pc, out ulong[] regs, out List<Run> runs);

        byte[] elf = File.ReadAllBytes(guestPath);
        List<Section> sections = ReadSectionTable(elf, out int eShoff, out int eShentsize, out int eShnum);

        // 1. write the register blob into __zkvm_snapshot
        ulong blobVaddr = FindSymbolVaddr(elf, sections, SnapshotSymbol);
        int blobOffset = VaddrToFileOffset(sections, blobVaddr);
        byte[] blob = BuildBlob(pc, regs);
        if (blob.Length > BlobReserved)
            throw new Exception($"rebake: register blob {blob.Length} exceeds reserved {BlobReserved}");
        Array.Copy(blob, 0, elf, blobOffset, blob.Length);

        // 2. neutralise the guest's own writable-in-RAM sections so their cold
        //    values do not override the warm image
        int neutralised = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            Section s = sections[i];
            if ((s.Flags & ShfAlloc) != 0 && (s.Flags & ShfWrite) != 0 &&
                s.Addr >= RamLo && s.Addr < RamHi)
            {
                int hdr = eShoff + i * eShentsize;
                BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(hdr + 8), s.Flags & ~ShfAlloc);
                neutralised++;
            }
        }

        // 3. append the warm runs as new writable PROGBITS sections, then a new
        //    section header table (original entries + one per run)
        using var outp = new MemoryStream();
        outp.Write(elf, 0, elf.Length);

        var runOffsets = new List<long>();
        long liveBytes = 0;
        foreach (Run run in runs)
        {
            Pad8(outp);
            runOffsets.Add(outp.Position);
            byte[] data = run.Data.ToArray();
            outp.Write(data, 0, data.Length);
            liveBytes += data.Length;
        }
        Pad8(outp);
        long newShoff = outp.Position;

        // original section headers (carrying the neutralised flags) ...
        outp.Write(elf, eShoff, eShnum * eShentsize);
        // ... plus one PROGBITS entry per warm run
        for (int r = 0; r < runs.Count; r++)
        {
            byte[] hdr = PackSectionHeader(runs[r].Start, (ulong)runOffsets[r], (ulong)runs[r].Data.Count);
            outp.Write(hdr, 0, hdr.Length);
        }

        byte[] outBytes = outp.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(outBytes.AsSpan(0x28), (ulong)newShoff);            // e_shoff
        BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(0x3C), (ushort)(eShnum + runs.Count)); // e_shnum
        File.WriteAllBytes(outputPath, outBytes);

        long span = runs.Count == 0 ? 0
            : (long)(runs[runs.Count - 1].Start + (ulong)runs[runs.Count - 1].Data.Count - runs[0].Start);
        Console.WriteLine($"rebake: {SnapshotSymbol} @ 0x{blobVaddr:x}, baked pc=0x{pc:x}");
        Console.WriteLine($"rebake: neutralised {neutralised} cold sections; " +
                          $"{runs.Count} warm sections, {liveBytes / 1024} KiB live (span {span / 1024} KiB)");
        Console.WriteLine($"rebake: -> {outputPath}");
        return 0;
    }

    private static void ParseSnapshot(byte[] d, out ulong pc, out ulong[] regs, out List<Run> runs)
    {
        if (d.Length < 336 || RdU64(d, 0) != SnapMagicFile)
            throw new Exception("rebake: not a ziskemu snapshot (bad magic)");
        if (RdU64(d, 8) != 2)
            throw new Exception($"rebake: unsupported snapshot version {RdU64(d, 8)}");

        pc = RdU64(d, 16);
        regs = new ulong[32];
        for (int i = 0; i < 32; i++)
            regs[i] = RdU64(d, 56 + i * 8);

        ulong pageSize = RdU64(d, 320);
        ulong nPages = RdU64(d, 328);
        if (pageSize != Page)
            throw new Exception($"rebake: snapshot page size {pageSize} != {Page}");
        int rec = 8 + Page;
        if (d.Length < 336L + (long)nPages * rec)
            throw new Exception("rebake: snapshot page data truncated");

        // collect (addr, file offset of the page bytes), sorted by address
        var pages = new List<KeyValuePair<ulong, int>>();
        for (ulong i = 0; i < nPages; i++)
        {
            int b = 336 + (int)i * rec;
            pages.Add(new KeyValuePair<ulong, int>(RdU64(d, b), b + 8));
        }
        pages.Sort((a, b) => a.Key.CompareTo(b.Key));

        // group page-contiguous runs, skipping the emulator system area
        runs = new List<Run>();
        foreach (KeyValuePair<ulong, int> p in pages)
        {
            ulong addr = p.Key;
            if (addr < RamLo || addr >= RamHi)
                continue;
            Run last = runs.Count > 0 ? runs[runs.Count - 1] : null;
            if (last != null && last.Start + (ulong)last.Data.Count == addr)
            {
                last.Data.AddRange(new ArraySegment<byte>(d, p.Value, Page));
            }
            else
            {
                var run = new Run { Start = addr };
                run.Data.AddRange(new ArraySegment<byte>(d, p.Value, Page));
                runs.Add(run);
            }
        }
    }

    private static List<Section> ReadSectionTable(byte[] elf, out int eShoff, out int eShentsize, out int eShnum)
    {
        if (elf.Length < 64 || elf[0] != 0x7F || elf[1] != (byte)'E' ||
            elf[2] != (byte)'L' || elf[3] != (byte)'F' || elf[4] != 2)
            throw new Exception("rebake: not a 64-bit ELF");

        eShoff = (int)RdU64(elf, 0x28);
        eShentsize = RdU16(elf, 0x3A);
        eShnum = RdU16(elf, 0x3C);

        var secs = new List<Section>();
        for (int i = 0; i < eShnum; i++)
        {
            int b = eShoff + i * eShentsize;
            secs.Add(new Section
            {
                Type = RdU32(elf, b + 4),
                Flags = RdU64(elf, b + 8),
                Addr = RdU64(elf, b + 16),
                Offset = RdU64(elf, b + 24),
                Size = RdU64(elf, b + 32),
                Link = RdU32(elf, b + 40),
                Entsize = RdU64(elf, b + 56),
            });
        }
        return secs;
    }

    private static ulong FindSymbolVaddr(byte[] elf, List<Section> secs, string name)
    {
        foreach (Section s in secs)
        {
            if (s.Type != ShtSymtab || s.Entsize == 0)
                continue;
            ulong strOff = secs[(int)s.Link].Offset;
            for (ulong o = s.Offset; o < s.Offset + s.Size; o += s.Entsize)
            {
                uint stName = RdU32(elf, (int)o);
                if (ReadCString(elf, (int)(strOff + stName)) == name)
                    return RdU64(elf, (int)o + 8);
            }
        }
        throw new Exception($"rebake: symbol {name} not found");
    }

    private static int VaddrToFileOffset(List<Section> secs, ulong vaddr)
    {
        foreach (Section s in secs)
        {
            if (s.Type == ShtProgbits && s.Size != 0 &&
                vaddr >= s.Addr && vaddr < s.Addr + s.Size)
                return (int)(s.Offset + (vaddr - s.Addr));
        }
        throw new Exception($"rebake: vaddr 0x{vaddr:x} not in any PROGBITS section");
    }

    private static byte[] BuildBlob(ulong pc, ulong[] regs)
    {
        // layout: magic u32, pad u32, pc u64, regs[32] u64  ->  272 bytes
        var b = new byte[8 + 8 + 32 * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), TrampolineMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), pc);
        for (int i = 0; i < 32; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16 + i * 8), regs[i]);
        return b;
    }

    private static byte[] PackSectionHeader(ulong addr, ulong offset, ulong size)
    {
        var h = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), ShtProgbits);            // sh_type
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), ShfAlloc | ShfWrite);    // sh_flags
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(16), addr);                  // sh_addr
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), offset);                // sh_offset
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), size);                  // sh_size
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(48), 8);                     // sh_addralign
        return h;                                                                     // sh_name/link/info/entsize = 0
    }

    private static string ReadCString(byte[] b, int off)
    {
        int end = off;
        while (end < b.Length && b[end] != 0)
            end++;
        return Encoding.ASCII.GetString(b, off, end - off);
    }

    private static void Pad8(MemoryStream s)
    {
        while ((s.Position & 7) != 0)
            s.WriteByte(0);
    }

    private static ulong RdU64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    private static uint RdU32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ushort RdU16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
}
