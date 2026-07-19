using System;
using System.IO;
using System.Text;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// libkernel: the core system library providing threads, memory, time, file I/O
/// and the FreeBSD-derived syscall surface. We implement a partial, research-grade
/// subset sufficient to get early boot code and HLE stubs running. Unimplemented
/// calls return a benign error code and are logged for triage.
/// </summary>
public sealed class KernelModule : SysModule
{
    public override string Name => "libkernel";
    public override uint ModuleId => 0x2001;

    protected override void RegisterExports()
    {
        // A handful of commonly-imported NIDs. The literal NID values below are
        // representative placeholders; real games use hashes from the SDK. Because
        // we cannot ship proprietary NID tables, we key our resolver on the NID
        // value the guest actually calls and fall through to a generic stub, which
        // is the honest behavior for an early research emulator.
        Register("sceKernelGetTscFrequency", 0x9B6A17B8u, SceKernelGetTscFrequency);
        Register("sceKernelClockGettime", 0xE2A84C67u, SceKernelClockGettime);
        Register("sceKernelUsleep", 0xB4FCFB4Du, SceKernelUsleep);
        Register("sceKernelGetCurrentThread", 0xECFDBCD7u, SceKernelGetCurrentThread);
        Register("sceKernelAllocate", 0xEBA61C2Eu, SceKernelAllocate);
        Register("sceKernelFree", 0x2E8B1C2Fu, SceKernelFree);
        Register("sceKernelOpen", 0x7B5549Fxu, SceKernelOpen);
        Register("sceKernelClose", 0x4C0AEB5Du, SceKernelClose);
        Register("sceKernelRead", 0x1C62B160u, SceKernelRead);
        Register("sceKernelWrite", 0x8ECB75A3u, SceKernelWrite);
        Register("sceFiberStart", 0xFA6F4A17u, SceFiberStart);
        Register("sceKernelGetSystemTimeWide", 0x996CDCA1u, SceKernelGetSystemTimeWide);
    }

    private void Register(string name, uint nid, NidHandler h)
    {
        Table.Register(NID(nid), name, (ctx, mem) =>
        {
            Log.Trace("kernel", $"call {name}");
            return h(ctx, mem);
        });
    }

    // --- Host filesystem bridge ---
    // For research we expose a sandboxed view rooted at a configurable host
    // directory so a game's file opens can be resolved against a local dump.
    public static string HostRoot { get; set; } = ".";

    private static ulong SceKernelGetTscFrequency(CpuContext ctx, GuestMemory mem)
    {
        // ~3.5 GHz on a base PS5
        return 3_500_000_000UL;
    }

    private static ulong SceKernelClockGettime(CpuContext ctx, GuestMemory mem)
    {
        // clock_gettime: arg0 = clk_id, arg1 = struct timespec*
        ulong tsPtr = ctx.Rsi;
        long ticks = DateTime.Now.Ticks; // 100ns units
        long sec = ticks / 10_000_000;
        long nsec = (ticks % 10_000_000) * 100;
        if (tsPtr != 0)
        {
            mem.WriteUInt64(tsPtr, (ulong)sec);
            mem.WriteUInt64(tsPtr + 8, (ulong)nsec);
        }
        return 0;
    }

    private static ulong SceKernelGetSystemTimeWide(CpuContext ctx, GuestMemory mem)
    {
        return (ulong)(DateTime.Now.Ticks - 621355968000000000L); // 100ns since epoch
    }

    private static ulong SceKernelUsleep(CpuContext ctx, GuestMemory mem)
    {
        uint us = (uint)ctx.Rdi;
        if (us > 0 && us < 1_000_000) System.Threading.Thread.Sleep((int)(us / 1000));
        return 0;
    }

    private static ulong SceKernelGetCurrentThread(CpuContext ctx, GuestMemory mem)
    {
        return 0x1000; // dummy thread handle
    }

    private static ulong SceKernelAllocate(CpuContext ctx, GuestMemory mem)
    {
        ulong size = ctx.Rdi;
        // Allocate inside guest memory from top for simplicity.
        return SimpleHeap.Allocate(size);
    }

