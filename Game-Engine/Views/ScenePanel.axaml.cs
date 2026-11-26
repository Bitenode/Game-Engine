using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Game_Engine.Core;
using Game_Engine.Core.Rendering;

namespace Game_Engine.Views;

public partial class ScenePanel : UserControl
{
    private sealed record DebugViewItem(RenderDebugMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly DebugViewItem[] s_debugViewItems =
    {
        new(RenderDebugMode.Lit, "Lit"),
        new(RenderDebugMode.Wireframe, "Wireframe"),
        new(RenderDebugMode.Albedo, "Albedo"),
        new(RenderDebugMode.Normals, "Normals"),
        new(RenderDebugMode.Roughness, "Roughness"),
        new(RenderDebugMode.Metallic, "Metallic"),
        new(RenderDebugMode.Depth, "Depth"),
        new(RenderDebugMode.SSAO, "SSAO"),
        new(RenderDebugMode.CsmCascades, "CSM Cascades"),
        new(RenderDebugMode.ToneMapCompare, "ToneMap Compare"),
    };

    public ScenePanel()
    {
        InitializeComponent();

        var debugCombo = this.FindControl<ComboBox>("DebugViewCombo");
        if (debugCombo != null)
        {
            debugCombo.ItemsSource = s_debugViewItems;
            debugCombo.SelectedIndex = 0;
            debugCombo.SelectionChanged += (_, _) =>
            {
                if (debugCombo.SelectedItem is DebugViewItem item)
                {
                    SceneRenderer.DebugViewMode = item.Mode;
                    var scene = this.FindControl<SceneView>("Scene");
                    if (scene != null)
                        scene.DebugViewMode = item.Mode;
                    SceneService.RequestFrame();
                }
            };
        }

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
