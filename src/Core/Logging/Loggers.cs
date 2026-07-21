using System.IO;

namespace Zenith.Core.Logging;

public sealed class ConsoleLogger : ILogger
{
    public static ConsoleLogger Instance { get; } = new ConsoleLogger();

    public void Log(LogLevel level, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        var prefix = level switch
        {
            LogLevel.Trace => "[TRC]",
            LogLevel.Debug => "[DBG]",
            LogLevel.Info => "[INF]",
            LogLevel.Warn => "[WRN]",
            LogLevel.Error => "[ERR]",
            LogLevel.Fatal => "[FTL]",
            _ => "[???]"
        };

        Console.WriteLine($"{timestamp} {prefix} {message}");
    }
}

public sealed class FileLogger : ILogger, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLogger(string path)
    {
        _writer = new StreamWriter(path) { AutoFlush = true };
        Log(LogLevel.Info, $"Log started at {DateTime.UtcNow:O}");
    }

    public void Log(LogLevel level, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        var prefix = level switch
        {
            LogLevel.Trace => "[TRC]",
            LogLevel.Debug => "[DBG]",
            LogLevel.Info => "[INF]",
            LogLevel.Warn => "[WRN]",
            LogLevel.Error => "[ERR]",
            LogLevel.Fatal => "[FTL]",
            _ => "[???]"
        };

        lock (_lock)
        {
            _writer.WriteLine($"{timestamp} {prefix} {message}");
        }
    }

    public void Dispose()
    {
        Log(LogLevel.Info, "Log ended.");
        _writer.Dispose();
    }
}
