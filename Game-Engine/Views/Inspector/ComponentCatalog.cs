using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Game_Engine.Core;
using CoreTransform = Game_Engine.Core.Component.Transform;

namespace Game_Engine.Views.Inspector;

public sealed class ScriptInfo
{
    public string Name { get; }
    public string FullName { get; }
    public string FilePath { get; }
    public ScriptInfo(string name, string fullName, string filePath)
    {
        Name = name;
        FullName = fullName;
        FilePath = filePath;
    }
}

public sealed class ComponentCatalogItem
{
    public string Display { get; set; } = "";
    public string Category { get; set; } = "";
    public Type? Type { get; set; }
    public ScriptInfo? Script { get; set; }
}

/// <summary>Built-in + project script types for Add Component, cached across inspector rebuilds.</summary>
public static class ComponentCatalog
{
    const double ScriptCacheSeconds = 2;

    static readonly Regex RxNamespace = new(@"namespace\s+([A-Za-z_][\w\.]*)", RegexOptions.Compiled);
    static readonly Regex RxClassDecl = new(
        @"(?:(?:\[[^\]]*\]\s*)|(?:public|internal|protected|private|sealed|abstract|partial)\s+)*class\s+([A-Za-z_]\w*)\s*:\s*([^\r\n{]+)",
        RegexOptions.Compiled);
    static readonly Regex RxBaseContainsBehavior = new(
        @"\b(?:global::)?(?:[A-Za-z_]\w*\.)*Behavior\b", RegexOptions.Compiled);

    static List<Type>? _builtIns;
    static string _builtInFingerprint = "";

    static List<ScriptInfo> _scripts = new();
    static bool _scriptsCached;
    static string _scriptCacheRoot = "";
    static DateTime _scriptCacheStamp = DateTime.MinValue;

    public static void Invalidate()
    {
        _builtIns = null;
        _builtInFingerprint = "";
        InvalidateScripts();
    }

    public static void InvalidateScripts()
    {
        _scripts = new List<ScriptInfo>();
        _scriptsCached = false;
        _scriptCacheRoot = "";
        _scriptCacheStamp = DateTime.MinValue;
    }

    public static (SortedDictionary<string, List<ComponentCatalogItem>> Categories, List<ScriptInfo> Scripts) Get()
    {
        var builtIns = GetBuiltInTypes();
        var scripts = GetProjectScripts();
        scripts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var scriptFullNames = new HashSet<string>(scripts.Select(s => s.FullName), StringComparer.Ordinal);
        var categoryMap = new SortedDictionary<string, List<ComponentCatalogItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in builtIns)
        {
            var fn = t.FullName ?? t.Name;
            if (scriptFullNames.Contains(fn)) continue;
            var cat = GetCategory(t);
            if (!categoryMap.TryGetValue(cat, out var list))
            {
                list = new List<ComponentCatalogItem>();
                categoryMap[cat] = list;
            }
            list.Add(new ComponentCatalogItem { Display = t.Name, Category = cat, Type = t });
        }

