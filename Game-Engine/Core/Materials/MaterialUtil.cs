#nullable enable
using Game_Engine.Core.Component;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Game_Engine.Core;

public static class MaterialUtil
{
    /// <summary>
    /// When FBX omits texture paths (empty .material), match <see cref="Material.Name"/> to a <c>*_Col*</c> file
    /// in nearby <c>textures</c> folders (e.g. material <c>BarkB</c> → <c>BarkBB_Col.png</c>).
    /// </summary>
    public static void TryGuessMissingMapsByMaterialName(Material m, string? materialName, IEnumerable<string>? textureDirectoriesAbsolute, string? projectRootForRelativePaths)
    {
        if (HasLoadedAlbedoTexture(m)) return;
        if (string.IsNullOrWhiteSpace(projectRootForRelativePaths)) return;
        if (textureDirectoriesAbsolute == null) return;

        var dirs = textureDirectoriesAbsolute
            .Select(d => Path.GetFullPath(d))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dirs.Count == 0) return;

        string mat = CleanBlenderMaterialSuffix(materialName ?? m.Name ?? "");
        if (string.IsNullOrWhiteSpace(mat)) return;

        var (albedoAbs, stem) = FindBestMatchingColMap(mat, dirs);
        if (string.IsNullOrEmpty(albedoAbs) || string.IsNullOrEmpty(stem)) return;

        string projRoot = Path.GetFullPath(projectRootForRelativePaths);

        try
        {
            var alb = Texture2D.FromFile(albedoAbs);
            string rel = Path.GetRelativePath(projRoot, Path.GetFullPath(albedoAbs));
            if (rel.StartsWith("..", StringComparison.Ordinal)) rel = albedoAbs.Replace('\\', '/');
            else rel = rel.Replace('\\', '/');
            m.Textures.Add(new RuntimeTexSlot { Usage = "Albedo", Texture = alb, SourcePath = rel, FaceMask = -1 });
        }
        catch { return; }

