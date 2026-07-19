using System;
using System.Collections.Generic;
using System.Text;
using Prospero.Emu.Cpu;
using Prospero.Emu.Core;
using Prospero.Emu.Graphics;

namespace Prospero.Emu.Kernel
{
    /// <summary>
    /// Partial FreeBSD/Orbis/Prospero kernel personality.
    ///
    /// On PS5 the SYSCALL instruction (0x0F 0x05) is used by libkernel. RAX holds
    /// the syscall id; RDI, RSI, RDX, R10, R8, R9 hold args (FreeBSD convention).
    /// We implement a *partial* set so that CRT init, memory reservation, logging,
    /// file ops, thread/process setup, and AGC init stubs can progress.
    ///
    /// This emulator does NOT ship a real libkernel or system modules. Instead we
    /// provide benign stubs that return success / synthetic handles and log every
    /// syscall so researchers can see what a title requires. Known Sony syscall
    /// ids are named where documented; unknown ids are logged and return 0.
    /// </summary>
    public sealed class PartialKernel : IKernel
    {
        private readonly Logger _log;
        private readonly GuestMemory _mem;
        private readonly HackedFileSystem _fs;
        private readonly Graphics.Agc _agc;

        private ulong _nextHandle = 0x1000;
        private ulong _scratchHeap = 0x7000_0000_0000; // a high guest region for synthetic buffers
        private readonly Dictionary<ulong, string> _syscallNames = new();
        private readonly HashSet<ulong> _knownNids = new();

        public PartialKernel(Logger log, GuestMemory mem, HackedFileSystem fs, Graphics.Agc agc)
        {
            _log = log;
            _mem = mem;
            _fs = fs;
            _agc = agc;
            foreach (var k in KnownNids.All) _knownNids.Add(k);
            InitSyscallNames();
        }

        /// <summary>Registers the NIDs a loaded module imports so that calls routed
        /// through the synthetic trampolines are recognised as libkernel calls.</summary>
        public void RegisterNids(IEnumerable<ulong> nids)
        {
            foreach (var n in nids) _knownNids.Add(n & 0xFFFFFFFF);
        }

        private void InitSyscallNames()
        {
            // FreeBSD syscall ids (a subset).
            _syscallNames[1] = "exit";
            _syscallNames[3] = "read";
            _syscallNames[4] = "write";
            _syscallNames[17] = "obreak";
            _syscallNames[73] = "getcontext";
            _syscallNames[74] = "setcontext";
            _syscallNames[97] = "mmap";
            _syscallNames[73] = "munmap";
            _syscallNames[199] = "sysctl";
            _syscallNames[476] = "thr_new";
            _syscallNames[477] = "thr_self";
            _syscallNames[478] = "thr_kill";
            _syscallNames[506] = "pdfork";
            // Sony / Orbis specific (approximate, for logging only).
            _syscallNames[0x200] = "sceKernelGetProcParam";
            _syscallNames[0x201] = "sceKernelAllocateDirectMemory";
            _syscallNames[0x202] = "sceKernelMapDirectMemory";
            _syscallNames[0x203] = "sceKernelAllocateMemoryBlock";
            _syscallNames[0x204] = "sceKernelGetMemoryBlockBase";
        }

