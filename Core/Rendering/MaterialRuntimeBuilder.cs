using System;
using System.IO;
using System.Numerics; // for Vector4
using Game_Engine.Core;               
using Game_Engine.Core.Rendering;

namespace Game_Engine.Core.Rendering
{
    public static class MaterialRuntimeBuilder
    {
        // Build the engine's runtime Material from MaterialAsset + ShaderAsset
        public static Material Build(MaterialAsset src, ShaderAsset shader)
        {

            var m = new Material();

            if (string.Equals(shader.Technique, "Unlit/Color", StringComparison.OrdinalIgnoreCase))
            {
                // base properties: _BaseColor (Color), _MainTex (Texture2D)
                var color = GetColor(src, "_BaseColor", 1f, 1f, 1f, 1f);
                m.Tint = ColorUtil.FromRGBA(color.X, color.Y, color.Z, color.W);

                var mainTex = GetTexture(src, "_MainTex");
                if (!string.IsNullOrEmpty(mainTex))
                    m.AlbedoTexturePath = mainTex;

                m.Lit = false; // unlit
            }
            else if (string.Equals(shader.Technique, "Lit/Standard", StringComparison.OrdinalIgnoreCase))
            {
                // Minimal PBR-ish pack for your current rasterizer params:
                // _BaseColor (Color), _BaseMap (Texture2D)
                // _Metallic (Range 0..1), _Smoothness (Range 0..1)
                // _NormalMap (Texture2D), _AOMap (Texture2D)
                var color = GetColor(src, "_BaseColor", 1f, 1f, 1f, 1f);
                m.Tint = ColorUtil.FromRGBA(color.X, color.Y, color.Z, color.W);

                var baseMap = GetTexture(src, "_BaseMap");
                if (!string.IsNullOrEmpty(baseMap))
                    m.AlbedoTexturePath = baseMap;

                float metallic = GetFloat(src, "_Metallic", 0f);
                float smooth = GetFloat(src, "_Smoothness", 0.5f);

                m.Metallic = metallic;
                m.Smoothness = smooth; // map to whatever  rasterizer uses for specular/roughness WIP FIX LATER

                var nrm = GetTexture(src, "_NormalMap");
                if (!string.IsNullOrEmpty(nrm))
                    m.NormalTexturePath = nrm;

                var ao = GetTexture(src, "_AOMap");
                if (!string.IsNullOrEmpty(ao))
                    m.AOTexturePath = ao;

                m.Lit = true;
            }
            else
            {
                // Fallback: treat as unlit color
                var color = GetColor(src, "_BaseColor", 1f, 1f, 1f, 1f);
                m.Tint = ColorUtil.FromRGBA(color.X, color.Y, color.Z, color.W);
                m.Lit = false;
            }

            return m;
        }

        private static float GetFloat(MaterialAsset src, string name, float def)
        {
            MaterialPropertyValue v;
            if (src.Properties.TryGetValue(name, out v) && v != null)
            {
                if ((v.Type == ShaderPropType.Float || v.Type == ShaderPropType.Range) && v.Floats != null && v.Floats.Length >= 1)
                    return v.Floats[0];
            }
            return def;
        }

        private static Vector4 GetColor(MaterialAsset src, string name, float r, float g, float b, float a)
        {
            MaterialPropertyValue v;
            if (src.Properties.TryGetValue(name, out v) && v != null && v.Type == ShaderPropType.Color && v.Floats != null && v.Floats.Length >= 4)
                return new Vector4(v.Floats[0], v.Floats[1], v.Floats[2], v.Floats[3]);
            return new Vector4(r, g, b, a);
        }

        private static string GetTexture(MaterialAsset src, string name)
        {
            MaterialPropertyValue v;
            if (src.Properties.TryGetValue(name, out v) && v != null && v.Type == ShaderPropType.Texture2D)
                return v.TexturePath;
            return null;
        }
    }
}
