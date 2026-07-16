// ---------------------------------------------------------------------------
// Prospero Emu - Educational PlayStation 5 (Prospero) research emulator.
//
// NOTICE: This software is for EDUCATIONAL and RESEARCH purposes only.
// It does NOT circumvent, defeat, or bypass any encryption, signature, or
// DRM protection. It expects the user to supply a *legally obtained*,
// already-decrypted ELF/fself (e.g. produced by tools the user is legally
// entitled to use on their own hardware). It contains NO keys, NO decryptor,
// and NO copyrighted system firmware. Do not use this tool with pirated
// material. Respect the law and the rights of content owners.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Prospero.Emu.Core
{
    /// <summary>
    /// Simple, dependency-free logger that mirrors output to a log file.
    /// The log file is the primary debugging artifact requested by the user.
    /// </summary>
    public sealed class Logger : IDisposable
    {
        private readonly object _lock = new();
        private readonly StreamWriter? _file;
        private readonly HashSet<string> _suppressed = new();

        public LogLevel MinLevel { get; set; } = LogLevel.Trace;

        public enum LogLevel
        {
            Trace = 0,
            Debug = 1,
            Info = 2,
            Warn = 3,
            Error = 4,
            Fatal = 5,
        }

        public Logger(string? path = null)
        {
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    _file = new StreamWriter(new FileStream(path!, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                    _file.WriteLine($"# Prospero Emu log started {DateTime.UtcNow:u}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[logger] could not open log file '{path}': {ex.Message}");
                }
            }
        }

        public void Suppress(string category) => _suppressed.Add(category);

        public void Log(LogLevel level, string category, string message)
        {
            if (level < MinLevel) return;
            if (category != null && _suppressed.Contains(category)) return;

            string line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{level,-5}] [{category}] {message}";
            lock (_lock)
            {
                ConsoleColor? color = level switch
                {
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Fatal => ConsoleColor.Magenta,
                    LogLevel.Info => ConsoleColor.Cyan,
                    _ => null
                };
                if (color.HasValue)
                {
                    Console.ForegroundColor = color.Value;
                    Console.WriteLine(line);
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine(line);
                }
                _file?.WriteLine(line);
            }
        }

        public void Trace(string c, string m) => Log(LogLevel.Trace, c, m);
        public void Debug(string c, string m) => Log(LogLevel.Debug, c, m);
        public void Info(string c, string m) => Log(LogLevel.Info, c, m);
        public void Warn(string c, string m) => Log(LogLevel.Warn, c, m);
        public void Error(string c, string m) => Log(LogLevel.Error, c, m);
        public void Fatal(string c, string m) => Log(LogLevel.Fatal, c, m);

        public void Dispose()
        {
            try { _file?.Dispose(); } catch { /* ignore */ }
        }
    }
}
