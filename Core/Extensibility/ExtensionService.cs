using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
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

        // ----------------------------- Public API -----------------------------

        /// <summary>Wipes all discovered extensions and their menu model.</summary>
        public static void Clear(bool notify = false)
        {
            _instances.Clear();
            _menuRoots.Clear();
            if (notify) Changed?.Invoke();            // default false: no notify
        }

        /// <summary>Rebuild extensions/menu from specific assemblies (e.g. freshly loaded scripts).</summary>
        public static void RefreshFromAssemblies(IEnumerable<Assembly> assemblies)
        {
            var list = (assemblies ?? Array.Empty<Assembly>()).ToList();
           // Log.Info($"[Ext] RefreshFromAssemblies() — assemblies passed={list.Count}");

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
                    map[t.FullName ?? t.Name] = t; // last in wins
                }
            }

            foreach (var t in map.Values)
            {
                try { _instances.Add((EditorExtension)Activator.CreateInstance(t)); }
                catch (Exception ex) { Log.Error(ex, $"[Ext] CreateInstance failed: {t.FullName}"); }
            }

            var b = new MenuBuilder();
            var ui = new EditorUI(b);
            foreach (var ext in _instances)
                try { ext.Contribute(ui); } catch (Exception ex) { Log.Error(ex, $"[Ext] Contribute failed: {ext.GetType().FullName}"); }

            _menuRoots.AddRange(b.TopLevelMenus);
            //Log.Info($"[Ext] Built menu model — roots={_menuRoots.Count}");
            Changed?.Invoke();
        }

        

        /// <summary>
        /// Clear existing extension commands/menus, then (re)load EditorScripts_*.dll
        /// from the current project's Builds/EditorScripts folder, and rebuild menus.
        /// </summary>
        public static void RefreshFromEditorScriptsFolder()
        {
            // wipe commands (extensions only) and menu roots/instances
           // Game_Engine.Core.Log.Info("[Ext] RefreshFromEditorScriptsFolder() — clearing extension commands + menus");
            CommandRegistry.ClearExtensions();
            _instances.Clear();
            _menuRoots.Clear();

            var proj = ProjectService.Current;
            if (proj is null)
            {
                Game_Engine.Core.Log.Warning("[Ext] No project; nothing to load.");
                Changed?.Invoke();
                return;
            }

            var baseDir = string.IsNullOrWhiteSpace(proj.BuildsPath) ? proj.RootPath : proj.BuildsPath;
            var dir = Path.Combine(baseDir!, "EditorScripts");
            if (!Directory.Exists(dir))
            {
                Game_Engine.Core.Log.Warning("[Ext] EditorScripts folder not found: " + dir);
                Changed?.Invoke();
                return;
            }

            // unload previous ALC so the new DLL can be loaded cleanly
            try { s_editorScriptsAlc?.Unload(); } catch { }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            s_editorScriptsAlc = new AssemblyLoadContext("EditorScriptsHot", isCollectible: true);

            // load latest EditorScripts_*.dll (or all — here we pick the newest one)
            var latest = Directory.EnumerateFiles(dir, "EditorScripts_*.dll", SearchOption.TopDirectoryOnly)
                                  .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                                  .FirstOrDefault();

            if (latest is null)
            {
                Game_Engine.Core.Log.Info("[Ext] No EditorScripts_*.dll found to load.");
                Changed?.Invoke();
                return;
            }

            // load from memory (no file lock); try to load the PDB for nicer stack traces
            Assembly? asm = null;
            try
            {
                using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;

                Stream? pdb = null;
                var pdbPath = Path.ChangeExtension(latest, ".pdb");
                if (File.Exists(pdbPath)) pdb = new MemoryStream(File.ReadAllBytes(pdbPath));

                asm = (pdb != null) ? s_editorScriptsAlc.LoadFromStream(ms, pdb) : s_editorScriptsAlc.LoadFromStream(ms);
              //  Game_Engine.Core.Log.Info($"[Ext] Loaded editor script assembly: {Path.GetFileName(latest)}");
            }
            catch (Exception ex)
            {
                Game_Engine.Core.Log.Warning($"[Ext] Failed to load {latest}: {ex.Message}");
            }

            // build the extension model ONLY from the loaded editor script assembly
            var list = new List<Assembly>();
            if (asm != null) list.Add(asm);

            // Reuse existing builder logic
            RefreshFromAssemblies(list);
        }



    public static void ReloadFromAppDomain()
        {
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
            Changed?.Invoke();                        
        }

        /// <summary>Rebuild extensions/menu by scanning the current AppDomain.</summary>
        public static void RefreshFromAppDomain()
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
           // Log.Info($"[Ext] RefreshFromAppDomain: scanning={asms.Length}");

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

        private static void BuildMenus()
        {
            var builder = new MenuBuilder();
            var ui = new EditorUI(builder);

          //  Log.Info($"[Ext] Contribute: instances={_instances.Count}");
            for (int i = 0; i < _instances.Count; i++)
            {
                try { _instances[i].Contribute(ui); }
                catch (Exception ex) { Log.Error(ex, $"[Ext] Contribute failed: {_instances[i].GetType().FullName}"); }
            }

            _menuRoots.AddRange(builder.TopLevelMenus);
          //  Log.Info($"[Ext] Built menu model: roots={_menuRoots.Count}");
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

        // ---- helpers for Toggle/Invoke items ----

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
    }
}
