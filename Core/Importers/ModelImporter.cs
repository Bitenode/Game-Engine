using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SN = System.Numerics;
using Assimp;
using Game_Engine.Core.Component;

using CoreVec3 = Game_Engine.Core.Vector3;
using Avalonia.Media;

namespace Game_Engine.Core.Importers
{
    public static class ModelImporter
    {
        /// <summary>
        /// Load a model (fbx/obj/gltf/dae/…) and return a root GameObject that mirrors the file’s node tree.
        /// Meshes are triangulated; normals are generated if missing; UV0 is imported when available.
        /// Diffuse/baseColor/other useful textures are imported and each MaterialTexture gets a project-relative SourcePath.
        /// MeshFilter exposes a string ModelPath, it is populated (project-relative) for later mesh rebuilds.
        /// </summary>
        public static GameObject ImportModel(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("Model not found", path);

            // Keep the original abs path and compute project-relative once
            var absModel = Path.GetFullPath(path);
            var relModel = MakeProjectRelative(absModel);
            var modelDir = Path.GetDirectoryName(absModel)!;

            var ctx = new AssimpContext();

            var pp = PostProcessSteps.Triangulate
                   | PostProcessSteps.JoinIdenticalVertices
                   | PostProcessSteps.GenerateSmoothNormals
                   | PostProcessSteps.ImproveCacheLocality
                   | PostProcessSteps.RemoveRedundantMaterials;

            var scene = ctx.ImportFile(absModel, pp);
            if (scene is null || !scene.HasMeshes)
                throw new InvalidDataException("No meshes in file, or import failed.");

            // Build materials first (index -> engine Material)
            var materials = BuildMaterials(scene, modelDir);

            // Normalize scale
            float maxScale = 0f;
            foreach (var m in scene.Meshes)
            {
                var (radius, _) = ApproxRadialAndHeight(m);
                maxScale = Math.Max(maxScale, radius);
            }
            float scaleFactor = maxScale > 0 ? 1f / maxScale : 1f;

            // Convert nodes recursively with a running part index
            int partIndex = 0; // <- running layer number across the whole model (DFS order)
            var root = ConvertNode(scene, scene.RootNode, materials, scaleFactor, relModel, ref partIndex);

            root.Name = Path.GetFileNameWithoutExtension(absModel);
            return root;
        }


        // -------- project-relative helpers (kept local to avoid SceneSerialization dependency) --------
        static string? MakeProjectRelative(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            try
            {
                var abs = Path.GetFullPath(fullPath);
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var root = Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return Path.GetRelativePath(root, abs);
                }
                return abs; // fallback
            }
            catch { return fullPath; }
        }

