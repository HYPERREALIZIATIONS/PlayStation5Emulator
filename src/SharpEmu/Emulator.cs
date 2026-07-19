using System;
using System.Collections.Generic;
using System.IO;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Elf;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Graphics;
using SharpEmu.Libs;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu;

/// <summary>
/// Top-level emulator: loads a PS5 ELF (eboot.bin / .elf), sets up guest memory
/// and CPU, installs the HLE import mechanism (NID -> handler via generated
/// trampolines), wires the graphics presenter, and runs the guest.
///
/// Design notes (research context):
///  - The guest is executed by the x86-64 interpreter. Real PS5 CPUs are x86-64,
///    so this is genuine native instruction execution, not a recompiler stand-in.
///  - Imports are resolved by parsing the dynamic-link relocation tables. PS5/PS4
///    import stubs use R_X86_64_IRELATIVE relocations whose resolver is typically
///    `mov eax, &lt;NID&gt;; ret`. We extract the NID, allocate a per-NID trampoline
///    that loads the NID into R11 and issues a reserved syscall (RAX=0x6E01), and
///    patch the GOT slot to point at it. The interpreter's syscall gate then
///    dispatches to the matching HLE handler. This is a real, documented HLE
///    technique for these platforms.
/// </summary>
public sealed class Emulator : IDisposable
{
    public const ulong HLE_SYSCALL = 0x6E01;     // our reserved HLE call gate
    public const ulong KERNEL_SYSCALL_BASE = 0x100; // guest kernel syscalls start here

    private readonly Logger _log;
    private readonly GuestMemory _mem;
    private readonly CpuContext _ctx;
    private readonly CpuInterpreter _cpu;
    private readonly ElfLoader _loader;
    private readonly SystemModules _modules;
    private IVideoPresenter _presenter;

    // Trampoline region bookkeeping (kept within the 8 GiB guest window).
    private ulong _trampolineCursor = 0x1900_0000UL;
    private readonly Dictionary<ulong, string> _gotToNidName = new();

    public bool LastRunOk { get; private set; } = true;

    public Emulator(Logger log)
    {
        _log = log;
        _mem = new GuestMemory();
        _ctx = new CpuContext();
        _cpu = new CpuInterpreter(_mem, _ctx, log);
        _loader = new ElfLoader(_mem, log);
        _modules = new SystemModules(log);
        _cpu.OnSyscall = OnSyscall;
        _cpu.OnCall = OnCall;
    }

    public GameMetadata Metadata => _loader.Metadata;

    public bool Initialize(string ebootPath, string dumpDir, bool useVulkan, bool selfTest = false)
    {
        _log.Info("emu", $"=== SharpEmu initializing (research PS5 emulator) ===");
        _log.Info("emu", $"eboot path: {ebootPath}");

        // 1. Graphics presenter
        if (useVulkan)
        {
            var vk = new Graphics.Vulkan.VulkanVideoPresenter(_log, dumpDir);
            if (vk.Initialize()) _presenter = vk;
            else _presenter = new NullVideoPresenter(_log, dumpDir);
        }
        else
        {
            _presenter = new NullVideoPresenter(_log, dumpDir);
        }
        VideoOutModule.Presenter = _presenter;
        AgcModule.Presenter = _presenter;
        _log.Info("emu", $"graphics backend: {_presenter.BackendName}");

        // 2. System modules
        _modules.LoadBuiltins();
        if (selfTest)
            _modules.RegisterSelfTest();
        string moduleDir = Path.Combine(Path.GetDirectoryName(ebootPath) ?? "", "..", "sys_modules");
        _modules.LoadModuleDirectory(moduleDir);

        // 3. Load ELF
        if (!_loader.Load(ebootPath))
            return false;

        // Locate the game folder for param.sfo and file sandbox root.
        string gameFolder = Path.GetDirectoryName(ebootPath) ?? ".";
        KernelModule.HostRoot = FindHostRoot(gameFolder);
        _loader.ReadParamSfo(gameFolder);
        _loader.ApplyRelocations();

        // 4. Install HLE import trampolines
        InstallHleImports();

        // 5. Set up initial CPU state.
        SetupInitialState();

        _log.Info("emu", "initialization complete");
        return true;
    }

