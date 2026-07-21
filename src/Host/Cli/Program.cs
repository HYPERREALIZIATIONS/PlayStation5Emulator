using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Zenith.Core.Logging;
using Zenith.Core.Memory;
using Zenith.Core.Os;
using Zenith.Core.Loader;

namespace Zenith.Host.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            {
                Console.WriteLine("Zenith - PS5 Research Emulator (C#)");
                Console.WriteLine("Usage: Zenith <path/to/eboot.bin>");
                Console.WriteLine();
                return 1;
            }

            var targetPath = args[0];
            if (!File.Exists(targetPath))
            {
                Console.Error.WriteLine($"File not found: {targetPath}");
                return 1;
            }

            var logPath = Path.Combine(
                Environment.CurrentDirectory,
                $"Zenith-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

            Log.SetGlobal(new FileLogger(logPath));
            Log.Info("=== Zenith PS5 Emulator ===");
            Log.Info($"Target: {targetPath}");
            Log.Info($"Host: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
            Log.Info($"Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            Log.Info($"Log: {logPath}");

            Config.EnsureInitialized();
            Config.SetString("Vulkan.Backend", RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "MoltenVK" : "Vulkan");

            var memory = new MemoryManager(Config.UInt64("Memory.GuestRamSize"));
            var syscallHandler = new SyscallHandler(memory);
            var loader = new SelfLoader(memory, syscallHandler);
            var (info, entry, stackTop) = await loader.LoadAsync(targetPath);

            Log.Info($"Title: {info.Title} [{info.TitleId}] v{info.Version}");
            Log.Info("Press Ctrl+C to stop.");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var cpu = new Cpu.Interpreter(memory, syscallHandler);
            cpu.Run(entry, stackTop);

            Log.Info("Emulation stopped normally.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal: {ex.Message}");
            Log.Fatal($"Fatal exception: {ex}");
            return 1;
        }
    }
}
