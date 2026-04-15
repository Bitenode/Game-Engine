using System.Linq;
using Avalonia;
using Game_Engine.Core;
using Game_Engine.Core.Extensibility;
using Game_Engine.Core.UIX;
using static Game_Engine.Core.UIX.UIX;

/// <summary>Reference add-on: batch hierarchy tools under Tools → Batch tools (menus + UIX + CommandRegistry).</summary>
public sealed class BatchToolsEditorExtension : EditorExtension
{
    string _prefix = "";
    string _suffix = "";

    public override void Contribute(EditorUI ui)
    {
        CommandRegistry.Register("addon.batch.prefixSuffix", "Batch: Prefix / suffix names…", OpenPrefixSuffixWindow,
            () => ProjectService.Current != null && SelectionService.Selected.Count > 0);
        CommandRegistry.Register("addon.batch.countHierarchy", "Batch: Count hierarchy under selection", CountHierarchy,
            () => SelectionService.Current != null);
        CommandRegistry.Register("addon.batch.logSelection", "Batch: Log selected object names", LogSelection,
            () => SelectionService.Selected.Count > 0);

        ui.Menu("Tools")
            .Submenu("Batch tools")
                .Command("Prefix / suffix names…", "addon.batch.prefixSuffix")
                .Command("Count hierarchy under selection", "addon.batch.countHierarchy")
                .Command("Log selected object names", "addon.batch.logSelection")
            .EndSubmenu();
    }

    private void OpenPrefixSuffixWindow()
    {
        var content = Card(
            Stack(
                Header("Rename selected"),
                Text("Applies to every object in the current selection."),
                Textbox(_prefix, "Prefix…", onChanged: s => _prefix = s ?? ""),
                Textbox(_suffix, "Suffix…", onChanged: s => _suffix = s ?? ""),
                Row(
                    Button("Apply", ApplyPrefixSuffix, primary: true)
                ).WithMargin(new Thickness(12, 8, 12, 12))
            ));

        WindowKit.Show(new WindowSpec
        {
            Title = "Batch rename",
            Width = 420,
            Height = 260,
            Utility = true,
            CloseOnBlur = true,
            DragAnywhere = true,
            Resizable = true,
            ShowTitleBar = true,
            Content = content
        });
    }

    private void ApplyPrefixSuffix()
    {
        var sel = SelectionService.Selected.ToList();
        if (sel.Count == 0) return;
        foreach (var go in sel)
            go.Name = _prefix + go.Name + _suffix;
        SceneService.NotifyChanged();
        Log.Info($"[Batch] Renamed {sel.Count} object(s).");
    }

    private static void CountHierarchy()
    {
        var root = SelectionService.Current;
        if (root == null) return;
        var n = CountDescendants(root);
        Log.Info($"[Batch] '{root.Name}' subtree: {n} object(s) (including root).");
    }

    private static int CountDescendants(GameObject go)
    {
        var n = 1;
        foreach (var c in go.Children)
            n += CountDescendants(c);
        return n;
    }

    private static void LogSelection()
    {
        foreach (var go in SelectionService.Selected)
            Log.Info($"[Batch] Selected: {go.Name}");
    }
}
