#!/usr/bin/python3
import argparse
import re
import subprocess
import sys

import lief
from elftools.dwarf.callframe import FDE
from elftools.elf.elffile import ELFFile


def prepare_parser():
    """
    Parse arguments
    """
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("input_file", help="Input ELF")
    parser.add_argument("output_file", help="Output ELF")
    parser.add_argument(
        "--objdump",
        help="objdump binary to use for disassembly (default: riscv64-linux-gnu-objdump, "
             "falls back to llvm-objdump if not found)",
        default=None,
    )
    parser.add_argument(
        "--fix-init-array",
        help="Fix up .init_array type to PROGBITS and set alignment to 8",
        action="store_true",
        default=False,
    )
    parser.add_argument(
        "--fix-tdata",
        help="Fix up .tdata flags to ALLOC|WRITE|TLS",
        action="store_true",
        default=False,
    )
    parser.add_argument(
        "--nop-fences",
        help="Rewrite every FENCE/FENCE.I (opcode 0x0f) word in .text to NOP. Single-hart "
             "zkVMs have no memory ordering to enforce, and SP1's transpiler maps FENCE to an "
             "illegal instruction that faults when executed",
        action="store_true",
        default=False,
    )
    parser.add_argument(
        "--nop-zero-words",
        help="Rewrite the alignment padding between functions in .text to NOP. ILC pads with "
             "either an all-zero word, which is not an encoding at all, or c.nop, which is one "
             "the no-C targets cannot decode; SP1 rejects both when it transpiles the whole "
             "segment up front",
        action="store_true",
        default=False,
    )
    parser.add_argument(
        "--split-code-data",
        help="Nullify all data in .text section, make a cloned .text_overlay with data+code",
        action="store_true",
        default=False,
    )
    parser.add_argument(
        "--print-fn-boundaries",
        help="Print function boundary report (EH vs symtab/dynsym vs objdump and final chosen size)",
        action="store_true",
        default=False,
    )
    parser.add_argument(
        "--remove-eh", help="Remove EH sections", action="store_true", default=False
    )
    parser.add_argument(
        "--trim-bss", help="Remove .bss section", action="store_true", default=False
    )
    parser.add_argument(
        "--unsigned-muldiv",
        help="ELF32 only: move each signed RV32M instruction (mulh, mulhsu, div, rem) of the "
             "native code into a trampoline that computes it from unsigned instructions, for a "
             "zkVM that proves only the unsigned ones (Airbender v3). The trampolines go into "
             "__signed_tramp_start..__signed_tramp_end, which the linker script reserves",
        action="store_true",
        default=False,
    )
    return parser


# An ELF32 image (riscv32) gets the passes that rewrite instruction words in
# place, on the bytes of the file: --nop-fences and --unsigned-muldiv. lief
# would rebuild the file, and the flat image of a riscv32 zkVM is the file's
# load segments as linked.
ELF32_PASSES = ("nop_fences", "unsigned_muldiv")


