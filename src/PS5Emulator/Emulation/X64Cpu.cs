using System.Runtime.CompilerServices;
using PS5Emulator.Logging;
using PS5Emulator.Memory;
using PS5Emulator.Emulation;

namespace PS5Emulator.Emulation;

public class X64Cpu
{
    private readonly MemoryManager _memory;
    public CpuState State { get; } = new();

    // ModRM helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadModRmVia(byte modrm, out int reg, out int rm, out bool hasSib)
    {
        var mod = (modrm >> 6) & 3;
        reg = (modrm >> 3) & 7;
        rm = modrm & 7;
        hasSib = false;

        if (mod == 3) return 0; // register mode
        if (mod == 0 && rm == 5) // rip-relative
        {
            var imm = unchecked((uint)_memory.ReadInt32(State.RIP));
            State.RIP += 4;
            return unchecked((uint)((long)State.RIP + imm));
        }

        if (mod == 0 && rm == 4) hasSib = true;
        else if (mod == 1) hasSib = rm == 4;
        else if (mod == 2) hasSib = rm == 4;

        return 0; // placeholder; real address calculation is complex and omitted for brevity in this research build
    }

    public X64Cpu(MemoryManager memory)
    {
        _memory = memory;
    }

    public void SetInitialState(ulong entryPoint)
    {
        State.RIP = entryPoint;
        // A research-friendly stack top. Real PS5 maps lower, but for research this keeps segfaults away from common stacks.
        State.RSP = 0x7000_0000_0000_0000UL;
        State.RFLAGS = 0x202;
        State.CS = 0x33;
        State.SS = 0x2B;
        Logger.Info("CPU", $"Initial CPU state: RIP=0x{State.RIP:X16}, RSP=0x{State.RSP:X16}");
    }

    public void Run(int maxInstructions = 100_000)
    {
        for (var i = 0; i < maxInstructions; i++)
        {
            Step();
            if (State.RIP == 0) break;
        }
        Logger.Info("CPU", $"Execution stopped after {maxInstructions} instructions. RIP=0x{State.RIP:X16}");
    }

