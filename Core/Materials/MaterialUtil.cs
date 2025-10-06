#nullable enable
using Game_Engine.Core.Component;
using System;
using System.Linq;
using System.Reflection;

namespace Game_Engine.Core;

public static class MaterialUtil
{
    /// Decide if this renderer should be drawn in the transparent pass.
    public static bool IsRendererTransparent(MeshRenderer mr)
    {
        if (mr.Color.A < 255) return true;

        var matProp = mr.GetType().GetProperty("Material",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var mat = matProp?.GetValue(mr);
        if (mat == null) return false;

        var mt = mat.GetType();

        if (TryGetBool(mt, mat, "Transparent", out var isTrans) && isTrans) return true;

        if (TryGetDouble(mt, mat, "Opacity", out var opacity) && opacity < 0.999) return true;

        if (TryGetString(mt, mat, "Blend", out var blend) && BlendImpliesTransparency(blend)) return true;

        if (TryGetString(mt, mat, "BlendMode", out var blendMode) && BlendImpliesTransparency(blendMode)) return true;

        var texListProp = mt.GetProperty("Textures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (texListProp?.GetValue(mat) is System.Collections.IEnumerable slots)
        {
            foreach (var slot in slots)
            {
                string usage = GetUsage(slot);

                if (usage == "opacity" || usage == "transparent" ||
                    usage.Contains("alpha") || usage.Contains("transp"))
                    return true;

                if (usage.Contains("albedo") || usage.Contains("basecolor") ||
                    usage.Contains("base") || usage.Contains("diff"))
                {
                    var texObj = GetTextureObject(slot);
                    if (texObj != null && TextureHasAnyAlpha(texObj))
                        return true;
                }
            }
        }

        return false;
    }

    // ---------- helpers ----------
    static bool TryGetBool(Type t, object o, string name, out bool v)
    {
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.PropertyType == typeof(bool)) { v = (bool)p.GetValue(o)!; return true; }
        v = false; return false;
    }
    static bool TryGetDouble(Type t, object o, string name, out double v)
    {
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null) { var raw = p.GetValue(o); v = raw is float f ? f : raw is double d ? d : 1.0; return true; }
        v = 1.0; return false;
    }
    static bool TryGetString(Type t, object o, string name, out string s)
    {
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null) { s = p.GetValue(o)?.ToString() ?? ""; return true; }
        s = ""; return false;
    }
    static bool BlendImpliesTransparency(string s)
    {
        s = (s ?? "").ToLowerInvariant();
        return s.Contains("alpha") || s.Contains("transp") || s.Contains("add") || s.Contains("screen");
    }
    static string GetUsage(object slot)
    {
        var up = slot.GetType().GetProperty("Usage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var u = up?.GetValue(slot);
        return (u?.ToString() ?? "albedo").ToLowerInvariant();
    }
    static object? GetTextureObject(object slot)
    {
        var p = slot.GetType().GetProperty("Texture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var raw = p?.GetValue(slot);
        var tex = raw as Texture2D ?? TextureBridge.EnsureEngineTexture2D(raw);
        if (tex != null && tex != raw && p is { CanWrite: true }) p!.SetValue(slot, tex);
        return tex;
    }
    static bool TextureHasAnyAlpha(object texLike)
    {
        var tex = texLike as Texture2D ?? TextureBridge.EnsureEngineTexture2D(texLike);
        if (tex is null) return false;

        var rgba = tex.Rgba;
        if (rgba == null || rgba.Length < 4) return false;
        int pixels = rgba.Length / 4;
        if (pixels <= 0) return false;

        int step = Math.Max(1, pixels / 1024);
        for (int i = 0; i < pixels; i += step)
        {
            int a = rgba[i * 4 + 3];
            if (a < 250) return true; // allow minor noise
        }
        return false;
    }
}
