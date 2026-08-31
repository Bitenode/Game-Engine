using System;
using System.Collections.Generic;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core.Voxel;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// One node in a per-face quadtree. Leaf nodes own a VoxelChunk and a generated Mesh.
/// Rendering is runtime-managed from GeneratedMesh (no per-leaf GameObjects).
/// </summary>
public sealed class QuadNode
{
    public int Face { get; }
    public int LodLevel { get; }
    public float U0 { get; }
    public float V0 { get; }
    public float U1 { get; }
    public float V1 { get; }

    public QuadNode? Parent { get; }
    public QuadNode?[]? Children { get; private set; }

    /// <summary>
    /// True until a split is committed. Prefetch may allocate <see cref="Children"/>
    /// while this node remains the visible leaf.
    /// </summary>
    bool _splitCommitted;

    public bool IsLeaf => !_splitCommitted;
    public bool HasPrefetchChildren => Children != null && !_splitCommitted;

    public VoxelChunk? Chunk { get; set; }
    public Mesh? GeneratedMesh { get; set; }
    public Mesh? GeneratedWaterMesh { get; set; }

    /// <summary>
    /// Max mesh radius per leaf UV cell. Built once from the generated mesh
    /// so LOD join ramps / T-junction snaps are in the player collider
    /// without walking verts every frame.
    /// </summary>
    public const int StandGridSize = 16;
    public float[]? StandRadiusGrid { get; set; }

    /// <summary>
    /// 4 bits for the 4 edges of this quad (±U, ±V), set when the neighbor across
    /// that edge is at a coarser LOD, meaning we need transition cells on that side.
    /// Bit 0 = -U, 1 = +U, 2 = -V, 3 = +V.
    /// </summary>
    public byte TransitionMask { get; set; }

    /// <summary>
    /// Per-edge vertex stride for T-junction snaps (2^LOD delta).
    /// Byte 0 = -U, 1 = +U, 2 = -V, 3 = +V.
    /// </summary>
    public int TransitionStride { get; set; }

    public bool NeedsMeshRebuild { get; set; } = true;

    public volatile bool IsGenerating;

    /// <summary>Incremented when this node is merged/cancelled so stale async jobs are ignored.</summary>
    public int GenerationToken { get; private set; }

    public void InvalidateGeneration()
    {
        GenerationToken++;
        IsGenerating = false;
    }

    public QuadNode(int face, int lod, float u0, float v0, float u1, float v1, QuadNode? parent = null)
    {
        Face = face;
        LodLevel = lod;
        U0 = u0;
        V0 = v0;
        U1 = u1;
        V1 = v1;
        Parent = parent;
    }

    public float UCentre => (U0 + U1) * 0.5f;
    public float VCentre => (V0 + V1) * 0.5f;

    public SN.Vector3 CentreDirection => CubeSphereMath.FaceUVToDirection(Face, UCentre, VCentre);

    public SN.Vector3 WorldCentre(float radius) => CentreDirection * radius;

    public float WorldSize(float radius)
    {
        var a = CubeSphereMath.FaceUVToDirection(Face, U0, V0) * radius;
        var b = CubeSphereMath.FaceUVToDirection(Face, U1, V1) * radius;
        return SN.Vector3.Distance(a, b);
    }

    public bool ShouldSplit(SN.Vector3 cameraPos, float worldRadius, float splitMultiplier, int maxDepth)
    {
        if (LodLevel >= maxDepth) return false;
        float size = WorldSize(worldRadius);
        float dist = CameraPriorityDistance(cameraPos, worldRadius);
        return dist < size * splitMultiplier;
    }

    public bool ShouldPrefetch(SN.Vector3 cameraPos, float worldRadius, float prefetchMultiplier, int maxDepth)
        => ShouldSplit(cameraPos, worldRadius, prefetchMultiplier, maxDepth);

    public bool ShouldMerge(SN.Vector3 cameraPos, float worldRadius, float mergeMultiplier)
    {
        float size = WorldSize(worldRadius);
        float dist = CameraPriorityDistance(cameraPos, worldRadius);
        return dist > size * mergeMultiplier;
    }

    /// <summary>
    /// Distance used for split/merge/scheduling. From orbit this is the nearest
    /// outer-surface corner. Inside or just under the crust it is the distance to
    /// this patch's spherical sector at the camera radius, so looking around in
    /// fly-cam still refines cave walls without orbiting onto them.
    /// </summary>
    public float CameraPriorityDistance(SN.Vector3 cameraPos, float worldRadius)
    {
        float camLenSq = cameraPos.LengthSquared();
        if (camLenSq < 1e-8f)
            return worldRadius * 0.5f;

        float camR = MathF.Sqrt(camLenSq);
        bool nearCrust = camR < worldRadius * 1.12f;
        if (!nearCrust)
            return MathF.Sqrt(MinSampleDistanceSq(cameraPos, worldRadius));

        var dir = cameraPos / camR;
        var (face, u, v) = CubeSphereMath.SphereToCube(dir);
        float innerR = worldRadius * 0.08f;
        float outerR = worldRadius * 1.06f;
        float sampleR = Math.Clamp(camR, innerR, outerR);

        if (face == Face)
        {
            float cu = Math.Clamp(u, U0, U1);
            float cv = Math.Clamp(v, V0, V1);
            var closest = CubeSphereMath.FaceUVToDirection(Face, cu, cv) * sampleR;
            return SN.Vector3.Distance(cameraPos, closest);
        }

        return MathF.Sqrt(MinSampleDistanceSq(cameraPos, sampleR));
    }

