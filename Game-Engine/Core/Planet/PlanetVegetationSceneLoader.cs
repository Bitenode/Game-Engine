#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Planet;

/// <summary>
/// After <see cref="SceneService.LoadFromFile"/>, reads each planet’s <c>.planet</c> JSON on a thread-pool thread
/// and applies vegetation on the UI thread so disk + deserialize don’t block the main thread.
/// </summary>
public static class PlanetVegetationSceneLoader
{
    static int s_hydrationGeneration;

    public static void ScheduleHydrateAfterSceneReplace()
    {
        int generation = Interlocked.Increment(ref s_hydrationGeneration);
        var terrains = new List<PlanetTerrain>();
        foreach (var root in SceneService.Root)
            Collect(root, terrains);
        if (terrains.Count == 0) return;

        foreach (var terrain in terrains)
        {
            var relPath = terrain.PlanetAssetPath;
            if (string.IsNullOrWhiteSpace(relPath)) continue;
            if (terrain.gameObject == null) continue;
            var vegetation = terrain.gameObject.Behaviors
                .OfType<PlanetVegetationSystem>()
                .FirstOrDefault(v => v.IsActiveAndEnabled);
            if (vegetation == null)
                continue;

            terrain.AsyncVegetationHydrationPending = true;
            string pathCaptured = relPath;
            var terrainRef = new WeakReference<PlanetTerrain>(terrain);
            _ = Task.Run(() => BackgroundLoadAndPost(pathCaptured, terrainRef, generation));
        }
    }

    static void Collect(GameObject go, List<PlanetTerrain> list)
    {
        foreach (var b in go.Behaviors)
            if (b is PlanetTerrain pt) list.Add(pt);
        foreach (var c in go.Children)
            Collect(c, list);
    }

    static void BackgroundLoadAndPost(
        string projectRelativePath,
        WeakReference<PlanetTerrain> terrainRef,
        int generation)
    {
        try
        {
            if (!PlanetAssetIO.TryLoad(projectRelativePath, out var data, out _) || data?.Vegetation == null)
            {
                PostClearPending(terrainRef, generation);
                return;
            }

            var pl = data.Vegetation.Placements;
            if (pl == null || pl.Length == 0)
            {
                PostClearPending(terrainRef, generation);
                return;
            }

            var copy = data.Vegetation.Clone();
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != Volatile.Read(ref s_hydrationGeneration)
                    || !terrainRef.TryGetTarget(out var terrain))
                    return;
                try
                {
                    Hydrate(terrain, copy);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[PlanetVegetationSceneLoader] UI hydrate failed: {ex.Message}");
                }
                finally
                {
                    terrain.AsyncVegetationHydrationPending = false;
                }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Warning($"[PlanetVegetationSceneLoader] Background load failed ({projectRelativePath}): {ex.Message}");
            PostClearPending(terrainRef, generation);
        }
    }

    static void PostClearPending(WeakReference<PlanetTerrain> terrainRef, int generation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == Volatile.Read(ref s_hydrationGeneration)
                && terrainRef.TryGetTarget(out var terrain))
                terrain.AsyncVegetationHydrationPending = false;
        }, DispatcherPriority.Background);
    }

    static void Hydrate(PlanetTerrain terrain, PlanetVegetationAssetData veg)
    {
        if (terrain.gameObject == null) return;
        var sys = terrain.gameObject.Behaviors
            .OfType<PlanetVegetationSystem>()
            .FirstOrDefault(v => v.IsActiveAndEnabled);
        if (sys == null) return;
        sys.ImportAssetData(veg);
        // Without a warmup, MaxAssetSpawnsPerUpdate (often 2) + mixed tree/grass order meant grass could
        // stay invisible for a long time after load; trees dominated the spawn budget.
        sys.WarmSpawnAfterDeferredImport();
        Log.Info($"[PlanetVegetationSceneLoader] Async-applied saved vegetation ({sys.StoredPlacementCount} placements) on '{terrain.gameObject.Name}'.");
        SceneService.NotifyChanged();
    }
}
