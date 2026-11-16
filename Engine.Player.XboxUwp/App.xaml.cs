using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Engine.Player.XboxUwp;

public sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // The main menu is hit-tested with the pointer. On Xbox the left stick moves that
        // cursor, and A clicks. Gameplay look stays on the right stick.
        RequiresPointerMode = ApplicationRequiresPointerMode.Auto;
        Suspending += OnSuspending;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        try
        {
            // Prefer fullscreen so the SwapChainPanel isn't born at a small windowed size.
            Windows.UI.ViewManagement.ApplicationView.PreferredLaunchWindowingMode =
                Windows.UI.ViewManagement.ApplicationViewWindowingMode.FullScreen;
            var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();
            view.SetDesiredBoundsMode(Windows.UI.ViewManagement.ApplicationViewBoundsMode.UseCoreWindow);
        }
        catch { }

        if (Window.Current.Content is not Frame rootFrame)
        {
            rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            Window.Current.Content = rootFrame;
        }

        if (!e.PrelaunchActivated)
        {
            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
            try { Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().TryEnterFullScreenMode(); }
            catch { }
        }
    }

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e) =>
        throw new Exception("Failed to load Page " + e.SourcePageType.FullName);

    private void OnSuspending(object sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        deferral.Complete();
    }
}
