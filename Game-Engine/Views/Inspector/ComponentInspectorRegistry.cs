using System.Collections.Generic;
using Avalonia.Controls;
using Game_Engine.Core;

namespace Game_Engine.Views.Inspector;

/// <summary>Static catalog of built-in <see cref="IComponentInspector"/> implementations.</summary>
public static class ComponentInspectorRegistry
{
    static readonly IComponentInspector[] All =
    {
        new MeshColliderInspector(),
        new TriggerVolumeInspector(),
        new TerrainInspector(),
        new PlanetTerrainInspector(),
        new TreeInspector(),
        new PlanetVegetationInspector(),
        new DialogueRunnerInspector(),
        new BehaviorTreeRunnerInspector(),
        new VisualBlueprintInspector(),
        new TimelinePlayerInspector(),
        new MeshLodGroupInspector(),
        new ReflectionProbeInspector(),
        new VegetationPainterInspector(),
        new PlanetAtmosphereInspector(),
    };

    public static IEnumerable<IComponentInspector> For(Behavior b)
    {
        foreach (var inspector in All)
            if (inspector.CanHandle(b))
                yield return inspector;
    }

    public static bool IsHiddenProperty(Behavior b, string propertyName)
    {
        foreach (var inspector in All)
        {
            if (inspector.CanHandle(b) && inspector.IsHiddenProperty(b, propertyName))
                return true;
        }
        return false;
    }

    public static IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, Behavior b)
    {
        foreach (var inspector in All)
        {
            if (!inspector.CanHandle(b)) continue;
            foreach (var extra in inspector.BuildChrome(host, owner, b))
                yield return extra;
        }
    }

    public static IEnumerable<Control> BuildBodySuffix(IInspectorHost host, Behavior b)
    {
        foreach (var inspector in All)
        {
            if (!inspector.CanHandle(b)) continue;
            foreach (var extra in inspector.BuildBodySuffix(host, b))
                yield return extra;
        }
    }
}
