#if !PLAYER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Per-project editor shortcuts for <see cref="CommandRegistry"/> command ids.
/// File: <c>ProjectSettings/editor_shortcuts.json</c>.
/// </summary>
public static class EditorShortcutBindings
{
    public const string FileName = "editor_shortcuts.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static Dictionary<string, string> s_commandToGesture = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gesture display string for palette (e.g. Ctrl+Shift+P), keyed by command id.</summary>
    public static IReadOnlyDictionary<string, string> CommandToGestureDisplay =>
        s_commandToGesture;

    public static void ReloadForCurrentProject()
    {
        s_commandToGesture = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var p = ProjectService.Current;
        if (p == null) return;
        var dir = Path.Combine(p.RootPath, "ProjectSettings");
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<ShortcutFile>(json, JsonOpts);
            if (doc?.Bindings == null) return;
            foreach (var kv in doc.Bindings)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                s_commandToGesture[kv.Key.Trim()] = kv.Value.Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[EditorShortcuts] Failed to load {path}: {ex.Message}");
        }
    }

    /// <summary>Returns true if a binding ran and the key event should be marked handled.</summary>
    public static bool TryExecuteMatching(KeyEventArgs e, Func<string, bool> tryExecuteCommandId)
    {
        if (s_commandToGesture.Count == 0) return false;
        foreach (var kv in s_commandToGesture)
        {
            if (!TryParseGesture(kv.Value, out var mods, out var key)) continue;
            if (e.Key != key) continue;
            if ((e.KeyModifiers & ExpectedModifiersMask) != mods) continue;
            if (!tryExecuteCommandId(kv.Key)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Subset of <see cref="KeyModifiers"/> we serialize (Control, Shift, Alt).</summary>
    private const KeyModifiers ExpectedModifiersMask =
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    public static bool TryParseGesture(string s, out KeyModifiers modifiers, out Key key)
    {
        modifiers = KeyModifiers.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts.AsSpan(0, parts.Length - 1))
        {
            var pl = p.ToLowerInvariant();
            if (pl is "ctrl" or "control") modifiers |= KeyModifiers.Control;
            else if (pl is "shift") modifiers |= KeyModifiers.Shift;
            else if (pl is "alt") modifiers |= KeyModifiers.Alt;
            else return false;
        }

        var keyName = parts[^1];
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out key))
            return false;
        return key != Key.None;
    }

    private sealed class ShortcutFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, string>? Bindings { get; set; }
    }
}
#endif
