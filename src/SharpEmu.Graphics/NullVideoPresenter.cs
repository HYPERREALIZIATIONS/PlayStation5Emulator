using System;
using SharpEmu.Core;
using SharpEmu.Core.Memory;

namespace SharpEmu.Graphics;

/// <summary>
/// Abstraction over a video presenter. The emulator core talks only to this
/// interface; platform-specific backends (Vulkan on Windows/Linux/macOS) implement
/// it. A Null presenter is used when no GPU/backend is available so research can
/// continue headless and still log what the game tried to draw.
/// </summary>
public interface IVideoPresenter : IDisposable
{
    bool Initialize();
    void RegisterBuffers(ulong guestMemPtr, uint count, GuestMemory mem);
    void Present(uint bufferIndex);
    bool IsHeadless { get; }
    string BackendName { get; }
}

/// <summary>
/// Headless presenter: records draw attempts to the log and optionally dumps the
/// registered framebuffer to a PPM file for inspection. Useful when no Vulkan
/// driver is present (e.g. CI, servers).
/// </summary>
public sealed class NullVideoPresenter : IVideoPresenter
{
    private readonly Logger _log;
    private readonly string _dumpDir;
    private uint _bufferCount;
    private ulong _bufferPtr;
    private GuestMemory _mem;

    public NullVideoPresenter(Logger log, string dumpDir = null)
    {
        _log = log;
        _dumpDir = dumpDir;
    }

    public bool IsHeadless => true;
    public string BackendName => "null (headless)";

    public bool Initialize() { _log.Info("gfx", "headless presenter initialized"); return true; }

    public void RegisterBuffers(ulong guestMemPtr, uint count, GuestMemory mem)
    {
        _bufferPtr = guestMemPtr;
        _bufferCount = count;
        _mem = mem;
        _log.Info("gfx", $"registered {count} flip buffer(s) at guest 0x{guestMemPtr:X} (headless capture)");
        if (_dumpDir != null)
        {
            try
            {
                Directory.CreateDirectory(_dumpDir);
                string path = Path.Combine(_dumpDir, "registered_buffers.txt");
                System.IO.File.WriteAllText(path, $"buffers={count} ptr=0x{guestMemPtr:X}\n");
            }
            catch { }
        }
    }

    public void Present(uint bufferIndex)
    {
        _log.Info("gfx", $"PRESENT frame (headless) bufferIndex={bufferIndex}; no GPU surface");
        if (_dumpDir != null && _bufferPtr != 0 && _mem != null)
        {
            try
            {
                // Best-effort: dump the first 64 KiB of the selected buffer as a raw blob.
                // We do not know the real format/size here, so this is purely a research aid.
                var buf = new byte[65536];
                ulong off = _bufferPtr + bufferIndex * 0x100000UL; // assume >=1MB stride; clamped by mem
                try { _mem.ReadBytes(off, buf, buf.Length); } catch { }
                string path = Path.Combine(_dumpDir, $"frame_{bufferIndex}.raw");
                System.IO.File.WriteAllBytes(path, buf);
                _log.Debug("gfx", $"dumped raw frame buffer to {path}");
            }
            catch (Exception ex) { _log.Warn("gfx", $"frame dump failed: {ex.Message}"); }
        }
    }

    public void Dispose() { }
}