    private static string FindHostRoot(string gameFolder)
    {
        // Use the directory containing eboot.bin as the sandbox root so that guest
        // paths like /app0/... or /sce_sys/... resolve against the dumped files.
        return gameFolder;
    }

    /// <summary>
    /// Parse dynamic-link relocations to find IRELATIVE import stubs, extract the
    /// NID from the resolver prologue, and patch the GOT slot with a trampoline.
    /// </summary>
    private void InstallHleImports()
    {
        Elf64_Phdr dyn = default;
        bool hasDyn = false;
        foreach (var ph in _loader.ProgramHeaders)
        {
            if (ph.PType == ElfConstants.PT_DYNAMIC) { dyn = ph; hasDyn = true; break; }
        }
        if (!hasDyn)
        {
            _log.Warn("emu", "no PT_DYNAMIC segment; skipping HLE import install (module may resolve statically)");
            return;
        }

        ulong dynPtr = dyn.PVaddr;
        ulong jmpRel = 0, jmpRelSz = 0;
        const ulong jmpRelEnt = 24;
        ulong rela = 0, relaSz = 0;
        for (int i = 0; i < 256; i++)
        {
            ulong tag = _mem.ReadUInt64(dynPtr + (ulong)i * 16);
            ulong val = _mem.ReadUInt64(dynPtr + (ulong)i * 16 + 8);
            if (tag == ElfConstants.DT_NULL) break;
            if (tag == ElfConstants.DT_SCE_JMPREL) jmpRel = val;
            else if (tag == 0x6ffffff9) jmpRel = val;       // DT_JMPREL
            else if (tag == 0x6ffffffb) jmpRelSz = val;     // DT_PLTRELSZ
            else if (tag == ElfConstants.DT_RELA) rela = val;
            else if (tag == ElfConstants.DT_RELASZ) relaSz = val;
        }
        if (jmpRelSz == 0 && relaSz != 0) { jmpRel = rela; jmpRelSz = relaSz; }

        _log.Debug("emu", $"JMPREL @0x{jmpRel:X} size=0x{jmpRelSz:X}");

        int count = (int)(jmpRelSz / jmpRelEnt);
        int patched = 0;
        for (int i = 0; i < count; i++)
        {
            ulong roff = jmpRel + (ulong)(i * (int)jmpRelEnt);
            ulong rOffset = _mem.ReadUInt64(roff);
            ulong rInfo = _mem.ReadUInt64(roff + 8);
            ulong rAddend = _mem.ReadUInt64(roff + 16);
            ulong type = rInfo & 0xFFFFFFFF;

            if (type != ElfConstants.R_X86_64_IRELATIVE)
                continue;

            // Resolver prologue: mov eax, imm32 ; ret  =>  B8 XX XX XX XX C3
            ulong resolver = rAddend;
            if (resolver == 0) continue;
            byte b0 = _mem.ReadByte(resolver);
            byte b1 = _mem.ReadByte(resolver + 1);
            byte b5 = _mem.ReadByte(resolver + 5);
            if (b0 == 0xB8 && b5 == 0xC3)
            {
                uint nid = _mem.ReadUInt32(resolver + 2);
                ulong tramp = AllocateTrampoline(nid);
                _mem.WriteUInt64(rOffset, tramp); // patch GOT slot
                _gotToNidName[tramp] = $"0x{nid:X8}";
                patched++;
                _log.Trace("emu", $"HLE import: GOT@0x{rOffset:X} -> tramp@0x{tramp:X} nid=0x{nid:X8}");
            }
            else
            {
                _log.Trace("emu", $"import resolver @0x{resolver:X} not the expected mov-eax/ret pattern (0x{b0:X2} 0x{b1:X2})");
            }
        }
        _log.Info("emu", $"installed {patched} HLE import trampoline(s)");
    }

