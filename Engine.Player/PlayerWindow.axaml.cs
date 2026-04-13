#nullable enable
using Avalonia.Markup.Xaml;

namespace Game_Engine;

public partial class PlayerWindow : Avalonia.Controls.Window
{
    public PlayerWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (Content is PlayerRoot root)
                root.Cleanup();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
