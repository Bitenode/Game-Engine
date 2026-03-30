#if !PLAYER
using Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core;

namespace Game_Engine.Views;

public sealed class CommandPaletteSource
{
    public string Title = "";
    public string Subtitle = "";
    /// <summary>Optional right-aligned shortcut label (e.g. Ctrl+S).</summary>
    public string? ShortcutHint;
    public Action Execute = () => { };
    public Func<bool>? CanRun;
}

/// <summary>Fuzzy-filtered list of <see cref="CommandRegistry"/> commands.</summary>
public sealed class EditorCommandPaletteWindow : Window
{
    private readonly TextBox _search = new();
    private readonly ListBox _list = new();
    private readonly IReadOnlyList<CommandPaletteSource> _sources;

    public EditorCommandPaletteWindow(IReadOnlyList<CommandPaletteSource> sources)
    {
        _sources = sources;
        Title = "Command Palette";
        Width = 560;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        MinHeight = 320;
        MinWidth = 400;
        Background = new SolidColorBrush(Color.Parse("#2B2D31"));

        _search.Watermark = "Filter commands… (Esc to close, Enter to run)";
        _search.FontSize = 14;
        _search.Margin = new Thickness(0, 0, 0, 8);

        _list.MinHeight = 260;
        _list.SelectionMode = SelectionMode.Single;

        var root = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 0,
            Children =
            {
                _search,
                _list
            }
        };
        Content = root;

        _search.TextChanged += (_, __) => RebuildList();
        _list.DoubleTapped += (_, __) => RunSelectedAndClose();
        _list.KeyDown += OnListKeyDown;
        KeyDown += OnWindowKeyDown;
        _search.KeyDown += OnSearchKeyDown;

        Opened += (_, __) =>
        {
            RebuildList();
            _search.Focus();
        };

        RebuildList();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunSelectedAndClose();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _list.ItemCount > 0)
        {
            _list.Focus();
            _list.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RunSelectedAndClose();
            e.Handled = true;
        }
    }

    private void RunSelectedAndClose()
    {
        if (_list.SelectedItem is not ListBoxItem lbi || lbi.Tag is not Action act) return;
        try { act(); }
        catch (Exception ex) { Log.Error(ex, "[CommandPalette]"); }
        Close();
    }

    private void RebuildList()
    {
        var q = _search.Text ?? "";
        var tokens = q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<CommandPaletteSource> candidates = _sources.Where(s => s.CanRun == null || s.CanRun());

        if (tokens.Length > 0)
        {
            candidates = candidates
                .Select(s => (s, Score: MatchScore(s, tokens)))
                .Where(t => t.Score >= 0)
                .OrderByDescending(t => t.Score)
                .ThenBy(t => t.s.Title, StringComparer.OrdinalIgnoreCase)
                .Select(t => t.s);
        }
        else
            candidates = candidates.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase);

        _list.Items.Clear();
        foreach (var s in candidates)
        {
            var title = new TextBlock
            {
                Text = s.Title,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            var sub = new TextBlock
            {
                Text = s.Subtitle ?? "",
                Opacity = 0.65,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = !string.IsNullOrWhiteSpace(s.Subtitle)
            };
            var textCol = new StackPanel { Spacing = 2, Children = { title, sub } };
            Control row = textCol;
            if (!string.IsNullOrWhiteSpace(s.ShortcutHint))
            {
                var grid = new Avalonia.Controls.Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var hint = new TextBlock
                {
                    Text = s.ShortcutHint,
                    Opacity = 0.5,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                Avalonia.Controls.Grid.SetColumn(textCol, 0);
                Avalonia.Controls.Grid.SetColumn(hint, 1);
                grid.Children.Add(textCol);
                grid.Children.Add(hint);
                row = grid;
            }

            var item = new ListBoxItem
            {
                Content = row,
                Tag = (Action)(() =>
                {
                    try { s.Execute(); }
                    catch (Exception ex) { Log.Error(ex, "[CommandPalette]"); }
                }),
                Padding = new Thickness(6, 4)
            };
            _list.Items.Add(item);
        }

        if (_list.ItemCount > 0)
            _list.SelectedIndex = 0;
    }

    private static int MatchScore(CommandPaletteSource s, string[] tokens)
    {
        var hay = ($"{s.Title} {s.Subtitle} {s.ShortcutHint}").ToLowerInvariant();
        int score = 0;
        foreach (var t in tokens)
        {
            var tl = t.ToLowerInvariant();
            var i = hay.IndexOf(tl, StringComparison.Ordinal);
            if (i < 0) return -1;
            score += 200 - Math.Min(i, 199);
        }
        return score;
    }

    private static readonly Dictionary<string, string> BuiltinShortcutHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["editor.commandPalette"] = "Ctrl+Shift+P",
        ["editor.quickOpen"] = "Ctrl+P",
        ["editor.project.saveScene"] = "Ctrl+S",
        ["editor.game.togglePlay"] = "F5",
        ["editor.revealInProject"] = "Ctrl+Shift+R",
    };

    public static IReadOnlyList<CommandPaletteSource> SourcesFromRegistry()
    {
        var list = new List<CommandPaletteSource>();
        foreach (var cmd in CommandRegistry.GetAllCommands())
        {
            var c = cmd;
            BuiltinShortcutHints.TryGetValue(c.Id, out var shortcut);
            list.Add(new CommandPaletteSource
            {
                Title = c.DisplayName,
                Subtitle = c.IsFromExtension ? c.Id + " · extension" : c.Id,
                ShortcutHint = shortcut,
                CanRun = c.CanExecute,
                Execute = () =>
                {
                    if (c.CanExecute != null && !c.CanExecute()) return;
                    c.Execute?.Invoke();
                }
            });
        }
        return list;
    }
}
#endif
