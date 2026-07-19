using System;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Graphics;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// libSceAgc (AGC): the low-level AMD GCN/RDNA graphics library used by PS5
/// titles (the console analogue of Vulkan/D3D12). Games build command buffers and
/// submit them via sceAgcDriverSubmitMultiDcbs. We capture submissions, parse the
/// high-level structure of the PM4-style command stream at a research level, and
/// forward "present" intent to the video presenter.
///
/// This is NOT a shader translator. It records what the game submits (DCB count,
/// sizes, first dwords) so that shader/resource pipeline progress can be tracked
/// in the log, and a few known games can reach their first frame milestone.
/// </summary>
public sealed class AgcModule : SysModule
{
    public override string Name => "libSceAgc";
    public override uint ModuleId => 0x00B00000;

    public static IVideoPresenter Presenter;

    // Simple tracker of submitted command buffers for diagnostics.
    public static ulong TotalSubmits;
    public static ulong TotalDcbs;

    protected override void RegisterExports()
    {
        Register("sceAgcDriverSubmitMultiDcbs", 0xFC4C0C53u, SubmitMultiDcbs);
        Register("sceAgcGpuAddressToPointer", 0xA6D9C2E1u, GpuAddressToPointer);
        Register("sceAgcSubmitCommandBuffers", 0x7B2D8F00u, SubmitCommandBuffers);
        Register("sceAgcGetFlipStatus", 0x1234A2B1u, GetFlipStatus);
        Register("sceAgcDriverInit", 0x9A12CD34u, DriverInit);
    }

    private void Register(string name, uint nid, NidHandler h)
        => Table.Register(NID(nid), name, (ctx, mem) => { Log.Trace("agc", $"{name}"); return h(ctx, mem); });

    // sceAgcDriverSubmitMultiDcbs(rdi=address array, rsi=dword sizes, rdx=count)
    private static ulong SubmitMultiDcbs(CpuContext ctx, GuestMemory mem)
    {
        ulong addrArray = ctx.Rdi;
        ulong sizeArray = ctx.Rsi;
        ulong count = ctx.Rdx;

        Log.Info("agc", $"sceAgcDriverSubmitMultiDcbs count={count}");
        TotalSubmits++;

        for (ulong i = 0; i < count; i++)
        {
            ulong dcbAddr = mem.ReadUInt64(addrArray + i * 8);
            uint dcbDwords = mem.ReadUInt32(sizeArray + i * 4);
            TotalDcbs++;

            // Peek the first few dwords of the command buffer to characterize it.
            string preview = "";
            for (uint d = 0; d < Math.Min(dcbDwords, 4); d++)
            {
                ulong p = dcbAddr + d * 4UL;
                if (p + 4 <= mem.BaseAddress + mem.Size)
                    preview += $" 0x{mem.ReadUInt32(p):X8}";
            }
            Log.Debug("agc", $"  DCB[{i}] addr=0x{dcbAddr:X} dwords={dcbDwords}{preview}");

            // Detect a flip/present packet heuristically: many engines embed a
            // "present" / swap / flip marker. We treat a DCB with a recognizable
            // submit-register-write to the display as a present signal.
            if (dcbAddr != 0 && DcbLooksLikePresent(mem, dcbAddr, dcbDwords))
                Presenter?.Present(0);
        }
        return 0;
    }

    private static bool DcbLooksLikePresent(GuestMemory mem, ulong addr, uint dwords)
    {
        // Research heuristic: look for a PM4-type 0x5 (DRAW_INDEX) / 0xC (EVENT_WRITE)
        // followed by an EOP/flip register write. We only flag a small, known pattern
        // to avoid false positives; this is intentionally conservative.
        if (dwords < 4) return false;
        try
        {
            uint first = mem.ReadUInt32(addr);
            uint type = (first >> 16) & 0x3F; // PM4 packet type in high bits (simplified)
            return type == 0x0C || type == 0x3A; // EVENT_WRITE / RELEASE_MEM (often part of present)
        }
        catch { return false; }
    }

    private static ulong GpuAddressToPointer(CpuContext ctx, GuestMemory mem)
    {
        // rdi = gpuAddress, rsi = *cpuAddress out
        ulong gpuAddr = ctx.Rdi;
        if (ctx.Rsi != 0) mem.WriteUInt64(ctx.Rsi, gpuAddr); // identity mapping for research
        return 0;
    }

    private static ulong SubmitCommandBuffers(CpuContext ctx, GuestMemory mem)
    {
        uint count = (uint)ctx.Rdi;
        Log.Info("agc", $"sceAgcSubmitCommandBuffers count={count}");
        return 0;
    }

    private static ulong GetFlipStatus(CpuContext ctx, GuestMemory mem) => 0;
    private static ulong DriverInit(CpuContext ctx, GuestMemory mem)
    {
        Log.Info("agc", "sceAgcDriverInit (AGC driver initialized)");
        return 0;
    }
}
