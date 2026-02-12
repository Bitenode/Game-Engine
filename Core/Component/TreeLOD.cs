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
        [Persist] public Mesh? Lod0 { get; set; }  // highest detail
        [Persist] public Mesh? Lod1 { get; set; }
        [Persist] public Mesh? Lod2 { get; set; }

        // --- Thresholds (meters). 0 or negative means "disabled".
        [Persist] public float Lod1Start { get; set; } = 15f;
        [Persist] public float Lod2Start { get; set; } = 30f;
        [Persist] public float ImpostorStart { get; set; } = 55f;

        // --- Billboard atlas (optional)
        [Persist] public Texture2D? BillboardAtlas { get; set; }
        [Persist] public int AtlasCols { get; set; } = 8; // yaw slices around
        [Persist] public int AtlasRows { get; set; } = 1; // keep 1 for now
        [Persist] public float BillboardHeight { get; set; } = 6f; // world units
        [Persist] public float BillboardWidthMul { get; set; } = 0.6f; // width = height * mul
        [Persist] public bool UprightYAxis { get; set; } = true; // keeps Y up

        // --- Runtime tracking (NOT persisted, NOT notifying) ---
        /// <summary>Current active LOD level: 0 = full, 1 = medium, 2 = low, 3 = billboard.</summary>
        public int CurrentLod { get; private set; }

        /// <summary>The original mesh to restore when switching back from billboard.</summary>
        private Mesh? _originalMesh;

        /// <summary>Cached billboard quad mesh (reused across frames).</summary>
        private Mesh? _billboardQuad;

        /// <summary>Last computed yaw slice for billboard atlas UV.</summary>
        public int LastYawSlice { get; private set; }

        /// <summary>Whether currently showing the billboard impostor.</summary>
        public bool IsBillboard => CurrentLod == 3;

        /// <summary>The mesh currently assigned by this LOD system (to avoid redundant sets).</summary>
        private Mesh? _currentMesh;

        /// Pick mesh by distance. Returns null if billboard should be used.
        public Mesh? PickMeshOrNullForBillboard(float dist, Mesh? fallback)
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
            float yaw = (float)Math.Atan2(toCam.X, toCam.Z); // [-PI, PI]
            if (yaw < 0f) yaw += 6.2831853f;                 // [0, 2PI)
            int slices = Math.Max(1, AtlasCols);
            int idx = (int)Math.Round((yaw / 6.2831853f) * slices) % slices;
            return idx;
        }

        /// <summary>
        /// Update LOD based on camera distance. Only swaps MeshFilter mesh when the LOD
        /// level actually changes. Call once per frame from the render loop.
        /// </summary>
        public void UpdateLOD(SN.Vector3 cameraPos)
        {
            if (gameObject == null) return;

            // Find MeshFilter without LINQ (hot path)
            MeshFilter? mf = null;
            var behaviors = gameObject.Behaviors;
            for (int i = 0; i < behaviors.Count; i++)
            {
                if (behaviors[i] is MeshFilter f) { mf = f; break; }
            }
            if (mf == null) return;

            // Store original mesh on first call
            if (_originalMesh == null && mf.Mesh != null)
                _originalMesh = mf.Mesh;

            // Compute distance from camera to object center
            var worldMat = TransformUtil.WorldFromTransform(gameObject.Transform);
            var objPos = new SN.Vector3(worldMat.M41, worldMat.M42, worldMat.M43);
            float dist = SN.Vector3.Distance(cameraPos, objPos);

            // Pick LOD mesh
            var picked = PickMeshOrNullForBillboard(dist, _originalMesh);

            if (picked == null)
            {
                // Billboard mode — only rebuild when yaw slice changes
                int newLod = 3;
                int newSlice = ComputeYawSlice(cameraPos, objPos);

                if (CurrentLod != newLod || LastYawSlice != newSlice || _billboardQuad == null)
                {
                    CurrentLod = newLod;
                    LastYawSlice = newSlice;
                    EnsureBillboardQuad(cameraPos, objPos);
                    if (_billboardQuad != null && !ReferenceEquals(mf.Mesh, _billboardQuad))
                    {
                        mf.Mesh = _billboardQuad;
                        _currentMesh = _billboardQuad;
                    }
                }
            }
            else
            {
                // Mesh LOD — only swap when the mesh actually changes
                int newLod;
                if (Lod2 != null && ReferenceEquals(picked, Lod2)) newLod = 2;
                else if (Lod1 != null && ReferenceEquals(picked, Lod1)) newLod = 1;
                else newLod = 0;

                if (CurrentLod != newLod || !ReferenceEquals(_currentMesh, picked))
                {
                    CurrentLod = newLod;
                    mf.Mesh = picked;
                    _currentMesh = picked;
                }
            }
        }

        /// <summary>
        /// Build or update the cached billboard quad. Reuses the same Mesh object
        /// and mutates its arrays to avoid GPU re-upload overhead.
        /// </summary>
        private void EnsureBillboardQuad(SN.Vector3 camPos, SN.Vector3 objPos)
        {
            float h = BillboardHeight;
            float w = h * BillboardWidthMul;
            float halfW = w * 0.5f;

            // Billboard orientation: face camera, upright Y
            SN.Vector3 forward;
            if (UprightYAxis)
            {
                var toCamera = camPos - objPos;
                toCamera.Y = 0;
                float len = toCamera.Length();
                forward = len > 1e-6f ? toCamera / len : SN.Vector3.UnitZ;
            }
            else
            {
                forward = SN.Vector3.Normalize(camPos - objPos);
            }

            var right = SN.Vector3.Normalize(SN.Vector3.Cross(SN.Vector3.UnitY, forward));

            // Quad corners
            var bl = -right * halfW;
            var br = right * halfW;
            var tl = -right * halfW + SN.Vector3.UnitY * h;
            var tr = right * halfW + SN.Vector3.UnitY * h;

            // UV from atlas slice
            int cols = Math.Max(1, AtlasCols);
            int rows = Math.Max(1, AtlasRows);
            float uMin = LastYawSlice / (float)cols;
            float uMax = (LastYawSlice + 1) / (float)cols;
            float vMax = 1f / rows;

            if (_billboardQuad == null)
            {
                // First time: create the mesh
                var verts = new SN.Vector3[] { bl, br, tr, tl };
                var norms = new SN.Vector3[] { forward, forward, forward, forward };
                var uvs = new SN.Vector2[]
                {
                    new(uMin, vMax), new(uMax, vMax),
                    new(uMax, 0f),   new(uMin, 0f)
                };
                int[] tris = { 0, 1, 2, 0, 2, 3 };
                int[] lines = { 0, 1, 1, 2, 2, 3, 3, 0 };

                _billboardQuad = new Mesh(verts, lines, tris)
                {
                    Normals = norms,
                    UVs = uvs
                };
            }
            else
            {
                // Reuse existing arrays — mutate in place (same Mesh reference = same GPU cache entry)
                var v = _billboardQuad.Vertices;
                v[0] = bl; v[1] = br; v[2] = tr; v[3] = tl;

                var n = _billboardQuad.Normals!;
                n[0] = forward; n[1] = forward; n[2] = forward; n[3] = forward;

                var uv = _billboardQuad.UVs!;
                uv[0] = new SN.Vector2(uMin, vMax);
                uv[1] = new SN.Vector2(uMax, vMax);
                uv[2] = new SN.Vector2(uMax, 0f);
                uv[3] = new SN.Vector2(uMin, 0f);
            }
        }
    }
}
