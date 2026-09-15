using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Game_Engine.Core.Dialogue;

namespace Game_Engine.Views;

/// <summary>Host for the dialogue node tree. The inspector only opens this; it does not embed the tree.</summary>
public sealed class DialogueEditorWindow : Window
{
    const double DefaultWidth = 760;
    const double DefaultHeight = 880;
    const int ScreenMarginPx = 32;

    static DialogueEditorWindow? s_open;

    public DialogueEditorWindow()
    {
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = 420;
        MinHeight = 320;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    public static void Show(Window? owner, DialogueRunner runner, Control treeEditor)
    {
        var title = string.IsNullOrWhiteSpace(runner.Tree?.Name)
            ? "Dialogue Editor"
            : $"Dialogue Editor — {runner.Tree!.Name}";

        if (s_open != null)
        {
            s_open.Title = title;
            s_open.Content = BuildChrome(s_open, treeEditor);
            FitToWorkArea(s_open, owner);
            ClampOntoScreen(s_open, owner);
            s_open.Activate();
            return;
        }

        var w = new DialogueEditorWindow { Title = title };
        w.Content = BuildChrome(w, treeEditor);
        s_open = w;
        w.Closed += (_, _) => { if (s_open == w) s_open = null; };
        w.Opened += (_, _) =>
        {
            FitToWorkArea(w, owner);
            ClampOntoScreen(w, owner);
        };

        FitToWorkArea(w, owner);
        if (owner != null) w.Show(owner);
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
        }
    }

    static Control BuildChrome(Window host, Control treeEditor)
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = treeEditor
        };

        var close = new Button
        {
            Content = "Close",
            Padding = new Thickness(16, 6),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        close.Click += (_, _) => host.Close();

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(scroll);
        return root;
    }

    static void FitToWorkArea(Window w, Window? owner)
    {
        if (!TryGetWorkArea(w, owner, out var wa, out var scale)) return;

        var margin = ScreenMarginPx / scale;
        var maxW = Math.Max(w.MinWidth, wa.Width / scale - margin);
        var maxH = Math.Max(w.MinHeight, wa.Height / scale - margin);

        w.MaxWidth = maxW;
        w.MaxHeight = maxH;
        w.Width = Math.Min(DefaultWidth, maxW);
        w.Height = Math.Min(DefaultHeight, maxH);
    }

    static void ClampOntoScreen(Window w, Window? owner)
    {
        if (!TryGetWorkArea(w, owner, out var wa, out var scale)) return;

        var wPx = Math.Max(1, (int)Math.Ceiling(w.Bounds.Width * scale));
        var hPx = Math.Max(1, (int)Math.Ceiling(w.Bounds.Height * scale));
        if (wPx <= 1 || hPx <= 1)
        {
            wPx = (int)Math.Ceiling(w.Width * scale);
            hPx = (int)Math.Ceiling(w.Height * scale);
        }

        var maxX = wa.X + Math.Max(0, wa.Width - wPx);
        var maxY = wa.Y + Math.Max(0, wa.Height - hPx);
        var x = Math.Clamp(w.Position.X, wa.X, maxX);
        var y = Math.Clamp(w.Position.Y, wa.Y, maxY);
        w.Position = new PixelPoint(x, y);
    }

    static bool TryGetWorkArea(Window w, Window? owner, out PixelRect workArea, out double scale)
    {
        var screens = owner?.Screens ?? w.Screens;
        var screen = (owner != null ? screens.ScreenFromWindow(owner) : null)
                     ?? screens.ScreenFromWindow(w)
                     ?? screens.Primary;
        if (screen == null)
        {
            workArea = default;
            scale = 1;
            return false;
        }

        workArea = screen.WorkingArea;
        scale = screen.Scaling > 0 ? screen.Scaling : 1;
        return workArea.Width > 0 && workArea.Height > 0;
    }
}