def rewrite_elf32(args):
    """--nop-fences and --unsigned-muldiv on an ELF32 RISC-V image, in place in a copy of it.

    Only words inside a sized function are instructions: ILC parks data in .text too, and a
    data word that happens to look like one of these instructions must keep its value. The
    native code they come from always has sized function symbols. The JIT emits neither for
    managed code (no fences in stubs; JitRiscVUnsignedMulDiv); the C/C++ compilers emit both.
    """
    import bisect
    import struct

    data = bytearray(open(args.input_file, "rb").read())
    e_shoff, = struct.unpack_from("<I", data, 0x20)
    e_shentsize, e_shnum = struct.unpack_from("<HH", data, 0x2E)
    # name type flags addr offset size link info align entsize
    sections = [struct.unpack_from("<IIIIIIIIII", data, e_shoff + i * e_shentsize) for i in range(e_shnum)]

    def symbols():
        """(name, value, size, type) of every symbol in the symbol table."""
        for sh in sections:
            if sh[1] != 2:  # SHT_SYMTAB
                continue
            strtab = sections[sh[6]]
            for p in range(sh[4], sh[4] + sh[5], 16):
                st_name, st_value, st_size, st_info = struct.unpack_from("<IIIB", data, p)
                end = data.index(0, strtab[4] + st_name)
                yield data[strtab[4] + st_name:end].decode(), st_value, st_size, st_info & 0xF

    def symbol(name):
        for sym_name, value, _, _ in symbols():
            if sym_name == name:
                return value
        sys.exit(f"patch_elf: --unsigned-muldiv: symbol {name} not found (the linker script reserves the trampolines)")

    STT_FUNC = 2
    functions = sorted((value, value + size) for _, value, size, kind in symbols() if kind == STT_FUNC and size > 0)
    function_starts = [start for start, _ in functions]

    def in_function(addr):
        i = bisect.bisect_right(function_starts, addr) - 1
        return i >= 0 and addr + 4 <= functions[i][1]

    def file_offset(addr):
        for sh in sections:
            if sh[1] == 1 and sh[3] <= addr < sh[3] + sh[5]:
                return sh[4] + addr - sh[3]
        sys.exit(f"patch_elf: address {addr:#x} is not in a PROGBITS section")

    def r_type(f7, rs2, rs1, f3, rd, op=0x33):
        return (f7 << 25) | (rs2 << 20) | (rs1 << 15) | (f3 << 12) | (rd << 7) | op

    def i_type(imm, rs1, f3, rd, op):
        return ((imm & 0xFFF) << 20) | (rs1 << 15) | (f3 << 12) | (rd << 7) | op

    def s_type(imm, rs2, rs1, f3=2):
        return (((imm >> 5) & 0x7F) << 25) | (rs2 << 20) | (rs1 << 15) | (f3 << 12) | ((imm & 0x1F) << 7) | 0x23

    def jal(rd, offset):
        assert -(1 << 20) <= offset < (1 << 20) and offset % 2 == 0, f"jump of {offset:#x} out of reach"
        imm = offset & 0x1FFFFF
        return ((((imm >> 20) & 1) << 31) | (((imm >> 1) & 0x3FF) << 21) | (((imm >> 11) & 1) << 20)
                | (((imm >> 12) & 0xFF) << 12) | (rd << 7) | 0x6F)

    ZERO, SP = 0, 2
    sub = lambda rd, a, b: r_type(0x20, b, a, 0, rd)
    xor = lambda rd, a, b: r_type(0, b, a, 4, rd)
    and_ = lambda rd, a, b: r_type(0, b, a, 7, rd)
    sltu = lambda rd, a, b: r_type(0, b, a, 3, rd)
    mulhu = lambda rd, a, b: r_type(1, b, a, 3, rd)
    divu = lambda rd, a, b: r_type(1, b, a, 5, rd)
    remu = lambda rd, a, b: r_type(1, b, a, 7, rd)
    addi = lambda rd, a, imm: i_type(imm, a, 0, rd, 0x13)
    srai = lambda rd, a, sh: i_type(0x400 | sh, a, 5, rd, 0x13)
    lw = lambda rd, base, imm: i_type(imm, base, 2, rd, 0x03)
    sw = lambda rs, base, imm: s_type(imm, rs, base)

    def body(f3, a, b, t0, t1, t2):
        """The result of the signed instruction with funct3 f3 on a, b, left in t0."""
        if f3 == 1:  # mulh = mulhu - (a < 0 ? b : 0) - (b < 0 ? a : 0)
            return [mulhu(t0, a, b), srai(t1, a, 31), and_(t1, t1, b), sub(t0, t0, t1),
                    srai(t1, b, 31), and_(t1, t1, a), sub(t0, t0, t1)]
        if f3 == 2:  # mulhsu = mulhu - (a < 0 ? b : 0)
            return [mulhu(t0, a, b), srai(t1, a, 31), and_(t1, t1, b), sub(t0, t0, t1)]
        magnitudes = [srai(t1, a, 31), xor(t0, a, t1), sub(t0, t0, t1),
                      srai(t1, b, 31), xor(t2, b, t1), sub(t2, t2, t1)]
        if f3 == 4:  # div: |a| / |b| signed by a ^ b; a zero divisor gives -1 whatever the sign of a
            return magnitudes + [divu(t0, t0, t2), xor(t1, a, b), srai(t1, t1, 31),
                                 sltu(t2, ZERO, b), sub(t2, ZERO, t2), and_(t1, t1, t2),
                                 xor(t0, t0, t1), sub(t0, t0, t1)]
        # rem: |a| % |b| signed by a; a zero divisor gives a
        return magnitudes + [remu(t0, t0, t2), srai(t1, a, 31), xor(t0, t0, t1), sub(t0, t0, t1)]

    if args.unsigned_muldiv:
        tramp_start = tramp = symbol("__signed_tramp_start")
        tramp_end = symbol("__signed_tramp_end")
    else:
        tramp_start = tramp = tramp_end = 0
    text = [sh for sh in sections if sh[1] == 1 and sh[2] & 0x4]  # executable PROGBITS
    NOP = addi(ZERO, ZERO, 0)
    fences = patched = skipped = 0
    for sh in text:
        for addr in range(sh[3], sh[3] + sh[5] - 3, 4):
            if tramp_start <= addr < tramp_end:
                continue
            p = sh[4] + addr - sh[3]
            insn, = struct.unpack_from("<I", data, p)
            f3, rd, a, b = (insn >> 12) & 7, (insn >> 7) & 31, (insn >> 15) & 31, (insn >> 20) & 31
            is_fence = args.nop_fences and (insn & 0x7F) == 0x0F and f3 in (0, 1)  # MISC-MEM: FENCE, FENCE.I
            is_signed = (args.unsigned_muldiv and (insn & 0x7F) == 0x33 and (insn >> 25) == 1
                         and f3 in (1, 2, 4, 6))
            if not (is_fence or is_signed):
                continue
            if not in_function(addr):
                skipped += 1
                continue
            if is_fence or rd == ZERO:
                struct.pack_into("<I", data, p, NOP)
                if is_fence:
                    fences += 1
                else:
                    patched += 1
                continue
            if SP in (rd, a, b):
                sys.exit(f"patch_elf: --unsigned-muldiv: stack pointer operand at {addr:#x}")
            t0, t1, t2 = [r for r in (5, 6, 7, 28, 29, 30, 31) if r not in (rd, a, b)][:3]
            code = [addi(SP, SP, -16), sw(t0, SP, 0), sw(t1, SP, 4), sw(t2, SP, 8)]
            code += body(f3, a, b, t0, t1, t2)
            code += [sw(t0, SP, 12), lw(t0, SP, 0), lw(t1, SP, 4), lw(t2, SP, 8), lw(rd, SP, 12), addi(SP, SP, 16)]
            code.append(jal(ZERO, addr + 4 - (tramp + 4 * len(code))))
            if tramp + 4 * len(code) > tramp_end:
                sys.exit("patch_elf: --unsigned-muldiv: the trampoline area is full")
            struct.pack_into(f"<{len(code)}I", data, file_offset(tramp), *code)
            struct.pack_into("<I", data, p, jal(ZERO, tramp - addr))
            tramp += 4 * len(code)
            patched += 1
    open(args.output_file, "wb").write(data)
    print(f"elf32 rewrites: {fences} fence(s) to NOP, {patched} signed mul/div moved out of line; "
          f"{skipped} matching word(s) outside any function left untouched")


