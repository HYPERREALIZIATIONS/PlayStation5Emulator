using System;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;

namespace SharpEmu.Libs.Fiber;

/// <summary>
/// libSceFiber: a lightweight cooperative scheduling primitive used by some PS5
/// games for job/task dispatch. We model fibers as logical handles; running a
/// fiber simply returns success so early boot code that sets them up can proceed.
/// </summary>
public sealed class FiberModule : SysModule
{
    public override string Name => "libSceFiber";
    public override uint ModuleId => 0x009A0000;

    protected override void RegisterExports()
    {
        Register("sceFiberInitialize", 0xC0D47123u, Initialize);
        Register("sceFiberStart", 0xFA6F4A17u, Start);
        Register("sceFiberRun", 0xAB8A27B8u, Run);
        Register("sceFiberSwitch", 0x7A5C5E01u, Switch);
        Register("sceFiberFinalize", 0xB6A38661u, Finalize);
        Register("sceFiberGetSelf", 0xEA5D27C5u, GetSelf);
    }

    private void Register(string name, uint nid, NidHandler h)
        => Table.Register(NID(nid), name, (ctx, mem) => { Log.Trace("fiber", $"{name}"); return h(ctx, mem); });

    private static ulong _nextFiber = 0x5000;

    private static ulong Initialize(CpuContext ctx, GuestMemory mem)
    {
        // rdi = fiber*, rsi = name*, rdx = entry, rcx = arg, r8 = stack, r9 = stackSize
        ulong handle = System.Threading.Interlocked.Increment(ref _nextFiber);
        if (ctx.Rdi != 0) mem.WriteUInt64(ctx.Rdi, handle);
        return 0;
    }
    private static ulong Start(CpuContext ctx, GuestMemory mem) => 0;
    private static ulong Run(CpuContext ctx, GuestMemory mem) => 0;
    private static ulong Switch(CpuContext ctx, GuestMemory mem) => 0;
    private static ulong Finalize(CpuContext ctx, GuestMemory mem) => 0;
    private static ulong GetSelf(CpuContext ctx, GuestMemory mem) => _nextFiber;
}
