using System;
using System.IO;
using System.Text;

namespace Prospero.Emu.Core
{
    /// <summary>
    /// Little-endian binary reader helpers used throughout the loader and CPU.
    /// PS5 executables are little-endian x86-64, so everything is LE unless noted.
    /// </summary>
    public sealed class LeReader
    {
        private readonly byte[] _data;

        public LeReader(byte[] data) => _data = data;

        public LeReader(ReadOnlySpan<byte> data) => _data = data.ToArray();

        public int Length => _data.Length;

        public byte ReadU8(int off)
        {
            if (off < 0 || off >= _data.Length) throw new IndexOutOfRangeException($"read u8 @ 0x{off:X}");
            return _data[off];
        }

        public ushort ReadU16(int off) => (ushort)(ReadU8(off) | (ReadU8(off + 1) << 8));
        public uint ReadU32(int off) => (uint)(ReadU16(off) | (ReadU16(off + 2) << 16));
        public ulong ReadU64(int off) => ReadU32(off) | ((ulong)ReadU32(off + 4) << 32);

        public short ReadI16(int off) => (short)ReadU16(off);
        public int ReadI32(int off) => (int)ReadU32(off);
        public long ReadI64(int off) => (long)ReadU64(off);

        /// <summary>Read a NUL-terminated ASCII string starting at off.</summary>
        public string ReadCString(int off, int max = 1024)
        {
            var sb = new StringBuilder();
            int i = off;
            while (i < _data.Length && _data[i] != 0 && (i - off) < max)
            {
                sb.Append((char)_data[i]);
                i++;
            }
            return sb.ToString();
        }

        public string ReadAscii(int off, int len)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < len && off + i < _data.Length; i++)
                sb.Append((char)_data[off + i]);
            return sb.ToString();
        }

        public byte[] Slice(int off, int len)
        {
            if (off < 0 || len < 0 || off + len > _data.Length)
                throw new IndexOutOfRangeException($"slice 0x{off:X}..0x{off + len:X}");
            var r = new byte[len];
            Array.Copy(_data, off, r, 0, len);
            return r;
        }
    }
}