    float MinSampleDistanceSq(SN.Vector3 cameraPos, float sampleRadius)
    {
        float d = SN.Vector3.DistanceSquared(cameraPos, CubeSphereMath.FaceUVToDirection(Face, UCentre, VCentre) * sampleRadius);
        d = MathF.Min(d, SN.Vector3.DistanceSquared(cameraPos, CubeSphereMath.FaceUVToDirection(Face, U0, V0) * sampleRadius));
        d = MathF.Min(d, SN.Vector3.DistanceSquared(cameraPos, CubeSphereMath.FaceUVToDirection(Face, U1, V0) * sampleRadius));
        d = MathF.Min(d, SN.Vector3.DistanceSquared(cameraPos, CubeSphereMath.FaceUVToDirection(Face, U0, V1) * sampleRadius));
        d = MathF.Min(d, SN.Vector3.DistanceSquared(cameraPos, CubeSphereMath.FaceUVToDirection(Face, U1, V1) * sampleRadius));
        return d;
    }

    /// <summary>Allocate child nodes for mesh generation without hiding this node's mesh.</summary>
    public void EnsurePrefetchChildren()
    {
        if (_splitCommitted || Children != null) return;
        AllocateChildren();
    }

    /// <summary>Make children the visible leaves only when all four already have meshes.</summary>
    public bool TryCommitSplit()
    {
        if (_splitCommitted || Children == null || !ChildrenHaveMeshes())
            return false;
        _splitCommitted = true;
        return true;
    }

    public bool ChildrenHaveMeshes()
    {
        if (Children == null) return false;
        for (int i = 0; i < 4; i++)
        {
            var c = Children[i];
            if (c == null || c.GeneratedMesh == null)
                return false;
        }
        return true;
    }

    public void CancelPrefetch()
    {
        if (_splitCommitted || Children == null) return;
        for (int i = 0; i < 4; i++)
        {
            var child = Children[i];
            if (child == null) continue;
            child.CancelPrefetch();
            DisposeChunkData(child);
        }
        Children = null;
    }

    public void Split()
    {
        if (_splitCommitted) return;
        EnsurePrefetchChildren();
        if (ChildrenHaveMeshes())
            _splitCommitted = true;
    }

    public void Merge()
    {
        if (Children == null)
        {
            _splitCommitted = false;
            if (GeneratedMesh == null)
                NeedsMeshRebuild = true;
            return;
        }

        bool childrenDirty = false;
        bool hadChildMeshes = false;
        for (int i = 0; i < 4; i++)
        {
            var child = Children[i];
            if (child == null) continue;
            if (child.GeneratedMesh != null)
                hadChildMeshes = true;
            if (SubtreeNeedsRebuild(child))
                childrenDirty = true;
            child.Merge();
            DisposeChunkData(child);
        }
        Children = null;
        _splitCommitted = false;
        // Child leaves held higher-detail geometry; the pre-split parent mesh is stale.
        if (GeneratedMesh == null || childrenDirty || hadChildMeshes)
            NeedsMeshRebuild = true;
    }

    static bool SubtreeNeedsRebuild(QuadNode node)
    {
        if (node.NeedsMeshRebuild)
            return true;
        if (node.Children == null)
            return false;
        for (int i = 0; i < 4; i++)
        {
            var child = node.Children[i];
            if (child != null && SubtreeNeedsRebuild(child))
                return true;
        }
        return false;
    }

    void AllocateChildren()
    {
        float uMid = UCentre;
        float vMid = VCentre;
        int childLod = LodLevel + 1;

        Children = new QuadNode[4];
        Children[0] = new QuadNode(Face, childLod, U0, V0, uMid, vMid, this);
        Children[1] = new QuadNode(Face, childLod, uMid, V0, U1, vMid, this);
        Children[2] = new QuadNode(Face, childLod, U0, vMid, uMid, V1, this);
        Children[3] = new QuadNode(Face, childLod, uMid, vMid, U1, V1, this);
    }

