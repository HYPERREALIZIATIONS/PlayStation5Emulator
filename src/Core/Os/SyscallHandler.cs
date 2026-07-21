using System.Runtime.InteropServices;
using Zenith.Core.Logging;
using Zenith.Core.Memory;

namespace Zenith.Core.Os;

public enum Syscall : ulong
{
    Read = 0,
    Write = 4,
    Open = 5,
    Close = 6,
    GetTicks = 72
}

public delegate ulong SyscallFunc(ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6);

public sealed class SyscallHandler
{
    private readonly MemoryManager _memory;
    private readonly Dictionary<ulong, SyscallFunc> _handlers;
    private ulong _nextFd = 3;

    public SyscallHandler(MemoryManager memory)
    {
        _memory = memory;
        _handlers = new()
        {
            [(ulong)Syscall.Write] = HandleWrite,
            [(ulong)Syscall.Read] = HandleRead,
            [(ulong)Syscall.Open] = HandleOpen,
            [(ulong)Syscall.Close] = HandleClose,
            [(ulong)Syscall.GetTicks] = HandleGetTicks
        };
    }

    public ulong Dispatch(ulong num, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
    {
        if (_handlers.TryGetValue(num, out var handler))
            return handler(a1, a2, a3, a4, a5, a6);

        Log.Warn($"Unimplemented syscall 0x{num:X}");
        return 0;
    }

    private unsafe ulong HandleWrite(ulong fd, ulong buf, ulong count, ulong a3, ulong a4, ulong a5)
    {
        try
        {
            var data = new ReadOnlySpan<byte>((byte*)_memory.Ref(buf), (int)count);
            if (fd == 1 || fd == 2)
            {
                var text = System.Text.Encoding.UTF8.GetString(data);
                Console.Write(text);
            }
            return (ulong)data.Length;
        }
        catch
        {
            return 0;
        }
    }

    private unsafe ulong HandleRead(ulong fd, ulong buf, ulong count, ulong a3, ulong a4, ulong a5)
    {
        try
        {
            var span = new Span<byte>((byte*)_memory.Ref(buf), (int)count);
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private unsafe ulong HandleOpen(ulong pathPtr, ulong flags, ulong mode, ulong a3, ulong a4, ulong a5)
    {
        try
        {
            var path = Marshal.PtrToStringUTF8((nint)pathPtr) ?? string.Empty;
            Log.Debug($"Open: {path}");
            return _nextFd++;
        }
        catch
        {
            return unchecked((ulong)(-1));
        }
    }

    private unsafe ulong HandleClose(ulong fd, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
    {
        return 0;
    }

    private ulong HandleGetTicks(ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
    {
        return (ulong)Environment.TickCount64;
    }
}
