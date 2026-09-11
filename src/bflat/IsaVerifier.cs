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
// otherwise raise false positives). A function symbol is further clipped to the
// code its unwind info (.eh_frame FDE) covers: since .NET 11 ILC appends the
// JIT's read-only data (jump tables, span/string constants) to the method it
// belongs to, inside the FUNC symbol's size, and only the FDE tells where the
// instructions end. If the binary carries no function symbols it falls back to
// the FDE ranges, then to scanning whole executable sections and says so.

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
        public readonly uint NameOff;
        public readonly uint Type;
        public readonly ulong Flags;
        public readonly ulong Addr;
        public readonly ulong Offset;
        public readonly ulong Size;
        public readonly uint Link;
        public readonly ulong EntSize;
        public Section(uint nameOff, uint type, ulong flags, ulong addr, ulong off, ulong size, uint link, ulong entsize)
        { NameOff = nameOff; Type = type; Flags = flags; Addr = addr; Offset = off; Size = size; Link = link; EntSize = entsize; }
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
                U32(elf, b + 0), U32(elf, b + 4), U64(elf, b + 8), U64(elf, b + 16),
                U64(elf, b + 24), U64(elf, b + 32), U32(elf, b + 40), U64(elf, b + 56));
        }
        int shstrndx = U16(elf, 62);
        ulong shstrOff = shstrndx < shnum ? sections[shstrndx].Offset : 0;

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

        // Code extents from the unwind info. A method's FUNC symbol covers the
        // JIT data ILC appends after its instructions; the FDE ends where the
        // instructions do, so clip every symbol to the last FDE starting in it.
        List<(ulong begin, ulong end)> fdes = ReadEhFrameRanges(elf, sections, shstrOff);
        int clipped = 0;
        if (fdes.Count > 0)
        {
            foreach (Func f in funcs)
            {
                ulong end = f.Addr + f.Size;
                ulong codeEnd = 0;
                int k = LowerBound(fdes, f.Addr);
                for (; k < fdes.Count && fdes[k].begin < end; k++)
                    if (fdes[k].end > codeEnd) codeEnd = fdes[k].end;
                if (codeEnd != 0 && codeEnd < end)
                {
                    f.Size = codeEnd - f.Addr;
                    clipped++;
                }
            }
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
            if (clipped > 0)
                Console.WriteLine($"ISA verification: {funcs.Count} functions scanned, {clipped} clipped to their unwind range (trailing JIT data skipped)");
        }
        else if (fdes.Count > 0)
        {
            // No symbols but unwind info: the FDE ranges are exactly the code.
            Console.WriteLine("warning: ISA verification: no function symbols; scanning the .eh_frame code ranges");
            foreach (var (begin, end) in fdes)
            {
                Section s = ContainingExec(sections, begin);
                if (s.Size == 0) continue;
                Scan(elf, (long)(s.Offset + (begin - s.Addr)), begin, end - begin, null, fp, comp, atom);
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
                // 16-bit unit. 0x0000 is the defined-illegal C encoding used as
                // inter-function alignment padding, never a real instruction -
                // skip it so trailing/hole padding does not inflate the count.
                // Any other 16-bit pattern is a real compressed (C) instruction
                // (RyuJIT emits c.add/c.mv/... and the linked libc has them too).
                if (h != 0x0000)
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

    // First index whose begin >= addr, on the sorted FDE list.
    private static int LowerBound(List<(ulong begin, ulong end)> r, ulong addr)
    {
        int lo = 0, hi = r.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (r[mid].begin < addr) lo = mid + 1; else hi = mid;
        }
        // Also cover an FDE that starts just before addr but the caller only
        // needs FDEs starting inside the symbol, so no step back is required.
        return lo;
    }

    // .eh_frame: the [pc_begin, pc_begin + pc_range) of every FDE, sorted by
    // pc_begin. Only what the parser needs of the CIE is decoded (the
    // augmentation, for the FDE pointer encoding); anything unexpected simply
    // ends the walk, leaving the ranges found so far.
    private static List<(ulong begin, ulong end)> ReadEhFrameRanges(byte[] elf, Section[] sections, ulong shstrOff)
    {
        var ranges = new List<(ulong, ulong)>();
        Section eh = default;
        foreach (Section s in sections)
            if (s.Type == 1 && ReadStr(elf, shstrOff, s.NameOff) == ".eh_frame") { eh = s; break; }
        if (eh.Size == 0 || eh.Offset + eh.Size > (ulong)elf.Length)
            return ranges;

        var cieEnc = new Dictionary<long, (byte fdeEnc, bool z)>();
        long secStart = (long)eh.Offset, secEnd = (long)(eh.Offset + eh.Size);
        long pos = secStart;
        try
        {
            while (pos + 4 <= secEnd)
            {
                ulong len = U32(elf, (int)pos);
                long hdr = 4;
                if (len == 0) { pos += 4; continue; } // terminator
                if (len == 0xffffffff) { len = U64(elf, (int)pos + 4); hdr = 12; }
                long entry = pos + hdr, next = entry + (long)len;
                if (next > secEnd || len < 4) break;
                uint id = U32(elf, (int)entry);
                if (id == 0)
                {
                    // CIE
                    long p = entry + 4;
                    byte version = elf[p++];
                    long augStart = p;
                    while (p < next && elf[p] != 0) p++;
                    string aug = System.Text.Encoding.ASCII.GetString(elf, (int)augStart, (int)(p - augStart));
                    p++;
                    if (aug.Contains("eh")) p += 8;
                    if (version >= 4) p += 2; // address_size, segment_size
                    ReadUleb(elf, ref p); // code alignment
                    ReadSleb(elf, ref p); // data alignment
                    if (version == 1) p++; else ReadUleb(elf, ref p); // return register
                    byte fdeEnc = 0; // DW_EH_PE_absptr
                    bool z = aug.StartsWith("z");
                    if (z)
                    {
                        ReadUleb(elf, ref p); // augmentation data length
                        for (int i = 1; i < aug.Length; i++)
                        {
                            switch (aug[i])
                            {
                                case 'L': p++; break;
                                case 'R': fdeEnc = elf[p++]; break;
                                case 'P': { byte enc = elf[p++]; ReadEncoded(elf, ref p, enc, eh); break; }
                                case 'S': case 'B': break;
                                default: i = aug.Length; break; // unknown: stop decoding this CIE
                            }
                        }
                    }
                    cieEnc[pos] = (fdeEnc, z);
                }
                else
                {
                    // FDE: id is the distance back to its CIE
                    long ciePos = entry - id;
                    if (!cieEnc.TryGetValue(ciePos, out var cie)) { pos = next; continue; }
                    long p = entry + 4;
                    ulong begin = ReadEncoded(elf, ref p, cie.fdeEnc, eh);
                    ulong range = ReadEncoded(elf, ref p, (byte)(cie.fdeEnc & 0x0f), eh);
                    if (range != 0)
                        ranges.Add((begin, begin + range));
                }
                pos = next;
            }
        }
        catch (Exception)
        {
            // Malformed unwind info: keep what was parsed.
        }
        ranges.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return ranges;
    }

    // DWARF exception-header pointer: value formats in the low nibble,
    // pc-relative application in bit 4 (relative to the field's own address).
    private static ulong ReadEncoded(byte[] elf, ref long p, byte enc, Section sec)
    {
        if (enc == 0xff) return 0; // DW_EH_PE_omit
        ulong fieldAddr = sec.Addr + (ulong)(p - (long)sec.Offset);
        ulong v;
        switch (enc & 0x0f)
        {
            case 0x00: v = U64(elf, (int)p); p += 8; break;                       // absptr
            case 0x01: v = ReadUleb(elf, ref p); break;                          // uleb128
            case 0x02: v = U16(elf, (int)p); p += 2; break;                      // udata2
            case 0x03: v = U32(elf, (int)p); p += 4; break;                      // udata4
            case 0x04: v = U64(elf, (int)p); p += 8; break;                      // udata8
            case 0x09: v = (ulong)ReadSleb(elf, ref p); break;                   // sleb128
            case 0x0a: v = (ulong)(long)(short)U16(elf, (int)p); p += 2; break;  // sdata2
            case 0x0b: v = (ulong)(long)(int)U32(elf, (int)p); p += 4; break;    // sdata4
            case 0x0c: v = U64(elf, (int)p); p += 8; break;                      // sdata8
            default: throw new NotSupportedException("eh_frame pointer encoding");
        }
        if ((enc & 0x70) == 0x10) v += fieldAddr; // DW_EH_PE_pcrel
        return v;
    }

    private static ulong ReadUleb(byte[] b, ref long p)
    {
        ulong result = 0; int shift = 0; byte c;
        do { c = b[p++]; result |= (ulong)(c & 0x7f) << shift; shift += 7; } while ((c & 0x80) != 0);
        return result;
    }

    private static long ReadSleb(byte[] b, ref long p)
    {
        long result = 0; int shift = 0; byte c;
        do { c = b[p++]; result |= (long)(c & 0x7f) << shift; shift += 7; } while ((c & 0x80) != 0);
        if (shift < 64 && (c & 0x40) != 0) result |= -1L << shift;
        return result;
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