    static void DisposeChunkData(QuadNode node)
    {
        GpuMeshReleaseQueue.Enqueue(node.GeneratedMesh);
        GpuMeshReleaseQueue.Enqueue(node.GeneratedWaterMesh);
        node.Chunk = null;
        node.GeneratedMesh = null;
        node.GeneratedWaterMesh = null;
        node.StandRadiusGrid = null;
        node.InvalidateGeneration();
    }

    public void CollectLeaves(System.Collections.Generic.List<QuadNode> leaves)
    {
        if (IsLeaf)
        {
            leaves.Add(this);
            return;
        }
        for (int i = 0; i < 4; i++)
            Children![i]?.CollectLeaves(leaves);
    }

    public void CollectPrefetchChildren(System.Collections.Generic.List<QuadNode> nodes)
    {
        if (Children == null) return;

        if (!_splitCommitted)
        {
            for (int i = 0; i < 4; i++)
            {
                var c = Children[i];
                if (c != null)
                    nodes.Add(c);
            }
            return;
        }

        for (int i = 0; i < 4; i++)
            Children[i]?.CollectPrefetchChildren(nodes);
    }

    public void CollectRenderable(System.Collections.Generic.List<QuadNode> nodes)
    {
        if (IsLeaf)
        {
            if (GeneratedMesh != null)
                nodes.Add(this);
            return;
        }

        if (ChildrenHaveMeshes())
        {
            for (int i = 0; i < 4; i++)
                Children![i]?.CollectRenderable(nodes);
            return;
        }

        if (GeneratedMesh != null)
        {
            nodes.Add(this);
            return;
        }

        // Split in progress and parent mesh already dropped: draw any ready children
        // instead of an empty patch (sky hole).
        if (Children != null)
        {
            for (int i = 0; i < 4; i++)
                Children[i]?.CollectRenderable(nodes);
        }
    }

    public static float[]? BuildStandRadiusGrid(
        int face, float u0, float v0, float u1, float v1,
        List<SN.Vector3> positions)
    {
        if (positions == null || positions.Count == 0)
            return null;
        return FillStandRadiusGrid(face, u0, v0, u1, v1, positions.Count, i => positions[i]);
    }

    public static float[]? BuildStandRadiusGrid(
        int face, float u0, float v0, float u1, float v1,
        SN.Vector3[] positions)
    {
        if (positions == null || positions.Length == 0)
            return null;
        var grid = new float[StandGridSize * StandGridSize];
        FillStandRadiusGrid(face, u0, v0, u1, v1, positions.Length, i => positions[i], grid);
        return grid;
    }

    static float[] FillStandRadiusGrid(
        int face, float u0, float v0, float u1, float v1,
        int count, Func<int, SN.Vector3> at, float[]? grid = null)
    {
        int n = StandGridSize;
        grid ??= new float[n * n];
        float du = MathF.Max(1e-6f, u1 - u0);
        float dv = MathF.Max(1e-6f, v1 - v0);

        for (int i = 0; i < count; i++)
        {
            var p = at(i);
            float r = p.Length();
            if (r < 1e-5f)
                continue;
            var dir = p / r;
            var (f, u, v) = CubeSphereMath.SphereToCube(dir);
            if (f != face)
                continue;
            float tu = (u - u0) / du;
            float tv = (v - v0) / dv;
            if (tu < -0.02f || tu > 1.02f || tv < -0.02f || tv > 1.02f)
                continue;
            int ix = Math.Clamp((int)(tu * n), 0, n - 1);
            int iy = Math.Clamp((int)(tv * n), 0, n - 1);
            int idx = iy * n + ix;
            if (r > grid[idx])
                grid[idx] = r;
        }

        return grid;
    }

    public bool TrySampleStandLocalRadius(SN.Vector3 sphereDir, out float localR)
    {
        localR = 0f;
        var grid = StandRadiusGrid;
        if (grid == null)
            return false;
        if (sphereDir.LengthSquared() < 1e-12f)
            return false;

        var (face, u, v) = CubeSphereMath.SphereToCube(SN.Vector3.Normalize(sphereDir));
        if (face != Face)
            return false;

        float tu = (u - U0) / MathF.Max(1e-6f, U1 - U0);
        float tv = (v - V0) / MathF.Max(1e-6f, V1 - V0);
        tu = Math.Clamp(tu, 0f, 1f);
        tv = Math.Clamp(tv, 0f, 1f);

        int n = StandGridSize;
        float fx = tu * (n - 1);
        float fy = tv * (n - 1);
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(n - 1, x0 + 1);
        int y1 = Math.Min(n - 1, y0 + 1);

        float best = 0f;
        float s00 = grid[y0 * n + x0];
        float s10 = grid[y0 * n + x1];
        float s01 = grid[y1 * n + x0];
        float s11 = grid[y1 * n + x1];
        if (s00 > best) best = s00;
        if (s10 > best) best = s10;
        if (s01 > best) best = s01;
        if (s11 > best) best = s11;
        if (best <= 1e-4f)
            return false;

        localR = best;
        return true;
    }
}