def fix_init_array(args, elf):
    sec = elf.get_section(".init_array")
    sec.type = lief.ELF.Section.TYPE.PROGBITS
    sec.alignment = 8


def fix_tdata(args, elf):
    sec = elf.get_section(".tdata")
    FL_ALLOC = 0x2
    FL_WRITE = 0x1
    FL_TLS = 0x400
    sec.flags = getattr(sec, "flags", 0) | FL_ALLOC | FL_WRITE | FL_TLS
    sec.alignment = max(8, sec.alignment or 0)


def get_text_data(args, elf):
    text = elf.get_section(".text")
    if text is None:
        raise SystemExit("no .text is present")

    text_va = int(text.virtual_address or 0)
    text_off = int(text.offset or 0)
    text_size = int(text.size or 0)
    text_end = text_va + text_size

    text_data = []
    with open(args.input_file, "rb") as f:
        f.seek(text_off)
        text_data = bytearray(f.read(text_size))
    return text, text_data


def add_overlay(args, elf, text_data):
    overlay = lief.ELF.Section(".text_overlay")
    overlay.type = lief.ELF.Section.TYPE.PROGBITS
    FL_ALLOC = 0x2
    overlay.flags = FL_ALLOC
    overlay.alignment = text.alignment or 16
    overlay.virtual_address = text.virtual_address
    overlay.size = len(text_data)
    overlay.content = list(text_data)
    elf.add(overlay)


