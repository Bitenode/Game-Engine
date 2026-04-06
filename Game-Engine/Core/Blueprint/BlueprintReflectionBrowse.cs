#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Populates reflection picker UI for <see cref="ReflectGet"/> / <see cref="ReflectSet"/> nodes.</summary>
    public static class BlueprintReflectionBrowse
    {
        const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        const BindingFlags Stat = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy | BindingFlags.IgnoreCase;
        public readonly record struct LabeledOption(string Display, string Stored);

        static bool IsUserAssembly(Assembly asm)
        {
            if (ReferenceEquals(asm, Assembly.GetEntryAssembly())) return true;
            var name = asm.GetName().Name ?? "";
            return name.StartsWith("Game_Engine", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "Game-Engine", StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<Type> GetTypesSafe(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null).Cast<Type>();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        static IEnumerable<Type> ConcreteBehaviors()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsUserAssembly(asm)) continue;
                foreach (var t in GetTypesSafe(asm))
                {
                    if (t is not { IsClass: true, IsAbstract: false }) continue;
                    if (!typeof(Behavior).IsAssignableFrom(t)) continue;
                    yield return t;
                }
            }
        }

        public static List<LabeledOption> GetComponentTypeOptions()
        {
            var list = new List<LabeledOption>
            {
                new("GameObject", "GameObject"),
                new("Transform", "Transform"),
            };
            var behaviors = ConcreteBehaviors().Where(t => t != typeof(Transform)).ToList();
            var dup = behaviors.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count() > 1, StringComparer.OrdinalIgnoreCase);
            foreach (var t in behaviors.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var multi = dup.TryGetValue(t.Name, out var d) && d;
                var stored = multi ? (t.FullName ?? t.Name) : t.Name;
                var display = multi ? $"{t.Name}  —  {t.Namespace}" : t.Name;
                list.Add(new LabeledOption(display, stored));
            }
            return list;
        }

        static bool HasReadableStaticMember(Type t)
        {
            foreach (var p in t.GetProperties(Stat))
            {
                if (p.CanRead && p.GetIndexParameters().Length == 0) return true;
            }
            foreach (var f in t.GetFields(Stat)) return true;
            return false;
        }

        public static List<LabeledOption> GetStaticTypeOptions()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<LabeledOption>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsUserAssembly(asm)) continue;
                foreach (var t in GetTypesSafe(asm))
                {
                    if (t is not { IsPublic: true } || t.ContainsGenericParameters) continue;
                    if (!HasReadableStaticMember(t)) continue;
                    var fn = t.FullName ?? t.Name;
                    if (!seen.Add(fn)) continue;
                    var ns = t.Namespace ?? "";
                    list.Add(new LabeledOption($"{t.Name}  —  {ns}", fn));
                }
            }
            return list.OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static Type? ResolveComponentRootType(string storedName)
        {
            if (string.IsNullOrWhiteSpace(storedName)) return null;
            if (storedName.Equals("GameObject", StringComparison.OrdinalIgnoreCase))
                return typeof(GameObject);
            if (storedName.Equals("Transform", StringComparison.OrdinalIgnoreCase))
                return typeof(Transform);
            return BlueprintReflection.ResolveNamedType(storedName.Trim());
        }

        public static bool ShouldExpandMemberType(Type pt)
        {
            pt = Nullable.GetUnderlyingType(pt) ?? pt;
            if (pt == typeof(object)) return false;
            if (pt == typeof(string)) return false;
            if (pt.IsPrimitive) return false;
            if (pt.IsEnum) return false;
            if (pt == typeof(decimal)) return false;
            if (pt == typeof(GameObject)) return false;
            if (typeof(Behavior).IsAssignableFrom(pt)) return false;
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(pt) && pt != typeof(string)) return false;
            return true;
        }

        public static List<string> GetMemberPathSuggestions(Type? rootType, int maxSegments = 5)
        {
            var result = new List<string>();
            if (rootType == null) return result;
            Walk(rootType, "", 0);
            void Walk(Type t, string prefix, int depth)
            {
                if (depth >= maxSegments) return;
                foreach (var pi in t.GetProperties(Inst))
                {
                    if (!pi.CanRead || pi.GetMethod?.IsPublic != true || pi.GetIndexParameters().Length > 0) continue;
                    var name = pi.Name;
                    var path = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
                    result.Add(path);
                    var pt = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
                    if (ShouldExpandMemberType(pt))
                        Walk(pt, path, depth + 1);
                }
                foreach (var fi in t.GetFields(Inst))
                {
                    if (!fi.IsPublic) continue;
                    var path = string.IsNullOrEmpty(prefix) ? fi.Name : $"{prefix}.{fi.Name}";
                    result.Add(path);
                    var ft = Nullable.GetUnderlyingType(fi.FieldType) ?? fi.FieldType;
                    if (ShouldExpandMemberType(ft))
                        Walk(ft, path, depth + 1);
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> GetStaticMemberPathSuggestions(Type? staticType, int maxSegments = 5)
        {
            var result = new List<string>();
            if (staticType == null) return result;

            foreach (var pi in staticType.GetProperties(Stat))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                result.Add(pi.Name);
                var pt = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
                if (ShouldExpandMemberType(pt))
                {
                    foreach (var sub in GetMemberPathSuggestions(pt, maxSegments - 1))
                        result.Add($"{pi.Name}.{sub}");
                }
            }
            foreach (var fi in staticType.GetFields(Stat))
            {
                result.Add(fi.Name);
                var ft = Nullable.GetUnderlyingType(fi.FieldType) ?? fi.FieldType;
                if (ShouldExpandMemberType(ft))
                {
                    foreach (var sub in GetMemberPathSuggestions(ft, maxSegments - 1))
                        result.Add($"{fi.Name}.{sub}");
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
