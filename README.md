# Prospero Emu

An **educational, research-focused** early **PlayStation 5 (codename *Prospero*)** emulator written in C#.

> ⚠️ **LEGAL / ETHICS NOTICE**
> This project is for **legal, educational, and compatibility research only**.
> It does **not** contain, link, or implement any decryption, signature
> verification, or DRM-circumvention code. It does **not** ship any Sony
> copyrighted system firmware, keys, or BIOS. You must supply your own
> **legally obtained, already-decrypted** `eboot.bin` / ELF / fself file
> (e.g. homebrew, or a dump you created from your own console with tooling you
> are legally permitted to use). Do not use this software with pirated material.

## What it does now

- Opens a legally dumped game `eboot.bin` / ELF / fself (Prospero `0xEEF51454`,
  Orbis `0x1D3D154F`, or raw ELF). **No decryption / keys / DRM code.**
- Reads game info: title id, version, SDK version, `sce_process_param`.
- **x86-64 (Zen 2) CPU interpreter** covering MOV, full ALU (ADD/OR/ADC/SBB/
  AND/SUB/XOR/CMP/TEST), shifts/rotates, MUL/IMUL/DIV (full-width RDX:RAX),
  MOVZX/MOVSX, PUSH/POP/CALL/RET, LEA, Jcc/JMP, NOP, CDQ, and **SSE** ops
  (MOVUPS/MOVAPS/MOVSS, PXOR/XORPS, ANDPS/ORPS, ADDPS/MULPS/SUBPS/DIVPS,
  UCOMISS). Unknown instructions are *skipped gracefully* (logged) so a research
  run gets as far as possible instead of faulting.
- **Partial kernel**: FreeBSD + Orbis/Prospero syscalls (read/write/mmap/exit,
  memory reservation, threads) and a **libkernel personality** reached via
  synthetic GOT/PLT trampolines: imported `libkernel`/`libc`/AGC calls are
  patched to `mov rax, nid; syscall; ret` trampolines and dispatched by NID.
- **Early graphics (AGC)**: AGC init, GNF texture parsing (on file open),
  PM4-style command-buffer decode (detects DRAW / SET_SHADER / resource setup /
  first-frame milestones), and a host **Vulkan** renderer path (Win/Linux/macOS
  via MoltenVK) with headless fallback.
- Writes a detailed **log file** for every stage.

## Platforms

- **Windows** — Vulkan via `vulkan-1.dll`.
- **Linux** — Vulkan via `libvulkan.so.1`.
- **macOS** — Vulkan via `libvulkan.dylib` (MoltenVK). The same Vulkan code path
  is used; if no Vulkan loader/ICD is present the emulator falls back to a
  *headless* renderer that still records frame milestones in the log.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```sh
cd src/Prospero.Emu
dotnet build -c Release
```

## Run

```sh
# Run against an eboot.bin / elf you are legally entitled to use:
dotnet run -- <path-to-eboot.bin> --root <game-dir> -l emu.log -v

# Generate a tiny built-in self-test ELF that exercises the CPU + syscalls:
dotnet run -- --make-test test.elf
dotnet run -- test.elf -l test.log
```

Options:

| Option | Description |
| ------ | ----------- |
| `<path>` | Path to `eboot.bin` / ELF / fself |
| `-r, --root <dir>` | Game/application directory (for file opens) |
| `-l, --log <file>` | Log file path (default `./prospero-emu.log`) |
| `-v, --verbose` | Verbose (trace) logging |
| `--make-test` | Emit a built-in self-test ELF and exit |
| `-h, --help` | Show help |

## Architecture

```
src/Prospero.Emu/
  Program.cs              CLI entry point, log file setup
  Emulator.cs             Orchestrator: load -> map -> patch GOT -> run
  Core/
    Logger.cs             Dual console+file logger (the debug artifact)
    LeReader.cs           Little-endian binary reader
    GuestMemory.cs        Sparse guest memory + MMIO hooks
  Format/
    ElfTypes.cs           ELF/SELF constants + structures
    SelfLoader.cs         SELF (Prospero/Orbis) + raw ELF loader
    GameInfo.cs           Extracts title/version/SDK from procpparam
    DynlibParser.cs       PT_SCE_DYNLIBDATA + JUMP_SLOT relocations (GOT->NID)
  Cpu/
    Registers.cs          x86-64 register file + RFLAGS + XMM
    Decoder.cs            ModRM/SIB/prefix decoder
    CpuCore.cs            Interpreter (MOV/ALU/shifts/mul/div/movzx/SSE/syscall)
    CpuExtensions.cs      XMM helpers + instruction-length decoder (skip fallback)
  Kernel/
    IKernel.cs            Syscall dispatch interface
    PartialKernel.cs      FreeBSD/Orbis/Prospero syscalls + NID libkernel dispatch
    KnownNids.cs          Curated libkernel/libc/AGC NID name table
    HackedFileSystem.cs   Maps game dir files for open/read (+ GNF parse)
  Graphics/
    Agc.cs                AGC init, command-buffer + GNF parsing, milestones
    VulkanHost.cs         Host Vulkan loader (Win/Linux/macOS) + headless fallback
  Tools/
    MakeTestElf.cs        Emits a built-in self-test ELF
```

## Current limitations (honest scope)

This is a **research emulator**, not a commercial-game emulator. It deliberately
does **not** and **cannot** run encrypted commercial titles:

- **No decryption.** Encrypted NPDRM SELF files are not decrypted (by design —
  no piracy, no keys). You must supply an already-decrypted ELF/fself you are
  legally entitled to run (homebrew, or a dump from your own console).
- **Interpreter, not JIT.** The CPU is a correct-but-slow interpreter. A real
  recompiler (AVX2 guest → host) is the next major step and not yet present.
- **Syscalls are benign stubs** returning success / synthetic handles. Real
  libkernel/system-module behavior (threads, async I/O, GPU pools) is partial.
- **Graphics is research-level.** We parse command buffers and GNF textures and
  record draw/resource-setup milestones; we do **not** yet translate GNM to
  real host Vulkan draw calls (a large, separate effort). The Vulkan host
  reports availability and drives headless frame milestones.
- Only a few known programs (homebrew / simple ELFs) are expected to reach
  booting, file loading, resource setup, AGC init, or first-frame milestones.

> Building a full PS5 emulator that runs commercial games requires (a) Sony
> decryption keys (illegal to possess/use for circumvention) and (b) years of
> RDNA-2/AGC reverse engineering that is not publicly available. This project
> is scoped to legal, educational research on the parts that *can* be studied
> openly: CPU behavior, loader formats, syscall surfaces, and the graphics
> command stream shape.

## References used (public documentation)

- PSDevWiki: SELF/SPRX, ELF specification, File Structures, Kernel (FreeBSD 11).
- OpenOrbis PS4 Toolchain docs: `PT_SCE_DYNLIBDATA`, dynlib/NID tables.
- etaHEN / Byepervisor: Prospero SELF header (`0xEEF51454`), `sce_self_header`.
- PS5 bulk-sdk-rewriter: `sce_process_param` ('ORBI') layout.
- Public AMD RDNA 2 / Oberon GPU specifications (36 CU, GFX10.3).

This software is provided "as is" for research. Respect all applicable laws.