# ELF code & data splitting
def find_fn_boundaries_in_symtab(sec, text_start, text_end, sym_sizes):
    if not sec:
        return sym_sizes
    for sym in sec.iter_symbols():
        ent = sym.entry

        # Filter bad entries
        if ent["st_info"]["type"] != "STT_FUNC":
            continue
        if ent["st_shndx"] == "SHN_UNDEF":
            continue

        start = int(ent["st_value"])
        size = int(ent["st_size"] or 0)
        end = start + size if size > 0 else start

        # truncate by .text boundaries
        s = max(start, text_start)
        e = min(end, text_end)

        # take the smallest among duplicates
        prev = sym_sizes.get(s)
        sym_sizes[s] = min(prev, e - s) if prev else (e - s)

    return sym_sizes


def find_fn_boundaries(args, print_report=False):
    with open(args.input_file, "rb") as f:
        elf = ELFFile(f)
        dwarf = elf.get_dwarf_info()

        # Determine .text section boundaries
        text = elf.get_section_by_name(".text")
        t0 = text["sh_addr"] if text else 0
        t1 = (text["sh_addr"] + text["sh_size"]) if text else (1 << 64)

        # 1. Collect function sizes from EH information
        eh_sizes = {}  # (start, size) pairs
        for entry in dwarf.EH_CFI_entries():
            if isinstance(entry, FDE):
                start = int(entry["initial_location"])
                size = int(entry["address_range"])
                end = start + size
                if end > start and not (end <= t0 or start >= t1):
                    # truncating by .text boundaries
                    s = max(start, t0)
                    e = min(end, t1)
                    if e > s:
                        eh_sizes[s] = e - s

        # 2. Collect function sizes from symtab information
        sym_sizes = {}
        sym_sizes = find_fn_boundaries_in_symtab(
            elf.get_section_by_name(".symtab"), t0, t1, sym_sizes
        )
        sym_sizes = find_fn_boundaries_in_symtab(
            elf.get_section_by_name(".dynsym"), t0, t1, sym_sizes
        )

        # 3. Collect function sizes from diassembly (yay!)
        objdump_candidates = (
            [args.objdump] if args.objdump
            else ["riscv64-linux-gnu-objdump", "llvm-objdump", "objdump"]
        )
        objdump_bin = None
        for candidate in objdump_candidates:
            try:
                probe = subprocess.run(
                    [candidate, "--version"],
                    capture_output=True, text=True, check=False,
                )
                if probe.returncode == 0:
                    objdump_bin = candidate
                    break
            except FileNotFoundError:
                continue
        if objdump_bin is None:
            print(
                "WARNING: no usable objdump found "
                f"(tried: {objdump_candidates}); skipping disasm pass",
                file=sys.stderr,
            )
        p = subprocess.run(
            [objdump_bin, "-Cd", args.input_file] if objdump_bin else [],
            capture_output=True,
            text=True,
            check=False,
        ) if objdump_bin else type("R", (), {"stdout": ""})()
        lines = p.stdout.splitlines()
        hdr_re = re.compile(r"^\s*([0-9a-fA-Fx]+)\s+<.+>:\s*$")
        insn_re = re.compile(r"^\s*([0-9a-fA-Fx]+):\s+[0-9a-fA-F ]+\s")
        unimp_re = re.compile(r"^\s*[0-9a-fA-Fx]+:\s+[0-9a-fA-F ]+\s+unimp\s*$")
        # llvm-objdump shows undecodable data bytes as "<unknown>".
        # These are data regions embedded in .text — do NOT add their addresses
        # to addrs_in_block and flush the current code block when seen.
        unknown_re = re.compile(r"<unknown>")
        cur_start = None
        addrs_in_block = []
        obj_sizes = {}

        def flush_disasm_block():
            nonlocal cur_start, addrs_in_block, obj_sizes
            if cur_start is None or not addrs_in_block:
                cur_start, addrs_in_block = None, []
                return
            # границы блока по инструкциям
            addrs_in_block.sort()
            last = addrs_in_block[-1]
            step = (
                (addrs_in_block[-1] - addrs_in_block[-2])
                if len(addrs_in_block) >= 2
                else 4
            )
            end = last + max(step, 2)
            s = int(cur_start, 16) if isinstance(cur_start, str) else int(cur_start)
            if t0 <= s < t1:
                e = min(end, t1)
                if e > s:
                    prev = obj_sizes.get(s, 0)
                    size = e - s
                    # берём максимальный размер из всех flush для одного адреса
                    obj_sizes[s] = max(prev, size)
            cur_start, addrs_in_block = None, []

        for ln in lines:
            m_hdr = hdr_re.match(ln)
            if m_hdr:
                # if there is a new block, the old one must be flushed
                flush_disasm_block()
                cur_start = int(m_hdr.group(1), 16)
                addrs_in_block = []
                continue

            # Check for unimp instruction (marks data boundary or diverging call end).
            # We flush the current block up to this point, then start a NEW block
            # immediately after the unimp (4 bytes), so that any unlabeled code
            # that follows is still captured and not silently zeroed.
            m_unimp = unimp_re.match(ln)
            if m_unimp and cur_start is not None:
                flush_disasm_block()
                # restart: new synthetic block starts right after the unimp
                m_addr = re.match(r"^\s*([0-9a-fA-F]+):", ln)
                if m_addr:
                    unimp_addr = int(m_addr.group(1), 16)
                    next_addr = unimp_addr + 4  # unimp is always 4 bytes
                    if t0 <= next_addr < t1:
                        cur_start = next_addr
                        addrs_in_block = []
                continue
            m_insn = insn_re.match(ln)
            if m_insn and cur_start is not None:
                # Skip data bytes shown as "<unknown>" by llvm-objdump — their
                # addresses must not extend the code block into a data region.
                # The block itself stays open; the end will be computed from the
                # last *real* instruction address.
                if unknown_re.search(ln):
                    continue
                a = int(m_insn.group(1), 16)
                if t0 <= a < t1:
                    addrs_in_block.append(a)

        flush_disasm_block()

        # 4. Merge all results from different tables
        merged = {}
        chosen_by = {}
        all_starts = sorted(
            set(eh_sizes.keys()) | set(sym_sizes.keys()) | set(obj_sizes.keys())
        )
        for s in all_starts:
            if s in eh_sizes:
                merged[s] = eh_sizes[s]
                chosen_by[s] = "eh"
            elif s in obj_sizes and obj_sizes[s] > 0:
                merged[s] = obj_sizes[s]
                chosen_by[s] = "objdump"
            elif s in sym_sizes:
                merged[s] = sym_sizes[s]
                chosen_by[s] = "symtab"
            else:
                merged[s] = 0
                chosen_by[s] = "none"

        # 4b. Residual objdump coverage:
        # When EH was chosen for address S with size EH_sz, but objdump recorded a
        # larger block (obj_sz > EH_sz), the range [S+EH_sz .. S+obj_sz) may contain
        # real unlabeled code that has no EH/sym entry of its own.  For every byte in
        # that residual range that is not already covered by any other merged entry,
        # add a synthetic objdump-sourced entry.
        ordered_m = sorted(merged.keys())
        # Build a fast set of covered starts for the residual pass
        new_entries = {}  # addr -> size, to be merged in after the loop
        for s in list(merged.keys()):
            if chosen_by.get(s) != "eh":
                continue
            obj_sz = obj_sizes.get(s, 0)
            eh_sz = eh_sizes.get(s, 0)
            if obj_sz <= eh_sz:
                continue
            residual_start = s + eh_sz
            residual_end = s + obj_sz
            # Walk through the residual range and find uncovered sub-ranges
            cur = residual_start
            for other_s in ordered_m:
                if other_s >= residual_end:
                    break
                if other_s <= cur:
                    # already-covered region: advance past it
                    other_end = other_s + merged.get(other_s, 0)
                    if other_end > cur:
                        cur = other_end
                    continue
                # gap between cur and other_s: uncovered residual code.
                # Only claim it if it is large enough to plausibly be real code
                # (≥ 16 bytes = 4 RISC-V instructions).  Tiny gaps are almost
                # always alignment padding or embedded data constants.
                gap_s = cur
                gap_e = min(other_s, residual_end)
                if gap_e - gap_s >= 16:
                    prev = new_entries.get(gap_s, 0)
                    new_entries[gap_s] = max(prev, gap_e - gap_s)
                # advance past other_s's range
                cur = max(cur, other_s + merged.get(other_s, 0))
            # tail residual after all known entries (same size threshold)
            if residual_end - cur >= 16:
                prev = new_entries.get(cur, 0)
                new_entries[cur] = max(prev, residual_end - cur)

        for s, sz in new_entries.items():
            if sz > 0 and s not in merged:
                merged[s] = sz
                chosen_by[s] = "objdump+residual"
            elif sz > 0 and merged.get(s, 0) == 0:
                merged[s] = sz
                chosen_by[s] = "objdump+residual"

        # 5. If any of sizes is 0, take size from next start address ---
        ordered_starts = sorted(merged.keys())
        for i, s in enumerate(ordered_starts):
            if merged[s] > 0:
                continue
            if i + 1 >= len(ordered_starts):
                continue

            next_s = ordered_starts[i + 1]
            if next_s <= s:
                continue

            # limiting by .text boundaries
            sz = max(0, min(next_s, t1) - max(s, t0))
            if sz > 0:
                merged[s] = sz
                chosen_by[s] = chosen_by.get(s, "none") + "+next_start"

        # 6. Prepare final sorted list
        funcs = sorted(
            ((s, merged[s]) for s in merged if merged[s] > 0), key=lambda x: x[0]
        )

        if print_report:
            print("== function boundary report ==")
            print(".text: [0x%x .. 0x%x) size=0x%x" % (t0, t1, max(0, t1 - t0)))
            print("columns: start  size  end   chosen  eh  sym  objdump")
            for s, sz in funcs:
                eh = eh_sizes.get(s, 0)
                sym = sym_sizes.get(s, 0)
                obj = obj_sizes.get(s, 0)
                end = s + sz
                print(
                    "0x%016x 0x%08x 0x%016x %-12s eh=0x%08x sym=0x%08x obj=0x%08x"
                    % (s, sz, end, chosen_by.get(s, "unknown"), eh, sym, obj)
                )
            print("== end function boundary report ==")

        return funcs


