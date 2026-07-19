using System;
using System.Runtime.CompilerServices;
using Prospero.Emu.Core;

namespace Prospero.Emu.Cpu
{
    /// <summary>
    /// XMM (128-bit SIMD) register helpers and a best-effort x86-64 instruction
    /// length decoder used for graceful fallback when the interpreter hits an
    /// opcode it does not yet implement (so a research run can skip it and keep
    /// going, logging what was seen, rather than hard-faulting immediately).
    ///
    /// This is deliberately conservative: when in doubt it errs toward NOT
    /// skipping (so we never corrupt RIP) and lets the caller fault.
    /// </summary>
    public sealed class CpuExtensions
    {
        private readonly Decoder _dec;
        private readonly CpuCore _cpu;

        public CpuExtensions(Decoder dec, CpuCore cpu)
        {
            _dec = dec;
            _cpu = cpu;
        }

        // ---- XMM register access (stored as two 64-bit halves) ----
        public ulong XmmLo(int i) => _cpu.Regs.XmmLo[i];
        public ulong XmmHi(int i) => _cpu.Regs.XmmHi[i];
        public void SetXmm(int i, ulong lo, ulong hi) { _cpu.Regs.XmmLo[i] = lo; _cpu.Regs.XmmHi[i] = hi; }

        public void ReadXmm(ulong addr, out ulong lo, out ulong hi)
        {
            lo = _cpu.Mem.ReadU64(addr);
            hi = _cpu.Mem.ReadU64(addr + 8);
        }
        public void WriteXmm(ulong addr, ulong lo, ulong hi)
        {
            _cpu.Mem.WriteU64(addr, lo);
            _cpu.Mem.WriteU64(addr + 8, hi);
        }

        /// <summary>Attempt to compute the length of the instruction at the
        /// decoder's current position (after prefixes have been consumed) so the
        /// CPU can skip it. Returns -1 if it cannot be determined safely.</summary>
        public int TryLengthAfterPrefixes(byte op)
        {
            try
            {
                int len = 1; // the opcode itself
                switch (op)
                {
                    case 0x0F: // two-byte
                        {
                            byte op2 = _dec.Peek(0);
                            _dec.ReadByte(); len++;
                            return 1 + TwoByteLength(op2, ref len);
                        }
                    case 0xF2: case 0xF3: case 0x66: // prefixes already handled
                    case 0x2E: case 0x36: case 0x3E: case 0x26: // segment overrides
                        return -1; // shouldn't be called post-prefix
                    case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                        // group 2 shifts: modrm + (imm8 for C0/C1, none for D0-D3)
                        len += ModRmLen();
                        if (op == 0xC0 || op == 0xC1) { _dec.ReadByte(); len++; }
                        return len;
                    case 0xF6: case 0xF7: // group 3 (test/div/mul)
                    case 0xFE: case 0xFF: // group 4/5
                        len += ModRmLen();
                        return len;
                    case 0x80: case 0x81: case 0x82: case 0x83: // alu imm
                        len += ModRmLen();
                        // 0x80/0x82 -> imm8 ; 0x81 imm32 ; 0x83 imm8 (sign-extended)
                        if (op == 0x81) { _dec.ReadU32(); len += 4; }
                        else { _dec.ReadByte(); len += 1; }
                        return len;
                    case 0xC6: case 0xC7: // mov imm
                        len += ModRmLen();
                        if (op == 0xC7 && _dec.RexW) { _dec.ReadU64(); len += 8; }
                        else if (op == 0xC7) { _dec.ReadU32(); len += 4; }
                        else { _dec.ReadByte(); len += 1; }
                        return len;
                    case 0x8D: case 0x63: // lea / movsxd (modrm)
                    case 0x88: case 0x89: case 0x8A: case 0x8B:
                    case 0x84: case 0x85: case 0x86: case 0x87:
                    case 0x00: case 0x01: case 0x02: case 0x03:
                    case 0x08: case 0x09: case 0x0A: case 0x0B:
                    case 0x10: case 0x11: case 0x12: case 0x13:
                    case 0x18: case 0x19: case 0x1A: case 0x1B:
                    case 0x20: case 0x21: case 0x22: case 0x23:
                    case 0x28: case 0x29: case 0x2A: case 0x2B:
                    case 0x30: case 0x31: case 0x32: case 0x33:
                    case 0x38: case 0x39: case 0x3A: case 0x3B:
                        len += ModRmLen();
                        return len;
                    case 0x50: case 0x51: case 0x52: case 0x53:
                    case 0x54: case 0x55: case 0x56: case 0x57:
                    case 0x58: case 0x59: case 0x5A: case 0x5B:
                    case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                    case 0x90: // push/pop/nop (no modrm)
                    case 0x91: case 0x92: case 0x93: case 0x94:
                    case 0x95: case 0x96: case 0x97: case 0x98:
                    case 0x99: case 0x9B: case 0x9C: case 0x9D:
                    case 0x9E: case 0x9F:
                    case 0xC3: case 0xC9: case 0xCB: case 0xCC:
                    case 0xA8: case 0xCC: case 0xF4: case 0x90:
                        return len;
                    case 0x68: case 0xA9: case 0xB8: case 0xB9:
                    case 0xBA: case 0xBB: case 0xBC: case 0xBD:
                    case 0xBE: case 0xBF: // push imm32 / test eax / mov r,i
                        _dec.ReadU32(); len += 4; return len;
                    case 0x6A: case 0x6B: case 0x6C: case 0x6D:
                    case 0xB0: case 0xB1: case 0xB2: case 0xB3:
                    case 0xB4: case 0xB5: case 0xB6: case 0xB7: // push imm8 / mov r8,i8
                        _dec.ReadByte(); len += 1; return len;
                    case 0xE8: case 0xE9: // call/jmp rel32
                        _dec.ReadU32(); len += 4; return len;
                    case 0xEB: // jmp rel8
                        _dec.ReadByte(); len += 1; return len;
                    case 0x70: case 0x71: case 0x72: case 0x73:
                    case 0x74: case 0x75: case 0x76: case 0x77:
                    case 0x78: case 0x79: case 0x7A: case 0x7B:
                    case 0x7C: case 0x7D: case 0x7E: case 0x7F: // jcc rel8
                        _dec.ReadByte(); len += 1; return len;
                    case 0xA0: case 0xA1: case 0xA2: case 0xA3: // mov off
                        if (op == 0xA1 || op == 0xA3) { _dec.ReadU32(); len += 4; }
                        else { _dec.ReadByte(); len += 1; }
                        return len;
                    default:
                        return -1;
                }
            }
            catch
            {
                return -1;
            }
        }

