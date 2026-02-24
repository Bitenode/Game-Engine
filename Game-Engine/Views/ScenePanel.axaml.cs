using Avalonia.Controls;
using Avalonia.Interactivity;
using Game_Engine.Core;

namespace Game_Engine.Views;

public partial class ScenePanel : UserControl
{
    public ScenePanel()
    {
        InitializeComponent();

        ProjectService.ProjectOpened += OnProjectOpened;
        AttachedToVisualTree += (_, _) => TryLoadViewSettings();
        DetachedFromVisualTree += (_, _) => ProjectService.ProjectOpened -= OnProjectOpened;

        // Handles flows where a project is already loaded before this panel is created.
        TryLoadViewSettings();
    }

    private void OnProjectOpened()
    {
        TryLoadViewSettings();
    }

    private void TryLoadViewSettings()
    {
        this.FindControl<SceneView>("Scene")?.LoadViewSettings();
    }
}
