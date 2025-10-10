#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// SpeedTree-style LOD + billboard (no SDK).
    /// Attach to a tree GameObject that already has a MeshFilter + MeshRenderer.
    /// - LOD0,1,2 are optional Mesh references (highest -> lowest).
    /// - Distances are start distances in world units at which the given LOD activates.
    /// - Beyond ImpostorStart, we switch to a camera-facing quad sampling an atlas slice.
    /// </summary>
    public sealed class TreeLOD : Behavior
    {
        // --- LOD meshes (optional). If null, we fall back to the MeshFilter.Mesh.
        [Persist] public Mesh Lod0 { get; set; }  // highest detail
        [Persist] public Mesh Lod1 { get; set; }
        [Persist] public Mesh Lod2 { get; set; }

        // --- Thresholds (meters). 0 or negative means "disabled".
        [Persist] public float Lod1Start { get; set; } = 15f;
        [Persist] public float Lod2Start { get; set; } = 30f;
        [Persist] public float ImpostorStart { get; set; } = 55f;

        // --- Billboard atlas (optional)
        [Persist] public Texture2D BillboardAtlas { get; set; }
        [Persist] public int AtlasCols { get; set; } = 8; // yaw slices around
        [Persist] public int AtlasRows { get; set; } = 1; // keep 1 for now
        [Persist] public float BillboardHeight { get; set; } = 6f; // world units
        [Persist] public float BillboardWidthMul { get; set; } = 0.6f; // width = height * mul
        [Persist] public bool UprightYAxis { get; set; } = true; // keeps Y up

        /// Pick mesh by distance. Returns null if billboard should be used.
        public Mesh PickMeshOrNullForBillboard(float dist, Mesh fallback)
        {
            if (ImpostorStart > 0f && BillboardAtlas != null && dist >= ImpostorStart)
                return null; // billboard

            if (Lod2 != null && Lod2Start > 0f && dist >= Lod2Start) return Lod2;
            if (Lod1 != null && Lod1Start > 0f && dist >= Lod1Start) return Lod1;
            if (Lod0 != null) return Lod0;
            return fallback;
        }

        /// Compute yaw-based atlas slice (0..Cols-1) from camera and object positions.
        public int ComputeYawSlice(in SN.Vector3 camPos, in SN.Vector3 objPos)
        {
            var toCam = camPos - objPos;
            float yaw = (float)Math.Atan2(toCam.X, toCam.Z); // [-π, π]
            if (yaw < 0f) yaw += 6.2831853f;                 // [0, 2π)
            int slices = Math.Max(1, AtlasCols);
            int idx = (int)Math.Round((yaw / 6.2831853f) * slices) % slices;
            return idx;
        }
    }
}
