using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game_Engine.Core.Biome;
using Game_Engine.Core;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Manages the 6 face quadtrees, queues async mesh generation for dirty
/// leaf nodes, and applies finished meshes to runtime chunk data on the main thread.
/// </summary>
public sealed class PlanetChunkManager
{
    public FaceQuadtree[] Faces { get; } = new FaceQuadtree[6];
    public PlanetConfig Config { get; }
    public BiomeMap BiomeMap { get; }

    readonly PlanetMeshGenerator _meshGen;
    readonly PlanetVoxelEditStore? _editStore;
    bool _clientStreaming;
    Action<QuadNode>? _clientMeshRequested;
    readonly ConcurrentQueue<MeshJob> _completed = new();
    readonly ConcurrentQueue<EditCommand> _pendingEditCommands = new();
    int _activeJobs;
    const int BaselineConcurrentJobs = 6;
    int _lastAppliedEditCommands;
    int _lastDirtyLeavesFromEdits;
    SN.Vector3 _lastLocalCamera = new(float.NaN);
    int _forceShellRebuildFrames = 4;
    readonly List<QuadNode> _leafScratch = new(256);
    readonly List<QuadNode> _coverageScratch = new(256);
    readonly List<QuadNode> _prefetchScratch = new(256);
    readonly List<QuadNode> _renderableCache = new(256);
    readonly List<QuadNode> _playRenderableScratch = new(64);
    readonly HashSet<QuadNode> _mergedParents = new();
    bool _renderableDirty = true;

    public int ActiveJobs => Volatile.Read(ref _activeJobs);
    public int PendingCompletedJobs => _completed.Count;
    public int PendingEditCommands => _pendingEditCommands.Count;
    public int LastAppliedEditCommands => _lastAppliedEditCommands;
    public int LastDirtyLeavesFromEdits => _lastDirtyLeavesFromEdits;

    public PlanetChunkManager(PlanetConfig config, BiomeMap biomeMap, PlanetVoxelEditStore? editStore = null)
    {
        Config = config;
        BiomeMap = biomeMap;
        _editStore = editStore;
        _meshGen = new PlanetMeshGenerator(config, biomeMap, editStore);

        for (int f = 0; f < 6; f++)
            Faces[f] = new FaceQuadtree(f);
    }

    /// <summary>
    /// When enabled, <see cref="ScheduleGeneration"/> does not run local jobs; it invokes <paramref name="onMeshRequested"/> instead (network client streaming).
    /// </summary>
    public void SetClientStreamingMode(bool enabled, Action<QuadNode>? onMeshRequested)
    {
        _clientStreaming = enabled;
        _clientMeshRequested = onMeshRequested;
    }

