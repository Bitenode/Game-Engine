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
                   | PostProcessSteps.RemoveRedundantMaterials;

            // UV orientation: our sampler flips V already (top-left images), so we do NOT FlipUVs here.

            var scene = ctx.ImportFile(path, pp);
            if (scene is null || !scene.HasMeshes)
                throw new InvalidDataException("No meshes in file, or import failed.");

            // Build materials first (index -> engine Material)
            var materials = BuildMaterials(scene, Path.GetDirectoryName(path)!);

            // Normalize scale
            float maxScale = 0f;
            foreach (var m in scene.Meshes)
            {
                var (radius, _) = ApproxRadialAndHeight(m);
                maxScale = Math.Max(maxScale, radius);
            }
            float scaleFactor = maxScale > 0 ? 1f / maxScale : 1f;

            // Convert nodes recursively with scale factor
            var root = ConvertNode(scene, scene.RootNode, materials, scaleFactor);

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

                // Collect textures from common PBR/classic slots.
                // We’ll also do a final pass over *all* slots to catch odd exporters.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Classic
                AddAllTexturesOfType(aimat, TextureType.Diffuse, m, sc, dir, MaterialTexture.TexUsage.Albedo, seen);
                AddAllTexturesOfType(aimat, TextureType.Emissive, m, sc, dir, MaterialTexture.TexUsage.Emissive, seen);
                AddAllTexturesOfType(aimat, TextureType.Normals, m, sc, dir, MaterialTexture.TexUsage.Normal, seen);
                AddAllTexturesOfType(aimat, TextureType.Lightmap, m, sc, dir, MaterialTexture.TexUsage.AmbientOcclusion, seen); // many tools put AO here

                // Some exporters use alternative enum values (Assimp build dependent).
                if (Enum.TryParse("BaseColor", out TextureType baseColorT))
                    AddAllTexturesOfType(aimat, baseColorT, m, sc, dir, MaterialTexture.TexUsage.Albedo, seen);
                if (Enum.TryParse("NormalCamera", out TextureType normalCamT))
                    AddAllTexturesOfType(aimat, normalCamT, m, sc, dir, MaterialTexture.TexUsage.Normal, seen);
                if (Enum.TryParse("Metalness", out TextureType metalT))
                    AddAllTexturesOfType(aimat, metalT, m, sc, dir, MaterialTexture.TexUsage.Metallic, seen);
                if (Enum.TryParse("DiffuseRoughness", out TextureType roughT))
                    AddAllTexturesOfType(aimat, roughT, m, sc, dir, MaterialTexture.TexUsage.Roughness, seen);
                if (Enum.TryParse("Roughness", out TextureType roughT2))
                    AddAllTexturesOfType(aimat, roughT2, m, sc, dir, MaterialTexture.TexUsage.Roughness, seen);
                if (Enum.TryParse("AmbientOcclusion", out TextureType aoT))
                    AddAllTexturesOfType(aimat, aoT, m, sc, dir, MaterialTexture.TexUsage.AmbientOcclusion, seen);

                // Fallback sweep: scan *all* types, guess usage from the type/name, and add anything we missed.
                foreach (TextureType t in Enum.GetValues(typeof(TextureType)))
                {
                    int cnt = aimat.GetMaterialTextureCount(t);
                    for (int k = 0; k < cnt; k++)
                    {
                        if (!aimat.GetMaterialTexture(t, k, out var slot)) continue;
                        var guess = GuessUsageFromTypeOrName(t, slot.FilePath);
                        AddTextureFromSlot(m, slot, sc, dir, guess, seen);
                    }
                }

                dict[i] = m;
            }

            return dict;
        }


        static void AddAllTexturesOfType(Assimp.Material aimat, TextureType type,
                                 Material m, Scene sc, string dir,
                                 MaterialTexture.TexUsage usage,
                                 HashSet<string> seen)
        {
            int n = aimat.GetMaterialTextureCount(type);
            for (int i = 0; i < n; i++)
            {
                if (aimat.GetMaterialTexture(type, i, out var slot))
                    AddTextureFromSlot(m, slot, sc, dir, usage, seen);
            }
        }

        static void AddTextureFromSlot(Material m, TextureSlot slot, Scene sc, string dir,
                                       MaterialTexture.TexUsage usage, HashSet<string> seen)
        {
            // Normalize a dedupe key (embedded textures have "*N")
            string key = slot.FilePath ?? string.Empty;
            if (!seen.Add(key)) return;

            var tex = TryLoadTexture(slot, sc, dir);
            if (tex == null) return;

            m.Textures.Add(new MaterialTexture
            {
                Name = Path.GetFileName(slot.FilePath),
                Texture = tex,
                Usage = usage,
                FaceMask = (MaterialTexture.CubeFaceMask)(-1),                     // models: use everywhere by default
                SourcePath = slot.FilePath
            });
        }

        // Try to choose a good usage from an Assimp type and/or the filename
        static MaterialTexture.TexUsage GuessUsageFromTypeOrName(TextureType t, string? path)
        {
            // Map by type first
            switch (t)
            {
                case TextureType.Diffuse: return MaterialTexture.TexUsage.Albedo;
                case TextureType.Emissive: return MaterialTexture.TexUsage.Emissive;
                case TextureType.Normals: return MaterialTexture.TexUsage.Normal;
                case TextureType.Lightmap: return MaterialTexture.TexUsage.AmbientOcclusion;
            }

            // Some builds ship extra enums we handled above via TryParse (BaseColor, Metalness, Roughness, AmbientOcclusion, NormalCamera)
            // If we get here, fall back to filename heuristics:
            var n = (path ?? "").ToLowerInvariant();

            // common tokens
            if (n.Contains("normal") || n.Contains("_n") || n.Contains("_N") || n.Contains("-nrm")) return MaterialTexture.TexUsage.Normal;
            if (n.Contains("rough") || n.Contains("_r")) return MaterialTexture.TexUsage.Roughness;
            if (n.Contains("metal") || n.Contains("metallic") || n.Contains("_M") || n.Contains("_m"))  return MaterialTexture.TexUsage.Metallic;
            if (n.Contains("ao") || n.Contains("occl") || n.Contains("AO") || n.Contains("ambientocclusion")) return MaterialTexture.TexUsage.AmbientOcclusion;
            if (n.Contains("emit") || n.Contains("emiss")) return MaterialTexture.TexUsage.Emissive;
            if (n.Contains("albedo") || n.Contains("basecolor") || n.Contains("diffuse") || n.Contains("_c")) return MaterialTexture.TexUsage.Albedo;

            // safest default
            return MaterialTexture.TexUsage.Albedo;
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


        static GameObject ConvertNode(Scene sc, Node node, Dictionary<int, Material> materials, float scaleFactor)
        {
            var go = new GameObject(node.Name);
            ApplyTransform(node.Transform, go.Transform);

            // Apply scale normalization to each component
           /* go.Transform.Scale = new Vector3(
                go.Transform.Scale.X * scaleFactor,
                go.Transform.Scale.Y * scaleFactor,
                go.Transform.Scale.Z * scaleFactor
            );*/

            // Mesh instances on this node
            foreach (var idx in node.MeshIndices)
            {
                var aim = sc.Meshes[idx];
                var (mesh, hasNormals) = ConvertMesh(aim, scaleFactor);

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

                // --- set double-sided from the Assimp material when available ---
                bool twoSided = false;
                if (aim.MaterialIndex >= 0 && aim.MaterialIndex < sc.MaterialCount)
                {
                    var aiMat = sc.Materials[aim.MaterialIndex];
                    var t = aiMat.GetType();

                    // Newer AssimpNet: bool TwoSided
                    var pTwo = t.GetProperty("TwoSided");
                    if (pTwo != null)
                        twoSided = Convert.ToBoolean(pTwo.GetValue(aiMat));
                    else
                    {
                        // Some builds: IsTwoSided
                        var pIs = t.GetProperty("IsTwoSided");
                        if (pIs != null)
                            twoSided = Convert.ToBoolean(pIs.GetValue(aiMat));
                        else
                        {
                            // Older builds: HasTwoSided (usually only present when true)
                            var pHas = t.GetProperty("HasTwoSided");
                            if (pHas != null)
                                twoSided = Convert.ToBoolean(pHas.GetValue(aiMat));
                        }
                    }
                }

                mr.DoubleSided = twoSided;

                // Force double-sided for FBX objects to prevent slicing of thin walls
                //mr.DoubleSided = true;

                

                go.AddBehavior(mf);
                go.AddBehavior(mr);
            }

            // Children
            foreach (var child in node.Children)
                go.AddChild(ConvertNode(sc, child, materials, scaleFactor));

            return go;
        }

        static (Mesh mesh, bool hadNormals) ConvertMesh(Assimp.Mesh m, float scale = 1f)
        {
            // Vertices
            var v = new SN.Vector3[m.VertexCount];
            for (int i = 0; i < v.Length; i++)
            {
                var p = m.Vertices[i];
                v[i] = new SN.Vector3(p.X * scale, p.Y * scale, p.Z * scale);
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

        // Helper method to approximate radius and height
        static (float radius, float height) ApproxRadialAndHeight(Assimp.Mesh m)
        {
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity, r = 0f;
            foreach (var p in m.Vertices)
            {
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
                float rr = (float)Math.Sqrt(p.X * p.X + p.Z * p.Z);
                if (rr > r) r = rr;
            }
            return (r, maxY - minY);
        }
    }
}