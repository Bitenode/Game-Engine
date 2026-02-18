#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Water component — renders a planar water surface with waves, reflections,
    /// and Fresnel-based transparency. Pairs with the water shader for vertex
    /// displacement (Gerstner waves) and depth-based foam.
    /// </summary>
    [ComponentCategory("Effects")]
    [Require(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class Water : Behavior
    {
        // ── Geometry ──
        private float _width = 50f;
        private float _length = 50f;
        private int _resolution = 64;

        [Persist] public float Width
        {
            get => _width;
            set { if (_width != value) { _width = value; MarkDirty(); } }
        }
        [Persist] public float Length
        {
            get => _length;
            set { if (_length != value) { _length = value; MarkDirty(); } }
        }
        [Persist] public int Resolution
        {
            get => _resolution;
            set { if (_resolution != value) { _resolution = value; MarkDirty(); } }
        }

        // ── Waves (Gerstner) ──
        [Persist] public float WaveAmplitude { get; set; } = 0.3f;
        [Persist] public float WaveFrequency { get; set; } = 1.5f;
        [Persist] public float WaveSpeed { get; set; } = 1f;
        [Persist] public SN.Vector2 WaveDirection { get; set; } = new SN.Vector2(1f, 0.5f);
        [Persist] public float WaveSteepness { get; set; } = 0.5f;   // 0..1

        // ── Second wave layer ──
        [Persist] public float Wave2Amplitude { get; set; } = 0.15f;
        [Persist] public float Wave2Frequency { get; set; } = 2.5f;
        [Persist] public float Wave2Speed { get; set; } = 0.7f;
        [Persist] public SN.Vector2 Wave2Direction { get; set; } = new SN.Vector2(-0.5f, 1f);

        // ── Appearance ──
        [Persist] public SN.Vector4 ShallowColor { get; set; } = new SN.Vector4(0.2f, 0.6f, 0.7f, 0.6f);
        [Persist] public SN.Vector4 DeepColor { get; set; } = new SN.Vector4(0.05f, 0.15f, 0.3f, 0.9f);
        [Persist] public float FresnelPower { get; set; } = 3f;
        [Persist] public float Reflectivity { get; set; } = 0.5f;
        [Persist] public float Transparency { get; set; } = 0.5f;

        // ── Foam ──
        [Persist] public bool FoamEnabled { get; set; } = true;
        [Persist] public float FoamDepthThreshold { get; set; } = 0.5f;
        [Persist] public float FoamIntensity { get; set; } = 0.8f;
        [Persist] public SN.Vector3 FoamColor { get; set; } = new SN.Vector3(0.9f, 0.95f, 1f);

        // ── Underwater ──
        [Persist] public SN.Vector3 UnderwaterTint { get; set; } = new SN.Vector3(0.1f, 0.3f, 0.5f);
        [Persist] public float UnderwaterFogDensity { get; set; } = 0.05f;
        [Persist] public float UnderwaterCausticStrength { get; set; } = 0.3f;
        [Persist] public float UnderwaterDistortion { get; set; } = 0.003f;
        [Persist] public float UnderwaterBuoyancy { get; set; } = 6f;
        [Persist] public float UnderwaterDrag { get; set; } = 3f;
        [Persist] public float SwimSpeed { get; set; } = 0.6f;

        // ── Normal map ──
        [Persist] public float NormalStrength { get; set; } = 1f;
        [Persist] public float NormalTiling { get; set; } = 10f;
        [Persist] public float NormalScrollSpeed { get; set; } = 0.03f;

        // ── Runtime ──
        private float _time;
        private bool _meshBuilt;

        /// <summary>Current animation time for wave displacement.</summary>
        public float AnimTime => _time;

        // ── Registry ──
        private static readonly System.Collections.Generic.List<Water> _all = new(4);
        public static System.Collections.Generic.IReadOnlyList<Water> ActiveWaters => _all;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_all.Contains(this)) _all.Add(this);

            // Build mesh immediately so water is visible in the editor (SceneView)
            // without needing Play-mode Awake/Update calls.
            if (!_meshBuilt) BuildMesh();
        }

        public override void OnDisable()
        {
            _all.Remove(this);
            base.OnDisable();
        }

        public override void Awake()
        {
            if (!_meshBuilt) BuildMesh();
        }

        public override void Update()
        {
            _time += Time.deltaTime * WaveSpeed;

            // Rebuild mesh if resolution changed
            if (!_meshBuilt) BuildMesh();
        }

        /// <summary>
        /// Ensure the water mesh exists. Called by the renderer as a safety net
        /// in case OnEnable ran before the MeshFilter was attached.
        /// </summary>
        public void EnsureMesh()
        {
            if (!_meshBuilt) BuildMesh();
        }

        /// <summary>Mark mesh for rebuild and immediately regenerate it.</summary>
        public void MarkDirty()
        {
            _meshBuilt = false;
            BuildMesh();
            SceneService.NotifyChanged();
        }

        /// <summary>Build the water plane mesh.</summary>
        public void BuildMesh()
        {
            _meshBuilt = true;
            var mf = GetComponent<MeshFilter>();
            if (mf == null) return;

            int res = Math.Max(2, Resolution);
            int vertCount = (res + 1) * (res + 1);
            var verts = new SN.Vector3[vertCount];
            var normals = new SN.Vector3[vertCount];
            var uvs = new SN.Vector2[vertCount];

            float halfW = Width * 0.5f;
            float halfL = Length * 0.5f;

            for (int z = 0; z <= res; z++)
            {
                for (int x = 0; x <= res; x++)
                {
                    int idx = z * (res + 1) + x;
                    float u = x / (float)res;
                    float v = z / (float)res;

                    verts[idx] = new SN.Vector3(
                        -halfW + u * Width,
                        0f,
                        -halfL + v * Length);
                    normals[idx] = SN.Vector3.UnitY;
                    uvs[idx] = new SN.Vector2(u, v);
                }
            }

            // Triangle indices
            int triCount = res * res * 6;
            var tris = new int[triCount];
            int ti = 0;
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int a = z * (res + 1) + x;
                    int b = a + 1;
                    int c = a + (res + 1);
                    int d = c + 1;

                    tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                    tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
                }
            }

            // Line indices for wireframe
            var lines = new System.Collections.Generic.List<int>();
            for (int z = 0; z <= res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int a = z * (res + 1) + x;
                    lines.Add(a); lines.Add(a + 1);
                }
            }
            for (int x = 0; x <= res; x++)
            {
                for (int z = 0; z < res; z++)
                {
                    int a = z * (res + 1) + x;
                    lines.Add(a); lines.Add(a + (res + 1));
                }
            }

            var mesh = new Mesh(verts, lines.ToArray(), tris)
            {
                Normals = normals,
                UVs = uvs
            };
            mf.Mesh = mesh;
        }

        // ── Static underwater detection ──

        /// <summary>
        /// Check if a world position is below any active water surface.
        /// Returns the Water component the point is under, or null if above water.
        /// </summary>
        public static Water? GetUnderwaterWater(SN.Vector3 worldPos)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                var w = _all[i];
                if (!w.IsActiveAndEnabled || w.gameObject == null) continue;

                // Check if the point is within the water plane's XZ bounds
                var wPos = new SN.Vector3(
                    (float)w.Transform.Position.X,
                    (float)w.Transform.Position.Y,
                    (float)w.Transform.Position.Z);

                float halfW = w.Width * 0.5f;
                float halfL = w.Length * 0.5f;

                float localX = worldPos.X - wPos.X;
                float localZ = worldPos.Z - wPos.Z;

                // Allow some margin beyond the water plane so the effect doesn't pop
                float margin = 2f;
                if (localX < -halfW - margin || localX > halfW + margin) continue;
                if (localZ < -halfL - margin || localZ > halfL + margin) continue;

                // Sample the water surface height at this XZ position
                float surfaceY = w.SampleHeight(worldPos.X, worldPos.Z);

                if (worldPos.Y < surfaceY)
                    return w;
            }
            return null;
        }

        /// <summary>Check if a world position is underwater (convenience).</summary>
        public static bool IsUnderwater(SN.Vector3 worldPos) => GetUnderwaterWater(worldPos) != null;

        /// <summary>Compute Gerstner wave displacement at a world XZ position.</summary>
        public float SampleHeight(float worldX, float worldZ)
        {
            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

            float h = pos.Y;

            // Wave 1
            var dir1 = SN.Vector2.Normalize(WaveDirection);
            float dot1 = dir1.X * worldX + dir1.Y * worldZ;
            h += WaveAmplitude * MathF.Sin(dot1 * WaveFrequency + _time * WaveSpeed);

            // Wave 2
            var dir2 = SN.Vector2.Normalize(Wave2Direction);
            float dot2 = dir2.X * worldX + dir2.Y * worldZ;
            h += Wave2Amplitude * MathF.Sin(dot2 * Wave2Frequency + _time * Wave2Speed);

            return h;
        }
    }
}
