using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Avalonia.Controls;

namespace Game_Engine.Core.Extensibility
{
    public static class ExtensionService
    {
        // Living instances of discovered EditorExtension types
        private static readonly List<EditorExtension> _instances = new();

        // Root nodes for the menu model those extensions contributed
        private static readonly List<MenuNode> _menuRoots = new();

        public static event Action? Changed;

        // hot slot for editor scripts we load from disk (so we can unload them on rebuild)
        private static AssemblyLoadContext? s_editorScriptsAlc;

        private static readonly List<string> _scratchWarnings = new();
        private static readonly List<string> _scratchErrors = new();

        // ----------------------------- Public API -----------------------------

        /// <summary>Wipes all discovered extensions and their menu model.</summary>
        public static void Clear(bool notify = false)
        {
            ExtensionPanelRegistry.Clear();
            DisposeExtensionInstances();
            _instances.Clear();
            _menuRoots.Clear();
            if (notify) Changed?.Invoke();            // default false: no notify
        }

        /// <summary>
        /// Sync extensions with <see cref="ProjectService.Current"/>: load hot <c>EditorScripts_*.dll</c> when
        /// <c>Builds/EditorScripts</c> exists; otherwise unload hot assemblies (if any) and use AppDomain scan only.
        /// </summary>
        public static void RefreshForCurrentProject()
        {
            var proj = ProjectService.Current;
            if (proj is null)
            {
                CommandRegistry.ClearExtensions();
                TryUnloadEditorScriptsAlc();
                RefreshFromAppDomain();
                return;
            }

            var baseDir = string.IsNullOrWhiteSpace(proj.BuildsPath) ? proj.RootPath : proj.BuildsPath;
            var dir = Path.Combine(baseDir!, "EditorScripts");
            if (!Directory.Exists(dir))
            {
                Game_Engine.Core.Log.Info("[Ext] No EditorScripts folder; using AppDomain extensions only.");
                CommandRegistry.ClearExtensions();
                TryUnloadEditorScriptsAlc();
                RefreshFromAppDomain();
                return;
            }

            RefreshFromEditorScriptsFolder();
        }

