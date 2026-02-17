using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core.Editor;

namespace Game_Engine.Views;

/// <summary>
/// Represents one open file tab with its own buffer, caret, undo stack, and scroll state.
/// </summary>
public sealed class EditorTab
{
    public string FilePath { get; set; }
    public string FileName => Path.GetFileName(FilePath);
    public TextBuffer Buffer { get; } = new();
    public CaretState Caret { get; } = new();
    public UndoStack UndoStack { get; } = new();
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }
    public bool IsDirty { get; set; }
    public IReadOnlyList<EditorClassifiedSpan>? ClassifiedSpans { get; set; }

    public EditorTab(string filePath) { FilePath = filePath; }
}

/// <summary>
/// Horizontal tab strip showing open file tabs with close buttons and dirty indicators.
/// </summary>
public partial class EditorTabBar : UserControl
{
    private readonly List<EditorTab> _tabs = new();
    private EditorTab? _activeTab;

    // ── Brushes ─────────────────────────────────────────────────
    private static readonly IBrush s_activeBg   = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush s_inactiveBg = new SolidColorBrush(Color.Parse("#2D2D2D"));
    private static readonly IBrush s_hoverBg    = new SolidColorBrush(Color.Parse("#383838"));
    private static readonly IBrush s_textBrush  = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush s_dimText    = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush s_closeBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush s_closeHover = new SolidColorBrush(Color.Parse("#E0E0E0"));

    // ── Events ──────────────────────────────────────────────────
    public event Action<EditorTab>? TabSelected;
    public event Action<EditorTab>? TabCloseRequested;

    public IReadOnlyList<EditorTab> Tabs => _tabs;
    public EditorTab? ActiveTab => _activeTab;

    public EditorTabBar()
    {
        InitializeComponent();
    }

    // ── Public API ──────────────────────────────────────────────

    public EditorTab AddTab(string filePath, bool activate = true)
    {
        // Reuse existing tab for same path
        foreach (var t in _tabs)
        {
            if (string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                if (activate) SetActiveTab(t);
                return t;
            }
        }

        var tab = new EditorTab(filePath);
        _tabs.Add(tab);
        if (activate) SetActiveTab(tab);
        RebuildUI();
        return tab;
    }

    public void RemoveTab(EditorTab tab)
    {
        int idx = _tabs.IndexOf(tab);
        if (idx < 0) return;
        _tabs.RemoveAt(idx);

        if (_activeTab == tab)
        {
            if (_tabs.Count > 0)
            {
                int newIdx = Math.Min(idx, _tabs.Count - 1);
                SetActiveTab(_tabs[newIdx]);
            }
            else
            {
                _activeTab = null;
            }
        }

        RebuildUI();
    }

    public void SetActiveTab(EditorTab tab)
    {
        _activeTab = tab;
        TabSelected?.Invoke(tab);
        RebuildUI();
    }

    public void RefreshTabDirtyState()
    {
        RebuildUI();
    }

    // ── UI rebuild ──────────────────────────────────────────────

    private void RebuildUI()
    {
        TabStack.Children.Clear();

        foreach (var tab in _tabs)
        {
            bool isActive = tab == _activeTab;
            var tabPanel = BuildTabPanel(tab, isActive);
            TabStack.Children.Add(tabPanel);
        }
    }

    private Border BuildTabPanel(EditorTab tab, bool isActive)
    {
        var label = new TextBlock
        {
            Text = (tab.IsDirty ? "* " : "") + tab.FileName,
            Foreground = isActive ? s_textBrush : s_dimText,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(10, 0, 4, 0),
        };

        var closeBtn = new Button
        {
            Content = "\u00D7", // multiplication sign as close icon
            FontSize = 14,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            Foreground = s_closeBrush,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 20,
            MinHeight = 20,
        };
        closeBtn.Click += (_, _) => TabCloseRequested?.Invoke(tab);

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(label);
        stack.Children.Add(closeBtn);

        var border = new Border
        {
            Background = isActive ? s_activeBg : s_inactiveBg,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            Padding = new Thickness(0, 4),
            Child = stack,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var capturedTab = tab;
        border.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(border).Properties.IsMiddleButtonPressed)
                TabCloseRequested?.Invoke(capturedTab);
            else
                SetActiveTab(capturedTab);
            e.Handled = true;
        };

        return border;
    }
}
