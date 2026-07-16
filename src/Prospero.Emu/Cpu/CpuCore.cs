using System;
using Prospero.Emu.Core;

namespace Prospero.Emu.Cpu
{
    /// <summary>
    /// A research-grade x86-64 interpreter for the PS5 (Zen 2) userland.
    ///
    /// This is NOT a complete CPU. It implements a practical subset of the
    /// integer instruction set so that early game/program code (CRT init,
    /// libkernel stubs, simple loops, syscall dispatch) can execute far enough
    /// to reach file loading, resource setup, AGC init, or first frames for a
    /// few known programs. Unknown instructions raise CpuFault and are logged.
    ///
    /// Syscalls (0x0F 0x05) are delegated to the kernel abstraction.
    /// </summary>
    public sealed class CpuCore
    {
        private readonly Logger _log;
        private readonly GuestMemory _mem;
        private readonly IKernel _kernel;
        private readonly Decoder _dec = new();

        public Registers Regs { get; } = new();

        public ulong InstructionsExecuted;
        public ulong SyscallCount;

        // Limits to avoid runaway execution during research runs.
        public ulong MaxInstructions = 60_000_000;
        public bool TraceInstructions;

        public int ExitCode = 0;
        public bool Finished;

        public CpuCore(Logger log, GuestMemory mem, IKernel kernel)
        {
            _log = log;
            _mem = mem;
            _kernel = kernel;
        }

        public void Reset(ulong entry, ulong stackTop)
        {
            Regs.Gp.AsSpan().Clear();
            Regs.Rip = entry;
            Regs.Rflags = 0x2; // bit 1 (reserved) always 1
            Regs.Gp[4] = stackTop; // RSP
            InstructionsExecuted = 0;
            SyscallCount = 0;
            Finished = false;
        }

        // ---- memory bridging ----
        private ulong ReadGuest(ulong addr, int bytes)
        {
            return bytes switch
            {
                1 => _mem.ReadU8(addr),
                2 => _mem.ReadU16(addr),
                4 => _mem.ReadU32(addr),
                8 => _mem.ReadU64(addr),
                _ => throw new CpuFault($"bad read size {bytes}")
            };
        }
        private void WriteGuest(ulong addr, ulong val, int bytes)
        {
            switch (bytes)
            {
                case 1: _mem.WriteU8(addr, (byte)val); break;
                case 2: _mem.WriteU16(addr, (ushort)val); break;
                case 4: _mem.WriteU32(addr, (uint)val); break;
                case 8: _mem.WriteU64(addr, val); break;
                default: throw new CpuFault($"bad write size {bytes}");
            }
        }

        private ulong Reg(int i) => Regs.Gp[i];
        private void SetReg(int i, ulong v) => Regs.Gp[i] = v;

        /// <summary>Run until a limit, a HALT/fault, or the program finishes.</summary>
        public void Run()
        {
            while (!Finished && InstructionsExecuted < MaxInstructions)
            {
                try
                {
                    Step();
                }
                catch (CpuFault f)
                {
                    _log.Error("cpu", $"Fault at rip=0x{Regs.Rip:X}: {f.Message}");
                    Finished = true;
                    ExitCode = -1;
                    return;
                }
                catch (SyscallExit e)
                {
                    _log.Info("cpu", $"Program requested exit (code {e.Code})");
                    Finished = true;
                    ExitCode = e.Code;
                    return;
                }
            }
            if (!Finished)
            {
                _log.Warn("cpu", $"Reached instruction limit ({MaxInstructions}); halting research run.");
                Finished = true;
            }
        }

        public void Step()
        {
            // Read up to 15 bytes of instruction into the decoder buffer.
            const int maxLen = 15;
            var buf = new byte[maxLen];
            for (int i = 0; i < maxLen; i++)
                buf[i] = _mem.ReadU8(Regs.Rip + (ulong)i);
            _dec.Code = buf;
            _dec.Pc = 0;
            _dec.DecodePrefixes();

            ulong startRip = Regs.Rip;
            int opSize = _dec.RexW ? 8 : 4; // default operand size (we treat 32/64; 16 rare in PS5 code)

            byte op = _dec.ReadByte();
            Dispatch(op, opSize, startRip);

            InstructionsExecuted++;
        }