        foreach (var list in categoryMap.Values)
            list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));

        return (categoryMap, scripts);
    }

    static List<Type> GetBuiltInTypes()
    {
        var fingerprint = BuiltInFingerprint();
        if (_builtIns != null && string.Equals(_builtInFingerprint, fingerprint, StringComparison.Ordinal))
            return _builtIns;

        _builtIns = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !IsEditorScriptAssembly(a))
            .SelectMany(LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(Behavior).IsAssignableFrom(t)
                        && t != typeof(CoreTransform))
            .Cast<Type>()
            .ToList();
        _builtInFingerprint = fingerprint;
        return _builtIns;
    }

    static List<ScriptInfo> GetProjectScripts()
    {
        var p = ProjectService.Current;
        if (p == null) return new List<ScriptInfo>();

        var rootSig = p.RootPath ?? "";
        if (_scriptsCached
            && string.Equals(_scriptCacheRoot, rootSig, StringComparison.OrdinalIgnoreCase)
            && (DateTime.UtcNow - _scriptCacheStamp).TotalSeconds < ScriptCacheSeconds)
        {
            return new List<ScriptInfo>(_scripts);
        }

        var found = DiscoverProjectBehaviorScripts();
        _scripts = found;
        _scriptsCached = true;
        _scriptCacheRoot = rootSig;
        _scriptCacheStamp = DateTime.UtcNow;
        return new List<ScriptInfo>(_scripts);
    }

    static string BuiltInFingerprint()
    {
        var names = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !IsEditorScriptAssembly(a))
            .Select(a => a.FullName ?? a.GetName().Name ?? "")
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join('\n', names);
    }

    public static bool IsEditorScriptAssembly(Assembly asm)
    {
        try
        {
            if (AssemblyLoadContext.GetLoadContext(asm)?.IsCollectible == true)
                return true;

            var loc = asm.Location;
            if (string.IsNullOrWhiteSpace(loc)) return false;

            if (Path.GetFileName(loc).StartsWith("EditorScripts_", StringComparison.OrdinalIgnoreCase))
                return true;

            var marker = Path.DirectorySeparatorChar + "EditorScripts" + Path.DirectorySeparatorChar;
            var norm = Path.GetFullPath(loc);
            return norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    static IEnumerable<Type> LoadableTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types!.Where(t => t is not null)!; }
        catch { return Array.Empty<Type>(); }
    }

    static string GetCategory(Type t)
    {
        var attr = t.GetCustomAttributes(typeof(ComponentCategoryAttribute), false);
        if (attr.Length > 0) return ((ComponentCategoryAttribute)attr[0]).Category;
        return "Misc";
    }

    static IEnumerable<string> CandidateScriptRoots()
    {
        var p = ProjectService.Current;
        if (p == null) yield break;

        var dirs = new[]
        {
            p.RootPath,
            p.AssetsPath,
            p.ScenesPath,
            p.PackagesPath,
            p.BuildsPath
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var full = Path.GetFullPath(d);
            if (!Directory.Exists(full)) continue;
            if (seen.Add(full)) yield return full;
        }
    }

    static List<ScriptInfo> DiscoverProjectBehaviorScripts()
    {
        var found = new List<ScriptInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in CandidateScriptRoots())
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var s = f.Replace('/', Path.DirectorySeparatorChar);
                        var sep = Path.DirectorySeparatorChar;
                        return s.IndexOf($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) < 0
                            && s.IndexOf($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase) < 0
                            && s.IndexOf($"{sep}.git{sep}", StringComparison.OrdinalIgnoreCase) < 0;
                    })
                    .Take(5000);
            }
            catch { continue; }

            foreach (var f in files)
            {
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }

                string ns = "";
                var nsMatches = RxNamespace.Matches(text);
                if (nsMatches.Count > 0)
                    ns = nsMatches[nsMatches.Count - 1].Groups[1].Value.Trim();

                var clsMatches = RxClassDecl.Matches(text);
                if (clsMatches.Count == 0) continue;

                for (int m = 0; m < clsMatches.Count; m++)
                {
                    var cm = clsMatches[m];
                    var className = cm.Groups[1].Value.Trim();
                    var baseList = cm.Groups[2].Value.Trim();

                    if (!RxBaseContainsBehavior.IsMatch(baseList)) continue;

                    var headStart = Math.Max(0, cm.Index - 64);
                    var headLen = Math.Min(text.Length - headStart, cm.Length + 64);
                    var header = text.Substring(headStart, headLen);
                    if (header.IndexOf("abstract class", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    var full = string.IsNullOrEmpty(ns) ? className : (ns + "." + className);
                    if (!seen.Add(full)) continue;

                    found.Add(new ScriptInfo(className, full, f));
                }
            }
        }

        return found;
    }
}
