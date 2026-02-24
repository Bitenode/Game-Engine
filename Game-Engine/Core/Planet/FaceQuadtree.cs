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

    public FaceQuadtree(int face)
    {
        Face = face;
        Root = new QuadNode(face, 0, 0f, 0f, 1f, 1f);
    }

    public void Update(SN.Vector3 cameraPos, PlanetConfig config)
    {
        UpdateRecursive(Root, cameraPos, config);
    }

    void UpdateRecursive(QuadNode node, SN.Vector3 cameraPos, PlanetConfig config)
    {
        float splitMult = Math.Max(0.05f, config.LodDistanceMultiplier * Math.Max(0.05f, config.SplitDistanceScale));
        bool shouldSplit = node.ShouldSplit(cameraPos, config.Radius, splitMult, config.MaxLodDepth);

        if (shouldSplit && node.IsLeaf)
        {
            node.Split();
        }
        else if (!shouldSplit && !node.IsLeaf)
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
                node.Merge();
        }

        if (!node.IsLeaf)
        {
            for (int i = 0; i < 4; i++)
                UpdateRecursive(node.Children![i]!, cameraPos, config);
        }
    }

    public List<QuadNode> GetLeafNodes()
    {
        var leaves = new List<QuadNode>();
        Root.CollectLeaves(leaves);
        return leaves;
    }

    /// <summary>
    /// Find the leaf neighbor of a node in the given direction within this face.
    /// Direction: 0=-U, 1=+U, 2=-V, 3=+V.
    /// Returns null if the neighbor is on a different face.
    /// </summary>
    public QuadNode? FindNeighbor(QuadNode node, int direction)
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

        if (probeU < 0f || probeU > 1f || probeV < 0f || probeV > 1f)
            return null;

        return FindLeafAt(Root, probeU, probeV);
    }

    static QuadNode? FindLeafAt(QuadNode node, float u, float v)
    {
        if (node.IsLeaf) return node;

        float uMid = node.UCentre;
        float vMid = node.VCentre;

        int idx = (u < uMid ? 0 : 1) + (v < vMid ? 0 : 2);
        return FindLeafAt(node.Children![idx]!, u, v);
    }

    public void UpdateTransitionMasks()
    {
        var leaves = GetLeafNodes();
        foreach (var leaf in leaves)
        {
            byte mask = 0;
            for (int dir = 0; dir < 4; dir++)
            {
                var neighbor = FindNeighbor(leaf, dir);
                if (neighbor != null && neighbor.LodLevel < leaf.LodLevel)
                    mask |= (byte)(1 << dir);
            }
            leaf.TransitionMask = mask;
        }
    }
}
