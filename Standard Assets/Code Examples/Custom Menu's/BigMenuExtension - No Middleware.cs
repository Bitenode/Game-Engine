/*using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.ApplicationLifetimes;
using Game_Engine.Core;
using Game_Engine.Core.Extensibility;

public sealed class BigMenuExtension : EditorExtension
{
    public override void Contribute(EditorUI ui)
    {
        // Register command owned by this extension
        CommandRegistry.Register("demo.bigmenu", "Open Big Menu No Middleware.", ShowBigMenu, () => true);

        // Add to Tools
        ui.Menu("Tools").Command("Open Big Menu No Middleware.", "demo.bigmenu");
    }

    private void ShowBigMenu()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.MainWindow;
        if (owner == null) return;

        var win = new Window
        {
            Width = 360,
            Height = 260,
            Title = "Big Menu",
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Topmost = true
        };

        win.Content = BuildContent();

        // Optional: close when focus leaves the window
        win.Deactivated += (_, __) => win.Close();

        win.Show(owner);
    }

    private Control BuildContent()
    {
        var header = new TextBlock
        {
            Text = "Demo Tools",
            FontWeight = FontWeight.Bold,
            FontSize = 16,
            Margin = new Thickness(12, 12, 12, 8)
        };

        Button Make(string text)
        {
            var b = new Button { Content = text, Margin = new Thickness(12, 6, 12, 6) };
            b.Click += (_, __) => {  };
            return b;
        }

        var close = new Button { Content = "Close", Margin = new Thickness(12, 12, 12, 12) };
        close.Click += (_, __) =>
        {
            // FIX: use TopLevel.GetTopLevel instead of GetVisualRoot()
            var top = TopLevel.GetTopLevel(close) as Window;
            if (top != null) top.Close();
        };

        var stack = new StackPanel
        {
            Children =
            {
                header,
                new Separator{ Margin=new Thickness(12,0,12,6) },
                Make("Do Nothing A"),
                Make("Do Nothing B"),
                Make("Do Nothing C"),
                new Separator{ Margin=new Thickness(12,6,12,6) },
                close
            }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2B2E31")),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(10),
            Child = stack
        };
    }
}*/
