#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Projection axis for decals.</summary>
    public enum DecalProjection { Forward, Up, Down }

    /// <summary>
    /// Decal component — projects a texture onto nearby surfaces.
    /// Used for bullet holes, blood splatters, dirt, scorch marks, etc.
    /// Decals are rendered as projected quads in the deferred or forward pass.
    /// </summary>
    [Require(typeof(MeshFilter), typeof(MeshRenderer))]
    [ComponentCategory("Rendering")]
    public sealed class Decal : Behavior
    {
        // ── Appearance ──
        private string _texturePath = "";
        private float _width = 1f;
        private float _height = 1f;

        [Persist] public string TexturePath
        {
            get => _texturePath;
            set { if (_texturePath != value) { _texturePath = value; ApplyDecalMaterial(); } }
        }
        [Persist] public float Width
        {
            get => _width;
            set { if (_width != value) { _width = value; _meshBuilt = false; BuildDecalMesh(); } }
        }
        [Persist] public float Height
        {
            get => _height;
            set { if (_height != value) { _height = value; _meshBuilt = false; BuildDecalMesh(); } }
        }
        [Persist] public float Depth { get; set; } = 0.5f;        // projection depth
        [Persist] public SN.Vector4 Color { get; set; } = SN.Vector4.One;
        [Persist] public float Opacity { get; set; } = 1f;

        // ── Projection ──
        [Persist] public DecalProjection Projection { get; set; } = DecalProjection.Forward;
        [Persist] public float AngleFade { get; set; } = 60f;     // fade at steep angles (degrees)

        // ── Lifetime ──
        [Persist] public float Lifetime { get; set; } = 0f;       // 0 = infinite
        [Persist] public float FadeOutTime { get; set; } = 1f;    // fade out over last N seconds

        // ── Runtime ──
        private float _age;
        private bool _meshBuilt;
        private string? _loadedTexPath;   // track which texture we loaded

        /// <summary>Current opacity including fade-out.</summary>
        public float EffectiveOpacity
        {
            get
            {
                if (Lifetime <= 0f) return Opacity;
                float remaining = Lifetime - _age;
                if (remaining <= 0f) return 0f;
                if (remaining < FadeOutTime)
                    return Opacity * (remaining / FadeOutTime);
                return Opacity;
            }
        }

        // ── Registry ──
        private static readonly List<Decal> _all = new(32);
        public static IReadOnlyList<Decal> AllDecals => _all;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_all.Contains(this)) _all.Add(this);
            BuildDecalMesh();
            ApplyDecalMaterial();
        }

        public override void OnDisable()
        {
            _all.Remove(this);
            base.OnDisable();
        }

        public override void Awake()
        {
            BuildDecalMesh();
            ApplyDecalMaterial();
        }

        public override void Update()
        {
            if (Lifetime > 0f)
            {
                _age += Time.deltaTime;
                if (_age >= Lifetime)
                {
                    // Auto-destroy when lifetime expires
                    if (gameObject != null)
                    {
                        if (gameObject.Parent != null)
                            gameObject.Parent.Children.Remove(gameObject);
                        else
                            SceneService.Remove(gameObject);
                    }
                }
            }

            // Rebuild mesh / reload texture if something changed
            if (!_meshBuilt) BuildDecalMesh();
            if (_loadedTexPath != TexturePath) ApplyDecalMaterial();
        }

        /// <summary>Mark for mesh rebuild.</summary>
        public void MarkDirty()
        {
            _meshBuilt = false;
            _loadedTexPath = null;   // force texture reload
            BuildDecalMesh();
            ApplyDecalMaterial();
            SceneService.NotifyChanged();
        }

        /// <summary>
        /// Build a quad mesh for the decal, oriented based on the Projection setting.
        /// Forward: quad in XY plane (facing -Z), for walls/forward surfaces.
        /// Up: quad in XZ plane (facing +Y), for floors/ground.
        /// Down: quad in XZ plane (facing -Y), for ceilings.
        /// Depth offsets the quad along the projection normal to prevent z-fighting.
        /// </summary>
        void BuildDecalMesh()
        {
            if (gameObject == null) return;
            _meshBuilt = true;
            var mf = GetComponent<MeshFilter>();
            if (mf == null) return;

            float hw = Width * 0.5f;
            float hh = Height * 0.5f;
            float depthOffset = Depth * 0.01f; // small offset along normal to prevent z-fighting

            SN.Vector3[] verts;
            SN.Vector3[] normals;
            SN.Vector2[] uvs;

            switch (Projection)
            {
                case DecalProjection.Up:
                    // Quad in XZ plane, facing +Y (for ground/floors)
                    verts = new SN.Vector3[]
                    {
                        new(-hw, depthOffset, -hh),
                        new( hw, depthOffset, -hh),
                        new( hw, depthOffset,  hh),
                        new(-hw, depthOffset,  hh),
                    };
                    normals = new SN.Vector3[] { SN.Vector3.UnitY, SN.Vector3.UnitY, SN.Vector3.UnitY, SN.Vector3.UnitY };
                    uvs = new SN.Vector2[] { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
                    break;

                case DecalProjection.Down:
                    // Quad in XZ plane, facing -Y (for ceilings)
                    verts = new SN.Vector3[]
                    {
                        new(-hw, -depthOffset,  hh),
                        new( hw, -depthOffset,  hh),
                        new( hw, -depthOffset, -hh),
                        new(-hw, -depthOffset, -hh),
                    };
                    normals = new SN.Vector3[] { -SN.Vector3.UnitY, -SN.Vector3.UnitY, -SN.Vector3.UnitY, -SN.Vector3.UnitY };
                    uvs = new SN.Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
                    break;

                case DecalProjection.Forward:
                default:
                    // Quad in XY plane, facing -Z (for walls, forward projection)
                    verts = new SN.Vector3[]
                    {
                        new(-hw, -hh, depthOffset),
                        new( hw, -hh, depthOffset),
                        new( hw,  hh, depthOffset),
                        new(-hw,  hh, depthOffset),
                    };
                    normals = new SN.Vector3[] { -SN.Vector3.UnitZ, -SN.Vector3.UnitZ, -SN.Vector3.UnitZ, -SN.Vector3.UnitZ };
                    uvs = new SN.Vector2[] { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
                    break;
            }

            var tris = new int[] { 0, 2, 1, 0, 3, 2 };
            var lines = new int[] { 0, 1, 1, 2, 2, 3, 3, 0 };

            mf.Mesh = new Mesh(verts, lines, tris) { Normals = normals, UVs = uvs };
        }

        /// <summary>Create a fresh material with the decal's own texture so it doesn't share another object's material.</summary>
        void ApplyDecalMaterial()
        {
            // Guard: don't run if not attached to a GameObject yet
            if (gameObject == null) return;

            _loadedTexPath = TexturePath;

            var mr = GetComponent<MeshRenderer>();
            if (mr == null) return;

            // Clear any persisted material paths so MaterialRebind won't overwrite our material
            mr.MaterialPaths.Clear();
            mr.ResolvedMaterials.Clear();

            // Always give the decal its own clean material
            // Opaque + AlphaCutoff = cutout mode: shader discards transparent pixels
            // without the renderer overriding our settings for blended transparency
            var mat = new Material
            {
                Name = "Decal",
                Transparent = false,
                AlphaCutoff = 0.1f,    // discard transparent background pixels
                Lit = false,
                BaseColor = Avalonia.Media.Color.FromArgb(255, 255, 255, 255)
            };

            // Load the decal texture if a path is set
            if (!string.IsNullOrWhiteSpace(TexturePath))
            {
                try
                {
                    string? absPath = ResolveTexturePath(TexturePath);
                    if (absPath != null && System.IO.File.Exists(absPath))
                    {
                        var tex = Texture2D.FromFile(absPath);
                        if (tex != null)
                        {
                            mat.Textures.Add(new RuntimeTexSlot
                            {
                                Texture = tex,
                                Usage = "Albedo",
                                SourcePath = TexturePath
                            });
                        }
                    }
                }
                catch (Exception ex) { Log.Warning($"[Decal] Failed to load texture: {ex.Message}"); }
            }

            mr.Material = mat;
            mr.Color = Avalonia.Media.Color.FromArgb(255, 255, 255, 255);
            mr.DoubleSided = true;
        }

        /// <summary>Resolve a texture path the same way other importers do.</summary>
        static string? ResolveTexturePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (System.IO.Path.IsPathRooted(path) && System.IO.File.Exists(path)) return path;

            var root = ProjectService.Current?.RootPath;
            if (!string.IsNullOrEmpty(root))
            {
                var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
                if (System.IO.File.Exists(candidate)) return candidate;

                candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "Assets", path));
                if (System.IO.File.Exists(candidate)) return candidate;

                // Search Assets by filename
                var assetsDir = System.IO.Path.Combine(root, "Assets");
                if (System.IO.Directory.Exists(assetsDir))
                {
                    try
                    {
                        var found = System.IO.Directory.GetFiles(assetsDir, System.IO.Path.GetFileName(path), System.IO.SearchOption.AllDirectories);
                        if (found.Length > 0) return found[0];
                    }
                    catch { }
                }
            }

            if (System.IO.File.Exists(path)) return System.IO.Path.GetFullPath(path);
            return null;
        }

        /// <summary>
        /// Spawn a decal at a hit point facing the given normal.
        /// Convenience factory method for script use.
        /// </summary>
        public static Decal Spawn(SN.Vector3 position, SN.Vector3 normal, string texturePath,
            float width = 0.5f, float height = 0.5f, float lifetime = 10f)
        {
            var go = new GameObject("Decal");
            go.Transform.Position = new Vector3(position.X, position.Y, position.Z);

            // Orient to face along the normal
            float yaw = MathF.Atan2(normal.X, normal.Z) * (180f / MathF.PI);
            float pitch = MathF.Asin(-normal.Y) * (180f / MathF.PI);
            go.Transform.Rotation = new Vector3(pitch, yaw, 0);

            // Offset slightly along normal to prevent z-fighting
            go.Transform.Position = new Vector3(
                position.X + normal.X * 0.01f,
                position.Y + normal.Y * 0.01f,
                position.Z + normal.Z * 0.01f);

            var decal = new Decal
            {
                TexturePath = texturePath,
                Width = width,
                Height = height,
                Lifetime = lifetime
            };
            go.AddBehavior(decal);

            SceneService.Add(go);
            return decal;
        }
    }
}
