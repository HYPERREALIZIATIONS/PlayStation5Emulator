using System;
using System.IO;
using System.Linq;
using Prospero.Emu.Core;

namespace Prospero.Emu
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string? exePath = null;
            string? gameRoot = null;
            string? logPath = null;
            bool verbose = false;
            bool makeTest = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help": case "-h":
                        PrintUsage();
                        return 0;
                    case "--root": case "-r":
                        gameRoot = args[++i];
                        break;
                    case "--log": case "-l":
                        logPath = args[++i];
                        break;
                    case "--verbose": case "-v":
                        verbose = true;
                        break;
                    case "--make-test":
                        makeTest = true;
                        // Default the output path if not given as next arg.
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                            exePath = args[++i];
                        break;
                    default:
                        if (exePath == null) exePath = args[i];
                        else _ = args[i];
                        break;
                }
            }

            if (makeTest)
            {
                string outPath = exePath ?? Path.Combine(Directory.GetCurrentDirectory(), "test.elf");
                Tools.MakeTestElf.Emit(outPath);
                return 0;
            }

            if (exePath == null)
            {
                Console.Error.WriteLine("error: no eboot.bin / elf path provided.");
                PrintUsage();
                return 1;
            }

            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine($"error: file not found: {exePath}");
                return 1;
            }

            // Default log file next to the executable / in cwd.
            logPath ??= Path.Combine(Directory.GetCurrentDirectory(), "prospero-emu.log");

            using var logger = new Logger(logPath)
            {
                MinLevel = verbose ? Logger.LogLevel.Trace : Logger.LogLevel.Info
            };

            logger.Info("main", $"Log file: {Path.GetFullPath(logPath)}");

            try
            {
                var emu = new Emulator(logger);
                return emu.Run(exePath, gameRoot, logPath);
            }
            catch (Exception ex)
            {
                logger.Fatal("main", $"Unhandled exception: {ex}");
                return 3;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Prospero Emu - educational PS5 research emulator");
            Console.WriteLine("Usage:  prospero-emu <path-to-eboot.bin|elf> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -r, --root <dir>    Game/application directory (for file opens)");
            Console.WriteLine("  -l, --log <file>    Log file path (default: ./prospero-emu.log)");
            Console.WriteLine("  -v, --verbose       Verbose (trace) logging");
            Console.WriteLine("  -h, --help          Show this help");
            Console.WriteLine();
            Console.WriteLine("LEGAL: for research/education only. Use only legally obtained,");
            Console.WriteLine("already-decrypted ELF/fself files. No decryption/piracy.");
        }
    }
}