        private void Dispatch(byte op, int opSize, ulong startRip)
        {
            switch (op)
            {
                case 0x50: case 0x51: case 0x52: case 0x53:
                case 0x54: case 0x55: case 0x56: case 0x57: // PUSH rN (+ REX.B => r8..r15)
                    Push(Reg((op - 0x50) | (_dec.RexBase != 0 ? 8 : 0)));
                    Regs.Rip = startRip + (ulong)_dec.Pc;
                    break;
                case 0x58: case 0x59: case 0x5A: case 0x5B:
                case 0x5C: case 0x5D: case 0x5E: case 0x5F: // POP rN
                    SetReg((op - 0x58) | (_dec.RexBase != 0 ? 8 : 0), Pop());
                    Regs.Rip = startRip + (ulong)_dec.Pc;
                    break;
                case 0x68: // PUSH imm32
                    Push(_dec.ReadU32());
                    Regs.Rip = startRip + (ulong)_dec.Pc;
                    break;
                case 0x6A: // PUSH imm8
                    Push((ulong)(sbyte)_dec.ReadByte());
                    Regs.Rip = startRip + (ulong)_dec.Pc;
                    break;

                case 0x70: case 0x71: case 0x72: case 0x73:
                case 0x74: case 0x75: case 0x76: case 0x77:
                case 0x78: case 0x79: case 0x7A: case 0x7B:
                case 0x7C: case 0x7D: case 0x7E: case 0x7F: // Jcc rel8
                    {
                        sbyte rel = (sbyte)_dec.ReadByte();
                        if (CondJump(op - 0x70))
                            Regs.Rip = startRip + (ulong)rel;
                        else
                            Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;

                case 0x83: // group: ADD/OR/ADC/SBB/AND/SUB/XOR/CMP r/m, imm8
                    AluGroup(0x83, opSize, startRip);
                    break;
                case 0x81: // same group with imm32
                    AluGroup(0x81, opSize, startRip);
                    break;
                case 0x01: // ADD r/m, r
                case 0x03: // ADD r, r/m
                case 0x09: // OR  r/m, r
                case 0x0B: // OR  r, r/m
                case 0x11: // ADC r/m, r
                case 0x13: // ADC r, r/m
                case 0x19: // SBB r/m, r
                case 0x1B: // SBB r, r/m
                case 0x21: // AND r/m, r
                case 0x23: // AND r, r/m
                case 0x29: // SUB r/m, r
                case 0x2B: // SUB r, r/m
                case 0x31: // XOR r/m, r
                case 0x33: // XOR r, r/m
                case 0x39: // CMP r/m, r
                case 0x3B: // CMP r, r/m
                    AluRegMem(op, opSize, startRip);
                    break;

                case 0x85: // TEST r/m, r
                case 0x87: // XCHG r/m, r
                    AluRegMem(op, opSize, startRip);
                    break;
                case 0xA8: // TEST al, imm8
                    {
                        byte imm = _dec.ReadByte();
                        Test8(Regs.Gp[0], imm);
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;
                case 0xA9: // TEST eax/rax, imm
                    {
                        ulong imm = opSize == 8 ? _dec.ReadU64() : _dec.ReadU32();
                        Test(Reg(0), imm, opSize);
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;

                case 0x88: case 0x89: // MOV r/m, r  (8/16/32/64)
                case 0x8A: case 0x8B: // MOV r, r/m
                    MovRegMem(op, opSize, startRip);
                    break;
                case 0xC6: // MOV r/m8, imm8
                case 0xC7: // MOV r/m, imm
                    MovImm(op, opSize, startRip);
                    break;
                case 0xB0: case 0xB1: case 0xB2: case 0xB3:
                case 0xB4: case 0xB5: case 0xB6: case 0xB7: // MOV r8, imm8
                    {
                        int r = op - 0xB0;
                        Regs.Gp[r] = _dec.ReadByte();
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;
                case 0xB8: case 0xB9: case 0xBA: case 0xBB:
                case 0xBC: case 0xBD: case 0xBE: case 0xBF: // MOV rN, imm
                    {
                        int r = op - 0xB8;
                        if (_dec.RexW) Regs.Gp[r] = _dec.ReadU64();
                        else if (opSize == 4) Regs.Gp[r] = _dec.ReadU32();
                        else Regs.Gp[r] = _dec.ReadU16();
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;

                case 0x8D: // LEA r, m
                    {
                        var mr = _dec.ReadModRm();
                        ulong ea = _dec.EffectiveAddress(mr, Reg, startRip);
                        SetReg(_dec.RegIndex(mr), ea);
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;

                case 0xE8: // CALL rel32
                    {
                        int rel = (int)_dec.ReadU32();
                        ulong ret = startRip + (ulong)_dec.Pc;
                        Push(ret);
                        Regs.Rip = startRip + (ulong)rel;
                    }
                    break;
                case 0xE9: // JMP rel32
                    {
                        int rel = (int)_dec.ReadU32();
                        Regs.Rip = startRip + (ulong)rel;
                    }
                    break;
                case 0xEB: // JMP rel8
                    {
                        sbyte rel = (sbyte)_dec.ReadByte();
                        Regs.Rip = startRip + (ulong)rel;
                    }
                    break;
                case 0xC3: // RET
                    {
                        ulong ret = Pop();
                        Regs.Rip = ret;
                    }
                    break;
                case 0xC2: // RET imm16 (pop bytes)
                    {
                        ushort add = _dec.ReadU16();
                        ulong ret = Pop();
                        Regs.Gp[4] += add;
                        Regs.Rip = ret;
                    }
                    break;

                case 0xFF: // group 5: INC/DEC/CALL/JMP/PUSH r/m
                    Group5(opSize, startRip);
                    break;

                case 0x90: // NOP / XCHG eax,eax
                    Regs.Rip = startRip + (ulong)_dec.Pc;
                    break;

                case 0x99: // CDQ / CQO (sign extend eax->edx / rax->rdx)
                    {
                        if (_dec.RexW) Regs.Gp[2] = (Regs.Gp[0] & (1UL << 63)) != 0 ? ulong.MaxValue : 0;
                        else Regs.Gp[2] = (Regs.Gp[0] & (1UL << 31)) != 0 ? 0xFFFFFFFF : 0;
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;

                case 0x0F:
                    TwoByteOp(startRip);
                    break;

                default:
                    throw new CpuFault($"unsupported opcode 0x{op:X2} at 0x{startRip:X}");
            }

            if (TraceInstructions && InstructionsExecuted < 200)
            {
                _log.Trace("cpu", $"rip=0x{startRip:X} op=0x{op:X2} -> rip=0x{Regs.Rip:X} rax=0x{Regs.Gp[0]:X}");
            }
        }

        private void TwoByteOp(ulong startRip)
        {
            byte op = _dec.ReadByte();
            switch (op)
            {
                case 0x05: // SYSCALL
                    {
                        SyscallCount++;
                        Regs.Rip = startRip + (ulong)_dec.Pc; // advance past syscall
                        _kernel.HandleSyscall(this);
                    }
                    break;
                case 0x80: case 0x81: case 0x82: case 0x83:
                case 0x84: case 0x85: case 0x86: case 0x87:
                case 0x88: case 0x89: case 0x8A: case 0x8B:
                case 0x8C: case 0x8D: case 0x8E: case 0x8F: // Jcc rel32
                    {
                        int rel = (int)_dec.ReadU32();
                        if (CondJump(op - 0x80))
                            Regs.Rip = startRip + (ulong)rel;
                        else
                            Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;
                case 0x1F: // NOP (multi-byte, r/m)
                    {
                        var mr = _dec.ReadModRm();
                        if (mr.Mod != 3) _dec.EffectiveAddress(mr, Reg, startRip);
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;
                case 0xAF: // (REP) LODS/SCAS/CMPS prefixes handled elsewhere; treat as NOP for now
                    {
                        var mr = _dec.ReadModRm();
                        if (mr.Mod != 3) _dec.EffectiveAddress(mr, Reg, startRip);
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;
                case 0x10: case 0x11: case 0x12: case 0x13:
                case 0x14: case 0x15: case 0x16: case 0x17:
                case 0x18: case 0x19: // various SSE/MOVUPS etc. - stub as NOP with operand decode
                    {
                        var mr = _dec.ReadModRm();
                        if (mr.Mod != 3) _dec.EffectiveAddress(mr, Reg, startRip);
                        Regs.Rip = startRip + (ulong)_dec.Pc;
                    }
                    break;
                default:
                    throw new CpuFault($"unsupported 0F opcode 0x{op:X2} at 0x{startRip:X}");
            }
        }

        // ---- ALU helpers ----
        private bool CondJump(int cc)
        {
            bool c = Regs.Cf, z = Regs.Zf, s = Regs.Sf, o = Regs.Of;
            return cc switch
            {
                0 => o != s,            // JO
                1 => o == s,           // JNO
                2 => c,                // JB
                3 => !c,               // JAE
                4 => z,                // JE
                5 => !z,               // JNE
                6 => c || z,           // JBE
                7 => !c && !z,         // JA
                8 => s,                // JS
                9 => !s,               // JNS
                10 => Regs.Of,         // JP (parity, simplified)
                11 => !Regs.Of,
                12 => s != o,          // JLE
                13 => s == o,          // JG
                14 => z || (s != o),   // JLE
                15 => !z && (s == o),  // JG
                _ => false
            };
        }

        private void AluGroup(byte grp, int opSize, ulong startRip)
        {
            var mr = _dec.ReadModRm();
            int sub = mr.Reg;
            ulong imm = grp == 0x83 ? (ulong)(sbyte)_dec.ReadByte() : _dec.ReadU32();

            if (mr.Mod == 3)
            {
                int r = mr.Rm | (_dec.RexBase != 0 ? 8 : 0);
                ulong v = Reg(r);
                ulong res = AluOp(sub, ref v, imm, opSize, isMem: false);
                // CMP (sub 7) does not write back; AND/ADD/etc. do.
                if (sub != 7)
                    SetReg(r, Mask(v, opSize));
                _ = res;
            }
            else
            {
                ulong addr = _dec.EffectiveAddress(mr, Reg, startRip);
                ulong v = ReadGuest(addr, opSize);
                ulong res = AluOp(sub, ref v, imm, opSize, isMem: true);
                if (sub != 7)
                    WriteGuest(addr, Mask(v, opSize), opSize);
                _ = res;
            }
            Regs.Rip = startRip + (ulong)_dec.Pc;
        }

        private void AluRegMem(byte op, int opSize, ulong startRip)
        {
            var mr = _dec.ReadModRm();
            // x86 direction bit: op&2 == 0 => "r/m, r" (dest = r/m, dir 0);
            //                       op&2 == 2 => "r, r/m" (dest = reg, dir 1).
            int dir = (op & 2) != 0 ? 1 : 0;
            bool is8 = (op == 0x88 || op == 0x8A);
            if (is8) opSize = 1;

            if (mr.Mod == 3)
            {
                int dst = mr.Rm | (_dec.RexBase != 0 ? 8 : 0);
                int src = _dec.RegIndex(mr);
                if (op == 0x87) // XCHG: swap the two registers
                {
                    ulong tmp = Reg(dst);
                    SetReg(dst, Reg(src));
                    SetReg(src, tmp);
                }
                else if (op == 0x85) // TEST: no write-back
                {
                    _ = AluBinary(op, Reg(dst), Reg(src), opSize);
                }
                else
                {
                    ulong a = Reg(dst), b = Reg(src);
                    ulong r = AluBinary(op, a, b, opSize);
                    if (dir == 0) SetReg(dst, Mask(r, opSize));
                    else SetReg(src, Mask(r, opSize));
                }
            }
            else
            {
                ulong addr = _dec.EffectiveAddress(mr, Reg, startRip);
                if (op == 0x87) // XCHG mem <-> reg
                {
                    ulong mem = ReadGuest(addr, opSize);
                    ulong reg = Reg(_dec.RegIndex(mr));
                    WriteGuest(addr, Mask(reg, opSize), opSize);
                    SetReg(_dec.RegIndex(mr), mem);
                }
                else if (op == 0x85) // TEST: no write-back
                {
                    _ = AluBinary(op, ReadGuest(addr, opSize), Reg(_dec.RegIndex(mr)), opSize);
                }
                else if (dir == 0) // r/m <- r
                {
                    ulong b = Reg(_dec.RegIndex(mr));
                    ulong a = ReadGuest(addr, opSize);
                    ulong r = AluBinary(op, a, b, opSize);
                    WriteGuest(addr, Mask(r, opSize), opSize);
                }
                else // r <- r/m  (a = reg value, b = r/m value, so a op b is correct)
                {
                    ulong a = Reg(_dec.RegIndex(mr));
                    ulong b = ReadGuest(addr, opSize);
                    ulong r = AluBinary(op, a, b, opSize);
                    SetReg(_dec.RegIndex(mr), Mask(r, opSize));
                }
            }
            Regs.Rip = startRip + (ulong)_dec.Pc;
        }

        private ulong AluBinary(byte op, ulong a, ulong b, int opSize)
        {
            return op switch
            {
                0x01 or 0x03 => Add(a, b, opSize),
                0x09 or 0x0B => Or(a, b, opSize),
                0x11 or 0x13 => Add(a, b, opSize),  // ADC simplified (ignore carry in)
                0x19 or 0x1B => Sub(a, b, opSize),  // SBB simplified
                0x21 or 0x23 => And(a, b, opSize),
                0x29 or 0x2B => Sub(a, b, opSize),
                0x31 or 0x33 => Xor(a, b, opSize),
                0x39 or 0x3B => Sub(a, b, opSize),  // CMP
                0x85 => And(a, b, opSize),          // TEST
                0x87 => Xchg(a, b, opSize),
                _ => throw new CpuFault($"alu op 0x{op:X2}")
            };
        }

        // sub: 0 ADD,1 OR,2 ADC,3 SBB,4 AND,5 SUB,6 XOR,7 CMP
        private ulong AluOp(int sub, ref ulong v, ulong imm, int opSize, bool isMem)
        {
            return sub switch
            {
                0 => Add(v, imm, opSize),
                1 => Or(v, imm, opSize),
                2 => Add(v, imm, opSize),
                3 => Sub(v, imm, opSize),
                4 => And(v, imm, opSize),
                5 => Sub(v, imm, opSize),
                6 => Xor(v, imm, opSize),
                7 => Sub(v, imm, opSize),
                _ => throw new CpuFault($"alu group sub {sub}")
            };
        }

        private ulong Add(ulong a, ulong b, int opSize)
        {
            a = Mask(a, opSize); b = Mask(b, opSize);
            ulong full = a + b;
            ulong res = Mask(full, opSize);
            Regs.SetZfSf(res, opSize * 8);
            Regs.SetCf(full > Mask(ulong.MaxValue, opSize));
            // Overflow: both same sign, result differs
            bool of = ((a ^ res) & (b ^ res) & SignMask(opSize)) != 0;
            Regs.SetOf(of);
            Regs.SetAf(((a ^ b ^ res) & 0x10) != 0);
            return res;
        }
        private ulong Sub(ulong a, ulong b, int opSize)
        {
            a = Mask(a, opSize); b = Mask(b, opSize);
            ulong full = a - b;
            ulong res = Mask(full, opSize);
            Regs.SetZfSf(res, opSize * 8);
            Regs.SetCf(b > a);
            bool of = ((a ^ b) & (a ^ res) & SignMask(opSize)) != 0;
            Regs.SetOf(of);
            Regs.SetAf(((a ^ b ^ res) & 0x10) != 0);
            return res;
        }
        private ulong And(ulong a, ulong b, int opSize)
        {
            ulong res = Mask(a & b, opSize);
            Regs.SetZfSf(res, opSize * 8);
            Regs.SetCf(false); Regs.SetOf(false);
            return res;
        }
        private ulong Or(ulong a, ulong b, int opSize)
        {
            ulong res = Mask(a | b, opSize);
            Regs.SetZfSf(res, opSize * 8);
            Regs.SetCf(false); Regs.SetOf(false);
            return res;
        }
        private ulong Xor(ulong a, ulong b, int opSize)
        {
            ulong res = Mask(a ^ b, opSize);
            Regs.SetZfSf(res, opSize * 8);
            Regs.SetCf(false); Regs.SetOf(false);
            return res;
        }
        private ulong Xchg(ulong a, ulong b, int opSize) => b; // handled by caller

        private void Test(ulong a, ulong b, int opSize)
        {
            ulong res = Mask(a & b, opSize);
            Regs.SetZfSf(res, opSize * 8);
            Regs.SetCf(false); Regs.SetOf(false);
        }
        private void Test8(ulong a, byte b)
        {
            byte res = (byte)(a & b);
            Regs.SetZfSf(res, 8);
            Regs.SetCf(false); Regs.SetOf(false);
        }

        private ulong SignMask(int opSize) => 1UL << (opSize * 8 - 1);
        private ulong Mask(ulong v, int opSize)
        {
            return opSize switch
            {
                1 => v & 0xFF,
                2 => v & 0xFFFF,
                4 => v & 0xFFFFFFFF,
                _ => v
            };
        }

        private void MovRegMem(byte op, int opSize, ulong startRip)
        {
            bool is8 = (op == 0x88 || op == 0x8A);
            bool toMem = (op == 0x88 || op == 0x89);
            if (is8) opSize = 1;
            var mr = _dec.ReadModRm();
            int regIdx = _dec.RegIndex(mr);
            if (mr.Mod == 3)
            {
                int rm = mr.Rm | (_dec.RexBase != 0 ? 8 : 0);
                if (toMem) SetReg(rm, Mask(Reg(regIdx), opSize));
                else SetReg(regIdx, Mask(Reg(rm), opSize));
            }
            else
            {
                ulong addr = _dec.EffectiveAddress(mr, Reg, startRip);
                if (toMem) WriteGuest(addr, Mask(Reg(regIdx), opSize), opSize);
                else SetReg(regIdx, ReadGuest(addr, opSize));
            }
            Regs.Rip = startRip + (ulong)_dec.Pc;
        }

        // C7 (MOV r/m, imm): with REX.W the immediate is imm32 sign-extended to
        // 64 bits (NOT a full imm64; that is the B8+ opcode form).
        private ulong ReadMovImm()
        {
            int imm = (int)_dec.ReadU32();
            return _dec.RexW ? (ulong)(long)imm : (ulong)(uint)imm;
        }

        private void MovImm(byte op, int opSize, ulong startRip)
        {
            var mr = _dec.ReadModRm();
            bool is8 = op == 0xC6;
            int size = is8 ? 1 : opSize;
            if (mr.Mod == 3)
            {
                int rm = mr.Rm | (_dec.RexBase != 0 ? 8 : 0);
                ulong v = is8 ? _dec.ReadByte() : ReadMovImm();
                SetReg(rm, Mask(v, size));
            }
            else
            {
                ulong addr = _dec.EffectiveAddress(mr, Reg, startRip);
                ulong v = is8 ? _dec.ReadByte() : ReadMovImm();
                WriteGuest(addr, Mask(v, size), size);
            }
            Regs.Rip = startRip + (ulong)_dec.Pc;
        }

        private void Group5(int opSize, ulong startRip)
        {
            var mr = _dec.ReadModRm();
            int sub = mr.Reg;
            if (mr.Mod == 3)
            {
                int rm = mr.Rm | (_dec.RexBase != 0 ? 8 : 0);
                switch (sub)
                {
                    case 0: SetReg(rm, Add(Reg(rm), 1, opSize)); break;       // INC
                    case 1: SetReg(rm, Sub(Reg(rm), 1, opSize)); break;       // DEC
                    case 2: case 4: // CALL near indirect reg
                        Push(startRip + (ulong)_dec.Pc);
                        Regs.Rip = Reg(rm);
                        return;
                    case 6: Regs.Rip = Reg(rm); return;                       // JMP r/m
                    default: throw new CpuFault($"group5 sub {sub} reg");
                }
                Regs.Rip = startRip + (ulong)_dec.Pc;
            }
            else
            {
                ulong addr = _dec.EffectiveAddress(mr, Reg, startRip);
                switch (sub)
                {
                    case 0: WriteGuest(addr, Add(ReadGuest(addr, opSize), 1, opSize), opSize); break;
                    case 1: WriteGuest(addr, Sub(ReadGuest(addr, opSize), 1, opSize), opSize); break;
                    case 2: case 4: // CALL near indirect mem
                        Push(startRip + (ulong)_dec.Pc);
                        Regs.Rip = ReadGuest(addr, 8);
                        return;
                    case 6: Regs.Rip = ReadGuest(addr, 8); return;            // JMP [mem]
                    default: throw new CpuFault($"group5 sub {sub} mem");
                }
                Regs.Rip = startRip + (ulong)_dec.Pc;
            }
        }

        // ---- stack ----
        public void Push(ulong v)
        {
            Regs.Gp[4] -= 8;
            _mem.WriteU64(Regs.Gp[4], v);
        }
        public ulong Pop()
        {
            ulong v = _mem.ReadU64(Regs.Gp[4]);
            Regs.Gp[4] += 8;
            return v;
        }

        /// <summary>Read a NUL-terminated C string from guest memory (for syscall logging).</summary>
        public string ReadCStringGuest(ulong addr, int max = 256)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < max; i++)
            {
                byte b = _mem.ReadU8(addr + (ulong)i);
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }

    public sealed class SyscallExit : Exception
    {
        public int Code;
        public SyscallExit(int code) { Code = code; }
    }
}
