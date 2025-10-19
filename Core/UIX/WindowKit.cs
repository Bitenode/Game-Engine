using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;

// Alias Avalonia layout types to avoid collisions with own Grid class
using AGrid = Avalonia.Controls.Grid;
using ARowDefinitions = Avalonia.Controls.RowDefinitions;
using AColumnDefinitions = Avalonia.Controls.ColumnDefinitions;
using Avalonia.Layout;

namespace Game_Engine.Core.UIX
{
    public sealed class WindowSpec
    {
        public string Title = "Window";
        public double Width = 360;
        public double Height = 240;

        public bool Utility = true;         // border-only, no taskbar
        public bool CloseOnBlur = true;     // close when deactivated

        public bool DragAnywhere = true;    //  click-drag content to move
        public bool Resizable = true;       //  show invisible resize grips

        public bool ShowTitleBar = true;

        public Game_Engine.Core.UIX.VNode Content;
    }


    public static class WindowKit
    {
        public static void Show(WindowSpec spec)
        {
            if (spec == null) return;

            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;


            var w = new Window
            {
                Title = spec.Title,
                Width = spec.Width,
                Height = spec.Height,
                CanResize = spec.Resizable,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            // prevent transparent composition + ensure solid background
            w.TransparencyLevelHint = new System.Collections.Generic.List<WindowTransparencyLevel>
            {
                WindowTransparencyLevel.None
            };
            w.Background = new SolidColorBrush(Color.Parse("#202225")); //  solid color

            if (spec.Utility)
            {
                w.SystemDecorations = SystemDecorations.BorderOnly;       // menu look
                w.ShowInTaskbar = false;
                w.Topmost = true;
            }
            else
            {
                w.SystemDecorations = SystemDecorations.Full;
            }

            // Render user UI
            var content = UIXRenderer.Render(spec.Content);

            // Wrap with drag+resize chrome 
            var chrome = WindowChrome.Wrap(
                content,
                w,
                spec.DragAnywhere,
                spec.Resizable,
                spec.ShowTitleBar,   
                spec.Title           
            );

            w.Content = chrome;

            if (spec.CloseOnBlur)
                w.Deactivated += (_, __) => w.Close();

            if (owner != null) w.Show(owner); else w.Show();
        }

        internal static class WindowChrome
        {
            private const double Grip = 6;
            private const double TitleH = 32;

            public static Control Wrap(
                Control content,
                Window host,
                bool dragAnywhere,
                bool resizable,
                bool showTitleBar,
                string titleText)
            {
                // OUTER grid: [Title(optional)] + [ContentWithGrips]
                var outer = new AGrid();

                if (showTitleBar)
                    outer.RowDefinitions = new ARowDefinitions($"{TitleH},*");
                else
                    outer.RowDefinitions = new ARowDefinitions($"*");

                // --- Title bar (optional) ---
                if (showTitleBar)
                {
                    var title = BuildTitleBar(host, titleText);
                    AGrid.SetRow(title, 0);
                    outer.Children.Add(title);
                }

                // --- Inner: content + grips (your existing logic) ---
                var inner = new AGrid();

                // Make rendered UI fill the middle cell
                content.HorizontalAlignment = HorizontalAlignment.Stretch;
                content.VerticalAlignment = VerticalAlignment.Stretch;
                content.Margin = new Thickness(0);

                inner.Children.Add(content);

                // Drag anywhere (also provided by title bar)
                if (dragAnywhere)
                {
                    inner.PointerPressed += (s, e) =>
                    {
                        if (e.GetCurrentPoint(inner).Properties.IsLeftButtonPressed)
                            host.BeginMoveDrag(e);
                    };
                }

                if (resizable)
                {
                    // Edges
                    AddGrip(inner, host, WindowEdge.North, new Cursor(StandardCursorType.TopSide), 0, 1, 1, 1, horizontal: true);
                    AddGrip(inner, host, WindowEdge.South, new Cursor(StandardCursorType.BottomSide), 2, 1, 1, 1, horizontal: true);
                    AddGrip(inner, host, WindowEdge.West, new Cursor(StandardCursorType.LeftSide), 1, 0, 1, 1);
                    AddGrip(inner, host, WindowEdge.East, new Cursor(StandardCursorType.RightSide), 1, 2, 1, 1);

                    // Corners
                    AddGrip(inner, host, WindowEdge.NorthWest, new Cursor(StandardCursorType.TopLeftCorner), 0, 0, 1, 1, corner: true);
                    AddGrip(inner, host, WindowEdge.NorthEast, new Cursor(StandardCursorType.TopRightCorner), 0, 2, 1, 1, corner: true);
                    AddGrip(inner, host, WindowEdge.SouthWest, new Cursor(StandardCursorType.BottomLeftCorner), 2, 0, 1, 1, corner: true);
                    AddGrip(inner, host, WindowEdge.SouthEast, new Cursor(StandardCursorType.BottomRightCorner), 2, 2, 1, 1, corner: true);

                    // Stretch middle cell
                    inner.RowDefinitions = new ARowDefinitions($"{Grip},*,{Grip}");
                    inner.ColumnDefinitions = new AColumnDefinitions($"{Grip},*,{Grip}");
                    AGrid.SetRow(content, 1);
                    AGrid.SetColumn(content, 1);
                }

                // Put inner below title
                AGrid.SetRow(inner, showTitleBar ? 1 : 0);
                outer.Children.Add(inner);

                return outer;
            }

            private static Control BuildTitleBar(Window host, string titleText)
            {
                // grid: [title] [min] [max/restore] [close]
                var g = new AGrid
                {
                    Background = new SolidColorBrush(Color.Parse("#1F2225")),
                    Height = TitleH
                };
                g.ColumnDefinitions = new AColumnDefinitions("*,Auto,Auto,Auto");

                // title text (also acts as drag handle + double-click to maximize/restore)
                var title = new TextBlock
                {
                    Text = titleText ?? "Window",
                    Margin = new Thickness(10, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White
                };
                AGrid.SetColumn(title, 0);
                g.Children.Add(title);

                // drag from title area
                g.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(g).Properties.IsLeftButtonPressed)
                    {
                        e.Handled = true;           
                        host.BeginMoveDrag(e);
                    }
                };
                // double-click title toggles maximize/restore
                g.DoubleTapped += (s, e) => ToggleMax(host);

                // buttons
                var btnMin = MakeChromeButton("—", "Minimize");
                var btnMax = MakeChromeButton("▢", "Maximize");
                var btnClose = MakeChromeButton("✕", "Close");

                btnMin.Click += (_, __) => host.WindowState = WindowState.Minimized;
                btnMax.Click += (_, __) => ToggleMax(host);
                btnClose.Click += (_, __) => host.Close();

                AGrid.SetColumn(btnMin, 1);
                AGrid.SetColumn(btnMax, 2);
                AGrid.SetColumn(btnClose, 3);

                g.Children.Add(btnMin);
                g.Children.Add(btnMax);
                g.Children.Add(btnClose);

                return g;

                // local helpers
                static Button MakeChromeButton(string glyph, string tooltip)
                {
                    return new Button
                    {
                        Content = new TextBlock { Text = glyph, FontSize = 14, Margin = new Thickness(0, -2, 0, 0) },
                        Width = 36,
                        Height = TitleH,
                        Background = Brushes.Transparent,
                        Foreground = Brushes.White,
                        BorderBrush = Brushes.Transparent,
                        Padding = new Thickness(0),
                        [ToolTip.TipProperty] = tooltip
                    };
                }
            }

            private static void ToggleMax(Window host)
            {
                host.WindowState = host.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }

            private static void AddGrip(
                AGrid root,
                Window host,
                WindowEdge edge,
                Cursor cursor,
                int row,
                int col,
                int width,
                int height,
                bool horizontal = false,
                bool corner = false)
            {
                var grip = new Border
                {
                    Background = Brushes.Transparent,
                    Cursor = cursor,
                    IsHitTestVisible = true
                };

                AGrid.SetRow(grip, row); AGrid.SetColumn(grip, col);
                AGrid.SetRowSpan(grip, height); AGrid.SetColumnSpan(grip, width);

                if (corner) { grip.MinWidth = Grip; grip.MinHeight = Grip; }
                else if (horizontal) { grip.MinHeight = Grip; }
                else { grip.MinWidth = Grip; }

                grip.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
                        host.BeginResizeDrag(edge, e);
                };

                root.Children.Add(grip);
            }
        }



    }
}
