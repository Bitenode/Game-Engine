using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Game_Engine;

public partial class App : Application
{
    /// <summary>Path to build.json, set by Program.Main before the app starts.</summary>
    public static string? BuildJsonPath { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new PlayerWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
