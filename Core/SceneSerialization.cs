using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using SN = System.Numerics;

namespace Game_Engine.Core
{
    /// <summary>
    /// Scene <-> JSON
    /// - GameObject tree + Transform
    /// - [Persist] properties on Behaviors
    /// - Color as #AARRGGBB
    /// - Material with multi-texture slots: name, usage, faceMask, path (project-relative) or inline Texture2D
    /// - Texture2D (path preferred; else embedded W/H/RGBA)
    /// - Mesh ALWAYS includes full geometry (v/n/tri/line) + also writes preset/kind for readability
    /// - Back-compat: 'texturePath' (single slot) still read
    /// </summary>
    public static class SceneSerialization
    {
        // ---------------- JSON setup ----------------
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
        static GameObjectDTO ToDTO(GameObject go)
        {
            var dto = new GameObjectDTO();
            dto.Name = go.Name;
            dto.Transform = new TransformDTO
            {
                LocalPosition = go.Transform.Position,
                LocalRotationEuler = go.Transform.Rotation,
                LocalScale = go.Transform.Scale
            };
            dto.Behaviors = go.Behaviors.Where(b => !(b is Component.Transform)).Select(BehaviorToDTO).ToList();
            dto.Children = go.Children.Select(ToDTO).ToList();
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

            var bag = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var p in props)
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.GetIndexParameters().Length > 0) continue;

                var n = p.Name;
                if (n == "Parent" || n == "Children" || n == "gameObject" || n == "Transform") continue;

                object raw = null;
                try { raw = p.GetValue(behavior); } catch { }
                if (raw == null) { bag[n] = null; continue; }

                var persisted = PersistValue(p, raw);
                if (persisted is Skip) continue;
                bag[n] = persisted is KeepNull ? (object)null : persisted;
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

            // 1) Set all [Persist] properties from JSON (your original logic)
            var props = GetPersistableProps(type).ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
            if (dto.Properties != null)
            {
                foreach (var kv in dto.Properties)
                {
                    PropertyInfo pi;
                    if (!props.TryGetValue(kv.Key, out pi)) continue;
                    try
                    {
                        var converted = ConvertPersisted(kv.Value, pi.PropertyType);
                        pi.SetValue(instance, converted);
                    }
                    catch { /* ignore bad values so other properties can load */ }
                }
            }