        private int TwoByteLength(byte op2, ref int len)
        {
            switch (op2)
            {
                case 0x10: case 0x11: case 0x12: case 0x13:
                case 0x14: case 0x15: case 0x16: case 0x17:
                case 0x18: case 0x19: case 0x1A: case 0x1B:
                case 0x1C: case 0x1D: case 0x1E: case 0x1F:
                case 0x28: case 0x29: case 0x2A: case 0x2B:
                case 0x2C: case 0x2D: case 0x2E: case 0x2F:
                case 0x40: case 0x41: case 0x42: case 0x43:
                case 0x44: case 0x45: case 0x46: case 0x47:
                case 0x48: case 0x49: case 0x4A: case 0x4B:
                case 0x4C: case 0x4D: case 0x4E: case 0x4F:
                case 0x50: case 0x51: case 0x52: case 0x53:
                case 0x54: case 0x55: case 0x56: case 0x57:
                case 0x58: case 0x59: case 0x5A: case 0x5B:
                case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                case 0x60: case 0x61: case 0x62: case 0x63:
                case 0x64: case 0x65: case 0x66: case 0x67:
                case 0x6E: case 0x6F: case 0x70: case 0x71:
                case 0x72: case 0x73: case 0x74: case 0x75:
                case 0x76: case 0x77: case 0x78: case 0x79:
                case 0x7A: case 0x7B: case 0x7C: case 0x7D:
                case 0x7E: case 0x7F:
                case 0x80: case 0x81: case 0x82: case 0x83:
                case 0x84: case 0x85: case 0x86: case 0x87:
                case 0x88: case 0x89: case 0x8A: case 0x8B:
                case 0x8C: case 0x8D: case 0x8E: case 0x8F:
                case 0x90: case 0x91: case 0x92: case 0x93:
                case 0x94: case 0x95: case 0x96: case 0x97:
                case 0x98: case 0x99: case 0x9A: case 0x9B:
                case 0x9C: case 0x9D: case 0x9E: case 0x9F:
                case 0xA0: case 0xA1: case 0xA2: case 0xA3:
                case 0xA4: case 0xA5: case 0xA6: case 0xA7:
                case 0xA8: case 0xA9: case 0xAA: case 0xAB:
                case 0xAC: case 0xAD: case 0xAE: case 0xAF:
                case 0xB0: case 0xB1: case 0xB2: case 0xB3:
                case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                case 0xB8: case 0xB9: case 0xBA: case 0xBB:
                case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                case 0xC0: case 0xC1: case 0xC2: case 0xC3:
                case 0xC4: case 0xC5: case 0xC6: case 0xC7:
                case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                case 0xD4: case 0xD5: case 0xD6: case 0xD7:
                case 0xD8: case 0xD9: case 0xDA: case 0xDB:
                case 0xDC: case 0xDD: case 0xDE: case 0xDF:
                case 0xE0: case 0xE1: case 0xE2: case 0xE3:
                case 0xE4: case 0xE5: case 0xE6: case 0xE7:
                case 0xE8: case 0xE9: case 0xEA: case 0xEB:
                case 0xEC: case 0xED: case 0xEE: case 0xEF:
                case 0xF0: case 0xF1: case 0xF2: case 0xF3:
                case 0xF4: case 0xF5: case 0xF6: case 0xF7:
                    len += ModRmLen();
                    return len;
                case 0xB6: case 0xB7: case 0xBE: case 0xBF: // movzx/movsx
                    len += ModRmLen();
                    return len;
                case 0x05: // syscall
                    return len;
                case 0x06: case 0x07: case 0x34: case 0x35: // clts/sysenter/etc
                    return len;
                case 0x31: case 0x32: case 0x33: // rdtsc/rdmsr/wrmsr
                    return len;
                case 0x77: // emms
                case 0x2E: case 0x2F: // ucomiss/comiss
                    len += ModRmLen();
                    return len;
                default:
                    return -1;
            }
        }

        private int ModRmLen()
        {
            if (_dec.Pc >= _dec.Code.Length) throw new InvalidOperationException();
            byte m = _dec.Peek(0);
            _dec.ReadByte();
            int mod = m >> 6;
            int rm = m & 7;
            int len = 1;
            if (mod == 3) return len;
            if (rm == 4) // SIB
            {
                byte sib = _dec.Peek(0);
                _dec.ReadByte();
                len++;
                int basef = sib & 7;
                if (basef == 5 && mod == 0) { _dec.ReadU32(); len += 4; } // disp32
            }
            if (mod == 1) { _dec.ReadByte(); len += 1; }
            else if (mod == 2) { _dec.ReadU32(); len += 4; }
            return len;
        }
    }
}
