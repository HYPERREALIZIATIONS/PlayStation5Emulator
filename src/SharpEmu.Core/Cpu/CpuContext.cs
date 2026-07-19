using System;
using System.Runtime.CompilerServices;

namespace SharpEmu.Core.Cpu;

/// <summary>
/// x86-64 general purpose register file plus RIP/RSP/RFLAGS.
/// System V / FreeBSD ABI register order is used (RDI, RSI, RDX, RCX, R8, R9 = args).
/// </summary>
public sealed class CpuContext
{
    // RAX, RCX, RDX, RBX, RSP, RBP, RSI, RDI, R8..R15
    public ulong[] Gpr = new ulong[16];
    public ulong Rip;
    public ulong Rflags;

    // XMM (128-bit) registers, stored as two ulongs (lo, hi). Used minimally.
    public ulong[][] Xmm = new ulong[16][];

    public CpuContext()
    {
        for (int i = 0; i < 16; i++) Xmm[i] = new ulong[2];
    }

    public ulong Rax { get => Gpr[0]; set => Gpr[0] = value; }
    public ulong Rcx { get => Gpr[1]; set => Gpr[1] = value; }
    public ulong Rdx { get => Gpr[2]; set => Gpr[2] = value; }
    public ulong Rbx { get => Gpr[3]; set => Gpr[3] = value; }
    public ulong Rsp { get => Gpr[4]; set => Gpr[4] = value; }
    public ulong Rbp { get => Gpr[5]; set => Gpr[5] = value; }
    public ulong Rsi { get => Gpr[6]; set => Gpr[6] = value; }
    public ulong Rdi { get => Gpr[7]; set => Gpr[7] = value; }
    public ulong R8 { get => Gpr[8]; set => Gpr[8] = value; }
    public ulong R9 { get => Gpr[9]; set => Gpr[9] = value; }
    public ulong R10 { get => Gpr[10]; set => Gpr[10] = value; }
    public ulong R11 { get => Gpr[11]; set => Gpr[11] = value; }
    public ulong R12 { get => Gpr[12]; set => Gpr[12] = value; }
    public ulong R13 { get => Gpr[13]; set => Gpr[13] = value; }
    public ulong R14 { get => Gpr[14]; set => Gpr[14] = value; }
    public ulong R15 { get => Gpr[15]; set => Gpr[15] = value; }

    // RFLAGS bits
    public const ulong CF = 1;
    public const ulong PF = 1 << 2;
    public const ulong AF = 1 << 4;
    public const ulong ZF = 1 << 6;
    public const ulong SF = 1 << 7;
    public const ulong OF = 1 << 11;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetFlag(ulong flag) => (Rflags & flag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetFlag(ulong flag, bool v)
    {
        if (v) Rflags |= flag; else Rflags &= ~flag;
    }
}