            //    Post-pass: for each Texture2D property, if there is a sibling "<Name>Path" string,
            //    try to load the texture from disk using the project-relative path.
            //    This enables Skybox.Texture + Skybox.TexturePath and any other "*Path" convention.
            var allProps = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < allProps.Length; i++)
            {
                var texProp = allProps[i];
                if (texProp.PropertyType != typeof(Texture2D)) continue;
                if (!texProp.CanRead || !texProp.CanWrite) continue;

                // Look for sibling "<TexturePropName>Path"
                var pathProp = type.GetProperty(texProp.Name + "Path",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pathProp == null || pathProp.PropertyType != typeof(string) || !pathProp.CanRead) continue;

                string rel = null;
                try { rel = (string)pathProp.GetValue(instance); } catch { rel = null; }
                if (string.IsNullOrWhiteSpace(rel))
                {
                    // In rare cases if the path property wasn't marked [Persist] but exists,
                    // try to read it directly from the incoming DTO map.
                    object raw;
                    if (dto.Properties != null && dto.Properties.TryGetValue(texProp.Name + "Path", out raw))
                    {
                        try
                        {
                            if (raw is string) rel = (string)raw;
                            else if (raw is JsonElement)
                            {
                                var je = (JsonElement)raw;
                                if (je.ValueKind == JsonValueKind.String) rel = je.GetString();
                            }
                        }
                        catch { rel = null; }
                    }
                }

                if (string.IsNullOrWhiteSpace(rel)) continue;

                var abs = ResolveAssetPath(rel);
                if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs)) continue;

                try
                {
                    var texFromFile = Texture2D.FromFile(abs);
                    if (texFromFile != null)
                    {
                        // Prefer file-backed texture so future saves write a clean path.
                        texProp.SetValue(instance, texFromFile);
                    }
                }
                catch
                {
                    // If loading fails, keep whatever value was already set by ConvertPersisted.
                }
            }
        }


        // ---------- Persist rules ----------
        sealed class Skip { public static readonly Skip Value = new(); }
        sealed class KeepNull { public static readonly KeepNull Value = new(); }

        static object? PersistValue(PropertyInfo p, object? value)
        {
            if (value is null) return KeepNull.Value;

            var t = p.PropertyType;

            // Block engine refs
            if (typeof(GameObject).IsAssignableFrom(t)
             || typeof(Behavior).IsAssignableFrom(t)
             || typeof(Component.Transform).IsAssignableFrom(t))
                return Skip.Value;

            // Simple types
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) ||
                t == typeof(double) || t == typeof(float) || t == typeof(decimal) ||
                t == typeof(Vector3))
                return value;

            // Avalonia.Color -> hex
            if (t == typeof(Color))
                return ColorToHex((Color)value);

            // Material -> DTO
            if (t == typeof(Material))
                return ToDto((Material)value);

            //  Texture2D -> SKIP if this property has a sibling "<Name>Path" string on the same behavior.
            //    This keeps Skybox nice (TexturePath is saved; Texture object is rebuilt from the path on load).
            if (t == typeof(Texture2D))
            {
                var decl = p.DeclaringType;
                if (decl != null)
                {
                    var pathProp = decl.GetProperty(p.Name + "Path",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pathProp != null && pathProp.PropertyType == typeof(string))
                        return Skip.Value; // rely on *Path; don't inline w/h/rgba
                }
                // if no sibling path property exists (other components), fall back to embedding so it still round-trips
                return ToDto((Texture2D)value);
            }

            // Mesh -> preset/kind/geometry
            if (t == typeof(Mesh))
                return ToDto((Mesh)value);

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

        static object ConvertPersisted(object jsonValue, Type targetType)
        {
            if (jsonValue == null) return null;

            if (targetType == typeof(Color))
            {
                string s = null;
                var je = jsonValue as JsonElement?;
                if (jsonValue is string) s = (string)jsonValue;
                else if (je.HasValue && je.Value.ValueKind == JsonValueKind.String) s = je.Value.GetString();
                return HexToColor(s ?? "#FFFFFFFF");
            }

            if (targetType == typeof(Material))
            {
                MaterialDTO dto = null;
                var je = jsonValue as JsonElement?;
                try
                {
                    if (jsonValue is MaterialDTO) dto = (MaterialDTO)jsonValue;
                    else if (je.HasValue) dto = JsonSerializer.Deserialize<MaterialDTO>(je.Value.GetRawText(), _json);
                    else dto = JsonSerializer.Deserialize<MaterialDTO>(JsonSerializer.Serialize(jsonValue, _json), _json);
                }
                catch { }
                return dto == null ? null : FromDto(dto);
            }

            if (targetType == typeof(Texture2D))
            {
                Texture2DDTO dto = null;
                var je = jsonValue as JsonElement?;
                try
                {
                    if (jsonValue is Texture2DDTO) dto = (Texture2DDTO)jsonValue;
                    else if (je.HasValue) dto = JsonSerializer.Deserialize<Texture2DDTO>(je.Value.GetRawText(), _json);
                    else dto = JsonSerializer.Deserialize<Texture2DDTO>(JsonSerializer.Serialize(jsonValue, _json), _json);
                }
                catch { }
                return dto == null ? null : FromDto(dto);
            }

            if (targetType == typeof(Mesh))
            {
                MeshDTO dto = null;
                var je = jsonValue as JsonElement?;
                try
                {
                    if (jsonValue is MeshDTO) dto = (MeshDTO)jsonValue;
                    else if (je.HasValue) dto = JsonSerializer.Deserialize<MeshDTO>(je.Value.GetRawText(), _json);
                    else dto = JsonSerializer.Deserialize<MeshDTO>(JsonSerializer.Serialize(jsonValue, _json), _json);
                }
                catch { }
                return dto == null ? null : FromDto(dto);
            }

            if (targetType == typeof(Vector3))
            {
                if (jsonValue is Vector3) return (Vector3)jsonValue;
                var je = jsonValue as JsonElement?;
                if (je.HasValue && je.Value.ValueKind == JsonValueKind.Array)
                    return JsonSerializer.Deserialize<Vector3>(je.Value.GetRawText(), _json);
            }

            try
            {
                var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (t.IsEnum)
                {
                    var je = jsonValue as JsonElement?;
                    if (jsonValue is string) return Enum.Parse(t, (string)jsonValue, true);
                    if (je.HasValue)
                    {
                        if (je.Value.ValueKind == JsonValueKind.String) return Enum.Parse(t, je.Value.GetString(), true);
                        if (je.Value.ValueKind == JsonValueKind.Number) return Enum.ToObject(t, je.Value.GetInt32());
                    }
                }

                var je2 = jsonValue as JsonElement?;
                if (je2.HasValue && je2.Value.ValueKind == JsonValueKind.Number)
                {
                    var num = je2.Value;
                    if (t == typeof(double)) return num.GetDouble();
                    if (t == typeof(float)) return (float)num.GetDouble();
                    if (t == typeof(int)) return num.GetInt32();
                    if (t == typeof(long)) return num.GetInt64();
                    if (t == typeof(decimal)) return num.GetDecimal();
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
                if (hasPersist) yield return p;
            }
        }

        // ---------------- Color & paths ----------------
        static string ColorToHex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B); }

        static Color HexToColor(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 6) s = "FF" + s;
            byte a = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            byte r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            byte g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            byte b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
            return Color.FromArgb(a, r, g, b);
        }

        static string MakeAssetRelative(string path)
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

        static string ResolveAssetPath(string stored)
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

        static string GuessAssetPathByName(string fileName)
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

        static string TryResolveTextureFile(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;

            var candidates = new List<string?>();
            // Normal project resolution
            candidates.Add(ResolveAssetPath(stored));

            // If it’s relative, also try Assets/ and Root/ directly
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

            //Fallback: search by file name in Assets tree
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
                catch { /* ignore */ }
            }
            return null;
        }


        // ---------------- Texture2D DTO ----------------
        sealed class Texture2DDTO
        {
            public string path { get; set; } // optional project-relative
            public int w { get; set; }
            public int h { get; set; }
            public string rgba { get; set; } // base64 raw RGBA
        }

        static Texture2DDTO ToDto(Texture2D tex)
        {
            var dto = new Texture2DDTO();
            dto.w = tex.Width;
            dto.h = tex.Height;
            dto.rgba = Convert.ToBase64String(tex.Rgba ?? new byte[0]);
            // dto.path stays null unless you later store a path on Texture2D
            return dto;
        }

        static Texture2D FromDto(Texture2DDTO d)
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

        // ---------------- Material DTO (multi-texture) ----------------
        sealed class MaterialDTO
        {
            public string tint { get; set; }      // "#AARRGGBB"
            public float metallic { get; set; }
            public float smoothness { get; set; }
            public List<MatSlotDTO> textures { get; set; }

            // legacy single-path for old scenes
            public string texturePath { get; set; }
        }

        sealed class MatSlotDTO
        {
            public string name { get; set; }
            public string usage { get; set; }     // enum name
            public int faceMask { get; set; }     // -1 or bitmask
            public string path { get; set; }      // project-relative
            public Texture2DDTO inline { get; set; } // if no path
        }

        static MaterialDTO ToDto(Material m)
        {
            var dto = new MaterialDTO();
            dto.tint = ColorToHex(m.Tint);
            dto.metallic = m.Metallic;
            dto.smoothness = m.Smoothness;

            var list = new List<MatSlotDTO>();

            for (int i = 0; i < m.Textures.Count; i++)
            {
                var t = m.Textures[i];
                var slot = new MatSlotDTO();
                slot.name = t.Name;
                slot.usage = t.Usage.ToString();
                slot.faceMask = (int)t.FaceMask;

                string rel = null;
                if (!string.IsNullOrWhiteSpace(t.SourcePath)) rel = t.SourcePath;
                else if (!string.IsNullOrWhiteSpace(t.Name)) rel = GuessAssetPathByName(Path.GetFileName(t.Name));

                if (!string.IsNullOrWhiteSpace(rel))
                {
                    var abs = ResolveAssetPath(rel);
                    slot.path = MakeAssetRelative(abs ?? rel);
                }
                else if (t.Texture != null)
                {
                    slot.inline = ToDto(t.Texture);
                }

                list.Add(slot);
            }

            dto.textures = list;
            dto.texturePath = (list.Count > 0) ? list[0].path : null; // legacy filler
            return dto;
        }

        static Material FromDto(MaterialDTO d)
        {
            var mat = new Material
            {
                Tint = string.IsNullOrWhiteSpace(d.tint) ? Colors.White : HexToColor(d.tint),
                Metallic = d.metallic,
                Smoothness = d.smoothness
            };

            // Prefer multi-slot list; if missing, synthesize from legacy texturePath.
            var slots = d.textures;
            if ((slots == null || slots.Count == 0) && !string.IsNullOrWhiteSpace(d.texturePath))
            {
                slots = new List<MatSlotDTO>
        {
            new MatSlotDTO
            {
                name = Path.GetFileName(d.texturePath),
                usage = "Albedo",
                faceMask = -1,
                path = d.texturePath
            }
        };
            }

            if (slots != null)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];

                    var texSlot = new MaterialTexture
                    {
                        Name = s.name
                    };

                    // Usage (fallback to Albedo if unknown)
                    if (!Enum.TryParse<MaterialTexture.TexUsage>(s.usage ?? "", true, out var usage))
                        usage = MaterialTexture.TexUsage.Albedo;
                    texSlot.Usage = usage;

                    // Face mask
                    try { texSlot.FaceMask = (MaterialTexture.CubeFaceMask)s.faceMask; }
                    catch { texSlot.FaceMask = MaterialTexture.CubeFaceMask.All; }

                    // Load texture:
                    // 1) If a path was saved, resolve robustly (root, assets, by-name fallback).
                    // 2) Else, use inline RGBA payload if present.
                    if (!string.IsNullOrWhiteSpace(s.path))
                    {
                        texSlot.SourcePath = s.path; // keep what was in the scene file

                        var file = TryResolveTextureFile(s.path);
                        if (!string.IsNullOrWhiteSpace(file))
                        {
                            try
                            {
                                texSlot.Texture = Texture2D.FromFile(file);     // runtime uses this
                                texSlot.SourcePath = MakeAssetRelative(file);    // normalize for next save
                            }
                            catch { /* leave Texture null if load failed */ }
                        }
                    }
                    else if (s.inline != null)
                    {
                        texSlot.Texture = FromDto(s.inline);
                        texSlot.SourcePath = null;
                    }

                    mat.Textures.Add(texSlot);
                }
            }

            return mat;
        }


        // ---------------- Mesh DTO ----------------
        sealed class MeshDTO
        {
            // metadata (for readability)
            public string? preset { get; set; }   // "Cube" | "Quad" | "Plane"
            public string? kind { get; set; }   // "Generic" | "Sphere" | "Cylinder" | "Cone"
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

        static string RecognizePreset(Mesh m)
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
            //  If it's one of our recognized presets, write both preset and "Generic" kind
            var preset = RecognizePreset(m);
            if (!string.IsNullOrWhiteSpace(preset))
            {
                return new MeshDTO
                {
                    preset = preset,
                    kind = MeshKind.Generic.ToString()
                    // (no geometry needed here)
                };
            }

            // If it's a procedural kind with tessellation, write kind + tess A/B
            if (m.Kind != MeshKind.Generic)
            {
                return new MeshDTO
                {
                    kind = m.Kind.ToString(),
                    tessA = m.TessA,
                    tessB = m.TessB
                    // (no geometry needed here)
                };
            }

            // Otherwise, write full geometry and explicitly mark kind as Generic
            var flatV = new float[m.Vertices.Length * 3];
            for (int i = 0, j = 0; i < m.Vertices.Length; i++)
            {
                var p = m.Vertices[i];
                flatV[j++] = p.X; flatV[j++] = p.Y; flatV[j++] = p.Z;
            }

            float[] flatN = null;
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
            // If a preset is present, prefer it (Cube/Quad/Plane). We allowed "Generic" kind alongside preset.
            if (!string.IsNullOrWhiteSpace(d.preset))
            {
                switch (d.preset)
                {
                    case "Cube": return Mesh.CreateCube(1f);
                    case "Quad": return Mesh.CreateQuad(1f, 1f);
                    case "Plane": return Mesh.CreatePlane(2f, 2f, 16, 16);
                }
                // If someone saved "Sphere"/etc in preset by mistake, fall through to B with kind.
            }

            // Procedural kinds (Sphere/Cylinder/Cone) with tessellation numbers
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

            // Full geometry (explicit Generic or missing kind)
            if (d.v == null || d.tri == null)
                throw new InvalidDataException("Mesh DTO missing vertices or triangles.");

            var verts = new SN.Vector3[d.v.Length / 3];
            for (int i = 0, j = 0; i < verts.Length; i++)
                verts[i] = new SN.Vector3(d.v[j++], d.v[j++], d.v[j++]);

            SN.Vector3[] norms = null;
            if (d.n != null && d.n.Length > 0)
            {
                norms = new SN.Vector3[d.n.Length / 3];
                for (int i = 0, j = 0; i < norms.Length; i++)
                    norms[i] = new SN.Vector3(d.n[j++], d.n[j++], d.n[j++]);
            }

            var mesh = new Mesh(verts, d.line ?? Array.Empty<int>(), d.tri)
            {
                Kind = MeshKind.Generic, // init-only -> use initializer
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
        public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
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
        public List<GameObjectDTO> Root { get; set; } = new List<GameObjectDTO>();
    }

    public class GameObjectDTO
    {
        public string Name { get; set; }
        public TransformDTO Transform { get; set; }
        public List<BehaviorDTO> Behaviors { get; set; }
        public List<GameObjectDTO> Children { get; set; }
    }

    public class TransformDTO
    {
        public Vector3 LocalPosition { get; set; }
        public Vector3 LocalRotationEuler { get; set; }
        public Vector3 LocalScale { get; set; }
    }

    public class BehaviorDTO
    {
        public string Type { get; set; }
        public Dictionary<string, object> Properties { get; set; }
    }
}