    public void Step()
    {
        var rip = State.RIP;
        if (rip >= _memory.Size)
        {
            Logger.Error("CPU", $"RIP=0x{rip:X16} is outside memory. Halting.");
            State.RIP = 0;
            return;
        }

        var opcode = _memory.ReadUInt8(rip);
        State.RIP = rip + 1;

        switch (opcode)
        {
            case 0x00: case 0x01: case 0x02: case 0x03:
            case 0x08: case 0x09: case 0x0A: case 0x0B:
            case 0x10: case 0x11: case 0x12: case 0x13:
            case 0x18: case 0x19: case 0x1A: case 0x1B:
            case 0x20: case 0x21: case 0x22: case 0x23:
            case 0x28: case 0x29: case 0x2A: case 0x2B:
            case 0x30: case 0x31: case 0x32: case 0x33:
            case 0x38: case 0x39: case 0x3A: case 0x3B:
            case 0x88: case 0x89: case 0x8A: case 0x8B:
            case 0x8C: case 0x8D: case 0x8E: case 0x8F:
            case 0x84: case 0x85: case 0x86: case 0x87:
                ExecuteArithmeticOrMove(opcode);
                break;

            case 0x90: // NOP
                break;

            case 0xCC: // INT3
                Logger.Debug("CPU", "INT3 breakpoint encountered.");
                break;

            case 0xCD: // INT imm8
                var intVal = _memory.ReadUInt8(State.RIP);
                State.RIP += 1;
                HandleInt(intVal);
                break;

            case 0xCE: // INTO
                State.RIP += 1;
                break;

            case 0xF4: // HLT
                Logger.Info("CPU", "HLT reached.");
                State.RIP = 0;
                break;

            case 0xEA: // JMP ptr16:32 or ptr16:64
                ExecuteFarJump();
                break;

            case 0xEB: // JMP rel8
                var rel8 = unchecked((sbyte)_memory.ReadUInt8(State.RIP));
                State.RIP += 1;
                State.RIP += (ulong)(long)rel8;
                break;

            case 0xE8: // CALL rel32
                var rel32 = _memory.ReadInt32(State.RIP);
                State.RIP += 4;
                State.RSP -= 8;
                _memory.WriteUInt64(State.RSP, State.RIP);
                State.RIP += (ulong)(long)rel32;
                break;

            case 0xFF:
                ExecuteFFGroup();
                break;

            case 0x0F:
                ExecuteTwoByteOpcode();
                break;

            case 0xB0: case 0xB1: case 0xB2: case 0xB3: case 0xB4: case 0xB5: case 0xB6: case 0xB7:
            {
                var val = _memory.ReadUInt8(State.RIP);
                State.RIP += 1;
                var r = opcode & 7;
                State.SetGpr(r, (State.GetGpr(r) & ~0xFFUL) | val);
                break;
            }

            case 0xB8: case 0xB9: case 0xBA: case 0xBB: case 0xBC: case 0xBD: case 0xBE: case 0xBF:
            {
                var val = _memory.ReadUInt64(State.RIP);
                State.RIP += 8;
                var r = opcode & 7;
                State.SetGpr(r, val);
                break;
            }

            case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
            case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
            {
                var r = opcode & 7;
                if ((opcode & 0x08) == 0) // push
                {
                    State.RSP -= 8;
                    _memory.WriteUInt64(State.RSP, State.GetGpr(r));
                }
                else // pop
                {
                    State.SetGpr(r, _memory.ReadUInt64(State.RSP));
                    State.RSP += 8;
                }
                break;
            }

            case 0xC3: // RET
                State.RIP = _memory.ReadUInt64(State.RSP);
                State.RSP += 8;
                break;

            case 0xC9: // LEAVE
                State.RBP = State.RSP;
                State.RSP += 8;
                break;

            case 0xC7: // MOV Ev Iz / MOVS
            case 0xC6: // MOV Eb Ib
            {
                var modrm = _memory.ReadUInt8(State.RIP);
                State.RIP += 1;
                var rm = modrm & 7;
                var mod = (modrm >> 6) & 3;
                // Only handle register form for this research build
                if (mod == 3)
                {
                    if (opcode == 0xC7)
                    {
                        var imm = _memory.ReadUInt32(State.RIP);
                        State.RIP += 4;
                        State.SetGpr(rm, (State.GetGpr(rm) & ~0xFFFFFFFFUL) | imm);
                    }
                    else
                    {
                        var imm = _memory.ReadUInt8(State.RIP);
                        State.RIP += 1;
                        State.SetGpr(rm, (State.GetGpr(rm) & ~0xFFUL) | imm);
                    }
                }
                else
                {
                    Logger.Warn("CPU", $"MOV with memory destination at 0x{rip:X16} is not implemented. Halting.");
                    State.RIP = 0;
                }
                break;
            }

            case 0xA1: // MOV RAX moffs64
                State.RAX = _memory.ReadUInt64(State.RIP);
                State.RIP += 8;
                break;
            case 0xA3: // MOV moffs64 RAX
                _memory.WriteUInt64(State.RIP, State.RAX);
                State.RIP += 8;
                break;

            case 0xA4: case 0xA5: // MOVS
            case 0xA6: case 0xA7: // CMPS
                State.RIP += 1; // stub
                break;

            case 0xAF: case 0xAE: case 0xAD: case 0xAC: // SCAS, LODS, STOS
                State.RIP += 1; // stub
                break;

            default:
                Logger.Warn("CPU", $"Unknown opcode 0x{opcode:X2} at 0x{rip:X16}. Halting.");
                State.RIP = 0;
                break;
        }
    }

