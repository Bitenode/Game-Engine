using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SN = System.Numerics;
using Assimp;
using Game_Engine.Core;

using CoreVec3 = Game_Engine.Core.Vector3;
using Avalonia.Media;

namespace Game_Engine.Core.Importers
{
    public static class ModelImporter
    {
        /// <summary>
        /// Load a model (fbx/obj/gltf/dae/…) and return a root GameObject that mirrors the file’s node tree.
        /// Meshes are triangulated; normals are generated if missing; UV0 is imported when available.
        /// First diffuse/baseColor texture is hooked into Material.
        /// </summary>
        // Color (0–255) -> Vector3 (0–1)  **double-based**
        static CoreVec3 ColorToVec3(Color c)
            => new CoreVec3(c.R / 255.0, c.G / 255.0, c.B / 255.0);

        // Vector3 (0–1) -> Color (0–255)  **double-based**
        static Color Vec3ToColor(CoreVec3 v)
        {
            static double Clamp01(double f) => f < 0.0 ? 0.0 : (f > 1.0 ? 1.0 : f);

            byte r = (byte)(Clamp01(v.X) * 255.0);
            byte g = (byte)(Clamp01(v.Y) * 255.0);
            byte b = (byte)(Clamp01(v.Z) * 255.0);

            return Color.FromRgb(r, g, b);
        }
        public static GameObject ImportModel(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("Model not found", path);

            var ctx = new AssimpContext();

            // Triangulate, join verts, smooth normals, improve cache locality, etc.
            var pp = PostProcessSteps.Triangulate
                   | PostProcessSteps.JoinIdenticalVertices
                   | PostProcessSteps.GenerateSmoothNormals
                   | PostProcessSteps.ImproveCacheLocality
                   | PostProcessSteps.RemoveRedundantMaterials
                   | PostProcessSteps.FixInFacingNormals
                   | PostProcessSteps.FlipWindingOrder;   // FBX convention often wants this with our rasterizer

            // UV orientation: our sampler flips V already (top-left images), so we do NOT FlipUVs here.

            var scene = ctx.ImportFile(path, pp);
            if (scene is null || !scene.HasMeshes)
                throw new InvalidDataException("No meshes in file, or import failed.");

            // Build materials first (index -> engine Material)
            var materials = BuildMaterials(scene, Path.GetDirectoryName(path)!);

            // Convert nodes recursively
            var root = ConvertNode(scene, scene.RootNode, materials);

            root.Name = Path.GetFileNameWithoutExtension(path);
            return root;
        }

        static Dictionary<int, Material> BuildMaterials(Scene sc, string dir)
        {
            var dict = new Dictionary<int, Material>();

            for (int i = 0; i < sc.MaterialCount; i++)
            {
                var aimat = sc.Materials[i];
                var m = new Material();

                // Optional tint from diffuse color
                if (aimat.HasColorDiffuse)
                {
                    var c = aimat.ColorDiffuse;
                    m.Tint = Avalonia.Media.Color.FromRgb(
                        (byte)Math.Clamp((int)(c.R * 255f), 0, 255),
                        (byte)Math.Clamp((int)(c.G * 255f), 0, 255),
                        (byte)Math.Clamp((int)(c.B * 255f), 0, 255));
                }

                // Grab first reasonable texture slot
                if (TryGetFirstTextureSlot(aimat, out var slot))
                {
                    var tex = TryLoadTexture(slot, sc, dir);
                    if (tex != null)
                    {
                        m.Textures.Add(new MaterialTexture
                        {
                            Name = Path.GetFileName(slot.FilePath),
                            Texture = tex
                        });
                    }
                }

                dict[i] = m;
            }

            return dict;
        }

        // Prefer Diffuse, then PBR BaseColor (if the enum exists), then any available slot.
        static bool TryGetFirstTextureSlot(Assimp.Material mat, out TextureSlot slot)
        {
            // Classic diffuse
            if (mat.GetMaterialTextureCount(TextureType.Diffuse) > 0 &&
                mat.GetMaterialTexture(TextureType.Diffuse, 0, out slot))
                return true;

            // PBR base color (only in newer AssimpNet builds)
            if (Enum.TryParse("BaseColor", out TextureType baseColorType))
            {
                if (mat.GetMaterialTextureCount(baseColorType) > 0 &&
                    mat.GetMaterialTexture(baseColorType, 0, out slot))
                    return true;
            }

            // Fall back to whatever is first (Unknown / Emissive / etc.)
            foreach (TextureType t in Enum.GetValues(typeof(TextureType)))
            {
                if (mat.GetMaterialTextureCount(t) > 0 &&
                    mat.GetMaterialTexture(t, 0, out slot))
                    return true;
            }

            slot = default;
            return false;
        }


        static Texture2D? TryLoadTexture(TextureSlot slot, Scene sc, string dir)
        {
            // Embedded texture (FilePath like "*0", "*1", …)
            if (!string.IsNullOrEmpty(slot.FilePath) && slot.FilePath.StartsWith("*"))
            {
                int idx;
                if (int.TryParse(slot.FilePath.AsSpan(1), out idx))
                {
                    if (idx >= 0 && idx < sc.TextureCount)
                    {
                        var emb = sc.Textures[idx];
                        if (emb is not null)
                        {
                            byte[] bytes = emb.HasCompressedData
                                ? emb.CompressedData
                                : FlattenRawEmbedded(emb);

                            try { return Texture2D.FromBytes(bytes); }
                            catch { /* ignore */ }
                        }
                    }
                }
            }

            // External file path (relative to model)
            if (!string.IsNullOrEmpty(slot.FilePath))
            {
                var p = slot.FilePath.Replace('\\', '/');
                // Some exporters put absolute, some relative, some only file names
                var tryPaths = new[]
                {
                    Path.Combine(dir, p),
                    Path.Combine(dir, Path.GetFileName(p))
                };

                foreach (var tp in tryPaths)
                {
                    if (File.Exists(tp))
                    {
                        try { return Texture2D.FromFile(tp); }
                        catch { /* ignore */ }
                    }
                }
            }

            return null;
        }

