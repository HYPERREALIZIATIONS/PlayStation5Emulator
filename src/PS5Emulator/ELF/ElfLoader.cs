using System.Buffers.Binary;
using System.IO;
using PS5Emulator.Logging;
using PS5Emulator.Memory;
using PS5Emulator.Models;

namespace PS5Emulator.ELF;

public static class ElfLoader
{
    private const byte ELFMAG0 = 0x7f;
    private const byte ELFMAG1 = (byte)'E';
    private const byte ELFMAG2 = (byte)'L';
    private const byte ELFMAG3 = (byte)'F';

    public static GameInfo Load(string path, MemoryManager memory)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // e_ident: 16 bytes
        var e_ident = reader.ReadBytes(16);
        if (e_ident[0] != ELFMAG0 || e_ident[1] != ELFMAG1 || e_ident[2] != ELFMAG2 || e_ident[3] != ELFMAG3)
            throw new InvalidDataException("Not a valid ELF file (bad magic).");

        var elfClass = e_ident[4]; // 1=32, 2=64
        var elfData = e_ident[5]; // 1=LE, 2=BE
        if (elfClass != 2) throw new NotSupportedException("Only ELF64 is supported by this research build.");
        if (elfData != 1) throw new NotSupportedException("Only little-endian ELF is supported.");

        var e_type = reader.ReadUInt16();
        var e_machine = reader.ReadUInt16();
        var e_version = reader.ReadUInt32();
        var e_entry = reader.ReadUInt64();
        var e_phoff = reader.ReadUInt64();
        var e_shoff = reader.ReadUInt64();
        var e_flags = reader.ReadUInt32();
        var e_ehsize = reader.ReadUInt16();
        var e_phentsize = reader.ReadUInt16();
        var e_phnum = reader.ReadUInt16();
        var e_shentsize = reader.ReadUInt16();
        var e_shnum = reader.ReadUInt16();
        var e_shstrndx = reader.ReadUInt16();

        var info = new GameInfo
        {
            Is64Bit = elfClass == 2,
            EntryPoint = (long)e_entry,
            ImageBase = 0
        };

        if (e_machine != 0x3E) // EM_X86_64
        {
            Logger.Warn("ELF", $"Machine type 0x{e_machine:X4} is not X86_64 (0x3E). Compatibility is not guaranteed.");
        }

        Logger.Info("ELF", $"Type=0x{e_type:X4}, Machine=0x{e_machine:X4}, Entry=0x{e_entry:X16}, Segments={e_phnum}");

        // Read program headers
        if (e_phentsize < 56)
            throw new InvalidDataException($"Unexpected program header size: {e_phentsize} (expected >= 56 for ELF64).");

        stream.Seek((long)e_phoff, SeekOrigin.Begin);
        for (var i = 0; i < e_phnum; i++)
        {
            var p_type = reader.ReadUInt32();
            var p_flags = reader.ReadUInt32();
            var p_offset = reader.ReadUInt64();
            var p_vaddr = reader.ReadUInt64();
            var p_paddr = reader.ReadUInt64();
            var p_filesz = reader.ReadUInt64();
            var p_memsz = reader.ReadUInt64();
            var p_align = reader.ReadUInt64();

            Logger.Debug("ELF", $"PHDR {i}: type=0x{p_type:X8}, vaddr=0x{p_vaddr:X16}, offset=0x{p_offset:X16}, filesz={p_filesz}, memsz={p_memsz}, flags=0x{p_flags:X8}");

            if (p_type == 1) // PT_LOAD
            {
                LoadSegment(stream, memory, (long)p_offset, (long)p_vaddr, (int)p_filesz, (int)p_memsz);
                if (info.ImageBase == 0) info.ImageBase = (long)p_vaddr;
            }
            else if (p_type == 4) // PT_NOTE
            {
                if (p_filesz > 0 && p_filesz < 4 * 1024 * 1024)
                {
                    try { ReadNotes(stream, (long)p_offset, (int)p_filesz, info); }
                    catch { /* best-effort */ }
                }
            }
        }

        return info;
    }

    private static void LoadSegment(Stream stream, MemoryManager memory, long offset, long vaddr, int fileSize, int memSize)
    {
        var pid = Thread.CurrentThread.ManagedThreadId;
        Logger.Debug("ELF", $"Loading segment: offset=0x{offset:X16}, vaddr=0x{vaddr:X16}, fileSize={fileSize}, memSize={memSize}");

        var oldPos = stream.Position;
        try
        {
            if (vaddr + memSize > memory.Size)
            {
                Logger.Warn("ELF", $"Segment 0x{vaddr:X16} length {memSize} exceeds available RAM. Skipping.");
                return;
            }

            var buf = new byte[fileSize];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.Read(buf, 0, fileSize);

            memory.WriteBytes((ulong)vaddr, buf, 0, fileSize);
            if (memSize > fileSize)
            {
                var zeros = new byte[memSize - fileSize];
                memory.WriteBytes((ulong)(vaddr + fileSize), zeros, 0, zeros.Length);
            }
        }
        finally
        {
            stream.Seek(oldPos, SeekOrigin.Begin);
        }
    }

    private static void ReadNotes(Stream stream, long offset, int size, GameInfo info)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var reader = new BinaryReader(stream);
        var start = stream.Position;
        while (stream.Position < start + size)
        {
            var namesz = reader.ReadInt32();
            var descsz = reader.ReadInt32();
            var nameType = reader.ReadInt32();

            if (namesz < 0 || descsz < 0 || namesz > 64 || descsz > 4096)
            {
                Logger.Warn("ELF", "Malformed note entry, stopping note parsing.");
                break;
            }

            var nameBytes = reader.ReadBytes(namesz);
            // Pad to 4-byte boundary
            while ((stream.Position - (start + 12 + namesz)) % 4 != 0)
            {
                if (stream.ReadByte() == -1) break;
            }

            var nameStr = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            Logger.Debug("ELF", $"Note: name='{nameStr}', type={nameType}, descsz={descsz}");

            if (descsz > 0)
            {
                var descBytes = reader.ReadBytes(descsz);
                while ((stream.Position % 4) != 0)
                {
                    if (stream.ReadByte() == -1) break;
                }

                // Known notes: "SCE", "PS5", app version strings, etc.
                var descText = System.Text.Encoding.ASCII.GetString(descBytes);
                if (info.Title == null && !string.IsNullOrWhiteSpace(descText) && descText.Length >= 3 && descText.Length <= 128)
                {
                    var clean = descText.Split('\0')[0].Trim();
                    if (clean.Length >= 3) info.Title = clean;
                }
            }
        }
    }
}
