using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpEmu.Core.Elf;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Parsed metadata from the SCE process/module parameter segment and, when
/// available, the game folder's sce_sys/param.sfo. This is the "basic game info
/// like title and version" the emulator surfaces.
/// </summary>
public sealed class GameMetadata
{
    public string TitleId = "(unknown)";
    public string Title = "(unknown)";
    public string Version = "(unknown)";
    public uint SdkVersionPs5;
    public uint SdkVersionPs4;
    public string ProcessName = "(eboot)";
    public bool IsPs5Process;
    public Dictionary<string, string> ParamSfo = new();
}

/// <summary>
/// Loads a PS5 ELF (eboot.bin, module .elf/.prx/.sprx raw ELF) into guest memory.
/// Handles the standard LOAD segments and the SCE-specific parameter segments.
/// A full PS5 .self is encrypted; this loader works on decrypted/raw ELF dumps,
/// which is the correct, legal input for a research emulator.
/// </summary>
public sealed class ElfLoader
{
    private readonly GuestMemory _mem;
    private readonly Logger _log;
    public Elf64_Ehdr Header;
    public List<Elf64_Phdr> ProgramHeaders = new();
    public ulong LoadBase;
    public ulong EntryPoint;
    public GameMetadata Metadata = new();

    public ElfLoader(GuestMemory mem, Logger log)
    {
        _mem = mem;
        _log = log;
    }

    public bool Load(string path, ulong preferredBase = 0)
    {
        _log.Info("loader", $"loading ELF: {path}");
        _relaPath = path;
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { _log.Error("loader", $"cannot read file: {ex.Message}"); return false; }

        if (data.Length < 64) { _log.Error("loader", "file too small to be an ELF"); return false; }

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        Header = ElfReader.ReadHeader(r);

        if (Header.Magic != ElfConstants.ELF_MAGIC)
        {
            // Some .self files carry a 0x100-byte SCE header before the ELF. Try to skip it.
            _log.Warn("loader", "bad ELF magic; attempting SCE self header skip (0x100)");
            ms.Seek(0x100, SeekOrigin.Begin);
            Header = ElfReader.ReadHeader(r);
            if (Header.Magic != ElfConstants.ELF_MAGIC)
            {
                _log.Error("loader", "not a valid ELF (or unsupported encrypted self)");
                return false;
            }
        }

        if (Header.EiClass != ElfConstants.ELFCLASS64) { _log.Error("loader", "not a 64-bit ELF"); return false; }
        if (Header.EiData != ElfConstants.ELFDATA2LSB) { _log.Error("loader", "not little-endian ELF"); return false; }

        _log.Debug("loader", $"e_type=0x{Header.EType:X} e_machine=0x{Header.EMachine:X} e_entry=0x{Header.EEntry:X} phnum={Header.EPhnum}");

        // Read program headers
        ms.Seek((long)Header.EPhoff, SeekOrigin.Begin);
        ProgramHeaders.Clear();
        for (int i = 0; i < Header.EPhnum; i++)
        {
            var ph = ElfReader.ReadProgramHeader(r);
            ProgramHeaders.Add(ph);
            _log.Trace("loader", $"PH[{i}] {ElfReader.ProgramHeaderName(ph.PType)} off=0x{ph.POffset:X} vaddr=0x{ph.PVaddr:X} filesz=0x{ph.PFilesz:X} memsz=0x{ph.PMemsz:X} flags=0x{ph.PFlags:X}");
        }

        // Determine load base. For ET_DYN we pick a preferred base; ET_EXEC uses the vaddr.
        ulong minVaddr = ulong.MaxValue;
        ulong maxEnd = 0;
        foreach (var ph in ProgramHeaders)
        {
            if (ph.PType != ElfConstants.PT_LOAD) continue;
            if (ph.PVaddr < minVaddr) minVaddr = ph.PVaddr;
            if (ph.PVaddr + ph.PMemsz > maxEnd) maxEnd = ph.PVaddr + ph.PMemsz;
        }
        if (minVaddr == ulong.MaxValue) { _log.Error("loader", "no LOAD segments"); return false; }

        if (Header.EType == ElfConstants.ET_EXEC)
        {
            LoadBase = 0; // fixed addresses
            EntryPoint = Header.EEntry;
        }
        else
        {
            LoadBase = preferredBase != 0 ? preferredBase : _mem.BaseAddress + 0x1000000;
            EntryPoint = Header.EEntry == 0 ? 0 : LoadBase + Header.EEntry;
            // Adjust segment vaddrs by base for PIE
            foreach (var ph in ProgramHeaders)
            {
                if (ph.PType == ElfConstants.PT_LOAD)
                {
                    ph.PVaddr += LoadBase;
                    ph.PPaddr += LoadBase;
                }
            }
            minVaddr = LoadBase + minVaddr;
            maxEnd = LoadBase + maxEnd;
        }

        // Map LOAD segments
        foreach (var ph in ProgramHeaders)
        {
            if (ph.PType != ElfConstants.PT_LOAD) continue;
            ulong vaddr = ph.PVaddr;
            ulong filesz = ph.PFilesz;
            ulong memsz = ph.PMemsz;
            if (filesz > 0)
            {
                ms.Seek((long)ph.POffset, SeekOrigin.Begin);
                byte[] seg = r.ReadBytes((int)filesz);
                _mem.WriteBytes(vaddr, seg, (int)filesz);
            }
            if (memsz > filesz)
            {
                // BSS zeroed implicitly by GuestMemory initial state, but be explicit.
                var zeros = new byte[memsz - filesz];
                _mem.WriteBytes(vaddr + filesz, zeros, (int)(memsz - filesz));
            }
            _mem.Map(vaddr, memsz, ph.PFlags, ElfReader.ProgramHeaderName(ph.PType));
            _log.Debug("loader", $"mapped LOAD @0x{vaddr:X} size=0x{memsz:X} flags={ph.PFlags}");
        }

        // Parse SCE process/module param for metadata
        ParseSceParam(data, ms);

        _log.Info("loader", $"load complete: base=0x{LoadBase:X} entry=0x{EntryPoint:X}");
        return true;
    }

