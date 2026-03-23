#if !PLAYER
using Avalonia;
using Game_Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core;

namespace Game_Engine.Views;

/// <summary>Project file picker with fuzzy filter (.scene, .cs, .material, …).</summary>
public sealed class EditorQuickOpenWindow : Window
{
    private readonly TextBox _search = new();
    private readonly ListBox _list = new();
    private readonly MainWindow _host;
    private IReadOnlyList<FileRow> _files = Array.Empty<FileRow>();

    private sealed class FileRow
    {
        public string Rel = "";
        public string Abs = "";
    }

    public EditorQuickOpenWindow(MainWindow host)
    {
        _host = host;
        Title = "Quick Open";
        Width = 640;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        MinHeight = 360;
        MinWidth = 480;
        Background = new SolidColorBrush(Color.Parse("#2B2D31"));

        _search.Watermark = "Filter by path or name… (Esc to close, Enter to open)";
        _search.FontSize = 14;
        _search.Margin = new Thickness(0, 0, 0, 8);

        _list.MinHeight = 280;
        _list.SelectionMode = SelectionMode.Single;

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children = { _search, _list }
        };

        _search.TextChanged += (_, __) => RebuildList();
        _list.DoubleTapped += (_, __) => OpenSelectedAndClose();
        _list.KeyDown += OnListKeyDown;
        KeyDown += OnWindowKeyDown;
        _search.KeyDown += OnSearchKeyDown;

        Opened += (_, __) =>
        {
            RefreshIndex();
            RebuildList();
            _search.Focus();
        };
    }

    private void RefreshIndex()
    {
        var proj = ProjectService.Current;
        if (proj is null)
        {
            _files = Array.Empty<FileRow>();
            return;
        }

        var root = Path.GetFullPath(proj.RootPath);
        var rows = new List<FileRow>();
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".scene", ".cs", ".material", ".prefab", ".boneanim", ".shadergraph"
        };

        try
        {
            foreach (var abs in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (ShouldSkipPath(abs, root)) continue;
                var ext = Path.GetExtension(abs);
                if (!exts.Contains(ext)) continue;
                var rel = Path.GetRelativePath(root, abs);
                rows.Add(new FileRow { Abs = abs, Rel = rel.Replace(Path.DirectorySeparatorChar, '/') });
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[QuickOpen] Index failed: {ex.Message}");
        }

        _files = rows.OrderBy(r => r.Rel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ShouldSkipPath(string abs, string root)
    {
        var p = abs.Replace('/', Path.DirectorySeparatorChar);
        var seg = $"{Path.DirectorySeparatorChar}";
        if (p.IndexOf($"{seg}.git{seg}", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (p.IndexOf($"{seg}bin{seg}", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (p.IndexOf($"{seg}obj{seg}", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedAndClose();
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
            OpenSelectedAndClose();
            e.Handled = true;
        }
    }

    private async void OpenSelectedAndClose()
    {
        if (_list.SelectedItem is not ListBoxItem lbi || lbi.Tag is not string abs) return;
        Close();
        await _host.OpenQuickOpenFileAsync(abs);
    }

    private void RebuildList()
    {
        var q = (_search.Text ?? "").Trim();
        var tokens = q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<FileRow> rows = _files;
        if (tokens.Length > 0)
        {
            rows = _files
                .Select(r => (r, Score: MatchScore(r.Rel, tokens)))
                .Where(t => t.Score >= 0)
                .OrderByDescending(t => t.Score)
                .ThenBy(t => t.r.Rel, StringComparer.OrdinalIgnoreCase)
                .Select(t => t.r);
        }

        _list.Items.Clear();
        foreach (var r in rows)
        {
            var tb = new TextBlock
            {
                Text = r.Rel,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = FontFamily.Parse("Consolas, Courier New, monospace")
            };
            var item = new ListBoxItem
            {
                Content = tb,
                Tag = r.Abs,
                Padding = new Thickness(6, 3)
            };
            _list.Items.Add(item);
        }

        if (_list.ItemCount > 0)
            _list.SelectedIndex = 0;
    }

    private static int MatchScore(string rel, string[] tokens)
    {
        var hay = rel.ToLowerInvariant();
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
}
#endif