        static string? ResolveProjectRelative(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                if (Path.IsPathRooted(stored)) return stored;
                var proj = ProjectService.Current;
                if (proj == null) return stored;
                return Path.Combine(proj.RootPath, stored);
            }
            catch { return stored; }
        }

        // ---------------------------------------------------------------------------------------------

        static Dictionary<int, Material> BuildMaterials(Scene sc, string modelDir)
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

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Classic
                AddAllTexturesOfType(aimat, TextureType.Diffuse, m, sc, modelDir, MaterialTexture.TexUsage.Albedo, seen);
                AddAllTexturesOfType(aimat, TextureType.Emissive, m, sc, modelDir, MaterialTexture.TexUsage.Emissive, seen);
                AddAllTexturesOfType(aimat, TextureType.Normals, m, sc, modelDir, MaterialTexture.TexUsage.Normal, seen);
                AddAllTexturesOfType(aimat, TextureType.Lightmap, m, sc, modelDir, MaterialTexture.TexUsage.AmbientOcclusion, seen);

                // Some builds use extra enums
                if (Enum.TryParse("BaseColor", out TextureType baseColorT))
                    AddAllTexturesOfType(aimat, baseColorT, m, sc, modelDir, MaterialTexture.TexUsage.Albedo, seen);
                if (Enum.TryParse("NormalCamera", out TextureType normalCamT))
                    AddAllTexturesOfType(aimat, normalCamT, m, sc, modelDir, MaterialTexture.TexUsage.Normal, seen);
                if (Enum.TryParse("Metalness", out TextureType metalT))
                    AddAllTexturesOfType(aimat, metalT, m, sc, modelDir, MaterialTexture.TexUsage.Metallic, seen);
                if (Enum.TryParse("DiffuseRoughness", out TextureType roughT))
                    AddAllTexturesOfType(aimat, roughT, m, sc, modelDir, MaterialTexture.TexUsage.Roughness, seen);
                if (Enum.TryParse("Roughness", out TextureType roughT2))
                    AddAllTexturesOfType(aimat, roughT2, m, sc, modelDir, MaterialTexture.TexUsage.Roughness, seen);
                if (Enum.TryParse("AmbientOcclusion", out TextureType aoT))
                    AddAllTexturesOfType(aimat, aoT, m, sc, modelDir, MaterialTexture.TexUsage.AmbientOcclusion, seen);

                // Fallback sweep over all types
                foreach (TextureType t in Enum.GetValues(typeof(TextureType)))
                {
                    int cnt = aimat.GetMaterialTextureCount(t);
                    for (int k = 0; k < cnt; k++)
                    {
                        if (!aimat.GetMaterialTexture(t, k, out var slot)) continue;
                        var guess = GuessUsageFromTypeOrName(t, slot.FilePath);
                        AddTextureFromSlot(m, slot, sc, modelDir, guess, seen);
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

            var (tex, resolvedAbsPath) = TryLoadTexture(slot, sc, dir);
            if (tex == null) return;

            m.Textures.Add(new MaterialTexture
            {
                Name = Path.GetFileName(slot.FilePath),
                Texture = tex,
                Usage = usage,
                FaceMask = (MaterialTexture.CubeFaceMask)(-1),   // all faces for model textures by default
                SourcePath = string.IsNullOrWhiteSpace(resolvedAbsPath)
                                ? null
                                : MakeProjectRelative(resolvedAbsPath)  // project-relative for serialization
            });
        }

        // Try to choose a good usage from an Assimp type and/or the filename
        static MaterialTexture.TexUsage GuessUsageFromTypeOrName(TextureType t, string? path)
        {
            switch (t)
            {
                case TextureType.Diffuse: return MaterialTexture.TexUsage.Albedo;
                case TextureType.Emissive: return MaterialTexture.TexUsage.Emissive;
                case TextureType.Normals: return MaterialTexture.TexUsage.Normal;
                case TextureType.Lightmap: return MaterialTexture.TexUsage.AmbientOcclusion;
            }

            var n = (path ?? "").ToLowerInvariant();
            if (n.Contains("normal") || n.Contains("_n") || n.Contains("-nrm")) return MaterialTexture.TexUsage.Normal;
            if (n.Contains("rough") || n.Contains("_r")) return MaterialTexture.TexUsage.Roughness;
            if (n.Contains("metal") || n.Contains("metallic") || n.Contains("_m")) return MaterialTexture.TexUsage.Metallic;
            if (n.Contains("ao") || n.Contains("occl") || n.Contains("ambientocclusion")) return MaterialTexture.TexUsage.AmbientOcclusion;
            if (n.Contains("emit") || n.Contains("emiss")) return MaterialTexture.TexUsage.Emissive;
            if (n.Contains("albedo") || n.Contains("basecolor") || n.Contains("diffuse") || n.EndsWith("_c")) return MaterialTexture.TexUsage.Albedo;

            return MaterialTexture.TexUsage.Albedo;
        }

        /// <summary>
        /// Load texture data; return (Texture2D, absoluteResolvedPathOrNullIfEmbedded)
        /// </summary>
        static (Texture2D? tex, string? absPath) TryLoadTexture(TextureSlot slot, Scene sc, string dir)
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

                            try { return (Texture2D.FromBytes(bytes), null); }
                            catch { /* ignore */ }
                        }
                    }
                }
                return (null, null);
            }

            // External file path (relative to model)
            if (!string.IsNullOrEmpty(slot.FilePath))
            {
                var p = slot.FilePath.Replace('\\', '/');
                var tryPaths = new[]
                {
                    Path.Combine(dir, p),
                    Path.Combine(dir, Path.GetFileName(p))
                };

                foreach (var tp in tryPaths)
                {
                    if (File.Exists(tp))
                    {
                        try { return (Texture2D.FromFile(tp), Path.GetFullPath(tp)); }
                        catch { /* ignore */ }
                    }
                }
            }

            return (null, null);
        }

        static byte[] FlattenRawEmbedded(EmbeddedTexture t)
        {
            int w = t.Width;
            int h = t.Height;

            var src = t.NonCompressedData; // BGRA texels
            if (src is null || src.Length < w * h) return Array.Empty<byte>();

            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                var texel = src[i];
                rgba[i * 4 + 0] = texel.R;
                rgba[i * 4 + 1] = texel.G;
                rgba[i * 4 + 2] = texel.B;
                rgba[i * 4 + 3] = texel.A;
            }
            return rgba;
        }

        static GameObject ConvertNode(Scene sc, Node node, Dictionary<int, Material> materials, float scaleFactor, string relModelPathForNode, ref int partIndex)
        {
            var go = new GameObject(node.Name);
            ApplyTransform(node.Transform, go.Transform);

            // Mesh instances on this node
            foreach (var idx in node.MeshIndices)
            {
                var aim = sc.Meshes[idx];
                var (mesh, hasNormals) = ConvertMesh(aim, scaleFactor);

                var mf = new MeshFilter { Mesh = mesh };
                var mr = new MeshRenderer();

                //MeshFilter exposes a string ModelPath, populate it (project-relative)
                try
                {
                    var mpProp = typeof(MeshFilter).GetProperty("ModelPath",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                    if (mpProp != null && mpProp.PropertyType == typeof(string) && mpProp.CanWrite)
                        mpProp.SetValue(mf, relModelPathForNode);
                }
                catch { /* ignore if property absent */ }

                //  set the sequential layer number on import if MeshFilter has ModelPartIndex (int)
                try
                {
                    var mpiProp = typeof(MeshFilter).GetProperty("ModelPartIndex",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                    if (mpiProp != null && mpiProp.CanWrite && mpiProp.PropertyType == typeof(int))
                        mpiProp.SetValue(mf, partIndex);
                }
                catch { /* ignore if property absent */ }
                finally
                {
                    partIndex++; // increment after assigning to this mesh
                }

                // Attach material if present
                if (aim.MaterialIndex >= 0 && materials.TryGetValue(aim.MaterialIndex, out var mat))
                {
                    mr.Material = mat;
                    mr.Color = mat.Tint; // tint multiplies the textures in your renderer
                }

                if (!hasNormals) mesh.RecalculateNormalsSmooth();

                // Double-sided if material says so
                bool twoSided = false;
                if (aim.MaterialIndex >= 0 && aim.MaterialIndex < sc.MaterialCount)
                {
                    var aiMat = sc.Materials[aim.MaterialIndex];
                    var t = aiMat.GetType();

                    var pTwo = t.GetProperty("TwoSided");
                    if (pTwo != null) twoSided = Convert.ToBoolean(pTwo.GetValue(aiMat));
                    else
                    {
                        var pIs = t.GetProperty("IsTwoSided");
                        if (pIs != null) twoSided = Convert.ToBoolean(pIs.GetValue(aiMat));
                        else
                        {
                            var pHas = t.GetProperty("HasTwoSided");
                            if (pHas != null) twoSided = Convert.ToBoolean(pHas.GetValue(aiMat));
                        }
                    }
                }
                mr.DoubleSided = twoSided;

                go.AddBehavior(mf);
                go.AddBehavior(mr);
            }

            // Children
            foreach (var child in node.Children)
                go.AddChild(ConvertNode(sc, child, materials, scaleFactor, relModelPathForNode, ref partIndex));

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
            // UV1 -> UV2 (for wind weight/phase)
            if (m.HasTextureCoords(1))
            {
                var ch = m.TextureCoordinateChannels[1];
                var uv2 = new SN.Vector2[ch.Count];
                for (int i = 0; i < uv2.Length; i++)
                {
                    var t = ch[i]; // Assimp stores 3D UVs; ignore Z
                    uv2[i] = new SN.Vector2(t.X, t.Y);
                }
                mesh.UV2 = uv2;
            }


            return (mesh, hadNormals);
        }

        static void ApplyTransform(Matrix4x4 ai, Component.Transform t)
        {
            ai.Decompose(out var s, out var r, out var p);

            // Position
            t.Position = new Vector3(p.X, p.Y, p.Z);

            // Rotation (Euler YXZ)
            ToEulerYXZ(r, out double rx, out double ry, out double rz);
            t.Rotation = new Vector3(rx, ry, rz);

            // Scale
            t.Scale = new Vector3(s.X, s.Y, s.Z);
        }

        static void ToEulerYXZ(Assimp.Quaternion q, out double rx, out double ry, out double rz)
        {
            var nq = new SN.Quaternion(q.X, q.Y, q.Z, q.W);
            var m = SN.Matrix4x4.CreateFromQuaternion(nq);

            ry = Math.Atan2(m.M13, m.M33);
            rx = Math.Asin(Math.Clamp(-m.M23, -1f, 1f));
            rz = Math.Atan2(m.M21, m.M22);

            const double Rad2Deg = 180.0 / Math.PI;
            rx *= Rad2Deg; ry *= Rad2Deg; rz *= Rad2Deg;
        }

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
