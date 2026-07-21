using System.IO;
using System.Text;

namespace PS5Emulator.Logging;

public static class Logger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new object();

    public static void Initialize(string path)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
        Info("Logger", $"Log initialized at {Path.GetFullPath(path)}");
    }

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message) => Write("ERROR", category, message);
    public static void Fatal(string category, string message) => Write("FATAL", category, message);
    public static void Debug(string category, string message) => Write("DEBUG", category, message);

    private static void Write(string level, string category, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}";
        lock (_lock)
        {
            _writer?.WriteLine(line);
        }
    }
}