def find_gaps(text, funcs):
    text_va = int(text.virtual_address or 0)
    text_off = int(text.offset or 0)
    text_size = int(text.size or 0)
    text_end = text_va + text_size

    # Find intervals inside funcs
    ranges = []
    for i, (start, size) in enumerate(funcs):
        end = (
            start + size
            if size > 0
            else (funcs[i + 1][0] if i + 1 < len(funcs) else text_end)
        )
        start = max(start, text_va)
        end = min(end, text_end)
        if end > start:
            ranges.append((start, end))
    # Merging intersections
    ranges.sort()
    merged = []
    for a, b in ranges:
        if not merged or a > merged[-1][1]:
            merged.append([a, b])
        else:
            merged[-1][1] = max(merged[-1][1], b)
    ranges = [(a, b) for a, b in merged]
    return ranges


def nullify_gaps(text, text_data, gaps):
    text_va = int(text.virtual_address or 0)
    text_off = int(text.offset or 0)
    text_size = int(text.size or 0)
    text_end = text_va + text_size

    zeroed = text_data[:]  # копия
    cur = text_va
    zero_total = 0
    for a, b in gaps:
        if a > cur:
            ofs = a - cur
            base = cur - text_va
            zeroed[base : base + ofs] = b"\x00" * ofs
            zero_total += ofs
        cur = max(cur, b)
    if cur < text_end:
        ofs = text_end - cur
        base = cur - text_va
        zeroed[base : base + ofs] = b"\x00" * ofs
        zero_total += ofs

    return zeroed


