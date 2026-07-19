using System;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Graphics;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// libSceVideoOut: the PS5 video output service. Games open a video port,
/// register flip buffers, and submit flips. We capture the buffer registration
/// and flip so the AGC/Vulkan pipeline can present an early frame.
/// </summary>
public sealed class VideoOutModule : SysModule
{
    public override string Name => "libSceVideoOut";
    public override uint ModuleId => 0x80000011;

    // Shared handle to the graphics subsystem for presenting frames.
    public static IVideoPresenter Presenter;

    protected override void RegisterExports()
    {
        Register("sceVideoOutOpen", 0x9F641EC5u, Open);
        Register("sceVideoOutClose", 0xE5717D5Bu, Close);
        Register("sceVideoOutRegisterBuffers", 0x6ED895B2u, RegisterBuffers);
        Register("sceVideoOutSubmitFlip", 0xB4DB6A34u, SubmitFlip);
        Register("sceVideoOutGetFlipStatus", 0x0D73176Eu, GetFlipStatus);
        Register("sceVideoOutSetBufferAttribute", 0x1B9821B1u, SetBufferAttribute);
    }

    private void Register(string name, uint nid, NidHandler h)
        => Table.Register(NID(nid), name, (ctx, mem) => { Log.Trace("videoout", $"{name}"); return h(ctx, mem); });

    private static ulong Open(CpuContext ctx, GuestMemory mem)
    {
        // rdi=userHandle, rsi=type, rdx=index, rcx=mode*, r8=param*
        Log.Info("videoout", "sceVideoOutOpen (port opened)");
        return 1; // handle
    }
    private static ulong Close(CpuContext ctx, GuestMemory mem) => 0;

    private static ulong RegisterBuffers(CpuContext ctx, GuestMemory mem)
    {
        // rdi=handle, rsi=startIndex, rdx=num, rcx=attribute*, r8=pMem*, r9=elements*
        uint num = (uint)ctx.Rdx;
        ulong pMem = ctx.R8;
        Log.Info("videoout", $"sceVideoOutRegisterBuffers num={num} pMem=0x{pMem:X}");
        Presenter?.RegisterBuffers(pMem, num, mem);
        return 0;
    }

    private static ulong SubmitFlip(CpuContext ctx, GuestMemory mem)
    {
        uint handle = (uint)ctx.Rdi;
        uint bufferIndex = (uint)ctx.Rsi;
        ulong flipArg = ctx.Rdx;
        Log.Info("videoout", $"sceVideoOutSubmitFlip bufferIndex={bufferIndex}");
        Presenter?.Present(bufferIndex);
        return 0;
    }

    private static ulong GetFlipStatus(CpuContext ctx, GuestMemory mem)
    {
        ulong statusPtr = ctx.Rsi;
        if (statusPtr != 0)
        {
            mem.WriteUInt64(statusPtr, 1);        // flip pending
            mem.WriteUInt64(statusPtr + 8, 0);    // flip arg
            mem.WriteUInt64(statusPtr + 16, 1);   // count
        }
        return 0;
    }

    private static ulong SetBufferAttribute(CpuContext ctx, GuestMemory mem)
    {
        // rdi=attr*, rsi=format, rdx=tilingMode, rcx=aspectRatio, r8=pixelSize, r9=width, stack=height
        uint width = (uint)ctx.R9;
        Log.Debug("videoout", $"sceVideoOutSetBufferAttribute width={width}");
        return 0;
    }
}
