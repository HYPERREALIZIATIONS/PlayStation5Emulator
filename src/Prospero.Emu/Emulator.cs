using System;
using System.IO;
using System.Collections.Generic;
using Prospero.Emu.Core;
using Prospero.Emu.Format;
using Prospero.Emu.Cpu;
using Prospero.Emu.Kernel;
using Prospero.Emu.Graphics;

namespace Prospero.Emu
{
    /// <summary>
    /// Top-level orchestrator: loads an executable, extracts game info, sets up
    /// guest memory + CPU + partial kernel + AGC, loads system module stubs, and
    /// runs the program until it finishes or hits a research limit.
    /// </summary>
    public sealed class Emulator
    {
        private readonly Logger _log;
        private GuestMemory _mem = null!;
        public Emulator(Logger log) => _log = log;

        public int Run(string exePath, string? gameRoot, string? logPath)
        {
            _log.Info("emu", "Prospero Emu - educational PS5 research emulator");
            _log.Info("emu", "THIS TOOL IS FOR LEGAL, EDUCATIONAL RESEARCH ONLY. Supply only");
            _log.Info("emu", "legally-obtained, already-decrypted ELF/fself files. No keys/DRM.");

            var mem = new GuestMemory();
            _mem = mem;
            var loader = new SelfLoader(_log);
            LoadedExecutable exe;
            try
            {
                exe = loader.Load(exePath);
            }
            catch (Exception ex)
            {
                _log.Error("emu", $"Failed to load executable: {ex.Message}");
                return 2;
            }

            // Game info.
            var info = new GameInfoExtractor(_log).Extract(exe);
            info.Print(_log);

            // Map segments into guest memory.
            const ulong LoadBase = 0x400000; // typical ELF base for PIE/dynamic
            bool isDynamic = exe.Header.Type == ElfConsts.ET_DYN ||
                             exe.Header.Type == ElfConsts.ET_SCE_DYNAMIC ||
                             exe.Header.Type == ElfConsts.ET_SCE_DYNEXEC;
            ulong entry = exe.Header.Entry;
            if (isDynamic)
            {
                entry += LoadBase;
            }
            foreach (var seg in exe.Segments)
            {
                if (seg.Header.Type == ElfConsts.PT_LOAD)
                {
                    ulong vaddr = isDynamic
                        ? seg.Header.Vaddr + LoadBase : seg.Header.Vaddr;
                    mem.Commit(vaddr, Math.Max(seg.Header.Memsz, 1));
                    mem.Write(vaddr, seg.Data);
                    _log.Debug("emu", $"mapped LOAD seg @ 0x{vaddr:X} (0x{seg.Data.Length:X} bytes)");
                }
            }

            // System modules: parse the dynlib imports (the modules the game needs).
            var dyn = new DynlibParser(_log);
            dyn.Parse(exe);
            ulong moduleBase = isDynamic ? LoadBase : 0;
            PatchImports(dyn, moduleBase);
            LoadSystemModules(dyn.ImportedLibraries);

            // Graphics.
            var vk = new VulkanHost(_log, mem);
            var agc = new Agc(_log, vk);
            agc.Init();

            // Kernel personality + filesystem.
            var fs = new HackedFileSystem(_log, gameRoot);
            var kernel = new PartialKernel(_log, mem, fs, agc);

            // CPU.
            var cpu = new CpuCore(_log, mem, kernel)
            {
                MaxInstructions = 40_000_000,
                TraceInstructions = false,
            };
            ulong stackTop = 0x6000_0000;
            mem.Commit(stackTop - 0x100000, 0x100000);
            cpu.Reset(entry, stackTop);

            _log.Info("emu", $"Starting execution at entry 0x{entry:X} (RSP=0x{stackTop:X})");
            cpu.Run();
            _log.Info("emu", $"Execution ended. instructions={cpu.InstructionsExecuted} syscalls={cpu.SyscallCount} exit={cpu.ExitCode}");

            vk.Shutdown();
            return cpu.ExitCode == 0 ? 0 : 1;
        }

        /// <summary>
        /// Patches the GOT so that imported libkernel calls (resolved via
        /// R_X86_64_JUMP_SLOT relocations) jump to a synthetic trampoline that
        /// does `mov rax, nid; syscall; ret`. The kernel then recognises the NID
        /// and routes it to the appropriate stub. This is how a homebrew/ELF that
        /// links libkernel can actually run under the research kernel.
        /// </summary>
        private void PatchImports(DynlibParser dyn, ulong moduleBase)
        {
            if (dyn.Relocations.Count == 0) return;
            _log.Info("emu", $"Patching {dyn.Relocations.Count} imported-call GOT slots...");

            // Allocate one trampoline page and place a 16-byte trampoline per NID.
            ulong page = _scratch;
            _scratch += 0x1000;
            _mem.Commit(page, 0x1000);

            var seen = new Dictionary<ulong, ulong>();
            int off = 0;
            foreach (var rel in dyn.Relocations)
            {
                if (!seen.TryGetValue(rel.Nid, out ulong tramp))
                {
                    tramp = page + (ulong)off;
                    // 48 B8 <nid:8> 0F 05 C3  => mov rax, imm64(nid); syscall; ret  (16 bytes)
                    var code = new byte[]
                    {
                        0x48, 0xB8,
                        (byte)(rel.Nid & 0xFF), (byte)((rel.Nid >> 8) & 0xFF),
                        (byte)((rel.Nid >> 16) & 0xFF), (byte)((rel.Nid >> 24) & 0xFF),
                        0, 0, 0, 0,            // high 32 bits zero
                        0x0F, 0x05, 0xC3, 0, 0, 0
                    };
                    _mem.Write(tramp, code);
                    off += 16;
                    seen[rel.Nid] = tramp;
                    _log.Debug("emu", $"  trampoline for NID 0x{rel.Nid & 0xFFFFFFFF:X8} ({rel.Name}) @ 0x{tramp:X}");
                }
                ulong gotVa = moduleBase + rel.GotVa;
                _mem.WriteU64(gotVa, tramp);
            }
        }

        private ulong _scratch = 0x7800_0000_0000;

        private void LoadSystemModules(List<string> libs)
        {
            _log.Info("emu", "Loading system module stubs:");
            foreach (var lib in libs)
            {
                string shortName = lib.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
                    ? lib : lib + ".prx";
                _log.Info("emu", $"  - {shortName} (stub: returns success / synthetic handle)");
            }
            if (libs.Count == 0)
                _log.Info("emu", "  (no imported libraries; static or homebrew binary)");
        }
    }
}