    private void ExecuteArithmeticOrMove(byte opcode)
    {
        var modrm = _memory.ReadUInt8(State.RIP);
        State.RIP += 1;

        var mod = (modrm >> 6) & 3;
        var reg = (modrm >> 3) & 7;
        var rm = modrm & 7;

        var op = (OpcodeGroup)(opcode & 0xF8);
        var size = (opcode & 7) switch { 0 or 1 or 4 or 5 => 1, 2 or 3 or 6 or 7 => 1, _ => 1 }; // research stub: assume 8/16/32/64 forms

        // Simplified: if mod == 3, both are registers. Otherwise memory.
        if (mod == 3)
        {
            var dst = State.GetGpr(rm);
            var src = State.GetGpr(reg);
            var result = PerformArithmetic(op, src, dst);
            State.SetGpr(rm, result);
        }
        else
        {
            // Memory form: we do not fully implement address calculation in this research build.
            // Instead we log and try a simple form when mod==0 and rm==5 (disp32) with reg as source.
            if (mod == 0 && rm == 5)
            {
                var imm32 = unchecked((uint)_memory.ReadInt32(State.RIP));
                State.RIP += 4;
                var addr = (ulong)imm32;
                var src = State.GetGpr(reg);

                switch (op)
                {
                    case OpcodeGroup.Mov:
                        if (opcode == 0x8B || opcode == 0x8A) // MOV r32/64, moffs
                        {
                            State.SetGpr(reg, _memory.ReadUInt64(addr));
                        }
                        else if (opcode == 0x89 || opcode == 0x88) // MOV moffs, r
                        {
                            _memory.WriteUInt64(addr, src);
                        }
                        else if (opcode == 0xA1 || opcode == 0xA3) { /* handled elsewhere */ }
                        else
                        {
                            _memory.WriteUInt64(addr, src);
                        }
                        break;
                    case OpcodeGroup.Add:
                        State.SetGpr(reg, _memory.ReadUInt64(addr) + src);
                        _memory.WriteUInt64(addr, State.GetGpr(reg));
                        break;
                    case OpcodeGroup.Sub:
                        State.SetGpr(reg, _memory.ReadUInt64(addr) - src);
                        _memory.WriteUInt64(addr, State.GetGpr(reg));
                        break;
                    case OpcodeGroup.Cmp:
                    case OpcodeGroup.Test:
                        // set flags only (stubbed)
                        break;
                    default:
                        Logger.Warn("CPU", $"Unimplemented memory arithmetic op=0x{opcode:X2} at 0x{State.RIP:X16}");
                        break;
                }
            }
            else
            {
                Logger.Warn("CPU", $"Complex ModRM decoding not yet implemented. mod={mod}, rm={rm}, reg={reg}, opcode=0x{opcode:X2}");
            }
        }
    }

    private ulong PerformArithmetic(OpcodeGroup op, ulong src, ulong dst)
    {
        return op switch
        {
            OpcodeGroup.Add => dst + src,
            OpcodeGroup.Or => dst | src,
            OpcodeGroup.Adc => dst + src + (State.RFLAGS & 1), // carry flag
            OpcodeGroup.Sbb => dst - src - (State.RFLAGS & 1),
            OpcodeGroup.And => dst & src,
            OpcodeGroup.Sub => dst - src,
            OpcodeGroup.Xor => dst ^ src,
            OpcodeGroup.Cmp => dst, // cmp is sub but doesn't write dst; flags stub
            OpcodeGroup.Test => dst, // test is and but doesn't write dst; flags stub
            OpcodeGroup.Mov => src,
            OpcodeGroup.Xchg => src, // simplified
            _ => dst
        };
    }

