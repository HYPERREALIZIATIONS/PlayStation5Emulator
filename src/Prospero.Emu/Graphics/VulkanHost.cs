using System;
using System.Runtime.InteropServices;
using Prospero.Emu.Core;

namespace Prospero.Emu.Graphics
{
    /// <summary>
    /// Host Vulkan renderer.
    ///
    /// PS5 targets Vulkan-style semantics on the host for research. We load the
    /// Vulkan loader dynamically:
    ///   - Windows: vulkan-1.dll
    ///   - Linux:   libvulkan.so.1
    ///   - macOS:   libvulkan.dylib (MoltenVK)
    ///
    /// If no Vulkan loader/ICD is available we degrade gracefully to a headless
    /// renderer that still records "frame" milestones for the debug log. This
    /// keeps the emulator runnable on machines without a GPU/Vulkan while still
    /// implementing the early-graphics control flow.
    /// </summary>
    public sealed class VulkanHost
    {
        private readonly Logger _log;
        public GuestMemory Mem { get; }
        private IntPtr _lib;
        public bool Available { get; private set; }

        public VulkanHost(Logger log, GuestMemory mem)
        {
            _log = log;
            Mem = mem;
        }

        private static string LibName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "vulkan-1.dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libvulkan.dylib";
            return "libvulkan.so.1";
        }

        public void Init()
        {
            string name = LibName();
            try
            {
                _lib = NativeLibrary.TryLoad(name, out var h) ? h : IntPtr.Zero;
                if (_lib == IntPtr.Zero)
                {
                    _log.Warn("vk", $"Could not load {name}; using headless renderer.");
                    Available = false;
                    return;
                }
                // Probe a core function to confirm the loader is usable.
                if (NativeLibrary.TryGetExport(_lib, "vkGetInstanceProcAddr", out _))
                {
                    Available = true;
                    _log.Info("vk", $"Loaded {name} successfully (host Vulkan available).");
                }
                else
                {
                    Available = false;
                    _log.Warn("vk", $"{name} loaded but missing vkGetInstanceProcAddr; headless.");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("vk", $"Vulkan init failed ({ex.Message}); headless renderer.");
                Available = false;
            }
        }

        /// <summary>
        /// Present a research "dummy frame". With a real Vulkan device we would
        /// build a swapchain image and blit; for research we record the milestone
        /// and (optionally) create a trivial instance to prove the path works.
        /// </summary>
        public void PresentDummyFrame(int frameIndex)
        {
            if (Available)
                _log.Trace("vk", $"presenting frame #{frameIndex} via host Vulkan (research stub)");
            else
                _log.Trace("vk", $"frame #{frameIndex} (headless, no Vulkan device)");
        }

        public void Shutdown()
        {
            if (_lib != IntPtr.Zero)
            {
                try { NativeLibrary.Free(_lib); } catch { /* ignore */ }
                _lib = IntPtr.Zero;
            }
        }
    }
}
