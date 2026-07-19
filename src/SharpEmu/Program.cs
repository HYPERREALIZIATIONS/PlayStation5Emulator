using System;
using System.IO;
using SharpEmu.Core;
using SharpEmu.Core.Loader;

namespace SharpEmu;

/// <summary>
/// Command-line entry point. Usage:
///   SharpEmu &lt;path-to-eboot.bin&gt; [--log &lt;file&gt;] [--no-vulkan] [--dump &lt;dir&gt;] [--max-inst &lt;n&gt;]
///   SharpEmu --selftest   (runs the built-in smoke test)
///
/// This is an experimental, research-focused PlayStation 5 emulator. It loads a
/// legally obtained, decrypted game ELF (eboot.bin or .elf), reads basic metadata,
/// executes native x86-64 instructions via an interpreter, resolves system-module
/// imports through HLE stubs, and brings up an early Vulkan graphics pipeline.
///
/// It is NOT a piracy tool. It contains no firmware, keys, or game data, and it
/// only runs dumps you are legally permitted to possess. Most commercial titles
/// will not progress past early boot; that is the nature of early research.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        PrintBanner();

        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "/?")
        {
            PrintUsage();
            return 0;
        }

        string eboot = null;
        string logPath = "sharpemu.log";
        string dumpDir = "dump";
        bool useVulkan = true;
        int maxInst = 50_000_000;
        bool selfTest = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log": logPath = args[++i]; break;
                case "--dump": dumpDir = args[++i]; break;
                case "--no-vulkan": useVulkan = false; break;
                case "--max-inst": maxInst = int.Parse(args[++i]); break;
                case "--selftest": selfTest = true; break;
                default:
                    if (eboot == null && !args[i].StartsWith("--")) eboot = args[i];
                    else { PrintUsage(); return 1; }
                    break;
            }
        }

        if (selfTest)
            return RunSelfTest(logPath, dumpDir, useVulkan, maxInst);

        if (string.IsNullOrEmpty(eboot))
        {
            Console.Error.WriteLine("error: no eboot.bin path provided");
            PrintUsage();
            return 1;
        }
        if (!File.Exists(eboot))
        {
            Console.Error.WriteLine($"error: file not found: {eboot}");
            return 1;
        }

        Directory.CreateDirectory(dumpDir);

        using var logger = new Logger(logPath, echoConsole: true, minLevel: Logger.Level.Info);
        EmulatorDiagnostics.Log = logger;

        logger.Info("main", $"SharpEmu started; log -> {logPath}");
        logger.Info("main", $"target: {Path.GetFullPath(eboot)}");

        try
        {
            using var emu = new Emulator(logger);
            bool ok = emu.Initialize(eboot, dumpDir, useVulkan);
            if (!ok)
            {
                logger.Error("main", "initialization failed; see log for details");
                return 2;
            }

            var m = emu.Metadata;
            Console.WriteLine();
            Console.WriteLine("  Game metadata:");
            Console.WriteLine($"    Title   : {m.Title}");
            Console.WriteLine($"    Version : {m.Version}");
            Console.WriteLine($"    Title ID: {m.TitleId}");
            Console.WriteLine($"    SDK     : PS5=0x{m.SdkVersionPs5:X8} PS4=0x{m.SdkVersionPs4:X8}");
            Console.WriteLine();

            emu.Run(maxInst);
            logger.Info("main", "done. debug log written to " + logPath);
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("main", $"fatal: {ex}");
            return 3;
        }
    }

    /// <summary>
    /// Generates a minimal sample ELF, boots it, and verifies the CPU interpreter
    /// and HLE syscall gate work end-to-end. Returns 0 on success.
    /// </summary>
    private static int RunSelfTest(string logPath, string dumpDir, bool useVulkan, int maxInst)
    {
        Directory.CreateDirectory(dumpDir);
        using var logger = new Logger(logPath, echoConsole: true, minLevel: Logger.Level.Info);
        EmulatorDiagnostics.Log = logger;

        logger.Info("selftest", "building sample ELF...");
        byte[] elf = SampleElfGenerator.Build();
        string tmp = Path.Combine(Path.GetTempPath(), "sharpemu_selftest.elf");
        File.WriteAllBytes(tmp, elf);
        logger.Info("selftest", $"wrote sample ELF ({elf.Length} bytes) to {tmp}");

        using var emu = new Emulator(logger);
        bool ok = emu.Initialize(tmp, dumpDir, useVulkan, selfTest: true);
        if (!ok)
        {
            logger.Error("selftest", "init failed");
            return 2;
        }

        emu.Run(maxInst);

        bool pass = emu.LastRunOk && emu.Metadata != null;
        logger.Info("selftest", pass ? "SELFTEST PASSED" : "SELFTEST FAILED");
        Console.WriteLine(pass ? "SELFTEST PASSED" : "SELFTEST FAILED");
        return pass ? 0 : 1;
    }

    private static void PrintBanner()
    {
        Console.WriteLine("SharpEmu - experimental PlayStation 5 research emulator (C#)");
        Console.WriteLine("Educational / non-commercial. No firmware, keys, or game data included.");
        Console.WriteLine();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: SharpEmu <path-to-eboot.bin|game.elf> [options]");
        Console.WriteLine("       SharpEmu --selftest");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --log <file>       Debug log file path (default: sharpemu.log)");
        Console.WriteLine("  --dump <dir>       Directory for frame/debug dumps (default: dump)");
        Console.WriteLine("  --no-vulkan        Disable Vulkan backend (headless)");
        Console.WriteLine("  --max-inst <n>     Instruction execution budget (default: 50000000)");
        Console.WriteLine();
        Console.WriteLine("Only run dumps you are legally permitted to possess.");
    }
}
