using System;
using System.Collections.Generic;
using System.Text;

namespace Prospero.Emu.Format
{
    /// <summary>
    /// Layout constants shared by SELF/ELF parsing. Sources:
    ///  - PSDevWiki SELF/SPRX, ELF specification
    ///  - etaHEN/Byepervisor sce_self_header
    ///  - OpenOrbis PS4 ELF specification (PT_SCE_* segment types reused on PS5)
    /// </summary>
    public static class ElfConsts
    {
        public const uint ElfMagic = 0x464C457F;          // 0x7F 'E' 'L' 'F'
        public const uint SelfProsperoMagic = 0xEEF51454; // PS5 native SELF
        public const uint SelfOrbisMagic = 0x1D3D154F;    // PS4 compat SELF (also present on PS5)

        // e_type
        public const ushort ET_NONE = 0x0000;
        public const ushort ET_REL = 0x0001;
        public const ushort ET_EXEC = 0x0002;
        public const ushort ET_DYN = 0x0003;
        public const ushort ET_CORE = 0x0004;
        public const ushort ET_SCE_EXEC = 0xFE00;
        public const ushort ET_SCE_DYNEXEC = 0xFE10;
        public const ushort ET_SCE_DYNAMIC = 0xFE18;

        // p_type
        public const uint PT_LOAD = 0x00000001;
        public const uint PT_DYNAMIC = 0x00000002;
        public const uint PT_INTERP = 0x00000003;
        public const uint PT_NOTE = 0x00000004;
        public const uint PT_SCE_DYNLIBDATA = 0x61000000;
        public const uint PT_SCE_PROCPARAM = 0x61000001;
        public const uint PT_SCE_MODULE_PARAM = 0x61000002;
        public const uint PT_SCE_RELRO = 0x61000010;
        public const uint PT_SCE_COMMENT = 0x6FFFFF00;
        public const uint PT_GNU_EH_FRAME = 0x6474E550;
        public const uint PT_GNU_STACK = 0x6474E551;
        public const uint PT_GNU_RELRO = 0x6474E552;

        // p_flags
        public const uint PF_X = 0x1;
        public const uint PF_W = 0x2;
        public const uint PF_R = 0x4;

        public const byte ELFCLASS64 = 2;
        public const byte ELFDATA2LSB = 1;
        public const byte EM_X86_64 = 0x3E;
    }

    public sealed class Elf64Ehdr
    {
        public byte[] Ident = new byte[16];
        public ushort Type;
        public ushort Machine;
        public uint Version;
        public ulong Entry;
        public ulong Phoff;
        public ulong Shoff;
        public uint Flags;
        public ushort Ehsize;
        public ushort Phentsize;
        public ushort Phnum;
        public ushort Shentsize;
        public ushort Shnum;
        public ushort Shstrndx;

        public bool Is64 => Ident[4] == ElfConsts.ELFCLASS64;
        public bool IsLe => Ident[5] == ElfConsts.ELFDATA2LSB;
    }

    public sealed class Elf64Phdr
    {
        public uint Type;
        public uint Flags;
        public ulong Offset;
        public ulong Vaddr;
        public ulong Paddr;
        public ulong Filesz;
        public ulong Memsz;
        public ulong Align;
    }

    public sealed class LoadedSegment
    {
        public Elf64Phdr Header = new();
        public byte[] Data = Array.Empty<byte>();
    }

    /// <summary>
    /// Result of loading an executable: the ELF header, the (decrypted/plain)
    /// segments, and the raw bytes of the whole ELF for re-parsing metadata.
    /// </summary>
    public sealed class LoadedExecutable
    {
        public string SourcePath = "";
        public string Kind = "unknown";          // "raw-elf" | "ps5-self" | "ps4-self"
        public Elf64Ehdr Header = new();
        public List<LoadedSegment> Segments = new();
        public byte[] RawElf = Array.Empty<byte>();

        public LoadedSegment? FindSegment(uint pType)
            => Segments.Find(s => s.Header.Type == pType);

        public LoadedSegment? FindSegmentByVaddr(ulong vaddr)
        {
            foreach (var s in Segments)
                if (vaddr >= s.Header.Vaddr && vaddr < s.Header.Vaddr + s.Header.Memsz)
                    return s;
            return null;
        }
    }
}
