using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using SN = System.Numerics;
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    /// <summary>
    /// Scene <-> JSON
    /// - GameObject tree with explicit Transform (Position/Rotation/Scale)
    /// - Behaviors via [Persist] properties
    /// - Avalonia.Color as #AARRGGBB
    /// - Material (tint/metallic/smoothness + first texture path, project-relative)
    /// - Mesh with primitive detection that mirrors the PropertyEditor list:
    ///     preset: Cube | Quad | Plane
    ///     kind+tess: Sphere | Cylinder | Cone  (MeshKind)
    ///     or full geometry snapshot
    /// </summary>
    public static class SceneSerialization
    {
        static readonly JsonSerializerOptions _json = new()
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

        // ---------- Public API ----------

        public static void SaveScene(string path, IEnumerable<GameObject> root)
        {
            var dto = new SceneDTO
            {
                Version = 1,
                Root = root.Select(ToDTO).ToList()
            };
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, _json));
        }

        public static List<GameObject> LoadScene(string path)
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<SceneDTO>(json, _json)
                      ?? throw new InvalidDataException("Scene file is empty or invalid.");
            return dto.Root.Select(FromDTO).ToList();
        }

        // ---------- Mapping ----------

        static GameObjectDTO ToDTO(GameObject go) => new()
        {
            Name = go.Name,
            Transform = new TransformDTO
            {
                LocalPosition = go.Transform.Position,
                LocalRotationEuler = go.Transform.Rotation,
                LocalScale = go.Transform.Scale
            },
            Behaviors = go.Behaviors.Where(b => b is not Component.Transform).Select(BehaviorToDTO).ToList(),
            Children = go.Children.Select(ToDTO).ToList()
        };

        static BehaviorDTO BehaviorToDTO(Behavior behavior)
        {
            var type = behavior.GetType();
            var props = GetPersistableProps(type);

            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var p in props)
            {
                var n = p.Name;

                // avoid graph links
                if (n is "Parent" or "Children" or "gameObject" or "Transform")
                    continue;

                object? raw;
                try { raw = p.GetValue(behavior); }
                catch { continue; }

                var persisted = PersistValue(p, raw);
                if (persisted is Skip) continue;
                if (persisted is KeepNull) { bag[n] = null; continue; }
                if (persisted is not null) bag[n] = persisted;
            }

            return new BehaviorDTO
            {
                Type = type.AssemblyQualifiedName!,
                Properties = bag
            };
        }

        static GameObject FromDTO(GameObjectDTO dto)
        {
            var go = new GameObject(dto.Name ?? "GameObject");

            // Transform
            if (dto.Transform is not null)
            {
                go.Transform.Position = dto.Transform.LocalPosition;
                go.Transform.Rotation = dto.Transform.LocalRotationEuler;
                go.Transform.Scale = dto.Transform.LocalScale;
            }

            // Behaviors (non-Transform)
            if (dto.Behaviors != null)
            {
                foreach (var b in dto.Behaviors)
                    RestoreBehavior(go, b);
            }

            // Children
            foreach (var childDTO in dto.Children ?? Enumerable.Empty<GameObjectDTO>())
                go.AddChild(FromDTO(childDTO));

            return go;
        }

        static void RestoreBehavior(GameObject go, BehaviorDTO dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Type)) return;

            var type = Type.GetType(dto.Type, throwOnError: false);
            if (type == null) return;
            if (!typeof(Behavior).IsAssignableFrom(type)) return;
            if (typeof(Component.Transform).IsAssignableFrom(type)) return; // GO already has Transform

            Behavior? instance = null;
            try { instance = Activator.CreateInstance(type) as Behavior; } catch { }
            if (instance == null) return;

            go.AddBehavior(instance); // sets gameObject + OnEnable()

            var props = GetPersistableProps(type).ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

            foreach (var (key, jsonVal) in dto.Properties ?? Enumerable.Empty<KeyValuePair<string, object?>>())
            {
                if (!props.TryGetValue(key, out var pi)) continue;
                try
                {
                    var converted = ConvertPersisted(jsonVal, pi.PropertyType);
                    pi.SetValue(instance, converted);
                }
                catch { /* ignore and continue */ }
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

            // Material -> compact DTO (relative path)
            if (t == typeof(Material))
                return ToDto((Material)value);

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
            => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(Vector3);

        static object? ConvertPersisted(object? jsonValue, Type targetType)
        {
            if (jsonValue is null) return null;

            // Color
            if (targetType == typeof(Color))
            {
                if (jsonValue is string s) return HexToColor(s);
                if (jsonValue is JsonElement je && je.ValueKind == JsonValueKind.String)
                    return HexToColor(je.GetString() ?? "#FFFFFFFF");
            }

            // Material
            if (targetType == typeof(Material))
            {
                MaterialDTO? dto = null;

                if (jsonValue is MaterialDTO mdto) dto = mdto;
                else if (jsonValue is JsonElement je) { try { dto = JsonSerializer.Deserialize<MaterialDTO>(je.GetRawText(), _json); } catch { } }
                else { try { dto = JsonSerializer.Deserialize<MaterialDTO>(JsonSerializer.Serialize(jsonValue, _json), _json); } catch { } }

                return dto is null ? null : FromDto(dto);
            }

            // Mesh
            if (targetType == typeof(Mesh))
            {
                MeshDTO? dto = null;

                if (jsonValue is MeshDTO mdto) dto = mdto;
                else if (jsonValue is JsonElement je) { try { dto = JsonSerializer.Deserialize<MeshDTO>(je.GetRawText(), _json); } catch { } }
                else { try { dto = JsonSerializer.Deserialize<MeshDTO>(JsonSerializer.Serialize(jsonValue, _json), _json); } catch { } }

                return dto is null ? null : FromDto(dto);
            }

            // Vector3
            if (targetType == typeof(Vector3))
            {
                if (jsonValue is Vector3 v) return v;
                if (jsonValue is JsonElement je && je.ValueKind == JsonValueKind.Array)
                    return JsonSerializer.Deserialize<Vector3>(je.GetRawText(), _json);
            }

            // primitives/strings/enums
            try
            {
                var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (t.IsEnum)
                {
                    if (jsonValue is string es) return Enum.Parse(t, es, true);
                    if (jsonValue is JsonElement ee)
                    {
                        if (ee.ValueKind == JsonValueKind.String) return Enum.Parse(t, ee.GetString()!, true);
                        if (ee.ValueKind == JsonValueKind.Number) return Enum.ToObject(t, ee.GetInt32());
                    }
                }
                if (jsonValue is JsonElement num && num.ValueKind == JsonValueKind.Number)
                {
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

        // ---------- Reflection helpers ----------

        static IEnumerable<PropertyInfo> GetPersistableProps(Type t)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var p in t.GetProperties(flags))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.GetIndexParameters().Length > 0) continue;

                var hasPersist = p.GetCustomAttributes(true).Any(a => a.GetType().Name is "PersistAttribute");
                var hasDoNot = p.GetCustomAttributes(true).Any(a => a.GetType().Name is "DoNotPersistAttribute");

                if (hasDoNot) continue;
                if (hasPersist) yield return p;
            }
        }

        // ---------- Color helpers ----------

        static string ColorToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        static Color HexToColor(string s)
        {
            s = s.Trim();
            if (s.StartsWith("#")) s = s[1..];
            if (s.Length == 6) s = "FF" + s;
            byte a = byte.Parse(s[..2], System.Globalization.NumberStyles.HexNumber);
            byte r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            byte g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            byte b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
            return Color.FromArgb(a, r, g, b);
        }

        // ---------- Project-relative asset paths ----------

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
            if (!Directory.Exists(assets)) return null;
            try
            {
                var match = Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
                return match is null ? null : MakeAssetRelative(match);
            }
            catch { return null; }
        }

        // ---------- Material DTO & mapping ----------

        sealed class MaterialDTO
        {
            public string? tint { get; set; }      // "#AARRGGBB"
            public float metallic { get; set; }
            public float smoothness { get; set; }
            public string? texturePath { get; set; } // relative to project
        }

        static MaterialDTO ToDto(Material m)
        {
            // take first texture slot
            string? path = m.Textures.FirstOrDefault()?.SourcePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                var name = m.Textures.FirstOrDefault()?.Name;
                var guessed = GuessAssetPathByName(name);
                if (!string.IsNullOrWhiteSpace(guessed)) path = ResolveAssetPath(guessed);
            }

            return new MaterialDTO
            {
                tint = ColorToHex(m.Tint),
                metallic = m.Metallic,
                smoothness = m.Smoothness,
                texturePath = MakeAssetRelative(path)
            };
        }

        static Material FromDto(MaterialDTO d)
        {
            var mat = new Material
            {
                Tint = string.IsNullOrWhiteSpace(d.tint) ? Colors.White : HexToColor(d.tint!),
                Metallic = d.metallic,
                Smoothness = d.smoothness,
            };

            var abs = ResolveAssetPath(d.texturePath);
            if (!string.IsNullOrWhiteSpace(abs))
            {
                var tex = new MaterialTexture
                {
                    Name = Path.GetFileName(abs),
                    SourcePath = d.texturePath // keep relative
                };
                try { tex.Texture = Texture2D.FromFile(abs); } catch { }
                mat.Textures.Add(tex);
            }

            return mat;
        }

        // ---------- Mesh DTO & mapping (PropertyEditor preset + MeshKind) ----------

        sealed class MeshDTO
        {
            // If preset is known (Cube/Quad/Plane) we only store this.
            public string? preset { get; set; }   // "None" | "Cube" | "Quad" | "Plane"

            // If kind is procedural (Sphere/Cylinder/Cone), we store kind + tess.
            public string? kind { get; set; }     // "Sphere" | "Cylinder" | "Cone" | "Generic"
            public int tessA { get; set; }        // e.g., lon or sides
            public int tessB { get; set; }        // e.g., lat for sphere

            // Otherwise, a full geometry snapshot.
            public float[]? v { get; set; }       // x,y,z,...
            public float[]? n { get; set; }       // x,y,z,...
            public int[]? tri { get; set; }
            public int[]? line { get; set; }
        }

        // Small private entry to mirror your PropertyEditor primitives
        readonly struct Prim
        {
            public Prim(string name, Func<Mesh?> factory) { Name = name; Factory = factory; }
            public string Name { get; }
            public Func<Mesh?> Factory { get; }
        }

        static readonly Prim[] _primitiveCatalog =
        {
            new("Cube",     () => Mesh.CreateCube(1f)),
            new("Quad",     () => Mesh.CreateQuad(1f, 1f)),
            new("Plane",    () => Mesh.CreatePlane(2f, 2f, 16, 16)),
            // The following exist as MeshKind too; kept here so "preset" names match the inspector if defaults were used.
            new("Sphere",   () => Mesh.CreateUvSphere(24, 16, 0.5f)),
            new("Cylinder", () => Mesh.CreateCylinder(24, 0.5f, 1f, true)),
            new("Cone",     () => Mesh.CreateCone(24, 0.5f, 1f, true)),
        };

        static string? RecognizePreset(Mesh m)
        {
            // We only try to label Cube/Quad/Plane as presets; spheres/cylinders/cones use MeshKind below
            foreach (var prim in _primitiveCatalog)
            {
                var sample = prim.Factory();
                if (sample is null) continue;

                // quick signature: verts + tris (+ lines to reduce collisions)
                bool match = sample.Vertices.Length == m.Vertices.Length
                          && sample.TriIndices.Length == m.TriIndices.Length
                          && sample.LineIndices.Length == m.LineIndices.Length;

                if (match && (prim.Name == "Cube" || prim.Name == "Quad" || prim.Name == "Plane"))
                    return prim.Name;
            }
            return null;
        }

        static MeshDTO ToDto(Mesh m)
        {
            // Prefer MeshKind for procedural primitives with tessellation
            if (m.Kind != MeshKind.Generic)
            {
                return new MeshDTO
                {
                    kind = m.Kind.ToString(),
                    tessA = m.TessA,
                    tessB = m.TessB
                };
            }

            // Try to recognize Cube/Quad/Plane by comparing to the same factories the PropertyEditor uses
            var preset = RecognizePreset(m);
            if (!string.IsNullOrWhiteSpace(preset))
                return new MeshDTO { preset = preset };

            // Otherwise persist full geometry
            var flatV = new float[m.Vertices.Length * 3];
            for (int i = 0, j = 0; i < m.Vertices.Length; i++)
            {
                var p = m.Vertices[i];
                flatV[j++] = p.X; flatV[j++] = p.Y; flatV[j++] = p.Z;
            }

            float[]? flatN = null;
            if (m.Normals is { Length: > 0 })
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
                tri = m.TriIndices,
                line = m.LineIndices
            };
        }

        static Mesh FromDto(MeshDTO d)
        {
            // 1) Preset from PropertyEditor (Cube/Quad/Plane)
            if (!string.IsNullOrWhiteSpace(d.preset))
            {
                return d.preset switch
                {
                    "Cube" => Mesh.CreateCube(1f),
                    "Quad" => Mesh.CreateQuad(1f, 1f),
                    "Plane" => Mesh.CreatePlane(2f, 2f, 16, 16),
                    // if someone saved "Sphere" etc. as preset (unlikely), fall through to kind
                    _ => FromDto(new MeshDTO { kind = d.preset, tessA = d.tessA, tessB = d.tessB })
                };
            }

            // 2) MeshKind + tessellation (Sphere/Cylinder/Cone)
            if (!string.IsNullOrWhiteSpace(d.kind) &&
                Enum.TryParse<MeshKind>(d.kind, true, out var kind) &&
                kind != MeshKind.Generic)
            {
                return kind switch
                {
                    MeshKind.Sphere => Mesh.CreateUvSphere(Math.Max(3, d.tessA), Math.Max(2, d.tessB), 0.5f),
                    MeshKind.Cylinder => Mesh.CreateCylinder(Math.Max(3, d.tessA), 0.5f, 1f, caps: true),
                    MeshKind.Cone => Mesh.CreateCone(Math.Max(3, d.tessA), 0.5f, 1f, cap: true),
                    _ => throw new InvalidDataException($"Unsupported MeshKind '{d.kind}'.")
                };
            }

            // 3) Full geometry snapshot
            if (d.v is null || d.tri is null)
                throw new InvalidDataException("Mesh DTO missing vertices or triangles.");

            var verts = new SN.Vector3[d.v.Length / 3];
            for (int i = 0, j = 0; i < verts.Length; i++)
                verts[i] = new SN.Vector3(d.v[j++], d.v[j++], d.v[j++]);

            SN.Vector3[]? norms = null;
            if (d.n is { Length: > 0 })
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

    // ---------- Converters & root DTOs ----------

    public sealed class CoreVector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Vector3 must be [x,y,z].");

            reader.Read(); var x = reader.GetDouble();
            reader.Read(); var y = reader.GetDouble();
            reader.Read(); var z = reader.GetDouble();
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
            return Type.GetType(name, throwOnError: false);
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
