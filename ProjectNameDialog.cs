using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia;

namespace Game_Engine
{
    public sealed class ProjectNameDialog : Window
    {
        readonly TextBox _tb = new() { Width = 280, Watermark = "My Game" };

        public ProjectNameDialog()
        {
            Width = 380;
            Height = 160;

            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

            ok.Click += (_, __) => Close(_tb.Text?.Trim());
            cancel.Click += (_, __) => Close(null);

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
            {
                new TextBlock { Text = "Project Name:" },
                _tb,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel }
                }
            }
            };
        }
    }
}
