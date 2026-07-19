using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs;

/// <summary>
/// A NID (64-bit import identifier) is how PS4/PS5 binaries reference imported
/// functions from system modules. The dynamic linker resolves a NID to an
/// address. We instead resolve a NID to a managed HLE handler.
///
/// Handlers receive the guest CPU context and memory so they can read arguments
/// from registers, write return values, and touch guest memory. They return the
/// value that should be placed in RAX (or 0 if they set it themselves).
/// </summary>
public delegate ulong NidHandler(Cpu.CpuContext ctx, Memory.GuestMemory mem);

public sealed class NidTable
{
    private readonly Dictionary<ulong, NidHandler> _handlers = new();
    private readonly Dictionary<ulong, string> _names = new();

    public void Register(ulong nid, string name, NidHandler handler)
    {
        _handlers[nid] = handler;
        _names[nid] = name;
    }

    public bool TryResolve(ulong nid, out NidHandler handler, out string name)
    {
        if (_handlers.TryGetValue(nid, out handler))
        {
            name = _names[nid];
            return true;
        }
        name = $"nid_0x{nid:X16}";
        return false;
    }

    public string NameOf(ulong nid) => _names.TryGetValue(nid, out var n) ? n : $"0x{nid:X16}";
}

/// <summary>
/// Base class for a system module (prx / sys_module). Each concrete module
/// registers its NID handlers into the shared NidTable at load time.
/// </summary>
public abstract class SysModule
{
    public abstract string Name { get; }
    public virtual uint ModuleId => 0;
    protected NidTable Table;
    protected Logger Log;

    public void Load(NidTable table, Logger log)
    {
        Table = table;
        Log = log;
        RegisterExports();
    }

    protected abstract void RegisterExports();

    // Helper to compute the standard library NID from a 32-bit hash commonly
    // seen in PS4/PS5 imports. We store NIDs as the 64-bit value used by the
    // guest; callers pass the literal value.
    protected static ulong NID(uint low) => (ulong)low;
}
