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
    }

    private void OnProjectOpened()
    {
        var scene = this.FindControl<SceneView>("Scene");
        scene?.LoadViewSettings();
    }
}