    private void ParseSceParam(byte[] data, MemoryStream ms)
    {
        foreach (var ph in ProgramHeaders)
        {
            if (ph.PType != ElfConstants.PT_SCE_PROCPARAM && ph.PType != ElfConstants.PT_SCE_MODULE_PARAM)
                continue;

            ms.Seek((long)ph.POffset, SeekOrigin.Begin);
            using var r = new BinaryReader(ms);
            uint structSize = r.ReadUInt32();
            uint magic = r.ReadUInt32();
            uint version = r.ReadUInt32();
            uint sdkPs4 = r.ReadUInt32();
            uint sdkPs5 = r.ReadUInt32();
            Metadata.SdkVersionPs4 = sdkPs4;
            Metadata.SdkVersionPs5 = sdkPs5;
            Metadata.IsPs5Process = ph.PType == ElfConstants.PT_SCE_PROCPARAM;
            _log.Debug("loader", $"SCE param: magic=0x{magic:X} ver=0x{version:X} ps4sdk=0x{sdkPs4:X} ps5sdk=0x{sdkPs5:X}");

            // The process param carries a pointer to a process name string and, in some
            // layouts, a title id. We do a best-effort scan of the segment for an ASCII
            // title id pattern (e.g. "PPSAxxxxx") for display.
            ScanForTitleId(data, (long)ph.POffset, (long)(ph.POffset + Math.Max(ph.PFilesz, 0x200)));
        }
    }

