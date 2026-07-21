using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PS5Emulator.Logging;

namespace PS5Emulator.Memory;

public class MemoryManager
{
    private readonly byte[] _ram;
    public const int PageSize = 4096;

    public ulong Size { get; }
    public const ulong DefaultSize = 8UL * 1024 * 1024 * 1024;

    public MemoryManager(ulong size)
    {
        if (size < 256 * 1024 * 1024) size = 256 * 1024 * 1024;
        if (size > 32UL * 1024 * 1024 * 1024) size = 32UL * 1024 * 1024 * 1024;
        _ram = new byte[size];
        Size = (ulong)_ram.Length;
        Logger.Info("Memory", $"Allocated {Size / (1024 * 1024)}MB of system RAM");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckBounds(ulong address, int length)
    {
        if (address >= Size || (ulong)length > Size - address)
            throw new AccessViolationException($"Memory access out of range: 0x{address:X16}, length={length}");
    }

    public Span<byte> GetSpan(ulong address, int length)
    {
        CheckBounds(address, length);
        return new Span<byte>(_ram, (int)address, length);
    }

    public int ReadInt32(ulong address)
    {
        var span = GetSpan(address, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    public uint ReadUInt32(ulong address)
    {
        var span = GetSpan(address, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    public long ReadInt64(ulong address)
    {
        var span = GetSpan(address, 8);
        return BinaryPrimitives.ReadInt64LittleEndian(span);
    }

    public ulong ReadUInt64(ulong address)
    {
        var span = GetSpan(address, 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(span);
    }

    public byte ReadUInt8(ulong address)
    {
        return GetSpan(address, 1)[0];
    }

    public ushort ReadUInt16(ulong address)
    {
        var span = GetSpan(address, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    public void WriteInt32(ulong address, int value)
    {
        var span = GetSpan(address, 4);
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
    }

    public void WriteInt64(ulong address, long value)
    {
        var span = GetSpan(address, 8);
        BinaryPrimitives.WriteInt64LittleEndian(span, value);
    }

    public void WriteUInt8(ulong address, byte value)
    {
        GetSpan(address, 1)[0] = value;
    }

    public void WriteUInt16(ulong address, ushort value)
    {
        var span = GetSpan(address, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
    }

    public void WriteUInt32(ulong address, uint value)
    {
        var span = GetSpan(address, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
    }

    public void WriteUInt64(ulong address, ulong value)
    {
        var span = GetSpan(address, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
    }

    public void ReadBytes(ulong address, byte[] buffer, int offset, int length)
    {
        buffer.AsSpan(offset, length).CopyTo(GetSpan(address, length));
    }

    public void WriteBytes(ulong address, byte[] buffer, int offset, int length)
    {
        GetSpan(address, length).CopyTo(buffer.AsSpan(offset, length));
    }
}
