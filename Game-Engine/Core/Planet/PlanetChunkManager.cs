using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game_Engine.Core.Biome;
using Game_Engine.Core;
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
    const int MaxConcurrentJobs = 4;
    int _lastAppliedEditCommands;
    int _lastDirtyLeavesFromEdits;

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
    public TransvoxelMeshData? ServerGenerateMeshForBounds(int face, float u0, float v0, float u1, float v1)
    {
        try
        {
            return _meshGen.Generate(face, u0, v0, u1, v1, Config.ChunkSize);
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
        public TransvoxelMeshData MeshData;
    }

    readonly struct EditCommand
    {
        public SN.Vector3 Center { get; init; }
        public float Radius { get; init; }
        public float DensityDelta { get; init; }
        public float Falloff { get; init; }
    }

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

        for (int f = 0; f < 6; f++)
        {
            Faces[f].Update(localCameraPos, Config);
            Faces[f].UpdateTransitionMasks();
        }

        EnforceLeafBudget(localCameraPos);
        ProcessEditCommands(planetCenter);

        int applyBudget = Math.Max(1, Config.MaxMeshAppliesPerUpdate);
        int applied = 0;
        while (applied < applyBudget && _completed.TryDequeue(out var job))
        {
            ApplyMesh(job.Node, job.MeshData);
            job.Node.IsGenerating = false;
            applied++;
        }

        // Prioritize nearest leaves first so chunks around the player refine first.
        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
            leaves.AddRange(Faces[f].GetLeafNodes());
        leaves.Sort((a, b) =>
        {
            float worldRadius = Config.EffectiveWorldRadius;
            float da = SN.Vector3.DistanceSquared(localCameraPos, a.WorldCentre(worldRadius));
            float db = SN.Vector3.DistanceSquared(localCameraPos, b.WorldCentre(worldRadius));
            return da.CompareTo(db);
        });

        int scheduleBudget = Math.Max(1, Config.MaxGenerationSchedulesPerUpdate);
        int scheduled = 0;
        int fillBudget = Math.Clamp(scheduleBudget / 3, 8, 128);
        int detailBudget = Math.Max(1, scheduleBudget - fillBudget);
        int activeChunkCap = Math.Max(64, Config.MaxActiveChunks);
        int nearCount = Math.Min(activeChunkCap, leaves.Count);
        for (int i = 0; i < nearCount; i++)
        {
            var leaf = leaves[i];
            if (scheduled >= detailBudget) break;
            if (_activeJobs >= MaxConcurrentJobs) break;
            if ((leaf.NeedsMeshRebuild || leaf.GeneratedMesh == null) && !leaf.IsGenerating)
            {
                leaf.NeedsMeshRebuild = false;
                leaf.IsGenerating = true;
                ScheduleGeneration(leaf);
                scheduled++;
            }
        }

        // Always reserve budget to fill missing meshes anywhere on the planet.
        // This prevents persistent "holes" when near-camera detail keeps consuming
        // all scheduling budget.
        int filled = 0;
        if (_activeJobs < MaxConcurrentJobs)
        {
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i];
                if (filled >= fillBudget) break;
                if (scheduled >= scheduleBudget) break;
                if (_activeJobs >= MaxConcurrentJobs) break;

                if (leaf.GeneratedMesh == null && !leaf.IsGenerating)
                {
                    leaf.NeedsMeshRebuild = false;
                    leaf.IsGenerating = true;
                    ScheduleGeneration(leaf);
                    scheduled++;
                    filled++;
                }
            }
        }

        // Spend any remaining budget on near-camera detail.
        if (scheduled < scheduleBudget && _activeJobs < MaxConcurrentJobs)
        {
            for (int i = 0; i < nearCount; i++)
            {
                var leaf = leaves[i];
                if (scheduled >= scheduleBudget) break;
                if (_activeJobs >= MaxConcurrentJobs) break;

                if (leaf.NeedsMeshRebuild && !leaf.IsGenerating)
                {
                    leaf.NeedsMeshRebuild = false;
                    leaf.IsGenerating = true;
                    ScheduleGeneration(leaf);
                    scheduled++;
                }
            }
        }
    }

    public void UpdateNoLod(SN.Vector3 planetCenter)
    {
        _lastAppliedEditCommands = 0;
        _lastDirtyLeavesFromEdits = 0;

        // Force a non-LOD representation for editor scene view:
        // one root chunk per face, always visible.
        for (int f = 0; f < 6; f++)
        {
            var root = Faces[f].Root;
            if (!root.IsLeaf)
                root.Merge();
            root.NeedsMeshRebuild = true;
            Faces[f].UpdateTransitionMasks();
        }

        ProcessEditCommands(planetCenter);

        int applyBudget = Math.Max(6, Config.MaxMeshAppliesPerUpdate);
        int applied = 0;
        while (applied < applyBudget && _completed.TryDequeue(out var job))
        {
            ApplyMesh(job.Node, job.MeshData);
            job.Node.IsGenerating = false;
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

    void ProcessEditCommands(SN.Vector3 planetCenter)
    {
        int cmdBudget = Math.Max(1, Config.MaxEditCommandsPerUpdate);
        int dirtyBudget = Math.Max(1, Config.MaxEditDirtyLeavesPerUpdate);
        if (_pendingEditCommands.IsEmpty || _editStore == null)
            return;

        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
            leaves.AddRange(Faces[f].GetLeafNodes());

        int processed = 0;
        int dirtied = 0;
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
                if (!IntersectsLeaf(leaf, cmd.Center - planetCenter, sphereRadius))
                    continue;

                if (!leaf.NeedsMeshRebuild)
                {
                    leaf.NeedsMeshRebuild = true;
                    dirtied++;
                }
            }
        }

        _lastAppliedEditCommands = processed;
        _lastDirtyLeavesFromEdits = dirtied;
    }

    /// <summary>Marks leaves overlapping a world-space edit region so clients can re-request streamed meshes after server edits.</summary>
    public void MarkLeavesDirtyNearWorldEdit(SN.Vector3 worldCenter, SN.Vector3 planetCenter, float sphereRadius)
    {
        var localCenter = worldCenter - planetCenter;
        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
            leaves.AddRange(Faces[f].GetLeafNodes());

        foreach (var leaf in leaves)
        {
            if (!IntersectsLeaf(leaf, localCenter, sphereRadius))
                continue;
            leaf.NeedsMeshRebuild = true;
            leaf.IsGenerating = false;
        }
    }

    bool IntersectsLeaf(QuadNode leaf, SN.Vector3 localCenter, float sphereRadius)
    {
        float worldRadius = Config.EffectiveWorldRadius;
        var leafCenter = leaf.WorldCentre(worldRadius);
        float leafRadius = Math.Max(leaf.WorldSize(worldRadius) * 0.75f, worldRadius * 0.01f);
        float maxDist = leafRadius + sphereRadius + Config.VoxelIsoSearchRange * 0.15f;
        return SN.Vector3.DistanceSquared(localCenter, leafCenter) <= maxDist * maxDist;
    }

    void EnforceLeafBudget(SN.Vector3 cameraPos)
    {
        int maxLeaves = Config.MaxLeafNodes;
        if (maxLeaves <= 0) return;

        // Multiple passes let us progressively merge far regions while keeping near detail.
        for (int pass = 0; pass < 8; pass++)
        {
            var leaves = new List<QuadNode>(1024);
            for (int f = 0; f < 6; f++)
                leaves.AddRange(Faces[f].GetLeafNodes());

            if (leaves.Count <= maxLeaves)
                return;

            // Merge farthest leaves first.
            leaves.Sort((a, b) =>
            {
                float worldRadius = Config.EffectiveWorldRadius;
                float da = SN.Vector3.DistanceSquared(cameraPos, a.WorldCentre(worldRadius));
                float db = SN.Vector3.DistanceSquared(cameraPos, b.WorldCentre(worldRadius));
                return db.CompareTo(da);
            });

            bool mergedAny = false;
            foreach (var leaf in leaves)
            {
                if (TryMergeParent(leaf))
                {
                    mergedAny = true;
                    break; // Recompute leaves after each successful merge for correctness.
                }
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
            if (c == null || !c.IsLeaf || c.IsGenerating)
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

        Task.Run(() =>
        {
            try
            {
                var meshData = _meshGen.Generate(face, u0, v0, u1, v1, resolution);
                _completed.Enqueue(new MeshJob { Node = node, MeshData = meshData });
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

    void ApplyMesh(QuadNode node, TransvoxelMeshData meshData)
    {
        if (meshData.IsEmpty)
        {
            // Keep existing mesh to avoid visual holes; retry later.
            node.NeedsMeshRebuild = true;
            return;
        }

        var mesh = meshData.ToEngineMesh();
        node.GeneratedMesh = mesh;
    }

    public void Dispose()
    {
        for (int f = 0; f < 6; f++)
        {
            var leaves = Faces[f].GetLeafNodes();
            foreach (var leaf in leaves)
            {
                leaf.GeneratedMesh = null;
                leaf.Chunk = null;
            }
        }
    }

    public List<QuadNode> GetRenderableLeaves()
    {
        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
            leaves.AddRange(Faces[f].GetLeafNodes());
        leaves.RemoveAll(l => l.GeneratedMesh == null);
        return leaves;
    }
}
