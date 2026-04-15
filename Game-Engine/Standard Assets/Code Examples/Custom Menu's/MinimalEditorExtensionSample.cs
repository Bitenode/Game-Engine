using Game_Engine.Core;
using Game_Engine.Core.Extensibility;

/// <summary>
/// Minimal editor add-on: registers two commands and attaches them under <c>Tools</c>.
/// Copy into your project's <c>Assets/</c> or <c>Packages/</c>, then compile (Ctrl+B).
/// Output: <c>Builds/EditorScripts/EditorScripts_*.dll</c>; the editor loads the newest DLL and calls <see cref="Contribute"/>.
/// </summary>
public sealed class MinimalEditorExtensionSample : EditorExtension
{
    public override void Contribute(EditorUI ui)
    {
        CommandRegistry.Register("sample.minimal.hello", "Minimal: Say Hello", () =>
        {
            Log.Info("Hello from MinimalEditorExtensionSample.");
        });

        CommandRegistry.Register("sample.minimal.count", "Minimal: Count root objects", () =>
        {
            var n = SceneService.Root?.Count ?? 0;
            Log.Info($"Root object count: {n}");
        });

        ui.Menu("Tools")
            .Submenu("Minimal sample")
                .Command("Say Hello", "sample.minimal.hello")
                .Command("Count root objects", "sample.minimal.count")
            .EndSubmenu();
    }
}
