#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace Game_Engine.Core
{
    /// <summary>
    /// Finds the project copy of an asset whose stored path does not exist on this machine.
    /// Scenes saved from another checkout hold absolute paths such as
    /// <c>C:/Users/.../OtherCheckout/Assets/Terrain/Land.terrain.json</c>. A built player
    /// only has the files under its own Assets folder, so those paths must be mapped back.
    /// </summary>
    public static class AssetPathRemap
    {
        static readonly object s_lock = new();
        static Dictionary<string, List<string>>? s_byName;
        static string? s_indexRoot;
        static DateTime s_indexBuiltUtc;

        /// <summary>
        /// The existing file for <paramref name="stored"/>: as given, relative to the project,
        /// or the same asset found under the project Assets folder. Null when nothing matches.
        /// </summary>
        public static string? FindExisting(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                string p = stored.Trim();
                var proj = ProjectService.Current;
                if (Path.IsPathRooted(p))
                {
                    if (File.Exists(p)) return Path.GetFullPath(p);
                }
                else if (proj != null)
                {
                    var fromRoot = Path.GetFullPath(Path.Combine(proj.RootPath, p));
                    if (File.Exists(fromRoot)) return fromRoot;
                    var fromAssets = Path.GetFullPath(Path.Combine(proj.AssetsPath, p));
                    if (File.Exists(fromAssets)) return fromAssets;
                }

                return proj == null ? null : FindUnderAssets(p, proj.AssetsPath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Maps a foreign path into <paramref name="assetsRoot"/>. Tries the part after the last
        /// <c>Assets</c> folder first, then the file name, preferring the hit whose parent folders
        /// match the stored path best.
        /// </summary>
        public static string? FindUnderAssets(string stored, string assetsRoot)
        {
            if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(assetsRoot)) return null;
            if (!Directory.Exists(assetsRoot)) return null;

            var parts = stored.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            for (int i = parts.Length - 2; i >= 0; i--)
            {
                if (!parts[i].Equals("Assets", StringComparison.OrdinalIgnoreCase)) continue;
                var tail = Path.Combine(assetsRoot, Path.Combine(parts[(i + 1)..]));
                if (File.Exists(tail)) return Path.GetFullPath(tail);
                break;
            }

            string name = parts[^1];
            var hits = Lookup(assetsRoot, name);
            if (hits == null || hits.Count == 0) return null;
            if (hits.Count == 1) return hits[0];

            string? best = null;
            int bestScore = -1;
            foreach (var hit in hits)
            {
                var hp = hit.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                int score = 0;
                while (score < hp.Length - 1 && score < parts.Length - 1
                       && hp[hp.Length - 2 - score].Equals(parts[parts.Length - 2 - score], StringComparison.OrdinalIgnoreCase))
                    score++;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hit;
                }
            }
            return best;
        }

        static List<string>? Lookup(string assetsRoot, string fileName)
        {
            lock (s_lock)
            {
                bool sameRoot = s_byName != null
                    && string.Equals(s_indexRoot, assetsRoot, StringComparison.OrdinalIgnoreCase);
                if (sameRoot && s_byName!.TryGetValue(fileName, out var cached))
                    return cached;

                // A miss may mean the file was added after the index was built (editor). Rebuild, but not per lookup.
                if (sameRoot && (DateTime.UtcNow - s_indexBuiltUtc).TotalSeconds < 10)
                    return null;

                var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
                {
                    var key = Path.GetFileName(file);
                    if (!index.TryGetValue(key, out var list))
                        index[key] = list = new List<string>(1);
                    list.Add(file);
                }
                s_byName = index;
                s_indexRoot = assetsRoot;
                s_indexBuiltUtc = DateTime.UtcNow;
                return index.TryGetValue(fileName, out var found) ? found : null;
            }
        }
    }
}