        public void HandleSyscall(CpuCore cpu)
        {
            ulong id = cpu.Regs.Gp[0];
            ulong a1 = cpu.Regs.Gp[7];  // RDI
            ulong a2 = cpu.Regs.Gp[6];  // RSI
            ulong a3 = cpu.Regs.Gp[2];  // RDX
            ulong a4 = cpu.Regs.Gp[10]; // R10
            ulong a5 = cpu.Regs.Gp[8];  // R8
            ulong a6 = cpu.Regs.Gp[9];  // R9

            string name = _syscallNames.TryGetValue(id, out var n) ? n : $"syscall_0x{id:X}";

            // A NID in RAX means the call came from a synthetic libkernel
            // trampoline (mov rax, nid; syscall). Route it to the libkernel layer.
            if (_knownNids.Contains(id))
            {
                name = KnownNids.Resolve(id);
                _log.Trace("libkernel", $"NID 0x{id:X8} {name} args=({a1:X}, {a2:X}, {a3:X}, {a4:X}, {a5:X}, {a6:X})");
                cpu.Regs.Gp[0] = HandleLibkernel(id, cpu, a1, a2, a3, a4, a5, a6);
                return;
            }

            _log.Trace("syscall", $"#{id} {name} args=({a1:X}, {a2:X}, {a3:X}, {a4:X}, {a5:X}, {a6:X})");
            ulong result = Dispatch(id, name, cpu, a1, a2, a3, a4, a5, a6);
            cpu.Regs.Gp[0] = result;
        }

