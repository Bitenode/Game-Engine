using Avalonia.Controls;
using Game_Engine.Core;
using Game_Engine.Core.AI;
using Game_Engine.Core.Blueprint;
using Game_Engine.Core.Component;
using Game_Engine.Core.Dialogue;
using Game_Engine.Core.Timeline;

namespace Game_Engine.Views.Inspector;

/// <summary>
/// Services the component-inspector registry needs from <c>InspectorPanel</c>.
/// Heavy UI builders stay on the panel; later slices can move them out.
/// </summary>
public interface IInspectorHost
{
    Control MeshColliderTargetRow(GameObject owner, MeshCollider mc);
    Control TriggerVolumeReactionsUI(GameObject owner, TriggerVolume tv);
    Control TerrainToolsRow(GameObject owner, Terrain t);
    Control TerrainBrushMasks(Terrain t);
    Control TerrainLayersUI(GameObject owner, Terrain t);
    Control PlanetToolsRow(GameObject owner, PlanetTerrain planet);
    Control TreeInspectorUI(GameObject owner, Tree tree);
    Control PlanetVegetationInspectorUI(GameObject owner, PlanetVegetationSystem veg);
    Control DialogueRunnerInspectorUI(DialogueRunner runner);
    Control BehaviorTreeRunnerInspectorUI(BehaviorTreeRunner runner);
    Control VisualBlueprintBehaviorInspectorUI(VisualBlueprintBehavior vb);
    Control TimelinePlayerInspectorUI(TimelinePlayer player);
    Control MeshLodGroupInspectorUI(MeshLodGroup g);
    Control ReflectionProbeGpuCubemapRow(ReflectionProbe probe);
    Control BuildVegetationActionsPanel(VegetationPainter vp);
    Control BuildPlanetAtmospherePresetPanel(PlanetAtmosphere pa);
}
