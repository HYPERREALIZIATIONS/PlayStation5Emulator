using System.Runtime.CompilerServices;
using PS5Emulator.Logging;
using PS5Emulator.Memory;
using PS5Emulator.Models;

namespace PS5Emulator.Emulation;

public class CpuState
{
    public ulong RAX, RBX, RCX, RDX, RSI, RDI, RBP, RSP;
    public ulong R8, R9, R10, R11, R12, R13, R14, R15;
    public ulong RIP;
    public ulong RFLAGS;

    public ushort CS, DS, ES, FS, GS, SS;

    // Helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetGpr(int index)
    {
        return index switch
        {
            0 => RAX, 1 => RCX, 2 => RDX, 3 => RBX,
            4 => RSP, 5 => RBP, 6 => RSI, 7 => RDI,
            8 => R8, 9 => R9, 10 => R10, 11 => R11,
            12 => R12, 13 => R13, 14 => R14, 15 => R15,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGpr(int index, ulong value)
    {
        switch (index)
        {
            case 0: RAX = value; break;
            case 1: RCX = value; break;
            case 2: RDX = value; break;
            case 3: RBX = value; break;
            case 4: RSP = value; break;
            case 5: RBP = value; break;
            case 6: RSI = value; break;
            case 7: RDI = value; break;
            case 8: R8 = value; break;
            case 9: R9 = value; break;
            case 10: R10 = value; break;
            case 11: R11 = value; break;
            case 12: R12 = value; break;
            case 13: R13 = value; break;
            case 14: R14 = value; break;
            case 15: R15 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
