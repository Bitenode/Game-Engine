using System.Collections.Generic;
using Avalonia.Controls;
using Game_Engine.Core;
using Game_Engine.Core.AI;
using Game_Engine.Core.Blueprint;
using Game_Engine.Core.Component;
using Game_Engine.Core.Dialogue;
using Game_Engine.Core.Timeline;

namespace Game_Engine.Views.Inspector;

sealed class MeshColliderInspector : ComponentInspector<MeshCollider>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, MeshCollider target)
    {
        yield return host.MeshColliderTargetRow(owner, target);
    }
}

sealed class TriggerVolumeInspector : ComponentInspector<TriggerVolume>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, TriggerVolume target)
    {
        yield return host.TriggerVolumeReactionsUI(owner, target);
    }
}

sealed class TerrainInspector : ComponentInspector<Terrain>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, Terrain target)
    {
        yield return host.TerrainToolsRow(owner, target);
        yield return host.TerrainBrushMasks(target);
        yield return host.TerrainLayersUI(owner, target);
    }
}

sealed class PlanetTerrainInspector : ComponentInspector<PlanetTerrain>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, PlanetTerrain target)
    {
        yield return host.PlanetToolsRow(owner, target);
    }
}

sealed class TreeInspector : ComponentInspector<Tree>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, Tree target)
    {
        yield return host.TreeInspectorUI(owner, target);
    }
}

sealed class PlanetVegetationInspector : ComponentInspector<PlanetVegetationSystem>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, PlanetVegetationSystem target)
    {
        yield return host.PlanetVegetationInspectorUI(owner, target);
    }
}

sealed class DialogueRunnerInspector : ComponentInspector<DialogueRunner>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, DialogueRunner target)
    {
        yield return host.DialogueRunnerInspectorUI(target);
    }
}

sealed class BehaviorTreeRunnerInspector : ComponentInspector<BehaviorTreeRunner>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, BehaviorTreeRunner target)
    {
        yield return host.BehaviorTreeRunnerInspectorUI(target);
    }
}

sealed class VisualBlueprintInspector : ComponentInspector<VisualBlueprintBehavior>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, VisualBlueprintBehavior target)
    {
        yield return host.VisualBlueprintBehaviorInspectorUI(target);
    }
}

sealed class TimelinePlayerInspector : ComponentInspector<TimelinePlayer>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, TimelinePlayer target)
    {
        yield return host.TimelinePlayerInspectorUI(target);
    }
}

sealed class MeshLodGroupInspector : ComponentInspector<MeshLodGroup>
{
    protected override IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, MeshLodGroup target)
    {
        yield return host.MeshLodGroupInspectorUI(target);
    }
}

sealed class ReflectionProbeInspector : ComponentInspector<ReflectionProbe>
{
    protected override IEnumerable<Control> BuildBodySuffix(IInspectorHost host, ReflectionProbe target)
    {
        yield return host.ReflectionProbeGpuCubemapRow(target);
    }
}

sealed class VegetationPainterInspector : ComponentInspector<VegetationPainter>
{
    protected override IEnumerable<Control> BuildBodySuffix(IInspectorHost host, VegetationPainter target)
    {
        yield return host.BuildVegetationActionsPanel(target);
    }
}

sealed class PlanetAtmosphereInspector : ComponentInspector<PlanetAtmosphere>
{
    protected override IEnumerable<Control> BuildBodySuffix(IInspectorHost host, PlanetAtmosphere target)
    {
        yield return host.BuildPlanetAtmospherePresetPanel(target);
    }
}
