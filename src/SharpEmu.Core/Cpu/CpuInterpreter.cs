using System;
using System.Runtime.CompilerServices;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Cpu;

/// <summary>
/// A minimal but real x86-64 interpreter. The PS5 CPU is an AMD Zen 2 (x86-64),
/// so unlike older consoles there is no exotic ISA to decode. This interpreter
/// decodes and executes a practical subset of 64-bit user-mode instructions
/// (MOV, arithmetic, shifts, comparisons, branches, calls/returns, stack ops,
/// and a few SSE data moves). It is not a full CPU -- it is enough to run the
/// early CRT / HLE bootstrap paths and to demonstrate native execution.
///
/// Unimplemented opcodes raise CpuUndefinedInstruction so the host can decide
/// whether to fall back to a native JIT backend or stop.
/// </summary>
public sealed class CpuInterpreter
{
    private readonly GuestMemory _mem;
    private readonly CpuContext _ctx;
    public Action<ulong> OnSyscall;
    public Func<ulong, bool> OnCall; // return true if handled by HLE and should not actually branch

    public ulong InstructionsExecuted;
    private readonly Logger _log;

    // REX prefix bits
    private const byte REX_W = 0x08;
    private const byte REX_R = 0x04;
    private const byte REX_X = 0x02;
    private const byte REX_B = 0x01;

