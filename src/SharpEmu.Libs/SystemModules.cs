using System;
using System.Collections.Generic;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Fiber;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Libc;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.SelfTest;

namespace SharpEmu.Libs;

/// <summary>
/// Loads the set of built-in HLE system modules and (optionally) real module ELF
/// files from a sys_modules directory. Resolves NIDs into handlers the guest can
/// call. This mirrors the PS5's "load system modules (prx / sys_module)" step.
/// </summary>
public sealed class SystemModules
{
    private readonly NidTable _table = new();
    private readonly Logger _log;
    private readonly List<SysModule> _modules = new();

    public NidTable Nids => _table;

    public SystemModules(Logger log)
    {
        _log = log;
        EmulatorDiagnostics.Log = log;
    }

    /// <summary>Register all the built-in research modules.</summary>
    public void LoadBuiltins()
    {
        Register(new KernelModule());
        Register(new LibcModule());
        Register(new FiberModule());
        Register(new AmprModule());
        Register(new VideoOutModule());
        Register(new AgcModule());
        _log.Info("modules", $"loaded {_modules.Count} built-in HLE modules");
    }

    /// <summary>Registers the self-test module (used by SharpEmu --selftest).</summary>
    public void RegisterSelfTest()
    {
        Register(new SelfTestModule());
    }

    private void Register(SysModule m)
    {
        m.Load(_table, _log);
        _modules.Add(m);
        _log.Debug("modules", $"module '{m.Name}' registered");
    }

    /// <summary>
    /// Load real module ELF files (sprx/prx) found in a directory. These are
    /// mapped into guest memory and their NID exports would be collected in a full
    /// implementation. For research we map them and let the dynamic-linker-style
    /// resolver fall through to built-in stubs, logging which modules were present.
    /// </summary>
    public void LoadModuleDirectory(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.sprx"))
            _log.Debug("modules", $"found system module file: {System.IO.Path.GetFileName(f)}");
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.prx"))
            _log.Debug("modules", $"found system module file: {System.IO.Path.GetFileName(f)}");
    }

    /// <summary>
    /// Attempt to resolve a NID the guest called. Returns true and sets the handler
    /// if a built-in module exports it; otherwise returns false (caller should
    /// treat as an unhandled import and stub it).
    /// </summary>
    public bool TryResolve(ulong nid, out NidHandler handler, out string name)
        => _table.TryResolve(nid, out handler, out name);
}
