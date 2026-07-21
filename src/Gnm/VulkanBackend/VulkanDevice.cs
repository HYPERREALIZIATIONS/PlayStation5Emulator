using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Zenith.Core.Logging;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;

namespace Zenith.Gnm.VulkanBackend;

public sealed class VulkanDevice : IDisposable
{
    private const uint QueueCount = 1;
    private readonly IView _window;
    private readonly Vk _vk;
    private readonly Instance _instance;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Queue _graphicsQueue;
    private readonly Queue _presentQueue;
    private readonly SurfaceKHR _surface;
    private readonly SurfaceTransformFlagsKHR _preTransform;
    private readonly Extent2D _swapchainExtent;
    private readonly SwapchainKHR _swapchain;
    private readonly Image[] _swapchainImages;
    private readonly ImageView[] _swapchainImageViews;
    private readonly Format _swapchainFormat = Format.B8G8R8A8Unorm;
    private readonly DescriptorPool _descriptorPool;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers;
    private readonly Semaphore _imageAvailable;
    private readonly Semaphore _renderFinished;
    private readonly Fence[] _inFlightFences;
    private readonly uint _swapchainImageCount;
    private bool _disposed;

    public uint SwapchainImageCount => _swapchainImageCount;
    public ReadOnlySpan<Image> SwapchainImages => _swapchainImages;
    public ReadOnlySpan<ImageView> SwapchainImageViews => _swapchainImageViews;
    public ReadOnlySpan<CommandBuffer> CommandBuffers => _commandBuffers;
    public Device Device => _device;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    public Queue GraphicsQueue => _graphicsQueue;
    public Queue PresentQueue => _presentQueue;
    public DescriptorPool DescriptorPool => _descriptorPool;
    public CommandPool CommandPool => _commandPool;
    public Extent2D Extent => _swapchainExtent;
    public Format Format => _swapchainFormat;