    private static ulong SceKernelFree(CpuContext ctx, GuestMemory mem)
    {
        return 0;
    }

    private static ulong SceKernelOpen(CpuContext ctx, GuestMemory mem)
    {
        ulong pathPtr = ctx.Rdi;
        ulong flags = ctx.Rsi;
        string path = ReadString(mem, pathPtr);
        string host = Path.Combine(HostRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(host) || Directory.Exists(host))
            {
                int fd = HostFs.Open(host);
                Log.Debug("kernel", $"open '{path}' -> fd {fd}");
                return (ulong)(uint)fd;
            }
            Log.Warn("kernel", $"open '{path}' -> not found on host (sandbox root '{HostRoot}')");
            return 0xFFFFFFFF; // -1
        }
        catch (Exception ex)
        {
            Log.Warn("kernel", $"open '{path}' error: {ex.Message}");
            return 0xFFFFFFFF;
        }
    }

    private static ulong SceKernelClose(CpuContext ctx, GuestMemory mem)
    {
        int fd = (int)ctx.Rdi;
        HostFs.Close(fd);
        return 0;
    }

    private static ulong SceKernelRead(CpuContext ctx, GuestMemory mem)
    {
        int fd = (int)ctx.Rdi;
        ulong buf = ctx.Rsi;
        ulong count = ctx.Rdx;
        var data = HostFs.Read(fd, (int)count);
        if (data == null) return 0xFFFFFFFF;
        mem.WriteBytes(buf, data, data.Length);
        return (ulong)(uint)data.Length;
    }

    private static ulong SceKernelWrite(CpuContext ctx, GuestMemory mem)
    {
        int fd = (int)ctx.Rdi;
        ulong buf = ctx.Rsi;
        ulong count = ctx.Rdx;
        var data = new byte[count];
        mem.ReadBytes(buf, data, (int)count);
        HostFs.Write(fd, data);
        return count;
    }

    private static ulong SceFiberStart(CpuContext ctx, GuestMemory mem)
    {
        // Fibers are a lightweight scheduling primitive. We just acknowledge.
        return 0;
    }

    public static string ReadString(GuestMemory mem, ulong ptr)
    {
        if (ptr == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < 4096; i++)
        {
            byte b = mem.ReadByte(ptr + (ulong)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Very small bump-pointer heap living in guest memory (used by sceKernelAllocate
/// and similar when the guest expects a guest-virtual address back).
/// </summary>
public static class SimpleHeap
{
    private static ulong _cursor = 0x1800_0000UL;
    private static readonly object _l = new object();
    public static ulong Allocate(ulong size)
    {
        lock (_l)
        {
            size = (size + 0xFFF) & ~0xFFFUL; // page align
            ulong r = _cursor;
            _cursor += size;
            return r;
        }
    }
}

/// <summary>
/// Sandboxed host file descriptor table bridging guest fds to host files.
/// </summary>
public static class HostFs
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, FileStream> _fds = new();
    private static int _next = 3;
    private static readonly object _l = new object();

    public static int Open(string hostPath)
    {
        try
        {
            var fs = new FileStream(hostPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            int fd = System.Threading.Interlocked.Increment(ref _next);
            _fds[fd] = fs;
            return fd;
        }
        catch
        {
            try
            {
                var fs = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int fd = System.Threading.Interlocked.Increment(ref _next);
                _fds[fd] = fs;
                return fd;
            }
            catch { return -1; }
        }
    }
    public static void Close(int fd) { if (_fds.TryRemove(fd, out var fs)) fs.Dispose(); }
    public static byte[] Read(int fd, int count)
    {
        if (!_fds.TryGetValue(fd, out var fs)) return null;
        var buf = new byte[count];
        int n = fs.Read(buf, 0, count);
        if (n == count) return buf;
        var outp = new byte[n];
        Array.Copy(buf, outp, n);
        return outp;
    }
    public static void Write(int fd, byte[] data)
    {
        if (_fds.TryGetValue(fd, out var fs)) fs.Write(data, 0, data.Length);
    }
}
