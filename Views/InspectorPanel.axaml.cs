using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data.Converters;
using System.Globalization;
using Game_Engine.Core;
using CoreTransform = Game_Engine.Core.Component.Transform;
using CoreVector3 = Game_Engine.Core.Vector3;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using System.IO;
using Avalonia.Platform.Storage;
using System.Runtime.CompilerServices;
using Game_Engine.Core.Component;
using static Assimp.Metadata;
using System.Text.RegularExpressions;
using System.Runtime.Loader;

namespace Game_Engine.Views;

file sealed class PrimitiveChoice
{
    public string Name { get; init; } = "";
    public Func<Game_Engine.Core.Mesh?> Factory { get; init; } = () => null;
    public override string ToString() => Name;
}

file sealed class NumberConverter : IValueConverter
{
    public static readonly NumberConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return BindingOperations.DoNothing;
        s = s.Trim();

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return BindingOperations.DoNothing;

        if (targetType == typeof(double)) return d;
        if (targetType == typeof(float)) return (float)d;
        if (targetType == typeof(int)) return (int)d;
        if (targetType == typeof(long)) return (long)d;
        if (targetType == typeof(decimal)) return (decimal)d;
        if (targetType == typeof(short)) return (short)d;

        return BindingOperations.DoNothing;
    }
}

// === User-extensible custom inspector contract ===
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class CustomInspectorAttribute : Attribute { }

// Optional interface (users may implement instead of using the attribute)
public interface ICustomInspector
{
    // Return a root Control (return null to fall back to default inspector)
    Control? BuildInspectorUI(InspectorContext ctx);
}

// Helper handed to user code so they can reuse the built-in editors safely.
public sealed class InspectorContext
{
    public Behavior Target { get; }
    public Func<PropertyInfo, Control> EditorForProperty { get; }
    public Func<string, Control> Header { get; }
    public Func<string, Control, Control> Row { get; }
    public Func<Control> DefaultInspector { get; }
    public IEnumerable<PropertyInfo> Properties => _props();
    private readonly Func<IEnumerable<PropertyInfo>> _props;

    public InspectorContext(
        Behavior target,
        Func<PropertyInfo, Control> editorForProperty,
        Func<string, Control> header,
        Func<string, Control, Control> row,
        Func<Control> defaultInspector,
        Func<IEnumerable<PropertyInfo>> props)
    {
        Target = target;
        EditorForProperty = editorForProperty;
        Header = header;
        Row = row;
        DefaultInspector = defaultInspector;
        _props = props;
    }
}


public partial class InspectorPanel : UserControl
{
    private GameObject? _target;   // what THIS inspector is showing
    private bool _isLocked;        // lock state for THIS inspector
    private Window? OwnerWindow => this.GetVisualRoot() as Window;

