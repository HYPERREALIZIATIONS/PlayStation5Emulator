using System;
using System.IO;
using Prospero.Emu.Core;
using Prospero.Emu.Cpu;

namespace Prospero.Emu.Graphics
{
    /// <summary>
    /// AGC (AMD Graphics Core on PS5) - early graphics pipeline research.
    ///
    /// On PS5 the GPU is a custom RDNA 2 part ("Oberon"). The userland talks to
    /// it through libSceGnm / AGC, building command buffers that the driver
    /// submits. For research we model:
    ///   - AGC init / interface acquisition (stub returning a synthetic iface)
    ///   - GNF texture loading (parse the GNF header and read the data)
    ///   - Command buffer submission: we *parse* a subset of PM4-style packets
    ///     so we can detect "draw init", shader/resource setup, and "first
    ///     frame" milestones, then hand the frame to the host Vulkan renderer.
    ///
    /// Host rendering uses Vulkan on Windows, Linux, and macOS (MoltenVK). If a
    /// Vulkan loader/ICD is not available we fall back to a headless renderer
    /// that still records milestones for the debug log.
    /// </summary>
    public sealed class Agc
    {
        private readonly Logger _log;
        private readonly VulkanHost _vk;
        private bool _initialized;
        private int _framesSubmitted;

        public Agc(Logger log, VulkanHost vk)
        {
            _log = log;
            _vk = vk;
        }

        public void Init()
        {
            if (_initialized) return;
            _log.Info("agc", "Initializing AGC (early graphics pipeline)...");
            _vk.Init();
            _initialized = true;
            _log.Info("agc", "AGC ready. Host renderer: " + (_vk.Available ? "Vulkan" : "headless"));
        }

        // ---- syscall-facing helpers (called by PartialKernel) ----

        public ulong GetAgcInterface(CpuCore cpu, ulong outPtr)
        {
            _log.Debug("agc", "sceGnmGetAGCInterface");
            if (outPtr != 0)
            {
                // Write a synthetic AGC interface struct (version + function ptrs).
                _vk.Mem.WriteU64(outPtr, 0x1); // version
                _vk.Mem.WriteU64(outPtr + 8, 0x0); // function table (stub)
            }
            return 0;
        }

        public ulong SubmitCommandBuffers(CpuCore cpu, int count, ulong ptr)
        {
            if (!_initialized) Init();
            _framesSubmitted++;
            _log.Info("agc", $"SubmitCommandBuffers(count={count}, ptr=0x{ptr:X}) [submit #{_framesSubmitted}]");

            for (int i = 0; i < count; i++)
            {
                ulong cb = _vk.Mem.ReadU64(ptr + (ulong)(i * 8));
                ParseCommandBuffer(cb);
            }

            // Hand a (research) frame to the host renderer.
            _vk.PresentDummyFrame(_framesSubmitted);
            return 0;
        }

        public ulong VideoOutOpen(CpuCore cpu, ulong p1, ulong p2, ulong p3, ulong p4)
        {
            _log.Debug("agc", "sceVideoOutOpen -> synthetic handle");
            return 0x5000_0001;
        }

        public ulong VideoOutRegisterBuffers(CpuCore cpu, ulong vo, ulong startIndex, ulong addr, ulong num, ulong p5, ulong p6)
        {
            _log.Debug("agc", $"sceVideoOutRegisterBuffers(vo=0x{vo:X}, start={startIndex})");
            return 0;
        }

        public ulong VideoOutSubmitFlip(CpuCore cpu, ulong vo, ulong a, ulong b, ulong c)
        {
            _log.Debug("agc", $"sceVideoOutSubmitFlip(vo=0x{vo:X}) -> frame flip");
            _vk.PresentDummyFrame(_framesSubmitted);
            return 0;
        }

        /// <summary>
        /// Parse a single command buffer (PM4-ish). We detect milestone packets
        /// so the log can show how far a title gets (resource setup, first draw).
        /// </summary>
        private void ParseCommandBuffer(ulong cb)
        {
            if (cb == 0) return;
            _log.Debug("agc", $"  parsing command buffer @ 0x{cb:X}");
            // Read up to a few hundred dwords for research.
            int maxDwords = 512;
            for (int i = 0; i < maxDwords; i++)
            {
                ulong dword = _vk.Mem.ReadU32(cb + (ulong)(i * 4));
                if (dword == 0 && i > 4) break; // likely end-of-buffer padding
                ushort type = (ushort)(dword >> 30);
                if (type == 3) // type-3 PM4 packet
                {
                    ushort opcode = (ushort)((dword >> 8) & 0x3FF);
                    switch (opcode)
                    {
                        case 0x10: _log.Trace("agc", "    PKT3: NOP"); break;
                        case 0x3F: _log.Info("agc", "    PKT3: DRAW_INDEX detected (GPU draw)"); break;
                        case 0x36: _log.Info("agc", "    PKT3: DRAW_INDEX_AUTO detected (GPU draw)"); break;
                        case 0x5A: _log.Info("agc", "    PKT3: SET_SHADER (shader/resource setup)"); break;
                        case 0x73: _log.Info("agc", "    PKT3: SET_CONTEXT_REG (resource setup)"); break;
                        case 0x9F: _log.Info("agc", "    PKT3: EVENT_WRITE (pipeline event)"); break;
                        default: _log.Trace("agc", $"    PKT3 opcode 0x{opcode:X2}"); break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Minimal GNF (GNM texture file) header parser. GNF files store textures
    /// used by PS5 games; they begin with a 0x14-byte header ('GNF ' magic) then
    /// a version + texture info. We read the dimensions for research logging.
    /// </summary>
    public sealed class GnfTexture
    {
        public uint Magic;
        public uint Version;
        public uint DataSize;
        public uint Width;
        public uint Height;
        public uint Format;

        public static GnfTexture? Parse(byte[] data, Logger log)
        {
            if (data.Length < 0x14) return null;
            uint magic = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
            if (magic != 0x20464E47) // 'GNF '
            {
                log.Trace("gnf", $"not a GNF file (magic 0x{magic:X8})");
                return null;
            }
            var t = new GnfTexture
            {
                Magic = magic,
                Version = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24)),
            };
            // Texture info word at offset 0x10 encodes dimensions (documented GNF layout).
            uint info = (uint)(data[0x10] | (data[0x11] << 8) | (data[0x12] << 16) | (data[0x13] << 24));
            t.Width = (info & 0xFFFF) + 1;
            t.Height = ((info >> 16) & 0xFFFF) + 1;
            log.Info("gnf", $"GNF texture: version=0x{t.Version:X} size=0x{data.Length:X} {t.Width}x{t.Height}");
            return t;
        }
    }
}
