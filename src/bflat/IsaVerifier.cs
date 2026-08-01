// Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
//
// Exact whole-image ISA verification for the linked RISC-V binary. Unlike the
// IL-level --error-on-float (which reasons about compiled method bodies before
// codegen), this decodes the ACTUAL instructions in the output ELF and flags
// floating-point (F/D), compressed (C) or atomic (A) encodings - extensions a
// bare zkVM target (rv64im, no C, soft-float) must not contain.
//
// To stay precise it walks only real code: the function symbols (SttFunc) in
// the executable sections, never the constant pools / method tables / RTTI that
// NativeAOT interleaves in .text (those decode as garbage instructions and would
// otherwise raise false positives). If the binary carries no function symbols it
// falls back to scanning whole executable sections and says so.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

internal static class IsaVerifier
{
    // RISC-V base major opcodes (instr[6:0]) for the extensions we reject.
    // FP: LOAD-FP, STORE-FP, F[N]MADD/F[N]MSUB, OP-FP.
    private const int OpLoadFp  = 0x07;
    private const int OpStoreFp = 0x27;
    private const int OpMadd    = 0x43;
    private const int OpMsub    = 0x47;
    private const int OpNmsub   = 0x4B;
    private const int OpNmadd   = 0x4F;
    private const int OpOpFp    = 0x53;
    private const int OpAmo     = 0x2F; // atomic (lr/sc/amo*)

    private const uint ShfExecinstr = 0x4;
    private const uint ShtSymtab = 2;
    private const uint SttFunc = 2;
    private const int SymEntSize = 24;

    private readonly struct Section
    {
        public readonly uint Type;
        public readonly ulong Flags;
        public readonly ulong Addr;
        public readonly ulong Offset;
        public readonly ulong Size;
        public readonly uint Link;
        public readonly ulong EntSize;
        public Section(uint type, ulong flags, ulong addr, ulong off, ulong size, uint link, ulong entsize)
        { Type = type; Flags = flags; Addr = addr; Offset = off; Size = size; Link = link; EntSize = entsize; }
        public bool IsExec => (Flags & ShfExecinstr) != 0 && Type == 1 /*PROGBITS*/;
    }

    private sealed class Func
    {
        public ulong Addr;
        public ulong Size;
        public string Name;
    }