        string texDir = Path.GetDirectoryName(albedoAbs)!;
        TryAddOptionalTextureFile(m, texDir, stem, projRoot, new[] { "_Nor.png", "_Nor.PNG", "_Nor2.png", "_Normal.png", "_NORMAL.png" }, "Normal");
        TryAddOptionalTextureFile(m, texDir, stem, projRoot, new[] { "_Rgn.png", "_Rgh.png", "_Roughness.png" }, "Roughness");
    }

    static bool HasLoadedAlbedoTexture(Material m)
    {
        foreach (var s in m.Textures)
        {
            if (s is not RuntimeTexSlot r || r.Texture == null) continue;
            string u = r.Usage?.ToLowerInvariant() ?? "";
            if (u.Contains("albedo") || u.Contains("diffuse") || u.Contains("base") || u == "")
                return true;
        }
        return false;
    }

    static bool HasTextureUsageLoaded(Material m, string usageNeedle)
    {
        string n = usageNeedle.ToLowerInvariant();
        foreach (var s in m.Textures)
        {
            if (s is not RuntimeTexSlot r || r.Texture == null) continue;
            if ((r.Usage ?? "").Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static string CleanBlenderMaterialSuffix(string name)
    {
        name = name.Trim();
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1)
        {
            ReadOnlySpan<char> tail = name.AsSpan(dot + 1);
            if (tail.Length <= 4 && int.TryParse(tail, out _))
                return name.Substring(0, dot);
        }
        return name;
    }

    static (string? path, string? stem) FindBestMatchingColMap(string matName, List<string> dirs)
    {
        string? bestPath = null;
        string? bestStem = null;
        int bestScore = int.MinValue;

        foreach (string texDir in dirs)
        {
            foreach (string abs in Directory.EnumerateFiles(texDir))
            {
                string ext = Path.GetExtension(abs);
                if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                    continue;

                string fn = Path.GetFileNameWithoutExtension(abs);
                if (fn.IndexOf("_Col", StringComparison.OrdinalIgnoreCase) < 0) continue;

                int idx = fn.LastIndexOf("_Col", StringComparison.OrdinalIgnoreCase);
                if (idx <= 0) continue;
                string stem = fn.Substring(0, idx);
                if (stem.Length == 0) continue;

                bool match = stem.StartsWith(matName, StringComparison.OrdinalIgnoreCase)
                    || matName.StartsWith(stem, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;

                int score = Math.Min(matName.Length, stem.Length) * 4;
                if (string.Equals(stem, matName, StringComparison.OrdinalIgnoreCase)) score += 200;
                else if (stem.StartsWith(matName, StringComparison.OrdinalIgnoreCase)) score += 120;
                else score += 40;

                bool fileIsCol2Variant = fn.IndexOf("_Col2", StringComparison.OrdinalIgnoreCase) >= 0
                    || fn.IndexOf("_col2", StringComparison.OrdinalIgnoreCase) >= 0;
                if (fileIsCol2Variant && matName.Length > 0 && !char.IsDigit(matName[matName.Length - 1]))
                    score -= 100;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = abs;
                    bestStem = stem;
                }
            }
        }

        return (bestPath, bestStem);
    }

    static void TryAddOptionalTextureFile(Material m, string texDir, string stem, string projRoot, string[] fileSuffixesWithExt, string usage)
    {
        if (HasTextureUsageLoaded(m, usage)) return;
        foreach (string suf in fileSuffixesWithExt)
        {
            string path = Path.Combine(texDir, stem + suf);
            if (!File.Exists(path)) continue;
            try
            {
                var t = Texture2D.FromFile(path);
                string rel = Path.GetRelativePath(projRoot, Path.GetFullPath(path));
                if (rel.StartsWith("..", StringComparison.Ordinal)) rel = path.Replace('\\', '/');
                else rel = rel.Replace('\\', '/');
                m.Textures.Add(new RuntimeTexSlot { Usage = usage, Texture = t, SourcePath = rel, FaceMask = -1 });
                return;
            }
            catch { /* next */ }
        }
    }

    /// <summary>
    /// Many DCC exports use <c>Thing_Col.png</c> for RGB and <c>Thing_Alpha.png</c> for a mask; FBX often only references the Col map.
    /// Loads a matching sibling opacity file into an Opacity slot when missing.
    /// </summary>
    public static void TryBindColAlphaSiblingMaps(Material m, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return;
        projectRoot = Path.GetFullPath(projectRoot);
        foreach (var s in m.Textures)
        {
            if (s is RuntimeTexSlot rr && (rr.Usage ?? "").Contains("opacity", StringComparison.OrdinalIgnoreCase))
                return;
        }

        foreach (var texSlot in m.Textures.ToArray())
        {
            if (texSlot is not RuntimeTexSlot rts || string.IsNullOrWhiteSpace(rts.SourcePath) || rts.Texture == null) continue;
            string usage = rts.Usage?.ToLowerInvariant() ?? "";
            if (usage != "" && !usage.Contains("albedo") && !usage.Contains("diffuse") && !usage.Contains("base")) continue;

            string rel = rts.SourcePath.Replace('/', Path.DirectorySeparatorChar);
            string absAlbedo = Path.GetFullPath(Path.Combine(projectRoot, rel));
            if (!File.Exists(absAlbedo)) continue;

            string? absOp = FindColSiblingOpacityPath(absAlbedo);
            if (string.IsNullOrEmpty(absOp) || !File.Exists(absOp)) continue;

            try
            {
                var opTex = Texture2D.FromFile(absOp);
                string relOp = Path.GetRelativePath(projectRoot, Path.GetFullPath(absOp));
                if (relOp.StartsWith("..", StringComparison.Ordinal)) continue;
                relOp = relOp.Replace('\\', '/');
                m.Textures.Add(new RuntimeTexSlot
                {
                    Usage = "Opacity",
                    Texture = opTex,
                    SourcePath = relOp,
                    FaceMask = -1
                });
                return;
            }
            catch { /* ignore */ }
        }
    }

    static string? FindColSiblingOpacityPath(string absAlbedoPath)
    {
        string? dir = Path.GetDirectoryName(absAlbedoPath);
        if (string.IsNullOrEmpty(dir)) return null;
        string fn = Path.GetFileNameWithoutExtension(absAlbedoPath);
        int idx = fn.LastIndexOf("_Col", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string prefix = fn.Substring(0, idx);
        if (string.IsNullOrEmpty(prefix)) return null;
        string suffix = fn.Substring(idx + 4);
        string[] mids = { "_Alpha", "_alpha" };
        string[] exts = { ".png", ".PNG", ".tga", ".TGA" };
        foreach (var mid in mids)
        {
            foreach (var ext in exts)
            {
                string p = Path.Combine(dir, prefix + mid + suffix + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>
    /// Opaque materials: enable cutout from texture alpha, sibling opacity maps, or explicit JSON values.
    /// </summary>
    public static void EnsureOpaqueFoliageCutout(Material m)
    {
        if (m.Transparent) return;

        bool hasOpacitySlot = false;
        Texture2D? albedoTex = null;
        string? albedoRelPath = null;
        foreach (var texSlot in m.Textures)
        {
            if (texSlot is not RuntimeTexSlot rts || rts.Texture == null) continue;
            string u = rts.Usage?.ToLowerInvariant() ?? "";
            if (u.Contains("opacity") || u.Contains("transparency"))
                hasOpacitySlot = true;
            bool albedoSlot = u.Contains("albedo") || u.Contains("diffuse") || u.Contains("base") || u == "";
            if (albedoSlot)
            {
                albedoTex = rts.Texture;
                if (string.IsNullOrEmpty(albedoRelPath) && !string.IsNullOrWhiteSpace(rts.SourcePath))
                    albedoRelPath = rts.SourcePath;
            }
        }

        // Older builds auto-set LumaClip using a heuristic that also matched bark PBR maps.
        if (m.LumaClip > 0.0001f && !string.IsNullOrEmpty(albedoRelPath))
        {
            var low = albedoRelPath.ToLowerInvariant();
            if (low.Contains("bark") || low.Contains("cortex") || low.Contains("trunk") || low.Contains("wood"))
                m.LumaClip = 0f;
        }

        // Opacity map defines the silhouette — do not combine with luma discard (eats dark bark texels).
        if (hasOpacitySlot)
        {
            m.LumaClip = 0f;
            if (m.AlphaCutoff <= 0.0001f)
                m.AlphaCutoff = 0.45f;
            return;
        }

        if (m.AlphaCutoff > 0.0001f || m.LumaClip > 0.0001f) return;

        if (albedoTex != null && TextureHasMeaningfulAlpha(albedoTex))
        {
            m.AlphaCutoff = 0.45f;
            return;
        }

        // No automatic LumaClip: bark/trunk PBR textures also have dark creases + highlights and
        // matched the old heuristic, discarding the whole mesh. Use *_Alpha siblings, real
        // albedo alpha, or set LumaClip manually in .material when needed.
    }

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
        return TextureHasMeaningfulAlpha(tex);
    }

    /// <summary>
    /// Vegetation cards often ship as opaque RGB with black/beige backdrops. Build alpha from
    /// backdrop distance + green chroma so alpha-test can cut the rectangle.
    /// </summary>
    public static void EnsureVegetationCardCutout(Material m)
    {
        if (m == null) return;

        RuntimeTexSlot? albedo = null;
        foreach (var texSlot in m.Textures)
        {
            if (texSlot is not RuntimeTexSlot rts || rts.Texture == null) continue;
            string u = rts.Usage?.ToLowerInvariant() ?? "";
            if (u.Contains("opacity") || u.Contains("transparency"))
            {
                if (m.AlphaCutoff < 0.05f) m.AlphaCutoff = 0.4f;
                return;
            }
            bool albedoSlot = u.Contains("albedo") || u.Contains("diffuse") || u.Contains("base") || u == "";
            if (albedoSlot && albedo == null)
                albedo = rts;
        }

        if (albedo?.Texture == null)
        {
            if (m.AlphaCutoff < 0.05f) m.AlphaCutoff = 0.4f;
            if (m.LumaClip < 0.05f) m.LumaClip = 0.14f;
            return;
        }

        if (TextureHasMeaningfulAlpha(albedo.Texture))
        {
            if (m.AlphaCutoff < 0.05f) m.AlphaCutoff = 0.4f;
            return;
        }

        var cut = BuildRgbBackdropAlphaTexture(albedo.Texture);
        if (cut != null)
        {
            albedo.Texture = cut;
            m.AlphaCutoff = 0.42f;
            m.LumaClip = 0.08f;
            return;
        }

        if (m.AlphaCutoff < 0.05f) m.AlphaCutoff = 0.4f;
        if (m.LumaClip < 0.05f) m.LumaClip = 0.16f;
    }

    static Texture2D? BuildRgbBackdropAlphaTexture(Texture2D src)
    {
        if (src.Rgba == null || src.Width < 4 || src.Height < 4) return null;
        int w = src.Width;
        int h = src.Height;
        var rgba = new byte[src.Rgba.Length];
        Buffer.BlockCopy(src.Rgba, 0, rgba, 0, rgba.Length);

        int bandX = Math.Max(2, w / 12);
        int bandY = Math.Max(2, h / 12);
        float bgR = 0f, bgG = 0f, bgB = 0f;
        int bgCount = 0;
        void Acc(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                int row = y * w * 4;
                for (int x = x0; x < x1; x++)
                {
                    int i = row + x * 4;
                    bgR += rgba[i + 0];
                    bgG += rgba[i + 1];
                    bgB += rgba[i + 2];
                    bgCount++;
                }
            }
        }
        Acc(0, 0, bandX, bandY);
        Acc(w - bandX, 0, w, bandY);
        Acc(0, h - bandY, bandX, h);
        Acc(w - bandX, h - bandY, w, h);
        if (bgCount > 0)
        {
            bgR /= bgCount;
            bgG /= bgCount;
            bgB /= bgCount;
        }

        int kept = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                float r = rgba[i + 0];
                float g = rgba[i + 1];
                float b = rgba[i + 2];
                float dr = r - bgR, dg = g - bgG, db = b - bgB;
                float dist = MathF.Sqrt(dr * dr + dg * dg + db * db) / 441.67294f;
                float nr = r / 255f, ng = g / 255f, nb = b / 255f;
                float luma = nr * 0.299f + ng * 0.587f + nb * 0.114f;
                float greenScore = ng - MathF.Max(nr, nb);
                float alpha = MathF.Max(Smooth01(0.05f, 0.22f, dist), Smooth01(0.01f, 0.14f, greenScore));
                if (luma < 0.07f) alpha = 0f;
                if (dist < 0.045f && greenScore < 0.04f) alpha = 0f;
                byte outA = (byte)Math.Clamp((int)(alpha * 255f), 0, 255);
                rgba[i + 3] = outA;
                if (outA > 24) kept++;
            }
        }

        float keepRatio = kept / (float)(w * h);
        if (keepRatio < 0.03f || keepRatio > 0.97f)
            return null;
        return new Texture2D(w, h, rgba, src.SourcePath);
    }

    static float Smooth01(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / Math.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>True if CPU-side RGBA data has any clearly non-opaque pixels (import / cutout heuristics).</summary>
    public static bool TextureHasMeaningfulAlpha(Texture2D? tex)
    {
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
