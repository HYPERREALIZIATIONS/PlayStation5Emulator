using System.Collections.Concurrent;

namespace Zenith.Host.Platform;

public static class Config
{
    private static readonly ConcurrentDictionary<string, string> Values = new();

    public static void EnsureInitialized()
    {
        Values["Memory.GuestRamSize"] = "17179869184";
        Values["Cpu.CoreCount"] = "8";
        Values["Vulkan.Backend"] = "Vulkan";
    }

    public static void SetString(string key, string value) => Values[key] = value;
    public static string GetString(string key) => Values.TryGetValue(key, out var v) ? v : string.Empty;
    public static ulong UInt64(string key) => ulong.TryParse(GetString(key), out var v) ? v : 0;
    public static uint UInt32(string key) => uint.TryParse(GetString(key), out var v) ? v : 0;
    public static bool Bool(string key) => bool.TryParse(GetString(key), out var v) && v;
}
