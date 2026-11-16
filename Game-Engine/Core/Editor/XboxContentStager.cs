#if !PLAYER
using ImageMagick;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Game_Engine.Core.Rendering;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Fills the Xbox <c>Data</c> folder: loose assets under <c>Data/Assets</c> and scenes under
/// <c>Data/Scenes</c>. The console reads a package it cannot write to and cannot see the PC's
/// drives, so everything is copied in, textures are compressed here, and absolute paths in
/// the scenes are rewritten to project-relative ones.
/// </summary>
public static class XboxContentStager
{
    public sealed record Summary(
        int ProjectFiles,
        int ExternalFiles,
        int RemappedPaths,
        int TexturesBaked,
        int StaleFilesRemoved,
        IReadOnlyList<string> Unresolved,
        IReadOnlyList<string> Undecodable);

    public static Summary Stage(
        Project proj,
        IReadOnlyList<string> scenePaths,
        string dataDir,
        Action<string>? status = null)
    {
        var collected = BuildAssetCollector.Collect(proj, scenePaths);
        var assetsRoot = Path.GetFullPath(proj.AssetsPath);
        var looseRoot = Path.Combine(dataDir, "Assets");
        Directory.CreateDirectory(looseRoot);

        // The Xbox layout is loose files. A leftover pack from a PC build would win over them.
        var pak = Path.Combine(dataDir, "Assets.dll");
        if (File.Exists(pak)) File.Delete(pak);

        // The editor leaves .dds copies next to images, often BC7 for the PC's GPU. Those are
        // re-made below in a format the console can load, so the PC ones are not copied.
        var pcSidecars = new HashSet<string>(
            collected.Files.Where(IsBakeable).Select(f => Path.GetFullPath(TextureBcCompression.GetSidecarDdsPath(f))),
            StringComparer.OrdinalIgnoreCase);

        var staged = new List<string>(collected.Files.Count + collected.External.Count);
        var materialTextures = new List<string>();
        int total = collected.Files.Count;
        int index = 0;
        int cards = 0;
        status?.Invoke($"Copying {total + collected.External.Count} asset file(s)...");
        foreach (var abs in collected.Files)
        {
            index++;
            if (pcSidecars.Contains(abs)) continue;
            var rel = Path.GetRelativePath(assetsRoot, abs);
            if (IsPhotoshop(abs))
            {
                cards++;
                var dest = Path.ChangeExtension(Path.Combine(looseRoot, rel), ".png");
                if (cards == 1 || cards % 10 == 0)
                    status?.Invoke($"Converting grass card {cards} ({Path.GetFileName(abs)})...");
                if (PngIsCurrent(abs, dest) || WritePhotoshopPng(abs, dest))
                    staged.Add(dest);
                continue;
            }
            if (index % 50 == 0)
                status?.Invoke($"Copying assets {index}/{total}...");
            var copied = Path.Combine(looseRoot, rel);
            CopyReplacing(abs, copied);
            staged.Add(copied);
            // Only material textures are ever loaded compressed. Icons, sky and terrain-layer images are not.
            if (IsBakeable(copied) && collected.MaterialTextures.Contains(abs))
                materialTextures.Add(copied);
        }
        foreach (var (source, relative) in collected.External)
        {
            var dest = Path.Combine(looseRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            CopyReplacing(source, dest);
            staged.Add(dest);
        }

        var undecodable = staged
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".hdr" or ".exr" or ".ktx" or ".ktx2" or ".basis" or ".ogg" or ".flac")
            .Select(p => Path.GetRelativePath(looseRoot, p))
            .ToList();
        foreach (var file in undecodable)
            Log.Warning($"[Build] The Xbox player cannot decode this format; convert it to PNG/JPG/TGA or WAV/MP3: {file}");

        // BC-compress here. The console never encodes: its package is read-only and one 2k encode stalls a frame.
        var toBake = materialTextures.Where(NeedsBake).ToList();
        int baked = 0, done = 0;
        if (toBake.Count > 0)
        {
            status?.Invoke($"Compressing {toBake.Count} texture(s) for Xbox...");
            foreach (var path in toBake)
            {
                if (Bake(path)) baked++;
                done++;
                if (done == 1 || done % 5 == 0 || done == toBake.Count)
                    status?.Invoke($"Compressing texture {done}/{toBake.Count} ({Path.GetFileName(path)})...");
            }
        }

        int removed = Prune(looseRoot, staged, materialTextures);
        RetargetPhotoshopReferences(looseRoot);
        StageScenes(scenePaths, dataDir, collected.PathRemap);

        return new Summary(collected.Files.Count, collected.External.Count, collected.PathRemap.Count,
            baked, removed, collected.Unresolved, undecodable);
    }

    /// <summary>Copies the build scenes, replacing absolute asset paths with the ones the build ships.</summary>
    static void StageScenes(IReadOnlyList<string> scenePaths, string dataDir, IReadOnlyDictionary<string, string> remap)
    {
        var outDir = Path.Combine(dataDir, "Scenes");
        Directory.CreateDirectory(outDir);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Regex? rx = null;
        if (remap.Count > 0)
        {
            // Longest first, so a path that is a prefix of another cannot win.
            var alternatives = remap.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
            rx = new Regex("\"(" + string.Join("|", alternatives) + ")\"", RegexOptions.CultureInvariant);
        }

        foreach (var src in scenePaths)
        {
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(outDir, Path.GetFileName(src));
            kept.Add(Path.GetFullPath(dest));
            MakeWritable(dest);

            if (rx == null)
            {
                using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                continue;
            }

            string text;
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(input, Encoding.UTF8))
                text = reader.ReadToEnd();
            int replaced = 0;
            text = rx.Replace(text, m =>
            {
                replaced++;
                return "\"" + remap[m.Groups[1].Value] + "\"";
            });
            File.WriteAllText(dest, text, new UTF8Encoding(false));
            if (replaced > 0)
                Log.Info($"[Build] {Path.GetFileName(src)}: {replaced} absolute path(s) rewritten for the Xbox package.");
        }

