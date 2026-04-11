#nullable enable
using System.Linq;
using System.Text;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Networking
{
    /// <summary>
    /// Logs terrain and planet asset paths and seeds once per scene while networking is active,
    /// so server and client builds can be compared for shared world data.
    /// </summary>
    public static class NetworkWorldDiagnostics
    {
        /// <summary>
        /// Emit a single log line listing <see cref="Terrain"/>, <see cref="TerrainStreamer"/>, and
        /// <see cref="PlanetTerrain"/> settings relevant to multiplayer consistency.
        /// </summary>
        public static void LogSharedWorldSnapshot()
        {
            var terrains = SceneQuery.FindBehaviors<Terrain>().ToList();
            var streamers = SceneQuery.FindBehaviors<TerrainStreamer>().ToList();
            var planets = SceneQuery.FindBehaviors<PlanetTerrain>().ToList();

            if (terrains.Count == 0 && streamers.Count == 0 && planets.Count == 0)
            {
                Log.Info("[NetworkWorld] No Terrain, TerrainStreamer, or PlanetTerrain in scene — nothing to fingerprint for shared assets.");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[NetworkWorld] Shared world fingerprint (compare server vs client): ");

            if (terrains.Count > 0)
            {
                sb.Append("Terrain[");
                for (int i = 0; i < terrains.Count; i++)
                {
                    var t = terrains[i];
                    var go = t.gameObject;
                    if (i > 0) sb.Append("; ");
                    sb.Append(HierarchyPath(go)).Append(" path=").Append(t.TerrainAssetPath ?? "");
                }
                sb.Append("] ");
            }

            if (streamers.Count > 0)
            {
                sb.Append("TerrainStreamer[");
                for (int i = 0; i < streamers.Count; i++)
                {
                    var s = streamers[i];
                    if (i > 0) sb.Append("; ");
                    sb.Append(HierarchyPath(s.gameObject)).Append(" folder=").Append(s.TilesSubfolder ?? "");
                }
                sb.Append("] ");
            }

            if (planets.Count > 0)
            {
                sb.Append("PlanetTerrain[");
                for (int i = 0; i < planets.Count; i++)
                {
                    var p = planets[i];
                    if (i > 0) sb.Append("; ");
                    sb.Append(HierarchyPath(p.gameObject))
                        .Append(" planet=").Append(p.PlanetAssetPath ?? "")
                        .Append(" biome=").Append(p.BiomeGraphPath ?? "")
                        .Append(" seed=").Append(p.Seed)
                        .Append(" weatherSeed=").Append(p.WeatherSeed);
                }
                sb.Append(']');
            }

            Log.Info(sb.ToString());
        }

        static string HierarchyPath(GameObject? go)
        {
            if (go == null) return "?";
            var parts = new System.Collections.Generic.List<string>();
            for (var n = go; n != null; n = n.Parent)
                parts.Add(n.Name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
