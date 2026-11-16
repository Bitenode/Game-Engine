using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game_Engine.Core;
using Windows.Storage;

namespace Engine.Player.XboxUwp
{
    /// <summary>
    /// Writes engine log lines to <c>LocalState\player.log</c>. The console has no editor console,
    /// so a missing asset or a failed script is otherwise invisible. On a PC the file is under
    /// <c>%LOCALAPPDATA%\Packages\&lt;package family&gt;\LocalState</c>; on the Xbox, Device Portal → File explorer.
    /// </summary>
    public static class XboxPlayerLog
    {
        static readonly object s_lock = new object();
        static StreamWriter s_writer;
        static readonly HashSet<string> s_seenExceptions = new HashSet<string>();
        const int MaxSwallowedExceptions = 300;
        [ThreadStatic] static bool t_inHandler;

        public static string FilePath { get; private set; }

        public static void Start()
        {
            lock (s_lock)
            {
                if (s_writer != null) return;
                try
                {
                    FilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "player.log");
                    s_writer = new StreamWriter(
                        new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(false))
                    { AutoFlush = true };
                }
                catch
                {
                    return;
                }
            }

            Log.Logged += (_, item) => Write(item.Severity.ToString(), item.Message);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Write("Fatal", e.ExceptionObject?.ToString() ?? "unknown");
            // The engine swallows many load failures. Record each distinct one once so a missing file shows up.
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (t_inHandler) return;
                t_inHandler = true;
                try
                {
                    var ex = e.Exception;
                    string key = ex.GetType().Name + ": " + ex.Message;
                    lock (s_lock)
                    {
                        if (s_seenExceptions.Count >= MaxSwallowedExceptions || !s_seenExceptions.Add(key))
                            return;
                    }
                    Write("Exception", key + "\n" + Trim(ex.StackTrace));
                }
                catch { }
                finally { t_inHandler = false; }
            };
            Write("Info", "Player log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        public static void Write(string severity, string message)
        {
            lock (s_lock)
            {
                if (s_writer == null) return;
                try { s_writer.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + severity + ": " + message); }
                catch { }
            }
        }

        static string Trim(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            var lines = stack.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length && i < 6; i++)
                sb.Append("    ").Append(lines[i].Trim()).Append('\n');
            return sb.ToString().TrimEnd();
        }
    }
}