    public CpuInterpreter(GuestMemory mem, CpuContext ctx, Logger log)
    {
        _mem = mem;
        _ctx = ctx;
        _log = log;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadU8(ref ulong ip)
    {
        var b = _mem.ReadByte(ip);
        ip++;
        return b;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadU16(ref ulong ip)
    {
        ushort v = _mem.ReadUInt16(ip);
        ip += 2;
        return v;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadU32(ref ulong ip)
    {
        uint v = _mem.ReadUInt32(ip);
        ip += 4;
        return v;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadU64(ref ulong ip)
    {
        ulong v = _mem.ReadUInt64(ip);
        ip += 8;
        return v;
    }

    public void Run(ulong entry, int maxInstructions = 40_000_000)
    {
        _ctx.Rip = entry;
        try
        {
            while (InstructionsExecuted < (ulong)maxInstructions)
            {
                Step();
                if (_ctx.Rip == 0)
                    throw new CpuHalt("RIP reached 0 (likely a return from the top entry)");
            }
            if (InstructionsExecuted >= (ulong)maxInstructions)
                _log.Warn("cpu", $"instruction budget ({maxInstructions}) reached without halt");
        }
        catch (CpuHalt h)
        {
            _log.Info("cpu", $"halt: {h.Message} (executed {InstructionsExecuted} instructions)");
        }
        catch (CpuUndefinedInstruction u)
        {
            _log.Error("cpu", $"undefined instruction at RIP=0x{_ctx.Rip:X}: {u.Message}");
            throw;
        }
    }

    public void Step()
    {
        ulong ip = _ctx.Rip;
        DecodeAndExecute(ref ip);
        _ctx.Rip = ip;
        InstructionsExecuted++;
    }

    private void DecodeModRm(ref ulong ip, byte rexR, byte rexX, byte rexB, out int reg, out ulong rmAddr, out int rmReg)
    {
        byte modrm = ReadU8(ref ip);
        int mod = modrm >> 6;
        int rm = modrm & 0x7;
        reg = ((int)rexR << 3) | (modrm >> 3 & 0x7);
        rmReg = -1;
        rmAddr = 0;

        int rmIdx = rm | ((int)rexB << 3);

        if (mod == 3)
        {
            rmReg = rmIdx;
            return;
        }

        ulong baseVal = 0;
        if (rm == 4) // SIB
        {
            byte sib = ReadU8(ref ip);
            int scale = 1 << (sib >> 6);
            int index = (sib >> 3) & 0x7;
            int bas = sib & 0x7;
            ulong baseV = (bas == 5 && mod == 0) ? 0 : _ctx.Gpr[bas | ((int)rexB << 3)];
            ulong indexV = (index == 4) ? 0 : (_ctx.Gpr[index | ((int)rexX << 3)] * (ulong)scale);
            baseVal = baseV + indexV;
            if (bas == 5 && mod == 0)
                baseVal += (ulong)(int)ReadU32(ref ip);
        }
        else
        {
            baseVal = _ctx.Gpr[rmIdx];
        }

        switch (mod)
        {
            case 0:
                if (rm == 5) baseVal = ip + (ulong)(int)ReadU32(ref ip); // RIP-relative
                break;
            case 1: baseVal += (ulong)(sbyte)ReadU8(ref ip); break;
            case 2: baseVal += (ulong)(int)ReadU32(ref ip); break;
        }
        rmAddr = baseVal;
    }

    private ulong GetReg(int idx, bool wide) => wide ? _ctx.Gpr[idx] : _ctx.Gpr[idx] & 0xFFFFFFFF;
    private void SetReg(int idx, ulong val, bool wide)
    {
        if (wide) _ctx.Gpr[idx] = val;
        else _ctx.Gpr[idx] = (_ctx.Gpr[idx] & 0xFFFFFFFF00000000UL) | (val & 0xFFFFFFFF);
    }

    private void DecodeAndExecute(ref ulong ip)
    {
        byte rex = 0;
        bool rexW = false, rexR = false, rexX = false, rexB = false;
        byte op = ReadU8(ref ip);
        while (op >= 0x40 && op <= 0x4F)
        {
            rex = op;
            rexW = (op & REX_W) != 0;
            rexR = (op & REX_R) != 0;
            rexX = (op & REX_X) != 0;
            rexB = (op & REX_B) != 0;
            op = ReadU8(ref ip);
        }
        bool w = rexW;

        switch (op)
        {
            case 0x50: case 0x51: case 0x52: case 0x53:
            case 0x54: case 0x55: case 0x56: case 0x57:
                { int r = (op - 0x50) | (rexB ? 8 : 0); _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, _ctx.Gpr[r]); return; }
            case 0x58: case 0x59: case 0x5A: case 0x5B:
            case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                { int r = (op - 0x58) | (rexB ? 8 : 0); _ctx.Gpr[r] = _mem.ReadUInt64(_ctx.Rsp); _ctx.Rsp += 8; return; }
            case 0x68:
                { uint imm = ReadU32(ref ip); _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, imm); return; }
            case 0x6A:
                { byte imm = ReadU8(ref ip); _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, (ulong)(sbyte)imm); return; }
            case 0x04: case 0x0C: case 0x14: case 0x1C:
            case 0x24: case 0x2C: case 0x34: case 0x3C:
            case 0x05: case 0x0D: case 0x15: case 0x1D:
            case 0x25: case 0x2D: case 0x35: case 0x3D:
                {
                    int alu = (op >> 3) & 7;
                    bool imm8 = (op & 0x01) == 0;
                    ulong imm = imm8 ? ReadU8(ref ip) : ReadU32(ref ip);
                    if (imm8)
                    {
                        uint a = (uint)(_ctx.Rax & 0xFF);
                        uint r = Alu32(alu, a, (uint)(imm & 0xFF), out bool cf8);
                        _ctx.Rax = (_ctx.Rax & 0xFFFFFFFFFFFFFF00UL) | (r & 0xFF);
                        _ctx.SetFlag(CF, cf8); UpdateZS(r, false);
                    }
                    else
                    {
                        uint a = (uint)(_ctx.Rax & 0xFFFFFFFF);
                        uint r = Alu32(alu, a, (uint)(imm & 0xFFFFFFFF), out bool cf32);
                        _ctx.Rax = (_ctx.Rax & 0xFFFFFFFF00000000UL) | r;
                        _ctx.SetFlag(CF, cf32); UpdateZS(r, false);
                    }
                    return;
                }
            case 0x70: case 0x71: case 0x72: case 0x73:
            case 0x74: case 0x75: case 0x76: case 0x77:
            case 0x78: case 0x79: case 0x7A: case 0x7B:
            case 0x7C: case 0x7D: case 0x7E: case 0x7F:
                { sbyte rel = (sbyte)ReadU8(ref ip); if (Condition(op - 0x70)) ip = (ulong)((long)ip + rel); return; }
            case 0x80: case 0x81: case 0x83:
                {
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    ulong imm = op == 0x81 ? ReadU32(ref ip) : (ulong)(sbyte)ReadU8(ref ip);
                    Grp1(reg, addr, rm, w, imm);
                    return;
                }
            case 0x88: case 0x89:
                {
                    bool b = op == 0x88;
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    ulong val = b ? (_ctx.Gpr[reg] & 0xFF) : _ctx.Gpr[reg];
                    if (rm >= 0) { if (b) WriteRegByte(rm, (byte)val); else SetReg(rm, val, w); }
                    else { if (b) _mem.WriteByte(addr, (byte)val); else if (w) _mem.WriteUInt64(addr, val); else _mem.WriteUInt32(addr, (uint)val); }
                    return;
                }
            case 0x8A: case 0x8B:
                {
                    bool b = op == 0x8A;
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    ulong val;
                    if (rm >= 0) val = b ? ReadRegByte(rm) : GetReg(rm, w);
                    else val = b ? _mem.ReadByte(addr) : (w ? _mem.ReadUInt64(addr) : _mem.ReadUInt32(addr));
                    if (b) WriteRegByte(reg, (byte)val); else SetReg(reg, val, w);
                    return;
                }
            case 0x8D:
                {
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    SetReg(reg, addr, w);
                    return;
                }
            case 0x90: return;
            case 0x99:
                {
                    if (w) _ctx.Rdx = (_ctx.Rax & 0x8000000000000000UL) != 0 ? 0xFFFFFFFFFFFFFFFFUL : 0;
                    else _ctx.Rdx = (_ctx.Rax & 0x80000000U) != 0 ? 0xFFFFFFFFU : 0;
                    return;
                }
            case 0xA8: { byte imm = ReadU8(ref ip); Test8((byte)_ctx.Rax, imm); return; }
            case 0xA9:
                {
                    ulong imm = w ? ReadU64(ref ip) : ReadU32(ref ip);
                    if (w) Test64(_ctx.Rax, imm); else Test32((uint)_ctx.Rax, (uint)imm);
                    return;
                }
            case 0xB0: case 0xB1: case 0xB2: case 0xB3:
            case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                { int r = (op - 0xB0) | (rexB ? 8 : 0); WriteRegByte(r, ReadU8(ref ip)); return; }
            case 0xB8: case 0xB9: case 0xBA: case 0xBB:
            case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                { int r = (op - 0xB8) | (rexB ? 8 : 0); if (w) SetReg(r, ReadU64(ref ip), true); else SetReg(r, ReadU32(ref ip), false); return; }
            case 0xC3:
                { ulong ret = _mem.ReadUInt64(_ctx.Rsp); _ctx.Rsp += 8; ip = ret; return; }
            case 0xC6: case 0xC7:
                {
                    bool b = op == 0xC6;
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    if (b) { byte v = ReadU8(ref ip); if (rm >= 0) WriteRegByte(rm, v); else _mem.WriteByte(addr, v); }
                    else { ulong v = w ? ReadU64(ref ip) : ReadU32(ref ip); if (rm >= 0) SetReg(rm, v, w); else if (w) _mem.WriteUInt64(addr, v); else _mem.WriteUInt32(addr, (uint)v); }
                    return;
                }
            case 0xC9:
                { _ctx.Rsp = _ctx.Rbp; _ctx.Rbp = _mem.ReadUInt64(_ctx.Rsp); _ctx.Rsp += 8; return; }
            case 0xCC: throw new CpuHalt("INT3 breakpoint encountered");
            case 0xD1: case 0xD3:
                {
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    int cnt = op == 0xD1 ? 1 : (int)(_ctx.Rcx & 0x3F);
                    Grp2(reg, addr, rm, w, cnt);
                    return;
                }
            case 0xE8:
                {
                    long rel = (long)(int)ReadU32(ref ip);
                    ulong target = (ulong)((long)ip + rel);
                    _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, ip);
                    if (OnCall != null && OnCall(target)) { } else ip = target;
                    return;
                }
            case 0xE9: { long rel = (long)(int)ReadU32(ref ip); ip = (ulong)((long)ip + rel); return; }
            case 0xEB: { sbyte rel = (sbyte)ReadU8(ref ip); ip = (ulong)((long)ip + rel); return; }
            case 0xF6: case 0xF7:
                {
                    bool b = op == 0xF6;
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    Grp3(reg, addr, rm, w, b, ref ip);
                    return;
                }
            case 0xFF:
                {
                    DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                    Grp5(reg, addr, rm);
                    return;
                }
            default: break;
        }

        if (op == 0x0F)
        {
            byte op2 = ReadU8(ref ip);
            switch (op2)
            {
                case 0x10: case 0x11: case 0x28: case 0x29:
                case 0x6E: case 0x7E: case 0x6F: case 0x7F:
                    {
                        DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                        if ((op2 & 1) == 0)
                        {
                            _ctx.Xmm[reg][0] = _mem.ReadUInt64(addr);
                            if (op2 == 0x6F || op2 == 0x7F) _ctx.Xmm[reg][1] = _mem.ReadUInt64(addr + 8);
                        }
                        else
                        {
                            _mem.WriteUInt64(addr, _ctx.Xmm[rm][0]);
                            if (op2 == 0x6F || op2 == 0x7F) _mem.WriteUInt64(addr + 8, _ctx.Xmm[rm][1]);
                        }
                        return;
                    }
                case 0x1F: return;
                case 0x80: case 0x81: case 0x82: case 0x83:
                case 0x84: case 0x85: case 0x86: case 0x87:
                case 0x88: case 0x89: case 0x8A: case 0x8B:
                case 0x8C: case 0x8D: case 0x8E: case 0x8F:
                    { int cond = op2 - 0x80; long rel = (long)(int)ReadU32(ref ip); if (Condition(cond)) ip = (ulong)((long)ip + rel); return; }
                case 0x90: case 0x91: case 0x92: case 0x93:
                case 0x95: case 0x96: case 0x97:
                    {
                        DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                        byte v = Condition(op2 - 0x90) ? (byte)1 : (byte)0;
                        if (rm >= 0) WriteRegByte(rm, v); else _mem.WriteByte(addr, v);
                        return;
                    }
                case 0xAF:
                    {
                        DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                        ulong src = rm >= 0 ? GetReg(rm, w) : (w ? _mem.ReadUInt64(addr) : _mem.ReadUInt32(addr));
                        ulong res = w ? _ctx.Gpr[reg] * src : (_ctx.Gpr[reg] & 0xFFFFFFFF) * (src & 0xFFFFFFFF);
                        SetReg(reg, res, w);
                        return;
                    }
                case 0xB6: case 0xB7:
                    {
                        bool word = op2 == 0xB7;
                        DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                        ulong val = rm >= 0 ? ReadRegByte(rm) : _mem.ReadByte(addr);
                        if (word) val = rm >= 0 ? (_ctx.Gpr[rm] & 0xFFFF) : _mem.ReadUInt16(addr);
                        SetReg(reg, val, w);
                        return;
                    }
                case 0xBE: case 0xBF:
                    {
                        bool word = op2 == 0xBF;
                        DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                        ulong val = rm >= 0 ? ReadRegByte(rm) : (ulong)(sbyte)_mem.ReadByte(addr);
                        if (word) val = rm >= 0 ? (ulong)(short)(_ctx.Gpr[rm] & 0xFFFF) : (ulong)(short)_mem.ReadUInt16(addr);
                        SetReg(reg, val, w);
                        return;
                    }
                case 0x40: case 0x41: case 0x42: case 0x43:
                case 0x44: case 0x45: case 0x46: case 0x47:
                    {
                        int cond = op2 - 0x40;
                        DecodeModRm(ref ip, (byte)(rexR ? 1 : 0), (byte)(rexX ? 1 : 0), (byte)(rexB ? 1 : 0), out int reg, out ulong addr, out int rm);
                        if (Condition(cond))
                        {
                            ulong v = rm >= 0 ? GetReg(rm, w) : (w ? _mem.ReadUInt64(addr) : _mem.ReadUInt32(addr));
                            SetReg(reg, v, w);
                        }
                        return;
                    }
                case 0x05: { OnSyscall?.Invoke(_ctx.Rip); return; }
                default: throw new CpuUndefinedInstruction($"0F {op2:X2}");
            }
        }

        throw new CpuUndefinedInstruction($"op 0x{op:X2} (REX={(rex != 0 ? rex.ToString("X2") : "-")})");
    }

