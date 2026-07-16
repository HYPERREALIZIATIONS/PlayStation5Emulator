using System;

namespace Prospero.Emu.Cpu
{
    /// <summary>
    /// x86-64 general purpose register file + RIP + RFLAGS.
    /// PS5 uses a Zen 2 CPU (x86-64, with AVX2 etc.). For research we model the
    /// legacy + 64-bit integer register set and a subset of SSE/AVX as needed.
    /// </summary>
    public sealed class Registers
    {
        public ulong[] Gp = new ulong[16];   // RAX..R15
        public ulong Rip;
        public ulong Rflags;

        // XMM (128-bit) registers, stored as two 64-bit halves for simplicity.
        public ulong[] XmmLo = new ulong[16];
        public ulong[] XmmHi = new ulong[16];

        public const ulong FLAG_CF = 1UL << 0;
        public const ulong FLAG_PF = 1UL << 2;
        public const ulong FLAG_AF = 1UL << 4;
        public const ulong FLAG_ZF = 1UL << 6;
        public const ulong FLAG_SF = 1UL << 7;
        public const ulong FLAG_OF = 1UL << 11;

        public ulong this[int i]
        {
            get => Gp[i];
            set => Gp[i] = value;
        }

        public void SetZfSf(ulong result, int opSizeBits)
        {
            ulong signBit = 1UL << (opSizeBits - 1);
            if (opSizeBits < 64)
            {
                ulong mask = (1UL << opSizeBits) - 1;
                result &= mask;
            }
            Rflags = (Rflags & ~(FLAG_ZF | FLAG_SF)) |
                     (result == 0 ? FLAG_ZF : 0) |
                     ((result & signBit) != 0 ? FLAG_SF : 0);
        }

        public bool Zf => (Rflags & FLAG_ZF) != 0;
        public bool Sf => (Rflags & FLAG_SF) != 0;
        public bool Cf => (Rflags & FLAG_CF) != 0;
        public bool Of => (Rflags & FLAG_OF) != 0;

        public void SetCf(bool v) => Rflags = v ? Rflags | FLAG_CF : Rflags & ~FLAG_CF;
        public void SetOf(bool v) => Rflags = v ? Rflags | FLAG_OF : Rflags & ~FLAG_OF;
        public void SetAf(bool v) => Rflags = v ? Rflags | FLAG_AF : Rflags & ~FLAG_AF;

        public void Dump()
        {
            var names = new[] { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
                                "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };
            for (int i = 0; i < 16; i++)
            {
                if (i % 4 == 0) Console.Write("  ");
                Console.Write($"{names[i],3}=0x{Gp[i]:X16} ");
                if (i % 4 == 3) Console.WriteLine();
            }
            if (Rip != 0) Console.WriteLine($"  rip=0x{Rip:X16} rflags=0x{Rflags:X16}");
        }
    }
}
