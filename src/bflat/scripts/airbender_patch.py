#!/usr/bin/env python3
"""Rewrites the native instructions of an Airbender ELF32 image that Airbender cannot run.

- FENCE and FENCE.I become NOP: Airbender rejects them, and a single-hart guest has nothing to order.
- The signed RV32M instructions (mulh, mulhsu, div, rem) move out of line: Airbender v3 proves only the
  unsigned ones. Each becomes a jump to a trampoline that computes the same result from unsigned
  instructions, with RISC-V's results for a zero divisor and for MinValue / -1, saving its scratch registers
  on the stack. The trampolines go into __signed_tramp_start..__signed_tramp_end, which the linker script
  reserves at the end of .text so every native site is within a jump's reach.

The JIT emits neither for managed code (no fences in stubs, JitRiscVUnsignedMulDiv); the C/C++ compilers emit
both. Only words inside a sized function are rewritten.

Usage: airbender_patch.py <elf>  (rewrites the ELF in place)
"""
import bisect
import struct
import sys

path = sys.argv[1]
data = bytearray(open(path, "rb").read())
assert data[:4] == b"\x7fELF" and data[4] == 1, "ELF32 expected"
e_shoff, = struct.unpack_from("<I", data, 0x20)
e_shentsize, e_shnum = struct.unpack_from("<HH", data, 0x2E)

sections = []
for i in range(e_shnum):
    off = e_shoff + i * e_shentsize
    sections.append(struct.unpack_from("<IIIIIIIIII", data, off))  # name type flags addr offset size link info align entsize


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
    raise SystemExit(f"airbender_patch: symbol {name} not found")


# Only words inside a sized function are instructions: ILC parks data in .text too, and a data word that
# happens to look like one of these instructions must keep its value. The native code they come from always
# has sized function symbols.
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
    raise SystemExit(f"airbender_patch: address {addr:#x} is not in a PROGBITS section")


def r_type(f7, rs2, rs1, f3, rd, op=0x33):
    return (f7 << 25) | (rs2 << 20) | (rs1 << 15) | (f3 << 12) | (rd << 7) | op


def i_type(imm, rs1, f3, rd, op):
    return ((imm & 0xFFF) << 20) | (rs1 << 15) | (f3 << 12) | (rd << 7) | op


def s_type(imm, rs2, rs1, f3=2):
    return (((imm >> 5) & 0x7F) << 25) | (rs2 << 20) | (rs1 << 15) | (f3 << 12) | ((imm & 0x1F) << 7) | 0x23


def jal(rd, offset):
    assert -(1 << 20) <= offset < (1 << 20) and offset % 2 == 0, f"jump of {offset:#x} out of reach"
    imm = offset & 0x1FFFFF
    return (((imm >> 20) & 1) << 31) | (((imm >> 1) & 0x3FF) << 21) | (((imm >> 11) & 1) << 20) | (((imm >> 12) & 0xFF) << 12) | (rd << 7) | 0x6F


ZERO, SP = 0, 2
add = lambda rd, a, b: r_type(0, b, a, 0, rd)
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


tramp_start = tramp = symbol("__signed_tramp_start")
tramp_end = symbol("__signed_tramp_end")
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
        is_fence = (insn & 0x7F) == 0x0F and f3 in (0, 1)  # MISC-MEM: FENCE, FENCE.I
        is_signed = (insn & 0x7F) == 0x33 and (insn >> 25) == 1 and f3 in (1, 2, 4, 6)
        if not (is_fence or is_signed):
            continue
        if not in_function(addr):
            skipped += 1
            continue
        if is_fence:
            struct.pack_into("<I", data, p, NOP)
            fences += 1
            continue
        if rd == ZERO:
            struct.pack_into("<I", data, p, NOP)
            patched += 1
            continue
        assert SP not in (rd, a, b), f"stack pointer operand at {addr:#x}"
        t0, t1, t2 = [r for r in (5, 6, 7, 28, 29, 30, 31) if r not in (rd, a, b)][:3]
        code = [addi(SP, SP, -16), sw(t0, SP, 0), sw(t1, SP, 4), sw(t2, SP, 8)]
        code += body(f3, a, b, t0, t1, t2)
        code += [sw(t0, SP, 12), lw(t0, SP, 0), lw(t1, SP, 4), lw(t2, SP, 8), lw(rd, SP, 12), addi(SP, SP, 16)]
        code.append(jal(ZERO, addr + 4 - (tramp + 4 * len(code))))
        if tramp + 4 * len(code) > tramp_end:
            raise SystemExit("airbender_patch: trampoline area full")
        struct.pack_into(f"<{len(code)}I", data, file_offset(tramp), *code)
        struct.pack_into("<I", data, p, jal(ZERO, tramp - addr))
        tramp += 4 * len(code)
        patched += 1
open(path, "wb").write(data)
print(f"airbender_patch: {fences} fence(s) to NOP, {patched} signed mul/div moved out of line; "
      f"{skipped} matching word(s) outside any function left untouched")
