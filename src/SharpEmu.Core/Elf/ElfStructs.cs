using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SharpEmu.Core.Elf;

/// <summary>
/// Constants for the ELF64 format and the Sony-specific ("SCE") program header
/// types used by PS4/PS5 executables (eboot.bin, modules, etc.).
///
/// These values are documented publicly (e.g. the ps5-payload-dev SDK and the
/// PS4/PS5 homebrew toolchains) and are reimplemented here for educational use.
/// </summary>
public static class ElfConstants
{
    public const uint ELF_MAGIC = 0x464C457F; // 0x7F 'E' 'L' 'F'

    // e_ident indices
    public const int EI_CLASS = 4;
    public const int EI_DATA = 5;
    public const int ELFCLASS64 = 2;
    public const int ELFDATA2LSB = 1;

    // e_type
    public const ushort ET_NONE = 0;
    public const ushort ET_REL = 1;
    public const ushort ET_EXEC = 2;
    public const ushort ET_DYN = 3;
    public const ushort ET_CORE = 4;

    // p_type (standard)
    public const uint PT_NULL = 0;
    public const uint PT_LOAD = 1;
    public const uint PT_DYNAMIC = 2;
    public const uint PT_INTERP = 3;
    public const uint PT_NOTE = 4;
    public const uint PT_SHLIB = 5;
    public const uint PT_PHDR = 6;
    public const uint PT_TLS = 7;
    public const uint PT_GNU_EH_FRAME = 0x6474E550;
    public const uint PT_GNU_STACK = 0x6474E551;
    public const uint PT_GNU_RELRO = 0x6474E552;

    // p_type (Sony / SCE specific)
    public const uint PT_SCE_RELA = 0x60000000;
    public const uint PT_SCE_DYNLIBDATA = 0x61000000;
    public const uint PT_SCE_PROCPARAM = 0x61000001;
    public const uint PT_SCE_MODULE_PARAM = 0x61000002;
    public const uint PT_SCE_RELRO = 0x61000010;
    public const uint PT_SCE_COMMENT = 0x6FFFFF00;
    public const uint PT_SCE_VERSION = 0x6FFFFF01;

    // p_flags
    public const uint PF_X = 1;
    public const uint PF_W = 2;
    public const uint PF_R = 4;

    // d_tag (dynamic)
    public const long DT_NULL = 0;
    public const long DT_NEEDED = 1;
    public const long DT_HASH = 4;
    public const long DT_STRTAB = 5;
    public const long DT_SYMTAB = 6;
    public const long DT_STRSZ = 10;
    public const long DT_SYMENT = 11;
    public const long DT_RELA = 7;
    public const long DT_RELASZ = 8;
    public const long DT_RELAENT = 9;
    public const long DT_SCE_PLTGOT = 0x61000010;
    public const long DT_SCE_JMPREL = 0x61000011;
    public const long DT_SCE_DYNLIBDATA = 0x61000020;
    public const long DT_SCE_PROCESS_PARAM = 0x61000030;

    // Relocation types (x86-64)
    public const ulong R_X86_64_NONE = 0;
    public const ulong R_X86_64_64 = 1;
    public const ulong R_X86_64_PC32 = 2;
    public const ulong R_X86_64_GLOB_DAT = 6;
    public const ulong R_X86_64_JUMP_SLOT = 7;
    public const ulong R_X86_64_RELATIVE = 8;
    public const ulong R_X86_64_DTPMOD64 = 16;
    public const ulong R_X86_64_DTPOFF64 = 17;
    public const ulong R_X86_64_TPOFF64 = 18;
    public const ulong R_X86_64_IRELATIVE = 37;

    // Magic numbers for SCE parameter segments
    public const uint SCE_PROCESS_PARAM_MAGIC = 0x4942524F;
    public const uint SCE_MODULE_PARAM_MAGIC = 0x3C13F4BF;
}

