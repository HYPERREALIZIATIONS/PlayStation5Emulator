using System;
using System.Collections.Generic;

namespace SharpEmu.Core.Memory;

/// <summary>
/// Sparse, page-based guest process address space. Real PS5 processes use a large
/// 64-bit virtual address space with ASLR and fine-grained protection. Rather than
/// allocating the whole space up front, we back it with on-demand 4 KiB pages so a
/// few touched pages use a few KB of host RAM while any guest virtual address
/// (including low addresses used by ET_EXEC and the stack, and high addresses used
/// by PIE modules) is valid.
///
/// The null page (address 0) is special-cased: reads return zero and writes throw,
/// which matches typical "null dereference" behavior and helps catch bad pointers
/// during early research.
/// </summary>
public sealed class GuestMemory
{
    private const ulong PAGE_SHIFT = 12;
    private const ulong PAGE_SIZE = 1UL << (int)PAGE_SHIFT;
    private const ulong PAGE_MASK = PAGE_SIZE - 1;

    private readonly Dictionary<ulong, byte[]> _pages = new();
    private readonly object _lock = new object();

    // Bookkeeping of mapped segments for diagnostics.
    public class Region
    {
        public ulong Start;
        public ulong End;
        public uint Flags;
        public string Name;
    }
    private readonly List<Region> _regions = new List<Region>();

    public ulong BaseAddress => 0;
    public ulong Size => ulong.MaxValue; // sparse: entire 64-bit space is addressable
    public IReadOnlyList<Region> Regions => _regions;

    private byte[] GetPage(ulong addr, bool create)
    {
        ulong idx = addr >> (int)PAGE_SHIFT;
        lock (_lock)
        {
            if (_pages.TryGetValue(idx, out var p)) return p;
            if (!create) return null;
            p = new byte[PAGE_SIZE];
            _pages[idx] = p;
            return p;
        }
    }

    public void Map(ulong vaddr, ulong length, uint flags, string name)
    {
        lock (_lock)
            _regions.Add(new Region { Start = vaddr, End = vaddr + length, Flags = flags, Name = name });
    }

    public void Unmap(ulong vaddr, ulong length)
    {
        lock (_lock)
            _regions.RemoveAll(r => r.Start == vaddr && (r.End - r.Start) == length);
    }

    public bool TryGetHostPointer(ulong vaddr, out int offset)
    {
        // Sparse model has no single host pointer; report unsupported.
        offset = 0;
        return false;
    }

    public byte ReadByte(ulong addr)
    {
        if (addr == 0) return 0;
        var p = GetPage(addr, false);
        if (p == null) return 0;
        return p[addr & PAGE_MASK];
    }

    public void WriteByte(ulong addr, byte value)
    {
        if (addr == 0) throw new AccessViolationException("write to null page");
        var p = GetPage(addr, true);
        p[addr & PAGE_MASK] = value;
    }

    public void ReadBytes(ulong addr, byte[] buffer, int count)
    {
        if (addr == 0 && count > 0) { Array.Clear(buffer, 0, count); return; }
        for (int i = 0; i < count; i++)
            buffer[i] = ReadByte(addr + (ulong)i);
    }

    public void WriteBytes(ulong addr, byte[] buffer, int count)
    {
        if (addr == 0) throw new AccessViolationException("write to null page");
        for (int i = 0; i < count; i++)
            WriteByte(addr + (ulong)i, buffer[i]);
    }

    public ushort ReadUInt16(ulong addr)
    {
        if (addr == 0) return 0;
        return (ushort)(ReadByte(addr) | ((uint)ReadByte(addr + 1) << 8));
    }

    public void WriteUInt16(ulong addr, ushort value)
    {
        if (addr == 0) throw new AccessViolationException("write to null page");
        WriteByte(addr, (byte)value);
        WriteByte(addr + 1, (byte)(value >> 8));
    }

    public uint ReadUInt32(ulong addr)
    {
        if (addr == 0) return 0;
        return (uint)(ReadByte(addr) | ((uint)ReadByte(addr + 1) << 8) |
                      ((uint)ReadByte(addr + 2) << 16) | ((uint)ReadByte(addr + 3) << 24));
    }

    public void WriteUInt32(ulong addr, uint value)
    {
        if (addr == 0) throw new AccessViolationException("write to null page");
        WriteByte(addr, (byte)value);
        WriteByte(addr + 1, (byte)(value >> 8));
        WriteByte(addr + 2, (byte)(value >> 16));
        WriteByte(addr + 3, (byte)(value >> 24));
    }

    public ulong ReadUInt64(ulong addr)
    {
        if (addr == 0) return 0;
        ulong v = 0;
        for (int i = 0; i < 8; i++)
            v |= (ulong)ReadByte(addr + (ulong)i) << (8 * i);
        return v;
    }

    public void WriteUInt64(ulong addr, ulong value)
    {
        if (addr == 0) throw new AccessViolationException("write to null page");
        for (int i = 0; i < 8; i++)
            WriteByte(addr + (ulong)i, (byte)(value >> (8 * i)));
    }

    /// <summary>Copy memory between guest regions (handles overlap like memcpy).</summary>
    public void Copy(ulong dst, ulong src, ulong length)
    {
        if (length == 0) return;
        if (dst == 0 || src == 0) throw new AccessViolationException("memcpy to/from null page");
        // Read into a temp buffer first to handle overlap correctly.
        var tmp = new byte[length];
        ReadBytes(src, tmp, (int)length);
        WriteBytes(dst, tmp, (int)length);
    }
}
