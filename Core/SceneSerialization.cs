using Avalonia.Media;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SN = System.Numerics;

namespace Game_Engine.Core
{
    /// <summary>
    /// Scene <-> JSON
    /// - GameObject tree + Transform
    /// - [Persist] properties on Behaviors (+ MeshFilter.Mesh back-compat)
    /// - Color as #AARRGGBB
    /// - Material with multi-texture slots: name, usage, faceMask, path (project-relative) or inline Texture2D
    /// - Texture2D: path preferred; else embedded W/H/RGBA
    /// - Mesh: if a component exposes "ModelPath" (string), Mesh is rebuilt from disk at load and is NOT persisted;
    ///         otherwise we persist geometry/preset just like the legacy format.
    /// </summary>
    public static class SceneSerialization
    {
        // Allow the app to plug in a model loader that returns a Mesh from a file path.
        // Set this once at startup: SceneSerialization.ResolveMeshFromModelPath = path => ...;
        public static Func<string, List<Mesh>>? ResolveMeshesFromModelPath;   // multi-mesh (preferred)
        public static Func<string, Mesh?>? ResolveMeshFromModelPath;     // single-mesh (fallback)

        // ---------------- JSON setup ----------------
        /// <summary>Shared JSON options used for scene serialization (read-only access).</summary>
        public static JsonSerializerOptions JsonOptions => _json;

        static readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            Converters =
            {
                new CoreVector3Converter(),
                new TypeNameHandlingConverter()
            }
        };

