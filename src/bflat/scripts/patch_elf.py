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
    return parser


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
        p = subprocess.run(
            ["riscv64-linux-gnu-objdump", "-Cd", args.input_file],
            capture_output=True,
            text=True,
            check=False,
        )
        lines = p.stdout.splitlines()
        hdr_re = re.compile(r"^\s*([0-9a-fA-Fx]+)\s+<[^>]+>:\s*$")
        insn_re = re.compile(r"^\s*([0-9a-fA-Fx]+):\s+[0-9a-fA-F ]+\s")
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
                    prev = obj_sizes[s] if s in obj_sizes.keys() else 0
                    size = e - s
                    obj_sizes[s] = min(prev, size) if prev else size
            cur_start, addrs_in_block = None, []

        for ln in lines:
            m_hdr = hdr_re.match(ln)
            if m_hdr:
                # if there is a new block, the old one must be flushed
                flush_disasm_block()
                cur_start = int(m_hdr.group(1), 16)
                addrs_in_block = []
                continue
            m_insn = insn_re.match(ln)
            if m_insn and cur_start is not None:
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
if args.remove_eh:
    elf.remove_section(".dotnet_eh_table", clear=False)
    elf.remove_section(".eh_frame_hdr", clear=False)
    elf.remove_section(".eh_frame", clear=False)

elf.write(args.output_file)
print("ELF file postprocessed")
