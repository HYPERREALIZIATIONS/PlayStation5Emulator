using System;
using System.Text;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Libc;

/// <summary>
/// C runtime functions imported by PS5 binaries (memset, memcpy, strlen, printf
/// family, etc.). These are reimplemented in managed code over guest memory.
/// </summary>
public sealed class LibcModule : SysModule
{
    public override string Name => "libc";
    public override uint ModuleId => 0x80000003;

    protected override void RegisterExports()
    {
        Register("memset", 0x733AAF49u, Memset);
        Register("memcpy", 0x29EB0A14u, Memcpy);
        Register("strlen", 0xBB2AF10Eu, Strlen);
        Register("strcmp", 0xECBDA688u, Strcmp);
        Register("printf", 0xCA8B7E60u, Printf);
        Register("sprintf", 0xB2C9C317u, Sprintf);
        Register("malloc", 0x9BDAF08Du, Malloc);
        Register("free", 0x1DDB8E8Du, Free);
    }

    private void Register(string name, uint nid, NidHandler h)
        => Table.Register(NID(nid), name, (ctx, mem) => { Log.Trace("libc", $"{name}"); return h(ctx, mem); });

    private static ulong Memset(CpuContext ctx, GuestMemory mem)
    {
        ulong dst = ctx.Rdi; ulong val = ctx.Rsi & 0xFF; ulong n = ctx.Rdx;
        var b = (byte)val;
        for (ulong i = 0; i < n; i++) mem.WriteByte(dst + i, b);
        return dst;
    }
    private static ulong Memcpy(CpuContext ctx, GuestMemory mem)
    {
        ulong dst = ctx.Rdi; ulong src = ctx.Rsi; ulong n = ctx.Rdx;
        var tmp = new byte[n];
        mem.ReadBytes(src, tmp, (int)n);
        mem.WriteBytes(dst, tmp, (int)n);
        return dst;
    }
    private static ulong Strlen(CpuContext ctx, GuestMemory mem)
    {
        ulong p = ctx.Rdi; ulong len = 0;
        while (mem.ReadByte(p + len) != 0) len++;
        return len;
    }
    private static ulong Strcmp(CpuContext ctx, GuestMemory mem)
    {
        ulong a = ctx.Rdi, b = ctx.Rsi;
        while (true)
        {
            byte ca = mem.ReadByte(a), cb = mem.ReadByte(b);
            if (ca != cb) return ca < cb ? 0xFFFFFFFF : 1;
            if (ca == 0) return 0;
            a++; b++;
        }
    }
    private static ulong Printf(CpuContext ctx, GuestMemory mem)
    {
        // Best-effort: print the format string to the log. Real formatting is
        // out of scope for a research stub.
        string fmt = KernelModule.ReadString(mem, ctx.Rdi);
        EmulatorDiagnostics.Log?.Info("libc", $"printf: {fmt}");
        return (ulong)fmt.Length;
    }
    private static ulong Sprintf(CpuContext ctx, GuestMemory mem)
    {
        string fmt = KernelModule.ReadString(mem, ctx.Rsi);
        mem.WriteBytes(ctx.Rdi, Encoding.UTF8.GetBytes(fmt + "\0"), fmt.Length + 1);
        return (ulong)fmt.Length;
    }
    private static ulong Malloc(CpuContext ctx, GuestMemory mem)
        => KernelModule_GuestAllocate(ctx.Rdi);
    private static ulong Free(CpuContext ctx, GuestMemory mem) => 0;
}

// Small extension to avoid a circular dependency for the guest allocator.
public static class KernelModule_GuestAllocate
{
    public static ulong Allocate(ulong size) => Kernel.SimpleHeap.Allocate(size);
}
