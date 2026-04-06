#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using CoreVector3 = Game_Engine.Core.Vector3;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>
    /// Reflection helpers for blueprint nodes: public instance fields/properties on scene objects,
    /// and public static fields/properties on types loaded from Game_Engine assemblies.
    /// </summary>
    public static class BlueprintReflection
    {
        const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        const BindingFlags Stat = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase | BindingFlags.FlattenHierarchy;

        static readonly Dictionary<string, Type?> TypeCache = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsStaticMode(BlueprintNode node) =>
            string.Equals(
                node.Properties.TryGetValue("mode", out var m) ? m.Trim() : "Instance",
                "Static",
                StringComparison.OrdinalIgnoreCase);

        public static GameObject? ResolveScopeGameObject(VisualBlueprintBehavior host, BlueprintNode node)
        {
            var scope = node.Properties.TryGetValue("scope", out var sc) ? sc.Trim() : "Self";
            if (string.Equals(scope, "Self", StringComparison.OrdinalIgnoreCase))
                return host.gameObject;
            return BlueprintFlowRuntime.ResolveTargetObject(node);
        }

        /// <summary>Resolves GameObject, Transform, or first matching <see cref="Behavior"/> on the object.</summary>
        public static object? ResolveMemberRoot(GameObject? go, string componentTypeName)
        {
            if (go == null || string.IsNullOrWhiteSpace(componentTypeName)) return null;
            var ct = componentTypeName.Trim();
            if (ct.Equals("GameObject", StringComparison.OrdinalIgnoreCase)) return go;
            if (ct.Equals("Transform", StringComparison.OrdinalIgnoreCase)) return go.Transform;
            foreach (var b in go.Behaviors)
            {
                if (b != null && TypeNameMatches(b.GetType(), ct))
                    return b;
            }
            return null;
        }

        static bool TypeNameMatches(Type t, string name)
        {
            if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            if (t.FullName != null)
            {
                if (t.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                if (t.FullName.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool IsUserAssembly(Assembly asm)
        {
            if (ReferenceEquals(asm, Assembly.GetEntryAssembly())) return true;
            var name = asm.GetName().Name ?? "";
            return name.StartsWith("Game_Engine", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "Game-Engine", StringComparison.OrdinalIgnoreCase);
        }

        public static Type? ResolveNamedType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var key = typeName.Trim();
            lock (TypeCache)
            {
                if (TypeCache.TryGetValue(key, out var cached)) return cached;
            }

            Type? found = null;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!IsUserAssembly(asm)) continue;
                    try
                    {
                        var t = asm.GetType(key, throwOnError: false, ignoreCase: true);
                        if (t != null) { found = t; break; }
                    }
                    catch { /* invalid type name for assembly */ }
                }

                if (found == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!IsUserAssembly(asm)) continue;
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException e)
                        {
                            types = e.Types.Where(x => x != null).Cast<Type>().ToArray();
                        }
                        catch { continue; }

                        foreach (var t in types)
                        {
                            if (t.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                                || (t.FullName != null && t.FullName.Equals(key, StringComparison.OrdinalIgnoreCase)))
                            {
                                found = t;
                                break;
                            }
                        }
                        if (found != null) break;
                    }
                }
            }
            catch
            {
                found = null;
            }

            lock (TypeCache)
            {
                TypeCache[key] = found;
            }
            return found;
        }

        static string[] SplitMemberPath(string path)
        {
            return path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        static PropertyInfo? GetReadableProperty(Type t, string name)
        {
            var p = t.GetProperty(name, Inst);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p;
            return null;
        }

        static FieldInfo? GetReadableField(Type t, string name)
        {
            var f = t.GetField(name, Inst);
            return f;
        }

        static bool TryNavigateStep(object obj, string segment, out object? next)
        {
            next = null;
            var t = obj.GetType();
            var p = GetReadableProperty(t, segment);
            if (p != null)
            {
                next = p.GetValue(obj);
                return true;
            }
            var f = GetReadableField(t, segment);
            if (f != null)
            {
                next = f.GetValue(obj);
                return true;
            }
            return false;
        }

        public static bool TryReadPath(object? root, string memberPath, out object? value, out string? error)
        {
            value = null;
            error = null;
            if (root == null)
            {
                error = "target is null";
                return false;
            }

            var parts = SplitMemberPath(memberPath);
            if (parts.Length == 0)
            {
                error = "memberPath is empty";
                return false;
            }

            object? cur = root;
            foreach (var seg in parts)
            {
                if (cur == null)
                {
                    error = $"null before '{seg}'";
                    return false;
                }
                if (!TryNavigateStep(cur, seg, out cur))
                {
                    error = $"'{seg}' not found on {cur.GetType().Name}";
                    return false;
                }
            }

            value = cur;
            return true;
        }

        static PropertyInfo? GetWritableProperty(Type t, string name)
        {
            var p = t.GetProperty(name, Inst);
            if (p != null && p.CanWrite && p.GetIndexParameters().Length == 0) return p;
            return null;
        }

        static FieldInfo? GetWritableField(Type t, string name)
        {
            var f = t.GetField(name, Inst);
            if (f != null && !f.IsInitOnly) return f;
            return null;
        }

        public static bool TryWritePath(object? root, string memberPath, string valueString, out string? error)
        {
            error = null;
            if (root == null)
            {
                error = "target is null";
                return false;
            }

            var parts = SplitMemberPath(memberPath);
            if (parts.Length == 0)
            {
                error = "memberPath is empty";
                return false;
            }

            object? parent = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parent == null)
                {
                    error = $"null before '{parts[i]}'";
                    return false;
                }
                if (!TryNavigateStep(parent, parts[i], out parent))
                {
                    error = $"'{parts[i]}' not found on {parent.GetType().Name}";
                    return false;
                }
            }

            if (parent == null)
            {
                error = "null parent";
                return false;
            }

            var last = parts[^1];
            var pt = parent.GetType();
            var prop = GetWritableProperty(pt, last);
            if (prop != null)
            {
                if (!TryConvertString(valueString, prop.PropertyType, out var converted, out var convErr))
                {
                    error = convErr;
                    return false;
                }
                try
                {
                    prop.SetValue(parent, converted);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            var field = GetWritableField(pt, last);
            if (field != null)
            {
                if (!TryConvertString(valueString, field.FieldType, out var converted, out var convErr))
                {
                    error = convErr;
                    return false;
                }
                try
                {
                    field.SetValue(parent, converted);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            error = $"'{last}' is not assignable on {pt.Name}";
            return false;
        }

        static bool TryGetStaticValue(Type t, string name, out object? value)
        {
            value = null;
            var p = t.GetProperty(name, Stat);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
            {
                value = p.GetValue(null);
                return true;
            }
            var f = t.GetField(name, Stat);
            if (f != null)
            {
                value = f.GetValue(null);
                return true;
            }
            return false;
        }

        public static bool TryReadStaticPath(string typeName, string memberPath, out object? value, out string? error)
        {
            value = null;
            error = null;
            var t = ResolveNamedType(typeName);
            if (t == null)
            {
                error = $"type '{typeName}' not found";
                return false;
            }

            var parts = SplitMemberPath(memberPath);
            if (parts.Length == 0)
            {
                error = "memberPath is empty";
                return false;
            }

            if (!TryGetStaticValue(t, parts[0], out object? cur))
            {
                error = $"'{parts[0]}' not found on {t.Name}";
                return false;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                if (cur == null)
                {
                    error = $"null before '{parts[i]}'";
                    return false;
                }
                if (!TryNavigateStep(cur, parts[i], out cur))
                {
                    error = $"'{parts[i]}' not found on {cur.GetType().Name}";
                    return false;
                }
            }

            value = cur;
            return true;
        }

        static bool TrySetStatic(Type t, string name, string valueString, out string? error)
        {
            error = null;
            var p = t.GetProperty(name, Stat);
            if (p != null && p.CanWrite && p.GetIndexParameters().Length == 0)
            {
                if (!TryConvertString(valueString, p.PropertyType, out var converted, out var convErr))
                {
                    error = convErr;
                    return false;
                }
                try
                {
                    p.SetValue(null, converted);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            var f = t.GetField(name, Stat);
            if (f != null && !f.IsInitOnly)
            {
                if (!TryConvertString(valueString, f.FieldType, out var converted, out var convErr))
                {
                    error = convErr;
                    return false;
                }
                try
                {
                    f.SetValue(null, converted);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            error = $"'{name}' is not assignable on {t.Name}";
            return false;
        }

        public static bool TryWriteStaticPath(string typeName, string memberPath, string valueString, out string? error)
        {
            error = null;
            var t = ResolveNamedType(typeName);
            if (t == null)
            {
                error = $"type '{typeName}' not found";
                return false;
            }

            var parts = SplitMemberPath(memberPath);
            if (parts.Length == 0)
            {
                error = "memberPath is empty";
                return false;
            }

            if (parts.Length == 1)
                return TrySetStatic(t, parts[0], valueString, out error);

            object? parent = null;
            if (!TryGetStaticValue(t, parts[0], out parent))
            {
                error = $"'{parts[0]}' not found on {t.Name}";
                return false;
            }

            for (int i = 1; i < parts.Length - 1; i++)
            {
                if (parent == null)
                {
                    error = $"null before '{parts[i]}'";
                    return false;
                }
                if (!TryNavigateStep(parent, parts[i], out parent))
                {
                    error = $"'{parts[i]}' not found";
                    return false;
                }
            }

            if (parent == null)
            {
                error = "null parent";
                return false;
            }

            return TryAssignMemberOnObject(parent, parts[^1], valueString, out error);
        }

        static bool TryAssignMemberOnObject(object parent, string singleSegment, string valueString, out string? error)
        {
            error = null;
            var pt = parent.GetType();
            var prop = GetWritableProperty(pt, singleSegment);
            if (prop != null)
            {
                if (!TryConvertString(valueString, prop.PropertyType, out var converted, out var convErr))
                {
                    error = convErr;
                    return false;
                }
                try
                {
                    prop.SetValue(parent, converted);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            var field = GetWritableField(pt, singleSegment);
            if (field != null)
            {
                if (!TryConvertString(valueString, field.FieldType, out var converted, out var convErr))
                {
                    error = convErr;
                    return false;
                }
                try
                {
                    field.SetValue(parent, converted);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            error = $"'{singleSegment}' not assignable";
            return false;
        }

        public static string FormatValue(object? v)
        {
            if (v == null) return "";
            if (v is bool b) return b ? "true" : "false";
            if (v is IFormattable fmt and not IConvertible)
                return fmt.ToString(null, CultureInfo.InvariantCulture);
            if (v is float f) return f.ToString(CultureInfo.InvariantCulture);
            if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (v is decimal m) return m.ToString(CultureInfo.InvariantCulture);
            if (v is CoreVector3 cv)
                return string.Concat(cv.X.ToString(CultureInfo.InvariantCulture), ";",
                    cv.Y.ToString(CultureInfo.InvariantCulture), ";",
                    cv.Z.ToString(CultureInfo.InvariantCulture));
            if (v is Enum e) return e.ToString();
            if (v is string s) return s;
            if (v is IConvertible)
            {
                try { return Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""; }
                catch { return v.ToString() ?? ""; }
            }
            return v.ToString() ?? "";
        }

        public static bool TryConvertString(string raw, Type targetType, out object? converted, out string? error)
        {
            converted = null;
            error = null;
            var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
            raw = raw.Trim();

            try
            {
                if (t == typeof(string))
                {
                    converted = raw;
                    return true;
                }
                if (t == typeof(bool))
                {
                    if (bool.TryParse(raw, out var bb)) { converted = bb; return true; }
                    if (raw == "1") { converted = true; return true; }
                    if (raw == "0") { converted = false; return true; }
                    error = "invalid bool";
                    return false;
                }
                if (t == typeof(int) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    converted = i;
                    return true;
                }
                if (t == typeof(long) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    converted = l;
                    return true;
                }
                if (t == typeof(float) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ff))
                {
                    converted = ff;
                    return true;
                }
                if (t == typeof(double) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd))
                {
                    converted = dd;
                    return true;
                }
                if (t == typeof(decimal) && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                {
                    converted = mm;
                    return true;
                }
                if (t.IsEnum)
                {
                    try
                    {
                        converted = Enum.Parse(t, raw, ignoreCase: true);
                        return true;
                    }
                    catch
                    {
                        error = $"invalid enum {t.Name}";
                        return false;
                    }
                }
                if (t == typeof(CoreVector3))
                {
                    var segs = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (segs.Length >= 3
                        && double.TryParse(segs[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var vx)
                        && double.TryParse(segs[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var vy)
                        && double.TryParse(segs[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vz))
                    {
                        converted = new CoreVector3(vx, vy, vz);
                        return true;
                    }
                    error = "vector3 expects 'x;y;z' or 'x,y,z'";
                    return false;
                }

                converted = Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                converted = null;
                return false;
            }
        }

        public static string ResolveValueString(VisualBlueprintBehavior host, BlueprintNode node)
        {
            var lit = node.Properties.TryGetValue("value", out var v) ? v : "";
            if (!string.IsNullOrEmpty(lit)) return lit;
            var vk = node.Properties.TryGetValue("valueVarKey", out var k) ? k.Trim() : "";
            if (vk.Length > 0 && host.Variables.TryGetValue(vk, out var fromVar) && fromVar != null)
                return fromVar;
            return "";
        }
    }
}
