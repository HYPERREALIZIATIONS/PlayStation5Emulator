using System;
using System.IO;
using System.Threading.Tasks;
using PS5Emulator.Logging;
using PS5Emulator.Models;
using PS5Emulator.Emulation;

if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
{
    Console.WriteLine("PS5Emulator - Educational/Research PlayStation 5 Emulator");
    Console.WriteLine();
    Console.WriteLine("Usage: PS5Emulator <path-to-eboot.bin> [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --help, -h              Show this help message");
    Console.WriteLine("  --log <path>            Set log file path (default: emulator.log in cwd)");
    Console.WriteLine("  --memory <size>MB       Set system RAM in MB (default: 8192)");
    Console.WriteLine("  --no-vulkan             Disable Vulkan window (headless mode)");
    Console.WriteLine();
    Console.WriteLine("NOTICE: This is an educational, research-only prototype.");
    Console.WriteLine("Do not use with copyrighted material you do not own.");
    return;
}

try
{
    if (!File.Exists(args[0]))
    {
        Console.Error.WriteLine($"Error: File not found: {args[0]}");
        return;
    }

    var settings = Settings.ParseArgs(args);
    Logger.Initialize(settings.LogPath);
    Logger.Info("Program", "PS5Emulator starting up");
    Logger.Info("Program", $"Target file: {Path.GetFullPath(args[0])}");
    Logger.Info("Program", $"Settings: Memory={settings.MemorySizeMB}MB, Headless={settings.Headless}, Log={settings.LogPath}");

    using var emulator = new Emulator(settings);
    await emulator.RunAsync(args[0]);

    Logger.Info("Program", "Emulation session ended.");
}
catch (Exception ex)
{
    Logger.Fatal("Program", $"Unhandled exception: {ex}");
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    Console.Error.WriteLine("See log file for details.");
}