    private void Grp1(int reg, ulong addr, int rm, bool w, ulong imm)
    {
        ulong Get() => rm >= 0 ? GetReg(rm, w) : (w ? _mem.ReadUInt64(addr) : _mem.ReadUInt32(addr));
        void Set(ulong v) { if (rm >= 0) SetReg(rm, v, w); else if (w) _mem.WriteUInt64(addr, v); else _mem.WriteUInt32(addr, (uint)v); }

        switch (reg)
        {
            case 0: { ulong a = Get(); ulong r = a + imm; SetFlag(CF, r < a); Set(r, w); break; }
            case 1: { ulong r = Get() | imm; Set(r, w); ClearArith(); break; }
            case 2: { ulong a = Get(); ulong r = a + imm; Set(r, w); break; }
            case 3: { ulong a = Get(); ulong r = a - imm; Set(r, w); break; }
            case 4: { ulong r = Get() & imm; Set(r, w); ClearArith(); break; }
            case 5: { ulong a = Get(); ulong r = a - imm; SetFlag(CF, a < imm); Set(r, w); UpdateZS(r, w); break; }
            case 6: { ulong r = Get() ^ imm; Set(r, w); ClearArith(); break; }
            case 7: { ulong a = Get(); SetFlag(CF, a < imm); UpdateZS(a - imm, w); break; }
        }
    }