print("Start postprocessing ELF file")
args = prepare_parser().parse_args()

with open(args.input_file, "rb") as f:
    ident = f.read(5)
if ident[:4] == b"\x7fELF" and ident[4] == 1:
    unsupported = [name for name, value in vars(args).items()
                   if value is True and name not in ELF32_PASSES]
    if unsupported:
        sys.exit(f"patch_elf: an ELF32 image takes only --nop-fences and --unsigned-muldiv, not: "
                 + ", ".join("--" + name.replace("_", "-") for name in unsupported))
    rewrite_elf32(args)
    print("ELF file postprocessed")
    sys.exit(0)
if args.unsigned_muldiv:
    sys.exit("patch_elf: --unsigned-muldiv is for an ELF32 image")

elf = lief.parse(args.input_file)

if args.fix_init_array:
    fix_init_array(args, elf)
if args.fix_tdata:
    fix_tdata(args, elf)
if args.split_code_data:
    text, text_data = get_text_data(args, elf)
    fn_boundaries = find_fn_boundaries(args, print_report=args.print_fn_boundaries)
    fn_gaps = find_gaps(text, fn_boundaries)
    nullified_text_data = nullify_gaps(text, text_data, fn_gaps)
    text.content = list(nullified_text_data)
    add_overlay(args, elf, text_data)
