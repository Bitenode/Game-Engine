using Game_Engine.Core;
using Game_Engine.Core.Extensibility;
using Game_Engine.Views;

/// <summary>Reference add-on: project/asset utilities — largest files under Assets, and validation preflight.</summary>
public sealed class AssetLargestFilesEditorExtension : EditorExtension
{
    public override void Contribute(EditorUI ui)
    {
        ExtensionPanelRegistry.Register(
            "Largest assets",
            Game_Engine.Docking.DockRegion.Right,
            static () => new LargestFilesPanel(),
            typeof(LargestFilesPanel));

        CommandRegistry.Register("addon.project.preflightValidation", "Project: Preflight (validate + extensions)", RunPreflight,
            () => ProjectService.Current != null);

        ui.Menu("Tools")
            .Submenu("Project tools")
                .Command("Largest assets (panel)", ExtensionPanelRegistry.GetPanelCommandId(typeof(LargestFilesPanel)))
                .Command("Preflight validation", "addon.project.preflightValidation")
            .EndSubmenu();
    }

    static void RunPreflight()
    {
        var issues = ProjectValidator.ValidateCurrentProject();
        Log.Info($"[Preflight] Validation reported {issues.Count} line(s). Use the Extensions status panel for the full list.");
        if (issues.Count == 0)
            Log.Success("[Preflight] No issues reported.");
        else
            Log.Warning("[Preflight] Review the console or Window → New Extensions Status Tab for details.");
    }
}