    /// <summary>Server-only: generate mesh data for the given face/UV bounds (same resolution as <see cref="PlanetConfig.ChunkSize"/>).</summary>
    public TransvoxelMeshData? ServerGenerateMeshForBounds(int face, float u0, float v0, float u1, float v1, byte transitionMask = 0, int lodLevel = 0)
    {
        try
        {
            return _meshGen.Generate(face, u0, v0, u1, v1, Config.ChunkSize, transitionMask, lodLevel).Mesh;
        }
        catch (Exception ex)
        {
            Log.Info($"[PlanetChunkManager] ServerGenerateMeshForBounds failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Apply mesh from the network and clear <see cref="QuadNode.IsGenerating"/>.</summary>
    public void ApplyNetworkMesh(QuadNode node, TransvoxelMeshData meshData)
    {
        ApplyMesh(node, meshData);
        node.IsGenerating = false;
    }

    struct MeshJob
    {
        public QuadNode Node;
        public int GenerationToken;
        public TransvoxelMeshData MeshData;
        public VoxelChunk? Chunk;
    }

    readonly struct EditCommand
    {
        public SN.Vector3 Center { get; init; }
        public float Radius { get; init; }
        public float DensityDelta { get; init; }
        public float Falloff { get; init; }
    }

    public void RequestFullShellRebuild(int frames = 8)
    {
        _forceShellRebuildFrames = Math.Max(_forceShellRebuildFrames, Math.Max(1, frames));
        for (int f = 0; f < Faces.Length; f++)
        {
            var leaves = Faces[f].GetLeafNodes();
            for (int i = 0; i < leaves.Count; i++)
                leaves[i].NeedsMeshRebuild = true;
        }
    }

    /// <summary>
    /// After loading <c>.planetvox</c> or clearing edits: drop stale async meshes,
    /// reset the quadtree, and schedule a full crust rebuild.
    /// </summary>
    public void ResetAfterVoxelEditsLoaded()
    {
        while (_completed.TryDequeue(out _)) { }
        Volatile.Write(ref _activeJobs, 0);

        for (int f = 0; f < Faces.Length; f++)
            DisposeNodeRecursive(Faces[f].Root);

        for (int f = 0; f < Faces.Length; f++)
            Faces[f] = new FaceQuadtree(f);

        RequestFullShellRebuild(16);
        _renderableDirty = true;
        _lastLocalCamera = new SN.Vector3(float.NaN);
    }

    static void DisposeNodeRecursive(QuadNode node)
    {
        node.InvalidateGeneration();
        GpuMeshReleaseQueue.Enqueue(node.GeneratedMesh);
        node.GeneratedMesh = null;
        node.Chunk = null;
        if (node.Children == null)
            return;
        for (int i = 0; i < 4; i++)
        {
            var child = node.Children[i];
            if (child != null)
                DisposeNodeRecursive(child);
        }
    }

    /// <param name="center">Brush center in planet-local unscaled space (<see cref="PlanetSpace"/>).</param>
    public void EnqueueSphereEdit(SN.Vector3 center, float radius, float densityDelta, float falloff)
    {
        if (radius <= 0.001f || Math.Abs(densityDelta) <= 1e-6f)
            return;

        _pendingEditCommands.Enqueue(new EditCommand
        {
            Center = center,
            Radius = radius,
            DensityDelta = densityDelta,
            Falloff = Math.Clamp(falloff, 0f, 1f),
        });
    }

    public void Update(SN.Vector3 cameraPos, SN.Vector3 planetCenter)
    {
        _lastAppliedEditCommands = 0;
        _lastDirtyLeavesFromEdits = 0;
        var localCameraPos = cameraPos - planetCenter;
        float dt = Math.Max(0f, (float)Time.deltaTime);
        float approachSpeed = 0f;
        if (dt > 1e-4f && !float.IsNaN(_lastLocalCamera.X))
            approachSpeed = SN.Vector3.Distance(localCameraPos, _lastLocalCamera) / dt;
        _lastLocalCamera = localCameraPos;

        int maxLeaves = ResolveMaxLeaves();
        int leafCount = CollectLeavesInto(_leafScratch);
        bool startedOverBudget = maxLeaves > 0 && leafCount > maxLeaves;
        int remainingSplits = maxLeaves > 0 ? Math.Max(0, maxLeaves - leafCount) : int.MaxValue;

        for (int f = 0; f < 6; f++)
            Faces[f].Update(localCameraPos, Config, approachSpeed, ref remainingSplits);

        if (!startedOverBudget && remainingSplits > 0)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                for (int f = 0; f < 6; f++)
                    Faces[f].ConstrainNeighborLod(Faces, ref remainingSplits);
            }
        }

        EnforceLeafBudget(localCameraPos, maxLeaves);
        CancelPrefetchIfOverBudget(maxLeaves);
        _renderableDirty = true;

        for (int f = 0; f < 6; f++)
            Faces[f].UpdateTransitionMasks(Faces);
        ProcessEditCommands();

        int applyBudget = Math.Max(1, Config.MaxMeshAppliesPerUpdate);
        int applied = 0;
        while (applied < applyBudget && _completed.TryDequeue(out var job))
        {
            if (!TryApplyCompletedJob(job))
                continue;
            applied++;
        }

        // Prioritize nearest leaves first so chunks around the player refine first.
        CollectLeavesInto(_leafScratch);
        var leaves = _leafScratch;

        if (_forceShellRebuildFrames > 0)
            _forceShellRebuildFrames--;

        float worldRadius = Config.EffectiveWorldRadius;
        float camR = localCameraPos.Length();
        if (Config.EnableCaves && camR < worldRadius * 1.08f)
        {
            float maxCell = MathF.Max(8f, Config.VolumetricMaxCellSize);
            int res = Math.Max(1, Config.ChunkSize);
            float planetR = Config.Radius;
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i];
                if (leaf.Chunk != null || leaf.IsGenerating)
                    continue;
                if (leaf.GeneratedMesh != null && !leaf.NeedsMeshRebuild)
                    continue;
                if (leaf.WorldSize(planetR) / res > maxCell)
                    continue;
                leaf.NeedsMeshRebuild = true;
            }
        }
        int CompareByCamera(QuadNode a, QuadNode b)
        {
            int lod = a.LodLevel.CompareTo(b.LodLevel);
            if (lod != 0) return lod;
            float da = a.CameraPriorityDistance(localCameraPos, worldRadius);
            float db = b.CameraPriorityDistance(localCameraPos, worldRadius);
            return da.CompareTo(db);
        }

