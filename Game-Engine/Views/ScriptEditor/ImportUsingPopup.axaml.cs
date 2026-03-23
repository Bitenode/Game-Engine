using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Game_Engine.Views;

/// <summary>Pick a namespace to insert as a <c>using</c> directive (hover quick-fix).</summary>
public partial class ImportUsingPopup : UserControl
{
    public event Action<string>? NamespaceChosen;

    public ImportUsingPopup()
    {
        InitializeComponent();
        SuggestionList.DoubleTapped += (_, _) => CommitSelected();
    }

    public void Show(IReadOnlyList<string> namespaces, Point positionInCanvasCoords)
    {
        SuggestionList.Items.Clear();
        foreach (var ns in namespaces)
        {
            var text = "using " + ns + ";";
            var item = new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                Tag = ns,
                Padding = new Thickness(4, 2),
            };
            item.Tapped += (_, _) =>
            {
                SuggestionList.SelectedItem = item;
                CommitSelected();
            };
            SuggestionList.Items.Add(item);
        }

        Margin = new Thickness(positionInCanvasCoords.X, positionInCanvasCoords.Y, 0, 0);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        IsVisible = SuggestionList.ItemCount > 0;
        if (IsVisible == true)
            SuggestionList.SelectedIndex = 0;
    }

    public new void Hide()
    {
        IsVisible = false;
        SuggestionList.Items.Clear();
    }

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

    void MoveSelection(int delta)
    {
        int count = SuggestionList.ItemCount;
        if (count == 0) return;
        int idx = SuggestionList.SelectedIndex + delta;
        SuggestionList.SelectedIndex = Math.Clamp(idx, 0, count - 1);
        SuggestionList.ScrollIntoView(SuggestionList.SelectedIndex);
    }

    void CommitSelected()
    {
        if (SuggestionList.SelectedItem is ListBoxItem li && li.Tag is string ns)
            NamespaceChosen?.Invoke(ns);
        Hide();
    }
}
