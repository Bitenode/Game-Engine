using Avalonia.Controls;
using Game_Engine.Core;
using Game_Engine.Core.AI;
using Game_Engine.Core.Blueprint;
using Game_Engine.Core.Component;
using Game_Engine.Core.Dialogue;
using Game_Engine.Core.Timeline;
using Game_Engine.Views.Inspector;

namespace Game_Engine.Views;

public partial class InspectorPanel : IInspectorHost
{
    Control IInspectorHost.MeshColliderTargetRow(GameObject owner, MeshCollider mc) => MeshColliderTargetRow(owner, mc);
    Control IInspectorHost.TriggerVolumeReactionsUI(GameObject owner, TriggerVolume tv) => TriggerVolumeReactionsUI(owner, tv);
    Control IInspectorHost.TerrainToolsRow(GameObject owner, Terrain t) => TerrainToolsRow(owner, t);
    Control IInspectorHost.TerrainBrushMasks(Terrain t) => TerrainBrushMasks(t);
    Control IInspectorHost.TerrainLayersUI(GameObject owner, Terrain t) => TerrainLayersUI(owner, t);
    Control IInspectorHost.PlanetToolsRow(GameObject owner, PlanetTerrain planet) => PlanetToolsRow(owner, planet);
    Control IInspectorHost.TreeInspectorUI(GameObject owner, Tree tree) => TreeInspectorUI(owner, tree);
    Control IInspectorHost.PlanetVegetationInspectorUI(GameObject owner, PlanetVegetationSystem veg) => PlanetVegetationInspectorUI(owner, veg);
    Control IInspectorHost.DialogueRunnerInspectorUI(DialogueRunner runner) => DialogueRunnerInspectorUI(runner);
    Control IInspectorHost.BehaviorTreeRunnerInspectorUI(BehaviorTreeRunner runner) => BehaviorTreeRunnerInspectorUI(runner);
    Control IInspectorHost.VisualBlueprintBehaviorInspectorUI(VisualBlueprintBehavior vb) => VisualBlueprintBehaviorInspectorUI(vb);
    Control IInspectorHost.TimelinePlayerInspectorUI(TimelinePlayer player) => TimelinePlayerInspectorUI(player);
    Control IInspectorHost.MeshLodGroupInspectorUI(MeshLodGroup g) => MeshLodGroupInspectorUI(g);
    Control IInspectorHost.ReflectionProbeGpuCubemapRow(ReflectionProbe probe) => ReflectionProbeGpuCubemapRow(probe);
    Control IInspectorHost.BuildVegetationActionsPanel(VegetationPainter vp) => BuildVegetationActionsPanel(vp);
    Control IInspectorHost.BuildPlanetAtmospherePresetPanel(PlanetAtmosphere pa) => BuildPlanetAtmospherePresetPanel(pa);
}
