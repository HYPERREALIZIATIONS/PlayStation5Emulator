using System;
using System.Runtime.InteropServices;

namespace SharpEmu.Graphics.Vulkan;

/// <summary>
/// Minimal, self-contained P/Invoke surface to the Vulkan loader. Only the subset
/// required to construct an instance, a logical device, a swapchain, and submit a
/// clear-and-present is bound here. This is enough to bring up the "early graphics
/// pipeline" on Windows, Linux and macOS (via MoltenVK) and let a handful of games
/// reach their first presented frame. It is intentionally not a full Vulkan wrapper.
///
/// The loader shared library name differs per OS (vulkan-1.dll / libvulkan.so.1 /
/// libvulkan.dylib). A DllImport resolver maps the neutral "vulkan-1" token to the
/// right file on each platform so the same binding works everywhere.
/// </summary>
internal static unsafe class VulkanNative
{
    private const string Neutral = "vulkan-1";

    static VulkanNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(VulkanNative), (name, assembly, searchPath) =>
        {
            if (name != Neutral)
                return IntPtr.Zero;

            string lib = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vulkan-1.dll"
                       : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libvulkan.dylib"
                       : "libvulkan.so.1";

            if (NativeLibrary.TryLoad(lib, assembly, searchPath, out var handle))
                return handle;
            // Fall back to the bare token in case the system provides it.
            return NativeLibrary.TryLoad(name, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
        });
    }

    [DllImport(Neutral, CallingConvention = CallingConvention.Winapi, EntryPoint = "vkGetInstanceProcAddr")]
    public static extern IntPtr GetInstanceProcAddr(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Neutral, CallingConvention = CallingConvention.Winapi, EntryPoint = "vkCreateInstance")]
    public static extern VkResult CreateInstance(in VkInstanceCreateInfo pCreateInfo, IntPtr pAllocator, out IntPtr pInstance);

    [DllImport(Neutral, CallingConvention = CallingConvention.Winapi, EntryPoint = "vkEnumeratePhysicalDevices")]
    public static extern VkResult EnumeratePhysicalDevices(IntPtr instance, ref uint pPhysicalDeviceCount, IntPtr pPhysicalDevices);

    public static IntPtr Resolve(IntPtr instance, string name) => GetInstanceProcAddr(instance, name);

    public enum VkResult
    {
        Success = 0,
        NotReady = 1,
        Timeout = 2,
        EventSet = 3,
        EventReset = 4,
        Incomplete = 5,
        ErrorOutOfHostMemory = -1,
        ErrorOutOfDeviceMemory = -2,
        ErrorInitializationFailed = -3,
        ErrorDeviceLost = -4,
        ErrorSurfaceLostKHR = -1000000000,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkApplicationInfo
    {
        public IntPtr sType;
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public uint applicationVersion;
        public IntPtr pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkInstanceCreateInfo
    {
        public IntPtr sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceQueueCreateInfo
    {
        public IntPtr sType;
        public IntPtr pNext;
        public uint queueFamilyIndex;
        public uint queueCount;
        public IntPtr pQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceCreateInfo
    {
        public IntPtr sType;
        public IntPtr pNext;
        public uint flags;
        public uint queueCreateInfoCount;
        public IntPtr pQueueCreateInfos;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
        public IntPtr pEnabledFeatures;
    }
}