elif args.print_fn_boundaries:
    # still allow printing boundaries without modifying the binary
    _ = find_fn_boundaries(args, print_report=True)
if args.nop_fences or args.nop_zero_words:
    text, text_data = get_text_data(args, elf)
    text_va = int(text.virtual_address or 0)
    NOP = (0x13).to_bytes(4, "little")

    # Only the bytes that belong to a function are code. ILC also parks data
    # in .text (per-method read-only data, jump tables), and rewriting a word
    # there would silently change what the program computes - a zero word is a
    # perfectly ordinary constant. So the rewrites are confined to the function
    # ranges the unwind tables and the symbol table agree on, and the padding
    # between them is handled separately: a gap is only rewritten when it is
    # ENTIRELY zero and no longer than the maximum code alignment, which is what
    # alignment padding looks like and what a data blob does not.
    MAX_ALIGN_PAD = 16
    fn_ranges = find_gaps(text, find_fn_boundaries(args))
    in_fn = [(a - text_va, b - text_va) for a, b in fn_ranges]

    fences = zeros = skipped = 0

    # What alignment padding looks like: ILC writes a repeated pattern, zero on
    # the targets that have no NOP byte and c.nop (0001) on RISC-V. Neither is
    # something a no-C decoder will accept, and neither can be a data blob of
    # any use, so a gap made entirely of one of them is padding.
    C_NOP = b"\x01\x00"

    def is_padding(gap):
        return not any(gap) or gap == C_NOP * (len(gap) // 2)

    def rewrite_code(lo, hi):
        global fences, zeros
        for off in range(lo, min(hi, len(text_data)) - 3, 4):
            word = text_data[off:off + 4]
            if args.nop_fences and (word[0] & 0x7F) == 0x0F:
                text_data[off:off + 4] = NOP
                fences += 1
            elif args.nop_zero_words and word in (b"\0\0\0\0", C_NOP * 2):
                text_data[off:off + 4] = NOP
                zeros += 1

    prev_end = 0
    for lo, hi in in_fn:
        # The gap before this function: alignment padding, or data.
        gap = text_data[prev_end:lo]
        if (args.nop_zero_words and gap and len(gap) <= MAX_ALIGN_PAD
                and len(gap) % 4 == 0 and is_padding(gap)):
            for off in range(prev_end, lo - 3, 4):
                text_data[off:off + 4] = NOP
                zeros += 1
        elif gap:
            skipped += len(gap)
        rewrite_code(lo, hi)
        prev_end = max(prev_end, hi)
    tail = text_data[prev_end:]
    if (args.nop_zero_words and tail and len(tail) <= MAX_ALIGN_PAD
            and len(tail) % 4 == 0 and is_padding(tail)):
        for off in range(prev_end, len(text_data) - 3, 4):
            text_data[off:off + 4] = NOP
            zeros += 1
    elif tail:
        skipped += len(tail)

    text.content = list(text_data)
    print(f"nop-rewrites: {fences} fence(s), {zeros} zero word(s); "
          f"{skipped} byte(s) outside any function left untouched")
if args.remove_eh:
    elf.remove_section(".dotnet_eh_table", clear=False)
    elf.remove_section(".eh_frame_hdr", clear=False)
    elf.remove_section(".eh_frame", clear=False)

if args.trim_bss:
    elf.remove_section(".bss", clear=False)

elf.write(args.output_file)
print("ELF file postprocessed")
