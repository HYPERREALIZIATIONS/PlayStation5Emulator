# Zenith

**Zenith is an early-stage, research-only PlayStation 5 emulator written in C#. It is not stable, it is not complete, and it is not ready for daily use.**

Right now this project is a collection of moving parts—an ELF/SELF loader, an x86-64 interpreter, partial syscall stubs, a Vulkan backend scaffold, and a lot of logging. Some games barely get past the logo. Most games crash immediately. Nothing is guaranteed to work. If you are looking for something you can sit down and play, this is not it yet.

That said, the goal is real. We are building this from scratch to understand the PS5 stack, reverse-engineer the boot flow, and gradually chip away at compatibility. Every broken test and every logged unimplemented opcode is progress.

---

## Legal stance

This project does **not** support or condone piracy.

You must own a PlayStation 5 and dump your own legally purchased games. Distributing or downloading copyrighted game files, system firmware, or proprietary Sony assets is illegal and explicitly discouraged here.

If you do not have a legal dump, this project is not for you.

---

## Requirements

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Vulkan SDK](https://vulkan.lunarg.com/) on Windows or Linux
- MoltenVK on macOS
- Vulkan-capable GPU with current drivers
- 16 GB RAM minimum (32 GB recommended)

---

## Build

```bash
dotnet build
```

## Run from source

```bash
dotnet run --project src/Host/Host.csproj -- "/path/to/your/eboot.bin"
```

A timestamped log file is written to the working directory.

---

## Beta releases

Automated CI builds are attached to every commit that lands on `main`.

They are tagged as pre-releases, stripped of stability promises, and shipped purely so contributors and testers can pull artifacts without building locally.

Do not expect them to last more than a few minutes before the next crash.

---

## Known limitations

- No working audio
- No save-state or rewind
- No DualSense input, haptics, or light bar
- No online, PSN, or matchmaking
- Incomplete x86-64 decoder (most games hit an unsupported opcode quickly)
- No shader recompiler yet (graphics output is theoretical)
- No texture cache
- No SELF decryption (pre-decrypted ELFs only)
- Very partial kernel ABI coverage
- No test suite worth mentioning

---

## Contributing

See `CONTRIBUTING.md`. Pull requests are welcome, but please match the existing C# style and keep changes small. This is a learning project—clarity over cleverness.