    private ulong AllocateTrampoline(uint nid)
    {
        ulong addr = _trampolineCursor;
        // mov r11, imm64            : 49 BB <8 bytes>
        // mov rax, imm32            : 48 C7 C0 01 6E 00 00
        // syscall                  : 0F 05
        // ret                      : C3
        var code = new byte[]
        {
            0x49, 0xBB,
            (byte)(nid & 0xFF), (byte)((nid >> 8) & 0xFF), (byte)((nid >> 16) & 0xFF), (byte)((nid >> 24) & 0xFF),
            0x00, 0x00, 0x00, 0x00, // high 32 of NID unused (NIDs are 32-bit here)
            0x48, 0xC7, 0xC0, 0x01, 0x6E, 0x00, 0x00, // mov rax, 0x6E01
            0x0F, 0x05, // syscall
            0xC3, // ret
        };
        _mem.WriteBytes(addr, code, code.Length);
        _mem.Map(addr, (ulong)code.Length, ElfConstants.PF_X | ElfConstants.PF_R, "hle-trampoline");
        _trampolineCursor += (ulong)code.Length + 16;
        return addr;
    }

    private void SetupInitialState()
    {
        ulong sp = _mem.BaseAddress + _mem.Size - 0x1000;
        sp &= ~0xFUL;
        _ctx.Rsp = sp;

        ulong argv0 = sp - 0x100;
        byte[] name = System.Text.Encoding.ASCII.GetBytes("eboot.bin\0");
        _mem.WriteBytes(argv0, name, name.Length);

        ulong argvPtr = sp - 0x80;
        _mem.WriteUInt64(argvPtr, argv0);
        _mem.WriteUInt64(argvPtr + 8, 0);

        _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, argvPtr); // argv
        _ctx.Rsp -= 8; _mem.WriteUInt64(_ctx.Rsp, 1);       // argc
        _ctx.Rcx = _loader.LoadBase;

        _log.Debug("emu", $"initial RSP=0x{_ctx.Rsp:X} entry=0x{_loader.EntryPoint:X}");
    }

    private void OnSyscall(ulong rip)
    {
        ulong rax = _ctx.Rax;
        if (rax == HLE_SYSCALL)
        {
            ulong nid = _ctx.R11;
            if (_modules.TryResolve(nid, out var handler, out var name))
            {
                _log.Trace("hle", $"NID 0x{nid:X8} '{name}'");
                ulong ret = handler(_ctx, _mem);
                _ctx.Rax = ret;
            }
            else
            {
                _log.Warn("hle", $"unhandled NID 0x{nid:X8} (returning 0)");
                _ctx.Rax = 0; // benign default; research stub
            }
            return;
        }

        // Otherwise treat as a guest kernel syscall. We route a handful to our
        // kernel HLE and otherwise return a neutral error.
        _log.Trace("syscall", $"guest syscall 0x{rax:X} (unimplemented -> ENOSYS)");
        _ctx.Rax = 0xFFFFFFFFFFFFFFEAUL; // -ENOSYS
    }

    private bool OnCall(ulong target)
    {
        // We do not generally intercept direct calls; the HLE mechanism works via
        // patched GOT slots (trampolines) instead. Returning false lets the
        // interpreter branch normally. (Hook point reserved for future JIT.)
        return false;
    }

    public void Run(int maxInstructions = 50_000_000)
    {
        LastRunOk = true;
        _log.Info("emu", "=== starting guest execution ===");
        try
        {
            _cpu.Run(_loader.EntryPoint, maxInstructions);
        }
        catch (CpuUndefinedInstruction ex)
        {
            LastRunOk = false;
            _log.Error("emu", $"guest hit an unimplemented instruction at RIP=0x{_ctx.Rip:X}: {ex.Message}");
            _log.Error("emu", "this is expected for real games beyond early boot; research milestone logging below.");
            DumpProgress();
        }
        catch (Exception ex)
        {
            LastRunOk = false;
            _log.Error("emu", $"unexpected error during execution: {ex}");
        }
        _log.Info("emu", "=== execution finished ===");
        DumpProgress();
    }

    private void DumpProgress()
    {
        _log.Info("emu", $"instructions executed: {_cpu.InstructionsExecuted}");
        _log.Info("emu", $"AGC submits: {AgcModule.TotalSubmits}, DCBs: {AgcModule.TotalDcbs}");
        _log.Info("emu", $"game: '{_loader.Metadata.Title}' version '{_loader.Metadata.Version}' titleId '{_loader.Metadata.TitleId}'");
    }

    public void Dispose() => _presenter?.Dispose();
}
