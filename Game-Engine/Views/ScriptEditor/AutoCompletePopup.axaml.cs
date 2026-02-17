using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Game_Engine.Core.Editor;

namespace Game_Engine.Views;

public partial class AutoCompletePopup : UserControl
{
    private List<CompletionEntry> _items = new();
    private string _filter = "";

    /// <summary>Fired when the user commits a completion (string = text to insert).</summary>
    public event Action<string>? CompletionCommitted;

    /// <summary>Fired when the popup is dismissed.</summary>
    public event Action? Dismissed;

    public AutoCompletePopup()
    {
        InitializeComponent();
        CompletionList.DoubleTapped += (_, _) => CommitSelected();
    }

    // ── Public API ──────────────────────────────────────────────

    public void Show(List<CompletionEntry> items, string filter, Point position)
    {
        _items = items;
        _filter = filter;
        Margin = new Thickness(position.X, position.Y, 0, 0);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Rebuild();
        IsVisible = _items.Count > 0;
    }

    public new void Hide()
    {
        IsVisible = false;
        _items.Clear();
        Dismissed?.Invoke();
    }

    public void UpdateFilter(string filter)
    {
        _filter = filter;
        Rebuild();
        if (CompletionList.ItemCount == 0) Hide();
    }

    /// <summary>Handle keyboard navigation from the editor control.</summary>
    public bool HandleKey(Key key)
    {
        if (!IsVisible) return false;

        switch (key)
        {
            case Key.Down:
                MoveSelection(1);
                return true;
            case Key.Up:
                MoveSelection(-1);
                return true;
            case Key.Tab:
            case Key.Enter:
                CommitSelected();
                return true;
            case Key.Escape:
                Hide();
                return true;
        }
        return false;
    }

    // ── Internals ───────────────────────────────────────────────

    private void Rebuild()
    {
        CompletionList.Items.Clear();

        var comparison = StringComparison.OrdinalIgnoreCase;
        foreach (var item in _items)
        {
            if (!string.IsNullOrEmpty(_filter) &&
                !item.DisplayText.Contains(_filter, comparison))
                continue;

            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };

            sp.Children.Add(new TextBlock
            {
                Text = KindIcon(item.Kind),
                FontSize = 11,
                Width = 18,
                Foreground = KindBrush(item.Kind),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            sp.Children.Add(new TextBlock
            {
                Text = item.DisplayText,
                Foreground = Brushes.White,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            CompletionList.Items.Add(new ListBoxItem
            {
                Content = sp,
                Tag = item.InsertText,
                Padding = new Thickness(4, 2),
            });
        }

        if (CompletionList.ItemCount > 0)
            CompletionList.SelectedIndex = 0;
    }

    private void MoveSelection(int delta)
    {
        int count = CompletionList.ItemCount;
        if (count == 0) return;
        int idx = CompletionList.SelectedIndex + delta;
        CompletionList.SelectedIndex = Math.Clamp(idx, 0, count - 1);
        CompletionList.ScrollIntoView(CompletionList.SelectedIndex);
    }

    private void CommitSelected()
    {
        if (CompletionList.SelectedItem is ListBoxItem li && li.Tag is string text)
        {
            CompletionCommitted?.Invoke(text);
        }
        Hide();
    }

    private static string KindIcon(string kind) => kind switch
    {
        "Method" => "M",
        "Property" => "P",
        "Field" => "F",
        "Class" => "C",
        "Struct" => "S",
        "Interface" => "I",
        "Enum" => "E",
        "Namespace" => "N",
        "Keyword" => "K",
        "Local" => "L",
        "Variable" => "V",
        "Event" => "E",
        "Delegate" => "D",
        _ => "·",
    };

    private static IBrush KindBrush(string kind) => kind switch
    {
        "Method" => new SolidColorBrush(Color.Parse("#DCDCAA")),
        "Property" or "Field" => new SolidColorBrush(Color.Parse("#9CDCFE")),
        "Class" or "Struct" or "Interface" or "Enum" => new SolidColorBrush(Color.Parse("#4EC9B0")),
        "Keyword" => new SolidColorBrush(Color.Parse("#569CD6")),
        _ => new SolidColorBrush(Color.Parse("#D4D4D4")),
    };
}