    private uint Alu32(int alu, uint a, uint b, out bool carry)
    {
        carry = false;
        switch (alu)
        {
            case 0: { ulong r = (ulong)a + b; carry = r > 0xFFFFFFFF; return (uint)r; }
            case 1: return a | b;
            case 2: { ulong r = (ulong)a + b; carry = r > 0xFFFFFFFF; return (uint)r; }
            case 3: { ulong r = (ulong)a - b; carry = a < b; return (uint)r; }
            case 4: return a & b;
            case 5: { ulong r = (ulong)a - b; carry = a < b; return (uint)r; }
            case 6: return a ^ b;
            case 7: { ulong r = (ulong)a - b; carry = a < b; return (uint)r; }
            default: return 0;
        }
    }

    private void Grp2(int reg, ulong addr, int rm, bool w, int cnt)
    {
        ulong Get() => rm >= 0 ? GetReg(rm, w) : (w ? _mem.ReadUInt64(addr) : _mem.ReadUInt32(addr));
        void Set(ulong v) { if (rm >= 0) SetReg(rm, v, w); else if (w) _mem.WriteUInt64(addr, v); else _mem.WriteUInt32(addr, (uint)v); }

        ulong v = Get();
        if (reg == 4 || reg == 5) v = v << cnt;
        else if (reg == 7) v = (ulong)((long)v >> cnt);
        else if (reg == 1) v = v >> cnt;
        Set(v);
        UpdateZS(v, w);
    }