        /// <summary>Resolves an imported libkernel function (by NID) to benign
        /// research behavior. Returns 0 / success or a synthetic handle.</summary>
        private ulong HandleLibkernel(ulong nid, CpuCore cpu, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
        {
            switch (nid & 0xFFFFFFFF)
            {
                case 0x0DEB17A8: // sceKernelExitProcess
                case 0xCD3C83E0: // sceKernelExit
                    throw new SyscallExit((int)a1);
                case 0xEF092AAC: // sceKernelGetProcParam
                    return AllocScratch(0x100);
                case 0x7D37B4E8: // sceKernelAllocateDirectMemory
                    {
                        ulong pout = a6;
                        ulong baseAddr = AllocScratch(a3);
                        if (pout != 0) _mem.WriteU64(pout, baseAddr);
                        return 0;
                    }
                case 0xA186B7E0: // sceKernelMapDirectMemory
                    return a1;
                case 0x2C69DD61: // sceKernelAllocateMemoryBlock
                    {
                        ulong pbase = a5;
                        ulong baseAddr = AllocScratch(a3);
                        if (pbase != 0) _mem.WriteU64(pbase, baseAddr);
                        return 0;
                    }
                case 0xAFBB2A3E: // sceKernelGetMemoryBlockBase
                    { _mem.WriteU64(a2, AllocScratch(0x1000)); return 0; }
                case 0x35DAE69E: // sceKernelUsleep
                    return 0;
                case 0x689C8FF3: // sceKernelGetThreadId
                case 0x4C9A9E8C: // sceKernelGetCurrentThread
                    return 1;
                case 0x7FE4B4C8: // sceVideoOutOpen
                    return _agc.VideoOutOpen(cpu, a1, a2, a3, a4);
                case 0xCD3286AB: // sceVideoOutRegisterBuffers
                    return _agc.VideoOutRegisterBuffers(cpu, a1, a2, a3, a4, a5, a6);
                case 0xCB3C7E4C: // sceVideoOutSubmitFlip
                    return _agc.VideoOutSubmitFlip(cpu, a1, a2, a3, a4);
                case 0x5C1C0A93: // sceGnmGetAGCInterface
                    return _agc.GetAgcInterface(cpu, a1);
                case 0xBA9C4B1B: // sceGnmSubmitCommandBuffers
                    return _agc.SubmitCommandBuffers(cpu, (int)a1, a2);
                case 0x8F6FC354: // sceGnmCreateGpuMemory
                case 0xAD2D4BA0: // sceGnmDestroyGpuMemory
                case 0x9E7C7C0B: // sceGnmInitDefaultGpuMemory
                    return NewHandle();
                case 0x5C0D8A37: // sceSysmoduleLoadModule
                case 0x4FC36FCE: // sceSysmoduleUnloadModule
                case 0x42F6E558: // sceSysmoduleIsLoaded
                    return 0;
                default:
                    _log.Debug("libkernel", $"Unhandled NID 0x{nid & 0xFFFFFFFF:X8} ({KnownNids.Resolve(nid)}) -> 0");
                    return 0;
            }
        }

        private ulong Dispatch(ulong id, string name, CpuCore cpu, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
        {
            switch (id)
            {
                case 1: // exit
                    throw new SyscallExit((int)a1);

                case 3: // read(fd, buf, n)
                    {
                        int n = _fs.Read((int)a1, _mem, a2, (int)a3);
                        return (ulong)(long)n;
                    }
                case 4: // write(fd, buf, n) - echo to log
                    {
                        if (a1 == 1 || a1 == 2) // stdout/stderr
                        {
                            string s = cpu.ReadCStringGuest(a2, (int)Math.Min(a3, 4096));
                            _log.Info("guest.stdout", s.TrimEnd('\n'));
                            return a3;
                        }
                        int n = _fs.Write((int)a1, _mem, a2, (int)a3);
                        return (ulong)(long)n;
                    }
                case 17: //obreak - always "ok"
                case 73: // munmap
                    return 0;

                case 97: // mmap(addr, len, prot, flags, fd, off)
                    return Mmap(a2, a3);

                case 73 + 400: // placeholder
                    return 0;

                case 0x200: // sceKernelGetProcParam -> pointer to a synthetic proc param
                    {
                        ulong p = AllocScratch(0x100);
                        return p;
                    }
                case 0x201: // sceKernelAllocateDirectMemory(start, len, align, flags, kind, pout)
                    {
                        ulong outPtr = a6;
                        ulong baseAddr = AllocScratch(a3); // treat as guest alloc
                        if (outPtr != 0) _mem.WriteU64(outPtr, baseAddr);
                        return 0;
                    }
                case 0x202: // sceKernelMapDirectMemory - return the address
                    return a1;

                case 0x203: // sceKernelAllocateMemoryBlock(namePtr, type, size, flags, pbase)
                    {
                        ulong pbase = a5;
                        ulong baseAddr = AllocScratch(a3);
                        if (pbase != 0) _mem.WriteU64(pbase, baseAddr);
                        return 0;
                    }
                case 0x204: // sceKernelGetMemoryBlockBase(blockId, pbase)
                    {
                        _mem.WriteU64(a2, AllocScratch(0x1000));
                        return 0;
                    }

                // ---- AGC / graphics init stubs ----
                case 0x500: // sceGnmGetAGCInterface(ptr)
                    return _agc.GetAgcInterface(cpu, a1);
                case 0x501: // sceGnmSubmitCommandBuffers(count, ptr)
                    return _agc.SubmitCommandBuffers(cpu, (int)a1, a2);
                case 0x502: // sceGnmCreateGpuMemory(size, ...) -> handle
                case 0x503:
                    return NewHandle();
                case 0x510: // sceVideoOutOpen
                    return _agc.VideoOutOpen(cpu, a1, a2, a3, a4);
                case 0x511: // sceVideoOutRegisterBuffers
                    return _agc.VideoOutRegisterBuffers(cpu, a1, a2, a3, a4, a5, a6);
                case 0x512: // sceVideoOutSubmitFlip
                    return _agc.VideoOutSubmitFlip(cpu, a1, a2, a3, a4);

                default:
                    // Unknown syscall: log at debug level, return benign 0 so the
                    // program can (usually) keep going for research triage.
                    _log.Debug("syscall", $"Unhandled {name} -> returning 0 (benign stub)");
                    return 0;
            }
        }

        private ulong Mmap(ulong len, ulong prot)
        {
            if (len == 0) return 0;
            ulong addr = AllocScratch(len);
            _mem.Commit(addr, len);
            return addr;
        }

        private ulong AllocScratch(ulong size)
        {
            size = (size + 0xFFF) & ~0xFFFUL;
            ulong a = _scratchHeap;
            _scratchHeap += size;
            return a;
        }

        private ulong NewHandle() => ++_nextHandle | 0x8000_0000_0000_0000;

        public string ResolveNidName(ulong nid) => KnownNids.Resolve(nid);
    }
}
