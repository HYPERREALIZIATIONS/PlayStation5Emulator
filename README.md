# Zenith

Zenith is an experimental PlayStation 5 emulator written in C# (.NET 8), targeting Windows x64, Linux x64, and macOS x64.

It is developed purely for research and educational purposes. There are no commercial goals.

## Status

Early development. The current focus is accuracy and infrastructure setup rather than game-specific compatibility.

Current capabilities include:
- Loading `eboot.bin` and `.elf` files
- Executing native x86-64 instructions via interpreter
- Reading basic game metadata
- Partial kernel syscall handling
- Logging to a per-run log file
- Vulkan hardware backend scaffold (cross-platform via Silk.NET)

## Legal

This project does **not** support or condone piracy.

All games used during development and testing must be legally obtained—dumped from a console you own. Users are expected to use only legally obtained copies of their games.

This project does not contain copyrighted system firmware, game data, or proprietary PlayStation assets.

## Requirements

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Vulkan SDK](https://vulkan.lunarg.com/) (Windows / Linux)
- MoltenVK (provided on macOS via package or build)
- Vulkan-capable GPU and current graphics driver

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run --project src/Host/Host.csproj -- "/path/to/eboot.bin"
```

A log file will be written to the working directory.
