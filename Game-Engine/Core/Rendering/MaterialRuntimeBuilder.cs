using System;
using System.IO;
using System.Numerics; // Vector4
using Avalonia.Media;
using Game_Engine.Core;

namespace Game_Engine.Core.Rendering
{
    public static class MaterialRuntimeBuilder
    {
        // Build runtime Material from asset+shader
        public static Material Build(MaterialAsset src, ShaderAsset shader)
        {
            var m = new Material();
            m.Name = string.IsNullOrWhiteSpace(src?.Name) ? "Material" : src.Name;
            m.ShaderAssetPath = src != null ? src.ShaderPath : null;

            // Lit/Unlit from shader technique prefix
            var tech = (shader != null ? shader.Technique : null) ?? "";
            m.Lit = tech.StartsWith("Lit", StringComparison.OrdinalIgnoreCase);

            // Scalars
            var baseCol = GetColor(src, "_BaseColor", 1f, 1f, 1f, 1f);
            m.BaseColor = Color.FromArgb(
                (byte)Math.Round(baseCol.W * 255f),
                (byte)Math.Round(baseCol.X * 255f),
                (byte)Math.Round(baseCol.Y * 255f),
                (byte)Math.Round(baseCol.Z * 255f));

            // Either _Smoothness or _Roughness (if both missing, default 0.5 roughness)
            var smooth = GetFloat(src, "_Smoothness", -1f);
            var rough = GetFloat(src, "_Roughness", -1f);
            if (smooth >= 0f) m.Smoothness = Clamp01(smooth);
            else if (rough >= 0f) m.Roughness = Clamp01(rough);
            else m.Roughness = 0.5f;

            m.Metallic = Clamp01(GetFloat(src, "_Metallic", 0f));

            // Transparency flags
            var transp = GetBool(src, "Transparent", false) || GetBool(src, "_Transparent", false);
            m.Transparent = transp;
            m.AlphaCutoff = Clamp01(GetFloat(src, "_AlphaCutoff", m.AlphaCutoff));

            // ── Texture2D properties → RuntimeTexSlot entries ──
            // The old code never processed texture properties, so materials loaded
            // through the asset pipeline always had zero texture slots.
            if (src?.Properties != null)
            {
                foreach (var kv in src.Properties)
                {
                    var prop = kv.Value;
                    if (prop == null || prop.Type != ShaderPropType.Texture2D) continue;
                    if (string.IsNullOrWhiteSpace(prop.TexturePath)) continue;

                    string usage = GuessUsageFromPropertyName(kv.Key);
                    string absPath = ResolveTexturePath(prop.TexturePath);
                    if (string.IsNullOrWhiteSpace(absPath) || !File.Exists(absPath)) continue;

                    try
                    {
                        var tex = Texture2D.FromFile(absPath);
                        if (tex != null)
                        {
                            m.Textures.Add(new RuntimeTexSlot
                            {
                                Texture = tex,
                                Usage = usage,
                                FaceMask = -1,
                                SourcePath = prop.TexturePath
                            });
                        }
                    }
                    catch { /* skip unreadable textures */ }
                }
            }

            return m;
        }

        private static float Clamp01(float v) { if (v < 0f) return 0f; if (v > 1f) return 1f; return v; }

        private static float GetFloat(MaterialAsset src, string name, float def)
        {
            if (src == null || src.Properties == null) return def;
            MaterialPropertyValue v;
            if (src.Properties.TryGetValue(name, out v) && v != null && v.Floats != null && v.Floats.Length >= 1)
                return (float)v.Floats[0];
            return def;
        }

        private static bool GetBool(MaterialAsset src, string name, bool def)
        {
            if (src == null || src.Properties == null) return def;
            MaterialPropertyValue v;
            if (src.Properties.TryGetValue(name, out v) && v != null)
                return v.Bool;
            return def;
        }

        private static Vector4 GetColor(MaterialAsset src, string name, float r, float g, float b, float a)
        {
            if (src == null || src.Properties == null) return new Vector4(r, g, b, a);
            MaterialPropertyValue v;
            if (src.Properties.TryGetValue(name, out v) && v != null && v.Floats != null && v.Floats.Length >= 4)
                return new Vector4(v.Floats[0], v.Floats[1], v.Floats[2], v.Floats[3]);
            return new Vector4(r, g, b, a);
        }

        // ── Texture helpers ──

        /// <summary>
        /// Map a shader property name (e.g. "_BaseMap", "_NormalMap") to a texture usage
        /// string that the SceneRenderer recognizes for binding to the correct sampler.
        /// </summary>
        private static string GuessUsageFromPropertyName(string propName)
        {
            if (string.IsNullOrWhiteSpace(propName)) return "Albedo";
            var n = propName.ToLowerInvariant();
            if (n.Contains("base") || n.Contains("albedo") || n.Contains("diffuse") || n.Contains("maintex") || n.Contains("color_map")) return "Albedo";
            if (n.Contains("normal") || n.Contains("bump")) return "Normal";
            if (n.Contains("metal")) return "Metallic";
            if (n.Contains("rough") || n.Contains("smooth")) return "Roughness";
            if (n.Contains("ao") || n.Contains("occl")) return "AmbientOcclusion";
            if (n.Contains("emiss")) return "Emissive";
            if (n.Contains("spec")) return "Specular";
            if (n.Contains("opac") || n.Contains("alpha") || n.Contains("transp")) return "Opacity";
            return "Albedo";
        }

        /// <summary>
        /// Resolve a project-relative or absolute texture path to an absolute path.
        /// </summary>
        private static string ResolveTexturePath(string relOrAbs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relOrAbs)) return null;
                if (Path.IsPathRooted(relOrAbs)) return Path.GetFullPath(relOrAbs);
                var proj = ProjectService.Current;
                if (proj == null) return relOrAbs;
                return Path.GetFullPath(Path.Combine(proj.RootPath, relOrAbs));
            }
            catch { return relOrAbs; }
        }
    }
}
