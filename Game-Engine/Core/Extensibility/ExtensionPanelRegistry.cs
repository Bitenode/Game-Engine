using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Game_Engine.Docking;

namespace Game_Engine.Core.Extensibility;

/// <summary>
/// Register dockable editor tabs from <see cref="EditorExtension.Contribute"/>.
/// Cleared at the start of each extension refresh before instances are created.
/// </summary>
public static class ExtensionPanelRegistry
{
    public sealed class Entry
    {
        public Type PanelType { get; init; } = null!;
        public string Title { get; init; } = "";
        public DockRegion Region { get; init; }
        public Func<Control> Factory { get; init; } = null!;
        /// <summary>Stable id for <see cref="Game_Engine.Core.CommandRegistry"/> (e.g. editor.ext.panel.*).</summary>
        public string CommandId { get; init; } = "";
    }

    private static readonly List<Entry> s_entries = new();

    public static IReadOnlyList<Entry> Entries => s_entries;

    internal static void Clear() => s_entries.Clear();

    /// <summary>Command id used for <see cref="Register{T}"/> / <see cref="Register"/> and <see cref="MenuBuilder.Command"/>.</summary>
    public static string GetPanelCommandId(Type panelType)
    {
        if (panelType == null) throw new ArgumentNullException(nameof(panelType));
        return "editor.ext.panel." + (panelType.FullName ?? panelType.Name).Replace(' ', '_');
    }

    /// <summary>Register a panel with a custom factory (e.g. non-default constructor or contextual setup).</summary>
    public static void Register(string title, DockRegion region, Func<Control> factory, Type panelTypeKey)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException(nameof(title));
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (panelTypeKey == null) throw new ArgumentNullException(nameof(panelTypeKey));
        var id = GetPanelCommandId(panelTypeKey);
        s_entries.Add(new Entry
        {
            PanelType = panelTypeKey,
            Title = title.Trim(),
            Region = region,
            Factory = factory,
            CommandId = id
        });
    }

    /// <summary>Register a panel opened via Window → Add-on panels and the command palette.</summary>
    public static void Register<TPanel>(string title, DockRegion region = DockRegion.Right)
        where TPanel : Control, new()
    {
        Register(title, region, static () => new TPanel(), typeof(TPanel));
    }
}
