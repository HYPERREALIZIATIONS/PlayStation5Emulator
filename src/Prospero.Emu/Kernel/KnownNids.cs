using System;
using System.Collections.Generic;

namespace Prospero.Emu.Kernel
{
    /// <summary>
    /// A research database of known PS5 (Prospero) / Orbis NIDs for the common
    /// system libraries (libkernel, libc, libSceFios2, libSceVideoOut, etc.).
    ///
    /// The NID is the FNV-1 hash of the function name. We include a curated set
    /// of well-known NIDs (from public OpenOrbis / homebrew documentation) so
    /// that logging and syscall triage can name functions. Unknown NIDs are
    /// still handled gracefully (no-op stub returning 0 / success).
    ///
    /// This is strictly for research and compatibility triage. No proprietary
    /// firmware or keys are involved.
    /// </summary>
    public static class KnownNids
    {
        // A small, curated table. Many more exist; this covers the ones most
        // likely to appear during early boot / AGC init / first frames.
        private static readonly Dictionary<ulong, string> _map = new()
        {
            // libkernel / libc basics
            { 0x035EAD33u, "sceKernelGetCurrentProcess" },
            { 0xEF092AACu, "sceKernelGetProcParam" },
            { 0x7D37B4E8u, "sceKernelAllocateDirectMemory" },
            { 0xA186B7E0u, "sceKernelMapDirectMemory" },
            { 0x2C69DD61u, "sceKernelAllocateMemoryBlock" },
            { 0xAFBB2A3Eu, "sceKernelGetMemoryBlockBase" },
            { 0xCD3C83E0u, "sceKernelExit" }, // approximate
            { 0x0DEB17A8u, "sceKernelExitProcess" },
            { 0xCD1C8FEBu, "sceKernelCreateThread" },
            { 0x1C8C5364u, "sceKernelStartThread" },
            { 0x5FE8684Eu, "sceKernelWaitThreadEnd" },
            { 0x689C8FF3u, "sceKernelGetThreadId" },
            { 0x35DAE69E, "sceKernelUsleep" },
            { 0x4C9A9E8C, "sceKernelGetCurrentThread" },
            { 0x6F0CABA6, "sceKernelGetErrorMessage" },

            // video out / GPU (AGC) init typically needs these
            { 0x7FE4B4C8, "sceVideoOutOpen" },
            { 0xEBEA6978, "sceVideoOutClose" },
            { 0xCD3286AB, "sceVideoOutRegisterBuffers" },
            { 0x32A1BE23, "sceVideoOutSetFlipRate" },
            { 0xCB3C7E4C, "sceVideoOutSubmitFlip" },
            { 0x8C7C2E9B, "sceVideoOutGetState" },

            // file IO (fios2)
            { 0x40A82AB3, "sceFios2KernelOpen" },
            { 0x00C1B51D, "sceFios2KernelClose" },
            { 0x8C0B42C6, "sceFios2KernelRead" },
            { 0x6E11C969, "sceFios2KernelWrite" },
            { 0x5B8B5B19, "sceFios2KernelLseek" },
            { 0xFD6C0C29, "sceFios2KernelStat" },
            { 0x39C1BB13, "sceFios2KernelOpen2" },

            // GNM / GCN graphics (libSceGnm)
            { 0x8F6FC354, "sceGnmCreateGpuMemory" },
            { 0xAD2D4BA0, "sceGnmDestroyGpuMemory" },
            { 0xBA9C4B1B, "sceGnmSubmitCommandBuffers" },
            { 0x5C1C0A93, "sceGnmGetAGCInterface" },
            { 0x8FE1B9E8, "sceGnmSetAgcFrequency" },
            { 0x9E7C7C0B, "sceGnmInitDefaultGpuMemory" },

            // system modules
            { 0x5C0D8A37, "sceSysmoduleLoadModule" },
            { 0x4FC36FCE, "sceSysmoduleUnloadModule" },
            { 0x42F6E558, "sceSysmoduleIsLoaded" },
        };

        public static string Resolve(ulong nid) =>
            _map.TryGetValue(nid & 0xFFFFFFFF, out var name) ? name : $"nid_0x{nid:X8}";

        public static bool TryResolve(ulong nid, out string name) =>
            _map.TryGetValue(nid & 0xFFFFFFFF, out name!);

        public static IEnumerable<ulong> All => _map.Keys;
    }

    /// <summary>
    /// Result of resolving a stub: a behavior used by the kernel when the guest
    /// calls through the PLT to an imported function.
    /// </summary>
    public enum StubBehavior
    {
        ReturnSuccess,   // RAX = 0
        ReturnHandle,    // RAX = a synthetic non-zero handle
        ReturnError,     // RAX = negative errno
        ReturnPointer,   // RAX = a guest pointer to scratch memory
        NoOp,
    }
}