    private void Grp3(int reg, ulong addr, int rm, bool w, bool b, ref ulong ip)
    {
        ulong Get() => rm >= 0 ? (b ? ReadRegByte(rm) : GetReg(rm, w)) : (b ? _mem.ReadByte(addr) : (w ? _mem.ReadUInt64(addr) : _mem.ReadUInt32(addr)));
        void Set(ulong v) { if (rm >= 0) { if (b) WriteRegByte(rm, (byte)v); else SetReg(rm, v, w); } else { if (b) _mem.WriteByte(addr, (byte)v); else if (w) _mem.WriteUInt64(addr, v); else _mem.WriteUInt32(addr, (uint)v); } }

        switch (reg)
        {
            case 0: case 1:
                {
                    ulong imm = b ? ReadU8(ref ip) : (w ? ReadU64(ref ip) : ReadU32(ref ip));
                    if (b) Test8((byte)Get(), (byte)imm); else if (w) Test64(Get(), imm); else Test32((uint)Get(), (uint)imm);
                    break;
                }
            case 2: Set(~Get()); break;
            case 3: { ulong a = Get(); Set(0 - a); break; }
            case 4: case 5:
                {
                    if (b) { ushort r = (ushort)((_ctx.Rax & 0xFF) * (Get() & 0xFF)); _ctx.Rax = (_ctx.Rax & 0xFFFFFFFFFFFFFF00UL) | (r & 0xFF); _ctx.Rdx = (_ctx.Rdx & 0xFFFFFFFFFFFFFF00UL) | ((r >> 8) & 0xFF); }
                    else if (w) { ulong r = _ctx.Rax * Get(); _ctx.Rax = r; _ctx.Rdx = 0; }
                    else { uint r = (uint)(_ctx.Rax & 0xFFFFFFFF) * (uint)(Get() & 0xFFFFFFFF); _ctx.Rax = (_ctx.Rax & 0xFFFFFFFF00000000UL) | r; }
                    break;
                }
            case 6: case 7:
                {
                    ulong a = Get();
                    if (a == 0) throw new CpuHalt("DIV by zero");
                    _ctx.Rax = _ctx.Rax / a;
                    break;
                }
        }
    }

