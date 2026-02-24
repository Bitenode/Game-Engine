using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Component;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Manages the 6 face quadtrees, queues async mesh generation for dirty
/// leaf nodes, and applies finished meshes to their GameObjects on the main thread.
/// </summary>
public sealed class PlanetChunkManager
{
    public FaceQuadtree[] Faces { get; } = new FaceQuadtree[6];
    public PlanetConfig Config { get; }
    public BiomeMap BiomeMap { get; }

    readonly PlanetMeshGenerator _meshGen;
    readonly ConcurrentQueue<MeshJob> _completed = new();
    int _activeJobs;
    const int MaxConcurrentJobs = 4;

    public int ActiveJobs => Volatile.Read(ref _activeJobs);
    public int PendingCompletedJobs => _completed.Count;

    public PlanetChunkManager(PlanetConfig config, BiomeMap biomeMap)
    {
        Config = config;
        BiomeMap = biomeMap;
        _meshGen = new PlanetMeshGenerator(config, biomeMap);

        for (int f = 0; f < 6; f++)
            Faces[f] = new FaceQuadtree(f);
    }

    struct MeshJob
    {
        public QuadNode Node;
        public TransvoxelMeshData MeshData;
    }

    public void Update(SN.Vector3 cameraPos, GameObject parentGO)
    {
        for (int f = 0; f < 6; f++)
        {
            Faces[f].Update(cameraPos, Config);
        }

        EnforceLeafBudget(cameraPos);

        int applyBudget = Math.Max(1, Config.MaxMeshAppliesPerUpdate);
        int applied = 0;
        while (applied < applyBudget && _completed.TryDequeue(out var job))
        {
            ApplyMesh(job.Node, job.MeshData, parentGO);
            job.Node.IsGenerating = false;
            applied++;
        }

        // Prioritize nearest leaves first so chunks around the player refine first.
        var leaves = new List<QuadNode>(1024);
        for (int f = 0; f < 6; f++)
            leaves.AddRange(Faces[f].GetLeafNodes());
        leaves.Sort((a, b) =>
        {
            float da = SN.Vector3.DistanceSquared(cameraPos, a.WorldCentre(Config.Radius));
            float db = SN.Vector3.DistanceSquared(cameraPos, b.WorldCentre(Config.Radius));
            return da.CompareTo(db);
        });

        // Hard active chunk cap: unload distant chunk GameObjects to keep runtime cost bounded.
        int activeChunkCap = Math.Max(64, Config.MaxActiveChunks);
        for (int i = activeChunkCap; i < leaves.Count; i++)
            UnloadLeaf(leaves[i]);

        int scheduleBudget = Math.Max(1, Config.MaxGenerationSchedulesPerUpdate);
        int scheduled = 0;
        int nearCount = Math.Min(activeChunkCap, leaves.Count);
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

    static void UnloadLeaf(QuadNode leaf)
    {
        if (leaf.IsGenerating) return; // let in-flight work complete naturally
        if (leaf.ChunkGO != null)
        {
            leaf.ChunkGO.RemoveFromParent();
            leaf.ChunkGO = null;
        }
        leaf.GeneratedMesh = null;
        leaf.Chunk = null;
        leaf.NeedsMeshRebuild = true;
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
                float da = SN.Vector3.DistanceSquared(cameraPos, a.WorldCentre(Config.Radius));
                float db = SN.Vector3.DistanceSquared(cameraPos, b.WorldCentre(Config.Radius));
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
            catch
            {
                node.IsGenerating = false;
            }
            finally
            {
                Interlocked.Decrement(ref _activeJobs);
            }
        });
    }

    void ApplyMesh(QuadNode node, TransvoxelMeshData meshData, GameObject parentGO)
    {
        if (meshData.IsEmpty)
        {
            if (node.ChunkGO != null)
            {
                node.ChunkGO.RemoveFromParent();
                node.ChunkGO = null;
            }
            return;
        }

        var mesh = meshData.ToEngineMesh();
        node.GeneratedMesh = mesh;

        if (node.ChunkGO == null)
        {
            var go = new GameObject($"PlanetChunk_{node.Face}_{node.LodLevel}_{node.U0:F2}_{node.V0:F2}");

            var mf = new MeshFilter();
            go.AddBehavior(mf);

            parentGO.AddChild(go);
            node.ChunkGO = go;
        }

        var meshFilter = node.ChunkGO.Behaviors
            .OfType<MeshFilter>().FirstOrDefault();
        if (meshFilter != null)
        {
            meshFilter.Mesh = mesh;
        }
    }

    public void Dispose()
    {
        for (int f = 0; f < 6; f++)
        {
            var leaves = Faces[f].GetLeafNodes();
            foreach (var leaf in leaves)
            {
                if (leaf.ChunkGO != null)
                {
                    leaf.ChunkGO.RemoveFromParent();
                    leaf.ChunkGO = null;
                }
            }
        }
    }
}
