using System;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;

namespace SharpEmu.Libs.SelfTest;

/// <summary>
/// A tiny module used only by the built-in self-test (SharpEmu --selftest). It
/// exposes one HLE function with a known NID so the generated sample ELF can call
/// the emulator's HLE dispatch path and verify CPU + kernel wiring works.
/// </summary>
public sealed class SelfTestModule : SysModule
{
    // Marker NID used by the generated sample ELF.
    public const uint SELFTEST_NID = 0x55AA0001u;

    public override string Name => "selftest";
    public override uint ModuleId => 0x55AA;

    protected override void RegisterExports()
    {
        Table.Register(NID(SELFTEST_NID), "selfTestWriteMarker", (ctx, mem) =>
        {
            // rdi = guest address to write the marker at
            ulong addr = ctx.Rdi;
            mem.WriteUInt32(addr, 0x12345678);
            Log.Info("selftest", $"selfTestWriteMarker wrote marker at 0x{addr:X}");
            return 0;
        });
    }
}
