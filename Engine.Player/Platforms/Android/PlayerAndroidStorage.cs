#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using Android.App;

namespace Game_Engine;

/// <summary>
/// Extracts packaged Data.zip from Android assets to app storage and sets <see cref="App.BuildJsonPath"/>.
/// </summary>
public static class PlayerAndroidStorage
{
    const string DataZipAsset = "Data.zip";

    public static void EnsureDataFromAssets()
    {
        var ctx = global::Android.App.Application.Context;
        var filesDir = ctx.FilesDir;
        if (filesDir == null) return;

        var extractRoot = Path.Combine(filesDir.AbsolutePath!, "player_bundle");
        var buildJson = Path.Combine(extractRoot, "build.json");

        if (File.Exists(buildJson))
        {
            global::Game_Engine.App.BuildJsonPath = buildJson;
            return;
        }

        if (Directory.Exists(extractRoot))
        {
            try { Directory.Delete(extractRoot, recursive: true); }
            catch { /* best-effort */ }
        }
        Directory.CreateDirectory(extractRoot);

        using var input = ctx.Assets!.Open(DataZipAsset);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;
            var destPath = Path.Combine(extractRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            using var es = entry.Open();
            using var fs = File.Create(destPath);
            es.CopyTo(fs);
        }

        if (File.Exists(buildJson))
            global::Game_Engine.App.BuildJsonPath = buildJson;
    }
}
