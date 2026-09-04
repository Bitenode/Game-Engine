#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Game_Engine.Core.Planet;

namespace Game_Engine.Core.Component;

/// <summary>
/// Loads grass PSD/PNG cards off the UI thread. <see cref="Texture2D.FromFile"/>
/// (ImageMagick) on the GL thread is what froze play.
/// </summary>
public static class PlanetGrassTextureCache
{
    static readonly ConcurrentDictionary<string, Texture2D> s_ready = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, byte> s_queued = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentQueue<string> s_pendingAbs = new();
    static readonly List<string> s_catalog = new();
    static readonly List<string> s_carpetMix = new();
    static readonly object s_catalogLock = new();
    static int s_loaderRunning;
    static bool s_scanned;
    static int s_readyStamp;
    const int CarpetMixSize = 14;

    public static int ReadyCount => s_ready.Count;
    public static int ReadyStamp => Volatile.Read(ref s_readyStamp);

    public static void EnsureCatalog()
    {
        if (s_scanned) return;
        s_scanned = true;
        try
        {
            var root = ProjectService.Current?.RootPath;
            if (string.IsNullOrWhiteSpace(root)) return;
            string dir = Path.Combine(root, "Assets", "textures", "Grass");
            if (!Directory.Exists(dir)) return;
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch { return; }

            lock (s_catalogLock)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    string ext = Path.GetExtension(files[i]);
                    if (!IsGrassImageExt(ext)) continue;
                    string rel = PlanetAssetIO.NormalizeAssetReference(files[i]);
                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    if (!s_catalog.Exists(p => string.Equals(p, rel, StringComparison.OrdinalIgnoreCase)))
                        s_catalog.Add(rel);
                }
                BuildCarpetMixUnlocked();
            }
        }
        catch { /* catalog is best-effort */ }
    }

    public static void EnsureCarpetMix()
    {
        EnsureCatalog();
        lock (s_catalogLock)
            BuildCarpetMixUnlocked();
    }

    static void BuildCarpetMixUnlocked()
    {
        if (s_catalog.Count == 0)
            return;
        if (s_carpetMix.Count == 0)
        {
            int n = Math.Min(CarpetMixSize, s_catalog.Count);
            for (int i = 0; i < n; i++)
            {
                string pick = s_catalog[(i * s_catalog.Count) / n];
                if (!s_carpetMix.Exists(p => string.Equals(p, pick, StringComparison.OrdinalIgnoreCase)))
                    s_carpetMix.Add(pick);
            }
        }
        for (int i = 0; i < s_carpetMix.Count; i++)
            Request(s_carpetMix[i]);
    }

    public static int ReadyMixCount()
    {
        EnsureCarpetMix();
        int n = 0;
        lock (s_catalogLock)
        {
            for (int i = 0; i < s_carpetMix.Count; i++)
            {
                if (s_ready.ContainsKey(s_carpetMix[i]))
                    n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Stable card from the loaded mix only. Existing patches keep this key forever,
    /// so grass does not morph through PSDs as the background loader finishes.
    /// </summary>
    public static string? TryPickReady(int token)
    {
        EnsureCarpetMix();
        lock (s_catalogLock)
        {
            if (s_carpetMix.Count == 0)
                return null;
            int idx = token < 0 ? -token : token;
            string pick = s_carpetMix[idx % s_carpetMix.Count];
            return s_ready.ContainsKey(pick) ? pick : null;
        }
    }

    public static void Request(string? projectRelative)
    {
        string rel = PlanetAssetIO.NormalizeAssetReference(projectRelative ?? "");
        if (string.IsNullOrWhiteSpace(rel) || !IsGrassImageExt(Path.GetExtension(rel)))
            return;
        string abs = PlanetAssetIO.ToAbsolutePath(rel);
        if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs))
            return;
        if (s_ready.ContainsKey(rel))
            return;
        if (!s_queued.TryAdd(rel, 0))
            return;
        s_pendingAbs.Enqueue(abs);
        Kick();
    }

    public static bool TryGet(string? projectRelative, out Texture2D tex)
    {
        tex = null!;
        string rel = PlanetAssetIO.NormalizeAssetReference(projectRelative ?? "");
        if (string.IsNullOrWhiteSpace(rel))
            return false;
        return s_ready.TryGetValue(rel, out tex!);
    }

    public static bool TryGetAny(out Texture2D tex)
    {
        tex = null!;
        foreach (var kv in s_ready)
        {
            if (kv.Value == null) continue;
            tex = kv.Value;
            return true;
        }
        return false;
    }

    public static string Pick(int token)
    {
        EnsureCatalog();
        lock (s_catalogLock)
        {
            if (s_catalog.Count == 0)
                return "Assets/textures/Grass/Acanthus_01.psd";
            int i = token < 0 ? -token : token;
            string pick = s_catalog[i % s_catalog.Count];
            Request(pick);
            return pick;
        }
    }

    static void Kick()
    {
        if (Interlocked.CompareExchange(ref s_loaderRunning, 1, 0) != 0)
            return;
        Task.Run(LoaderLoop);
    }

    static void LoaderLoop()
    {
        try
        {
            while (s_pendingAbs.TryDequeue(out var abs))
            {
                try
                {
                    if (!File.Exists(abs))
                        continue;
                    var tex = Texture2D.FromFile(abs);
                    if (tex == null || tex.Width <= 0 || tex.Height <= 0)
                        continue;
                    tex = PrepareGrassCard(tex);
                    string rel = PlanetAssetIO.NormalizeAssetReference(abs);
                    if (string.IsNullOrWhiteSpace(rel))
                        rel = abs;
                    s_ready[rel] = tex;
                    Interlocked.Increment(ref s_readyStamp);
                }
                catch { /* skip broken cards */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref s_loaderRunning, 0);
            if (!s_pendingAbs.IsEmpty)
                Kick();
        }
    }

    static Texture2D PrepareGrassCard(Texture2D src)
    {
        var cropped = CropToAlpha(src);
        return Downscale(cropped, 160, 256);
    }

    static Texture2D CropToAlpha(Texture2D src)
    {
        int w = src.Width, h = src.Height;
        var px = src.Rgba;
        int minX = w, minY = h, maxX = 0, maxY = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (px[row + x * 4 + 3] < 16) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX <= minX || maxY <= minY)
            return src;
        int pad = 2;
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad);
        maxY = Math.Min(h - 1, maxY + pad);
        int nw = maxX - minX + 1;
        int nh = maxY - minY + 1;
        if (nw >= w && nh >= h)
            return src;
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            Array.Copy(px, ((minY + y) * w + minX) * 4, dst, y * nw * 4, nw * 4);
        return new Texture2D(nw, nh, dst, src.SourcePath);
    }

    static Texture2D Downscale(Texture2D src, int maxW, int maxH)
    {
        if (src.Width <= maxW && src.Height <= maxH)
            return src;
        float sx = maxW / (float)src.Width;
        float sy = maxH / (float)src.Height;
        float s = Math.Min(sx, sy);
        int nw = Math.Max(1, (int)MathF.Round(src.Width * s));
        int nh = Math.Max(1, (int)MathF.Round(src.Height * s));
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int syi = Math.Min(src.Height - 1, y * src.Height / nh);
            for (int x = 0; x < nw; x++)
            {
                int sxi = Math.Min(src.Width - 1, x * src.Width / nw);
                int si = (syi * src.Width + sxi) * 4;
                int di = (y * nw + x) * 4;
                dst[di] = src.Rgba[si];
                dst[di + 1] = src.Rgba[si + 1];
                dst[di + 2] = src.Rgba[si + 2];
                dst[di + 3] = src.Rgba[si + 3];
            }
        }
        return new Texture2D(nw, nh, dst, src.SourcePath);
    }

    static bool IsGrassImageExt(string? ext)
        => !string.IsNullOrWhiteSpace(ext)
           && (ext.Equals(".psd", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tga", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase));
}
