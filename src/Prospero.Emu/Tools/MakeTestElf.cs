// A tiny helper that emits a 64-bit ELF "homebrew" test program exercising the
// CPU interpreter and the syscall path. This lets the emulator be run end-to-end
// without a real PS5 dump, and serves as a self-test / documentation artifact.
//
// The generated program does:
//   write(1, msg, len)
//   exit(0)
//
// It is NOT a real PS5 binary; it is a plain x86-64 ELF used to validate the
// research emulator's interpreter + FreeBSD syscall convention.

using System;
using System.IO;
using System.Text;

namespace Prospero.Emu.Tools
{
    public static class MakeTestElf
    {
        public static void Emit(string path)
        {
            var msg = Encoding.ASCII.GetBytes("Hello from Prospero Emu research CPU!\n");
            var code = new MemoryStream();
            var bw = new BinaryWriter(code);

            // mov edi, 1
            bw.Write((byte)0xBF); bw.Write((uint)1);
            // mov edx, len
            bw.Write((byte)0xBA); bw.Write((uint)msg.Length);
            // lea rsi, [rip + disp]  (48 8D 35 disp32)
            long leaPos = code.Position;
            bw.Write((byte)0x48); bw.Write((byte)0x8D); bw.Write((byte)0x35); bw.Write((uint)0);
            // mov eax, 4  (freebsd write)
            bw.Write((byte)0xB8); bw.Write((uint)4);
            // syscall
            bw.Write((byte)0x0F); bw.Write((byte)0x05);
            // mov edi, 0
            bw.Write((byte)0xBF); bw.Write((uint)0);
            // mov eax, 1  (exit)
            bw.Write((byte)0xB8); bw.Write((uint)1);
            // syscall
            bw.Write((byte)0x0F); bw.Write((byte)0x05);

            byte[] blob = code.ToArray();
            long msgOff = blob.Length;
            long ripNext = leaPos + 7;
            int disp = (int)(msgOff - ripNext);
            byte[] dispBytes = BitConverter.GetBytes((uint)disp);
            Array.Copy(dispBytes, 0, blob, leaPos + 3, 4);

            // Append message data.
            var full = new MemoryStream();
            full.Write(blob, 0, blob.Length);
            full.Write(msg, 0, msg.Length);
            byte[] codeWithData = full.ToArray();

            using var elf = new MemoryStream();
            BuildElf(elf, codeWithData);
            File.WriteAllBytes(path, elf.ToArray());
            Console.WriteLine($"Wrote test ELF: {path} ({elf.Length} bytes)");
        }

        private static void BuildElf(MemoryStream elf, byte[] code)
        {
            const long fileOff = 0x1000;
            var ew = new BinaryWriter(elf);
            // Ehdr
            ew.Write(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
            ew.Write((byte)2); ew.Write((byte)1); ew.Write((byte)1); ew.Write(new byte[9]);
            ew.Write((ushort)2);                // ET_EXEC
            ew.Write((ushort)0x3E);             // x86-64
            ew.Write((uint)1);
            ew.Write((ulong)0x400000);          // e_entry
            ew.Write((ulong)0x40);              // e_phoff
            ew.Write((ulong)0);                 // e_shoff
            ew.Write((uint)0);
            ew.Write((ushort)0x40);             // e_ehsize
            ew.Write((ushort)0x38);             // e_phentsize
            ew.Write((ushort)1);                // e_phnum
            ew.Write((ushort)0);
            ew.Write((ushort)0);
            ew.Write((ushort)0);
            // Phdr (LOAD, R+X)
            ew.Write((uint)1); ew.Write((uint)5);
            ew.Write((ulong)fileOff);           // p_offset
            ew.Write((ulong)0x400000);          // p_vaddr
            ew.Write((ulong)0x400000);          // p_paddr
            ew.Write((ulong)code.Length);
            ew.Write((ulong)code.Length);
            ew.Write((ulong)0x1000);
            // pad to fileOff
            while (elf.Position < fileOff) ew.Write((byte)0);
            ew.Write(code, 0, code.Length);
        }
    }
}
