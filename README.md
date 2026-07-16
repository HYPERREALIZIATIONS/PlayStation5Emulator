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

## Goal

A *simple but real* research emulator that can:

- Open a legally dumped game's `eboot.bin` or an ELF file.
- Read basic game info: title id, version, SDK version, process params.
- Execute native x86-64 (Zen 2) CPU instructions via an interpreter.
- Load system modules / resolve the imported library stubs (libkernel, libc, …).
- Provide a *partial* FreeBSD/Orbis/Prospero kernel personality (syscalls).
- Include early graphics pipeline work: AGC init, GNF texture parsing, command
  buffer (PM4-style) parsing, and a host **Vulkan** renderer path so a few known
  programs can reach video-output milestones.
- Write a detailed **log file** for debugging every stage.

It is deliberately **not** a polished consumer emulator. Only a few known
programs are expected to reach booting, file loading, resource/shader setup,
AGC init, or first frames.

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
  Emulator.cs             Orchestrator: load -> map -> run
  Core/
    Logger.cs             Dual console+file logger (the debug artifact)
    LeReader.cs           Little-endian binary reader
    GuestMemory.cs        Sparse guest memory + MMIO hooks
  Format/
    ElfTypes.cs           ELF/SELF constants + structures
    SelfLoader.cs         SELF (Prospero/Orbis) + raw ELF loader
    GameInfo.cs           Extracts title/version/SDK from procpparam
    DynlibParser.cs       Parses PT_SCE_DYNLIBDATA imports (NIDs, modules)
  Cpu/
    Registers.cs          x86-64 register file + RFLAGS
    Decoder.cs            ModRM/SIB/prefix decoder
    CpuCore.cs            Interpreter (MOV/ALU/jumps/stack/syscall/…)
  Kernel/
    IKernel.cs            Syscall dispatch interface
    PartialKernel.cs      Partial FreeBSD/Orbis/Prospero syscalls
    KnownNids.cs          Curated libkernel/libc/AGC NID name table
    HackedFileSystem.cs   Maps game dir files for open/read
  Graphics/
    Agc.cs                AGC init, command-buffer + GNF parsing, milestones
    VulkanHost.cs         Host Vulkan loader (Win/Linux/macOS) + headless fallback
  Tools/
    MakeTestElf.cs        Emits a built-in self-test ELF
```

## Current limitations

- The CPU is an **interpreter** covering a practical subset of the integer
  instruction set (no AVX/AVX2/FPU/SIMD execution yet; SSE/AVX prefixes are
  tolerated as no-ops where safe).
- Syscalls are benign stubs that return success / synthetic handles. Real
  libkernel/system-module behavior is not implemented.
- Graphics is **research-level**: we parse command buffers to detect draw /
  resource-setup milestones and record them; we do not yet translate GNM to
  host Vulkan draw calls.
- Encrypted NPDRM SELF files are **not** decrypted (by design — no piracy).

## References used (public documentation)

- PSDevWiki: SELF/SPRX, ELF specification, File Structures, Kernel (FreeBSD 11).
- OpenOrbis PS4 Toolchain docs: `PT_SCE_DYNLIBDATA`, dynlib/NID tables.
- etaHEN / Byepervisor: Prospero SELF header (`0xEEF51454`), `sce_self_header`.
- PS5 bulk-sdk-rewriter: `sce_process_param` ('ORBI') layout.
- Public AMD RDNA 2 / Oberon GPU specifications (36 CU, GFX10.3).

This software is provided "as is" for research. Respect all applicable laws.