    private void ScanForTitleId(byte[] data, long start, long end)
    {
        for (long i = start; i + 9 < end; i++)
        {
            // Title IDs look like PPSA#####, CUSA#####, etc. (4 letters + 5 digits)
            if (data[i] >= 'A' && data[i] <= 'Z' &&
                data[i + 1] >= 'A' && data[i + 1] <= 'Z' &&
                data[i + 2] >= 'A' && data[i + 2] <= 'Z' &&
                data[i + 3] >= 'A' && data[i + 3] <= 'Z' &&
                data[i + 4] >= '0' && data[i + 4] <= '9' &&
                data[i + 5] >= '0' && data[i + 5] <= '9' &&
                data[i + 6] >= '0' && data[i + 6] <= '9' &&
                data[i + 7] >= '0' && data[i + 7] <= '9' &&
                data[i + 8] >= '0' && data[i + 8] <= '9')
            {
                string tid = Encoding.ASCII.GetString(data, (int)i, 9);
                // Avoid false positives: next byte should be a terminator or separator
                byte nb = i + 9 < data.Length ? data[i + 9] : (byte)0;
                if (nb == 0 || nb == '/' || nb == ' ' || nb == '.' || (nb >= 'A' && nb <= 'Z'))
                {
                    Metadata.TitleId = tid;
                    _log.Info("loader", $"detected title id: {tid}");
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Read the conventional sce_sys/param.sfo for richer metadata (title, version).
    /// Returns false if not present (not all dumps include it).
    /// </summary>
    public bool ReadParamSfo(string gameFolder)
    {
        foreach (var cand in new[]
        {
            Path.Combine(gameFolder, "sce_sys", "param.sfo"),
            Path.Combine(Path.GetDirectoryName(gameFolder) ?? "", "sce_sys", "param.sfo"),
        })
        {
            if (File.Exists(cand))
            {
                try
                {
                    var sfo = ParamSfo.Parse(File.ReadAllBytes(cand));
                    foreach (var kv in sfo)
                        Metadata.ParamSfo[kv.Key] = kv.Value;
                    if (sfo.TryGetValue("TITLE_ID", out var tid) && !string.IsNullOrEmpty(tid)) Metadata.TitleId = tid;
                    if (sfo.TryGetValue("TITLE", out var title) && !string.IsNullOrEmpty(title)) Metadata.Title = title;
                    if (sfo.TryGetValue("VERSION", out var ver) && !string.IsNullOrEmpty(ver)) Metadata.Version = ver;
                    _log.Info("loader", $"param.sfo: TITLE='{Metadata.Title}' VERSION='{Metadata.Version}' TITLE_ID='{Metadata.TitleId}'");
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Warn("loader", $"failed to parse param.sfo: {ex.Message}");
                }
            }
        }
        return false;
    }

    /// <summary>Apply R_X86_64_RELATIVE relocations for a PIE module given its load base.</summary>
    public void ApplyRelocations()
    {
        // RELATIVE relocations live in the SCE_RELA segment. After we remap LOAD
        // vaddrs by LoadBase, the RELA entries we stored still reference file
        // offsets; we re-read them from disk to get the original r_offset and
        // r_addend, then write the fixed-up value into guest memory at the
        // relocated address.
        foreach (var ph in ProgramHeaders)
        {
            if (ph.PType != ElfConstants.PT_SCE_RELA) continue;
            if (ph.PFilesz == 0) continue;
            int count = (int)(ph.PFilesz / 24); // Elf64_Rela = 24 bytes
            _log.Debug("loader", $"applying {count} RELA relocations from SCE_RELA");

            // Re-read the RELA bytes from disk using the original file offset.
            byte[] relaBytes = File.ReadAllBytes(_relaPath ?? "");
            if (relaBytes.Length == 0) continue;

            for (int i = 0; i < count; i++)
            {
                int off = i * 24;
                ulong rOffset = BitConverter.ToUInt64(relaBytes, off);
                ulong rInfo = BitConverter.ToUInt64(relaBytes, off + 8);
                long rAddend = (long)BitConverter.ToUInt64(relaBytes, off + 16);
                ulong type = rInfo & 0xFFFFFFFF;
                if (type == ElfConstants.R_X86_64_RELATIVE)
                {
                    _mem.WriteUInt64(rOffset + LoadBase, (ulong)rAddend + LoadBase);
                }
            }
        }
    }

    // Captured at Load() time for relocation re-reads.
    private string _relaPath;
}
