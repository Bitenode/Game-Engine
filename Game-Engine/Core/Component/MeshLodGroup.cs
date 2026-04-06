#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Unity-style mesh LOD for static meshes. Add to the same GameObject as <see cref="MeshFilter"/> + <see cref="MeshRenderer"/>.
    /// LOD0 is closest to the camera; assign lower-detail meshes to Lod1–3 and distance thresholds in world units.
    /// If Lod0 is null, the mesh currently on <see cref="MeshFilter"/> when this first runs is treated Lod0.
    /// </summary>
    [ComponentCategory("Rendering")]
    public sealed class MeshLodGroup : Behavior
    {
        [Persist] public Mesh? Lod0 { get; set; }
        [Persist] public Mesh? Lod1 { get; set; }
        [Persist] public Mesh? Lod2 { get; set; }
        [Persist] public Mesh? Lod3 { get; set; }

        /// <summary>World-distance at which Lod1 is used (must be &gt; 0). Ignored if Lod1 is null.</summary>
        [Persist] public float Lod1Distance { get; set; } = 20f;

        /// <summary>World-distance at which Lod2 is used. Ignored if Lod2 is null or threshold ≤ 0.</summary>
        [Persist] public float Lod2Distance { get; set; } = 45f;

        /// <summary>World-distance at which Lod3 is used. Ignored if Lod3 is null or threshold ≤ 0.</summary>
        [Persist] public float Lod3Distance { get; set; } = 90f;

        /// <summary>0 = Lod0 (or base mesh), 1–3 = explicit LOD slot used last frame.</summary>
        public int CurrentLodLevel { get; private set; }

        private Mesh? _capturedBaseMesh;
        private Mesh? _lastAssigned;

        /// <summary>
        /// Select mesh by distance from <paramref name="cameraPos"/> and assign to <see cref="MeshFilter"/>.
        /// Called from Scene/Game view each LOD tick (same scheduling as tree LOD).
        /// </summary>
        public void UpdateLOD(SN.Vector3 cameraPos)
        {
            if (gameObject == null) return;

            MeshFilter? mf = null;
            var behaviors = gameObject.Behaviors;
            for (int i = 0; i < behaviors.Count; i++)
                if (behaviors[i] is MeshFilter f && f.Enabled) { mf = f; break; }
            if (mf == null) return;

            if (_capturedBaseMesh == null && mf.Mesh != null)
                _capturedBaseMesh = mf.Mesh;

            var worldMat = TransformUtil.WorldFromTransform(gameObject.Transform);
            var objPos = new SN.Vector3(worldMat.M41, worldMat.M42, worldMat.M43);
            float dist = SN.Vector3.Distance(cameraPos, objPos);

            Mesh? pick = PickMesh(dist, out int level);
            CurrentLodLevel = level;

            if (pick == null) return;
            if (!ReferenceEquals(_lastAssigned, pick))
            {
                mf.Mesh = pick;
                _lastAssigned = pick;
            }
        }

        Mesh? PickMesh(float dist, out int level)
        {
            if (Lod3Distance > 0f && Lod3 != null && dist >= Lod3Distance)
            {
                level = 3;
                return Lod3;
            }
            if (Lod2Distance > 0f && Lod2 != null && dist >= Lod2Distance)
            {
                level = 2;
                return Lod2;
            }
            if (Lod1Distance > 0f && Lod1 != null && dist >= Lod1Distance)
            {
                level = 1;
                return Lod1;
            }
            if (Lod0 != null)
            {
                level = 0;
                return Lod0;
            }
            level = 0;
            return _capturedBaseMesh;
        }
    }
}