    private void Grp5(int reg, ulong addr, int rm)
    {
        switch (reg)
        {
            case 0:
                {
                    ulong v = rm >= 0 ? GetReg(rm, true) : _mem.ReadUInt64(addr);
                    v++; if (rm >= 0) SetReg(rm, v, true); else _mem.WriteUInt64(addr, v);
                    UpdateZS(v, true);
                    break;
                }
            case 1:
                {
                    ulong v = rm >= 0 ? GetReg(rm, true) : _mem.ReadUInt64(addr);
                    v--; if (rm >= 0) SetReg(rm, v, true); else _mem.WriteUInt64(addr, v);
                    UpdateZS(v, true);
                    break;
                }
            case 2:
                {
                    ulong target = rm >= 0 ? GetReg(rm, true) : _mem.ReadUInt64(addr);
                    _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, _ctx.Rip);
                    if (OnCall != null && OnCall(target)) { } else _ctx.Rip = target;
                    break;
                }
            case 4:
                {
                    ulong target = rm >= 0 ? GetReg(rm, true) : _mem.ReadUInt64(addr);
                    _ctx.Rip = target;
                    break;
                }
            case 6:
                {
                    ulong v = rm >= 0 ? GetReg(rm, true) : _mem.ReadUInt64(addr);
                    _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, v);
                    break;
                }
            default: throw new CpuUndefinedInstruction($"GRP5 reg {reg}");
        }
    }

    private byte ReadRegByte(int r) => (byte)_ctx.Gpr[r];
    private void WriteRegByte(int r, byte v) => _ctx.Gpr[r] = (_ctx.Gpr[r] & 0xFFFFFFFFFFFFFF00UL) | v;

    private void ClearArith()
    {
        _ctx.SetFlag(CF, false);
        _ctx.SetFlag(OF, false);
    }

    private void UpdateZS(ulong v, bool w)
    {
        ulong mask = w ? 0xFFFFFFFFFFFFFFFFUL : 0xFFFFFFFFUL;
        v &= mask;
        _ctx.SetFlag(ZF, v == 0);
        ulong sign = w ? 0x8000000000000000UL : 0x80000000UL;
        _ctx.SetFlag(SF, (v & sign) != 0);
    }
    private void UpdateZS(long v, bool w) => UpdateZS((ulong)v, w);

    private void Test8(byte a, byte b)
    {
        byte r = (byte)(a & b);
        _ctx.SetFlag(ZF, r == 0);
        _ctx.SetFlag(SF, (r & 0x80) != 0);
        _ctx.SetFlag(CF, false); _ctx.SetFlag(OF, false);
    }
    private void Test32(uint a, uint b)
    {
        uint r = a & b;
        _ctx.SetFlag(ZF, r == 0);
        _ctx.SetFlag(SF, (r & 0x80000000U) != 0);
        _ctx.SetFlag(CF, false); _ctx.SetFlag(OF, false);
    }
    private void Test64(ulong a, ulong b)
    {
        ulong r = a & b;
        _ctx.SetFlag(ZF, r == 0);
        _ctx.SetFlag(SF, (r & 0x8000000000000000UL) != 0);
        _ctx.SetFlag(CF, false); _ctx.SetFlag(OF, false);
    }

    private bool Condition(int cond)
    {
        bool cf = _ctx.GetFlag(CF), zf = _ctx.GetFlag(ZF), sf = _ctx.GetFlag(SF), of = _ctx.GetFlag(OF);
        bool pf = _ctx.GetFlag(PF);
        switch (cond)
        {
            case 0x0: return of;
            case 0x1: return !of;
            case 0x2: return cf;
            case 0x3: return !cf;
            case 0x4: return zf;
            case 0x5: return !zf;
            case 0x6: return cf || zf;
            case 0x7: return !cf && !zf;
            case 0x8: return sf;
            case 0x9: return !sf;
            case 0xA: return pf;
            case 0xB: return !pf;
            case 0xC: return sf != of;
            case 0xD: return sf == of;
            case 0xE: return sf != of && !zf;
            case 0xF: return sf == of || zf;
            default: return false;
        }
    }
}

public sealed class CpuHalt : Exception
{
    public CpuHalt(string m) : base(m) { }
}
public sealed class CpuUndefinedInstruction : Exception
{
    public CpuUndefinedInstruction(string m) : base(m) { }
}