    // Build the list of default/inspectable properties for a Behavior
    IEnumerable<PropertyInfo> InspectableProps(Behavior b)
    {
        return b.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => p.Name is not nameof(Behavior.Enabled) && p.Name is not nameof(Behavior.gameObject))
            // hide MeshCollider internals; they are drawn in MeshColliderTargetRow(...)
            .Where(p => !(b is MeshCollider) ||
                        (p.Name != nameof(MeshCollider.TargetFilters) &&
                         p.Name != nameof(MeshCollider.TargetPaths) &&
                         p.Name != nameof(MeshCollider.BindToTargetTransform) &&
                         p.Name != nameof(MeshCollider.Mesh)));
    }

    // Default property panel (what we previously inlined)
    Control DefaultPropsPanel(Behavior b)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var p in InspectableProps(b))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = p.Name, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(PropertyEditor(b, p));
            panel.Children.Add(row);
        }
        return panel;
    }

    // Try to obtain a custom inspector UI from the user's script
    bool TryBuildCustomInspectorUI(Behavior b, out Control? ui)
    {
        // Assemble the context the user can use to reuse built-ins
        var ctx = new InspectorContext(
            b,
            editorForProperty: pi => PropertyEditor(b, pi),
            header: SectionHeader,
            row: (label, control) =>
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                r.Children.Add(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center });
                r.Children.Add(control);
                return r;
            },
            defaultInspector: () => DefaultPropsPanel(b),
            props: () => InspectableProps(b)
        );

        // Interface path
        if (b is ICustomInspector ic)
        {
            try { ui = ic.BuildInspectorUI(ctx); if (ui != null) return true; } catch { /* ignore */ }
        }

        //  Attribute path: instance method marked with [CustomInspector]
        var method = b.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.GetCustomAttribute<CustomInspectorAttribute>() != null);

        if (method != null)
        {
            try
            {
                object? result;
                var ps = method.GetParameters();
                if (ps.Length == 0)
                {
                    result = method.Invoke(b, null);
                }
                else if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(typeof(InspectorContext)))
                {
                    result = method.Invoke(b, new object[] { ctx });
                }
                else
                {
                    result = null; // unsupported signature -> ignore
                }

                if (result is Control c) { ui = c; return true; }
            }
            catch { /* ignore */ }
        }

        // Conventional name fallback (OnInspectorGUI / BuildInspector)
        foreach (var name in new[] { "OnInspectorGUI", "BuildInspector" })
        {
            var m = b.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) continue;
            try
            {
                object? result;
                var ps = m.GetParameters();
                if (ps.Length == 0) result = m.Invoke(b, null);
                else if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(typeof(InspectorContext)))
                    result = m.Invoke(b, new object[] { ctx });
                else continue;

                if (result is Control c) { ui = c; return true; }
            }
            catch { }
        }

        ui = null;
        return false;
    }

    static bool IsEditorScriptAssembly(Assembly asm)
    {
        try
        {
            // Hot build loaded into a collectible ALC
            if (AssemblyLoadContext.GetLoadContext(asm)?.IsCollectible == true)
                return true;

            var loc = asm.Location;
            if (string.IsNullOrWhiteSpace(loc)) return false;

            // Persisted editor build (the files ScriptEditor saves)
            if (Path.GetFileName(loc).StartsWith("EditorScripts_", StringComparison.OrdinalIgnoreCase))
                return true;

            // Anything under an ".../EditorScripts/..." folder
            var marker = Path.DirectorySeparatorChar + "EditorScripts" + Path.DirectorySeparatorChar;
            var norm = Path.GetFullPath(loc);
            return norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    // ---------- Project Script Discovery (source .cs,  DLLs) ----------
    sealed class ScriptInfo
    {
        public string Name { get; }
        public string FullName { get; }
        public string FilePath { get; }
        public ScriptInfo(string name, string fullName, string filePath)
        {
            Name = name; FullName = fullName; FilePath = filePath;
        }
    }

    sealed class ComboItem
    {
        public string Display { get; set; } = "";   // <— property (not field)
        public Type Type { get; set; }              // non-null if loaded/instantiable
        public ScriptInfo Script { get; set; }      // non-null for source scripts
    }

    // tolerant: captures class name and the entire base list up to '{' or newline
    static readonly Regex RxClassDecl =
        new Regex(@"(?:(?:\[[^\]]*\]\s*)|(?:public|internal|protected|private|sealed|abstract|partial)\s+)*class\s+([A-Za-z_]\w*)\s*:\s*([^\r\n{]+)",
                  RegexOptions.Compiled);

    // used to decide if the base list contains Behavior (with or without namespace)
    static readonly Regex RxBaseContainsBehavior =
        new Regex(@"\b(?:global::)?(?:[A-Za-z_]\w*\.)*Behavior\b", RegexOptions.Compiled);


    static List<ScriptInfo> _scriptCache = new List<ScriptInfo>();
    static string _scriptCacheRoot = "";
    static DateTime _scriptCacheStamp = DateTime.MinValue;

    static void InvalidateScriptCache()
    {
        _scriptCache.Clear();
        _scriptCacheRoot = "";
        _scriptCacheStamp = DateTime.MinValue;
    }

    // Scan all likely project roots (dedup + exists)
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

    // Find .cs files that declare "class X : Behavior" (tolerant of attributes, whitespace, fqns)
    static List<ScriptInfo> DiscoverProjectBehaviorScripts()
    {
        var p = ProjectService.Current;
        if (p == null) return new List<ScriptInfo>();

        // small cache
        var rootSig = p.RootPath ?? "";
        if (_scriptCache.Count > 0 && string.Equals(_scriptCacheRoot, rootSig, StringComparison.OrdinalIgnoreCase))
        {
            if ((DateTime.UtcNow - _scriptCacheStamp).TotalSeconds < 2)
                return new List<ScriptInfo>(_scriptCache);
        }

        // local tolerant regex (don’t rely on outer fields)
        var rxNamespace = new Regex(@"namespace\s+([A-Za-z_][\w\.]*)", RegexOptions.Compiled);
        var rxClassDecl = new Regex(
            @"(?:(?:\[[^\]]*\]\s*)|(?:public|internal|protected|private|sealed|abstract|partial)\s+)*class\s+([A-Za-z_]\w*)\s*:\s*([^\r\n{]+)",
            RegexOptions.Compiled);
        var rxBaseContainsBehavior = new Regex(@"\b(?:global::)?(?:[A-Za-z_]\w*\.)*Behavior\b", RegexOptions.Compiled);

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
                                     // skip common build/metadata folders
                                     var s = f.Replace('/', Path.DirectorySeparatorChar);
                                     var sep = Path.DirectorySeparatorChar;
                                     return s.IndexOf($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) < 0
                                         && s.IndexOf($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase) < 0
                                         && s.IndexOf($"{sep}.git{sep}", StringComparison.OrdinalIgnoreCase) < 0;
                                 })
                                 .Take(5000); // safety cap
            }
            catch { continue; }

            foreach (var f in files)
            {
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }

                // choose last namespace in file (typical pattern for one-class files)
                string ns = "";
                var nsMatches = rxNamespace.Matches(text);
                if (nsMatches.Count > 0)
                    ns = nsMatches[nsMatches.Count - 1].Groups[1].Value.Trim();

                var clsMatches = rxClassDecl.Matches(text);
                if (clsMatches.Count == 0) continue;

                for (int m = 0; m < clsMatches.Count; m++)
                {
                    var cm = clsMatches[m];
                    var className = cm.Groups[1].Value.Trim();
                    var baseList = cm.Groups[2].Value.Trim();

                    // must inherit Behavior somewhere in the base list
                    if (!rxBaseContainsBehavior.IsMatch(baseList)) continue;

                    // skip abstract classes (quick header check around decl)
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

        _scriptCache = found;
        _scriptCacheRoot = rootSig;
        _scriptCacheStamp = DateTime.UtcNow;
        return new List<ScriptInfo>(_scriptCache);
    }


    static Type? TryResolveLoadedType(string fullName)
    {
        Type? best = null;

        // Prefer types from a collectible ALC (built this session via the ScriptEditor)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = null;
            try { t = asm.GetType(fullName, throwOnError: false, ignoreCase: false); } catch { }
            if (t == null) continue;

            var alc = AssemblyLoadContext.GetLoadContext(asm);
            if (alc?.IsCollectible == true)               // hot build
                return t;

            if (best == null) best = t;                   // keep a fallback
        }

        // Otherwise pick the newest persisted EditorScripts_*.dll
        DateTime bestTime = DateTime.MinValue;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false, false);
                if (t == null) continue;

                var loc = asm.Location;
                if (!string.IsNullOrWhiteSpace(loc) &&
                    Path.GetFileName(loc).StartsWith("EditorScripts_", StringComparison.OrdinalIgnoreCase))
                {
                    var ts = File.GetLastWriteTimeUtc(loc);
                    if (ts > bestTime) { best = t; bestTime = ts; }
                }
            }
            catch { }
        }

        return best;
    }

    static bool TryMigrateBehaviorToLatest(GameObject owner, ref Behavior b)
    {
        var fromType = b.GetType();
        var latest = TryResolveLoadedType(fromType.FullName!);
        if (latest == null || latest == fromType) return false;

        Behavior? nb = null;
        try { nb = (Behavior)Activator.CreateInstance(latest)!; }
        catch { return false; }

        // copy simple public props by name/type (best effort)
        var srcProps = fromType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var dstProps = latest.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                             .ToDictionary(p => p.Name);

        foreach (var sp in srcProps)
        {
            if (!sp.CanRead || sp.GetIndexParameters().Length != 0) continue;
            if (!dstProps.TryGetValue(sp.Name, out var dp) || dp.SetMethod == null) continue;
            if (!dp.PropertyType.IsAssignableFrom(sp.PropertyType)) continue;

            try { dp.SetValue(nb, sp.GetValue(b)); } catch { }
        }

        // swap on the GameObject
        owner.RemoveBehavior(b);
        owner.AddBehavior(nb);
        b = nb;
        return true;
    }

    void ShowInfo(string msg)
    {
        var win = this.GetVisualRoot() as Window;
        var alert = new Window
        {
            Title = "Info",
            Width = 420,
            Height = 200,
            Content = new TextBlock { Text = msg, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        alert.ShowDialog(win);
    }

    // Add a long-lived collectible ALC for persisted editor scripts
    static AssemblyLoadContext s_persistedScriptsAlc =
        new AssemblyLoadContext("EditorScriptsPersisted", isCollectible: true);

    // Track loaded paths to avoid double loads
    static readonly HashSet<string> s_loadedDlls = new(StringComparer.OrdinalIgnoreCase);
    static bool s_triedLoadPersisted;

    static IEnumerable<string> EditorScriptDllFolders()
    {
        var p = ProjectService.Current;
        if (p == null) yield break;

        if (!string.IsNullOrWhiteSpace(p.BuildsPath))
        {
            var dir = Path.Combine(p.BuildsPath, "EditorScripts");
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    // Read file with delete sharing; load from memory into a collectible ALC.
    static void TryLoadEditorDll(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return;
            if (!s_loadedDlls.Add(full)) return;

            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;

            // (optional) try load PDB for better stack traces
            Stream? pdb = null;
            var pdbPath = Path.ChangeExtension(full, ".pdb");
            if (File.Exists(pdbPath))
            {
                pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
            }

            s_persistedScriptsAlc.LoadFromStream(ms, pdb);  // <- no file lock
            Game_Engine.Core.Log.Info($"Loaded editor script assembly (memory): {full}");
        }
        catch
        {
            // swallow – bad DLLs shouldn’t break the UI
        }
    }

    static void EnsurePersistedEditorScriptsLoaded()
    {
        if (s_triedLoadPersisted) return;
        s_triedLoadPersisted = true;

        foreach (var dir in EditorScriptDllFolders())
            foreach (var dll in Directory.EnumerateFiles(dir, "EditorScripts_*.dll", SearchOption.TopDirectoryOnly))
                TryLoadEditorDll(dll);
    }

    // ---------- Undo snapshots (begin/commit) ----------
    readonly Dictionary<(object target, PropertyInfo prop), object?> _editStart = new();

    static object? SnapshotValue(Type t, object? v)
    {
        if (v is null) return null;
        if (t == typeof(CoreVector3) && v is CoreVector3 vv)
            return new CoreVector3(vv.X, vv.Y, vv.Z);
        return v; // structs, enums, numbers, strings, Mesh refs
    }

    // Try to create your engine texture from a file path using a few common patterns.
    // If nothing matches, we return null; the Inspector will still show a preview.
    private static Texture2D? TryCreateEngineTextureFromPath(string path, Bitmap? bmp)
    {
        var t = typeof(Texture2D);

        //  static FromFile(string)
        var m = t.GetMethod("FromFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (m != null) return (Texture2D?)m.Invoke(null, new object?[] { path });

        //  static Load(string)
        m = t.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (m != null) return (Texture2D?)m.Invoke(null, new object?[] { path });

        //  ctor(string)
        var ctorPath = t.GetConstructor(new[] { typeof(string) });
        if (ctorPath != null) return (Texture2D?)ctorPath.Invoke(new object?[] { path });

        //  static FromBytes(byte[])
        if (bmp != null)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms); // PNG-encoded bytes
            var bytes = ms.ToArray();

            m = t.GetMethod("FromBytes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(byte[]) }, modifiers: null);
            if (m != null) return (Texture2D?)m.Invoke(null, new object?[] { bytes });

            //  static Load(byte[])
            m = t.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(byte[]) }, modifiers: null);
            if (m != null) return (Texture2D?)m.Invoke(null, new object?[] { bytes });
        }

        // No compatible API found
        return null;
    }

    // Load preview (Avalonia Bitmap) and try to build an engine Texture2D.
    // Now supports PNG/JPG/BMP/TGA/DDS. For TGA/DDS we decode to RGBA,
    // build a WriteableBitmap (preview), then feed PNG bytes to Texture2D.FromBytes via TryCreateEngineTextureFromPath.
    private static (Texture2D? tex, IImage? preview) TryLoadTexture2D(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                using var fs = File.OpenRead(path);
                var bmp = new Bitmap(fs);                 // preview for UI
                var tex = TryCreateEngineTextureFromPath(path, bmp); // engine texture
                return (tex, bmp);
            }

            if (ext is ".tga" or ".targa")
            {
                var bytes = File.ReadAllBytes(path);
                var (w, h, rgba) = DecodeTgaToRgba(bytes);
                var wb = (WriteableBitmap)RgbaToWriteableBitmap(w, h, rgba);
                var tex = TryCreateEngineTextureFromPath(path, wb);  // will call FromBytes(PNG) using wb.Save(...)
                return (tex, wb);
            }

            if (ext == ".dds")
            {
                var bytes = File.ReadAllBytes(path);
                var (w, h, rgba) = DecodeDdsToRgba(bytes);
                var wb = (WriteableBitmap)RgbaToWriteableBitmap(w, h, rgba);
                var tex = TryCreateEngineTextureFromPath(path, wb);  // FromBytes(PNG)
                return (tex, wb);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    static bool ValueEquals(Type t, object? a, object? b)
    {
        if (t == typeof(CoreVector3) && a is CoreVector3 va && b is CoreVector3 vb)
            return va.X == vb.X && va.Y == vb.Y && va.Z == vb.Z;
        return Equals(a, b);
    }

    void BeginPropertyEdit(object target, PropertyInfo p)
    {
        _editStart[(target, p)] = SnapshotValue(p.PropertyType, p.GetValue(target));
    }

    void CommitPropertyEdit(object target, PropertyInfo p)
    {
        var key = (target, p);
        var oldVal = _editStart.TryGetValue(key, out var snap)
            ? snap
            : SnapshotValue(p.PropertyType, p.GetValue(target));
        var newVal = SnapshotValue(p.PropertyType, p.GetValue(target));
        _editStart.Remove(key);

        if (!ValueEquals(p.PropertyType, oldVal, newVal))
            Game_Engine.Core.UndoService.Exec(
                new Game_Engine.Core.PropertyChangeCmd(target, p, oldVal, newVal));
        else
            Game_Engine.Core.SceneService.NotifyChanged();
    }

    public InspectorPanel()
    {
        InitializeComponent();

        // start with whatever is currently selected
        _target = SelectionService.Current;

        // Rebuild the UI when global selection changes (unless locked)
        SelectionService.Changed += () =>
        {
            if (_isLocked) return;
            _target = SelectionService.Current;
            BuildUI(_target);
        };

        // Lock toggle wiring
        if (this.FindControl<ToggleButton>("LockToggle") is { } lockBtn)
        {
            lockBtn.IsChecked = false;

            lockBtn.Checked += (_, __) =>
            {
                _isLocked = true;
                if (_target is null) _target = SelectionService.Current;
                BuildUI(_target);
            };

            lockBtn.Unchecked += (_, __) =>
            {
                _isLocked = false;
                _target = SelectionService.Current;
                BuildUI(_target);
            };
        }
        // Load persisted editor script dlls when a project opens and on first run
        ProjectService.ProjectOpened += () => { s_triedLoadPersisted = false; EnsurePersistedEditorScriptsLoaded(); InvalidateScriptCache(); };
        EnsurePersistedEditorScriptsLoaded(); // first-time (app just launched)

        ProjectService.ProjectClosed += InvalidateScriptCache;
        ProjectService.Changed += InvalidateScriptCache;

        BuildUI(_target);
    }

    // ----------------- UI build -----------------
    void BuildUI(GameObject? go)
    {
        Host.Children.Clear();

        if (go is null)
        {
            Host.Children.Add(new TextBlock { Text = "No selection", Opacity = 0.6, Margin = new Thickness(6) });
            return;
        }

        // ---- Name -----------------------------------------------------------
        Host.Children.Add(SectionHeader("Name"));
        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        nameBox.Bind(TextBox.TextProperty, new Binding("Name") { Source = go, Mode = BindingMode.TwoWay });

        var pName = typeof(GameObject).GetProperty(nameof(GameObject.Name))!;
        nameBox.GotFocus += (_, __) => BeginPropertyEdit(go, pName);
        nameBox.LostFocus += (_, __) => CommitPropertyEdit(go, pName);

        Host.Children.Add(nameBox);

        // ---- Transform (mandatory) -----------------------------------------
        Host.Children.Add(SectionHeader("Transform"));
        Host.Children.Add(EditorForTransform(go.Transform));

        // ---- Add Component --------------------------------------------------
        // Built-ins = engine/editor components only (exclude any Script assemblies)
        var builtInTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !IsEditorScriptAssembly(a))
            .SelectMany(LoadableTypes)
            .Where(t => t != null && t.IsClass && !t.IsAbstract && typeof(Behavior).IsAssignableFrom(t))
            .Where(t => t != typeof(CoreTransform))
            .OrderBy(t => t.Name)
            .ToList();

        // Project scripts discovered from source files
        var scriptInfos = DiscoverProjectBehaviorScripts();
        scriptInfos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var choices = new List<ComboItem>();
        var scriptFullNames = new HashSet<string>(scriptInfos.Select(s => s.FullName), StringComparer.Ordinal);

        // Scripts first — always labeled as [Script] (or [Script: source only])
        foreach (var s in scriptInfos)
        {
            var loaded = TryResolveLoadedType(s.FullName);
            var label = (loaded != null) ? $"{s.Name}  [Script]" : $"{s.Name}  [Script: source only]";
            choices.Add(new ComboItem { Display = label, Type = loaded, Script = s });
        }

        // Built-ins after — skip anything that matches a script type FullName
        foreach (var t in builtInTypes)
        {
            var fn = t.FullName ?? t.Name;
            if (scriptFullNames.Contains(fn)) continue; // avoid duplicate entries
            choices.Add(new ComboItem { Display = t.Name, Type = t, Script = null });
        }


        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var typeBox = new ComboBox
        {
            Width = 260,
            ItemsSource = choices,
            SelectedIndex = choices.Count > 0 ? 0 : -1,
            DisplayMemberBinding = new Binding(nameof(ComboItem.Display)),
        };
        var addBtn = new Button { Content = "Add Component", IsEnabled = choices.Count > 0 };

        addBtn.Click += (_, __) =>
        {
            var sel = typeBox.SelectedItem as ComboItem;
            if (sel == null) return;

            if (sel.Type != null)
            {
                try
                {
                    var inst = (Behavior)Activator.CreateInstance(sel.Type)!;
                    go.AddBehavior(inst);
                    SceneService.NotifyChanged();
                    BuildUI(go);
                }
                catch (Exception ex)
                {
                    ShowInfo("Failed to add component:\n" + ex.Message);
                }
            }
            else if (sel.Script != null)
            {
                ShowInfo("That script exists in your project but isn’t compiled into the editor yet:\n\n" +
                         sel.Script.FullName + "\n\nOpen it, add it to your editor solution, and build so it becomes available.");
                try { ScriptEditorWindow.Open(OwnerWindow, sel.Script.FilePath); } catch { }
            }
        };

        addRow.Children.Add(typeBox);
        addRow.Children.Add(addBtn);
        Host.Children.Add(addRow);

        // ---- Other behaviors -----------------------------------------------
        foreach (var b in go.Behaviors.ToList())
            Host.Children.Add(EditorForBehavior(go, b));
    }


    private static IEnumerable<Type> LoadableTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types!.Where(t => t is not null)!; }
        catch { return Array.Empty<Type>(); }
    }

    // UI-only preview cache for Texture2D properties
    // key: owner object -> (prop -> IImage)
    static readonly ConditionalWeakTable<object, Dictionary<PropertyInfo, IImage>> _texPreviewCache = new();

    static IImage? GetCachedPreview(object owner, PropertyInfo prop)
        => _texPreviewCache.TryGetValue(owner, out var map) && map.TryGetValue(prop, out var img) ? img : null;

    static void SetCachedPreview(object owner, PropertyInfo prop, IImage img)
    {
        var map = _texPreviewCache.GetOrCreateValue(owner);
        map[prop] = img;
    }

    static void ClearCachedPreview(object owner, PropertyInfo prop)
    {
        if (_texPreviewCache.TryGetValue(owner, out var map) && map.Remove(prop, out var img))
            (img as IDisposable)?.Dispose();
    }


    Control SectionHeader(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 6, 0, 2)
    };

    Control EditorForTransform(CoreTransform t)
    {
        var grid = new Avalonia.Controls.Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
        };

        grid.Children.Add(new TextBlock { Text = "Position", Margin = new Thickness(0, 0, 8, 6) }.Place(0, 0));
        grid.Children.Add(new TextBlock { Text = "Rotation", Margin = new Thickness(0, 0, 8, 6) }.Place(0, 1));
        grid.Children.Add(new TextBlock { Text = "Scale", Margin = new Thickness(0, 0, 8, 0) }.Place(0, 2));

        var pPos = typeof(CoreTransform).GetProperty(nameof(CoreTransform.Position))!;
        var pRot = typeof(CoreTransform).GetProperty(nameof(CoreTransform.Rotation))!;
        var pScl = typeof(CoreTransform).GetProperty(nameof(CoreTransform.Scale))!;

        grid.Children.Add(Vector3EditorWithUndo(t, pPos).Place(1, 0, columnSpan: 3));
        grid.Children.Add(Vector3EditorWithUndo(t, pRot).Place(1, 1, columnSpan: 3));
        grid.Children.Add(Vector3EditorWithUndo(t, pScl).Place(1, 2, columnSpan: 3));
        return grid;
    }

    // Simple numeric binder (still useful for Vector3 fields)
    TextBox BoundNumber(object source, string path, Type targetType)
    {
        var tb = new TextBox { Width = 70 };
        tb.Bind(TextBox.TextProperty, new Binding(path)
        {
            Source = source,
            Mode = BindingMode.TwoWay,
            Converter = NumberConverter.Instance,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        return tb;
    }

    // A Vector3 editor that records a single undo step against property 'p' on owner 'owner'
    Control Vector3EditorWithUndo(object owner, PropertyInfo p)
    {
        var v = (CoreVector3)(p.GetValue(owner) ?? new CoreVector3());
        if (!ReferenceEquals(v, p.GetValue(owner))) p.SetValue(owner, v);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };

        TextBox Make(string comp)
        {
            var tb = BoundNumber(v, comp, typeof(double));
            tb.GotFocus += (_, __) => BeginPropertyEdit(owner, p);
            tb.LostFocus += (_, __) => CommitPropertyEdit(owner, p);
            tb.PropertyChanged += (_, __) => Game_Engine.Core.SceneService.NotifyChanged(); // live repaint
            return tb;
        }

        row.Children.Add(Make(nameof(CoreVector3.X)));
        row.Children.Add(Make(nameof(CoreVector3.Y)));
        row.Children.Add(Make(nameof(CoreVector3.Z)));
        return row;
    }

    Control EditorForBehavior(GameObject owner, Behavior b)
    {
        // --- outer container (border -> vertical stack) ---
        var outer = new StackPanel { Spacing = 6 };

        // make sure we're showing the freshest type
        TryMigrateBehaviorToLatest(owner, ref b);

        // Header row: [Enabled] [Title] [Remove]
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var enabled = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        enabled.Bind(CheckBox.IsCheckedProperty,
            new Binding(nameof(Behavior.Enabled)) { Source = b, Mode = BindingMode.TwoWay });

        var pEnabled = typeof(Behavior).GetProperty(nameof(Behavior.Enabled))!;
        enabled.GotFocus += (_, __) => BeginPropertyEdit(b, pEnabled);
        enabled.IsCheckedChanged += (_, __) => { SceneService.NotifyChanged(); CommitPropertyEdit(b, pEnabled); };

        var title = new TextBlock { Text = b.GetType().Name, VerticalAlignment = VerticalAlignment.Center };

        var remove = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Right };
        remove.Click += (_, __) =>
        {
            owner.RemoveBehavior(b);
            SceneService.NotifyChanged();
            BuildUI(owner);
        };

        header.Children.Add(enabled);
        header.Children.Add(title);
        header.Children.Add(remove);
        outer.Children.Add(header);

        // MeshCollider extra UI
        if (b is MeshCollider mc)
            outer.Children.Add(MeshColliderTargetRow(owner, mc));

        // --------- BODY: custom inspector first, else default ----------
        Control body = TryBuildCustomInspectorUI(b, out var custom) && custom != null
            ? custom
            : DefaultPropsPanel(b); // builds rows from InspectableProps(...)

        // disable body when component disabled
        var bodyHost = new StackPanel { Spacing = 8 };
        bodyHost.Bind(IsEnabledProperty,
            new Binding(nameof(Behavior.Enabled)) { Source = b, Mode = BindingMode.OneWay });
        bodyHost.Children.Add(body);

        outer.Children.Add(bodyHost);

        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = outer
        };
    }


    // Build a stable "Root/Child/SubChild" path for a GO
    static string BuildPath(GameObject go)
    {
        if (go == null) return string.Empty;
        var stack = new Stack<string>();
        var n = go;
        while (n != null) { stack.Push(n.Name ?? "GameObject"); n = n.Parent; }
        return string.Join("/", stack.ToArray());
    }

    readonly struct MFEntry
    {
        public readonly string Key;   // path + "#mf:ord"
        public readonly string Path;  // pretty (base path only)
        public readonly MeshFilter MF;
        public MFEntry(string key, string path, MeshFilter mf) { Key = key; Path = path; MF = mf; }
        public override string ToString() => Key; // or Path  cleaner UI
    }

    // Enumerate every MeshFilter in the scene with its hierarchy path
    static IEnumerable<MFEntry> EnumerateMeshFilters()
    {
        var stack = new Stack<GameObject>();
        foreach (var r in SceneService.Root) stack.Push(r);

        while (stack.Count > 0)
        {
            var n = stack.Pop();
            var filters = n.Behaviors.OfType<MeshFilter>().ToList();
            for (int i = 0; i < filters.Count; i++)
            {
                var mf = filters[i];
                var basePath = BuildPath(n);
                var key = $"{basePath}#mf:{i}";
                yield return new MFEntry(key, basePath, mf);
            }
            for (int i = 0; i < n.Children.Count; i++) stack.Push(n.Children[i]);
        }
    }


    Control MaterialEditor(object owner, PropertyInfo prop)
    {
        // Best-effort: project-relative if possible, otherwise absolute.
        // SceneView never reads this; it's only for serialization.
        static string NormalizePathForSave(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                var abs = Path.GetFullPath(path);
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var root = Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return Path.GetRelativePath(root, abs);
                }
                return abs; // ok to save absolute if not under project
            }
            catch { return path; }
        }
        var mat = (Material?)prop.GetValue(owner);
        if (mat is null) { mat = new Material(); prop.SetValue(owner, mat); }

        // --- helper (only used to store a nicer path; does NOT change loading) ---
        string MakeProjectRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var abs = Path.GetFullPath(path);
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var root = Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return Path.GetRelativePath(root, abs);
                }
                return abs; // fallback: absolute is fine
            }
            catch { return path; }
        }

        var previews = new Dictionary<MaterialTexture, IImage>();

        var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8) };
        var root = new StackPanel { Spacing = 8 };
        box.Child = root;

        var slotsPanel = new StackPanel { Spacing = 4 };

        // ---------------- Header ----------------
        var hdr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        hdr.Children.Add(new TextBlock { Text = "Material", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });

        var btnImport = new Button { Content = "Import…" };
        btnImport.Click += async (_, __) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import textures",
                AllowMultiple = true,
                Filters =
                {
                    new FileDialogFilter { Name = "Images", Extensions = { "png","jpg","jpeg","bmp","tga","dds","tif","tiff" } },
                    new FileDialogFilter { Name = "All files", Extensions = { "*" } }
                }
            };
            var files = await dlg.ShowAsync(OwnerWindow);
            if (files is { Length: > 0 }) AddFiles(files);
        };
        hdr.Children.Add(btnImport);

        var btnClear = new Button { Content = "Clear" };
        btnClear.Click += (_, __) =>
        {
            foreach (var img in previews.Values)
                (img as IDisposable)?.Dispose();
            previews.Clear();

            mat.Textures.Clear();
            SceneService.NotifyChanged();
            Rebuild();
        };
        hdr.Children.Add(btnClear);

        root.Children.Add(hdr);

        // ---------------- Drop area ----------------
        var drop = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            Child = new TextBlock { Text = "Drop textures here (png/jpg/bmp/tga/dds)…", Opacity = .7 }
        };
        DragDrop.SetAllowDrop(drop, true);

        drop.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            if (e.Data.Contains(DataFormats.FileNames) || e.Data.Contains(DataFormats.Files))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        });

        drop.AddHandler(DragDrop.DropEvent, async (s, e) =>
        {
            var paths = new List<string>();

            if (e.Data.Contains(DataFormats.FileNames))
            {
                var names = e.Data.GetFileNames();
                if (names != null) paths.AddRange(names);
            }

            if (e.Data.Contains(DataFormats.Files) && e.Data.Get(DataFormats.Files) is IEnumerable<IStorageItem> items)
            {
                foreach (var it in items)
                {
                    if (it is IStorageFile f)
                    {
                        var local = f.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(local))
                        {
                            paths.Add(local!);
                        }
                        else
                        {
                            // only RECORD the path for save
                            var tmpDir = ProjectService.Current?.TempPath ?? Path.GetTempPath();
                            Directory.CreateDirectory(tmpDir);
                            var dst = Path.Combine(tmpDir, f.Name);
                            await using var src = await f.OpenReadAsync();
                            await using var outFs = File.Create(dst);
                            await src.CopyToAsync(outFs);
                            paths.Add(dst);
                        }
                    }
                }
            }

            if (paths.Count > 0)
            {
                AddFiles(paths.Where(File.Exists));
                e.Handled = true;
            }
        });

        root.Children.Add(drop);

        // slots list
        root.Children.Add(slotsPanel);

        // ---------------- helpers for face mask UI ----------------
        const int FaceRight = 1;   // +X
        const int FaceLeft = 2;   // -X
        const int FaceTop = 4;     // +Y
        const int FaceBottom = 8;  // -Y
        const int FaceBack = 16;   // +Z
        const int FaceFront = 32;  // -Z
        const int FaceAll = FaceRight | FaceLeft | FaceTop | FaceBottom | FaceBack | FaceFront;

        static int GuessFaceMaskFromName(string nameNoExtLower)
        {
            if (nameNoExtLower.EndsWith("_px") || nameNoExtLower.Contains("right")) return FaceRight;
            if (nameNoExtLower.EndsWith("_nx") || nameNoExtLower.Contains("left")) return FaceLeft;
            if (nameNoExtLower.EndsWith("_py") || nameNoExtLower.Contains("top") || nameNoExtLower.Contains("up"))
                return FaceTop;
            if (nameNoExtLower.EndsWith("_ny") || nameNoExtLower.Contains("bottom") || nameNoExtLower.Contains("down"))
                return FaceBottom;
            if (nameNoExtLower.EndsWith("_pz") || nameNoExtLower.Contains("back")) return FaceBack;
            if (nameNoExtLower.EndsWith("_nz") || nameNoExtLower.Contains("front")) return FaceFront;
            return -1; // all
        }

        // -------- Row builder -------------------------------------------------------
        Control SlotRow(MaterialTexture slot)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

            if (previews.TryGetValue(slot, out var img))
                row.Children.Add(new Image { Source = img, Width = 32, Height = 32, Stretch = Stretch.UniformToFill });
            else
                row.Children.Add(new Border { Width = 32, Height = 32, Background = Brushes.Gray, Opacity = .25, CornerRadius = new CornerRadius(4) });

            // filename with tooltip containing the stored path
            var nameBlock = new TextBlock { Text = slot.Name ?? "(texture)", VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(nameBlock, string.IsNullOrWhiteSpace(slot.SourcePath) ? "(no path set)" : slot.SourcePath);
            row.Children.Add(nameBlock);

            // Usage
            var usageBox = new ComboBox
            {
                Width = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = Enum.GetValues(typeof(MaterialTexture.TexUsage))
                                 .Cast<MaterialTexture.TexUsage>()
                                 .ToArray(),
                SelectedItem = slot.Usage
            };
            ToolTip.SetTip(usageBox, "How this texture should be used (Albedo, AO, Normal, Specular, …)");
            usageBox.SelectionChanged += (_, __) =>
            {
                if (usageBox.SelectedItem is MaterialTexture.TexUsage u && u != slot.Usage)
                {
                    slot.Usage = u;
                    SceneService.NotifyChanged();
                    Rebuild();
                }
            };
            row.Children.Add(usageBox);

            // Faces (per-face mask UI)
            var facesWrap = new WrapPanel { ItemSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = "Faces:", VerticalAlignment = VerticalAlignment.Center, Opacity = .7 });
            row.Children.Add(facesWrap);

            var cbAll = new CheckBox { Content = "All" };
            var cbR = new CheckBox { Content = "Right" };
            var cbL = new CheckBox { Content = "Left" };
            var cbT = new CheckBox { Content = "Top" };
            var cbB = new CheckBox { Content = "Bottom" };
            var cbBack = new CheckBox { Content = "Back" };
            var cbFront = new CheckBox { Content = "Front" };
            facesWrap.Children.Add(cbAll);
            facesWrap.Children.Add(cbR);
            facesWrap.Children.Add(cbL);
            facesWrap.Children.Add(cbT);
            facesWrap.Children.Add(cbB);
            facesWrap.Children.Add(cbBack);
            facesWrap.Children.Add(cbFront);

            bool updating = false;

            void RefreshFaceChecks()
            {
                updating = true;
                int m = (int)slot.FaceMask;
                bool all = (m < 0) || ((m & FaceAll) == FaceAll);
                cbAll.IsChecked = all;
                cbR.IsChecked = all || ((m & FaceRight) != 0);
                cbL.IsChecked = all || ((m & FaceLeft) != 0);
                cbT.IsChecked = all || ((m & FaceTop) != 0);
                cbB.IsChecked = all || ((m & FaceBottom) != 0);
                cbBack.IsChecked = all || ((m & FaceBack) != 0);
                cbFront.IsChecked = all || ((m & FaceFront) != 0);

                bool lockOthers = all;
                cbR.IsEnabled = cbL.IsEnabled = cbT.IsEnabled = cbB.IsEnabled = cbBack.IsEnabled = cbFront.IsEnabled = !lockOthers;
                updating = false;
            }

            void WriteMaskFromChecks()
            {
                if (updating) return;

                if (cbAll.IsChecked == true)
                {
                    slot.FaceMask = (MaterialTexture.CubeFaceMask)(-1);
                }
                else
                {
                    int m = 0;
                    if (cbR.IsChecked == true) m |= FaceRight;
                    if (cbL.IsChecked == true) m |= FaceLeft;
                    if (cbT.IsChecked == true) m |= FaceTop;
                    if (cbB.IsChecked == true) m |= FaceBottom;
                    if (cbBack.IsChecked == true) m |= FaceBack;
                    if (cbFront.IsChecked == true) m |= FaceFront;

                    // normalize: all faces -> -1; otherwise keep explicit mask (incl. 0)
                    slot.FaceMask = (MaterialTexture.CubeFaceMask)((m == FaceAll) ? -1 : m);
                }
                SceneService.NotifyChanged();
                Rebuild();

                RefreshFaceChecks();
            }

            // All handlers — IMPORTANT: don’t refresh before writing mask
            cbAll.Checked += (_, __) =>
            {
                if (updating) return;
                slot.FaceMask = (MaterialTexture.CubeFaceMask)(-1);
                SceneService.NotifyChanged();
                Rebuild();
                RefreshFaceChecks();
            };
            cbAll.Unchecked += (_, __) =>
            {
                if (updating) return;
                // Start with "none" so user can pick faces; enables the six boxes.
                slot.FaceMask = (MaterialTexture.CubeFaceMask)0;
                SceneService.NotifyChanged();
                Rebuild();
                RefreshFaceChecks();
            };

            foreach (var cb in new[] { cbR, cbL, cbT, cbB, cbBack, cbFront })
            {
                cb.Checked += (_, __) => WriteMaskFromChecks();
                cb.Unchecked += (_, __) => WriteMaskFromChecks();
            }

            RefreshFaceChecks();

            // Reorder / Remove
            var up = new Button { Content = "↑" };
            up.Click += (_, __) =>
            {
                int i = mat.Textures.IndexOf(slot);
                if (i > 0)
                {
                    (mat.Textures[i - 1], mat.Textures[i]) = (mat.Textures[i], mat.Textures[i - 1]);
                    SceneService.NotifyChanged();
                    Rebuild();
                }
            };
            row.Children.Add(up);

            var down = new Button { Content = "↓" };
            down.Click += (_, __) =>
            {
                int i = mat.Textures.IndexOf(slot);
                if (i >= 0 && i < mat.Textures.Count - 1)
                {
                    (mat.Textures[i + 1], mat.Textures[i]) = (mat.Textures[i], mat.Textures[i + 1]);
                    SceneService.NotifyChanged();
                    Rebuild();
                }
            };
            row.Children.Add(down);

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, __) =>
            {
                if (previews.TryGetValue(slot, out var p))
                {
                    (p as IDisposable)?.Dispose();
                    previews.Remove(slot);
                }
                mat.Textures.Remove(slot);
                SceneService.NotifyChanged();
                Rebuild();
            };
            row.Children.Add(remove);

            return row;
        }

        void Rebuild()
        {
            slotsPanel.Children.Clear();
            foreach (var s in mat.Textures)
                slotsPanel.Children.Add(SlotRow(s));
        }

        void AddFiles(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds")) continue;

                var (tex, previewBmp) = TryLoadTexture2D(f);
                var name = Path.GetFileName(f);
                var nameNoExtLower = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();

                var slot = new MaterialTexture
                {
                    Name = name,
                    Texture = tex, // may be null if load failed; path still saved
                    Usage = GuessUsageFromName(Path.GetFileNameWithoutExtension(f)),
                    FaceMask = (MaterialTexture.CubeFaceMask)GuessFaceMaskFromName(nameNoExtLower),
                    SourcePath = /* keep your helper */ MakeProjectRelative(f)
                };

                mat.Textures.Add(slot);
                if (previewBmp is not null) previews[slot] = previewBmp;
            }

            SceneService.NotifyChanged();
            Rebuild();
        }


        static MaterialTexture.TexUsage GuessUsageFromName(string nameRaw)
        {
            var n = nameRaw.ToLowerInvariant();

            if (n.Contains("ao") || n.Contains("_ao") || n.Contains("ambientocclusion")) return MaterialTexture.TexUsage.AmbientOcclusion;
            if (n.Contains("nrm") || n.Contains("_n") || n.Contains("normal")) return MaterialTexture.TexUsage.Normal;
            if (n.Contains("spec") || n.Contains("_s") || n.Contains("gloss")) return MaterialTexture.TexUsage.Specular;
            if (n.Contains("rough") || n.Contains("rgh")) return MaterialTexture.TexUsage.Roughness;
            if (n.Contains("metal") || n.Contains("mtl")) return MaterialTexture.TexUsage.Metallic;
            if (n.Contains("emit") || n.Contains("emiss")) return MaterialTexture.TexUsage.Emissive;
            if (n.Contains("detail") || n.Contains("dirt") || n.Contains("grunge")) return MaterialTexture.TexUsage.Detail;

            if (n.Contains("albedo") || n.Contains("basecolor") || n.Contains("base") || n.Contains("diff") || n.EndsWith("_c"))
                return MaterialTexture.TexUsage.Albedo;

            return MaterialTexture.TexUsage.Albedo;
        }

        Rebuild();
        return box;
    }

    Control Texture2DEditor(object owner, PropertyInfo prop)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        // ---------- helpers ----------
        string MakeProjectRelative(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            try
            {
                var abs = Path.GetFullPath(fullPath);
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var root = Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return Path.GetRelativePath(root, abs);
                }
                return abs; // fallback
            }
            catch { return fullPath; }
        }

        string EnsureInProject(string fullPath)
        {
            // keep if already under project
            try
            {
                var proj = ProjectService.Current;
                if (proj == null) return fullPath;
                var abs = Path.GetFullPath(fullPath);
                var root = Path.GetFullPath(proj.RootPath);
                if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return abs;

                var assetsRoot = string.IsNullOrWhiteSpace(proj.AssetsPath) ? proj.RootPath : proj.AssetsPath;
                var importDir = Path.Combine(assetsRoot, "Imported");
                Directory.CreateDirectory(importDir);

                var dst = Path.Combine(importDir, Path.GetFileName(fullPath));
                if (File.Exists(dst))
                {
                    var name = Path.GetFileNameWithoutExtension(dst);
                    var ext = Path.GetExtension(dst);
                    int i = 1;
                    while (File.Exists(dst = Path.Combine(importDir, $"{name}_{i}{ext}"))) i++;
                }
                File.Copy(fullPath, dst, false);
                return dst;
            }
            catch { return fullPath; }
        }

        // sibling string property convention: Texture -> TexturePath
        void TrySetSiblingPath(object target, PropertyInfo texProp, string projectRelPath)
        {
            var pathProp = target.GetType().GetProperty(texProp.Name + "Path",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pathProp != null && pathProp.CanWrite && pathProp.PropertyType == typeof(string))
            {
                pathProp.SetValue(target, projectRelPath);
            }
        }

        // ---------- UI ----------
        var preview = new Image { Stretch = Stretch.UniformToFill };
        var previewHost = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Child = preview
        };

        // Initial preview (cache first, then best-effort reflection)
        preview.Source = GetCachedPreview(owner, prop)
                         ?? (prop.GetValue(owner) is { } existing ? TryPreviewFromTextureObject(existing) : null);

        var choose = new Button { Content = "Choose…" };
        choose.Click += async (_, __) =>
        {
            var dlg = new OpenFileDialog
            {
                AllowMultiple = false,
                Filters =
                {
                    new FileDialogFilter { Name = "Images", Extensions = { "png","jpg","jpeg","bmp","tga","dds","tif","tiff" } }
                }
            };
            var files = await dlg.ShowAsync(OwnerWindow);
            var rawPath = files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawPath)) return;

            // Bring the file under the project so path is stable
            var inProj = EnsureInProject(rawPath);
            var (engineTex, bmp) = TryLoadTexture2D(inProj);
            if (engineTex == null && bmp == null)
            {
                Game_Engine.Core.Log.Warning($"Could not load texture from: {rawPath}");
                return;
            }

            var rel = MakeProjectRelative(inProj);

            BeginPropertyEdit(owner, prop);

            // set the texture object
            prop.SetValue(owner, engineTex);

            // set the sibling "TexturePath" property if present
            TrySetSiblingPath(owner, prop, rel);

            // update preview + cache
            if (bmp != null)
            {
                preview.Source = bmp;
                SetCachedPreview(owner, prop, bmp);
            }

            SceneService.NotifyChanged();
            CommitPropertyEdit(owner, prop);
        };

        var clear = new Button { Content = "Clear" };
        clear.Click += (_, __) =>
        {
            BeginPropertyEdit(owner, prop);

            // clear texture
            prop.SetValue(owner, null);

            // clear sibling path if present
            TrySetSiblingPath(owner, prop, null);

            // Clear UI + cache first
            ClearCachedPreview(owner, prop);
            preview.Source = null;

            SceneService.NotifyChanged();
            CommitPropertyEdit(owner, prop);
        };

        row.Children.Add(previewHost);
        row.Children.Add(choose);
        row.Children.Add(clear);
        return row;
    }

    Control MeshColliderTargetRow(GameObject owner, MeshCollider mc)
    {
        var wrap = new StackPanel { Spacing = 6 };
        wrap.Children.Add(new TextBlock { Text = "Mesh Filters", FontWeight = FontWeight.Bold });

        var status = new TextBlock { Opacity = .7 };
        void RefreshStatus()
        {
            var count = mc.TargetFilters?.Count ?? 0;
            status.Text = count == 0 ? "Targets: (none)" : $"Targets: {count}";
        }
        RefreshStatus();
        wrap.Children.Add(status);

        // All MeshFilters in scene (each entry is a unique "path#mf:N" key)
        var all = EnumerateMeshFilters().ToList();

        var list = new ListBox
        {
            ItemsSource = all,
            SelectionMode = SelectionMode.Multiple,
            Width = 420,
            Height = 160
        };
        wrap.Children.Add(list);

        // Preselect items already targeted (by component ref or by saved Key)
        void SyncPreselect()
        {
            list.SelectedItems.Clear();
            foreach (var entry in all)
            {
                bool selected =
                    (mc.TargetFilters != null && mc.TargetFilters.Any(t => ReferenceEquals(t, entry.MF))) ||
                    (mc.TargetPaths != null && mc.TargetPaths.Any(k =>
                        string.Equals(k, entry.Key, StringComparison.OrdinalIgnoreCase)));

                if (selected) list.SelectedItems.Add(entry);
            }
        }
        SyncPreselect();

        // Buttons
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var addSel = new Button { Content = "Add Selected" };
        addSel.Click += (_, __) =>
        {
            var p = typeof(MeshCollider).GetProperty(nameof(MeshCollider.TargetPaths),
                        BindingFlags.Instance | BindingFlags.Public)!;

            BeginPropertyEdit(mc, p);
            foreach (MFEntry e in list.SelectedItems.OfType<MFEntry>())
                mc.AddTarget(e.MF);              // MeshCollider builds & stores the Key internally
            SceneService.NotifyChanged();
            CommitPropertyEdit(mc, p);

            RefreshStatus();
            SyncPreselect();
        };
        row.Children.Add(addSel);

        var remSel = new Button { Content = "Remove Selected" };
        remSel.Click += (_, __) =>
        {
            var p = typeof(MeshCollider).GetProperty(nameof(MeshCollider.TargetPaths),
                        BindingFlags.Instance | BindingFlags.Public)!;

            BeginPropertyEdit(mc, p);
            foreach (MFEntry e in list.SelectedItems.OfType<MFEntry>())
                mc.RemoveTarget(e.MF);           // removes by exact component; keys updated inside
            SceneService.NotifyChanged();
            CommitPropertyEdit(mc, p);

            RefreshStatus();
            SyncPreselect();
        };
        row.Children.Add(remSel);

        var addFromCurrentGO = new Button { Content = "Add From Selected GO" };
        addFromCurrentGO.Click += (_, __) =>
        {
            var sel = SelectionService.Current;
            if (sel == null) return;

            var p = typeof(MeshCollider).GetProperty(nameof(MeshCollider.TargetPaths),
                        BindingFlags.Instance | BindingFlags.Public)!;

            BeginPropertyEdit(mc, p);
            foreach (var mf in sel.Behaviors.OfType<MeshFilter>().Where(m => m.Enabled && m.Mesh != null))
                mc.AddTarget(mf);
            SceneService.NotifyChanged();
            CommitPropertyEdit(mc, p);

            RefreshStatus();
            SyncPreselect();
        };
        row.Children.Add(addFromCurrentGO);

        var clear = new Button { Content = "Clear All" };
        clear.Click += (_, __) =>
        {
            var p = typeof(MeshCollider).GetProperty(nameof(MeshCollider.TargetPaths),
                        BindingFlags.Instance | BindingFlags.Public)!;

            BeginPropertyEdit(mc, p);
            mc.ClearTargets();
            SceneService.NotifyChanged();
            CommitPropertyEdit(mc, p);

            RefreshStatus();
            SyncPreselect();
        };
        row.Children.Add(clear);

        var bindChk = new CheckBox { Content = "Bind To Each Target Transform", IsChecked = mc.BindToTargetTransform };
        bindChk.Checked += (_, __) => { mc.BindToTargetTransform = true; SceneService.NotifyChanged(); };
        bindChk.Unchecked += (_, __) => { mc.BindToTargetTransform = false; SceneService.NotifyChanged(); };
        row.Children.Add(bindChk);

        wrap.Children.Add(row);

        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = wrap
        };
    }



    // BGRA WriteableBitmap for Avalonia preview from RGBA bytes
    private static IImage RgbaToWriteableBitmap(int w, int h, byte[] rgba)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);

        using var fb = wb.Lock();
        unsafe
        {
            var dst = (byte*)fb.Address;
            fixed (byte* srcBase = rgba)
            {
                var src = srcBase;
                int count = w * h;
                for (int i = 0; i < count; i++)
                {
                    byte r = *src++; byte g = *src++; byte b = *src++; byte a = *src++;
                    *dst++ = b; *dst++ = g; *dst++ = r; *dst++ = a; // RGBA -> BGRA
                }
            }
        }
        return wb;
    }

    // Minimal TGA (24/32bpp truecolor; uncompressed or RLE)
    private static (int w, int h, byte[] rgba) DecodeTgaToRgba(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);

        byte idLength = br.ReadByte();
        byte colorMapType = br.ReadByte();
        byte imageType = br.ReadByte(); // 2 or 10
        br.ReadBytes(5);                 // color map spec
        br.ReadUInt16();                 // x origin
        br.ReadUInt16();                 // y origin
        ushort w = br.ReadUInt16();
        ushort h = br.ReadUInt16();
        byte bpp = br.ReadByte();        // 24/32
        byte desc = br.ReadByte();

        if (!(imageType == 2 || imageType == 10) || !(bpp == 24 || bpp == 32))
            throw new NotSupportedException("TGA: only 24/32bpp truecolor (RLE/uncompressed) supported.");
        if (idLength > 0) br.ReadBytes(idLength);
        if (colorMapType != 0) throw new NotSupportedException("TGA color-mapped not supported.");

        int cpp = bpp / 8;
        int count = w * h;
        var rgba = new byte[count * 4];

        void write(int i, byte b, byte g, byte r, byte a)
        {
            int o = i * 4;
            rgba[o + 0] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
        }

        if (imageType == 2)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = br.ReadByte(); byte g = br.ReadByte(); byte r = br.ReadByte();
                byte a = (cpp == 4) ? br.ReadByte() : (byte)255;
                write(i, b, g, r, a);
            }
        }
        else // RLE
        {
            int i = 0;
            while (i < count)
            {
                byte packet = br.ReadByte();
                int run = (packet & 0x7F) + 1;
                if ((packet & 0x80) != 0)
                {
                    byte b = br.ReadByte(); byte g = br.ReadByte(); byte r = br.ReadByte();
                    byte a = (cpp == 4) ? br.ReadByte() : (byte)255;
                    for (int k = 0; k < run; k++) write(i++, b, g, r, a);
                }
                else
                {
                    for (int k = 0; k < run; k++)
                    {
                        byte b = br.ReadByte(); byte g = br.ReadByte(); byte r = br.ReadByte();
                        byte a = (cpp == 4) ? br.ReadByte() : (byte)255;
                        write(i++, b, g, r, a);
                    }
                }
            }
        }

        // origin fix (bit 5)
        bool topLeft = (desc & 0x20) != 0;
        if (!topLeft)
        {
            int stride = w * 4;
            var row = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * stride, b = (h - 1 - y) * stride;
                Buffer.BlockCopy(rgba, a, row, 0, stride);
                Buffer.BlockCopy(rgba, b, rgba, a, stride);
                Buffer.BlockCopy(row, 0, rgba, b, stride);
            }
        }

        return (w, h, rgba);
    }

    // Minimal DDS (DXT1/3/5 compressed or 32-bit uncompressed)
    private static (int w, int h, byte[] rgba) DecodeDdsToRgba(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);

        uint magic = br.ReadUInt32(); // 'DDS '
        if (magic != 0x20534444) throw new InvalidDataException("Not a DDS.");

        int headerSize = br.ReadInt32(); // 124
        if (headerSize != 124) throw new InvalidDataException("Bad DDS header.");

        uint flags = br.ReadUInt32();
        int h = br.ReadInt32();
        int w = br.ReadInt32();
        int pitchOrLinear = br.ReadInt32();
        br.ReadInt32(); // depth
        br.ReadInt32(); // mip count
        br.ReadBytes(44);

        uint pfSize = br.ReadUInt32(); // 32
        uint pfFlags = br.ReadUInt32();
        uint fourCC = br.ReadUInt32();
        uint rgbBits = br.ReadUInt32();
        uint rMask = br.ReadUInt32();
        uint gMask = br.ReadUInt32();
        uint bMask = br.ReadUInt32();
        uint aMask = br.ReadUInt32();
        br.ReadBytes(16); // caps

        var rgba = new byte[w * h * 4];

        if ((pfFlags & 0x4) != 0) // FOURCC
        {
            int bw = Math.Max(1, (w + 3) / 4);
            int bh = Math.Max(1, (h + 3) / 4);
            if (fourCC == 0x31545844) // 'DXT1'
                DecompressDXT1(br.ReadBytes(pitchOrLinear > 0 ? pitchOrLinear : (bw * bh * 8)), w, h, rgba);
            else if (fourCC == 0x33545844) // 'DXT3'
                DecompressDXT3(br.ReadBytes(pitchOrLinear > 0 ? pitchOrLinear : (bw * bh * 16)), w, h, rgba);
            else if (fourCC == 0x35545844) // 'DXT5'
                DecompressDXT5(br.ReadBytes(pitchOrLinear > 0 ? pitchOrLinear : (bw * bh * 16)), w, h, rgba);
            else
                throw new NotSupportedException("DDS FourCC not supported.");
        }
        else
        {
            if (rgbBits != 32) throw new NotSupportedException("DDS: only 32-bit uncompressed supported.");
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint px = br.ReadUInt32();
                    byte r = (byte)((px & rMask) >> 16);
                    byte g = (byte)((px & gMask) >> 8);
                    byte b = (byte)((px & bMask) >> 0);
                    byte a = (byte)((px & aMask) >> 24);
                    int o = (y * w + x) * 4;
                    rgba[o + 0] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
                }
        }

        return (w, h, rgba);
    }

    private static void DecompressDXT1(byte[] data, int w, int h, byte[] rgba)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        int idx = 0;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                ushort c0 = BitConverter.ToUInt16(data, idx); idx += 2;
                ushort c1 = BitConverter.ToUInt16(data, idx); idx += 2;
                uint bits = BitConverter.ToUInt32(data, idx); idx += 4;

                Span<uint> pal = stackalloc uint[4];
                pal[0] = R5G6B5to8888(c0) | 0xFF000000u;
                pal[1] = R5G6B5to8888(c1) | 0xFF000000u;
                if (c0 > c1)
                {
                    pal[2] = Lerp8888(pal[0], pal[1], 1, 2);
                    pal[3] = Lerp8888(pal[0], pal[1], 2, 1);
                }
                else
                {
                    pal[2] = Lerp8888(pal[0], pal[1], 1, 1);
                    pal[3] = 0x00000000u;
                }

                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        uint sel = (bits >> (2 * (py * 4 + px))) & 0x3;
                        uint p = pal[(int)sel];
                        int o = (y * w + x) * 4;
                        rgba[o + 0] = (byte)((p >> 16) & 0xFF);
                        rgba[o + 1] = (byte)((p >> 8) & 0xFF);
                        rgba[o + 2] = (byte)(p & 0xFF);
                        rgba[o + 3] = (byte)((p >> 24) & 0xFF);
                    }
            }
    }

    private static void DecompressDXT3(byte[] data, int w, int h, byte[] rgba)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        int idx = 0;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                ulong alpha = BitConverter.ToUInt64(data, idx); idx += 8;

                ushort c0 = BitConverter.ToUInt16(data, idx); idx += 2;
                ushort c1 = BitConverter.ToUInt16(data, idx); idx += 2;
                uint bits = BitConverter.ToUInt32(data, idx); idx += 4;

                Span<uint> pal = stackalloc uint[4];
                pal[0] = R5G6B5to8888(c0);
                pal[1] = R5G6B5to8888(c1);
                pal[2] = Lerp8888(pal[0], pal[1], 1, 2);
                pal[3] = Lerp8888(pal[0], pal[1], 2, 1);

                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;

                        uint sel = (bits >> (2 * (py * 4 + px))) & 0x3;
                        uint p = pal[(int)sel];

                        int o = (y * w + x) * 4;
                        int a4 = (int)((alpha >> (4 * (py * 4 + px))) & 0xF);
                        byte a = (byte)((a4 << 4) | a4);

                        rgba[o + 0] = (byte)((p >> 16) & 0xFF);
                        rgba[o + 1] = (byte)((p >> 8) & 0xFF);
                        rgba[o + 2] = (byte)(p & 0xFF);
                        rgba[o + 3] = a;
                    }
            }
    }

    private static void DecompressDXT5(byte[] data, int w, int h, byte[] rgba)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        int idx = 0;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                byte a0 = data[idx++], a1 = data[idx++];
                ulong abits = 0;
                for (int i = 0; i < 6; i++) abits |= (ulong)data[idx++] << (8 * i);

                ushort c0 = BitConverter.ToUInt16(data, idx); idx += 2;
                ushort c1 = BitConverter.ToUInt16(data, idx); idx += 2;
                uint bits = BitConverter.ToUInt32(data, idx); idx += 4;

                Span<byte> apal = stackalloc byte[8];
                apal[0] = a0; apal[1] = a1;
                if (a0 > a1)
                {
                    apal[2] = (byte)((6 * a0 + 1 * a1) / 7);
                    apal[3] = (byte)((5 * a0 + 2 * a1) / 7);
                    apal[4] = (byte)((4 * a0 + 3 * a1) / 7);
                    apal[5] = (byte)((3 * a0 + 4 * a1) / 7);
                    apal[6] = (byte)((2 * a0 + 5 * a1) / 7);
                    apal[7] = (byte)((1 * a0 + 6 * a1) / 7);
                }
                else
                {
                    apal[2] = (byte)((4 * a0 + 1 * a1) / 5);
                    apal[3] = (byte)((3 * a0 + 2 * a1) / 5);
                    apal[4] = (byte)((2 * a0 + 3 * a1) / 5);
                    apal[5] = (byte)((1 * a0 + 4 * a1) / 5);
                    apal[6] = 0x00;
                    apal[7] = 0xFF;
                }

                Span<uint> pal = stackalloc uint[4];
                pal[0] = R5G6B5to8888(c0);
                pal[1] = R5G6B5to8888(c1);
                pal[2] = Lerp8888(pal[0], pal[1], 1, 2);
                pal[3] = Lerp8888(pal[0], pal[1], 2, 1);

                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;

                        int ai = (int)((abits >> (3 * (py * 4 + px))) & 0x7);
                        byte a = apal[ai];

                        uint sel = (bits >> (2 * (py * 4 + px))) & 0x3;
                        uint p = pal[(int)sel];

                        int o = (y * w + x) * 4;
                        rgba[o + 0] = (byte)((p >> 16) & 0xFF);
                        rgba[o + 1] = (byte)((p >> 8) & 0xFF);
                        rgba[o + 2] = (byte)(p & 0xFF);
                        rgba[o + 3] = a;
                    }
            }
    }

    private static uint R5G6B5to8888(ushort c)
    {
        uint r = (uint)((c >> 11) & 0x1F);
        uint g = (uint)((c >> 5) & 0x3F);
        uint b = (uint)(c & 0x1F);
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
        return (r << 16) | (g << 8) | b;
    }

    private static uint Lerp8888(uint a, uint b, int na, int nb)
    {
        uint r = ((a >> 16) * (uint)na + (b >> 16) * (uint)nb) / (uint)(na + nb);
        uint g = ((a >> 8) * (uint)na + (b >> 8) * (uint)nb) / (uint)(na + nb);
        uint bl = ((a >> 0) * (uint)na + (b >> 0) * (uint)nb) / (uint)(na + nb);
        return (r << 16) | (g << 8) | bl;
    }




    // ---  extract a preview from engine Texture2D -------------
    static IImage? TryPreviewFromTextureObject(object texObj)
    {
        try
        {
            var t = texObj.GetType();

            // Common string path property names
            foreach (var name in new[] { "Path", "FilePath", "SourcePath" })
            {
                if (t.GetProperty(name) is { } p && p.GetValue(texObj) is string s && !string.IsNullOrWhiteSpace(s) && File.Exists(s))
                    return new Avalonia.Media.Imaging.Bitmap(s);
            }

            // Method that returns a readable stream
            if (t.GetMethod("OpenRead", Type.EmptyTypes) is { } m && m.Invoke(texObj, null) is Stream s1)
                using (s1) return new Avalonia.Media.Imaging.Bitmap(s1);

            // Methods that return bytes
            foreach (var name in new[] { "GetBytes", "ToBytes" })
            {
                if (t.GetMethod(name, Type.EmptyTypes) is { } m2 && m2.Invoke(texObj, null) is byte[] bytes && bytes.Length > 0)
                    using (var ms = new MemoryStream(bytes)) return new Avalonia.Media.Imaging.Bitmap(ms);
            }
        }
        catch { /* best-effort */ }

        return null;
    }



    Control PropertyEditor(object target, PropertyInfo p)
    {
        var t = p.PropertyType;

        // ---- Mesh editor (None / Cube / etc.) --------------------------------
        if (t == typeof(Game_Engine.Core.Mesh))
        {
            var options = new[]
            {
                new PrimitiveChoice { Name = "None",     Factory = () => null },
                new PrimitiveChoice { Name = "Cube",     Factory = () => Game_Engine.Core.Mesh.CreateCube(1f) },
                new PrimitiveChoice { Name = "Quad",     Factory = () => Game_Engine.Core.Mesh.CreateQuad(1f, 1f) },
                new PrimitiveChoice { Name = "Plane",    Factory = () => Game_Engine.Core.Mesh.CreatePlane(2f, 2f, 16, 16) },
                new PrimitiveChoice { Name = "Sphere",   Factory = () => Game_Engine.Core.Mesh.CreateUvSphere(24, 16, 0.5f) },
                new PrimitiveChoice { Name = "Cylinder", Factory = () => Game_Engine.Core.Mesh.CreateCylinder(24, 0.5f, 1f, true) },
                new PrimitiveChoice { Name = "Cone",     Factory = () => Game_Engine.Core.Mesh.CreateCone(24, 0.5f, 1f, true) },
            };

            var cb = new ComboBox
            {
                Width = 160,
                ItemsSource = options,
                DisplayMemberBinding = new Binding(nameof(PrimitiveChoice.Name))
            };

            // preselect based on current mesh type
            var cur = p.GetValue(target) as Game_Engine.Core.Mesh;
            if (cur is null) cb.SelectedIndex = 0;
            else
            {
                int v = cur.Vertices.Length, tri = cur.TriIndices.Length;
                cb.SelectedIndex = options.ToList().FindIndex(o =>
                {
                    var m = o.Factory();
                    return m is not null && m.Vertices.Length == v && m.TriIndices.Length == tri;
                });
                if (cb.SelectedIndex < 0) cb.SelectedIndex = 1; // default to Cube
            }

            cb.DropDownOpened += (_, __) => BeginPropertyEdit(target, p);
            cb.SelectionChanged += (_, __) =>
            {
                var sel = cb.SelectedItem as PrimitiveChoice;
                p.SetValue(target, sel?.Factory());
                SceneService.NotifyChanged();
                CommitPropertyEdit(target, p);
            };

            return cb;
        }

        // ---- Color editor -----------------------------------------------------
        if (t == typeof(Color))
        {
            var tb = new TextBox { Width = 140, Watermark = "#RRGGBB or name" };
            tb.Bind(TextBox.TextProperty, new Binding(p.Name)
            {
                Source = target,
                Mode = BindingMode.TwoWay,
                Converter = ColorStringConverter.Instance
            });
            tb.GotFocus += (_, __) => BeginPropertyEdit(target, p);
            tb.LostFocus += (_, __) => { SceneService.NotifyChanged(); CommitPropertyEdit(target, p); };
            return tb;
        }

        // ---- bool -------------------------------------------------------------
        if (t == typeof(bool))
        {
            var cb = new CheckBox();
            cb.Bind(CheckBox.IsCheckedProperty, new Binding(p.Name) { Source = target, Mode = BindingMode.TwoWay });
            cb.GotFocus += (_, __) => BeginPropertyEdit(target, p);
            cb.IsCheckedChanged += (_, __) => { SceneService.NotifyChanged(); CommitPropertyEdit(target, p); };
            return cb;
        }

        // ---- enums ------------------------------------------------------------
        if (t.IsEnum)
        {
            var cb = new ComboBox { ItemsSource = Enum.GetValues(t) };
            cb.Bind(ComboBox.SelectedItemProperty, new Binding(p.Name) { Source = target, Mode = BindingMode.TwoWay });
            cb.DropDownOpened += (_, __) => BeginPropertyEdit(target, p);
            cb.SelectionChanged += (_, __) => { SceneService.NotifyChanged(); CommitPropertyEdit(target, p); };
            return cb;
        }

        // ---- numbers ----------------------------------------------------------
        if (t == typeof(int) || t == typeof(float) || t == typeof(double) ||
            t == typeof(decimal) || t == typeof(long) || t == typeof(short))
        {
            var tb = new TextBox { Width = 120 };
            tb.Bind(TextBox.TextProperty, new Binding(p.Name)
            {
                Source = target,
                Mode = BindingMode.TwoWay,
                Converter = NumberConverter.Instance,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            tb.GotFocus += (_, __) => BeginPropertyEdit(target, p);
            tb.LostFocus += (_, __) => { SceneService.NotifyChanged(); CommitPropertyEdit(target, p); };
            tb.PropertyChanged += (_, __) => SceneService.NotifyChanged(); // live repaint
            return tb;
        }

        // ---- Vector3 (Core.Vector3) ------------------------------------------
        if (t == typeof(CoreVector3))
            return Vector3EditorWithUndo(target, p);

        // ---- string -----------------------------------------------------------
        if (t == typeof(string))
        {
            var tb = new TextBox { Width = 240 };
            tb.Bind(TextBox.TextProperty, new Binding(p.Name) { Source = target, Mode = BindingMode.TwoWay });
            tb.GotFocus += (_, __) => BeginPropertyEdit(target, p);
            tb.LostFocus += (_, __) => CommitPropertyEdit(target, p);
            return tb;
        }
        
        // ---- textures ----------------------------------------------------------
        if (typeof(Texture2D).IsAssignableFrom(t))
            return Texture2DEditor(target, p);

        if (t == typeof(Material))
            return MaterialEditor(target, p);

        // ---- fallback: read-only type name -----------------------------------
        return new TextBlock { Text = t.Name, Opacity = 0.6 };
    }
}



// Helper: place Controls in Grid cells (Avalonia 11: constrain to Control)
static class GridPos
{
    public static T Place<T>(this T c, int col, int row, int columnSpan = 1, int rowSpan = 1)
        where T : Control
    {
        Avalonia.Controls.Grid.SetColumn(c, col);
        Avalonia.Controls.Grid.SetRow(c, row);
        if (columnSpan > 1) Avalonia.Controls.Grid.SetColumnSpan(c, columnSpan);
        if (rowSpan > 1) Avalonia.Controls.Grid.SetRowSpan(c, rowSpan);
        return c;
    }
}

file sealed class ColorStringConverter : IValueConverter
{
    public static readonly ColorStringConverter Instance = new();

    // A small set of named colors (add more if you like)
    static readonly Dictionary<string, Color> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = Colors.White,
        ["black"] = Colors.Black,
        ["gray"] = Colors.Gray,
        ["lightgray"] = Colors.LightGray,
        ["red"] = Colors.Red,
        ["green"] = Colors.Lime,
        ["blue"] = Colors.Blue,
        ["yellow"] = Colors.Yellow,
        ["cyan"] = Colors.Cyan,
        ["magenta"] = Colors.Magenta,
        ["orange"] = Color.FromRgb(0xFF, 0xA5, 0x00),
        ["deepskyblue"] = Colors.DeepSkyBlue,
    };



    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c)
        {
            var kv = Named.FirstOrDefault(k => k.Value == c);
            if (!kv.Equals(default(KeyValuePair<string, Color>))) return kv.Key;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            if (Named.TryGetValue(s.Trim(), out var named)) return named;
            try { return Color.Parse(s.Trim()); } catch { }
        }
        return BindingOperations.DoNothing; // keep original value if parse fails
    }
}
