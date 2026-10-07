#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Game_Engine.Core.Component;

namespace Game_Engine.Core;

/// <summary>
/// After a script compile, scene objects still hold the previous EditorScripts type.
/// Play then runs the old Start()/HUD. Prefer the newest EditorScripts assembly.
/// </summary>
public static class ScriptTypeReload
{
    public static Assembly? LatestEditorScripts { get; private set; }

    public static void NoteCompiled(Assembly asm)
    {
        if (asm == null) return;
        var n = asm.GetName().Name;
        if (n != null && n.StartsWith("EditorScripts_", StringComparison.OrdinalIgnoreCase))
            LatestEditorScripts = asm;
    }

    static Type? FindType(Assembly? asm, string fullName)
    {
        if (asm == null) return null;
        try
        {
            var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (t != null) return t;
        }
        catch { }
        try
        {
            foreach (var t in asm.GetTypes())
                if (t != null && t.FullName == fullName) return t;
        }
        catch { }
        return null;
    }

    public static Type? TryResolveLatestType(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        EnsureLatestLoaded();

        var hit = FindType(LatestEditorScripts, fullName);
        if (hit != null) return hit;

        var newestName = NewestEditorScriptsName();
        if (newestName == null) return null;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = asm.GetName().Name;
            if (asmName == null || !asmName.Equals(newestName, StringComparison.OrdinalIgnoreCase))
                continue;
            hit = FindType(asm, fullName);
            if (hit == null) continue;
            LatestEditorScripts = asm;
            return hit;
        }

        return null;
    }

    static AssemblyLoadContext? s_diskAlc;

    static string? NewestEditorScriptsPath()
    {
        try
        {
            var proj = ProjectService.Current;
            var root = string.IsNullOrWhiteSpace(proj?.BuildsPath) ? proj?.RootPath : proj!.BuildsPath;
            if (string.IsNullOrWhiteSpace(root)) return null;
            var dir = Path.Combine(root, "EditorScripts");
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "EditorScripts_*.dll")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static string? NewestEditorScriptsName()
    {
        var path = NewestEditorScriptsPath();
        return path == null ? null : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Prefer the newest on-disk EditorScripts DLL, not a stale in-memory compile.
    /// Stop→Play was keeping the previous assembly when Latest still resolved.
    /// </summary>
    public static void EnsureLatestLoaded()
    {
        var path = NewestEditorScriptsPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        var want = Path.GetFileNameWithoutExtension(path);
        var have = LatestEditorScripts?.GetName().Name;
        if (have != null && have.Equals(want, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name == null || !name.Equals(want, StringComparison.OrdinalIgnoreCase))
                continue;
            LatestEditorScripts = asm;
            return;
        }

        try
        {
            var alc = new AssemblyLoadContext(want, isCollectible: true);
            var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(path));
            if (s_diskAlc != null)
                RetireLoadContext(s_diskAlc);
            s_diskAlc = alc;
            NoteCompiled(asm);
        }
        catch { }
    }

    public static bool TryMigrate(GameObject owner, ref Behavior b, bool forceNewInstance = false)
    {
        if (owner == null || b == null) return false;
        if (b is Transform) return false;

        var fromType = b.GetType();
        var asmName = fromType.Assembly.GetName().Name;
        if (asmName == null || !asmName.StartsWith("EditorScripts_", StringComparison.OrdinalIgnoreCase))
            return false;

        var latest = TryResolveLatestType(fromType.FullName) ?? (forceNewInstance ? fromType : null);
        if (latest == null) return false;
        var latestAsm = latest.Assembly.GetName().Name;
        bool sameAssembly = latest == fromType
            || (latestAsm != null && latestAsm.Equals(asmName, StringComparison.OrdinalIgnoreCase));
        if (sameAssembly && !forceNewInstance) return false;

        Behavior? nb;
        try { nb = (Behavior)Activator.CreateInstance(latest)!; }
        catch { return false; }

        CopyBehaviorState(fromType, b, latest, nb);
        try { b.__OnDestroy(); } catch { }
        owner.RemoveBehavior(b);
        owner.AddBehavior(nb);
        b = nb;
        return true;
    }

    static void CopyBehaviorState(Type fromType, Behavior src, Type latest, Behavior dst)
    {
        var dstProps = latest.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.SetMethod != null && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var sp in fromType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!sp.CanRead || sp.GetIndexParameters().Length != 0) continue;
            if (!dstProps.TryGetValue(sp.Name, out var dp)) continue;
            if (!dp.PropertyType.IsAssignableFrom(sp.PropertyType)) continue;
            try { dp.SetValue(dst, sp.GetValue(src)); } catch { }
        }

        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var sf in fromType.GetFields(fields))
        {
            if (sf.IsStatic) continue;
            if (!sf.IsPublic && sf.GetCustomAttribute<PersistAttribute>() == null) continue;
            var df = latest.GetField(sf.Name, fields);
            if (df == null || !df.FieldType.IsAssignableFrom(sf.FieldType)) continue;
            try { df.SetValue(dst, sf.GetValue(src)); } catch { }
        }
    }

    public static int MigrateScene(bool forceNewInstance = false)
    {
        int n = 0;
        var roots = SceneService.Root;
        if (roots == null) return 0;
        var stack = new Stack<GameObject>();
        foreach (var r in roots) stack.Push(r);
        while (stack.Count > 0)
        {
            var go = stack.Pop();
            foreach (var child in go.Children)
                stack.Push(child);
            foreach (var cur in go.Behaviors.ToList())
            {
                var b = cur;
                if (TryMigrate(go, ref b, forceNewInstance)) n++;
            }
        }
        if (n > 0)
            Log.Info($"Script types remapped ({n}) — using the latest EditorScripts.");
        return n;
    }

    /// <summary>Set when scripts compile so the next Play rebuilds instances.</summary>
    public static bool NeedsFreshPlay { get; set; }

    static readonly List<AssemblyLoadContext> s_retiredAlcs = new();

    /// <summary>
    /// Keep the previous compile loaded until the scene is rematerialized.
    /// Unloading immediately leaves Game views holding dead types.
    /// </summary>
    public static void RetireLoadContext(AssemblyLoadContext? alc)
    {
        if (alc == null) return;
        lock (s_retiredAlcs)
            s_retiredAlcs.Add(alc);
    }

    public static void UnloadRetiredLoadContexts()
    {
        List<AssemblyLoadContext> list;
        lock (s_retiredAlcs)
        {
            if (s_retiredAlcs.Count == 0) return;
            list = s_retiredAlcs.ToList();
            s_retiredAlcs.Clear();
        }
        foreach (var alc in list)
        {
            try { alc.Unload(); } catch { }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Drop previous EditorScripts instances and attach fresh copies of the latest types.
    /// Does not reload the scene file — a full save/load was tearing planets and UI apart.
    /// </summary>
    public static void RematerializeScene()
    {
        EnsureLatestLoaded();
        int n = MigrateScene(forceNewInstance: true);
        UnloadRetiredLoadContexts();
        if (n > 0)
            Log.Info($"Play scripts rebuilt ({n}) from the latest EditorScripts compile.");
        else
            Log.Info("Play scripts already on the latest EditorScripts compile.");
    }
}
