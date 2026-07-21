using Zenith.Core.Logging;

namespace Zenith.Core.Cpu;

public class X86_64State
{
    public ulong Rax;
    public ulong Rbx;
    public ulong Rcx;
    public ulong Rdx;
    public ulong Rsi;
    public ulong Rdi;
    public ulong Rbp;
    public ulong Rsp;
    public ulong R8;
    public ulong R9;
    public ulong R10;
    public ulong R11;
    public ulong R12;
    public ulong R13;
    public ulong R14;
    public ulong R15;
    public ulong Rip;
    public ulong Rflags;
    public ulong Cr0 = 0x60000011;
    public ulong Cr2;
    public ulong Cr3;
    public ulong Cr4;
    public ulong Cr8;
    public ulong Efer = 0x500;
    public ulong FsBase;
    public ulong GsBase;

    public unsafe fixed ulong Xmm[16];
    public unsafe fixed ulong Ymm[16];
    public unsafe fixed ulong Zmm[16];

    public void Reset(ulong entryPoint, ulong stackPointer)
    {
        Rsp = stackPointer;
        Rip = entryPoint;
        Rflags = 0x202;
        Array.Clear(Xmm, 0, 16);
        Log.Info($"CPU reset: RIP=0x{entryPoint:X}, RSP=0x{stackPointer:X}");
    }

    public ref ulong GetGpr(int index)
    {
        return ref index switch
        {
            0 => ref Rax,
            1 => ref Rcx,
            2 => ref Rdx,
            3 => ref Rbx,
            4 => ref Rsp,
            5 => ref Rbp,
            6 => ref Rsi,
            7 => ref Rdi,
            8 => ref R8,
            9 => ref R9,
            10 => ref R10,
            11 => ref R11,
            12 => ref R12,
            13 => ref R13,
            14 => ref R14,
            15 => ref R15,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}
