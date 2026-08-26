using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Planet;

/// <summary>
/// Manages the quadtree for one face of the cube-sphere planet.
/// Handles split/merge decisions and neighbor lookup for transition masks.
/// </summary>
public sealed class FaceQuadtree
{
    public int Face { get; }
    public QuadNode Root { get; }

    /// <summary>How far beyond split distance to start generating child meshes.</summary>
    public const float PrefetchDistanceScale = 1.4f;

    public FaceQuadtree(int face)
    {
        Face = face;
        Root = new QuadNode(face, 0, 0f, 0f, 1f, 1f);
    }

    public void Update(SN.Vector3 cameraPos, PlanetConfig config, float approachSpeed, ref int remainingSplitBudget)
    {
        UpdateRecursive(Root, cameraPos, config, approachSpeed, ref remainingSplitBudget);
    }

    void UpdateRecursive(QuadNode node, SN.Vector3 cameraPos, PlanetConfig config, float approachSpeed, ref int remainingSplitBudget)
    {
        float worldRadius = Math.Max(0.001f, config.EffectiveWorldRadius);
        float splitMult = Math.Max(0.05f, config.LodDistanceMultiplier * Math.Max(0.05f, config.SplitDistanceScale));
        float mergeMult = splitMult * Math.Max(1.01f, config.MergeDistanceScale);
        float speedBoost = 1f + Math.Clamp(approachSpeed / 80f, 0f, 1f) * 0.6f;
        float prefetchMult = splitMult * PrefetchDistanceScale * speedBoost;

        // Inside the crust, refine a wide neighborhood so cave walls exist before
        // you walk up to them (otherwise heightfield parents pop to transvoxel late).
        float camR = cameraPos.Length();
        if (config.EnableCaves && camR < worldRadius * 1.08f)
        {
            splitMult *= 2.3f;
            prefetchMult *= 2.0f;
            mergeMult = splitMult * Math.Max(1.15f, config.MergeDistanceScale);
        }

        bool allowSplit = remainingSplitBudget > 0;
        bool shouldSplit = allowSplit && node.ShouldSplit(cameraPos, worldRadius, splitMult, config.MaxLodDepth);
        bool shouldPrefetch = allowSplit && node.ShouldPrefetch(cameraPos, worldRadius, prefetchMult, config.MaxLodDepth);

        if (node.IsLeaf)
        {
            if (shouldPrefetch)
                node.EnsurePrefetchChildren();
            else if (node.HasPrefetchChildren && (!allowSplit || node.ShouldMerge(cameraPos, worldRadius, mergeMult)))
                node.CancelPrefetch();

            if (shouldSplit && node.TryCommitSplit())
                remainingSplitBudget = Math.Max(0, remainingSplitBudget - 3);
        }
        else if (node.ShouldMerge(cameraPos, worldRadius, mergeMult))
        {
            bool allChildrenLeaves = true;
            for (int i = 0; i < 4; i++)
            {
                if (node.Children![i] != null && !node.Children[i]!.IsLeaf)
                {
                    allChildrenLeaves = false;
                    break;
                }
            }
            if (allChildrenLeaves)
            {
                node.Merge();
                remainingSplitBudget += 3;
            }
        }

        if (!node.IsLeaf)
        {
            for (int i = 0; i < 4; i++)
                UpdateRecursive(node.Children![i]!, cameraPos, config, approachSpeed, ref remainingSplitBudget);
        }
    }

    public List<QuadNode> GetLeafNodes()
    {
        var leaves = new List<QuadNode>();
        Root.CollectLeaves(leaves);
        return leaves;
    }

    public void CollectPrefetchNodes(List<QuadNode> nodes)
    {
        Root.CollectPrefetchChildren(nodes);
    }

    public void CollectRenderableNodes(List<QuadNode> nodes)
    {
        Root.CollectRenderable(nodes);
    }

    /// <summary>
    /// Find the leaf neighbor of a node. Direction: 0=-U, 1=+U, 2=-V, 3=+V.
    /// UV probes that leave this cube face wrap onto the adjacent face.
    /// </summary>
    public QuadNode? FindNeighbor(QuadNode node, int direction, FaceQuadtree[]? allFaces = null)
    {
        float epsilon = (node.U1 - node.U0) * 0.01f;
        float probeU, probeV;

        switch (direction)
        {
            case 0: probeU = node.U0 - epsilon; probeV = node.VCentre; break;
            case 1: probeU = node.U1 + epsilon; probeV = node.VCentre; break;
            case 2: probeU = node.UCentre; probeV = node.V0 - epsilon; break;
            case 3: probeU = node.UCentre; probeV = node.V1 + epsilon; break;
            default: return null;
        }

        if (probeU >= 0f && probeU <= 1f && probeV >= 0f && probeV <= 1f)
            return FindLeafAt(Root, probeU, probeV);

        var (nf, nu, nv) = CubeSphereMath.WrapFaceUV(node.Face, probeU, probeV);
        if (allFaces == null || nf < 0 || nf >= allFaces.Length)
            return null;
        return allFaces[nf].FindLeafAtUv(Math.Clamp(nu, 0f, 1f), Math.Clamp(nv, 0f, 1f));
    }

    public QuadNode? FindLeafAtUv(float u, float v)
        => FindLeafAt(Root, u, v);

    static QuadNode? FindLeafAt(QuadNode node, float u, float v)
    {
        if (node.IsLeaf) return node;

        float uMid = node.UCentre;
        float vMid = node.VCentre;

        int idx = (u < uMid ? 0 : 1) + (v < vMid ? 0 : 2);
        return FindLeafAt(node.Children![idx]!, u, v);
    }

    public void UpdateTransitionMasks(FaceQuadtree[]? allFaces = null)
    {
        var leaves = GetLeafNodes();
        foreach (var leaf in leaves)
        {
            byte mask = 0;
            int strides = 0;
            for (int dir = 0; dir < 4; dir++)
            {
                var neighbor = FindNeighbor(leaf, dir, allFaces);
                if (neighbor == null || neighbor.LodLevel >= leaf.LodLevel)
                    continue;
                mask |= (byte)(1 << dir);
                int delta = Math.Clamp(leaf.LodLevel - neighbor.LodLevel, 1, 5);
                strides |= (1 << delta) << (dir * 8);
            }
            if (leaf.TransitionMask != mask || leaf.TransitionStride != strides)
            {
                leaf.TransitionMask = mask;
                leaf.TransitionStride = strides;
                leaf.NeedsMeshRebuild = true;
            }
        }
    }

    /// <summary>Keep neighboring leaves within one LOD so T-junctions stay stitchable.</summary>
    public void ConstrainNeighborLod(FaceQuadtree[]? allFaces, ref int remainingSplitBudget)
    {
        if (remainingSplitBudget <= 0) return;

        var leaves = GetLeafNodes();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (remainingSplitBudget <= 0) return;
            var leaf = leaves[i];
            for (int dir = 0; dir < 4; dir++)
            {
                var neighbor = FindNeighbor(leaf, dir, allFaces);
                if (neighbor == null) continue;
                if (leaf.LodLevel <= neighbor.LodLevel + 1) continue;
                if (remainingSplitBudget <= 0) return;
                neighbor.EnsurePrefetchChildren();
                if (neighbor.Children != null)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        var child = neighbor.Children[c];
                        if (child != null && child.GeneratedMesh == null)
                            child.NeedsMeshRebuild = true;
                    }
                    if (neighbor.TryCommitSplit())
                        remainingSplitBudget = Math.Max(0, remainingSplitBudget - 3);
                }
            }
        }
    }
}
