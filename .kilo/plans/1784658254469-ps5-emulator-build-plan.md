# PS5 Emulator — Implementation Plan

**Project:** PS5 research emulator (C#, .NET 8, cross-platform)
**Scope:** Early compatibility — boot, load, partial kernel, first frames via Vulkan/MoltenVK
**Tone:** Research/educational. Non-piracy. Runs only legally obtained dumps.

---

## Locked Decisions

| Decision | Choice |
|---|---|
| Codebase | Build from scratch in C# |
| CPU emulation | x86‑64 interpreter-first, with block-based JIT migration path |
| Graphics | HLE `libSceGnm` wrapper translating to host Vulkan |
| Platforms | Windows x64, Linux x64, macOS x64 (Rosetta 2 acceptable) |
| Vulkan path | Vulkan on Win/Linux, MoltenVK on macOS |
| Game container | Accepts `eboot.bin` (SELF) or raw `.elf` |
| Logging | Per-run log file with timestamp, structured text |
| License | GPL-2.0 |

---

## Architecture

```
Zenith/
├── src/
│   ├── Core/
│   │   ├── Cpu/              # x86-64 interpreter + JIT cache
│   │   ├── Memory/           # Guest physical/virtual memory maps
│   │   ├── Os/               # HLE kernel: threads, mutexes, syscalls
│   │   ├── Loader/           # ELF/SELF parser, module loader
│   │   ├── Modules/          # HLE sysmodule stubs (SceLibc, SceRtc, etc.)
│   │   └── Logging/          # File + console logger
│   ├── Gnm/                  # HLE libSceGnm / libSceGpuError stubs
│   ├                    ├── VulkanBackend/   # Device init, swapchain, pipeline
│   │   │   ├── Shader/       # AMD GCN/RDNA → SPIR-V translator (initial subset)
│   │   │   └── TextureCache/ # Hash-based invalidation, format view
│   ├                    ├── VideoOut/        # sceVideoOut HLE → window present
│   └── Host/
│       ├── Cli/              # Entry point, arg parsing
│       └── Platform/         # OS-specific windowing & Vulkan instance
└── tests/
    ├── CpuTests/
    ├── LoaderTests/
    └── GnmTests/
```

---

## Subsystem Contracts

### 1. Loader (`src/Core/Loader`)
- Parse ELF64 headers/phdr/shdr.
- Detect SELF wrapper; if encrypted, require pre-decrypted input or stub with clear error.
- Map segments into guest memory.
- Read PSF metadata (title, version, category) from `param.sfo` if present; else stub placeholders.
- Expose `GameInfo` record: `TitleId`, `Title`, `Version`, `Category`.

### 2. Memory (`src/Core/Memory`)
- Flat `ulong[]` or `Memory<byte>` for guest physical RAM (16 GB for research; configurable).
- x86-64 page tables: identity or simple 4-level paging emulation for paging-on-demand.
- TLB + fast read/write helpers.
- Memory-mapped I/O regions for GPU registers (stubbed).

### 3. CPU Interpreter (`src/Core/Cpu`)
- State: `X86_64_Registers` struct (GPRs, RIP, RFLAGS, XMM0-15, MXCSR).
- Fetch-Decode-Execute loop over 16-byte-aligned blocks.
- Decoder covers: mandatory base ISA, SSE2, AVX (decode only), `syscall`, `sysret`.
- Unsupported opcodes hit `UnsupportedOpcode` logger + infinite loop stub.
- `syscall` dispatches to `Os/SyscallHandler`.
- JIT cache: `ConcurrentDictionary<ulong, CompiledBlock>`; blocks compiled on second hit.

### 4. JIT Frontend (migration path)
- Same block boundaries as interpreter.
- Copy supported instruction bytes verbatim; replace unsupported with callbacks.
- No register allocation needed initially (guest registers map 1:1).
- Cache keyed by guest RIP; invalidate on self-modifying writes (optional v0.1).

### 5. Kernel HLE (`src/Core/Os`)
- Thread model: host `Thread` mapped to guest TLS via `fs` base.
- Syscall table stubbed for: `read`, `write`, `open`, `close`, `mmap`, `munmap`, `ioctl`, `gettimeofday`, `clock_gettime`, `pthread_*`.
- File I/O redirected to host filesystem under a `game_root/` folder.
- No security model enforcement (research build).

### 6. System Modules (`src/Core/Modules`)
HLE implementations for libraries games load early:
- `libSceSystemService`
- `libSceUserService`
- `libSceRtc`
- `libScePosix`
- `libSceGnm` → thin shim into `Gnm` namespace
- `libSceGpuError`

Each module exports a table of function pointers returning into guest code.

### 7. Graphics HLE (`src/Gnm`)
- `SceGnmDevice` → creates host Vulkan device/queue.
- `SceGnmCommandBuffer` → records into an internal list; flushes become Vulkan command buffers.
- Shader: accept AMD ISA blobs; hand-wave translation to SPIR-V for known vertex/fragment patterns (identity, passthrough) and log unknown.
- Texture: map common PS5 formats (BC1-7, RGBA8) to Vulkan equivalents.
- Window: win32 / X11 / Cocoa surface → `VkSurfaceKHR` → swapchain.
- Present loop runs on a dedicated host thread, driven by `sceVideoOut` flip requests.

### 8. CLI & Logging (`src/Host/Cli`, `src/Core/Logging`)
- `PS5Emu.exe <path/to/eboot.bin>` or `<path/to/game.elf>`.
- Output: `PS5Emu-<timestamp>.log` in emulator working directory.
- Log levels: Trace, Debug, Info, Warn, Error.
- Startup banner: emulator name, build config, host OS, Vulkan driver, target title.
- Crash handler: write stack + last 1000 log lines.

---

## Phase Order

### Phase 1 — Scaffold & Tooling (Week 1)
- Solution setup, CI matrix (Win/Linux/mac).
- Logger, CLI arg parser, `GameInfo` model.
- ELF parser + unit tests with synthetic binaries.

### Phase 2 — Memory & CPU Interpreter (Week 2-4)
- Flat memory + MMIO helpers.
- x86‑64 decoder for common opcodes.
- Fetch-decode-execute loop.
- `syscall` dispatch to host functions.
- Unit tests for representative instruction sequences.

### Phase 3 — SELF Loader & Modules (Week 5-6)
- SELF header parsing (unencrypted / decrypted only).
- Segment mapping and relocation.
- Module loader + HLE stubs for `libSce*`.
- Integration test: load small homebrew ELF.

### Phase 4 — Kernel HLE & First Boot (Week 7-9)
- Implement syscall stubs (file I/O, threads, time).
- TLS setup.
- Boot a known tiny test title to `main()` equivalent; log output.

### Phase 5 — Graphics Backend (Week 10-13)
- Vulkan instance/device/queue init across Win/Linux/macOS.
- `libSceGnm` HLE stubs.
- Passthrough shader translate (vertex → SPIR-V, fragment → SPIR-V).
- Swapchain + present.
- Integration: game reaches video output.

### Phase 6 — Polish & Debugging (Week 14-16)
- Block JIT compiler (direct re-emit).
- Shader/resource caching.
- Better error messages for unsupported opcodes/calls.
- Readme with build steps, legal notice, known limitations.

---

## Validation

- **Loader tests:** `param.sfo`, ELF64 segments, SELF headers.
- **CPU tests:** Decode/execute known x86-64 instruction sequences end-to-end.
- **Boot test:** Known legally dumped title boots past `main()` and logs game info.
- **Graphics test:** Titles that reach `sceVideoOut` present a frame to a window.
- **Cross-platform build:** `dotnet build` succeeds on Win, Linux, macOS CI runners.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| SELF encryption blocks loading | Require decrypted input; implement SELF header parsing but fail fast with clear message |
| Vulkan driver fragmentation | Pin minimum Vulkan 1.2; use validation layers in debug builds |
| x86-64 decoder coverage gaps | Interpreter logs unsupported opcodes and halts cleanly; JIT migration can target hot paths first |
| Self-modifying code / code-cache coherency | Ignore v0.1; add write-watch invalidation in v0.2 if needed |

---

## Out of Scope
- PS4 game compatibility (use ShadPS4).
- Cycle-accurate GPU or LLE PM4 command processor.
- Online/networking, saves, trophies, controller input.
- Full syscall coverage or process suspend/resume.
