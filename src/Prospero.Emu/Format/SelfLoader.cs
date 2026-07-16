using System;
using System.Collections.Generic;
using System.IO;
using Prospero.Emu.Core;

namespace Prospero.Emu.Format
{
    /// <summary>
    /// Parses PS5 (Prospero) eboot.bin / fself and raw ELF files.
    ///
    /// IMPORTANT / anti-piracy: This loader does NOT decrypt, strip signatures,
    /// or strip DRM. It consumes *already-decrypted* ELF/fself blobs the user is
    /// legally entitled to run (e.g. homebrew, or dumps obtained by the user from
    /// their own console with tools they are legally permitted to use). For an
    /// encrypted NPDRM SELF we simply detect the structure; we do not break it.
    ///
    /// The PS5 SELF header layout (etaHEN/Byepervisor sce_self_header):
    ///   common header : magic(4) version(1) mode(1) endian(1) attribs(1)
    ///   ext header    : key_type(4) header_size(2) meta_size(2)
    ///                   file_size(8) num_entries(2) flags(2)
    ///   then num_entries * self_entry_t (segment descriptors), then the ELF.
    ///
    /// For our research purposes the simplest robust approach is: if the file
    /// begins with a SELF magic, locate the embedded ELF by scanning for the
    /// ELF magic (0x7F 'E' 'L' 'F') after the SELF header. This avoids depending
    /// on every field being correct and works for both Propsero and Orbis
    /// fakeself dumps produced by the user's own tooling.
    /// </summary>
    public sealed class SelfLoader
    {
        private readonly Logger _log;

        public SelfLoader(Logger log) => _log = log;

        public LoadedExecutable Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Executable not found: {path}", path);

            byte[] data = File.ReadAllBytes(path);
            _log.Info("loader", $"Reading '{path}' ({data.Length} bytes)");

            var exe = new LoadedExecutable { SourcePath = path, RawElf = data };

            uint maybeMagic = ReadU32(data, 0);
            if (maybeMagic == ElfConsts.ElfMagic)
            {
                exe.Kind = "raw-elf";
                _log.Info("loader", "Detected raw ELF");
            }
            else if (maybeMagic == ElfConsts.SelfProsperoMagic || maybeMagic == ElfConsts.SelfOrbisMagic)
            {
                exe.Kind = maybeMagic == ElfConsts.SelfProsperoMagic ? "ps5-self" : "ps4-self";
                _log.Info("loader", $"Detected SELF magic 0x{maybeMagic:X8} ({exe.Kind})");
                int elfOff = FindEmbeddedElf(data);
                if (elfOff < 0)
                    throw new InvalidDataException("SELF present but embedded ELF not found (encrypted/unhandled SELF?).");
                _log.Info("loader", $"Embedded ELF at offset 0x{elfOff:X}");
                // Re-scope the working buffer to the embedded ELF.
                data = SliceFrom(data, elfOff);
                exe.RawElf = data;
            }
            else
            {
                throw new InvalidDataException($"Not an ELF or SELF file (magic 0x{maybeMagic:X8}).");
            }

            ParseElf(exe, data);
            return exe;
        }

        private static int FindEmbeddedElf(byte[] data)
        {
            // Scan for the ELF magic after the first 0x20 bytes (past SELF header).
            for (int i = 0x20; i + 4 <= data.Length; i++)
            {
                if (data[i] == 0x7F && data[i + 1] == (byte)'E' && data[i + 2] == (byte)'L' && data[i + 3] == (byte)'F')
                    return i;
            }
            return -1;
        }

        private static byte[] SliceFrom(byte[] data, int off)
        {
            var r = new byte[data.Length - off];
            Array.Copy(data, off, r, 0, r.Length);
            return r;
        }

        private void ParseElf(LoadedExecutable exe, byte[] data)
        {
            var r = new LeReader(data);
            var eh = new Elf64Ehdr();
            eh.Ident = r.Slice(0, 16);
            if (r.ReadU32(0) != ElfConsts.ElfMagic)
                throw new InvalidDataException("ELF magic missing in embedded payload.");

            eh.Type = r.ReadU16(16);
            eh.Machine = r.ReadU16(18);
            eh.Version = r.ReadU32(20);
            eh.Entry = r.ReadU64(24);
            eh.Phoff = r.ReadU64(32);
            eh.Shoff = r.ReadU64(40);
            eh.Flags = r.ReadU32(48);
            eh.Ehsize = r.ReadU16(52);
            eh.Phentsize = r.ReadU16(54);
            eh.Phnum = r.ReadU16(56);
            eh.Shentsize = r.ReadU16(58);
            eh.Shnum = r.ReadU16(60);
            eh.Shstrndx = r.ReadU16(62);
            exe.Header = eh;

            _log.Info("loader", $"e_type=0x{eh.Type:X4} machine=0x{eh.Machine:X4} entry=0x{eh.Entry:X} phnum={eh.Phnum}");

            if (eh.Machine != ElfConsts.EM_X86_64)
                _log.Warn("loader", $"Unexpected machine 0x{eh.Machine:X4} (expected x86-64).");

            for (ushort i = 0; i < eh.Phnum; i++)
            {
                ulong poff = eh.Phoff + (ulong)i * eh.Phentsize;
                var ph = new Elf64Phdr
                {
                    Type = r.ReadU32((int)poff),
                    Flags = r.ReadU32((int)poff + 4),
                    Offset = r.ReadU64((int)poff + 8),
                    Vaddr = r.ReadU64((int)poff + 16),
                    Paddr = r.ReadU64((int)poff + 24),
                    Filesz = r.ReadU64((int)poff + 32),
                    Memsz = r.ReadU64((int)poff + 40),
                    Align = r.ReadU64((int)poff + 48),
                };

                var seg = new LoadedSegment { Header = ph };
                if (ph.Type == ElfConsts.PT_LOAD || ph.Type == ElfConsts.PT_SCE_DYNLIBDATA ||
                    ph.Type == ElfConsts.PT_SCE_PROCPARAM || ph.Type == ElfConsts.PT_SCE_MODULE_PARAM ||
                    ph.Type == ElfConsts.PT_SCE_RELRO || ph.Type == ElfConsts.PT_SCE_COMMENT ||
                    ph.Type == ElfConsts.PT_DYNAMIC || ph.Type == ElfConsts.PT_NOTE || ph.Type == ElfConsts.PT_GNU_RELRO)
                {
                    long copyLen = (long)Math.Min(ph.Filesz, (ulong)data.Length - (long)ph.Offset);
                    if (copyLen > 0 && ph.Offset + (ulong)copyLen <= (ulong)data.Length)
                        seg.Data = r.Slice((int)ph.Offset, (int)copyLen);
                    else
                        seg.Data = Array.Empty<byte>();
                }
                exe.Segments.Add(seg);
                _log.Debug("loader", $"  ph[{i}] type=0x{ph.Type:X8} vaddr=0x{ph.Vaddr:X} filesz=0x{ph.Filesz:X} memsz=0x{ph.Memsz:X}");
            }
        }

        private static uint ReadU32(byte[] data, int off) =>
            (uint)(data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24));
    }
}
