using System.Runtime.CompilerServices;

namespace Zenith.Core.Cpu;

public readonly ref struct X86_64Registers
{
    public readonly ulong Rax;
    public readonly ulong Rbx;
    public readonly ulong Rcx;
    public readonly ulong Rdx;
    public readonly ulong Rsi;
    public readonly ulong Rdi;
    public readonly ulong Rbp;
    public readonly ulong Rsp;
    public readonly ulong R8;
    public readonly ulong R9;
    public readonly ulong R10;
    public readonly ulong R11;
    public readonly ulong R12;
    public readonly ulong R13;
    public readonly ulong R14;
    public readonly ulong R15;
    public readonly ulong Rip;
    public readonly ulong Rflags;
    public readonly ulong Cr0;
    public readonly ulong Cr2;
    public readonly ulong Cr3;
    public readonly ulong Cr4;
    public readonly ulong Cr8;
    public readonly ulong Efer;
    public readonly ulong FsBase;
    public readonly ulong GsBase;
    public readonly unsafe fixed ulong Xmm[16];

    public X86_64Registers(
        ulong rax = 0, ulong rbx = 0, ulong rcx = 0, ulong rdx = 0,
        ulong rsi = 0, ulong rdi = 0, ulong rbp = 0, ulong rsp = 0,
        ulong r8 = 0, ulong r9 = 0, ulong r10 = 0, ulong r11 = 0,
        ulong r12 = 0, ulong r13 = 0, ulong r14 = 0, ulong r15 = 0,
        ulong rip = 0, ulong rflags = 0x202, ulong cr0 = 0x60000011,
        ulong cr2 = 0, ulong cr3 = 0, ulong cr4 = 0, ulong cr8 = 0,
        ulong efer = 0x500, ulong fsBase = 0, ulong gsBase = 0)
    {
        Rax = rax; Rbx = rbx; Rcx = rcx; Rdx = rdx;
        Rsi = rsi; Rdi = rdi; Rbp = rbp; Rsp = rsp;
        R8 = r8; R9 = r9; R10 = r10; R11 = r11;
        R12 = r12; R13 = r13; R14 = r14; R15 = r15;
        Rip = rip; Rflags = rflags;
        Cr0 = cr0; Cr2 = cr2; Cr3 = cr3; Cr4 = cr4; Cr8 = cr8;
        Efer = efer; FsBase = fsBase; GsBase = gsBase;
    }

    public X86_64Registers WithRip(ulong rip) => new X86_64Registers(
        Rax, Rbx, Rcx, Rdx, Rsi, Rdi, Rbp, Rsp,
        R8, R9, R10, R11, R12, R13, R14, R15,
        rip, Rflags, Cr0, Cr2, Cr3, Cr4, Cr8, Efer, FsBase, GsBase);

    public X86_64Registers WithRax(ulong rax) => new X86_64Registers(
        rax, Rbx, Rcx, Rdx, Rsi, Rdi, Rbp, Rsp,
        R8, R9, R10, R11, R12, R13, R14, R15,
        Rip, Rflags, Cr0, Cr2, Cr3, Cr4, Cr8, Efer, FsBase, GsBase);
}

public static class RegistersExtensions
{
    public static ref ulong GetGpr(this ref X86_64Registers regs, int index)
    {
        return ref index switch
        {
            0 => ref regs.GetRaxMut(),
            1 => ref regs.GetRcxMut(),
            2 => ref regs.GetRdxMut(),
            3 => ref regs.GetRbxMut(),
            4 => ref regs.GetRspMut(),
            5 => ref regs.GetRbpMut(),
            6 => ref regs.GetRsiMut(),
            7 => ref regs.GetRdiMut(),
            >= 8 && <= 15 => ref GetExtendedGprMut(ref regs, index),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    public static ref ulong GetExtendedGprMut(ref X86_64Registers regs, int index)
    {
        return index switch
        {
            8 => ref regs.GetR8Mut(),
            9 => ref regs.GetR9Mut(),
            10 => ref regs.GetR10Mut(),
            11 => ref regs.GetR11Mut(),
            12 => ref regs.GetR12Mut(),
            13 => ref regs.GetR13Mut(),
            14 => ref regs.GetR14Mut(),
            15 => ref regs.GetR15Mut(),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public static ref ulong GetRaxMut(this ref X86_64Registers regs) => ref Unsafe.AsRef(in regs.Rax); // hack for mutability
}
