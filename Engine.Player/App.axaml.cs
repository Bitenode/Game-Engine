using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Game_Engine;

public partial class App : Avalonia.Application
{
    /// <summary>Path to build.json, set by Program.Main before the app starts.</summary>
    public static string? BuildJsonPath { get; set; }

    /// <summary>Whether the FPS/frame-time HUD is shown on startup (enabled via --fps).</summary>
    public static bool ShowFpsHud { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Game_Engine.Core.EngineDispatcher.UseCallbacks(
            () => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(),
            a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new PlayerWindow();
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = new PlayerRoot();

        base.OnFrameworkInitializationCompleted();
    }
}
