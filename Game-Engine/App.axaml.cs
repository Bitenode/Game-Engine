using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Game_Engine;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Game_Engine.Core.EngineDispatcher.UseCallbacks(
            () => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(),
            a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
