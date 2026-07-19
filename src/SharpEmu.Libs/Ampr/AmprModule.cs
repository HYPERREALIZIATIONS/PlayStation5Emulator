using System;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;

namespace SharpEmu.Libs.Ampr;

/// <summary>
/// libSceAmpr (AMPR): low-level resource management used by some games for
/// memory pool / PAK-style sequential asset reads. We implement lightweight
/// stubs that acknowledge allocations so boot-time setup completes.
/// </summary>
public sealed class AmprModule : SysModule
{
    public override string Name => "libSceAmpr";
    public override uint ModuleId => 0x00A00000;

    protected override void RegisterExports()
    {
        Register("sceAmprPoolAllocate", 0xD9C2E3A1u, PoolAllocate);
        Register("sceAmprPoolFree", 0x1F4B8C72u, PoolFree);
        Register("sceAmprMemoryGetBaseAddress", 0x8E71AB20u, MemoryGetBase);
        Register("sceAmprRegisterPak", 0x6C39F0D4u, RegisterPak);
    }

    private void Register(string name, uint nid, NidHandler h)
        => Table.Register(NID(nid), name, (ctx, mem) => { Log.Trace("ampr", $"{name}"); return h(ctx, mem); });

    private static ulong PoolAllocate(CpuContext ctx, GuestMemory mem)
    {
        ulong size = ctx.Rdi;
        return Kernel.SimpleHeap.Allocate(size);
    }
    private static ulong PoolFree(CpuContext ctx, GuestMemory mem) => 0;
    private static ulong MemoryGetBase(CpuContext ctx, GuestMemory mem)
    {
        if (ctx.Rdi != 0) mem.WriteUInt64(ctx.Rdi, Kernel.SimpleHeap.Allocate(0x1000));
        return 0;
    }
    private static ulong RegisterPak(CpuContext ctx, GuestMemory mem) => 0;
}