        /// <summary>Rebuild extensions/menu from specific assemblies (e.g. freshly loaded scripts).</summary>
        /// <param name="clearScratch">When false, keep accumulated manifest/DLL diagnostic lines (used when loading from EditorScripts folder).</param>
        public static void RefreshFromAssemblies(
            IEnumerable<Assembly> assemblies,
            bool clearScratch = true,
            string? editorScriptsDir = null,
            string? manifestPath = null,
            EditorExtensionsManifest? manifest = null,
            IReadOnlyList<string>? explicitHotDllPaths = null)
        {
            var list = (assemblies ?? Array.Empty<Assembly>()).ToList();

            ExtensionPanelRegistry.Clear();
            if (clearScratch)
            {
                _scratchWarnings.Clear();
                _scratchErrors.Clear();
            }

            DisposeExtensionInstances();
            _instances.Clear();
            _menuRoots.Clear();

            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in list)
            {
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract) continue;
                    if (!typeof(EditorExtension).IsAssignableFrom(t)) continue;
                    var key = t.FullName ?? t.Name;
                    if (map.TryGetValue(key, out var prev) && prev != t)
                        _scratchWarnings.Add($"Duplicate EditorExtension type '{key}': using last in assembly order ({asm.GetName().Name}).");
                    map[key] = t;
                }
            }

            foreach (var t in map.Values)
            {
                try { _instances.Add((EditorExtension)Activator.CreateInstance(t)); }
                catch (Exception ex)
                {
                    var msg = $"CreateInstance failed: {t.FullName}: {ex.Message}";
                    _scratchErrors.Add(msg);
                    Log.Error(ex, "[Ext] " + msg);
                }
            }

            var b = new MenuBuilder();
            var ui = new EditorUI(b);
            foreach (var ext in _instances)
            {
                try { ext.Contribute(ui); }
                catch (Exception ex)
                {
                    var msg = $"Contribute failed: {ext.GetType().FullName}: {ex.Message}";
                    _scratchErrors.Add(msg);
                    Log.Error(ex, "[Ext] " + msg);
                }
            }

            _menuRoots.AddRange(b.TopLevelMenus);
            var dllPaths = explicitHotDllPaths ?? ExtractDllPathsFromAssemblies(list);
            RecordDiagnosticsSnapshot(
                clearScratch ? "Assemblies" : "EditorScripts",
                clearScratch ? null : editorScriptsDir,
                manifestPath,
                manifest,
                dllPaths,
                _scratchWarnings,
                _scratchErrors);
            Changed?.Invoke();
        }

        /// <summary>
        /// Clear existing extension commands/menus, then (re)load editor script assemblies from the
        /// current project's <c>Builds/EditorScripts</c> folder, and rebuild menus.
        /// </summary>
        public static void RefreshFromEditorScriptsFolder()
        {
            CommandRegistry.ClearExtensions();
            ExtensionPanelRegistry.Clear();
            _scratchWarnings.Clear();
            _scratchErrors.Clear();

            DisposeExtensionInstances();
            _instances.Clear();
            _menuRoots.Clear();

            var proj = ProjectService.Current;
            if (proj is null)
            {
                Game_Engine.Core.Log.Warning("[Ext] No project; reverting to AppDomain extensions.");
                TryUnloadEditorScriptsAlc();
                RefreshFromAppDomain();
                return;
            }

            var baseDir = string.IsNullOrWhiteSpace(proj.BuildsPath) ? proj.RootPath : proj.BuildsPath;
            var dir = Path.Combine(baseDir!, "EditorScripts");
            if (!Directory.Exists(dir))
            {
                Game_Engine.Core.Log.Warning("[Ext] EditorScripts folder not found: " + dir);
                TryUnloadEditorScriptsAlc();
                RefreshFromAppDomain();
                return;
            }

            try { s_editorScriptsAlc?.Unload(); } catch { }
            s_editorScriptsAlc = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var manifestPath = Path.Combine(dir, EditorExtensionsManifest.FileName);
            var manifest = EditorExtensionsManifest.TryLoad(manifestPath, out var manifestLoadErr);
            if (!string.IsNullOrEmpty(manifestLoadErr))
                _scratchWarnings.Add("Manifest parse: " + manifestLoadErr);

            var paths = EditorExtensionsManifest.ResolveDllPaths(dir, manifest, ProjectService.EngineVersion, out var skipReason);
            if (paths is null)
            {
                if (!string.IsNullOrEmpty(skipReason))
                {
                    Game_Engine.Core.Log.Warning("[Ext] " + skipReason);
                    _scratchErrors.Add(skipReason);
                }
                RefreshFromAssemblies(new[] { typeof(EditorExtension).Assembly }, clearScratch: false, dir, manifestPath, manifest, explicitHotDllPaths: Array.Empty<string>());
                LogExtensionSummary("EditorScripts (manifest skipped)", manifest, manifestPath, 0);
                return;
            }

            if (paths.Count == 0)
            {
                Game_Engine.Core.Log.Info("[Ext] No EditorScripts_*.dll in folder; built-in editor extensions only.");
                RefreshFromAssemblies(new[] { typeof(EditorExtension).Assembly }, clearScratch: false, dir, manifestPath, manifest, explicitHotDllPaths: Array.Empty<string>());
                LogExtensionSummary("EditorScripts (no hot DLLs)", manifest, manifestPath, 0);
                return;
            }

            s_editorScriptsAlc = new AssemblyLoadContext("EditorScriptsHot", isCollectible: true);
            var alc = s_editorScriptsAlc;
            alc.Resolving += (_, name) =>
            {
                if (name?.Name is null) return null;
                var dep = Path.Combine(dir, name.Name + ".dll");
                if (!File.Exists(dep)) return null;
                return LoadDllIntoAlc(alc, dep, _scratchWarnings);
            };

            var list = new List<Assembly>(paths.Count + 1) { typeof(EditorExtension).Assembly };
            foreach (var dllPath in paths)
            {
                var asm = LoadDllIntoAlc(alc, dllPath, _scratchWarnings);
                if (asm != null) list.Add(asm);
            }

            RefreshFromAssemblies(list, clearScratch: false, dir, manifestPath, manifest, explicitHotDllPaths: paths);
            LogExtensionSummary("EditorScripts", manifest, manifestPath, paths.Count);
        }

        private static IReadOnlyList<string> ExtractDllPathsFromAssemblies(List<Assembly> assemblies)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in assemblies)
            {
                try
                {
                    if (a.IsDynamic) continue;
                    var loc = a.Location;
                    if (!string.IsNullOrEmpty(loc) && loc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        set.Add(loc);
                }
                catch { /* dynamic / empty */ }
            }
            return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Assembly? LoadDllIntoAlc(AssemblyLoadContext alc, string dllPath, List<string>? warnings)
        {
            try
            {
                using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;

                Stream? pdb = null;
                var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                if (File.Exists(pdbPath)) pdb = new MemoryStream(File.ReadAllBytes(pdbPath));

                return pdb != null ? alc.LoadFromStream(ms, pdb) : alc.LoadFromStream(ms);
            }
            catch (Exception ex)
            {
                var msg = $"Failed to load {dllPath}: {ex.Message}";
                Game_Engine.Core.Log.Warning("[Ext] " + msg);
                warnings?.Add(msg);
                return null;
            }
        }

        public static void ReloadFromAppDomain()
        {
            ExtensionPanelRegistry.Clear();
            _scratchWarnings.Clear();
            _scratchErrors.Clear();

            DisposeExtensionInstances();
            _instances.Clear();
            _menuRoots.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract) continue;
                    if (!typeof(EditorExtension).IsAssignableFrom(t)) continue;
                    var key = t.FullName ?? t.Name;
                    if (!seen.Add(key)) continue;
                    try { _instances.Add((EditorExtension)Activator.CreateInstance(t)); }
                    catch (Exception ex) { Log.Error(ex, $"[Ext] CreateInstance failed: {t.FullName}"); }
                }
            }

            var b = new MenuBuilder();
            var ui = new EditorUI(b);
            foreach (var ext in _instances)
            {
                try { ext.Contribute(ui); }
                catch (Exception ex) { Log.Error(ex, $"[Ext] Contribute failed: {ext.GetType().FullName}"); }
            }

            _menuRoots.AddRange(b.TopLevelMenus);
            RecordDiagnosticsSnapshot("ReloadFromAppDomain", null, null, null, Array.Empty<string>(), _scratchWarnings, _scratchErrors);
            Changed?.Invoke();
        }

        /// <summary>Rebuild extensions/menu by scanning the current AppDomain.</summary>
        public static void RefreshFromAppDomain()
        {
            CommandRegistry.ClearExtensions();
            ExtensionPanelRegistry.Clear();
            _scratchWarnings.Clear();
            _scratchErrors.Clear();

            DisposeExtensionInstances();

            var asms = AppDomain.CurrentDomain.GetAssemblies();

            _instances.Clear();
            _menuRoots.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in asms)
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract) continue;
                    if (!typeof(EditorExtension).IsAssignableFrom(t)) continue;

                    var key = t.FullName ?? t.Name;
                    if (!seen.Add(key)) continue;

                    try { _instances.Add((EditorExtension)Activator.CreateInstance(t)!); }
                    catch (Exception ex) { Log.Error(ex, $"[Ext] CreateInstance: {t.FullName}"); }
                }
            }

            BuildMenus();
            LogExtensionSummary("AppDomain", null, null, 0);
            RecordDiagnosticsSnapshot("AppDomain", null, null, null, Array.Empty<string>(), _scratchWarnings, _scratchErrors);
            Changed?.Invoke();
        }

        /// <summary>Translate the menu model to Avalonia MenuItems for the UI.</summary>
        public static List<MenuItem> BuildAvaloniaMenus()
        {
            var list = new List<MenuItem>(_menuRoots.Count);
            foreach (var root in _menuRoots)
            {
                var mi = new MenuItem { Header = root.Header };
                AddChildren(mi, root);
                list.Add(mi);
            }
            return list;
        }

        // --------------------------- Implementation --------------------------

        private static void RecordDiagnosticsSnapshot(
            string loadSource,
            string? editorScriptsDir,
            string? manifestPath,
            EditorExtensionsManifest? manifest,
            IReadOnlyList<string> loadedDllPaths,
            List<string> warnings,
            List<string> errors)
        {
            var collisionCopy = CommandRegistry.RecentIdCollisions.ToList();
            var extNames = _instances.Select(e => e.GetType().FullName ?? e.GetType().Name).ToList();

            ExtensionDiagnostics.Record(new ExtensionDiagnostics.Snapshot
            {
                LoadSource = loadSource,
                EditorScriptsDir = editorScriptsDir,
                ManifestPath = manifestPath,
                Manifest = manifest,
                LoadedDllPaths = loadedDllPaths,
                ExtensionTypeNames = extNames,
                Warnings = warnings.ToList(),
                Errors = errors.ToList(),
                CommandIdCollisions = collisionCopy
            });
        }

        private static void BuildMenus()
        {
            var builder = new MenuBuilder();
            var ui = new EditorUI(builder);

            for (int i = 0; i < _instances.Count; i++)
            {
                try { _instances[i].Contribute(ui); }
                catch (Exception ex) { Log.Error(ex, $"[Ext] Contribute failed: {_instances[i].GetType().FullName}"); }
            }

            _menuRoots.AddRange(builder.TopLevelMenus);
        }

        private static void AddChildren(MenuItem parent, MenuNode node)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                var ch = node.Children[i];
                switch (ch.Kind)
                {
                    case MenuNodeKind.Separator:
                        parent.Items.Add(new Separator());
                        break;

                    case MenuNodeKind.Menu:
                        var sub = new MenuItem { Header = ch.Header };
                        AddChildren(sub, ch);
                        parent.Items.Add(sub);
                        break;

                    case MenuNodeKind.Item:
                        var item = new MenuItem { Header = ch.Header };
                        WireAction(item, ch);
                        parent.Items.Add(item);
                        break;
                }
            }
        }

        private static void WireAction(MenuItem item, MenuNode spec)
        {
            switch (spec.ActionKind)
            {
                case MenuItemActionKind.Command:
                    item.Click += delegate
                    {
                        var c = CommandRegistry.TryGet(spec.CommandId);
                        if (c != null && c.CanExecute()) c.Execute();
                    };
                    break;

                case MenuItemActionKind.Toggle:
                    item.Click += delegate { ToggleBool(spec.BehaviorType, spec.MemberName); };
                    break;

                case MenuItemActionKind.Invoke:
                    item.Click += delegate { InvokeMethod(spec.BehaviorType, spec.MemberName); };
                    break;
            }
        }

        private static void ToggleBool(string behaviorTypeName, string propName)
        {
            var t = FindBehaviorType(behaviorTypeName);
            if (t == null) { Log.Warning("Toggle: missing type " + behaviorTypeName); return; }

            var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null || p.PropertyType != typeof(bool) || !p.CanRead || !p.CanWrite)
            { Log.Warning("Toggle: missing bool prop " + behaviorTypeName + "." + propName); return; }

            var go = SelectionService.Current;
            if (go == null) return;

            var comp = go.Behaviors.FirstOrDefault(b => t.IsAssignableFrom(b.GetType()));
            if (comp == null) return;

            try
            {
                var cur = (bool)p.GetValue(comp)!;
                p.SetValue(comp, !cur);
                SceneService.NotifyChanged();
            }
            catch (Exception ex) { Log.Error(ex, "ToggleBool"); }
        }

        private static void InvokeMethod(string behaviorTypeName, string methodName)
        {
            var t = FindBehaviorType(behaviorTypeName);
            if (t == null) { Log.Warning("Invoke: missing type " + behaviorTypeName); return; }

            var m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m == null) { Log.Warning("Invoke: missing method " + behaviorTypeName + "." + methodName + "()"); return; }

            var go = SelectionService.Current;
            if (go == null) return;

            var comp = go.Behaviors.FirstOrDefault(b => t.IsAssignableFrom(b.GetType()));
            if (comp == null) return;

            try { m.Invoke(comp, null); SceneService.NotifyChanged(); }
            catch (Exception ex) { Log.Error(ex, "InvokeMethod"); }
        }

        private static Type? FindBehaviorType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                for (int i = 0; i < types.Length; i++)
                {
                    var tp = types[i];
                    if (tp == null) continue;
                    if (!typeof(Behavior).IsAssignableFrom(tp)) continue;
                    if (string.Equals(tp.Name, name, StringComparison.OrdinalIgnoreCase)) return tp;
                    if (string.Equals(tp.FullName, name, StringComparison.OrdinalIgnoreCase)) return tp;
                }
            }
            return null;
        }

        private static void TryUnloadEditorScriptsAlc()
        {
            try { s_editorScriptsAlc?.Unload(); } catch { }
            s_editorScriptsAlc = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }

        private static void DisposeExtensionInstances()
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                try { _instances[i]?.Dispose(); }
                catch (Exception ex) { Log.Error(ex, $"[Ext] Dispose failed: {_instances[i]?.GetType().FullName}"); }
            }
        }

        private static void LogExtensionSummary(string loadSource, EditorExtensionsManifest? manifest, string? manifestPath, int hotDllCount)
        {
            var sb = new StringBuilder();
            sb.Append("[Ext] ");
            sb.Append(loadSource);
            sb.Append(": count=").Append(_instances.Count);
            if (manifest != null)
            {
                if (!string.IsNullOrWhiteSpace(manifest.DisplayName))
                    sb.Append(", pack=").Append(manifest.DisplayName);
                if (!string.IsNullOrWhiteSpace(manifest.Version))
                    sb.Append(", ver=").Append(manifest.Version);
                if (!string.IsNullOrWhiteSpace(manifest.Author))
                    sb.Append(", by=").Append(manifest.Author);
                if (!string.IsNullOrWhiteSpace(manifest.Description))
                    sb.Append(", desc=").Append(manifest.Description);
            }
            if (!string.IsNullOrWhiteSpace(manifestPath))
                sb.Append(", manifest=").Append(manifestPath);
            if (hotDllCount > 0)
                sb.Append(", hotDlls=").Append(hotDllCount);
            sb.Append(". Types: ");
            if (_instances.Count == 0)
                sb.Append("(none)");
            else
            {
                for (int i = 0; i < _instances.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(_instances[i].GetType().FullName ?? _instances[i].GetType().Name);
                }
            }
            Game_Engine.Core.Log.Info(sb.ToString());
        }
    }
}