    public VulkanDevice(IView window)
    {
        _window = window;
        _vk = Vk.GetApi();

        var appName = Marshal.StringToHGlobalAnsi("Zenith");
        try
        {
            CreateInstance(appName);
        }
        finally { Marshal.FreeHGlobal(appName); }

        window.CreateSurface(_vk, (uint)_instance.Handle, out var surface);
        _surface = new SurfaceKHR(surface);
        _preTransform = SurfaceTransformFlagsKHR.IdentityBitKhr;

        var deviceExtensions = new List<string> { KhrSwapchain.ExtensionName };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            deviceExtensions.Add("VK_KHR_portability_subset");

        CreatePhysicalDevice(deviceExtensions);
        CreateLogicalDevice(deviceExtensions);
        GetQueues();
        CreateSwapchain();
        CreateCommandPool();
        CreateDescriptorPool();
        AllocateCommandBuffers();
        CreateSyncObjects();

        var capabilities = new SurfaceCapabilitiesKHR();
        _vk.GetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, ref capabilities);
        _swapchainImageCount = Math.Min(capabilities.MinImageCount + 1, capabilities.MaxImageCount);

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = _swapchainImageCount,
            ImageFormat = _swapchainFormat,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = _swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = _preTransform,
            CompositeAlpha = CompositeAlphaKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
        };

        _vk.CreateSwapchainKHR(_device, ref createInfo, null, out _swapchain);

        _vk.GetSwapchainImagesKHR(_device, _swapchain, (uint)_swapchainImageCount, out _swapchainImages);
        _swapchainImageViews = new ImageView[_swapchainImageCount];
        for (var i = 0; i < _swapchainImageCount; i++)
        {
            var ci = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            _vk.CreateImageView(_device, ref ci, null, out _swapchainImageViews[i]);
        }

        Log.Info($"Vulkan device ready. Swapchain images: {_swapchainImageCount}");
    }

    public unsafe uint AcquireNextImage(ulong timeout = ulong.MaxValue, Fence? fence = null)
    {
        var fi = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        _vk.CreateSemaphore(_device, ref fi, null, out _imageAvailable);

        if (_vk.AcquireNextImageKHR(_device, _swapchain, timeout, _imageAvailable, null) == Result.Success)
            return 0;

        return 0;
    }

    public void Submit(CommandBuffer cmd, ulong waitStage)
    {
        var si = new SemaphoreInfoKHR { SType = StructureType.SemaphoreInfoKhr, Semaphore = _imageAvailable };
        var si2 = new SemaphoreInfoKHR { SType = StructureType.SemaphoreInfoKhr, Semaphore = _renderFinished };
        var submits = new SubmitInfoKHR
        {
            SType = StructureType.SubmitInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &si,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &si2
        };

        _vk.QueueSubmit(_graphicsQueue, 1, ref submits, null);
    }

    public void Present(uint imageIndex)
    {
        var si = new SemaphoreInfoKHR { SType = StructureType.SemaphoreInfoKhr, Semaphore = _renderFinished };
        var pi = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &si,
            SwapchainCount = 1,
            PSwapchains = ref _swapchain,
            PImageIndices = ref imageIndex
        };

        _vk.QueuePresentKHR(_presentQueue, ref pi);
    }

    private unsafe void CreateInstance(nint appName)
    {
        var layers = new List<string>();
        var extensions = new List<string> { KhrSurface.ExtensionName, ExtDebugUtils.ExtensionName, "VK_EXT_swapchain_colorspace" };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) extensions.Add("VK_KHR_portability_enumeration");

        var ici = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApplicationVersion = 0x1000000,
                PEngineName = (byte*)Marshal.StringToHGlobalAnsi("Zenith"),
                EngineVersion = 0x1000000,
                ApiVersion = Vk.Version12
            },
            EnabledLayerCount = 0,
            PpEnabledLayerNames = [],
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = [.. extensions.Select(e => Marshal.StringToHGlobalAnsi(e)).ToArray()]
        };

        var debugCi = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                              DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                              DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                          DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                          DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)((severity, type, callbackData, userData) =>
            {
                var msg = Marshal.PtrToStringUTF8(callbackData->PMessage);
                Log.Debug($"[VK {severity}] {msg}");
                return false;
            })
        };

        fixed (Instance* instancePtr = &_instance)
            _vk.CreateInstance(ref ici, null, instancePtr);
    }

    private unsafe void CreatePhysicalDevice(IReadOnlyList<string> deviceExtensions)
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        var devices = new PhysicalDevice[count];
        _vk.EnumeratePhysicalDevices(_instance, ref count, devices);

        foreach (var dev in devices)
        {
            uint extCount = 0;
            _vk.EnumerateDeviceExtensionProperties(dev, null, ref extCount, null);
            var exts = new ExtensionProperties[extCount];
            _vk.EnumerateDeviceExtensionProperties(dev, null, ref extCount, exts);
            var extNames = exts.Select(e => Marshal.PtrToStringUTF8(e.ExtensionName)).ToHashSet();

            if (deviceExtensions.All(extNames.Contains))
            {
                _physicalDevice = dev;
                return;
            }
        }

        throw new NotSupportedException("No suitable physical device found");
    }

    private unsafe void CreateLogicalDevice(IReadOnlyList<string> deviceExtensions)
    {
        var priorities = new float[] { 1.0f };
        var qci = new QueueCreateInfo
        {
            SType = StructureType.QueueCreateInfo,
            QueueFamilyIndex = 0,
            QueueCount = (uint)priorities.Length,
            PQueuePriorities = priorities
        };

        var extPointers = deviceExtensions.Select(e => Marshal.StringToHGlobalAnsi(e)).ToArray();
        var dci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &qci,
            EnabledExtensionCount = (uint)extPointers.Length,
            PpEnabledExtensionNames = extPointers
        };

        fixed (Device* devicePtr = &_device)
            _vk.CreateDevice(_physicalDevice, ref dci, null, devicePtr);
    }

    private unsafe void GetQueues()
    {
        _vk.GetDeviceQueue(_device, 0, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, 0, 0, out _presentQueue);
    }

    private unsafe void CreateSwapchain()
    {
        var caps = new SurfaceCapabilitiesKHR();
        _vk.GetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, ref caps);

        uint modeCount = 0;
        _vk.GetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, _surface, ref modeCount, null);
        var modes = new PresentModeKHR[modeCount];
        _vk.GetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, _surface, ref modeCount, modes);

        var mode = modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;

        uint fmtCount = 0;
        _vk.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref fmtCount, null);
        var fmts = new SurfaceFormatKHR[fmtCount];
        _vk.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref fmtCount, fmts);

        var fmt = fmts.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm, fmts[0]);

        _swapchainExtent = new Extent2D(
            Math.Clamp(_window.Size.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
            Math.Clamp(_window.Size.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height)
        );
    }

    private unsafe void CreateCommandPool()
    {
        var cpci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = 0,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };

        fixed (CommandPool* poolPtr = &_commandPool)
            _vk.CreateCommandPool(_device, ref cpci, null, poolPtr);
    }

    private unsafe void CreateDescriptorPool()
    {
        var dpci = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1024,
            PoolSizeCount = 1,
            PPoolSizes = stackalloc[]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1024 }
            }
        };

        fixed (DescriptorPool* poolPtr = &_descriptorPool)
            _vk.CreateDescriptorPool(_device, ref dpci, null, poolPtr);
    }

    private unsafe void AllocateCommandBuffers()
    {
        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            CommandBufferCount = _swapchainImageCount
        };

        _commandBuffers = new CommandBuffer[_swapchainImageCount];
        fixed (CommandBuffer* bufPtr = _commandBuffers)
            _vk.AllocateCommandBuffers(_device, ref ai, bufPtr);
    }

    private unsafe void CreateSyncObjects()
    {
        var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        fixed (Semaphore* imgPtr = &_imageAvailable)
        fixed (Semaphore* renPtr = &_renderFinished)
        {
            _vk.CreateSemaphore(_device, ref sci, null, imgPtr);
            _vk.CreateSemaphore(_device, ref sci, null, renPtr);
        }

        _inFlightFences = new Fence[_swapchainImageCount];
        for (var i = 0; i < _swapchainImageCount; i++)
        {
            var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            fixed (Fence* fencePtr = &_inFlightFences[i])
                _vk.CreateFence(_device, ref fci, null, fencePtr);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyCommandPool(_device, _commandPool, null);
        foreach (var iv in _swapchainImageViews) _vk.DestroyImageView(_device, iv, null);
        _vk.DestroySwapchainKHR(_device, _swapchain, null);
        _vk.DestroySurfaceKHR(_instance, _surface, null);
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);
    }
}