        // ---------------- Public API ----------------
        public static void SaveScene(string path, IEnumerable<GameObject> root)
        {
            var dto = new SceneDTO
            {
                Version = 2,
                Root = root.Select(ToDTO).ToList()
            };

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, _json));
        }

        public static List<GameObject> LoadScene(string path)
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<SceneDTO>(json, _json);
            if (dto == null) throw new InvalidDataException("Scene file is empty or invalid.");

            return dto.Root.Select(FromDTO).ToList();

        }

        // ---------------- GameObject/Behavior mapping ----------------
        /// <summary>Children to skip during serialization (generated at runtime, e.g. grass chunks).</summary>
        static bool IsGeneratedChild(GameObject child)
            => child.Name == "Grass" || child.Name.StartsWith("grass_") || child.Name.StartsWith("chunk_");

        /// <summary>Serialize a single GameObject hierarchy to JSON (via the DTO pipeline).
        /// When <paramref name="includeAll"/> is true, no children are filtered (use for prefabs).</summary>
        public static string SerializeGameObjectToJson(GameObject go, bool includeAll = false)
        {
            var dto = includeAll ? ToDTOFull(go) : ToDTO(go);
            return JsonSerializer.Serialize(dto, _json);
        }

        /// <summary>Deserialize a single GameObject hierarchy from JSON (via the DTO pipeline).</summary>
        public static GameObject? DeserializeGameObjectFromJson(string json)
        {
            try
            {
                var dto = JsonSerializer.Deserialize<GameObjectDTO>(json, _json);
                if (dto == null) return null;
                return FromDTO(dto);
            }
            catch { return null; }
        }

        static GameObjectDTO ToDTO(GameObject go)
        {
            var dto = new GameObjectDTO
            {
                Name = go.Name,
                Transform = new TransformDTO
                {
                    LocalPosition = go.Transform.Position,
                    LocalRotationEuler = go.Transform.Rotation,
                    LocalScale = go.Transform.Scale
                },
                Behaviors = go.Behaviors.Where(b => b is not Component.Transform).Select(BehaviorToDTO).ToList(),
                Children = go.Children.Where(c => !IsGeneratedChild(c)).Select(ToDTO).ToList(),
                PrefabId = go.PrefabId,
                PrefabPath = go.PrefabPath
            };
            return dto;
        }

        /// <summary>Same as ToDTO but includes ALL children (no generated-child filter). Used for prefabs.</summary>
        static GameObjectDTO ToDTOFull(GameObject go)
        {
            var dto = new GameObjectDTO
            {
                Name = go.Name,
                Transform = new TransformDTO
                {
                    LocalPosition = go.Transform.Position,
                    LocalRotationEuler = go.Transform.Rotation,
                    LocalScale = go.Transform.Scale
                },
                Behaviors = go.Behaviors.Where(b => b is not Component.Transform).Select(BehaviorToDTO).ToList(),
                Children = go.Children.Select(ToDTOFull).ToList(),
                PrefabId = go.PrefabId,
                PrefabPath = go.PrefabPath
            };
            return dto;
        }

        static GameObject FromDTO(GameObjectDTO dto)
        {
            var go = new GameObject(dto.Name ?? "GameObject");

            if (dto.Transform != null)
            {
                go.Transform.Position = dto.Transform.LocalPosition;
                go.Transform.Rotation = dto.Transform.LocalRotationEuler;
                go.Transform.Scale = dto.Transform.LocalScale;
            }

            go.PrefabId = dto.PrefabId;
            go.PrefabPath = dto.PrefabPath;

            if (dto.Behaviors != null)
                for (int i = 0; i < dto.Behaviors.Count; i++) RestoreBehavior(go, dto.Behaviors[i]);

            if (dto.Children != null)
                for (int i = 0; i < dto.Children.Count; i++) go.AddChild(FromDTO(dto.Children[i]));

            return go;
        }

        static BehaviorDTO BehaviorToDTO(Behavior behavior)
        {
            var type = behavior.GetType();
            var props = GetPersistableProps(type);

            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var p in props)
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.GetIndexParameters().Length > 0) continue;

                var n = p.Name;
                if (n is "Parent" or "Children" or "gameObject" or "Transform") continue;

                object? raw = null;
                try { raw = p.GetValue(behavior); } catch { }
                if (raw == null) { bag[n] = null; continue; }

                //  pass the declaring instance so PersistValue can see ModelPath etc.
                var persisted = PersistValue(p, raw, behavior);
                if (persisted is Skip) continue;
                bag[n] = persisted is KeepNull ? null : persisted;
            }

            return new BehaviorDTO { Type = type.AssemblyQualifiedName, Properties = bag };
        }

        static void RestoreBehavior(GameObject go, BehaviorDTO dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Type)) return;

            var type = Type.GetType(dto.Type, false);
            if (type == null) return;
            if (!typeof(Behavior).IsAssignableFrom(type)) return;
            if (typeof(Component.Transform).IsAssignableFrom(type)) return;

            Behavior instance = null;
            try { instance = Activator.CreateInstance(type) as Behavior; } catch { }
            if (instance == null) return;

            go.AddBehavior(instance);

            // ---------- Apply persisted [Persist] properties ----------
            var props = GetPersistableProps(type).ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
            if (dto.Properties != null)
            {
                foreach (var kv in dto.Properties)
                {
                    if (!props.TryGetValue(kv.Key, out var pi)) continue;
                    try
                    {
                        var converted = ConvertPersisted(kv.Value, pi.PropertyType);
                        pi.SetValue(instance, converted);
                    }
                    catch
                    {
                        // ignore a single bad property to keep the rest loading
                    }
                }
            }

            // ---------- Texture2D post-pass via "*Path" siblings ----------
            {
                var allProps = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < allProps.Length; i++)
                {
                    var texProp = allProps[i];
                    if (texProp.PropertyType != typeof(Texture2D)) continue;
                    if (!texProp.CanRead || !texProp.CanWrite) continue;

                    var pathProp = type.GetProperty(texProp.Name + "Path",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pathProp == null || pathProp.PropertyType != typeof(string) || !pathProp.CanRead) continue;

                    string rel = null;
                    try { rel = (string)pathProp.GetValue(instance); } catch { rel = null; }

                    // fallback to raw dto map if property itself wasn't marked
                    if (string.IsNullOrWhiteSpace(rel) && dto.Properties != null &&
                        dto.Properties.TryGetValue(texProp.Name + "Path", out var raw))
                    {
                        try
                        {
                            if (raw is string sRaw) rel = sRaw;
                            else if (raw is JsonElement je && je.ValueKind == JsonValueKind.String) rel = je.GetString();
                        }
                        catch { rel = null; }
                    }

                    if (string.IsNullOrWhiteSpace(rel)) continue;

                    var abs = TryResolveTextureFile(rel);
                    if (string.IsNullOrWhiteSpace(abs)) abs = ResolveAssetPath(rel);
                    if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs)) continue;

                    try
                    {
                        var texFromFile = Texture2D.FromFile(abs);
                        if (texFromFile != null) texProp.SetValue(instance, texFromFile);
                    }
                    catch { /* keep whatever ConvertPersisted set */ }
                }
            }

            // ---------- MeshFilter post-pass: rebuild from ModelPath (robust, no empty fallback) ----------
            {
                var modelPathPI = type.GetProperty("ModelPath",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var partIndexPI = type.GetProperty("ModelPartIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var meshPI = type.GetProperty("Mesh",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (modelPathPI != null && modelPathPI.PropertyType == typeof(string) &&
                    meshPI != null && meshPI.PropertyType == typeof(Mesh) && meshPI.CanWrite)
                {
                    string relModel = null;
                    try { relModel = (string)modelPathPI.GetValue(instance); } catch { relModel = null; }

                    // If ModelPath didn't come through as [Persist], pull from DTO and push to instance
                    if (string.IsNullOrWhiteSpace(relModel) && dto.Properties != null &&
                        dto.Properties.TryGetValue("ModelPath", out var rawModel))
                    {
                        try
                        {
                            if (rawModel is string sRaw) relModel = sRaw;
                            else if (rawModel is JsonElement jem && jem.ValueKind == JsonValueKind.String) relModel = jem.GetString();
                        }
                        catch { relModel = null; }

                        // Normalize and store back so next save includes it
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(relModel))
                            {
                                // Make project-relative if needed
                                var normalized = MakeAssetRelative(ResolveAssetPath(relModel) ?? relModel);
                                modelPathPI.SetValue(instance, normalized ?? relModel);
                                relModel = normalized ?? relModel;
                            }
                        }
                        catch { }
                    }

                    // Pull ModelPartIndex robustly (handles both int and string property types)
                    int partIdx = 0;
                    if (partIndexPI != null && partIndexPI.CanRead)
                    {
                        try
                        {
                            var raw = partIndexPI.GetValue(instance);
                            if (raw is int ii) partIdx = ii;
                            else if (raw is string ss && int.TryParse(ss, out var pp)) partIdx = pp;
                        }
                        catch { partIdx = 0; }
                    }
                    // Fallback: read from DTO if instance value was 0 and DTO has a non-zero value
                    if (partIdx == 0 && dto.Properties != null && dto.Properties.TryGetValue("ModelPartIndex", out var rawIdx))
                    {
                        try
                        {
                            if (rawIdx is JsonElement je)
                            {
                                if (je.ValueKind == JsonValueKind.Number) partIdx = je.GetInt32();
                                else if (je.ValueKind == JsonValueKind.String)
                                {
                                    int parsed;
                                    if (int.TryParse(je.GetString(), out parsed)) partIdx = parsed;
                                }
                            }
                            else if (rawIdx is int i) partIdx = i;
                            else if (rawIdx is string s)
                            {
                                int parsed;
                                if (int.TryParse(s, out parsed)) partIdx = parsed;
                            }
                        }
                        catch { partIdx = 0; }
                    }

                    if (!string.IsNullOrWhiteSpace(relModel))
                    {
                        // Resolve as stored
                        string absModel = ResolveAssetPath(relModel);
                        bool exists = !string.IsNullOrWhiteSpace(absModel) && File.Exists(absModel);

                        // If missing, try to guess by file name anywhere under Assets and update ModelPath if found
                        if (!exists)
                        {
                            try
                            {
                                var guessedRel = GuessAssetPathByName(Path.GetFileName(relModel));
                                if (!string.IsNullOrWhiteSpace(guessedRel))
                                {
                                    absModel = ResolveAssetPath(guessedRel);
                                    exists = !string.IsNullOrWhiteSpace(absModel) && File.Exists(absModel);
                                    if (exists)
                                    {
                                        try { modelPathPI.SetValue(instance, guessedRel); } catch { }
                                        relModel = guessedRel;
                                    }
                                }
                            }
                            catch { }
                        }

                        // If exists, (multi-mesh preferred) rebuild the mesh
                        if (exists)
                        {
                            try
                            {
                                bool resolved = false;

                                if (ResolveMeshesFromModelPath != null)
                                {
                                    var list = ResolveMeshesFromModelPath(absModel);
                                    if (list != null && list.Count > 0)
                                    {
                                        if (partIdx < 0) partIdx = 0;
                                        if (partIdx >= list.Count) partIdx = list.Count - 1;
                                        var picked = list[partIdx];
                                        if (picked != null) { meshPI.SetValue(instance, picked); resolved = true; }
                                    }
                                }
                                if (!resolved && ResolveMeshFromModelPath != null)
                                {
                                    var m = ResolveMeshFromModelPath(absModel);
                                    if (m != null) { meshPI.SetValue(instance, m); resolved = true; }
                                }

                                // NOTE: If not resolved, we intentionally keep the current mesh (likely the default cube).
                                // We no longer clear to an empty mesh to avoid “mesh removes after restore”.
                            }
                            catch
                            {
                                // keep current mesh on failure
                            }
                        }
                        // else: still missing — do nothing, keep current mesh (no removal).
                    }
                }
            }

            // Allow components to reconcile scene-file data with external assets
            // (e.g., Terrain reloading heights from .terrain.json that were overwritten
            //  by stale [Persist] properties from the scene file).
            try { instance.PostDeserialize(); } catch { }
        }

        



        // ---------- Persist rules ----------
        sealed class Skip { public static readonly Skip Value = new(); }
        sealed class KeepNull { public static readonly KeepNull Value = new(); }

        static object? PersistValue(PropertyInfo p, object? value, object declaringInstance)
        {
            if (value is null) return KeepNull.Value;

            var t = p.PropertyType;

            // Normalize any "*Path"/ModelPath strings to project-relative for stable scenes.
            if (t == typeof(string))
            {
                if (p.Name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Name, "ModelPath", StringComparison.OrdinalIgnoreCase))
                {
                    return MakeAssetRelative((string)value);
                }
                return value;
            }

            // Block engine refs
            if (typeof(GameObject).IsAssignableFrom(t)
             || typeof(Behavior).IsAssignableFrom(t)
             || typeof(Component.Transform).IsAssignableFrom(t))
                return Skip.Value;

            // Simple types
            if (t.IsPrimitive || t.IsEnum ||
                t == typeof(double) || t == typeof(float) || t == typeof(decimal) ||
                t == typeof(Vector3))
                return value;

            // Avalonia.Color -> hex
            if (t == typeof(Color))
                return ColorToHex((Color)value);

            // Material -> DTO
            if (t == typeof(Material))
                return ToDto((Material)value);

            // Texture2D: skip if sibling "<Name>Path" exists; rely on path reload.
            if (t == typeof(Texture2D))
            {
                var decl = p.DeclaringType;
                if (decl != null)
                {
                    var pathProp = decl.GetProperty(p.Name + "Path",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pathProp != null && pathProp.PropertyType == typeof(string))
                        return Skip.Value; // use *Path; don’t embed pixels
                }
                // no sibling path -> embed to round-trip
                return ToDto((Texture2D)value);
            }

            // Mesh:
            //   * If this component has a ModelPath AND it's non-empty on THIS INSTANCE,
            //     we rebuild from disk on load -> SKIP persisting geometry.
            //   * Otherwise (primitives, custom meshes, or empty ModelPath), we DO persist
            //     preset/kind/geometry so primitives keep their shape.
            if (t == typeof(Mesh))
            {
                var decl = p.DeclaringType;
                if (decl != null)
                {
                    var modelPathProp = decl.GetProperty("ModelPath",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (modelPathProp != null && modelPathProp.PropertyType == typeof(string))
                    {
                        try
                        {
                            var mp = (string?)modelPathProp.GetValue(declaringInstance);
                            if (!string.IsNullOrWhiteSpace(mp))
                                return Skip.Value; // rebuild from ModelPath at load
                        }
                        catch
                        {
                            // if reading ModelPath fails, fall through and persist mesh
                        }
                    }
                }
                // No (usable) ModelPath on this instance -> persist mesh data.
                return ToDto((Mesh)value);
            }

            // Arrays / Lists of simple types or Vector3
            if (t.IsArray)
            {
                var et = t.GetElementType()!;
                return IsSimpleOrVec3(et) ? value : Skip.Value;
            }

            if (t.IsGenericType)
            {
                var genDef = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();

                if ((genDef == typeof(List<>) || genDef == typeof(ICollection<>) || genDef == typeof(IEnumerable<>))
                    && args.Length == 1 && IsSimpleOrVec3(args[0]))
                    return value;

                if (genDef == typeof(Dictionary<,>) && args.Length == 2 && args[0] == typeof(string) && IsSimpleOrVec3(args[1]))
                    return value;
            }

            return Skip.Value;
        }


        static bool IsSimpleOrVec3(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) ||
                   t == typeof(double) || t == typeof(float) || t == typeof(decimal) ||
                   t == typeof(Vector3);
        }

        static object? ConvertPersisted(object? jsonValue, Type targetType)
        {
            if (jsonValue == null) return null;

            if (targetType == typeof(Color))
            {
                string? s = null;
                if (jsonValue is string str) s = str;
                else if (jsonValue is JsonElement je && je.ValueKind == JsonValueKind.String) s = je.GetString();
                return HexToColor(s ?? "#FFFFFFFF");
            }

            if (targetType == typeof(Material))
            {
                MaterialDTO? dto = null;
                try
                {
                    if (jsonValue is MaterialDTO dd) dto = dd;
                    else if (jsonValue is JsonElement je) dto = JsonSerializer.Deserialize<MaterialDTO>(je.GetRawText(), _json);
                    else dto = JsonSerializer.Deserialize<MaterialDTO>(JsonSerializer.Serialize(jsonValue, _json), _json);
                }
                catch { }
                return dto == null ? null : FromDto(dto);
            }

            if (targetType == typeof(Texture2D))
            {
                Texture2DDTO? dto = null;
                try
                {
                    if (jsonValue is Texture2DDTO dd) dto = dd;
                    else if (jsonValue is JsonElement je) dto = JsonSerializer.Deserialize<Texture2DDTO>(je.GetRawText(), _json);
                    else dto = JsonSerializer.Deserialize<Texture2DDTO>(JsonSerializer.Serialize(jsonValue, _json), _json);
                }
                catch { }
                return dto == null ? null : FromDto(dto);
            }

            if (targetType == typeof(Mesh))
            {
                MeshDTO? dto = null;
                try
                {
                    if (jsonValue is MeshDTO dd) dto = dd;
                    else if (jsonValue is JsonElement je) dto = JsonSerializer.Deserialize<MeshDTO>(je.GetRawText(), _json);
                    else dto = JsonSerializer.Deserialize<MeshDTO>(JsonSerializer.Serialize(jsonValue, _json), _json);
                }
                catch { }
                return dto == null ? null : FromDto(dto);
            }

            if (targetType == typeof(Vector3))
            {
                if (jsonValue is Vector3 v3) return v3;
                if (jsonValue is JsonElement je && je.ValueKind == JsonValueKind.Array)
                    return JsonSerializer.Deserialize<Vector3>(je.GetRawText(), _json);
            }

            try
            {
                var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (t.IsEnum)
                {
                    if (jsonValue is string es) return Enum.Parse(t, es, true);
                    if (jsonValue is JsonElement je)
                    {
                        if (je.ValueKind == JsonValueKind.String) return Enum.Parse(t, je.GetString()!, true);
                        if (je.ValueKind == JsonValueKind.Number) return Enum.ToObject(t, je.GetInt32());
                    }
                }

                if (jsonValue is JsonElement numJe && numJe.ValueKind == JsonValueKind.Number)
                {
                    if (t == typeof(double)) return numJe.GetDouble();
                    if (t == typeof(float)) return (float)numJe.GetDouble();
                    if (t == typeof(int)) return numJe.GetInt32();
                    if (t == typeof(long)) return numJe.GetInt64();
                    if (t == typeof(decimal)) return numJe.GetDecimal();
                }
                return Convert.ChangeType(jsonValue, t);
            }
            catch
            {
                var json = JsonSerializer.Serialize(jsonValue, _json);
                return JsonSerializer.Deserialize(json, targetType, _json);
            }
        }

        static IEnumerable<PropertyInfo> GetPersistableProps(Type t)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var p in t.GetProperties(flags))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.GetIndexParameters().Length > 0) continue;

                var hasPersist = p.GetCustomAttributes(true).Any(a => a.GetType().Name == "PersistAttribute");
                var hasDoNot = p.GetCustomAttributes(true).Any(a => a.GetType().Name == "DoNotPersistAttribute");
                if (hasDoNot) continue;

                // Normal opt-in
                if (hasPersist)
                {
                    yield return p;
                    continue;
                }

                // ---- Back-compat / safety for MeshFilter ----
                // We always persist these even if not annotated, to preserve old scenes and keep defaults working.
                if (t.FullName == "Game_Engine.Core.Component.MeshFilter")
                {
                    // Geometry (may be skipped later by PersistValue if ModelPath is present)
                    if (p.Name == "Mesh" && p.PropertyType == typeof(Mesh))
                    {
                        yield return p;
                        continue;
                    }

                    // Rebuild hints for multi-part models
                    if (p.Name == "ModelPath" && p.PropertyType == typeof(string))
                    {
                        yield return p;
                        continue;
                    }
                    if (p.Name == "ModelPartIndex" && (p.PropertyType == typeof(int) || p.PropertyType == typeof(string)))
                    {
                        yield return p;
                        continue;
                    }
                }
            }
        }


        // ---------------- Color & paths ----------------
        static string ColorToHex(Color c) => string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);

        static Color HexToColor(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("#")) s = s[1..];
            if (s.Length == 6) s = "FF" + s;
            byte a = byte.Parse(s[..2], System.Globalization.NumberStyles.HexNumber);
            byte r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            byte g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            byte b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
            return Color.FromArgb(a, r, g, b);
        }

        static string? MakeAssetRelative(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var full = Path.GetFullPath(path);
                var proj = ProjectService.Current;
                if (proj == null) return full;
                var root = Path.GetFullPath(proj.RootPath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return Path.GetRelativePath(root, full);
                return full;
            }
            catch { return path; }
        }

        static string? ResolveAssetPath(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                if (Path.IsPathRooted(stored)) return stored;
                var proj = ProjectService.Current;
                if (proj == null) return stored;
                return Path.Combine(proj.RootPath, stored);
            }
            catch { return stored; }
        }

        static string? GuessAssetPathByName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var proj = ProjectService.Current;
            if (proj == null) return null;
            var assets = proj.AssetsPath;
            if (string.IsNullOrWhiteSpace(assets) || !Directory.Exists(assets)) return null;
            try
            {
                var match = Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
                return match != null ? MakeAssetRelative(match) : null;
            }
            catch { return null; }
        }

        static string? TryResolveTextureFile(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;

            var candidates = new List<string?>();
            candidates.Add(ResolveAssetPath(stored));

            if (!Path.IsPathRooted(stored))
            {
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    if (!string.IsNullOrWhiteSpace(proj.AssetsPath))
                        candidates.Add(Path.Combine(proj.AssetsPath, stored));
                    candidates.Add(Path.Combine(proj.RootPath, stored));
                }
            }

            var byName = GuessAssetPathByName(Path.GetFileName(stored));
            if (!string.IsNullOrWhiteSpace(byName))
                candidates.Add(ResolveAssetPath(byName));

            foreach (var c in candidates)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(c) && File.Exists(c))
                        return c;
                }
                catch { }
            }
            return null;
        }

        // ---------------- Texture2D DTO ----------------
        sealed class Texture2DDTO
        {
            public string? path { get; set; } // optional project-relative
            public int w { get; set; }
            public int h { get; set; }
            public string? rgba { get; set; } // base64 raw RGBA
        }

        static Texture2DDTO ToDto(Texture2D tex)
        {
            return new Texture2DDTO
            {
                w = tex.Width,
                h = tex.Height,
                rgba = Convert.ToBase64String(tex.Rgba ?? Array.Empty<byte>())
                // path stays null unless Texture2D starts carrying a source path 
            };
        }

        static Texture2D? FromDto(Texture2DDTO d)
        {
            if (!string.IsNullOrWhiteSpace(d.path))
            {
                var abs = ResolveAssetPath(d.path);
                if (!string.IsNullOrWhiteSpace(abs) && File.Exists(abs))
                {
                    try { return Texture2D.FromFile(abs); } catch { }
                }
            }

            try
            {
                var raw = string.IsNullOrWhiteSpace(d.rgba) ? null : Convert.FromBase64String(d.rgba);
                if (raw == null || raw.Length != d.w * d.h * 4) return null;
                return new Texture2D(d.w, d.h, raw);
            }
            catch { return null; }
        }

        // ---------------- Material DTO (asset-ref) ----------------

        // Allow the app to tell us how to load a .material and how to find a material's asset path when saving.
        public static Func<string, Material> ResolveMaterialFromPath;         // abs path -> Material (runtime)
        public static Func<Material, string> GetMaterialAssetPath;            // Material -> abs or rel path
        private const BindingFlags BF =
             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        sealed class MaterialDTO
        {
            public string? tint { get; set; }           // "#AARRGGBB"
            public float metallic { get; set; }
            public float smoothness { get; set; }
            public bool transparent { get; set; }

            // Path to the .material file (project-relative)
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? texturePath { get; set; }

            // Texture slot paths — only emitted when no .material file is found.
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<MatSlotDTO>? textures { get; set; }
        }


        sealed class MatSlotDTO
        {
            public string? name { get; set; }
            public string? usage { get; set; }
            public int faceMask { get; set; }
            public string? path { get; set; }
            public Texture2DDTO? inline { get; set; }
        }

        static string? FindMaterialAssetPath(Material m)
        {
            // Try common property names on Material (AssetPath/MaterialPath/SourcePath/Path)
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var t = m.GetType();
                foreach (var n in new[] { "AssetPath", "MaterialPath", "SourcePath", "Path" })
                {
                    var p = t.GetProperty(n, BF);
                    if (p != null && p.PropertyType == typeof(string))
                    {
                        var v = p.GetValue(m) as string;
                        if (!string.IsNullOrWhiteSpace(v)) return MakeAssetRelative(ResolveAssetPath(v) ?? v);
                    }
                }
            }
            catch { }

            // Guess by name: <Name>.material anywhere under Assets
            try
            {
                var nm = m.Name;
                if (!string.IsNullOrWhiteSpace(nm))
                {
                    var guess = GuessAssetPathByName(nm.EndsWith(".material", StringComparison.OrdinalIgnoreCase)
                                                     ? nm
                                                     : (nm + ".material"));
                    if (!string.IsNullOrWhiteSpace(guess)) return guess;
                }
            }
            catch { }

            return null;
        }


        static MaterialDTO ToDto(Material m)
        {
            var dto = new MaterialDTO
            {
                // keep these lightweight scalars for convenience/back-compat
                tint = ColorToHex(m.Tint),
                metallic = m.Metallic,
                smoothness = m.Smoothness,
                textures = null
            };

            // Persist the Transparent flag
            dto.transparent = m.Transparent;

            // point to the .material file if we can find it
            var matRel = FindMaterialAssetPath(m);
            if (!string.IsNullOrWhiteSpace(matRel))
                dto.texturePath = matRel;
            else
                dto.texturePath = null;

            // If no .material file, save texture slot paths so they survive save/load
            if (string.IsNullOrWhiteSpace(dto.texturePath) && m.Textures != null && m.Textures.Count > 0)
            {
                var slots = new List<MatSlotDTO>();
                foreach (var raw in m.Textures)
                {
                    string? path = null;
                    string? usage = null;
                    int faceMask = -1;

                    if (raw is RuntimeTexSlot rts)
                    {
                        path = rts.SourcePath;
                        usage = rts.Usage;
                        faceMask = rts.FaceMask;
                    }
                    else if (raw is MaterialTexture mtex)
                    {
                        path = mtex.SourcePath;
                        usage = mtex.Usage.ToString();
                        faceMask = (int)mtex.FaceMask;
                    }

                    if (!string.IsNullOrWhiteSpace(path))
                        slots.Add(new MatSlotDTO { usage = usage, path = path, faceMask = faceMask });
                }
                if (slots.Count > 0) dto.textures = slots;
            }

            return dto;
        }


        static Material FromDto(MaterialDTO d)
        {
            // Try to load from the saved .material path (source of truth)
            if (!string.IsNullOrWhiteSpace(d.texturePath))
            {
                try
                {
                    var abs = ResolveAssetPath(d.texturePath) ?? d.texturePath;
                    if (!string.IsNullOrWhiteSpace(abs) && File.Exists(abs))
                    {
                        // Host-provided loader first (recommended; prints your MatTrace logs, etc.)
                        if (ResolveMaterialFromPath != null)
                        {
                            try
                            {
                                var loaded = ResolveMaterialFromPath(abs);
                                if (loaded != null) return loaded;
                            }
                            catch { /* fall through */ }
                        }

                        // Try MaterialRuntimeBuilder.* via reflection (Load/FromFile/Build/TryLoad)
                        try
                        {
                            var asm = typeof(Material).Assembly;
                            var t = asm.GetType("Game_Engine.Core.MaterialRuntimeBuilder")
                                 ?? asm.GetTypes().FirstOrDefault(tt =>
                                        string.Equals(tt.Name, "MaterialRuntimeBuilder", StringComparison.Ordinal));

                            if (t != null)
                            {
                                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                                // Prefer a (string)->Material
                                var mLoad = t.GetMethod("Load", flags, null, new[] { typeof(string) }, null)
                                         ?? t.GetMethod("FromFile", flags, null, new[] { typeof(string) }, null);

                                if (mLoad != null)
                                {
                                    var res = mLoad.Invoke(null, new object[] { abs });
                                    if (res is Material mRet) return mRet;
                                }

                                // Accept a (Material,string) builder that fills in-place
                                var mBuild = t.GetMethod("Build", flags, null, new[] { typeof(Material), typeof(string) }, null)
                                           ?? t.GetMethod("TryLoad", flags, null, new[] { typeof(Material), typeof(string) }, null);
                                if (mBuild != null)
                                {
                                    var baseMat = new Material();
                                    mBuild.Invoke(null, new object[] { baseMat, abs });
                                    return baseMat;
                                }
                            }
                        }
                        catch { /* fall through */ }

                        // Minimal JSON fallback — recognizes your current .material schema.
                        try
                        {
                            return MinimalLoadMaterialAsset(abs);
                        }
                        catch { /* fall through */ }
                    }
                }
                catch { /* fall through */ }
            }

            // Scalar-only fallback (if no path or load failed)
            var col = string.IsNullOrWhiteSpace(d.tint) ? Colors.White : HexToColor(d.tint);
            var mat = new Material { Metallic = d.metallic, Smoothness = d.smoothness, Transparent = d.transparent };
            try
            {
                var pBase = mat.GetType().GetProperty("BaseColor", BF);
                if (pBase != null && pBase.PropertyType == typeof(Color)) pBase.SetValue(mat, col, null);
                else
                {
                    var pTint = mat.GetType().GetProperty("Tint", BF);
                    if (pTint != null && pTint.PropertyType == typeof(Color)) pTint.SetValue(mat, col, null);
                }
            }
            catch { }

            // Reload texture slots from saved paths (when no .material file).
            //    Always add the slot even when the texture file can't be loaded right now —
            //    preserving SourcePath lets MaterialRebind retry on later frames.
            if (d.textures != null)
            {
                foreach (var slotDto in d.textures)
                {
                    if (string.IsNullOrWhiteSpace(slotDto.path)) continue;

                    Texture2D t2 = null;
                    try
                    {
                        var absT = ResolveAssetPath(slotDto.path) ?? slotDto.path;
                        if (!string.IsNullOrWhiteSpace(absT) && File.Exists(absT))
                            t2 = Texture2D.FromFile(absT);
                    }
                    catch { /* deferred retry via MaterialRebind */ }

                    mat.Textures.Add(new RuntimeTexSlot
                    {
                        Texture = t2,
                        Usage = slotDto.usage ?? "Albedo",
                        FaceMask = slotDto.faceMask,
                        SourcePath = slotDto.path
                    });
                }
            }

            return mat;

            // ---- local helpers (C# 7.3-friendly) ---------------------------------
            Material MinimalLoadMaterialAsset(string absPath)
            {
                var json = File.ReadAllText(absPath);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var m = new Material();

                    // param.{Tint | Tint(hex) | Metallic | Roughness | Transparent | AlphaCutoff}
                    JsonElement param;
                    if (root.TryGetProperty("param", out param) && param.ValueKind == JsonValueKind.Object)
                    {
                        string tintHex = null;
                        JsonElement je;
                        if (param.TryGetProperty("Tint", out je) && je.ValueKind == JsonValueKind.String) tintHex = je.GetString();
                        else if (param.TryGetProperty("Tint(hex)", out je) && je.ValueKind == JsonValueKind.String) tintHex = je.GetString();

                        if (!string.IsNullOrWhiteSpace(tintHex))
                        {
                            try
                            {
                                var c = HexToColor(tintHex);
                                var pBase = m.GetType().GetProperty("BaseColor", BF);
                                if (pBase != null && pBase.PropertyType == typeof(Color)) pBase.SetValue(m, c, null);
                            }
                            catch { }
                        }

                        if (param.TryGetProperty("Metallic", out je) && je.ValueKind == JsonValueKind.Number)
                            m.Metallic = (float)je.GetDouble();
                        if (param.TryGetProperty("Roughness", out je) && je.ValueKind == JsonValueKind.Number)
                            m.Roughness = (float)je.GetDouble();
                        if (param.TryGetProperty("Transparent", out je) && je.ValueKind == JsonValueKind.True)
                            m.Transparent = true;
                        if (param.TryGetProperty("AlphaCutoff", out je) && je.ValueKind == JsonValueKind.Number)
                            m.AlphaCutoff = (float)je.GetDouble();
                    }

                    // textures (flat) or textures.obj { Albedo/Roughness/Metallic/AmbientOcclusion/Emissive/Opacity/Normal/Specular: "path" }
                    JsonElement tex;
                    if ((root.TryGetProperty("textures", out tex) || root.TryGetProperty("Textures", out tex)) && tex.ValueKind == JsonValueKind.Object)
                    {
                        var obj = tex;
                        JsonElement inner;
                        if (tex.TryGetProperty("obj", out inner) && inner.ValueKind == JsonValueKind.Object) obj = inner;

                        foreach (var kv in obj.EnumerateObject())
                        {
                            if (kv.Value.ValueKind != JsonValueKind.String) continue;
                            var rel = kv.Value.GetString();
                            var absT = ResolveAssetPath(rel) ?? rel;
                            if (string.IsNullOrWhiteSpace(absT) || !File.Exists(absT)) continue;

                            Texture2D t2 = null;
                            try { t2 = Texture2D.FromFile(absT); } catch { }
                            if (t2 == null) continue;

                            m.Textures.Add(new RuntimeTexSlot
                            {
                                Texture = t2,
                                Usage = kv.Name,
                                FaceMask = -1,
                                SourcePath = rel   // preserve for scene re-serialization
                            });
                        }
                    }

                    if (string.IsNullOrWhiteSpace(m.Name))
                        m.Name = Path.GetFileNameWithoutExtension(absPath);

                    return m;
                }
            }
        }



        // ---------------- Mesh DTO ----------------
        sealed class MeshDTO
        {
            // metadata (for readability)
            public string? preset { get; set; }   // "Cube" | "Quad" | "Plane"
            public string? kind { get; set; }     // "Generic" | "Sphere" | "Cylinder" | "Cone"
            public int tessA { get; set; }
            public int tessB { get; set; }

            // geometry (present for generic/explicit meshes)
            public float[]? v { get; set; }   // x,y,z,...
            public float[]? n { get; set; }   // x,y,z,...
            public int[]? tri { get; set; }
            public int[]? line { get; set; }
        }

        struct Prim
        {
            public Prim(string name, Func<Mesh> f) { Name = name; Factory = f; }
            public string Name;
            public Func<Mesh> Factory;
        }

        static readonly Prim[] _prims = new Prim[]
        {
            new Prim("Cube",     () => Mesh.CreateCube(1f)),
            new Prim("Quad",     () => Mesh.CreateQuad(1f, 1f)),
            new Prim("Plane",    () => Mesh.CreatePlane(2f, 2f, 16, 16)),
            new Prim("Sphere",   () => Mesh.CreateUvSphere(24, 16, 0.5f)),
            new Prim("Cylinder", () => Mesh.CreateCylinder(24, 0.5f, 1f, true)),
            new Prim("Cone",     () => Mesh.CreateCone(24, 0.5f, 1f, true)),
        };

        static string? RecognizePreset(Mesh m)
        {
            for (int i = 0; i < _prims.Length; i++)
            {
                var s = _prims[i].Factory();
                if (s == null) continue;
                bool match = s.Vertices.Length == m.Vertices.Length
                             && s.TriIndices.Length == m.TriIndices.Length
                             && s.LineIndices.Length == m.LineIndices.Length;
                if (match && (_prims[i].Name == "Cube" || _prims[i].Name == "Quad" || _prims[i].Name == "Plane"))
                    return _prims[i].Name;
            }
            return null;
        }

        static MeshDTO ToDto(Mesh m)
        {
            var preset = RecognizePreset(m);
            if (!string.IsNullOrWhiteSpace(preset))
            {
                return new MeshDTO
                {
                    preset = preset,
                    kind = MeshKind.Generic.ToString()
                };
            }

            if (m.Kind != MeshKind.Generic)
            {
                return new MeshDTO
                {
                    kind = m.Kind.ToString(),
                    tessA = m.TessA,
                    tessB = m.TessB
                };
            }

            var flatV = new float[m.Vertices.Length * 3];
            for (int i = 0, j = 0; i < m.Vertices.Length; i++)
            {
                var p = m.Vertices[i];
                flatV[j++] = p.X; flatV[j++] = p.Y; flatV[j++] = p.Z;
            }

            float[]? flatN = null;
            if (m.Normals != null && m.Normals.Length > 0)
            {
                flatN = new float[m.Normals.Length * 3];
                for (int i = 0, j = 0; i < m.Normals.Length; i++)
                {
                    var n = m.Normals[i];
                    flatN[j++] = n.X; flatN[j++] = n.Y; flatN[j++] = n.Z;
                }
            }

            return new MeshDTO
            {
                kind = MeshKind.Generic.ToString(),
                v = flatV,
                n = flatN,
                tri = m.TriIndices ?? Array.Empty<int>(),
                line = m.LineIndices ?? Array.Empty<int>()
            };
        }

        static Mesh FromDto(MeshDTO d)
        {
            if (!string.IsNullOrWhiteSpace(d.preset))
            {
                switch (d.preset)
                {
                    case "Cube": return Mesh.CreateCube(1f);
                    case "Quad": return Mesh.CreateQuad(1f, 1f);
                    case "Plane": return Mesh.CreatePlane(2f, 2f, 16, 16);
                }
            }

            if (!string.IsNullOrWhiteSpace(d.kind) &&
                Enum.TryParse<MeshKind>(d.kind, true, out var mk) &&
                mk != MeshKind.Generic)
            {
                switch (mk)
                {
                    case MeshKind.Sphere: return Mesh.CreateUvSphere(Math.Max(3, d.tessA), Math.Max(2, d.tessB), 0.5f);
                    case MeshKind.Cylinder: return Mesh.CreateCylinder(Math.Max(3, d.tessA), 0.5f, 1f, true);
                    case MeshKind.Cone: return Mesh.CreateCone(Math.Max(3, d.tessA), 0.5f, 1f, true);
                }
            }

            if (d.v == null || d.tri == null)
                throw new InvalidDataException("Mesh DTO missing vertices or triangles.");

            var verts = new SN.Vector3[d.v.Length / 3];
            for (int i = 0, j = 0; i < verts.Length; i++)
                verts[i] = new SN.Vector3(d.v[j++], d.v[j++], d.v[j++]);

            SN.Vector3[]? norms = null;
            if (d.n != null && d.n.Length > 0)
            {
                norms = new SN.Vector3[d.n.Length / 3];
                for (int i = 0, j = 0; i < norms.Length; i++)
                    norms[i] = new SN.Vector3(d.n[j++], d.n[j++], d.n[j++]);
            }

            var mesh = new Mesh(verts, d.line ?? Array.Empty<int>(), d.tri)
            {
                Kind = MeshKind.Generic,
                Normals = norms
            };
            return mesh;
        }
    }

    // ---------------- Converters & root DTOs ----------------
    public sealed class CoreVector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Vector3 must be [x,y,z].");

            reader.Read(); double x = reader.GetDouble();
            reader.Read(); double y = reader.GetDouble();
            reader.Read(); double z = reader.GetDouble();
            reader.Read(); // EndArray
            return new Vector3(x, y, z);
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }

    public sealed class TypeNameHandlingConverter : JsonConverter<Type>
    {
        public override Type? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var name = reader.GetString();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Type.GetType(name, false);
        }

        public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.AssemblyQualifiedName);
        }
    }

    public class SceneDTO
    {
        public int Version { get; set; }
        public List<GameObjectDTO> Root { get; set; } = new();
    }

    public class GameObjectDTO
    {
        public string? Name { get; set; }
        public TransformDTO? Transform { get; set; }
        public List<BehaviorDTO>? Behaviors { get; set; }
        public List<GameObjectDTO>? Children { get; set; }
        public string? PrefabId { get; set; }
        public string? PrefabPath { get; set; }
    }

    public class TransformDTO
    {
        public Vector3 LocalPosition { get; set; }
        public Vector3 LocalRotationEuler { get; set; }
        public Vector3 LocalScale { get; set; }
    }

    public class BehaviorDTO
    {
        public string? Type { get; set; }
        public Dictionary<string, object?>? Properties { get; set; }
    }
}