using Avalonia.Controls;
using Avalonia.Layout;

/// <summary>Sample add-on dock tab registered via <see cref="Game_Engine.Core.Extensibility.ExtensionPanelRegistry"/>.</summary>
public sealed class SelectionReportPanel : UserControl
{
    public SelectionReportPanel()
    {
        Content = new TextBlock
        {
            Text = "Use Tools → Scene report commands, or bind ProjectSettings/editor_shortcuts.json to addon.scene.reportSelection.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(12),
            VerticalAlignment = VerticalAlignment.Top
        };
    }
}
