using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Zenith.Core.Logging;
using Zenith.Core.Memory;
using Zenith.Core.Os;

namespace Zenith.Core.Cpu;

public sealed class Interpreter
{
    private const ulong PageMask = ~(ulong)0xFFF;
    private readonly MemoryManager _memory;
    private readonly SyscallHandler _syscallHandler;
    private readonly X86_64State _state = new();
    private readonly Dictionary<ulong, CompiledBlock> _jitCache = new();
    private bool _running;
    private ulong _lastRip;

    public X86_64State State => _state;

    public Interpreter(MemoryManager memory, SyscallHandler syscallHandler)
    {
        _memory = memory;
        _syscallHandler = syscallHandler;
    }

    public void Run(ulong entryPoint, ulong stackPointer)
    {
        _state.Reset(entryPoint, stackPointer);
        _running = true;
        _lastRip = entryPoint;

        try
        {
            while (_running)
            {
                ExecuteBlock();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal($"CPU halted: {ex.Message}");
            DumpState();
            throw;
        }
    }

    public void Stop() => _running = false;

    private void ExecuteBlock()
    {
        var start = _state.Rip;
        var end = start + 256;

        if (_jitCache.TryGetValue(start, out var cached))
        {
            cached.Execute(this);
            return;
        }

        var rip = start;
        while (rip < end && _running)
        {
            _lastRip = rip;
            var len = ExecuteInstruction(rip);
            if (len == 0) break;
            rip += (uint)len;
        }
    }

    public unsafe ulong ReadGuestPointer(ulong address)
    {
        if (address + sizeof(ulong) > _memory.Capacity)
            throw new MemoryAccessViolation(address, sizeof(ulong));
        return *(ulong*)_memory.Ref(address);
    }

    public unsafe void WriteGuestPointer(ulong address, ulong value)
    {
        if (address + sizeof(ulong) > _memory.Capacity)
            throw new MemoryAccessViolation(address, sizeof(ulong));
        *(ulong*)_memory.Ref(address) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe byte ReadByte(ulong address)
    {
        if (address >= _memory.Capacity)
            throw new MemoryAccessViolation(address, 1);
        return _memory.Ref(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ushort ReadUInt16(ulong address)
    {
        if (address + 2 > _memory.Capacity)
            throw new MemoryAccessViolation(address, 2);
        return *(ushort*)_memory.Ref(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe uint ReadUInt32(ulong address)
    {
        if (address + 4 > _memory.Capacity)
            throw new MemoryAccessViolation(address, 4);
        return *(uint*)_memory.Ref(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ulong ReadUInt64(ulong address)
    {
        if (address + 8 > _memory.Capacity)
            throw new MemoryAccessViolation(address, 8);
        return *(ulong*)_memory.Ref(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void WriteByte(ulong address, byte value)
    {
        if (address >= _memory.Capacity)
            throw new MemoryAccessViolation(address, 1);
        _memory.Ref(address) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void WriteUInt16(ulong address, ushort value)
    {
        if (address + 2 > _memory.Capacity)
            throw new MemoryAccessViolation(address, 2);
        *(ushort*)_memory.Ref(address) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void WriteUInt32(ulong address, uint value)
    {
        if (address + 4 > _memory.Capacity)
            throw new MemoryAccessViolation(address, 4);
        *(uint*)_memory.Ref(address) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void WriteUInt64(ulong address, ulong value)
    {
        if (address + 8 > _memory.Capacity)
            throw new MemoryAccessViolation(address, 8);
        *(ulong*)_memory.Ref(address) = value;
    }

    public int ExecuteInstruction(ulong oldRip)
    {
        var rip = oldRip;
        var b0 = ReadByte(rip++);

        if (b0 == 0x0F)
        {
            var b1 = ReadByte(rip++);
            return b1 switch
            {
                0x01 => ExecuteGrp1(rip, b0, b1),
                0x10 => Execute0f10(rip, b0, b1),
                0x11 => Execute0f11(rip, b0, b1),
                0x28 => Execute0f28(rip, b0, b1),
                0x29 => Execute0f29(rip, b0, b1),
                0x6F => Execute0f6F(rip, b0, b1),
                0x7F => Execute0f7F(rip, b0, b1),
                0xAE => Execute0fAE(rip, b0, b1),
                0xB6 => Execute0fB6(rip, b0, b1),
                0xBE => Execute0fBE(rip, b0, b1),
                0xBF => Execute0fBF(rip, b0, b1),
                _ => DefaultUnsupported(rip, b0, b1)
            };
        }

        if (b0 == 0x66)
        {
            var b1 = ReadByte(rip++);
            return b1 switch
            {
                0x0F => Execute66_0f(rip, b0, b1),
                _ => DefaultUnsupported(rip, b0, b1)
            };
        }

        if (b0 == 0xF3)
        {
            var b1 = ReadByte(rip++);
            return b1 switch
            {
                0x0F => ExecF3_0f(rip, b0, b1),
                _ => DefaultUnsupported(rip, b0, b1)
            };
        }

        return b0 switch
        {
            0x00 => Execute000(rip, b0),
            0x01 => Execute001(rip, b0),
            0x03 => Execute003(rip, b0),
            0x0B => Execute00B(rip, b0),
            0x31 => Execute031(rip, b0),
            0x39 => Execute039(rip, b0),
            0x3B => Execute03B(rip, b0),
            0x50 or 0x51 or 0x52 or 0x53 or 0x54 or 0x55 or 0x56 or 0x57 => ExecutePushReg(rip, b0),
            0x58 or 0x59 or 0x5A or 0x5B or 0x5C or 0x5D or 0x5E or 0x5F => ExecutePopReg(rip, b0),
            0x68 => Execute068(rip, b0),
            0x6A => Execute06A(rip, b0),
            0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x76 or 0x77 or 0x78 or 0x79 or 0x7A or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F => ExecuteJcc(rip, b0),
            0x83 => Execute083(rip, b0),
            0x84 => Execute084(rip, b0),
            0x85 => Execute085(rip, b0),
            0x88 => Execute088(rip, b0),
            0x89 => Execute089(rip, b0),
            0x8B => Execute08B(rip, b0),
            0x8D => Execute08D(rip, b0),
            0x90 => 1,
            0x99 => { _state.Rdx = (_state.Rax >> 63) != 0 ? ulong.MaxValue : 0; return 1; },
            0xB0 or 0xB1 or 0xB2 or 0xB3 or 0xB4 or 0xB5 or 0xB6 or 0xB7 => ExecuteMovR8Imm(rip, b0),
            0xB8 or 0xB9 or 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF => ExecuteMovR64Imm(rip, b0),
            0xC3 => { _state.Rip = ReadGuestPointer(_state.Rsp); _state.Rsp += 8; return 0; },
            0xC7 => Execute0C7(rip, b0),
            0xC9 => { _state.Rsp = _state.Rbp; _state.Rbp = ReadGuestPointer(_state.Rsp); _state.Rsp += 8; return 1; },
            0xCC => { Log.Warn($"INT3 at 0x{oldRip:X}"); return 1; },
            0xE8 => ExecuteE8(rip, b0),
            0xE9 => ExecuteE9(rip, b0),
            0xEB => ExecuteEB(rip, b0),
            0xF3 => ExecuteInstruction(rip),
            0xFF => ExecuteFF(rip, b0),
            0x0F => DefaultUnsupported(rip, b0),
            0xCD => ExecuteCD(rip, b0),
            0x80 => Execute080(rip, b0),
            0x81 => Execute081(rip, b0),
            0x86 => Execute086(rip, b0),
            0x87 => Execute087(rip, b0),
            0xF6 => ExecuteF6(rip, b0),
            0xF7 => ExecuteF7(rip, b0),
            0xC6 => ExecuteC6(rip, b0),
            _ => DefaultUnsupported(rip, b0)
        };
    }

    private int DefaultUnsupported(ulong rip, byte b0, byte b1 = 0)
    {
        Log.Debug($"Unsupported opcode 0x{b0:X2} 0x{b1:X2} at 0x{rip:X}");
        return 1;
    }

    private int ExecutePushReg(ulong rip, byte b0)
    {
        var reg = b0 - 0x50;
        _state.Rsp -= 8;
        WriteGuestPointer(_state.Rsp, _state.GetGpr(reg));
        return 1;
    }

    private int ExecutePopReg(ulong rip, byte b0)
    {
        var reg = b0 - 0x58;
        _state.GetGpr(reg) = ReadGuestPointer(_state.Rsp);
        _state.Rsp += 8;
        return 1;
    }

    private int ExecuteMovR8Imm(ulong rip, byte b0)
    {
        var reg = b0 - 0xB0;
        var imm = ReadByte(rip);
        _state.GetGpr(reg) = imm;
        return 2;
    }

    private int ExecuteMovR64Imm(ulong rip, byte b0)
    {
        var reg = b0 - 0xB8;
        var imm = ReadUInt64(rip);
        _state.GetGpr(reg) = imm;
        return 9;
    }

    private int Execute068(ulong rip, byte b0)
    {
        var imm = ReadUInt32(rip);
        _state.Rsp -= 8;
        WriteGuestPointer(_state.Rsp, imm);
        return 5;
    }

    private int Execute06A(ulong rip, byte b0)
    {
        var imm = ReadByte(rip);
        _state.Rsp -= 8;
        WriteGuestPointer(_state.Rsp, (ulong)(sbyte)imm);
        return 2;
    }

    private int ExecuteE8(ulong rip, byte b0)
    {
        var disp = (int)ReadUInt32(rip);
        var next = rip + 4;
        _state.Rsp -= 8;
        WriteGuestPointer(_state.Rsp, next);
        _state.Rip = (ulong)((long)next + disp);
        return 0;
    }

    private int ExecuteE9(ulong rip, byte b0)
    {
        var disp = (int)ReadUInt32(rip);
        _state.Rip = (ulong)((long)rip + disp);
        return 0;
    }

    private int ExecuteEB(ulong rip, byte b0)
    {
        var disp = (sbyte)ReadByte(rip);
        _state.Rip = (ulong)((long)rip + disp);
        return 0;
    }

    private int ExecuteCD(ulong rip, byte b0)
    {
        var intNo = ReadByte(rip);
        Log.Warn($"INT 0x{intNo:X} at 0x{rip-1:X} not implemented");
        return 2;
    }

    private int ExecuteFF(ulong rip, byte b0)
    {
        var sub = ReadByte(rip++);
        var modrm = ReadByte(rip++);
        var (mode, _, rm) = DecodeModRm(modrm);

        switch (sub)
        {
            case 0x15: // CALL QWORD PTR [RIP+disp32]
                if (mode == 0 && rm == 5)
                {
                    var disp = ReadUInt32(rip);
                    var addr = rip + disp;
                    var target = ReadGuestPointer(addr);
                    _state.Rsp -= 8;
                    WriteGuestPointer(_state.Rsp, rip);
                    _state.Rip = target;
                    return 0;
                }
                break;
            case 0x25: // JMP QWORD PTR [RIP+disp32]
                if (mode == 0 && rm == 5)
                {
                    var disp = ReadUInt32(rip);
                    var addr = rip + disp;
                    _state.Rip = ReadGuestPointer(addr);
                    return 0;
                }
                break;
            case 0x35: // PUSH QWORD PTR [RIP+disp32]
                if (mode == 0 && rm == 5)
                {
                    var disp = ReadUInt32(rip);
                    var addr = rip + disp;
                    var val = ReadGuestPointer(addr);
                    _state.Rsp -= 8;
                    WriteGuestPointer(_state.Rsp, val);
                    return 6;
                }
                break;
        }

        return DefaultUnsupported(rip - 2, 0xFF, sub);
    }

    private int ExecuteJcc(ulong rip, byte b0)
    {
        var cond = (b0 >> 4) & 7;
        var take = cond switch
        {
            0 => (_state.Rflags & 0x40) != 0,
            1 => (_state.Rflags & 0x40) == 0,
            2 => (_state.Rflags & 0x80) != 0,
            3 => (_state.Rflags & 0x80) == 0,
            4 => (_state.Rflags & 0x40) != 0,
            5 => (_state.Rflags & 0x40) == 0,
            6 => (_state.Rflags & 0x80) != 0,
            7 => (_state.Rflags & 0x80) == 0,
            _ => false
        };

        var disp = (sbyte)ReadByte(rip);
        if (take)
        {
            _state.Rip = (ulong)((long)rip + disp);
            return 0;
        }
        return 2;
    }

    private int Execute000(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        var opSize = mode == 3 ? 64 : 64;

        switch (reg)
        {
            case 0: // ADD
                if (mode == 3)
                {
                    _state.GetGpr(rm) = Add64(_state.GetGpr(rm), _state.GetGpr(0));
                }
                return (int)(rip - (rip - rip));
            default:
                return DefaultUnsupported(rip - 1, b0);
        }
    }

    private int Execute001(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        switch (reg)
        {
            case 0: // ADD
                if (mode == 3)
                {
                    _state.GetGpr(rm) = Add64(_state.GetGpr(rm), _state.GetGpr(0));
                }
                return (int)(rip - (rip - rip));
            case 5: // SUB
                if (mode == 3)
                {
                    _state.GetGpr(rm) = Sub64(_state.GetGpr(rm), _state.GetGpr(0));
                }
                return (int)(rip - (rip - rip));
            case 7: // CMP
                if (mode == 3)
                {
                    var a = _state.GetGpr(rm);
                    var b = _state.GetGpr(0);
                    _state.Rflags = CompareFlags(a, b);
                }
                return (int)(rip - (rip - rip));
            default:
                return DefaultUnsupported(rip - 1, b0);
        }
    }

    private int Execute083(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        var imm = (ulong)(sbyte)ReadByte(rip);
        rip += 1;

        if (mode == 3)
        {
            var dest = _state.GetGpr(rm);
            switch (reg)
            {
                case 0: _state.GetGpr(rm) = Add64(dest, imm); break;
                case 1: _state.GetGpr(rm) = Or64(dest, imm); break;
                case 2: _state.GetGpr(rm) = Adc64(dest, imm); break;
                case 3: _state.GetGpr(rm) = Sbb64(dest, imm); break;
                case 4: _state.GetGpr(rm) = And64(dest, imm); break;
                case 5: _state.GetGpr(rm) = Sub64(dest, imm); break;
                case 6: _state.GetGpr(rm) = Xor64(dest, imm); break;
                case 7: CompareFlags(dest, imm); break;
            }
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute003(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            _state.GetGpr(reg) = ReadGuestPointer(_state.GetGpr(rm));
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute00B(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var a = _state.GetGpr(reg);
            var b = _state.GetGpr(rm);
            _state.Rflags = CompareFlags(a, b);
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute039(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var a = _state.GetGpr(reg);
            var b = _state.GetGpr(rm);
            _state.Rflags = CompareFlags(a, b);
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute03B(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var a = _state.GetGpr(reg);
            var b = _state.GetGpr(rm);
            _state.Rflags = CompareFlags(a, b);
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute031(ulong rip, byte b0)
    {
        _state.Rax = (ulong)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
        _state.Rdx = 0;
        return 1;
    }

    private int Execute084(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var a = _state.GetGpr(reg) & 0xFF;
            var b = _state.GetGpr(rm) & 0xFF;
            _state.Rflags = (a & b) == 0 ? 0x40 : 0;
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute085(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var a = _state.GetGpr(reg);
            var b = _state.GetGpr(rm);
            _state.Rflags = (a & b) == 0 ? 0x40 : 0;
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute088(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            _state.GetGpr(rm) = (_state.GetGpr(reg) & 0xFF);
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute089(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            _state.GetGpr(rm) = _state.GetGpr(reg);
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute08B(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            _state.GetGpr(reg) = _state.GetGpr(rm);
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute08D(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 1 || mode == 2)
        {
            _state.GetGpr(reg) = _state.GetGpr(rm); // Simplified
        }
        return (int)(rip - (rip - rip));
    }

    private int Execute0C7(ulong rip, byte b0)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var imm = ReadUInt32(rip);
            _state.GetGpr(rm) = reg == 0 ? imm : _state.GetGpr(rm);
            rip += 4;
        }
        return (int)(rip - (rip - rip));
    }

    private int ExecuteGrp1(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0f10(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        var (mode, _, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var addr = _state.GetGpr(rm);
            for (var i = 0; i < 2; i++)
                _state.Xmm[0 + i] = ReadUInt64(addr + (ulong)(i * 8));
            return (int)(rip - (rip - rip));
        }
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0f11(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        var (mode, _, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var addr = _state.GetGpr(rm);
            for (var i = 0; i < 2; i++)
                WriteUInt64(addr + (ulong)(i * 8), _state.Xmm[0 + i]);
            return (int)(rip - (rip - rip));
        }
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0f28(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        var (mode, _, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var addr = _state.GetGpr(rm);
            for (var i = 0; i < 2; i++)
                _state.Xmm[0 + i] = ReadUInt64(addr + (ulong)(i * 8));
            return (int)(rip - (rip - rip));
        }
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0f29(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        var (mode, _, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            var addr = _state.GetGpr(rm);
            for (var i = 0; i < 2; i++)
                WriteUInt64(addr + (ulong)(i * 8), _state.Xmm[0 + i]);
            return (int)(rip - (rip - rip));
        }
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0f6F(ulong rip, byte b0, byte b1) => Execute0f10(rip, b0, b1);
    private int Execute0f7F(ulong rip, byte b0, byte b1) => Execute0f11(rip, b0, b1);

    private int Execute0fAE(ulong rip, byte b0, byte b1)
    {
        var sub = ReadByte(rip++);
        if (sub == 0xF5) { _state.Rflags &= ~0x40; return 2; } // LFENCE simplified
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0fB6(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            _state.GetGpr(reg) = (byte)(_state.GetGpr(rm) & 0xFF);
            return (int)(rip - (rip - rip));
        }
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0fBE(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            _state.GetGpr(reg) = (ulong)(sbyte)(_state.GetGpr(rm) & 0xFF);
            return (int)(rip - (rip - rip));
        }
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute0fBF(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        var (mode, reg, rm) = DecodeModRm(modrm);
        if (mode == 3)
        {
            _state.GetGpr(reg) = (ulong)(short)(_state.GetGpr(rm) & 0xFFFF);
            return (int)(rip - (rip - rip));
        }
        return DefaultUnsupported(rip - 2, b0, b1);
    }

    private int Execute66_0f(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        return DefaultUnsupported(rip - 3, b0, b1);
    }

    private int ExecF3_0f(ulong rip, byte b0, byte b1)
    {
        var modrm = ReadByte(rip++);
        return DefaultUnsupported(rip - 3, b0, b1);
    }

    private (byte mode, byte reg, byte rm) DecodeModRm(byte modrm)
    {
        return ((modrm >> 6) & 3, (modrm >> 3) & 7, modrm & 7);
    }

    private ulong Add64(ulong a, ulong b)
    {
        var r = a + b;
        _state.Rflags = (r == 0 ? 0x40u : 0u) | ((r < a) ? 0x800u : 0u);
        return r;
    }

    private ulong Sub64(ulong a, ulong b)
    {
        var r = a - b;
        _state.Rflags = (r == 0 ? 0x40u : 0u) | ((a < b) ? 0x800u : 0u);
        return r;
    }

    private ulong Or64(ulong a, ulong b)
    {
        var r = a | b;
        _state.Rflags = r == 0 ? 0x40u : 0u;
        return r;
    }

    private ulong And64(ulong a, ulong b)
    {
        var r = a & b;
        _state.Rflags = r == 0 ? 0x40u : 0u;
        return r;
    }

    private ulong Xor64(ulong a, ulong b)
    {
        var r = a ^ b;
        _state.Rflags = r == 0 ? 0x40u : 0u;
        return r;
    }

    private ulong Adc64(ulong a, ulong b)
    {
        var carry = (_state.Rflags >> 1) & 1;
        var r = a + b + carry;
        _state.Rflags = (r == 0 ? 0x40u : 0u) | ((r < a) ? 0x800u : 0u);
        return r;
    }

    private ulong Sbb64(ulong a, ulong b)
    {
        var carry = (_state.Rflags >> 1) & 1;
        var r = a - b - carry;
        _state.Rflags = (r == 0 ? 0x40u : 0u) | ((a < b) ? 0x800u : 0u);
        return r;
    }

    private uint CompareFlags(ulong a, ulong b)
    {
        if (a == b) return 0x40;
        return (a < b) ? 0x81 : 0x0;
    }

    private void DumpState()
    {
        Log.Warn($"Last RIP: 0x{_lastRip:X}");
        Log.Warn($"RAX=0x{_state.Rax:X} RBX=0x{_state.Rbx:X} RCX=0x{_state.Rcx:X} RDX=0x{_state.Rdx:X}");
        Log.Warn($"RSI=0x{_state.Rsi:X} RDI=0x{_state.Rdi:X} RBP=0x{_state.Rbp:X} RSP=0x{_state.Rsp:X}");
        Log.Warn($"R8=0x{_state.R8:X} R9=0x{_state.R9:X} R10=0x{_state.R10:X} R11=0x{_state.R11:X}");
        Log.Warn($"R12=0x{_state.R12:X} R13=0x{_state.R13:X} R14=0x{_state.R14:X} R15=0x{_state.R15:X}");
        Log.Warn($"RIP=0x{_state.Rip:X} RFLAGS=0x{_state.Rflags:X}");
    }
}

public sealed class CompiledBlock
{
    private readonly Action<Interpreter> _executor;

    public CompiledBlock(Action<Interpreter> executor)
    {
        _executor = executor;
    }

    public void Execute(Interpreter interpreter) => _executor(interpreter);
}
