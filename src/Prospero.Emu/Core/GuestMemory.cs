using System;
using System.Collections.Generic;

namespace Prospero.Emu.Core
{
    /// <summary>
    /// A simple sparse guest memory model.
    ///
    /// The PS5 physically addresses up to 16 GiB of unified GDDR6. For a research
    /// emulator we do NOT allocate 16 GiB; instead we lazily allocate 4 KiB pages
    /// on write (copy-on-write style) and allow MMIO regions to be registered for
    /// devices such as the GPU/AGC. This keeps memory usage proportional to what a
    /// game actually touches.
    /// </summary>
    public sealed class GuestMemory
    {
        public const ulong PageSize = 0x1000;
        public const ulong PageMask = PageSize - 1;

        private readonly Dictionary<ulong, byte[]> _pages = new();
        private readonly Dictionary<ulong, IMmioRegion> _mmio = new();

        public ulong TotalBytes { get; }

        public GuestMemory(ulong totalBytes = 4UL * 1024 * 1024 * 1024) => TotalBytes = totalBytes;

        public void MapMmio(ulong baseAddr, ulong size, IMmioRegion region)
        {
            for (ulong a = baseAddr; a < baseAddr + size; a += PageSize)
                _mmio[a & ~PageMask] = region;
        }

        private byte[] GetOrAllocPage(ulong page)
        {
            if (_pages.TryGetValue(page, out var p)) return p;
            p = new byte[PageSize];
            _pages[page] = p;
            return p;
        }

        private byte[]? GetPage(ulong page) => _pages.TryGetValue(page, out var p) ? p : null;

        private bool IsMmio(ulong page) => _mmio.ContainsKey(page & ~PageMask);

        public byte ReadU8(ulong addr)
        {
            ulong page = addr & ~PageMask;
            if (_mmio.TryGetValue(page, out var r))
                return r.ReadU8(addr);
            var p = GetPage(page);
            return p == null ? (byte)0 : p[addr & PageMask];
        }

        public void WriteU8(ulong addr, byte v)
        {
            ulong page = addr & ~PageMask;
            if (_mmio.TryGetValue(page, out var r)) { r.WriteU8(addr, v); return; }
            GetOrAllocPage(page)[addr & PageMask] = v;
        }

        public ushort ReadU16(ulong addr)
        {
            // unaligned access is legal on x86; compose byte-by-byte.
            return (ushort)(ReadU8(addr) | (ReadU8(addr + 1) << 8));
        }
        public void WriteU16(ulong addr, ushort v)
        {
            WriteU8(addr, (byte)v);
            WriteU8(addr + 1, (byte)(v >> 8));
        }
        public uint ReadU32(ulong addr) => (uint)(ReadU16(addr) | (ReadU16(addr + 2) << 16));
        public void WriteU32(ulong addr, uint v)
        {
            WriteU16(addr, (ushort)v);
            WriteU16(addr + 2, (ushort)(v >> 16));
        }
        public ulong ReadU64(ulong addr) => ReadU32(addr) | ((ulong)ReadU32(addr + 4) << 32);
        public void WriteU64(ulong addr, ulong v)
        {
            WriteU32(addr, (uint)v);
            WriteU32(addr + 4, (uint)(v >> 32));
        }

        public void Write(ulong addr, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
                WriteU8(addr++, b);
        }

        public void Read(ulong addr, Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
                data[i] = ReadU8(addr + (ulong)i);
        }

        /// <summary>Returns a snapshot byte[] for a contiguous region (used for dumping/spans).</summary>
        public byte[] ReadBytes(ulong addr, int len)
        {
            var r = new byte[len];
            Read(addr, r);
            return r;
        }

        /// <summary>Alloc (guest side) - just records that the page is committed/mapped.</summary>
        public void Commit(ulong addr, ulong size)
        {
            for (ulong a = addr & ~PageMask; a < addr + size; a += PageSize)
                GetOrAllocPage(a & ~PageMask);
        }

        public IEnumerable<ulong> MappedPages => _pages.Keys;
    }

    /// <summary>
    /// Memory-mapped IO region. Used for the GPU/AGC control registers and
    /// registers the CPU can poke to drive the early graphics pipeline.
    /// </summary>
    public interface IMmioRegion
    {
        byte ReadU8(ulong addr);
        void WriteU8(ulong addr, byte v);
    }
}
