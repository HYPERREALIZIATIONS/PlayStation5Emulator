using System.Runtime.InteropServices;
using Zenith.Core.Logging;

namespace Zenith.Core.Memory;

public sealed unsafe class MemoryManager : IDisposable
{
    private readonly ulong _capacity;
    private byte* _ptr;
    private bool _disposed;

    public MemoryManager(ulong capacity)
    {
        _capacity = capacity;

        _ptr = (byte*)Marshal.AllocHGlobal((nint)capacity);
        if (_ptr == null)
            throw new OutOfMemoryException($"Failed to allocate {capacity} bytes of guest memory");

        new Span<byte>(_ptr, (int)Math.Min(capacity, 64 * 1024 * 1024)).Clear();
        Log.Info($"Guest memory allocated: {capacity / (1024 * 1024)} MB");
    }

    public ulong Capacity => _capacity;

    public void Write(ulong address, ReadOnlySpan<byte> data)
    {
        if (address + (ulong)data.Length > _capacity)
            throw new MemoryAccessViolation(address, data.Length);

        new Span<byte>(_ptr + address, data.Length).CopyTo(data);
    }

    public void Read(ulong address, Span<byte> destination)
    {
        if (address + (ulong)destination.Length > _capacity)
            throw new MemoryAccessViolation(address, destination.Length);

        new ReadOnlySpan<byte>(_ptr + address, destination.Length).CopyTo(destination);
    }

    public ref byte Ref(ulong address)
    {
        if (address >= _capacity)
            throw new MemoryAccessViolation(address, 1);

        return ref _ptr[address];
    }

    public Span<byte> GetSpan(ulong address, int length)
    {
        if (address + (ulong)length > _capacity)
            throw new MemoryAccessViolation(address, length);

        return new Span<byte>(_ptr + address, length);
    }

    public void MapRegion(ulong address, ulong size)
    {
        if (address + size > _capacity)
            throw new ArgumentOutOfRangeException(nameof(address));

        // Mark as mapped by ensuring the span exists (no-op for now)
        new Span<byte>(_ptr + address, (int)size).Clear();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Marshal.FreeHGlobal((nint)_ptr);
            _disposed = true;
        }
    }
}

public sealed class MemoryAccessViolation : Exception
{
    public ulong Address { get; }
    public int Size { get; }

    public MemoryAccessViolation(ulong address, int size)
        : base($"Memory access violation at 0x{address:X} (size={size})")
    {
        Address = address;
        Size = size;
    }
}