        int scheduleBudget = ResolveScheduleBudget(approachSpeed);
        int scheduled = 0;

        if (SceneService.PlayMode)
        {
            _playRenderableScratch.Clear();
            for (int f = 0; f < 6; f++)
                Faces[f].CollectRenderableNodes(_playRenderableScratch);
            var renderable = _playRenderableScratch;
            for (int i = 0; i < renderable.Count; i++)
            {
                var node = renderable[i];
                if (!node.NeedsMeshRebuild || node.IsGenerating)
                    continue;
                if (scheduled >= scheduleBudget || _activeJobs >= MaxConcurrentJobs)
                    break;
                node.NeedsMeshRebuild = false;
                node.IsGenerating = true;
                ScheduleGeneration(node);
                scheduled++;
            }
        }

        bool TrySchedule(QuadNode node)
        {
            if (scheduled >= scheduleBudget || _activeJobs >= MaxConcurrentJobs)
                return false;
            if (node.IsGenerating)
                return false;
            if (node.GeneratedMesh != null && !node.NeedsMeshRebuild)
                return false;
            node.NeedsMeshRebuild = false;
            node.IsGenerating = true;
            ScheduleGeneration(node);
            scheduled++;
            return true;
        }

        // Coverage first: every face, coarsest patches first. Near-camera LOD5
        // used to consume the whole job budget, so the next cube face stayed a
        // hole (water/atmosphere) until you orbited over it.
        _coverageScratch.Clear();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].GeneratedMesh == null && !leaves[i].IsGenerating)
                _coverageScratch.Add(leaves[i]);
        }
        _prefetchScratch.Clear();
        for (int f = 0; f < 6; f++)
            Faces[f].CollectPrefetchNodes(_prefetchScratch);
        var coverage = _coverageScratch;
        var prefetch = _prefetchScratch;
        for (int i = 0; i < prefetch.Count; i++)
        {
            if (prefetch[i].GeneratedMesh == null && !prefetch[i].IsGenerating)
                coverage.Add(prefetch[i]);
        }
        coverage.Sort(CompareByCamera);
        for (int i = 0; i < coverage.Count; i++)
            TrySchedule(coverage[i]);

        prefetch.Sort((a, b) =>
        {
            float da = a.CameraPriorityDistance(localCameraPos, worldRadius);
            float db = b.CameraPriorityDistance(localCameraPos, worldRadius);
            return da.CompareTo(db);
        });
        for (int i = 0; i < prefetch.Count; i++)
            TrySchedule(prefetch[i]);

        leaves.Sort((a, b) =>
        {
            float da = a.CameraPriorityDistance(localCameraPos, worldRadius);
            float db = b.CameraPriorityDistance(localCameraPos, worldRadius);
            return da.CompareTo(db);
        });
        for (int i = 0; i < leaves.Count; i++)
            TrySchedule(leaves[i]);
    }

    public void UpdateNoLod(SN.Vector3 planetCenter)
    {
        _ = planetCenter;
        _lastAppliedEditCommands = 0;
        _lastDirtyLeavesFromEdits = 0;

        // Force a non-LOD representation for editor scene view:
        // one root chunk per face, always visible.
        for (int f = 0; f < 6; f++)
        {
            var root = Faces[f].Root;
            if (!root.IsLeaf)
                root.Merge();
            else
                root.CancelPrefetch();
            root.NeedsMeshRebuild = true;
            Faces[f].UpdateTransitionMasks();
        }

        ProcessEditCommands();

        int applyBudget = Math.Max(6, Config.MaxMeshAppliesPerUpdate);
        int applied = 0;
        while (applied < applyBudget && _completed.TryDequeue(out var job))
        {
            if (!TryApplyCompletedJob(job))
                continue;
            applied++;
        }

        // Schedule all root chunks so full planet appears quickly.
        for (int f = 0; f < 6; f++)
        {
            var root = Faces[f].Root;
            if (_activeJobs >= MaxConcurrentJobs) break;
            if ((root.NeedsMeshRebuild || root.GeneratedMesh == null) && !root.IsGenerating)
            {
                root.NeedsMeshRebuild = false;
                root.IsGenerating = true;
                ScheduleGeneration(root);
            }
        }
    }

    void ProcessEditCommands()
    {
        int cmdBudget = Math.Max(1, Config.MaxEditCommandsPerUpdate);
        int dirtyBudget = Math.Max(1, Config.MaxEditDirtyLeavesPerUpdate);
        if (_pendingEditCommands.IsEmpty || _editStore == null)
            return;

        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
        {
            leaves.AddRange(Faces[f].GetLeafNodes());
            Faces[f].CollectPrefetchNodes(leaves);
        }

        int processed = 0;
        int dirtied = 0;
        DensityGenerator.ComputeCrustBounds(Config, _editStore, out _, out float radialSpan);
        float crustPad = radialSpan * 0.35f;
        while (processed < cmdBudget && _pendingEditCommands.TryDequeue(out var cmd))
        {
            _editStore.AddSphere(cmd.Center, cmd.Radius, cmd.DensityDelta, cmd.Falloff);
            processed++;

            float sphereRadius = cmd.Radius + Math.Max(2f, MathF.Abs(cmd.DensityDelta));
            for (int i = 0; i < leaves.Count; i++)
            {
                if (dirtied >= dirtyBudget)
                    break;

                var leaf = leaves[i];
                if (!IntersectsLeaf(leaf, cmd.Center, sphereRadius, crustPad))
                    continue;

                MarkNodeAndAncestorsDirty(leaf, ref dirtied, dirtyBudget);
            }

            if (SceneService.PlayMode)
                ApplyPlayModeEditVisual(cmd.Center, sphereRadius);
        }

        _lastAppliedEditCommands = processed;
        _lastDirtyLeavesFromEdits = dirtied;
        if (processed > 0)
            _renderableDirty = true;
    }

    void MarkNodeAndAncestorsDirty(QuadNode node, ref int dirtied, int dirtyBudget)
    {
        for (var cur = node; cur != null; cur = cur.Parent)
        {
            cur.IsGenerating = false;
            if (cur.NeedsMeshRebuild)
                continue;
            if (dirtied >= dirtyBudget)
                return;
            cur.NeedsMeshRebuild = true;
            dirtied++;
        }
    }

    /// <summary>
    /// Play-mode: remesh overlapping leaves (editor dirty/schedule path) with a
    /// small budget. Coarse heightfield shells may get a one-frame in-place preview.
    /// </summary>
    public void ApplyPlayModeEditVisual(SN.Vector3 localCenter, float sphereRadius)
    {
        if (_editStore == null)
            return;

        if (TryPreviewDeformCoarseShells(localCenter, sphereRadius))
            _renderableDirty = true;

        DirtyOverlappingLeaves(localCenter, sphereRadius);

        int playBudget = Math.Clamp(MaxConcurrentJobs, 4, 8);
        ScheduleRenderablesNearEdit(localCenter, sphereRadius, maxNodes: playBudget);
    }

    bool TryPreviewDeformCoarseShells(SN.Vector3 localCenter, float sphereRadius)
    {
        DensityGenerator.ComputeCrustBounds(Config, _editStore!, out _, out float radialSpan);
        float crustPad = radialSpan * 0.35f;
        float maxCell = MathF.Max(8f, Config.VolumetricMaxCellSize);

        var renderable = new List<QuadNode>(64);
        for (int f = 0; f < 6; f++)
            Faces[f].CollectRenderableNodes(renderable);

        renderable.Sort((a, b) =>
        {
            float da = SN.Vector3.DistanceSquared(localCenter, a.WorldCentre(Config.Radius));
            float db = SN.Vector3.DistanceSquared(localCenter, b.WorldCentre(Config.Radius));
            return da.CompareTo(db);
        });

        var sampler = _meshGen.Sampler;
        bool any = false;
        int touched = 0;
        for (int i = 0; i < renderable.Count && touched < 2; i++)
        {
            var node = renderable[i];
            if (!IntersectsLeaf(node, localCenter, sphereRadius, crustPad))
                continue;

            var mesh = node.GeneratedMesh;
            if (mesh == null)
                continue;

            float spacing = PlanetShellDeformer.EstimateVertexSpacing(node, Config.Radius, Config.ChunkSize);
            if (spacing <= maxCell)
                continue;

            if (PlanetShellDeformer.TryDeformMesh(mesh, sampler, localCenter, sphereRadius, spacing))
            {
                any = true;
                touched++;
            }
        }

        return any;
    }

    void DirtyOverlappingLeaves(SN.Vector3 localCenter, float sphereRadius)
    {
        DensityGenerator.ComputeCrustBounds(Config, _editStore, out _, out float radialSpan);
        float crustPad = radialSpan * 0.35f;
        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
        {
            leaves.AddRange(Faces[f].GetLeafNodes());
            Faces[f].CollectPrefetchNodes(leaves);
        }

        int dirtied = 0;
        int dirtyBudget = Math.Max(1, Config.MaxEditDirtyLeavesPerUpdate);
        for (int i = 0; i < leaves.Count && dirtied < dirtyBudget; i++)
        {
            if (!IntersectsLeaf(leaves[i], localCenter, sphereRadius, crustPad))
                continue;
            MarkNodeAndAncestorsDirty(leaves[i], ref dirtied, dirtyBudget);
        }
        _lastDirtyLeavesFromEdits = Math.Max(_lastDirtyLeavesFromEdits, dirtied);
    }

    /// <summary>
    /// Queue async remesh for the visible chunk(s) overlapping a play-mode edit.
    /// </summary>
    public void ScheduleRenderablesNearEdit(SN.Vector3 localCenter, float sphereRadius, int maxNodes = 1)
    {
        if (_editStore == null || maxNodes <= 0)
            return;

        DensityGenerator.ComputeCrustBounds(Config, _editStore, out _, out float radialSpan);
        float crustPad = radialSpan * 0.35f;

        var renderable = new List<QuadNode>(64);
        for (int f = 0; f < 6; f++)
            Faces[f].CollectRenderableNodes(renderable);

        renderable.Sort((a, b) =>
        {
            float da = SN.Vector3.DistanceSquared(localCenter, a.WorldCentre(Config.Radius));
            float db = SN.Vector3.DistanceSquared(localCenter, b.WorldCentre(Config.Radius));
            return da.CompareTo(db);
        });

        int queued = 0;
        for (int i = 0; i < renderable.Count && queued < maxNodes; i++)
        {
            var node = renderable[i];
            if (!IntersectsLeaf(node, localCenter, sphereRadius, crustPad))
                continue;

            for (var cur = node; cur != null; cur = cur.Parent)
            {
                cur.IsGenerating = false;
                cur.NeedsMeshRebuild = true;
            }

            if (_activeJobs >= MaxConcurrentJobs || node.IsGenerating)
                continue;

            node.NeedsMeshRebuild = false;
            node.IsGenerating = true;
            ScheduleGeneration(node);
            queued++;
        }

        if (queued > 0)
            _renderableDirty = true;
    }

    /// <summary>Marks leaves overlapping a world-space edit region so clients can re-request streamed meshes after server edits.</summary>
    public void MarkLeavesDirtyNearWorldEdit(SN.Vector3 worldCenter, SN.Vector3 planetCenter, float worldSphereRadius)
    {
        var localCenter = PlanetSpace.WorldToLocal(worldCenter, planetCenter, Config.WorldRadiusScale);
        float localRadius = PlanetSpace.WorldToLocalLength(worldSphereRadius, Config.WorldRadiusScale);
        DensityGenerator.ComputeCrustBounds(Config, _editStore, out _, out float radialSpan);
        float crustPad = radialSpan * 0.35f;
        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
        {
            leaves.AddRange(Faces[f].GetLeafNodes());
            Faces[f].CollectPrefetchNodes(leaves);
        }

        foreach (var leaf in leaves)
        {
            if (!IntersectsLeaf(leaf, localCenter, localRadius, crustPad))
                continue;
            leaf.NeedsMeshRebuild = true;
            leaf.IsGenerating = false;
            if (leaf.Parent != null)
                leaf.Parent.NeedsMeshRebuild = true;
        }
    }

    bool IntersectsLeaf(QuadNode leaf, SN.Vector3 localCenter, float sphereRadius, float crustPad)
    {
        float planetR = Config.Radius;
        var leafCenter = leaf.WorldCentre(planetR);
        float leafRadius = Math.Max(leaf.WorldSize(planetR) * 0.75f, planetR * 0.01f);
        float maxDist = leafRadius + sphereRadius + crustPad;
        return SN.Vector3.DistanceSquared(localCenter, leafCenter) <= maxDist * maxDist;
    }

    int MaxConcurrentJobs => ResolveMaxConcurrentJobs();

    int ResolveMaxConcurrentJobs()
    {
        if (!Config.EnableAdaptiveScheduling)
            return BaselineConcurrentJobs;
        int scheduleCap = Math.Max(BaselineConcurrentJobs, Config.MaxGenerationSchedulesPerUpdate);
        return Math.Clamp(scheduleCap / 2, BaselineConcurrentJobs, 12);
    }

    int ResolveScheduleBudget(float approachSpeed)
    {
        int configured = Math.Max(1, Config.MaxGenerationSchedulesPerUpdate);
        if (!Config.EnableAdaptiveScheduling)
            return configured;

        float t = Math.Clamp(approachSpeed / 80f, 0f, 1f) * Math.Max(0f, Config.AdaptiveMotionBoost);
        float worldR = Math.Max(0.001f, Config.EffectiveWorldRadius);
        float camR = float.IsNaN(_lastLocalCamera.X) ? worldR : _lastLocalCamera.Length();
        if (camR < worldR * 1.08f)
            t += Math.Max(0f, Config.AdaptiveAltitudeBoost);
        int maxLeaves = ResolveMaxLeaves();
        if (maxLeaves > 0)
            t += Math.Clamp(_leafScratch.Count / (float)maxLeaves, 0f, 1f) * Math.Max(0f, Config.AdaptiveActiveChunkBoost);
        t = Math.Clamp(t, 0f, 1f);

        int min = Math.Min(configured, Math.Max(1, Config.AdaptiveMinScheduleBudget));
        int max = Math.Min(configured, Math.Max(min, Config.AdaptiveMaxScheduleBudget));
        return min + (int)MathF.Round((max - min) * t);
    }

    int ResolveMaxLeaves()
    {
        int maxLeaves = Config.MaxLeafNodes;
        if (Config.MaxActiveChunks > 0)
            maxLeaves = maxLeaves <= 0 ? Config.MaxActiveChunks : Math.Min(maxLeaves, Config.MaxActiveChunks);
        return maxLeaves;
    }

    int CollectLeavesInto(List<QuadNode> dest)
    {
        dest.Clear();
        for (int f = 0; f < 6; f++)
            Faces[f].Root.CollectLeaves(dest);
        return dest.Count;
    }

    void CancelPrefetchIfOverBudget(int maxLeaves)
    {
        if (maxLeaves <= 0) return;
        if (CollectLeavesInto(_leafScratch) < maxLeaves) return;
        for (int i = 0; i < _leafScratch.Count; i++)
        {
            if (_leafScratch[i].HasPrefetchChildren)
                _leafScratch[i].CancelPrefetch();
        }
    }

    void EnforceLeafBudget(SN.Vector3 cameraPos, int maxLeaves)
    {
        if (maxLeaves <= 0) return;

        float worldRadius = Config.EffectiveWorldRadius;
        for (int pass = 0; pass < 24; pass++)
        {
            CollectLeavesInto(_leafScratch);
            int extra = _leafScratch.Count - maxLeaves;
            if (extra <= 0)
                return;

            _leafScratch.Sort((a, b) =>
            {
                float da = a.CameraPriorityDistance(cameraPos, worldRadius);
                float db = b.CameraPriorityDistance(cameraPos, worldRadius);
                return db.CompareTo(da);
            });

            _mergedParents.Clear();
            bool mergedAny = false;
            for (int i = 0; i < _leafScratch.Count && extra > 0; i++)
            {
                var parent = _leafScratch[i].Parent;
                if (parent == null || _mergedParents.Contains(parent))
                    continue;
                if (!TryMergeParent(_leafScratch[i]))
                    continue;
                _mergedParents.Add(parent);
                extra -= 3;
                mergedAny = true;
            }

            if (!mergedAny)
                break;
        }
    }

    static bool TryMergeParent(QuadNode leaf)
    {
        var parent = leaf.Parent;
        if (parent == null || parent.IsLeaf || parent.Children == null) return false;

        for (int i = 0; i < 4; i++)
        {
            var c = parent.Children[i];
            if (c == null || !c.IsLeaf)
                return false;
        }

        parent.Merge();
        return true;
    }

    void ScheduleGeneration(QuadNode node)
    {
        if (_clientStreaming && _clientMeshRequested != null)
        {
            try
            {
                _clientMeshRequested(node);
            }
            catch (Exception ex)
            {
                Log.Warning($"[PlanetChunkManager] Client streaming request failed: {ex.Message}");
                node.IsGenerating = false;
                node.NeedsMeshRebuild = true;
            }
            return;
        }

        Interlocked.Increment(ref _activeJobs);

        int face = node.Face;
        float u0 = node.U0, v0 = node.V0, u1 = node.U1, v1 = node.V1;
        int resolution = Config.ChunkSize;
        byte transitionMask = node.TransitionMask;
        int transitionStride = node.TransitionStride;
        int lodLevel = node.LodLevel;

        int token = node.GenerationToken;

        Task.Run(() =>
        {
            try
            {
                var result = _meshGen.Generate(face, u0, v0, u1, v1, resolution, transitionMask, lodLevel, transitionStride);
                _completed.Enqueue(new MeshJob
                {
                    Node = node,
                    GenerationToken = token,
                    MeshData = result.Mesh,
                    Chunk = result.Chunk,
                });
            }
            catch (Exception ex)
            {
                Log.Info($"[PlanetChunkManager] Mesh generation failed for face={face} lod={node.LodLevel}: {ex.Message}");
                node.IsGenerating = false;
                node.NeedsMeshRebuild = true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeJobs);
            }
        });
    }

    bool TryApplyCompletedJob(MeshJob job)
    {
        if (job.GenerationToken != job.Node.GenerationToken)
        {
            job.Node.IsGenerating = false;
            return false;
        }

        ApplyMesh(job.Node, job.MeshData, job.Chunk);
        job.Node.IsGenerating = false;
        return true;
    }

    void ApplyMesh(QuadNode node, TransvoxelMeshData meshData, VoxelChunk? chunk = null)
    {
        if (meshData.IsEmpty)
        {
            // Keep existing mesh to avoid visual holes; retry later.
            node.NeedsMeshRebuild = true;
            node.IsGenerating = false;
            return;
        }

        GpuMeshReleaseQueue.Enqueue(node.GeneratedMesh);
        node.GeneratedMesh = meshData.ToEngineMesh();
        if (chunk != null)
            node.Chunk = chunk;
        _renderableDirty = true;
    }

    public void Dispose()
    {
        for (int f = 0; f < 6; f++)
        {
            var leaves = Faces[f].GetLeafNodes();
            foreach (var leaf in leaves)
            {
                GpuMeshReleaseQueue.Enqueue(leaf.GeneratedMesh);
                leaf.GeneratedMesh = null;
                leaf.Chunk = null;
            }
        }
    }

    public List<QuadNode> GetRenderableLeaves()
    {
        if (_renderableDirty)
        {
            _renderableCache.Clear();
            for (int f = 0; f < 6; f++)
                Faces[f].CollectRenderableNodes(_renderableCache);
            _renderableDirty = false;
        }
        return _renderableCache;
    }
}
