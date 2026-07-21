using System.IO.Compression;
using Zenith.Core.Logging;
using Zenith.Core.Memory;
using Zenith.Core.Models;

namespace Zenith.Core.Loader;

public sealed class SelfLoader
{
    private readonly MemoryManager _memory;
    private readonly SyscallHandler _syscallHandler;
    private GameInfo? _gameInfo;

    public SelfLoader(MemoryManager memory, SyscallHandler syscallHandler)
    {
        _memory = memory;
        _syscallHandler = syscallHandler;
    }

    public async ValueTask<(GameInfo info, ulong entryPoint, ulong stackTop)> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        Span<byte> headerBytes = stackalloc byte[64];
        stream.ReadExactly(headerBytes);
        var header = ElfReader.ReadHeader(headerBytes);

        if (header.Magic != ElfConstants.ELF_MAGIC)
            throw new BadImageFormatException("Not an ELF/SELF file");

        bool isSelf = false;
        if (stream.Length > header.HeaderSize && reader.ReadUInt32() == 0x0046454C) // "SELF"
        {
            isSelf = true;
            Log.Info("SELF wrapper detected");
            await SkipSelfHeader(stream, reader, ct);
        }

        var elfsize = stream.Length - stream.Position;
        var elfData = new byte[elfsize];
        await stream.ReadExactlyAsync(elfData, ct);
        var elf = new ElfReader(elfData);

        var info = new GameInfo
        {
            TitleId = "PPSA00000",
            Title = Path.GetFileNameWithoutExtension(path),
            Version = "01.00",
            Path = path,
            IsElf = header.Magic == ElfConstants.ELF_MAGIC && !isSelf,
            IsSelf = isSelf
        };

        foreach (var phdr in elf.ProgramHeaders)
        {
            if (!phdr.IsLoad || phdr.FileSize == 0)
                continue;

            if (phdr.Vaddr + phdr.MemSize > _memory.Capacity)
                throw new InvalidDataException($"Segment 0x{phdr.Vaddr:X} exceeds guest memory capacity");

            elfData.AsSpan((int)phdr.Offset, (int)phdr.FileSize).CopyTo(_memory.GetSpan((ulong)phdr.Vaddr, (int)phdr.FileSize));
            if (phdr.MemSize > phdr.FileSize)
                _memory.GetSpan((ulong)(phdr.Vaddr + phdr.FileSize), (int)(phdr.MemSize - phdr.FileSize)).Clear();

            Log.Debug($"Mapped segment: 0x{phdr.Vaddr:X} len=0x{phdr.FileSize:X}");
        }

        var entry = (ulong)elf.Header.Entry;
        var stackTop = 0x00007FFFFFFFFFFFUL;

        _gameInfo = info;
        Log.Info($"Loaded: {info.Title} ({info.TitleId}) Entry=0x{entry:X} Type={(isSelf ? "SELF" : "ELF")}");
        return (info, entry, stackTop);
    }

    private async ValueTask SkipSelfHeader(Stream stream, BinaryReader reader, CancellationToken ct)
    {
        var flags = reader.ReadUInt64();
        var offset = reader.ReadUInt64();
        var size = reader.ReadUInt64();
        var segmentCount = reader.ReadInt32();

        for (int i = 0; i < segmentCount; i++)
        {
            var segFlags = reader.ReadUInt64();
            var segOffset = reader.ReadUInt64();
            var segSize = reader.ReadUInt64();
            reader.BaseStream.Position = segOffset;
            break;
        }
    }
}
