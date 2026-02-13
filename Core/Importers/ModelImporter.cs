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
        /// <summary>Holds mesh data for skinned meshes that need to be flattened to root level.</summary>
        private struct PendingSkinnedMesh
        {
            public Assimp.Mesh AiMesh;
            public Node AiNode;
            public Mesh Mesh;
            public bool HadNormals;
            public SN.Matrix4x4 NodeGlobalTransform;
            public Material? Material;
            public bool DoubleSided;
            public int PartIndex;
            public string Name;
        }

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
                   | PostProcessSteps.RemoveRedundantMaterials
                   | PostProcessSteps.LimitBoneWeights;

            var scene = ctx.ImportFile(absModel, pp);
            if (scene is null || !scene.HasMeshes)
                throw new InvalidDataException("No meshes in file, or import failed.");

            // Build materials first (index -> engine Material)
            var materials = BuildMaterials(scene, modelDir);

            // Normalize scale — ALL meshes (including skinned) get vertex-scaled
            // so the model fits into ~1 unit radius. This is backward-compatible
            // with old scene saves that have Transform.Scale = (1,1,1).
            float maxScale = 0f;
            foreach (var m in scene.Meshes)
            {
                var (radius, _) = ApproxRadialAndHeight(m);
                maxScale = Math.Max(maxScale, radius);
            }
            float scaleFactor = maxScale > 0 ? 1f / maxScale : 1f;

            // Build skeleton from bone data (if model has skinned meshes)
            Skeleton? skeleton = null;
            var boneNameSet = CollectBoneNames(scene);
            bool hasSkin = boneNameSet.Count > 0;
            if (hasSkin)
            {
                skeleton = BuildSkeleton(scene, boneNameSet);

                // When vertices are uniformly scaled by s, the translation
                // components of bone matrices must also be scaled by s so
                // that the skinned output is in the same scaled space.
                // (Rotation/scale parts are unaffected by uniform scaling.)
                if (skeleton != null && Math.Abs(scaleFactor - 1f) > 0.0001f)
                {
                    for (int bi = 0; bi < skeleton.BoneCount; bi++)
                    {
                        var bone = skeleton.Bones[bi];

                        // Scale OffsetMatrix translation
                        var om = bone.OffsetMatrix;
                        om.M41 *= scaleFactor;
                        om.M42 *= scaleFactor;
                        om.M43 *= scaleFactor;
                        bone.OffsetMatrix = om;

                        // Scale LocalBindTransform translation
                        var lb = bone.LocalBindTransform;
                        lb.M41 *= scaleFactor;
                        lb.M42 *= scaleFactor;
                        lb.M43 *= scaleFactor;
                        bone.LocalBindTransform = lb;
                    }
                    Log.Info($"[ModelImporter] Adjusted bone matrix translations for vertex scale {scaleFactor:F6}");
                }
            }

            // Convert nodes recursively with a running part index.
            // For skinned models, mesh components are collected into a flat list
            // and added as direct children of root — this avoids bone hierarchy
            // transforms being baked into uModel, which would double-position
            // skinned vertices (once by the GO hierarchy, once by bone matrices).
            int partIndex = 0;
            List<PendingSkinnedMesh>? pendingMeshes = hasSkin ? new List<PendingSkinnedMesh>() : null;
            var root = ConvertNode(scene, scene.RootNode, materials, scaleFactor, relModel, ref partIndex, skeleton, pendingMeshes, (SN.Matrix4x4?)SN.Matrix4x4.Identity);

            root.Name = Path.GetFileNameWithoutExtension(absModel);

            // Add collected skinned meshes as direct children of root with identity transform
            if (pendingMeshes != null)
            {
                foreach (var pm in pendingMeshes)
                {
                    var mesh = pm.Mesh;

                    // For meshes without bone data: transform vertices from node-local
                    // space to model space and assign them to the nearest parent bone.
                    if (!mesh.HasBones && skeleton != null)
                    {
                        // Transform vertices to model space
                        if (pm.NodeGlobalTransform != SN.Matrix4x4.Identity)
                        {
                            for (int vi = 0; vi < mesh.Vertices.Length; vi++)
                                mesh.Vertices[vi] = SN.Vector3.Transform(mesh.Vertices[vi], pm.NodeGlobalTransform);
                            if (mesh.Normals != null)
                            {
                                var normalMat = pm.NodeGlobalTransform;
                                // Use inverse-transpose for normals
                                if (SN.Matrix4x4.Invert(normalMat, out var inv))
                                    normalMat = SN.Matrix4x4.Transpose(inv);
                                for (int ni = 0; ni < mesh.Normals.Length; ni++)
                                    mesh.Normals[ni] = SN.Vector3.TransformNormal(mesh.Normals[ni], normalMat);
                            }
                        }

                        // Find nearest bone from the Assimp parent node chain
                        int boneIdx = FindNearestBone(pm.AiNode, skeleton);
                        if (boneIdx < 0) boneIdx = 0; // fallback to first bone

                        // Assign all vertices to that bone with weight 1.0
                        int vc = mesh.Vertices.Length;
                        mesh.BoneWeights = new SN.Vector4[vc];
                        mesh.BoneIndices = new int[vc * 4];
                        for (int vi = 0; vi < vc; vi++)
                        {
                            mesh.BoneWeights[vi] = new SN.Vector4(1, 0, 0, 0);
                            mesh.BoneIndices[vi * 4] = boneIdx;
                        }
                        mesh.Skeleton = skeleton;
                    }

                    // Create child GO at root level with identity transform
                    var meshGO = new GameObject(pm.Name);
                    // Identity transform — bone matrices handle all positioning

                    var mf = new MeshFilter { Mesh = mesh };
                    var smr = new SkinnedMeshRenderer();
                    smr.Skeleton = skeleton;

                    // Set model path/part index for scene reload
                    try
                    {
                        var mpProp = typeof(MeshFilter).GetProperty("ModelPath",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (mpProp != null && mpProp.CanWrite)
                            mpProp.SetValue(mf, relModel);
                    }
                    catch { }
                    try
                    {
                        var mpiProp = typeof(MeshFilter).GetProperty("ModelPartIndex",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (mpiProp != null && mpiProp.CanWrite)
                        {
                            if (mpiProp.PropertyType == typeof(int)) mpiProp.SetValue(mf, pm.PartIndex);
                            else if (mpiProp.PropertyType == typeof(string)) mpiProp.SetValue(mf, pm.PartIndex.ToString());
                        }
                    }
                    catch { }

                    // Attach material
                    if (pm.Material != null)
                    {
                        smr.Material = pm.Material;
                        smr.Color = pm.Material.Tint;
                    }

                    if (!pm.HadNormals) mesh.RecalculateNormalsSmooth();
                    smr.DoubleSided = pm.DoubleSided;

                    meshGO.AddBehavior(mf);
                    meshGO.AddBehavior(smr);
                    root.AddChild(meshGO);
                }
            }

            // Import bone animations (if any)
            if (skeleton != null && scene.HasAnimations)
                ImportAnimations(scene, skeleton, relModel, scaleFactor);

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

                // Optional tint from diffuse color (including alpha)
                if (aimat.HasColorDiffuse)
                {
                    var c = aimat.ColorDiffuse;
                    byte ca = (byte)Math.Clamp((int)(c.A * 255f), 0, 255);
                    m.BaseColor = Avalonia.Media.Color.FromArgb(ca,
                        (byte)Math.Clamp((int)(c.R * 255f), 0, 255),
                        (byte)Math.Clamp((int)(c.G * 255f), 0, 255),
                        (byte)Math.Clamp((int)(c.B * 255f), 0, 255));
                    if (ca < 255) m.Transparent = true;
                }

                // Detect opacity from Assimp material
                if (aimat.HasOpacity && aimat.Opacity < 0.999f)
                {
                    m.Transparent = true;
                    // Bake opacity into base color alpha if not already low
                    if (m.BaseColor.A == 255)
                    {
                        byte opA = (byte)Math.Clamp((int)(aimat.Opacity * 255f), 0, 255);
                        m.BaseColor = Avalonia.Media.Color.FromArgb(opA,
                            m.BaseColor.R, m.BaseColor.G, m.BaseColor.B);
                    }
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

            // Map enum to string for RuntimeTexSlot
            string usageStr = usage switch
            {
                MaterialTexture.TexUsage.Albedo => "Albedo",
                MaterialTexture.TexUsage.Normal => "Normal",
                MaterialTexture.TexUsage.Metallic => "Metallic",
                MaterialTexture.TexUsage.Roughness => "Roughness",
                MaterialTexture.TexUsage.Specular => "Specular",
                MaterialTexture.TexUsage.Emissive => "Emissive",
                MaterialTexture.TexUsage.AmbientOcclusion => "AmbientOcclusion",
                MaterialTexture.TexUsage.Opacity => "Opacity",
                MaterialTexture.TexUsage.Detail => "Detail",
                _ => "Albedo"
            };

            m.Textures.Add(new RuntimeTexSlot
            {
                Texture = tex,
                Usage = usageStr,
                FaceMask = -1,
                SourcePath = !string.IsNullOrWhiteSpace(resolvedAbsPath)
                    ? MakeProjectRelative(resolvedAbsPath)
                    : null
            });

            System.Diagnostics.Debug.WriteLine($"[ModelImporter] +texture '{usageStr}' ({tex.Width}x{tex.Height}) for material '{m.Name}'");
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

        static GameObject ConvertNode(Scene sc, Node node, Dictionary<int, Material> materials,
            float scaleFactor, string relModelPathForNode, ref int partIndex, Skeleton? skeleton,
            List<PendingSkinnedMesh>? pendingMeshes = null, SN.Matrix4x4? parentGlobalOpt = null)
        {
            var parentGlobal = parentGlobalOpt ?? SN.Matrix4x4.Identity;

            var go = new GameObject(node.Name);
            ApplyTransform(node.Transform, go.Transform);

            // Compute this node's global transform (accumulated from root)
            var nodeGlobal = AiToSN(node.Transform) * parentGlobal;

            // Mesh instances on this node
            foreach (var idx in node.MeshIndices)
            {
                var aim = sc.Meshes[idx];
                var (mesh, hasNormals) = ConvertMesh(aim, scaleFactor, skeleton);

                // For skinned models, collect meshes for root-level attachment later.
                // This prevents bone hierarchy transforms from accumulating into uModel,
                // which would double-position skinned vertices.
                if (pendingMeshes != null)
                {
                    // Detect double-sided
                    bool twoSided = false;
                    if (aim.MaterialIndex >= 0 && aim.MaterialIndex < sc.MaterialCount)
                    {
                        var aiMat = sc.Materials[aim.MaterialIndex];
                        var t = aiMat.GetType();
                        var pTwo = t.GetProperty("TwoSided");
                        if (pTwo != null) twoSided = Convert.ToBoolean(pTwo.GetValue(aiMat));
                        else { var pIs = t.GetProperty("IsTwoSided"); if (pIs != null) twoSided = Convert.ToBoolean(pIs.GetValue(aiMat)); }
                    }

                    Material? clonedMat = null;
                    if (aim.MaterialIndex >= 0 && materials.TryGetValue(aim.MaterialIndex, out var mat))
                        clonedMat = mat.Clone();

                    pendingMeshes.Add(new PendingSkinnedMesh
                    {
                        AiMesh = aim,
                        AiNode = node,
                        Mesh = mesh,
                        HadNormals = hasNormals,
                        NodeGlobalTransform = nodeGlobal,
                        Material = clonedMat,
                        DoubleSided = twoSided,
                        PartIndex = partIndex,
                        Name = !string.IsNullOrWhiteSpace(aim.Name) ? aim.Name : node.Name
                    });
                    partIndex++;
                    continue;
                }

                // Non-skinned path (static models) — add directly to this GO
                var mf = new MeshFilter { Mesh = mesh };
                MeshRenderer mr = new MeshRenderer();

                try
                {
                    var mpProp = typeof(MeshFilter).GetProperty("ModelPath",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (mpProp != null && mpProp.PropertyType == typeof(string) && mpProp.CanWrite)
                        mpProp.SetValue(mf, relModelPathForNode);
                }
                catch { }

                try
                {
                    var mpiProp = typeof(MeshFilter).GetProperty("ModelPartIndex",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (mpiProp != null && mpiProp.CanWrite)
                    {
                        if (mpiProp.PropertyType == typeof(int)) mpiProp.SetValue(mf, partIndex);
                        else if (mpiProp.PropertyType == typeof(string)) mpiProp.SetValue(mf, partIndex.ToString());
                    }
                }
                catch { }
                finally { partIndex++; }

                if (aim.MaterialIndex >= 0 && materials.TryGetValue(aim.MaterialIndex, out var staticMat))
                {
                    mr.Material = staticMat.Clone();
                    mr.Color = mr.Material.Tint;
                }

                if (!hasNormals) mesh.RecalculateNormalsSmooth();

                bool twoSidedStatic = false;
                if (aim.MaterialIndex >= 0 && aim.MaterialIndex < sc.MaterialCount)
                {
                    var aiMat = sc.Materials[aim.MaterialIndex];
                    var t = aiMat.GetType();
                    var pTwo = t.GetProperty("TwoSided");
                    if (pTwo != null) twoSidedStatic = Convert.ToBoolean(pTwo.GetValue(aiMat));
                    else { var pIs = t.GetProperty("IsTwoSided"); if (pIs != null) twoSidedStatic = Convert.ToBoolean(pIs.GetValue(aiMat)); }
                }
                mr.DoubleSided = twoSidedStatic;

                go.AddBehavior(mf);
                go.AddBehavior(mr);
            }

            // Children
            foreach (var child in node.Children)
                go.AddChild(ConvertNode(sc, child, materials, scaleFactor, relModelPathForNode, ref partIndex, skeleton, pendingMeshes, (SN.Matrix4x4?)nodeGlobal));

            return go;
        }

        /// <summary>Walk up the Assimp node chain from a mesh node to find the nearest bone.</summary>
        static int FindNearestBone(Node meshNode, Skeleton skeleton)
        {
            // Check the node itself and its parents
            var cur = meshNode;
            while (cur != null)
            {
                int idx = skeleton.FindBone(cur.Name);
                if (idx >= 0) return idx;
                cur = cur.Parent;
            }
            // Fallback: return first root bone
            return skeleton.RootBoneIndices.Length > 0 ? skeleton.RootBoneIndices[0] : 0;
        }


        static (Mesh mesh, bool hadNormals) ConvertMesh(Assimp.Mesh m, float scale = 1f, Skeleton? skeleton = null)
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

            // ── Bone weights & indices ──
            if (m.HasBones && skeleton != null)
            {
                int vc = m.VertexCount;
                var weights = new SN.Vector4[vc];   // x,y,z,w = weights for bones 0..3
                var indices = new int[vc * 4];      // 4 bone indices per vertex

                // Initialize weights to 0 and indices to 0
                for (int i = 0; i < vc; i++)
                    weights[i] = SN.Vector4.Zero;

                foreach (var bone in m.Bones)
                {
                    int boneIdx = skeleton.FindBone(bone.Name);
                    if (boneIdx < 0) continue;

                    foreach (var vw in bone.VertexWeights)
                    {
                        int vi = vw.VertexID;
                        if (vi < 0 || vi >= vc) continue;

                        // Find the first empty slot (weight == 0)
                        int baseI = vi * 4;
                        ref var w = ref weights[vi];
                        if (w.X == 0f)      { indices[baseI + 0] = boneIdx; w.X = vw.Weight; }
                        else if (w.Y == 0f) { indices[baseI + 1] = boneIdx; w.Y = vw.Weight; }
                        else if (w.Z == 0f) { indices[baseI + 2] = boneIdx; w.Z = vw.Weight; }
                        else if (w.W == 0f) { indices[baseI + 3] = boneIdx; w.W = vw.Weight; }
                        // If all 4 slots are taken, skip (LimitBoneWeights should prevent this)
                    }
                }

                // Normalize weights so they sum to 1
                for (int i = 0; i < vc; i++)
                {
                    ref var w = ref weights[i];
                    float sum = w.X + w.Y + w.Z + w.W;
                    if (sum > 0f)
                        weights[i] = w / sum;
                    else
                        weights[i] = new SN.Vector4(1, 0, 0, 0); // bind to bone 0 with full weight
                }

                mesh.BoneWeights = weights;
                mesh.BoneIndices = indices;
                mesh.Skeleton = skeleton;
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

        // ── Skeleton / Bone import ─────────────────────────────────────────────

        /// <summary>Collect all bone names referenced by any mesh in the scene.</summary>
        static HashSet<string> CollectBoneNames(Scene sc)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in sc.Meshes)
            {
                if (!m.HasBones) continue;
                foreach (var b in m.Bones)
                    names.Add(b.Name);
            }
            return names;
        }

        /// <summary>Build a Skeleton from the Assimp scene node tree, using the bone names found in meshes.</summary>
        static Skeleton BuildSkeleton(Scene sc, HashSet<string> boneNames)
        {
            // Collect all bone nodes from the Assimp node tree.
            var boneList = new List<Bone>();
            var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Build the offset matrices from mesh bones
            var offsetMatrices = new Dictionary<string, SN.Matrix4x4>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in sc.Meshes)
            {
                if (!m.HasBones) continue;
                foreach (var b in m.Bones)
                {
                    if (!offsetMatrices.ContainsKey(b.Name))
                        offsetMatrices[b.Name] = AiToSN(b.OffsetMatrix);
                }
            }

            // Step 1: Compute full model-space global transform for every node in the Assimp tree.
            // This is needed because there may be intermediate non-bone nodes (like "Armature")
            // between bone nodes whose transforms must be included.
            var nodeGlobals = new Dictionary<string, SN.Matrix4x4>(StringComparer.OrdinalIgnoreCase);
            void ComputeGlobals(Node node, SN.Matrix4x4 parentGlobal)
            {
                var global = AiToSN(node.Transform) * parentGlobal;
                nodeGlobals[node.Name] = global;
                foreach (var child in node.Children)
                    ComputeGlobals(child, global);
            }
            ComputeGlobals(sc.RootNode, SN.Matrix4x4.Identity);

            // Step 2: Walk and pick out bone nodes (same DFS order as before)
            void WalkNodes(Node node)
            {
                if (boneNames.Contains(node.Name) && !nameToIdx.ContainsKey(node.Name))
                {
                    int idx = boneList.Count;
                    nameToIdx[node.Name] = idx;

                    var bone = new Bone
                    {
                        Name = node.Name,
                        Index = idx,
                        ParentIndex = -1, // resolved in step 3
                        OffsetMatrix = offsetMatrices.TryGetValue(node.Name, out var om) ? om : SN.Matrix4x4.Identity,
                        LocalBindTransform = SN.Matrix4x4.Identity // computed in step 4
                    };
                    boneList.Add(bone);
                }
                foreach (var child in node.Children)
                    WalkNodes(child);
            }
            WalkNodes(sc.RootNode);

            if (boneList.Count == 0)
                return new Skeleton(Array.Empty<Bone>(), Array.Empty<int>());

            // Step 3: Resolve parent indices (nearest bone ancestor, skipping non-bone nodes)
            void ResolveParents(Node node, int parentBoneIdx)
            {
                int myIdx = -1;
                if (nameToIdx.TryGetValue(node.Name, out var idx))
                {
                    boneList[idx].ParentIndex = parentBoneIdx;
                    myIdx = idx;
                }
                foreach (var child in node.Children)
                    ResolveParents(child, myIdx >= 0 ? myIdx : parentBoneIdx);
            }
            ResolveParents(sc.RootNode, -1);

            // Step 4: Compute correct LocalBindTransform for each bone.
            // For root bones (ParentIndex == -1): LocalBindTransform = full global transform
            //   (includes all intermediate non-bone nodes like Armature rotations)
            // For child bones: LocalBindTransform = Inverse(parentBoneGlobal) * thisBoneGlobal
            //   (collapses any intermediate non-bone nodes between parent and child)
            for (int i = 0; i < boneList.Count; i++)
            {
                var bone = boneList[i];
                if (!nodeGlobals.TryGetValue(bone.Name, out var myGlobal))
                    myGlobal = SN.Matrix4x4.Identity;

                if (bone.ParentIndex < 0)
                {
                    // Root bone: local = full global (from scene root to this bone)
                    bone.LocalBindTransform = myGlobal;
                }
                else
                {
                    var parentBone = boneList[bone.ParentIndex];
                    if (nodeGlobals.TryGetValue(parentBone.Name, out var parentGlobal)
                        && SN.Matrix4x4.Invert(parentGlobal, out var invParent))
                    {
                        bone.LocalBindTransform = myGlobal * invParent;
                    }
                    else
                    {
                        // Fallback: use just the node's own local transform
                        bone.LocalBindTransform = AiToSN(FindNodeByName(sc.RootNode, bone.Name)?.Transform ?? new Matrix4x4());
                    }
                }
                boneList[i] = bone;
            }

            // Build children arrays
            var childrenMap = new Dictionary<int, List<int>>();
            foreach (var b in boneList)
            {
                if (b.ParentIndex >= 0)
                {
                    if (!childrenMap.TryGetValue(b.ParentIndex, out var list))
                    {
                        list = new List<int>();
                        childrenMap[b.ParentIndex] = list;
                    }
                    list.Add(b.Index);
                }
            }
            foreach (var b in boneList)
                b.Children = childrenMap.TryGetValue(b.Index, out var c) ? c.ToArray() : Array.Empty<int>();

            // Root bones = those with ParentIndex == -1
            var roots = boneList.Where(b => b.ParentIndex == -1).Select(b => b.Index).ToArray();

            var skeleton = new Skeleton(boneList.ToArray(), roots);
            Log.Info($"[ModelImporter] Built skeleton with {skeleton.BoneCount} bones, {roots.Length} roots");
            return skeleton;
        }

        /// <summary>Find a node by name in the Assimp node tree.</summary>
        static Node? FindNodeByName(Node root, string name)
        {
            if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase)) return root;
            foreach (var child in root.Children)
            {
                var found = FindNodeByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Convert Assimp Matrix4x4 to System.Numerics Matrix4x4.</summary>
        static SN.Matrix4x4 AiToSN(Matrix4x4 m)
        {
            return new SN.Matrix4x4(
                m.A1, m.B1, m.C1, m.D1,
                m.A2, m.B2, m.C2, m.D2,
                m.A3, m.B3, m.C3, m.D3,
                m.A4, m.B4, m.C4, m.D4
            );
        }

        // ── Animation import ───────────────────────────────────────────────────

        /// <summary>Import all animations from the Assimp scene and save as .boneanim files.</summary>
        static void ImportAnimations(Scene sc, Skeleton skeleton, string relModelPath, float vertexScale = 1f)
        {
            if (!sc.HasAnimations) return;

            var animDir = Path.ChangeExtension(relModelPath, null) + "_Animations";

            for (int ai = 0; ai < sc.AnimationCount; ai++)
            {
                var anim = sc.Animations[ai];
                float ticksPerSec = (float)(anim.TicksPerSecond > 0 ? anim.TicksPerSecond : 24.0);
                float duration = (float)(anim.DurationInTicks / ticksPerSec);

                var clip = new BoneAnimationClip
                {
                    Name = !string.IsNullOrEmpty(anim.Name) ? anim.Name : $"Anim_{ai}",
                    Duration = duration,
                    Loop = true
                };

                foreach (var channel in anim.NodeAnimationChannels)
                {
                    int boneIdx = skeleton.FindBone(channel.NodeName);
                    if (boneIdx < 0) continue;

                    var track = new BoneTrack
                    {
                        BoneName = channel.NodeName,
                        BoneIndex = boneIdx
                    };

                    // Collect all unique times from pos/rot/scale keyframes
                    var times = new SortedSet<float>();
                    foreach (var k in channel.PositionKeys) times.Add((float)(k.Time / ticksPerSec));
                    foreach (var k in channel.RotationKeys) times.Add((float)(k.Time / ticksPerSec));
                    foreach (var k in channel.ScalingKeys) times.Add((float)(k.Time / ticksPerSec));

                    foreach (var t in times)
                    {
                        float tTicks = t * ticksPerSec;
                        var pos = SamplePosition(channel, tTicks);
                        var rot = SampleRotation(channel, tTicks);
                        var scl = SampleScale(channel, tTicks);

                        // Scale position keyframes to match vertex scaling
                        if (Math.Abs(vertexScale - 1f) > 0.0001f)
                            pos *= vertexScale;

                        track.Keyframes.Add(new BoneKeyframe(t, pos, rot, scl));
                    }

                    if (track.Keyframes.Count > 0)
                        clip.Tracks.Add(track);
                }

                if (clip.Tracks.Count > 0)
                {
                    var relPath = Path.Combine(animDir, $"{clip.Name}.boneanim");
                    try
                    {
                        BoneAnimationClipAsset.Save(clip, relPath);
                        Log.Info($"[ModelImporter] Saved bone animation '{clip.Name}' ({clip.Tracks.Count} tracks, {duration:F2}s) → {relPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[ModelImporter] Failed to save animation '{clip.Name}': {ex.Message}");
                    }
                }
            }
        }

        static SN.Vector3 SamplePosition(NodeAnimationChannel ch, float ticks)
        {
            if (ch.PositionKeyCount == 0) return SN.Vector3.Zero;
            if (ch.PositionKeyCount == 1 || ticks <= ch.PositionKeys[0].Time)
            {
                var k = ch.PositionKeys[0].Value;
                return new SN.Vector3(k.X, k.Y, k.Z);
            }
            if (ticks >= ch.PositionKeys[^1].Time)
            {
                var k = ch.PositionKeys[^1].Value;
                return new SN.Vector3(k.X, k.Y, k.Z);
            }

            for (int i = 0; i < ch.PositionKeyCount - 1; i++)
            {
                if (ch.PositionKeys[i + 1].Time >= ticks)
                {
                    var k0 = ch.PositionKeys[i];
                    var k1 = ch.PositionKeys[i + 1];
                    float seg = (float)(k1.Time - k0.Time);
                    float t = seg > 0 ? (float)((ticks - k0.Time) / seg) : 0f;
                    var a = new SN.Vector3(k0.Value.X, k0.Value.Y, k0.Value.Z);
                    var b = new SN.Vector3(k1.Value.X, k1.Value.Y, k1.Value.Z);
                    return SN.Vector3.Lerp(a, b, t);
                }
            }

            var last = ch.PositionKeys[^1].Value;
            return new SN.Vector3(last.X, last.Y, last.Z);
        }

        static SN.Quaternion SampleRotation(NodeAnimationChannel ch, float ticks)
        {
            if (ch.RotationKeyCount == 0) return SN.Quaternion.Identity;
            if (ch.RotationKeyCount == 1 || ticks <= ch.RotationKeys[0].Time)
            {
                var k = ch.RotationKeys[0].Value;
                return new SN.Quaternion(k.X, k.Y, k.Z, k.W);
            }
            if (ticks >= ch.RotationKeys[^1].Time)
            {
                var k = ch.RotationKeys[^1].Value;
                return new SN.Quaternion(k.X, k.Y, k.Z, k.W);
            }

            for (int i = 0; i < ch.RotationKeyCount - 1; i++)
            {
                if (ch.RotationKeys[i + 1].Time >= ticks)
                {
                    var k0 = ch.RotationKeys[i];
                    var k1 = ch.RotationKeys[i + 1];
                    float seg = (float)(k1.Time - k0.Time);
                    float t = seg > 0 ? (float)((ticks - k0.Time) / seg) : 0f;
                    var a = new SN.Quaternion(k0.Value.X, k0.Value.Y, k0.Value.Z, k0.Value.W);
                    var b = new SN.Quaternion(k1.Value.X, k1.Value.Y, k1.Value.Z, k1.Value.W);
                    return SN.Quaternion.Slerp(a, b, t);
                }
            }

            var last = ch.RotationKeys[^1].Value;
            return new SN.Quaternion(last.X, last.Y, last.Z, last.W);
        }

        static SN.Vector3 SampleScale(NodeAnimationChannel ch, float ticks)
        {
            if (ch.ScalingKeyCount == 0) return SN.Vector3.One;
            if (ch.ScalingKeyCount == 1 || ticks <= ch.ScalingKeys[0].Time)
            {
                var k = ch.ScalingKeys[0].Value;
                return new SN.Vector3(k.X, k.Y, k.Z);
            }
            if (ticks >= ch.ScalingKeys[^1].Time)
            {
                var k = ch.ScalingKeys[^1].Value;
                return new SN.Vector3(k.X, k.Y, k.Z);
            }

            for (int i = 0; i < ch.ScalingKeyCount - 1; i++)
            {
                if (ch.ScalingKeys[i + 1].Time >= ticks)
                {
                    var k0 = ch.ScalingKeys[i];
                    var k1 = ch.ScalingKeys[i + 1];
                    float seg = (float)(k1.Time - k0.Time);
                    float t = seg > 0 ? (float)((ticks - k0.Time) / seg) : 0f;
                    var a = new SN.Vector3(k0.Value.X, k0.Value.Y, k0.Value.Z);
                    var b = new SN.Vector3(k1.Value.X, k1.Value.Y, k1.Value.Z);
                    return SN.Vector3.Lerp(a, b, t);
                }
            }

            var last = ch.ScalingKeys[^1].Value;
            return new SN.Vector3(last.X, last.Y, last.Z);
        }
    }
}
