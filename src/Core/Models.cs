namespace Zenith.Core.Models;

public sealed record GameInfo
{
    public required string TitleId { get; init; }
    public required string Title { get; init; }
    public string Version { get; init; } = "01.00";
    public string Category { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsElf { get; init; }
    public bool IsSelf { get; init; }
}

public sealed class Ps5SystemConfig
{
    public ulong GuestRamSize { get; } = 16UL * 1024 * 1024 * 1024;
    public uint CpuCoreCount { get; } = 8;
    public uint GpuComputeUnits { get; } = 36;
    public string VulkanApiVersion { get; } = "VK_API_VERSION_1_2";
}
