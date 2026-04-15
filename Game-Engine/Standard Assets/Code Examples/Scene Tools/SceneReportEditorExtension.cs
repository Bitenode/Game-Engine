using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Extensibility;
using Game_Engine.Docking;

/// <summary>Reference add-on: scene selection reporting under Tools → Scene report.</summary>
public sealed class SceneReportEditorExtension : EditorExtension
{
    public override void Contribute(EditorUI ui)
    {
        ExtensionPanelRegistry.Register<SelectionReportPanel>("Selection report", DockRegion.Right);

        CommandRegistry.Register("addon.scene.reportSelection", "Scene: Report selection stats", ReportSelectionStats,
            () => ProjectService.Current != null && SelectionService.Selected.Count > 0);
        CommandRegistry.Register("addon.scene.listBehaviors", "Scene: List behavior types on selection", ListBehaviorTypes,
            () => SelectionService.Selected.Count > 0);
        CommandRegistry.Register("addon.scene.copyPaths", "Scene: Copy hierarchy paths to clipboard", CopyPathsToClipboard,
            () => SelectionService.Selected.Count > 0);

        ui.Menu("Tools")
            .Submenu("Scene report")
                .Command("Report selection stats", "addon.scene.reportSelection")
                .Command("List behavior types on selection", "addon.scene.listBehaviors")
                .Command("Copy hierarchy paths to clipboard", "addon.scene.copyPaths")
            .EndSubmenu();
    }

    private static void ReportSelectionStats()
    {
        var sel = SelectionService.Selected.ToList();
        Log.Info($"[SceneReport] {sel.Count} object(s) selected.");
        foreach (var go in sel)
            Log.Info($"[SceneReport]   - {HierarchyPath(go)}");
    }

    private static void ListBehaviorTypes()
    {
        foreach (var go in SelectionService.Selected)
        {
            var types = go.Behaviors.Select(b => b.GetType().Name).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            Log.Info($"[SceneReport] '{go.Name}': {string.Join(", ", types)}");
        }
    }

    private static void CopyPathsToClipboard()
    {
        var lines = SelectionService.Selected.Select(HierarchyPath).ToArray();
        var text = string.Join(Environment.NewLine, lines);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
                    && life.MainWindow?.Clipboard is { } cb)
                    await cb.SetTextAsync(text);
                Log.Info($"[SceneReport] Copied {lines.Length} path(s) to clipboard.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[SceneReport] Clipboard failed: {ex.Message}");
            }
        });
    }

    private static string HierarchyPath(GameObject go)
    {
        var parts = new List<string>();
        for (var p = go; p != null; p = p.Parent)
            parts.Add(p.Name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}
