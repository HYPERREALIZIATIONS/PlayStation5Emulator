global using System;

namespace Zenith.Core.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5
}

public interface ILogger
{
    void Log(LogLevel level, string message);
    void Trace(string message) => Log(LogLevel.Trace, message);
    void Debug(string message) => Log(LogLevel.Debug, message);
    void Info(string message) => Log(LogLevel.Info, message);
    void Warn(string message) => Log(LogLevel.Warn, message);
    void Error(string message) => Log(LogLevel.Error, message);
    void Fatal(string message) => Log(LogLevel.Fatal, message);
}

public static class Log
{
    public static ILogger Global = ConsoleLogger.Instance;

    public static void SetGlobal(ILogger logger) => Global = logger;

    public static void Trace(string message) => Global.Trace(message);
    public static void Debug(string message) => Global.Debug(message);
    public static void Info(string message) => Global.Info(message);
    public static void Warn(string message) => Global.Warn(message);
    public static void Error(string message) => Global.Error(message);
    public static void Fatal(string message) => Global.Fatal(message);
}
