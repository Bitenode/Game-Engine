using System;
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
    public bool IsLeaf => Children == null;

    public VoxelChunk? Chunk { get; set; }
    public Mesh? GeneratedMesh { get; set; }

    /// <summary>
    /// 4 bits for the 4 edges of this quad (±U, ±V), set when the neighbor across
    /// that edge is at a coarser LOD, meaning we need transition cells on that side.
    /// Bit 0 = -U, 1 = +U, 2 = -V, 3 = +V.
    /// </summary>
    public byte TransitionMask { get; set; }

    public bool NeedsMeshRebuild { get; set; } = true;

    public volatile bool IsGenerating;

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

    public bool ShouldSplit(SN.Vector3 cameraPos, float radius, float lodMultiplier, int maxDepth)
    {
        if (LodLevel >= maxDepth) return false;
        float size = WorldSize(radius);
        float dist = SN.Vector3.Distance(cameraPos, WorldCentre(radius));
        return dist < size * lodMultiplier;
    }

    public void Split()
    {
        if (!IsLeaf) return;

        float uMid = UCentre;
        float vMid = VCentre;
        int childLod = LodLevel + 1;

        Children = new QuadNode[4];
        Children[0] = new QuadNode(Face, childLod, U0, V0, uMid, vMid, this);
        Children[1] = new QuadNode(Face, childLod, uMid, V0, U1, vMid, this);
        Children[2] = new QuadNode(Face, childLod, U0, vMid, uMid, V1, this);
        Children[3] = new QuadNode(Face, childLod, uMid, vMid, U1, V1, this);
    }

    public void Merge()
    {
        if (IsLeaf) return;

        for (int i = 0; i < 4; i++)
        {
            var child = Children![i];
            if (child != null)
            {
                child.Merge();
                DisposeChunkData(child);
            }
        }
        Children = null;
        NeedsMeshRebuild = true;
    }

    static void DisposeChunkData(QuadNode node)
    {
        node.Chunk = null;
        node.GeneratedMesh = null;
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
}