        static byte[] FlattenRawEmbedded(EmbeddedTexture t)
        {
            int w = t.Width;
            int h = t.Height;

            // NonCompressedData is Texel[] (BGRA)
            var src = t.NonCompressedData;
            if (src is null || src.Length < w * h) return Array.Empty<byte>();

            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                var texel = src[i]; // Assimp.Texel
                rgba[i * 4 + 0] = texel.R; // R
                rgba[i * 4 + 1] = texel.G; // G
                rgba[i * 4 + 2] = texel.B; // B
                rgba[i * 4 + 3] = texel.A; // A
            }
            return rgba;
        }


        static GameObject ConvertNode(Scene sc, Node node, Dictionary<int, Material> materials)
        {
            var go = new GameObject(node.Name);
            ApplyTransform(node.Transform, go.Transform);

            // Mesh instances on this node
            foreach (var idx in node.MeshIndices)
            {
                var aim = sc.Meshes[idx];
                var (mesh, hasNormals) = ConvertMesh(aim);

                var mf = new MeshFilter { Mesh = mesh };
                var mr = new MeshRenderer();

                // Pass a base color (use Material.Tint as a suggestion)
                if (aim.MaterialIndex >= 0 && materials.TryGetValue(aim.MaterialIndex, out var mat))
                {
                    mr.Material = mat;
                    mr.Color = mat.Tint; // the rasterizer multiplies texture by MeshRenderer.Color (tint)
                }

                // If no normals in file, we already generated smooth normals above.
                if (!hasNormals) mesh.RecalculateNormalsSmooth();

                go.AddBehavior(mf);
                go.AddBehavior(mr);
            }

            // Children
            foreach (var child in node.Children)
                go.AddChild(ConvertNode(sc, child, materials));

            return go;
        }

        static (Mesh mesh, bool hadNormals) ConvertMesh(Assimp.Mesh m)
        {
            // Vertices
            var v = new SN.Vector3[m.VertexCount];
            for (int i = 0; i < v.Length; i++)
            {
                var p = m.Vertices[i];
                v[i] = new SN.Vector3(p.X, p.Y, p.Z);
            }

            // Indices (triangulated already)
            var tris = new List<int>(m.FaceCount * 3);
            foreach (var f in m.Faces)
            {
                if (f.IndexCount == 3) { tris.Add(f.Indices[0]); tris.Add(f.Indices[1]); tris.Add(f.Indices[2]); }
            }

            var mesh = new Mesh(v, Array.Empty<int>(), tris.ToArray())
            {
                Kind = MeshKind.Generic
            };

            // Normals
            bool hadNormals = m.HasNormals;
            if (m.HasNormals)
            {
                mesh.Normals = new SN.Vector3[m.Normals.Count];
                for (int i = 0; i < m.Normals.Count; i++)
                {
                    var n = m.Normals[i];
                    mesh.Normals[i] = new SN.Vector3(n.X, n.Y, n.Z);
                }
            }

            // UV0
            if (m.HasTextureCoords(0))
            {
                mesh.UVs = new SN.Vector2[m.TextureCoordinateChannels[0].Count];
                for (int i = 0; i < mesh.UVs.Length; i++)
                {
                    var t = m.TextureCoordinateChannels[0][i]; // Assimp uses 3D UVs; ignore Z
                    mesh.UVs[i] = new SN.Vector2(t.X, t.Y);
                }
            }

            return (mesh, hadNormals);
        }

        static void ApplyTransform(Matrix4x4 ai, Transform t)
        {
            // Assimp Matrix4x4 -> TRS
            // Decompose gives: scaling, rotation, translation
            ai.Decompose(out var s, out var r, out var p);

            // Position
            t.Position = new Vector3(p.X, p.Y, p.Z);

            // Rotation: convert quaternion to Euler degrees (YXZ is fine for editors)
            // We'll use yaw(Y), pitch(X), roll(Z).
            ToEulerYXZ(r, out double rx, out double ry, out double rz);
            t.Rotation = new Vector3(rx, ry, rz);

            // Scale
            t.Scale = new Vector3(s.X, s.Y, s.Z);
        }

        static void ToEulerYXZ(Assimp.Quaternion q, out double rx, out double ry, out double rz)
        {
            // Convert to System.Numerics first for convenience
            var nq = new SN.Quaternion(q.X, q.Y, q.Z, q.W);

            // YXZ decomposition
            // Reference formulation that’s stable for editors:
            var m = SN.Matrix4x4.CreateFromQuaternion(nq);

            // Extract Euler (YXZ)
            ry = Math.Atan2(m.M13, m.M33);                 // yaw (Y)
            rx = Math.Asin(Math.Clamp(-m.M23, -1f, 1f));   // pitch (X)
            rz = Math.Atan2(m.M21, m.M22);                 // roll (Z)

            const double Rad2Deg = 180.0 / Math.PI;
            rx *= Rad2Deg; ry *= Rad2Deg; rz *= Rad2Deg;
        }
    }
}