    /// Returns 0 when every enabled check passes, 1 when a rejected instruction
    /// class is found (or the binary cannot be scanned). Only the enabled checks
    /// can fail the build; all three are always counted and reported.
    public static int Verify(string elfPath, bool checkFloat, bool checkCompressed, bool checkAtomic)
    {
        if (!(checkFloat || checkCompressed || checkAtomic))
            return 0;

        byte[] elf;
        try { elf = File.ReadAllBytes(elfPath); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: ISA verification cannot read '{elfPath}': {e.Message}");
            return 1;
        }

        if (elf.Length < 64 || elf[0] != 0x7F || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F')
        {
            Console.Error.WriteLine($"error: ISA verification: '{elfPath}' is not an ELF file");
            return 1;
        }
        if (elf[4] != 2 /*ELFCLASS64*/ || elf[5] != 1 /*ELFDATA2LSB*/)
        {
            Console.Error.WriteLine("error: ISA verification: only little-endian ELF64 is supported");
            return 1;
        }

        ulong shoff = U64(elf, 40);
        int shentsize = U16(elf, 58);
        int shnum = U16(elf, 60);
        if (shoff == 0 || shnum == 0 || shentsize < 64)
        {
            Console.Error.WriteLine("error: ISA verification: no section headers to scan");
            return 1;
        }

        var sections = new Section[shnum];
        for (int i = 0; i < shnum; i++)
        {
            int b = (int)shoff + i * shentsize;
            if (b + 64 > elf.Length) { Console.Error.WriteLine("error: ISA verification: truncated section header"); return 1; }
            sections[i] = new Section(
                U32(elf, b + 4), U64(elf, b + 8), U64(elf, b + 16),
                U64(elf, b + 24), U64(elf, b + 32), U32(elf, b + 40), U64(elf, b + 56));
        }

        // Collect the real code: SttFunc symbols that live in an executable
        // section. This skips the data NativeAOT interleaves in .text.
        var funcs = new List<Func>();
        foreach (Section s in sections)
        {
            if (s.Type != ShtSymtab || s.EntSize == 0)
                continue;
            Section str = s.Link < (uint)shnum ? sections[s.Link] : default;
            ulong count = s.Size / (ulong)SymEntSize;
            for (ulong k = 0; k < count; k++)
            {
                int b = (int)(s.Offset + k * (ulong)SymEntSize);
                if (b + SymEntSize > elf.Length) break;
                byte info = elf[b + 4];
                if ((info & 0xF) != SttFunc) continue;
                ulong val = U64(elf, b + 8);
                ulong size = U64(elf, b + 16);
                if (val == 0) continue;
                funcs.Add(new Func { Addr = val, Size = size, Name = ReadStr(elf, str.Offset, U32(elf, b + 0)) });
            }
        }
        funcs.Sort((a, c) => a.Addr.CompareTo(c.Addr));

        // A size of 0 is common for assembly symbols; bound it by the next
        // function so the whole code range is still covered.
        for (int i = 0; i < funcs.Count; i++)
        {
            if (funcs[i].Size != 0) continue;
            ulong end = 0;
            for (int j = i + 1; j < funcs.Count; j++)
                if (funcs[j].Addr > funcs[i].Addr) { end = funcs[j].Addr; break; }
            if (end == 0)
            {
                Section es = ContainingExec(sections, funcs[i].Addr);
                if (es.Size != 0) end = es.Addr + es.Size;
            }
            if (end > funcs[i].Addr) funcs[i].Size = end - funcs[i].Addr;
        }

        var fp = new Findings("floating-point (F/D)");
        var comp = new Findings("compressed (C)");
        var atom = new Findings("atomic (A)");

        bool scannedFunctions = funcs.Count > 0;
        if (scannedFunctions)
        {
            foreach (Func f in funcs)
            {
                Section s = ContainingExec(sections, f.Addr);
                if (s.Size == 0) continue; // symbol not in an executable PROGBITS section
                long fileBase = (long)(s.Offset + (f.Addr - s.Addr));
                Scan(elf, fileBase, f.Addr, f.Size, f.Name, fp, comp, atom);
            }
        }
        else
        {
            // No symbols: scan whole executable sections. Data interleaved in
            // .text may raise false positives, so this is reported explicitly.
            Console.WriteLine("warning: ISA verification: no function symbols; scanning whole executable sections (data may cause false positives)");
            foreach (Section s in sections)
                if (s.IsExec && s.Size != 0)
                    Scan(elf, (long)s.Offset, s.Addr, s.Size, null, fp, comp, atom);
        }

        int rc = 0;
        rc |= Report(checkFloat, fp);
        rc |= Report(checkCompressed, comp);
        rc |= Report(checkAtomic, atom);
        return rc;
    }

    // Linear-decode a code range, classifying by RISC-V instruction length and
    // major opcode. Within real function bodies there is no data, so the length
    // bit (instr[1:0]) and the major opcode are trustworthy.
    private static void Scan(byte[] elf, long fileBase, ulong vaddr, ulong size, string name,
                             Findings fp, Findings comp, Findings atom)
    {
        if (fileBase < 0 || fileBase + (long)size > elf.Length)
            size = (ulong)Math.Max(0, elf.Length - fileBase);
        ulong i = 0;
        while (i + 2 <= size)
        {
            ushort h = (ushort)(elf[fileBase + (long)i] | (elf[fileBase + (long)i + 1] << 8));
            if ((h & 0x3) != 0x3)
            {
                // 16-bit -> compressed (C extension).
                comp.Add(vaddr + i, name);
                i += 2;
                continue;
            }
            if (i + 4 > size) break;
            uint w = U32(elf, (int)(fileBase + (long)i));
            int op = (int)(w & 0x7F);
            switch (op)
            {
                case OpLoadFp: case OpStoreFp: case OpMadd:
                case OpMsub: case OpNmsub: case OpNmadd: case OpOpFp:
                    fp.Add(vaddr + i, name); break;
                case OpAmo:
                    atom.Add(vaddr + i, name); break;
            }
            i += 4;
        }
    }

    private static Section ContainingExec(Section[] sections, ulong addr)
    {
        foreach (Section s in sections)
            if (s.IsExec && s.Size != 0 && addr >= s.Addr && addr < s.Addr + s.Size)
                return s;
        return default;
    }

    private sealed class Findings
    {
        public readonly string What;
        public int Count;
        public readonly List<(ulong addr, string fn)> Sample = new();
        public Findings(string what) { What = what; }
        public void Add(ulong addr, string fn)
        {
            Count++;
            if (Sample.Count < 12) Sample.Add((addr, fn));
        }
    }

    private static int Report(bool enabled, Findings f)
    {
        if (!enabled)
            return 0;
        if (f.Count == 0)
        {
            Console.WriteLine($"ISA verification: OK - no {f.What} instructions in the linked binary");
            return 0;
        }
        Console.Error.WriteLine($"error: ISA verification: {f.Count} {f.What} instruction(s) in the linked binary:");
        foreach (var (addr, fn) in f.Sample)
            Console.Error.WriteLine($"  {addr:x8}{(fn != null ? "  " + fn : "")}");
        if (f.Count > f.Sample.Count)
            Console.Error.WriteLine($"  ... and {f.Count - f.Sample.Count} more");
        return 1;
    }

    private static string ReadStr(byte[] b, ulong strOff, uint nameOff)
    {
        if (strOff == 0) return null;
        long p = (long)strOff + nameOff;
        if (p < 0 || p >= b.Length) return null;
        long e = p;
        while (e < b.Length && b[e] != 0) e++;
        return e > p ? System.Text.Encoding.UTF8.GetString(b, (int)p, (int)(e - p)) : null;
    }

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
}
