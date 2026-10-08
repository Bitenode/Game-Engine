#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using CoreVector3 = Game_Engine.Core.Vector3;

namespace Game_Engine.Core.Blueprint
{
    /// <summary> Exec is white; data pins use editor colors.</summary>
    public enum BlueprintPinType
    {
        Exec = 0,
        Bool = 1,
        Int = 2,
        Float = 3,
        String = 4,
        Vector = 5,
        Object = 6,
    }

    public enum BlueprintPinDirection
    {
        Input = 0,
        Output = 1,
    }

    public enum BlueprintWireKind
    {
        Exec = 0,
        Data = 1,
    }

    /// <summary>Catalog / dynamic pin description.</summary>
    public sealed class BlueprintPinDef
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public BlueprintPinType Type { get; init; }
        public BlueprintPinDirection Direction { get; init; }
        public string DefaultLiteral { get; init; } = "";
    }

    /// <summary>Typed variable declared on a blueprint document.</summary>
    public sealed class BlueprintVariableDecl
    {
        public string Name { get; set; } = "NewVar";
        public BlueprintPinType Type { get; set; } = BlueprintPinType.Float;
        public string DefaultLiteral { get; set; } = "0";
        public bool InstanceEditable { get; set; } = true;
    }

    /// <summary>Function signature + nested graph (Event Graph is the root document graph).</summary>
    public sealed class BlueprintFunctionDecl
    {
        public string Name { get; set; } = "NewFunction";
        public List<BlueprintPinDef> Inputs { get; set; } = new();
        public List<BlueprintPinDef> Outputs { get; set; } = new();
        public BlueprintGraph Graph { get; set; } = new();
    }

    /// <summary>Runtime typed value for data pins and variables.</summary>
    public readonly struct BlueprintValue
    {
        public BlueprintPinType Type { get; }
        public bool BoolValue { get; }
        public long IntValue { get; }
        public double FloatValue { get; }
        public string StringValue { get; }
        public CoreVector3 VectorValue { get; }
        public GameObject? ObjectValue { get; }

        BlueprintValue(BlueprintPinType type, bool b, long i, double f, string s, CoreVector3 v, GameObject? o)
        {
            Type = type;
            BoolValue = b;
            IntValue = i;
            FloatValue = f;
            StringValue = s;
            VectorValue = v;
            ObjectValue = o;
        }

        static CoreVector3 ZeroVec() => new(0, 0, 0);

        public static BlueprintValue FromBool(bool v) =>
            new(BlueprintPinType.Bool, v, 0, 0, "", ZeroVec(), null);

        public static BlueprintValue FromInt(long v) =>
            new(BlueprintPinType.Int, false, v, 0, "", ZeroVec(), null);

        public static BlueprintValue FromFloat(double v) =>
            new(BlueprintPinType.Float, false, 0, v, "", ZeroVec(), null);

        public static BlueprintValue FromString(string? v) =>
            new(BlueprintPinType.String, false, 0, 0, v ?? "", ZeroVec(), null);

        public static BlueprintValue FromVector(CoreVector3? v) =>
            new(BlueprintPinType.Vector, false, 0, 0, "", v ?? ZeroVec(), null);

        public static BlueprintValue FromObject(GameObject? v) =>
            new(BlueprintPinType.Object, false, 0, 0, "", ZeroVec(), v);

        public static BlueprintValue DefaultFor(BlueprintPinType type) => type switch
        {
            BlueprintPinType.Bool => FromBool(false),
            BlueprintPinType.Int => FromInt(0),
            BlueprintPinType.Float => FromFloat(0),
            BlueprintPinType.String => FromString(""),
            BlueprintPinType.Vector => FromVector(new CoreVector3(0, 0, 0)),
            BlueprintPinType.Object => FromObject(null),
            _ => FromString(""),
        };

        public static bool TryParse(BlueprintPinType type, string? literal, out BlueprintValue value)
        {
            value = DefaultFor(type);
            var s = literal?.Trim() ?? "";
            switch (type)
            {
                case BlueprintPinType.Bool:
                    if (bool.TryParse(s, out var b)) { value = FromBool(b); return true; }
                    if (s == "1") { value = FromBool(true); return true; }
                    if (s == "0" || s.Length == 0) { value = FromBool(false); return true; }
                    value = FromBool(s.Length > 0);
                    return true;
                case BlueprintPinType.Int:
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var li))
                    {
                        value = FromInt(li);
                        return true;
                    }
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var di))
                    {
                        value = FromInt((long)di);
                        return true;
                    }
                    return false;
                case BlueprintPinType.Float:
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var df))
                    {
                        value = FromFloat(df);
                        return true;
                    }
                    return false;
                case BlueprintPinType.String:
                    value = FromString(literal ?? "");
                    return true;
                case BlueprintPinType.Vector:
                {
                    if (TryParseVector(s, out var vec))
                    {
                        value = FromVector(vec);
                        return true;
                    }
                    return false;
                }
                case BlueprintPinType.Object:
                    if (string.IsNullOrEmpty(s) || string.Equals(s, "self", StringComparison.OrdinalIgnoreCase))
                    {
                        value = FromObject(null); // resolved to self by caller when needed
                        return true;
                    }
                    var byPath = SceneQuery.FindByPath(s);
                    if (byPath != null) { value = FromObject(byPath); return true; }
                    var byName = SceneQuery.FindByName(s);
                    value = FromObject(byName);
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryParseVector(string s, out CoreVector3 v)
        {
            v = new CoreVector3(0, 0, 0);
            if (string.IsNullOrWhiteSpace(s)) return true;
            var parts = s.Replace(';', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
            v = new CoreVector3(x, y, z);
            return true;
        }

        public string ToLiteral() => Type switch
        {
            BlueprintPinType.Bool => BoolValue ? "true" : "false",
            BlueprintPinType.Int => IntValue.ToString(CultureInfo.InvariantCulture),
            BlueprintPinType.Float => FloatValue.ToString(CultureInfo.InvariantCulture),
            BlueprintPinType.String => StringValue,
            BlueprintPinType.Vector =>
                $"{VectorValue.X.ToString(CultureInfo.InvariantCulture)},{VectorValue.Y.ToString(CultureInfo.InvariantCulture)},{VectorValue.Z.ToString(CultureInfo.InvariantCulture)}",
            BlueprintPinType.Object => ObjectValue?.Name ?? "",
            _ => "",
        };

        public bool AsBool() => Type switch
        {
            BlueprintPinType.Bool => BoolValue,
            BlueprintPinType.Int => IntValue != 0,
            BlueprintPinType.Float => Math.Abs(FloatValue) > 1e-12,
            BlueprintPinType.String => !string.IsNullOrEmpty(StringValue)
                                      && !string.Equals(StringValue, "false", StringComparison.OrdinalIgnoreCase)
                                      && StringValue != "0",
            BlueprintPinType.Object => ObjectValue != null,
            _ => false,
        };

        public double AsFloat() => Type switch
        {
            BlueprintPinType.Float => FloatValue,
            BlueprintPinType.Int => IntValue,
            BlueprintPinType.Bool => BoolValue ? 1.0 : 0.0,
            BlueprintPinType.String => double.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
            _ => 0,
        };

        public long AsInt() => Type switch
        {
            BlueprintPinType.Int => IntValue,
            BlueprintPinType.Float => (long)FloatValue,
            BlueprintPinType.Bool => BoolValue ? 1 : 0,
            BlueprintPinType.String => long.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (long)AsFloat(),
            _ => 0,
        };

        public string AsString() => Type switch
        {
            BlueprintPinType.String => StringValue,
            BlueprintPinType.Object => ObjectValue?.Name ?? "",
            _ => ToLiteral(),
        };

        public CoreVector3 AsVector() => Type == BlueprintPinType.Vector
            ? (VectorValue ?? ZeroVec())
            : new CoreVector3(AsFloat(), 0, 0);

        public GameObject? AsObject(GameObject? selfFallback = null) =>
            Type == BlueprintPinType.Object ? (ObjectValue ?? selfFallback) : selfFallback;

        /// <summary>True if <paramref name="from"/> can wire into <paramref name="to"/> (int→float allowed).</summary>
        public static bool AreTypesCompatible(BlueprintPinType from, BlueprintPinType to)
        {
            if (from == to) return true;
            if (from == BlueprintPinType.Int && to == BlueprintPinType.Float) return true;
            if (from == BlueprintPinType.Exec || to == BlueprintPinType.Exec) return from == to;
            return false;
        }

        public BlueprintValue CoerceTo(BlueprintPinType target)
        {
            if (Type == target) return this;
            return target switch
            {
                BlueprintPinType.Bool => FromBool(AsBool()),
                BlueprintPinType.Int => FromInt(AsInt()),
                BlueprintPinType.Float => FromFloat(AsFloat()),
                BlueprintPinType.String => FromString(AsString()),
                BlueprintPinType.Vector => FromVector(AsVector()),
                BlueprintPinType.Object => FromObject(ObjectValue),
                _ => this,
            };
        }
    }

    /// <summary>Editor colors matching conventions.</summary>
    public static class BlueprintPinColors
    {
        public static (byte R, byte G, byte B) For(BlueprintPinType t) => t switch
        {
            BlueprintPinType.Exec => (0xE8, 0xE8, 0xE8),
            BlueprintPinType.Bool => (0xE0, 0x40, 0x40),
            BlueprintPinType.Int => (0x3A, 0xC0, 0xD0),
            BlueprintPinType.Float => (0x5A, 0xC8, 0x5A),
            BlueprintPinType.String => (0xD0, 0x50, 0xC0),
            BlueprintPinType.Vector => (0xD0, 0xC0, 0x40),
            BlueprintPinType.Object => (0x40, 0x80, 0xE0),
            _ => (0xA0, 0xA0, 0xA0),
        };
    }

    /// <summary>Typed variable store for a running Visual Blueprint instance.</summary>
    public sealed class BlueprintTypedStore
    {
        readonly Dictionary<string, BlueprintValue> _values = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, BlueprintPinType> _types = new(StringComparer.OrdinalIgnoreCase);

        public void Clear()
        {
            _values.Clear();
            _types.Clear();
        }

        public void Declare(BlueprintVariableDecl decl)
        {
            if (string.IsNullOrWhiteSpace(decl.Name)) return;
            _types[decl.Name] = decl.Type;
            if (!_values.ContainsKey(decl.Name))
            {
                BlueprintValue.TryParse(decl.Type, decl.DefaultLiteral, out var v);
                _values[decl.Name] = v;
            }
        }

        public void SeedFromDecls(IEnumerable<BlueprintVariableDecl>? decls)
        {
            Clear();
            if (decls == null) return;
            foreach (var d in decls)
                Declare(d);
        }

        public bool TryGet(string name, out BlueprintValue value) => _values.TryGetValue(name, out value);

        public void Set(string name, BlueprintValue value)
        {
            if (_types.TryGetValue(name, out var t))
                _values[name] = value.CoerceTo(t);
            else
            {
                _types[name] = value.Type;
                _values[name] = value;
            }
        }

        public BlueprintPinType GetTypeOr(string name, BlueprintPinType fallback) =>
            _types.TryGetValue(name, out var t) ? t : fallback;

        public IReadOnlyDictionary<string, BlueprintValue> Snapshot => _values;
    }
}
