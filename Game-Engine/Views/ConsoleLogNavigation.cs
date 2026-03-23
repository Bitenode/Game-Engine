#if !PLAYER
using System;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace Game_Engine.Views;

/// <summary>Parse compiler-style paths from log lines and open the script editor.</summary>
public static class ConsoleLogNavigation
{
    private static readonly Regex RxParenLine = new(
        @"(?<path>[A-Za-z]:[^:(]*\.cs)\((?<line>\d+)(?:,\d+)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RxInPathLine = new(
        @"\s+in\s+(?<path>[^\s:]+\.cs):(?<line>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryOpenFromMessage(string message, Window? owner)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        foreach (var rx in new[] { RxParenLine, RxInPathLine })
        {
            var m = rx.Match(message);
            if (!m.Success) continue;
            var path = m.Groups["path"].Value.Trim();
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(path))
            {
                try { path = Path.GetFullPath(path); } catch { }
            }
            if (!File.Exists(path)) continue;
            if (!int.TryParse(m.Groups["line"].Value, out var line)) line = 1;
            ScriptEditorWindow.OpenAtLine(owner, path, line);
            return true;
        }

        return false;
    }
}
#endif
