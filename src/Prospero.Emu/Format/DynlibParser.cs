using System;
using System.Collections.Generic;
using Prospero.Emu.Core;
using Prospero.Emu.Kernel;

namespace Prospero.Emu.Format
{
    /// <summary>
    /// Parses the PT_SCE_DYNLIBDATA segment and the dynamic table to discover the
    /// imported libraries/modules and the NIDs (function name hashes) that the
    /// executable needs from the kernel / system modules.
    ///
    /// The NID scheme: each imported function name is hashed (FNV-1, the SCE
    /// variant) into a 32-bit identifier stored in the symbol table. We resolve
    /// them to stub handlers at runtime so dynamic linking completes.
    /// </summary>
    public sealed class DynlibParser
    {
        private readonly Logger _log;

        public DynlibParser(Logger log) => _log = log;

        public sealed class Import
        {
            public string Library = "";
            public ulong Nid;
            public string? ResolvedName;
        }

        /// <summary>
        /// A PLT/GOT relocation: the guest GOT slot at <see cref="GotVa"/> should
        /// be patched to point at the trampoline for <see cref="Nid"/>.
        /// </summary>
        public sealed class ImportRelocation
        {
            public ulong GotVa;
            public ulong Nid;
            public string? Name;
        }

        public List<string> ImportedLibraries { get; } = new();
        public List<Import> Imports { get; } = new();
        public List<ImportRelocation> Relocations { get; } = new();

        public void Parse(LoadedExecutable exe)
        {
            var seg = exe.FindSegment(ElfConsts.PT_SCE_DYNLIBDATA);
            if (seg == null || seg.Data.Length < 0x100)
            {
                _log.Debug("dynlib", "No PT_SCE_DYNLIBDATA segment (static binary or homebrew).");
                return;
            }
            var r = new LeReader(seg.Data);

            // Walk the dynamic table to find the relevant offsets.
            var dyn = exe.FindSegment(ElfConsts.PT_DYNAMIC);
            // PT_DYNAMIC is typically contained in the dynlibdata segment region.
            // For research we scan the segment for DT tags directly.
            ParseDynamic(r, seg.Data);
        }

        private void ParseDynamic(LeReader r, byte[] data)
        {
            // DT entries are d_tag(8) d_val/un(8). Stop at DT_NULL (0).
            int off = 0;
            ulong strTabOff = 0, symTabOff = 0, symEnt = 0, strSz = 0, symSz = 0;
            ulong jmpRel = 0, pltRelSz = 0, relaOff = 0;
            var needed = new List<ulong>();
            var importLibs = new List<ulong>();

            while (off + 16 <= data.Length)
            {
                ulong tag = r.ReadU64(off);
                ulong val = r.ReadU64(off + 8);
                off += 16;
                if (tag == 0) break; // DT_NULL

                switch (tag)
                {
                    case 0x61000001: strTabOff = val; break;          // DT_SCE_STRTAB
                    case 0x61000002: strSz = val; break;              // DT_SCE_STRSZ
                    case 0x61000003: symTabOff = val; break;          // DT_SCE_SYMTAB
                    case 0x61000004: symSz = val; break;              // DT_SCE_SYMTABSZ
                    case 0x61000005: symEnt = val; break;             // DT_SCE_SYMENT
                    case 0x61000014: jmpRel = val; break;            // DT_SCE_JMPREL
                    case 0x61000015: pltRelSz = val; break;          // DT_SCE_PLTRELSZ
                    case 0x61000017: relaOff = val; break;           // DT_SCE_RELA
                    case 0x1: needed.Add(val); break;                 // DT_NEEDED
                    case 0x61000011: importLibs.Add(val); break;      // DT_SCE_IMPORT_LIB
                }
            }

            // Resolve library names from DT_NEEDED values (string table offsets).
            foreach (var n in needed)
            {
                string lib = r.ReadCString((int)(strTabOff + n));
                if (!string.IsNullOrEmpty(lib)) ImportedLibraries.Add(lib);
            }
            // Also DT_SCE_IMPORT_LIB high dword encodes module index.
            foreach (var il in importLibs)
            {
                ulong strOff = il & 0xFFFFFFFF;
                string lib = r.ReadCString((int)(strTabOff + strOff));
                if (!string.IsNullOrEmpty(lib) && !ImportedLibraries.Contains(lib))
                    ImportedLibraries.Add(lib);
            }

            // Walk the symbol table: each entry is symEnt (0x18) bytes.
            // Layout: name_off(4) info(1) other(1) shndx(2) nid(8) ... we only need nid.
            if (symTabOff != 0 && symEnt != 0 && symSz != 0)
            {
                int count = (int)(symSz / symEnt);
                for (int i = 0; i < count; i++)
                {
                    int so = (int)(symTabOff + (ulong)i * symEnt);
                    // name offset is at +0 (relative to string table for dyn symbols)
                    ulong nid = r.ReadU64(so + 8);
                    if (nid == 0) continue;
                    Imports.Add(new Import { Nid = nid });
                }
            }

            _log.Info("dynlib", $"Imported libraries ({ImportedLibraries.Count}): {string.Join(", ", ImportedLibraries)}");
            _log.Info("dynlib", $"Imported function NIDs: {Imports.Count}");

            // Walk JUMP_SLOT relocations (R_X86_64_JUMP_SLOT = 0x7) so we can
            // patch the GOT to point at synthetic libkernel trampolines.
            // Rela entry layout: r_offset(8) r_info(8) r_addend(8). RelaEnt=0x18.
            ulong relBase = jmpRel != 0 ? jmpRel : relaOff;
            ulong relSize = jmpRel != 0 ? pltRelSz : 0;
            if (relBase != 0)
            {
                ulong count = relSize != 0 ? relSize / 0x18 : 0;
                for (ulong i = 0; i < count; i++)
                {
                    int re = (int)(relBase + i * 0x18);
                    ulong rOffset = r.ReadU64(re);
                    ulong rInfo = r.ReadU64(re + 8);
                    uint relType = (uint)(rInfo & 0xFFFFFFFF);
                    uint symIdx = (uint)(rInfo >> 32);
                    if (relType != 0x7) continue; // JUMP_SLOT only
                    // symbol entry: name_off(4) info(1) other(1) shndx(2) nid(8)
                    int symOff = (int)(symTabOff + (ulong)symIdx * symEnt);
                    ulong nid = symTabOff != 0 ? r.ReadU64(symOff + 8) : 0;
                    if (nid == 0) continue;
                    Relocations.Add(new ImportRelocation
                    {
                        GotVa = rOffset,
                        Nid = nid,
                        Name = KnownNids.Resolve(nid)
                    });
                }
                _log.Info("dynlib", $"JUMP_SLOT relocations (GOT->NID): {Relocations.Count}");
            }
            if (_log.MinLevel <= Logger.LogLevel.Debug)
                for (int i = 0; i < Math.Min(Imports.Count, 40); i++)
                    _log.Debug("dynlib", $"  NID 0x{Imports[i].Nid:X8}");
        }
    }
}
