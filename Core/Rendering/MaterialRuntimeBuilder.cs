using System;
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
    }
}
