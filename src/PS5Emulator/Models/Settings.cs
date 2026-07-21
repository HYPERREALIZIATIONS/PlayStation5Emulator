using System.Collections.Generic;

namespace PS5Emulator.Models;

public class Settings
{
    public string LogPath { get; set; } = "emulator.log";
    public ulong MemorySizeMB { get; set; } = 8192;
    public bool Headless { get; set; }

    public ulong MemorySizeBytes => MemorySizeMB * 1024UL * 1024UL;

    public static Settings ParseArgs(string[] args)
    {
        var settings = new Settings();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--log" && i + 1 < args.Length)
            {
                settings.LogPath = args[++i];
            }
            else if (args[i] == "--memory" && i + 1 < args.Length)
            {
                if (ulong.TryParse(args[++i], out var mb))
                {
                    settings.MemorySizeMB = mb > 512 ? mb : 8192;
                }
            }
            else if (args[i] == "--no-vulkan")
            {
                settings.Headless = true;
            }
        }
        return settings;
    }
}