    private void ExecuteFFgroup()
    {
        var modrm = _memory.ReadUInt8(State.RIP);
        State.RIP += 1;
        var reg = (modrm >> 3) & 7;
        var rm = modrm & 7;
        var mod = (modrm >> 6) & 3;

        if (reg == 6) // PUSH Ev
        {
            if (mod == 3)
            {
                State.RSP -= 8;
                _memory.WriteUInt64(State.RSP, State.GetGpr(rm));
            }
            else
            {
                // stub memory push
                State.RIP = 0;
            }
        }
        else if (reg == 0) // INC Ev
        {
            if (mod == 3)
            {
                State.SetGpr(rm, State.GetGpr(rm) + 1);
            }
            else
            {
                State.RIP = 0;
            }
        }
        else if (reg == 1) // DEC Ev
        {
            if (mod == 3)
            {
                State.SetGpr(rm, State.GetGpr(rm) - 1);
            }
            else
            {
                State.RIP = 0;
            }
        }
        else if (reg == 2) // CALL Ev
        {
            if (mod == 3)
            {
                State.RSP -= 8;
                _memory.WriteUInt64(State.RSP, State.RIP);
                State.RIP = State.GetGpr(rm);
            }
            else
            {
                State.RIP = 0;
            }
        }
        else if (reg == 4) // JMP Ev
        {
            if (mod == 3)
            {
                State.RIP = State.GetGpr(rm);
            }
            else
            {
                State.RIP = 0;
            }
        }
        else
        {
            Logger.Warn("CPU", $"Unsupported FF group reg={reg} at 0x{State.RIP:X16}");
            State.RIP = 0;
        }
    }

    private void ExecuteTwoByteOpcode()
    {
        var opcode = _memory.ReadUInt8(State.RIP);
        State.RIP += 1;

        switch (opcode)
        {
            case 0x05: // SYSCALL
                HandleSyscall();
                break;

            case 0x3F: // NOP (two-byte)
                break;

            case 0x80: case 0x81: case 0x82: case 0x83: case 0x84: case 0x85: case 0x86: case 0x87:
            case 0x88: case 0x89: case 0x8A: case 0x8B: case 0x8C: case 0x8D: case 0x8E: case 0x8F:
                // Reserved or future extensions; mostly unimplemented in this research build
                Logger.Warn("CPU", $"Unimplemented 0x0F 0x{opcode:X2} at 0x{State.RIP:X16}");
                State.RIP = 0;
                break;

            default:
                Logger.Warn("CPU", $"Unknown 0x0F opcode 0x{opcode:X2} at 0x{State.RIP:X16}. Halting.");
                State.RIP = 0;
                break;
        }
    }

    private void HandleFarJump()
    {
        var selector = _memory.ReadUInt16(State.RIP);
        var offset = _memory.ReadUInt32(State.RIP);
        State.RIP += 4;
        State.CS = selector;
        State.RIP = (ulong)(long)offset;
    }

    private void HandleInt(int val)
    {
        // BIOS/software interrupts. In real PS5 these are gone; we route to kernel.
        Logger.Debug("CPU", $"INT 0x{val:X2} at 0x{State.RIP:X16}");
        if (val == 0x80) // research-bios software interrupt
        {
            // leave RIP after INT, let caller handle result
        }
    }

    private void HandleSyscall()
    {
        // Read syscall number from RAX
        var num = State.RAX;
        var args = new ulong[6];
        args[0] = State.RDI;
        args[1] = State.RSI;
        args[2] = State.RDX;
        args[3] = State.R10;
        args[4] = State.R8;
        args[5] = State.R9;

        Logger.Debug("CPU", $"SYSCALL num={num} di=0x{args[0]:X16} si=0x{args[1]:X16} dx=0x{args[2]:X16}");

        var result = SyscallHandler.Handle(num, args, _memory);
        State.RAX = result;
    }

    private enum OpcodeGroup : byte
    {
        Add = 0x00, Or = 0x08, Adc = 0x10, Sbb = 0x18, And = 0x20, Sub = 0x28, Xor = 0x30, Cmp = 0x38,
        Mov = 0x88, Xchg = 0x86, Test = 0x84
    }
}
