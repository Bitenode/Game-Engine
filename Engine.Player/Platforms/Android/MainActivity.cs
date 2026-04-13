using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Game_Engine.Android;

[Activity(
    Label = "Engine Player",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/Icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<global::Game_Engine.App>
{
    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        PlayerAndroidStorage.EnsureDataFromAssets();
        base.OnCreate(savedInstanceState);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
