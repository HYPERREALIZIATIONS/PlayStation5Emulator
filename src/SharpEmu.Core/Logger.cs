using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace SharpEmu.Core;

/// <summary>
/// Lightweight, thread-safe logger used across the emulator.
/// Writes both to a debug log file (when configured) and optionally to the console.
/// This is intentionally simple: it is a research tool, not a production logging stack.
/// </summary>
public sealed class Logger : IDisposable
{
    public enum Level
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
    }

    private readonly object _fileLock = new object();
    private StreamWriter _writer;
    private readonly bool _echoConsole;
    private readonly Level _minLevel;
    private readonly ConcurrentQueue<string> _buffer = new ConcurrentQueue<string>();
    private readonly CancellationTokenSource _flushCts = new CancellationTokenSource();
    private Thread _flushThread;

    public Level MinimumLevel => _minLevel;

    public Logger(string path = null, bool echoConsole = true, Level minLevel = Level.Info)
    {
        _echoConsole = echoConsole;
        _minLevel = minLevel;

        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                // Background flusher keeps the hot path cheap.
                _flushThread = new Thread(FlushLoop) { IsBackground = true, Name = "logger-flush" };
                _flushThread.Start();

                RawWrite(Level.Info, "SharpEmu log opened");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[logger] failed to open log file '{path}': {ex.Message}");
                _writer = null;
            }
        }
    }

    private void FlushLoop()
    {
        while (!_flushCts.IsCancellationRequested)
        {
            while (_buffer.TryDequeue(out var line))
            {
                try { _writer?.WriteLine(line); } catch { /* ignore write errors */ }
            }
            Thread.Sleep(50);
        }
        // Drain remaining.
        while (_buffer.TryDequeue(out var line))
        {
            try { _writer?.WriteLine(line); } catch { }
        }
    }

    private string Format(Level level, string category, string message)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        return $"{ts} [{level,-5}] {category,-14} {message}";
    }

    private void RawWrite(Level level, string line)
    {
        lock (_fileLock)
        {
            try { _writer?.WriteLine(line); } catch { }
        }
        if (_echoConsole)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                Level.Error => ConsoleColor.Red,
                Level.Warn => ConsoleColor.Yellow,
                Level.Info => ConsoleColor.Gray,
                Level.Debug => ConsoleColor.Cyan,
                _ => ConsoleColor.DarkGray,
            };
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
        }
    }

    public void Log(Level level, string category, string message)
    {
        if (level < _minLevel) return;
        var line = Format(level, category, message);
        // Console echo is immediate; file write goes through the buffer for speed.
        if (_writer != null)
            _buffer.Enqueue(line);
        if (_echoConsole)
            RawWriteToConsole(level, line);
    }

    private void RawWriteToConsole(Level level, string line)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = level switch
        {
            Level.Error => ConsoleColor.Red,
            Level.Warn => ConsoleColor.Yellow,
            Level.Info => ConsoleColor.Gray,
            Level.Debug => ConsoleColor.Cyan,
            _ => ConsoleColor.DarkGray,
        };
        Console.WriteLine(line);
        Console.ForegroundColor = prev;
    }

    public void Trace(string category, string message) => Log(Level.Trace, category, message);
    public void Debug(string category, string message) => Log(Level.Debug, category, message);
    public void Info(string category, string message) => Log(Level.Info, category, message);
    public void Warn(string category, string message) => Log(Level.Warn, category, message);
    public void Error(string category, string message) => Log(Level.Error, category, message);

    public void Dispose()
    {
        try
        {
            _flushCts.Cancel();
            _flushThread?.Join(500);
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch { }
    }
}
