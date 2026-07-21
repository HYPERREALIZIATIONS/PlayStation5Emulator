using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Zenith.Core.Logging;
using Zenith.Core.Models;

namespace Zenith.Core.Loader;

public static class ElfConstants
{
    public const uint ELF_MAGIC = 0x464C457F; // 0x7F 'E' 'L' 'F' little-endian
    public const ushort ELF_CLASS_64 = 2;
    public const ushort ELF_DATA_LSB = 1;
    public const ushort ELF_TYPE_EXEC = 2;
    public const ushort EM_X86_64 = 62;
}

public readonly ref struct ElfHeader
{
    public readonly uint Magic;
    public readonly byte Class;
    public readonly byte Data;
    public readonly byte Version;
    public readonly byte OsAbi;
    public readonly ulong Pad;
    public readonly ushort Type;
    public readonly ushort Machine;
    public readonly uint Version32;
    public readonly ulong Entry;
    public readonly ulong PhdrOffset;
    public readonly ulong ShdrOffset;
    public readonly ulong Flags;
    public readonly ushort HeaderSize;
    public readonly ushort PhdrEntrySize;
    public readonly ushort PhdrCount;
    public readonly ushort ShdrEntrySize;
    public readonly ushort ShdrCount;
    public readonly ushort SectionNameIndex;

    public bool IsValid => Magic == ElfConstants.ELF_MAGIC
        && Class == ElfConstants.ELF_CLASS_64
        && Data == ElfConstants.ELF_DATA_LSB
        && Machine == ElfConstants.EM_X86_64;
}

public readonly ref struct ProgramHeader
{
    public readonly uint Type;
    public readonly uint Flags;
    public readonly ulong Offset;
    public readonly ulong Vaddr;
    public readonly ulong Paddr;
    public readonly ulong FileSize;
    public readonly ulong MemSize;
    public readonly ulong Align;

    public bool IsLoad => Type == 1;
}

public sealed class ElfReader
{
    private readonly byte[] _data;
    private readonly ElfHeader _header;
    private readonly ProgramHeader[] _programHeaders;

    public ElfReader(byte[] data)
    {
        _data = data;
        _header = ReadHeader(data);
        if (!_header.IsValid)
            throw new BadImageFormatException("Invalid ELF64 header");

        _programHeaders = ReadProgramHeaders(data, _header);
    }

    public static ElfHeader ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64)
            throw new BadImageFormatException("Data too small for ELF header");

        return new ElfHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(data),
            Class = data[4],
            Data = data[5],
            Version = data[6],
            OsAbi = data[7],
            Pad = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8)),
            Type = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(16)),
            Machine = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(18)),
            Version32 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
            Entry = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(24)),
            PhdrOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(32)),
            ShdrOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(40)),
            Flags = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(48)),
            HeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(56)),
            PhdrEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(58)),
            PhdrCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(60)),
            ShdrEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(62)),
            ShdrCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(64)),
            SectionNameIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(66))
        };
    }

    private static ProgramHeader[] ReadProgramHeaders(ReadOnlySpan<byte> data, ElfHeader header)
    {
        var phdrs = new ProgramHeader[header.PhdrCount];
        var offset = (int)header.PhdrOffset;

        for (var i = 0; i < header.PhdrCount; i++)
        {
            var pos = offset + i * header.PhdrEntrySize;
            if (pos + 56 > data.Length)
                break;

            phdrs[i] = new ProgramHeader
            {
                Type = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4)),
                Offset = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 8)),
                Vaddr = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 16)),
                Paddr = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 24)),
                FileSize = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 32)),
                MemSize = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 40)),
                Align = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 48))
            };
        }

        return phdrs;
    }

    public ElfHeader Header => _header;
    public ReadOnlySpan<byte> Data => _data;
    public IReadOnlyList<ProgramHeader> ProgramHeaders => _programHeaders;
}
