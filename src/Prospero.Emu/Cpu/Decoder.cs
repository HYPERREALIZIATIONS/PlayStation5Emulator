using System;

namespace Prospero.Emu.Cpu
{
    /// <summary>
    /// Compact x86-64 decoder supporting the instruction subset the CPU
    /// implements. It computes effective addresses through a register accessor
    /// supplied by the CPU, which keeps the ModRM/SIB logic correct and simple.
    /// </summary>
    public sealed class Decoder
    {
        public byte[] Code = Array.Empty<byte>();
        public int Pc;                 // index into Code

        public bool RexW;
        public int RexReg, RexIndex, RexBase;
        public bool RexPresent;

        public byte Peek(int ahead = 0)
        {
            int i = Pc + ahead;
            return i < Code.Length ? Code[i] : (byte)0;
        }

        public byte ReadByte()
        {
            if (Pc >= Code.Length) throw new CpuFault("read past end of code block");
            return Code[Pc++];
        }

        public ushort ReadU16()
        {
            byte lo = ReadByte(), hi = ReadByte();
            return (ushort)(lo | (hi << 8));
        }

        public uint ReadU32()
        {
            uint v = 0;
            for (int i = 0; i < 4; i++) v |= (uint)ReadByte() << (8 * i);
            return v;
        }

        public ulong ReadU64()
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)ReadByte() << (8 * i);
            return v;
        }

        public void DecodePrefixes()
        {
            RexW = false; RexReg = RexIndex = RexBase = 0; RexPresent = false;
            while (true)
            {
                byte b = Peek();
                if (b >= 0x40 && b <= 0x4F)
                {
                    RexPresent = true;
                    RexW = (b & 0x08) != 0;
                    RexReg = (b & 0x04) != 0 ? 1 : 0;
                    RexIndex = (b & 0x02) != 0 ? 1 : 0;
                    RexBase = (b & 0x01) != 0 ? 1 : 0;
                    Pc++;
                    continue;
                }
                break;
            }
        }

        public readonly struct ModRm { public byte Mod, Reg, Rm; }

        public ModRm ReadModRm()
        {
            byte m = ReadByte();
            return new ModRm
            {
                Mod = (byte)(m >> 6),
                Reg = (byte)((m >> 3) & 7),
                Rm = (byte)(m & 7),
            };
        }

        /// <summary>Register index (0..15) of the reg field with REX.R applied.</summary>
        public int RegIndex(ModRm mr) => mr.Reg | (RexReg != 0 ? 8 : 0);

        /// <summary>
        /// Compute the effective address for a memory operand.
        /// startRip is the guest address of the *start* of this instruction; the
        /// RIP-relative displacement is computed relative to the address of the
        /// instruction that follows (i.e. startRip + length), which we derive
        /// from the decoder position after consuming the displacement bytes.
        /// </summary>
        public ulong EffectiveAddress(ModRm mr, Func<int, ulong> regVal, ulong startRip)
        {
            if (mr.Mod == 3)
                throw new CpuFault("EffectiveAddress called on register operand");

            int rm = mr.Rm | (RexBase != 0 ? 8 : 0);

            if (mr.Mod == 0)
            {
                if (mr.Rm == 4) // SIB
                {
                    var (baseVal, indexVal, disp) = DecodeSib(mr, 0, regVal);
                    return baseVal + indexVal + disp;
                }
                if (mr.Rm == 5) // RIP-relative
                {
                    int disp = (int)ReadU32();
                    ulong ripHere = startRip + (ulong)Pc; // Pc is now past the disp
                    return ripHere + (ulong)disp;
                }
                return regVal(rm);
            }
            if (mr.Mod == 1)
            {
                sbyte disp = (sbyte)ReadByte();
                if (mr.Rm == 4)
                {
                    var (baseVal, indexVal, d) = DecodeSib(mr, disp, regVal);
                    return baseVal + indexVal + d;
                }
                return regVal(rm) + (ulong)disp;
            }
            // mod == 2
            int disp32 = (int)ReadU32();
            if (mr.Rm == 4)
            {
                var (baseVal, indexVal, d) = DecodeSib(mr, disp32, regVal);
                return baseVal + indexVal + d;
            }
            return regVal(rm) + (ulong)disp32;
        }

        private (ulong baseVal, ulong indexVal, long disp) DecodeSib(ModRm mr, long dispIn, Func<int, ulong> regVal)
        {
            byte sib = ReadByte();
            int scale = 1 << (sib >> 6);
            int index = (sib >> 3) & 7;
            int bas = sib & 7;
            index |= (RexIndex != 0 ? 8 : 0);
            bas |= (RexBase != 0 ? 8 : 0);

            ulong baseVal = 0;
            if (bas != 5 || mr.Mod != 0)
                baseVal = regVal(bas);

            ulong indexVal = 0;
            if (index != 4) // index==4(rsp) means "no index"
                indexVal = regVal(index) * (ulong)scale;

            return (baseVal, indexVal, dispIn);
        }
    }

    public sealed class CpuFault : Exception
    {
        public CpuFault(string m) : base(m) { }
        public CpuFault(string m, Exception e) : base(m, e) { }
    }
}
