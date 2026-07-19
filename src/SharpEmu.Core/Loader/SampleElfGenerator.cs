using System;
using System.Collections.Generic;
using System.IO;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Generates a minimal, valid raw ELF64 that the emulator can boot to verify the
/// CPU interpreter + HLE syscall gate end-to-end. The program:
///   1. calls the self-test HLE function (RAX=0x6E01, R11=NID) which writes a
///      marker dword to a guest address passed in RDI;
///   2. verifies the marker and loops a few times;
///   3. halts with INT3.
/// This is a research/CI smoke test; it is NOT a game.
/// </summary>
public static class SampleElfGenerator
{
    public static byte[] Build()
    {
        // Hand-assembled x86-64 (little-endian). Entry is at file offset 0x1000.
        var code = new List<byte>();

        // mov rdi, 0x110001000  (guest scratch, inside mapped range)
        code.Add(0x48); code.Add(0xBF);
        code.Add(0x00); code.Add(0x10); code.Add(0x00); code.Add(0x10); code.Add(0x01); code.Add(0x00); code.Add(0x00); code.Add(0x00);
        // mov rax, 0x6E01
        code.Add(0x48); code.Add(0xC7); code.Add(0xC0); code.Add(0x01); code.Add(0x6E); code.Add(0x00); code.Add(0x00);
        // mov r11, 0x55AA0001
        code.Add(0x49); code.Add(0xBB);
        code.Add(0x01); code.Add(0x00); code.Add(0xAA); code.Add(0x55); code.Add(0x00); code.Add(0x00); code.Add(0x00); code.Add(0x00);
        // syscall
        code.Add(0x0F); code.Add(0x05);
        // mov eax, [rdi]  (read marker)
        code.Add(0x8B); code.Add(0x07);
        // cmp eax, 0x12345678
        code.Add(0x3D); code.Add(0x78); code.Add(0x56); code.Add(0x34); code.Add(0x12);
        // jne fail  (rel8) -- we place a short jump to the fail label below
        // We'll compute offset after.
        int jnePos = code.Count;
        code.Add(0x75); code.Add(0x00); // placeholder

        // loop: mov rcx, 10; .L: dec rcx; jnz .L
        int loopStart = code.Count;
        code.Add(0x48); code.Add(0xC7); code.Add(0xC1); code.Add(0x0A); code.Add(0x00); code.Add(0x00); code.Add(0x00); // mov rcx, 10
        int inner = code.Count;
        code.Add(0x48); code.Add(0xFF); code.Add(0xC9); // dec rcx
        code.Add(0x75); code.Add((byte)(inner - (code.Count + 1))); // jnz inner  (rel8)
        // int3 (success halt)
        code.Add(0xCC);

        // fail: int3
        int failPos = code.Count;
        code.Add(0xCC);

        // patch jne offset: from jnePos+2 to failPos
        int rel = failPos - (jnePos + 2);
        code[jnePos + 1] = (byte)rel;

        // Build ELF. Layout:
        //  - Ehdr at 0
        //  - Phdr at 0x40 (one PT_LOAD)
        //  - Code at 0x1000
        const int ehdrSize = 0x40;
        const int phdrSize = 0x38;
        const int phdrOff = 0x40;
        const int codeOff = 0x1000;
        int fileSize = codeOff + code.Count;
        // pad to page
        while (fileSize % 0x1000 != 0) fileSize++;
        int memSize = fileSize;

        var buf = new byte[fileSize];
        // Ehdr
        WriteU32(buf, 0, 0x464C457F);      // magic
        buf[4] = 2;   // ELFCLASS64
        buf[5] = 1;   // ELFDATA2LSB
        buf[6] = 1;   // version
        WriteU16(buf, 0x10, 2);            // e_type = ET_EXEC
        WriteU16(buf, 0x12, 0x3E);         // e_machine = EM_X86_64
        WriteU32(buf, 0x14, 1);            // e_version
        WriteU64(buf, 0x18, (ulong)codeOff); // e_entry
        WriteU64(buf, 0x20, (ulong)phdrOff); // e_phoff
        WriteU64(buf, 0x28, 0);            // e_shoff
        WriteU32(buf, 0x30, 0);            // e_flags
        WriteU16(buf, 0x34, ehdrSize);     // e_ehsize
        WriteU16(buf, 0x36, phdrSize);     // e_phentsize
        WriteU16(buf, 0x38, 1);            // e_phnum
        WriteU16(buf, 0x3A, 0);            // e_shentsize
        WriteU16(buf, 0x3C, 0);            // e_shnum
        WriteU16(buf, 0x3E, 0);            // e_shstrndx

        // Phdr (PT_LOAD)
        int p = phdrOff;
        WriteU32(buf, p + 0, 1);           // p_type = PT_LOAD
        WriteU32(buf, p + 4, 5);           // p_flags = R+X
        WriteU64(buf, p + 8, 0);           // p_offset
        WriteU64(buf, p + 16, 0);          // p_vaddr
        WriteU64(buf, p + 24, 0);          // p_paddr
        WriteU64(buf, p + 32, (ulong)fileSize); // p_filesz
        WriteU64(buf, p + 40, (ulong)memSize);  // p_memsz
        WriteU64(buf, p + 48, 0x1000);     // p_align

        // Code
        Array.Copy(code.ToArray(), 0, buf, codeOff, code.Count);

        return buf;
    }

    private static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    private static void WriteU64(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }
}