[Serializable]
public struct Elf64_Ehdr
{
    public uint Magic;
    public byte EiClass;
    public byte EiData;
    public byte EiVersion;
    public byte EiOsAbi;
    public byte EiAbiVersion;
    public byte[] EiPad = new byte[7];
    public ushort EType;
    public ushort EMachine;
    public uint EVersion;
    public ulong EEntry;
    public ulong EPhoff;
    public ulong EShoff;
    public uint EFlags;
    public ushort EHsize;
    public ushort EPhentsize;
    public ushort EPhnum;
    public ushort EShentsize;
    public ushort EShnum;
    public ushort EShstrndx;
}

[Serializable]
public struct Elf64_Phdr
{
    public uint PType;
    public uint PFlags;
    public ulong POffset;
    public ulong PVaddr;
    public ulong PPaddr;
    public ulong PFilesz;
    public ulong PMemsz;
    public ulong PAlign;
}

[Serializable]
public struct Elf64_Dyn
{
    public long DTag;
    public ulong DVal;
}

[Serializable]
public struct Elf64_Rela
{
    public ulong ROffset;
    public ulong RInfo;
    public long RAddend;

    public ulong RSym => RInfo >> 32;
    public ulong RType => RInfo & 0xFFFFFFFF;
}

[Serializable]
public struct Elf64_Sym
{
    public uint StName;
    public byte StInfo;
    public byte StOther;
    public ushort StShndx;
    public ulong StValue;
    public ulong StSize;

    public byte Bind => (byte)(StInfo >> 4);
    public byte Type => (byte)(StInfo & 0xF);
}

public static class ElfReader
{
    public static Elf64_Ehdr ReadHeader(BinaryReader r)
    {
        var h = new Elf64_Ehdr();
        h.Magic = r.ReadUInt32();
        h.EiClass = r.ReadByte();
        h.EiData = r.ReadByte();
        h.EiVersion = r.ReadByte();
        h.EiOsAbi = r.ReadByte();
        h.EiAbiVersion = r.ReadByte();
        h.EiPad = r.ReadBytes(7);
        h.EType = r.ReadUInt16();
        h.EMachine = r.ReadUInt16();
        h.EVersion = r.ReadUInt32();
        h.EEntry = r.ReadUInt64();
        h.EPhoff = r.ReadUInt64();
        h.EShoff = r.ReadUInt64();
        h.EFlags = r.ReadUInt32();
        h.EHsize = r.ReadUInt16();
        h.EPhentsize = r.ReadUInt16();
        h.EPhnum = r.ReadUInt16();
        h.EShentsize = r.ReadUInt16();
        h.EShnum = r.ReadUInt16();
        h.EShstrndx = r.ReadUInt16();
        return h;
    }

    public static Elf64_Phdr ReadProgramHeader(BinaryReader r)
    {
        var p = new Elf64_Phdr();
        p.PType = r.ReadUInt32();
        p.PFlags = r.ReadUInt32();
        p.POffset = r.ReadUInt64();
        p.PVaddr = r.ReadUInt64();
        p.PPaddr = r.ReadUInt64();
        p.PFilesz = r.ReadUInt64();
        p.PMemsz = r.ReadUInt64();
        p.PAlign = r.ReadUInt64();
        return p;
    }

    public static string ProgramHeaderName(uint type) => type switch
    {
        ElfConstants.PT_LOAD => "LOAD",
        ElfConstants.PT_DYNAMIC => "DYNAMIC",
        ElfConstants.PT_INTERP => "INTERP",
        ElfConstants.PT_TLS => "TLS",
        ElfConstants.PT_GNU_EH_FRAME => "EH_FRAME",
        ElfConstants.PT_GNU_STACK => "GNU_STACK",
        ElfConstants.PT_SCE_RELA => "SCE_RELA",
        ElfConstants.PT_SCE_DYNLIBDATA => "SCE_DYNLIBDATA",
        ElfConstants.PT_SCE_PROCPARAM => "SCE_PROCPARAM",
        ElfConstants.PT_SCE_MODULE_PARAM => "SCE_MODULE_PARAM",
        ElfConstants.PT_SCE_RELRO => "SCE_RELRO",
        ElfConstants.PT_SCE_COMMENT => "SCE_COMMENT",
        ElfConstants.PT_SCE_VERSION => "SCE_VERSION",
        _ => $"0x{type:X8}",
    };
}
