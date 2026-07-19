# SharpEmu

> **Experimental PlayStation 5 research emulator written in C#.**

SharpEmu is an educational, research-focused emulator for the PlayStation 5. It is
**not** a polished consumer application and **not** a tool for piracy. It is intended
to help researchers and curious engineers study how a modern x86-64 console firmware
and game executable are structured, how system modules are loaded, and how an
early graphics pipeline can be wired to a host GPU via Vulkan.

## What it does

* Loads a **legally obtained, decrypted** game ELF (`eboot.bin` or a `.elf` module)
  — encrypted `.self` files are **not** supported (no keys, no firmware).
* Reads basic game metadata (title, version, title id) from the SCE process param
  segment and `sce_sys/param.sfo`.
* Executes **native x86-64 instructions** through a built-in interpreter (the PS5
  CPU is an AMD Zen 2, so this is genuine native execution rather than a recompiler
  for an exotic ISA).
* Resolves system-module imports through an HLE (high-level emulation) layer keyed
  by NID, including the `libkernel`, `libc`, `libSceFiber`, `libSceAmpr`,
  `libSceVideoOut` and `libSceAgc` modules.
* Provides **partial kernel handling** via a syscall gate.
* Brings up an **early graphics pipeline** using **Vulkan** on Windows, Linux and
  macOS (via MoltenVK). Games that reach `sceVideoOut` / AGC submission can be
  captured; on Windows the backend creates a real swapchain, and on Linux/macOS it
  renders a proof image to confirm the pipeline is wired.
* Writes a detailed **debug log file** (`sharpemu.log` by default) for triage.

Most commercial titles will only reach early boot before hitting an instruction or
syscall we have not yet implemented. That is expected for an early research project.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build -c Release
# or publish a self-contained binary:
dotnet publish src/SharpEmu -c Release -r win-x64 --self-contained
```

## Running

```bash
# Run a decrypted game eboot.bin:
SharpEmu.exe "path/to/eboot.bin"

# Options:
#   --log <file>     debug log file (default: sharpemu.log)
#   --dump <dir>     directory for frame/debug dumps (default: dump)
#   --no-vulkan      disable Vulkan backend (headless)
#   --max-inst <n>   instruction execution budget
#   --selftest       run the built-in smoke test (no game required)

# Run the built-in self-test (verifies CPU + HLE wiring):
SharpEmu.exe --selftest
```

The self-test generates a tiny in-memory ELF, boots it through the real CPU
interpreter and HLE syscall gate, and reports `SELFTEST PASSED`.

## Project layout

```
src/SharpEmu.Core/      ELF loader, guest memory, x86-64 CPU interpreter, logger
src/SharpEmu.Libs/      HLE system modules (kernel, libc, fiber, ampr, videoout, agc)
src/SharpEmu.Graphics/  Vulkan video presenter + headless fallback
src/SharpEmu/           CLI entry point and top-level Emulator orchestration
```

## Legality & ethics

* SharpEmu contains **no** PlayStation firmware, **no** decryption keys, **no**
  game data, and **no** copyrighted assets.
* It only runs ELF/SELF binaries you are **legally permitted to possess** (e.g.
  your own dumps, or homebrew built for this research environment).
* It does not circumvent copy protection. Encrypted `.self` files will simply fail
  to load.
* Reverse-engineering for interoperability and research is protected in many
  jurisdictions; respect the laws that apply to you.

## Disclaimer

This software is provided "as is", without warranty of any kind. It is a research
notebook, not a path to playing commercial games.

Based on publicly documented formats (ELF, SCE program headers, PARAM.SFO) and the
general architecture described in community research. Inspired by the broader
PS5 emulation research effort.
