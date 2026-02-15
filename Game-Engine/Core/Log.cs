using System;
using System.Diagnostics;

namespace Game_Engine.Core
{
    public enum LogSeverity { Info, Warning, Error, Success, Debug }

    public sealed class LogItem
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public LogSeverity Severity { get; init; }
        public string Message { get; init; } = "";
        public override string ToString() => $"[{Timestamp:HH:mm:ss}] {Severity}: {Message}";
    }

    public static class Log
    {
        public static event EventHandler<LogItem>? Logged;

        public static void Info(string msg) => Write(LogSeverity.Info, msg);
        public static void Warning(string msg) => Write(LogSeverity.Warning, msg);
        public static void Error(string msg) => Write(LogSeverity.Error, msg);
        public static void Success(string msg) => Write(LogSeverity.Success, msg);
        public static void Debug(string msg) => Write(LogSeverity.Debug, msg);

        public static void Error(Exception ex, string? context = null)
        {
            var msg = context is null ? ex.ToString() : $"{context}: {ex}";
            Write(LogSeverity.Error, msg);
        }

        private static void Write(LogSeverity sev, string msg)
        {
            var item = new LogItem { Severity = sev, Message = msg };
         //   Debug.WriteLine(item.ToString());
            Logged?.Invoke(null, item);
        }
    }
}