        // A scene removed from the build list must not stay in the package.
        foreach (var old in Directory.EnumerateFiles(outDir, "*.scene"))
        {
            if (kept.Contains(Path.GetFullPath(old))) continue;
            try { MakeWritable(old); File.Delete(old); } catch { /* best-effort */ }
        }
    }

    /// <summary>Deletes files left over from earlier builds. Returns how many were removed.</summary>
    static int Prune(string looseRoot, List<string> staged, List<string> materialTextures)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in staged)
            keep.Add(Path.GetFullPath(path));
        foreach (var path in materialTextures)
            keep.Add(Path.GetFullPath(TextureBcCompression.GetSidecarDdsPath(path)));

        // Written after this step by the font bake.
        var fonts = Path.GetFullPath(Path.Combine(looseRoot, "Standard Assets", "Fonts")) + Path.DirectorySeparatorChar;

        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(looseRoot, "*", SearchOption.AllDirectories).ToList())
        {
            var full = Path.GetFullPath(file);
            if (keep.Contains(full) || full.StartsWith(fonts, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                MakeWritable(full);
                File.Delete(full);
                removed++;
            }
            catch { /* best-effort */ }
        }

        foreach (var dir in Directory.EnumerateDirectories(looseRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length).ToList())
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { /* best-effort */ }
        }
        return removed;
    }

    static bool IsPhotoshop(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".psd" or ".psb";

    /// <summary>
    /// Grass cards and some ground images are Photoshop files. The console has no PSD decoder,
    /// so the build writes a PNG (no larger than 1024 on a side) and the profile is retargeted.
    /// </summary>
    static bool PngIsCurrent(string src, string dest)
    {
        try
        {
            if (!File.Exists(dest)) return false;
            return File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(src) && new FileInfo(dest).Length > 0;
        }
        catch { return false; }
    }

    static bool WritePhotoshopPng(string src, string dest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // One card at a time, and never a multi-hundred-megabyte decode. The previous
            // pass kept every full image and the build stopped mid-texture.
            ResourceLimits.Memory = 256UL * 1024UL * 1024UL;
            using var img = new MagickImage(src);
            img.AutoOrient();
            if (img.Width > 512 || img.Height > 512)
            {
                var geo = new MagickGeometry(512, 512) { Greater = true };
                img.Resize(geo);
            }
            img.Format = MagickFormat.Png32;
            MakeWritable(dest);
            img.Write(dest);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"[Build] Could not convert {Path.GetFileName(src)} to PNG: {ex.Message}");
            return false;
        }
    }

    static void RetargetPhotoshopReferences(string looseRoot)
    {
        if (!Directory.Exists(looseRoot)) return;
        foreach (var file in Directory.EnumerateFiles(looseRoot, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".json" or ".biomegraph" or ".material" or ".planet" or ".prefab" or ".shadergraph"))
                continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.IndexOf(".psd", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf(".psb", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\.ps[db]\b", ".png", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            try
            {
                MakeWritable(file);
                File.WriteAllText(file, text);
            }
            catch (Exception ex)
            {
                Log.Warning($"[Build] Could not retarget Photoshop paths in {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    static bool IsBakeable(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".tif" or ".tiff";

    static bool NeedsBake(string stagedPath)
    {
        if (!IsBakeable(stagedPath)) return false;
        var dds = TextureBcCompression.GetSidecarDdsPath(stagedPath);
        return !File.Exists(dds) || File.GetLastWriteTimeUtc(dds) < File.GetLastWriteTimeUtc(stagedPath);
    }

    static bool Bake(string stagedPath)
    {
        try
        {
            var tex = Texture2D.FromFileUncached(stagedPath);
            // The renderer only asks for a compressed copy above this size (ShouldCompressMaterialSlot).
            if (tex.Width <= 512 && tex.Height <= 512) return false;
            bool alpha = TextureBcCompression.HasMeaningfulAlpha(tex.Rgba, tex.Width, tex.Height);
            // bptc: false. BC7 needs feature level 11; an Xbox One UWP title gets 10.1.
            var format = TextureBcCompression.ChooseFormat(MaterialTexture.TexUsage.Albedo, alpha, bptc: false);
            return TextureBcCompression.TryEnsureSidecarDds(stagedPath, tex.Rgba, tex.Width, tex.Height, format, ignoreRuntimeCaps: true);
        }
        catch (Exception ex)
        {
            Log.Warning($"[Build] Xbox texture compression skipped for {Path.GetFileName(stagedPath)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Overwrite a staged file even when an earlier copy was marked read-only. Asset packs often ship
    /// that way, and <see cref="File.Copy(string, string, bool)"/> then fails with "Access to the path is denied".
    /// </summary>
    static void CopyReplacing(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        MakeWritable(dest);
        File.Copy(src, dest, overwrite: true);
        MakeWritable(dest);
    }

    static void MakeWritable(string path)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }
}
#endif
