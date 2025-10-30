using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Assimp.Metadata;
using CoreTransform = Game_Engine.Core.Component.Transform;
using CoreVector3 = Game_Engine.Core.Vector3;

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

    private bool _assetInspectorActive;

    // Use the custom delegate type
    private AssetSelectedHandler _onAssetSelected;

    private Action _onSelChanged, _onProjOpened, _onProjClosed, _onProjChanged;

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

        _target = SelectionService.Current;

        // Build the delegate with the correct signature
        _onAssetSelected = (sender, absPath) =>
        {
            if (_isLocked) return;
            if (string.IsNullOrWhiteSpace(absPath)) return;

            _assetInspectorActive = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnAssetSelected(absPath));
        };

        // Subscribe once
        ProjectService.AssetSelected += _onAssetSelected;

        _onSelChanged = () =>
        {
            if (_isLocked) return;

            if (_assetInspectorActive)
            {
                // leave material inspector when the user picks a GameObject
                Avalonia.Threading.Dispatcher.UIThread.Post(ExitAssetInspector);
                return;
            }

            _target = SelectionService.Current;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => BuildUI(_target));
        };

        SelectionService.Changed += _onSelChanged;

        _onProjOpened = () => { s_triedLoadPersisted = false; EnsurePersistedEditorScriptsLoaded(); InvalidateScriptCache(); };
        _onProjClosed = InvalidateScriptCache;
        _onProjChanged = InvalidateScriptCache;

        ProjectService.ProjectOpened += _onProjOpened;
        ProjectService.ProjectClosed += _onProjClosed;
        ProjectService.Changed += _onProjChanged;

        // Single tidy cleanup block
        this.Unloaded += (_, __) =>
        {
            if (_onAssetSelected != null) ProjectService.AssetSelected -= _onAssetSelected;
            if (_onSelChanged != null) SelectionService.Changed -= _onSelChanged;
            if (_onProjOpened != null) ProjectService.ProjectOpened -= _onProjOpened;
            if (_onProjClosed != null) ProjectService.ProjectClosed -= _onProjClosed;
            if (_onProjChanged != null) ProjectService.Changed -= _onProjChanged;
        };

        // Prefer showing currently selected asset (if any), else show selection inspector
        if (!string.IsNullOrWhiteSpace(ProjectService.SelectedAssetPath))
        {
            _assetInspectorActive = true;
            OnAssetSelected(ProjectService.SelectedAssetPath);
        }
        else
        {
            BuildUI(_target);
        }

        // Lock toggle wiring
        if (this.FindControl<ToggleButton>("LockToggle") is { } lockBtn)
        {
            lockBtn.IsChecked = false;
            lockBtn.Checked += (_, __) =>
            {
                _isLocked = true;    // freeze current view (either asset inspector or selection inspector)
            };
            lockBtn.Unchecked += (_, __) =>
            {
                _isLocked = false;
                if (_assetInspectorActive)
                {
                    // stay in asset mode unless selection changes explicitly; do nothing
                }
                else
                {
                    _target = SelectionService.Current;
                    BuildUI(_target);
                }
            };
        }
    }

    private void ExitAssetInspector()
    {
        _assetInspectorActive = false;
        _target = SelectionService.Current;
        Host.Children.Clear();
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

    private void OnAssetSelected(string absPath)
    {
        // If it’s not a .material, leave asset mode and go back to normal inspector.
        if (!absPath.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
        {
            _assetInspectorActive = false;
            _target = SelectionService.Current;
            BuildUI(_target);
            return;
        }

        _assetInspectorActive = true;

        try
        {
            var text = File.ReadAllText(absPath);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Material" : "Material";
            string shader = root.TryGetProperty("shader", out var s) ? s.GetString() ?? "" : "";
            var p = root.TryGetProperty("parameters", out var pp) ? pp : default;
            string tintHex = (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Tint", out var tEl))
                             ? (tEl.GetString() ?? "#FFFFFFFF") : "#FFFFFFFF";
            float metallic = ReadF(p, "Metallic", 0f);
            float roughness = ReadF(p, "Roughness", 0.5f);
            bool transparent = ReadB(p, "Transparent", false);
            float cutoff = ReadF(p, "AlphaCutoff", 0.5f);

            // Build into the existing Host container
            Host.Children.Clear();
            var props = new StackPanel { Spacing = 6 };
            Host.Children.Add(props);

            BuildMaterialInspectorUI(props, absPath, name, shader, tintHex, metallic, roughness, transparent, cutoff);
        }
        catch (Exception ex)
        {
            Host.Children.Clear();
            Host.Children.Add(new TextBlock { Text = "Failed to open material: " + ex.Message });
        }

        static float ReadF(JsonElement e, string k, float d)
            => (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetDouble(out var x)) ? (float)x : d;

        static bool ReadB(JsonElement e, string k, bool d)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out var v)) return d;
            return v.ValueKind == JsonValueKind.True ? true :
                   v.ValueKind == JsonValueKind.False ? false : d;
        }
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

        // Terrain extra UI (tools + brush masks)
        if (b is Terrain terr)
        {
            outer.Children.Add(TerrainToolsRow(owner, terr));
            outer.Children.Add(TerrainBrushMasks(terr));
        }


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


    private void BuildMaterialInspectorUI(
    StackPanel props,
    string path,
    string name,
    string shader,
    string tintHex,
    float metallic,
    float rough,
    bool transparent,
    float cutoff)
    {
        props.Children.Clear();

        // ---------- helpers ----------
        var owner = this.GetVisualRoot() as Window;
        var fileName = Path.GetFileName(path);
        var fileDir = Path.GetDirectoryName(path) ?? "";
        var projRoot = ProjectService.Current != null ? ProjectService.Current.RootPath : "";

        Func<string, Color> parseHex = hex =>
        {
            if (string.IsNullOrWhiteSpace(hex)) return Colors.White;
            var h = hex.Trim();
            if (h.StartsWith("#")) h = h.Substring(1);
            if (h.Length == 6) h += "FF";
            byte r = 255, g = 255, b = 255, a = 255;
            try
            {
                r = Convert.ToByte(h.Substring(0, 2), 16);
                g = Convert.ToByte(h.Substring(2, 2), 16);
                b = Convert.ToByte(h.Substring(4, 2), 16);
                a = Convert.ToByte(h.Substring(6, 2), 16);
            }
            catch { /* ignore */ }
            return Color.FromArgb(a, r, g, b);
        };
        Func<Color, string> toHex = c =>
            "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2") + c.A.ToString("X2");

        Func<string, bool> isUnderProject = abs =>
        {
            if (string.IsNullOrWhiteSpace(projRoot)) return false;
            try
            {
                var A = Path.GetFullPath(abs);
                var R = Path.GetFullPath(projRoot);
                return A.StartsWith(R + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(A, R, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        };

        Func<string, string> absToProjectRel = abs =>
        {
            if (string.IsNullOrWhiteSpace(projRoot)) return abs.Replace('\\', '/');
            try
            {
                var rel = Path.GetRelativePath(projRoot, Path.GetFullPath(abs));
                return rel.Replace('\\', '/');
            }
            catch { return abs.Replace('\\', '/'); }
        };

        // Load current textures -> show existing values
        var texturesOrder = new[] { "Albedo", "Normal", "Metallic", "Roughness", "AmbientOcclusion", "Emissive", "Opacity" };
        var texMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = File.ReadAllText(path);
            using (var doc = System.Text.Json.JsonDocument.Parse(raw))
            {
                var root = doc.RootElement;
                System.Text.Json.JsonElement t;
                if (root.TryGetProperty("textures", out t) && t.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var k in texturesOrder)
                    {
                        System.Text.Json.JsonElement v;
                        if (t.TryGetProperty(k, out v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                            texMap[k] = v.GetString();
                        else
                            texMap[k] = null;
                    }
                }
                else
                {
                    foreach (var k in texturesOrder) texMap[k] = null;
                }
            }
        }
        catch
        {
            foreach (var k in texturesOrder) texMap[k] = null;
        }

        // ---------- UI ----------
        var headerGrid = new Avalonia.Controls.Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var headerLeft = new StackPanel { Spacing = 0 };
        headerLeft.Children.Add(new TextBlock { Text = "Material", FontWeight = FontWeight.Bold });
        headerLeft.Children.Add(new TextBlock { Text = fileName, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0) });
        headerGrid.Children.Add(headerLeft.Place(0, 0));

        var btnClose = new Button
        {
            Content = "Back to Selection",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnClose.Click += (_, __) =>
        {
            if (_isLocked)
            {
                ShowInfo("Inspector is locked. Unlock to leave material view.");
                return;
            }
            ExitAssetInspector();
        };
        headerGrid.Children.Add(btnClose.Place(1, 0));

        props.Children.Add(headerGrid);

        Func<string, Control, Control> Labeled = (label, input) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
            var lab = new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
            input.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.Children.Add(lab);
            row.Children.Add(input);
            return row;
        };

        // Name
        var tbName = new TextBox { Text = name ?? "", Width = 240 };
        props.Children.Add(Labeled("Name", tbName));

        // Shader 
        var tbShader = new TextBox { Text = shader ?? "", Width = 240, Watermark = "Shader asset path (.shader)" };
        var btnPickShader = new Button { Content = "Browse…" };
        var btnClearShader = new Button { Content = "Clear" };
        var shaderRowInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        shaderRowInner.Children.Add(tbShader);
        shaderRowInner.Children.Add(btnPickShader);
        shaderRowInner.Children.Add(btnClearShader);
        props.Children.Add(Labeled("Shader", shaderRowInner));

        btnPickShader.Click += async (_, __) =>
        {
            if (owner == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select Shader",
                AllowMultiple = false,
                Filters = new List<FileDialogFilter>
            {
                new FileDialogFilter { Name = "Shader", Extensions = new List<string>{ "shader" } },
                new FileDialogFilter { Name = "All Files", Extensions = new List<string>{ "*" } },
            }
            };
            var files = await dlg.ShowAsync(owner);
            if (files != null && files.Length > 0)
            {
                var picked = files[0];
                tbShader.Text = isUnderProject(picked) ? absToProjectRel(picked) : picked;
            }
        };
        btnClearShader.Click += (_, __) => tbShader.Text = "";

        // Tint
        var tbTint = new TextBox { Text = string.IsNullOrWhiteSpace(tintHex) ? "#FFFFFFFF" : tintHex.Trim(), Width = 120 };
        var swatch = new Border
        {
            Width = 28,
            Height = 20,
            Background = new SolidColorBrush(parseHex(tbTint.Text)),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(6, 0, 0, 0)
        };
        tbTint.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                try
                {
                    var b = swatch.Background as SolidColorBrush;
                    if (b != null) b.Color = parseHex(tbTint.Text);
                }
                catch { }
            }
        };
        var tintRowInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tintRowInner.Children.Add(tbTint);
        tintRowInner.Children.Add(swatch);
        props.Children.Add(Labeled("Tint", tintRowInner));

        // Metallic / Roughness
        Func<double, Control> MakeValueLabel = v => new TextBlock { Text = v.ToString("0.00"), Width = 44, HorizontalAlignment = HorizontalAlignment.Right };

        var sMetal = new Slider { Minimum = 0, Maximum = 1, Value = metallic, Width = 180 };
        var lblMetal = (TextBlock)MakeValueLabel(sMetal.Value);
        sMetal.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) lblMetal.Text = sMetal.Value.ToString("0.00"); };
        var metalRowInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        metalRowInner.Children.Add(sMetal);
        metalRowInner.Children.Add(lblMetal);
        props.Children.Add(Labeled("Metallic", metalRowInner));

        var sRough = new Slider { Minimum = 0, Maximum = 1, Value = rough, Width = 180 };
        var lblRough = (TextBlock)MakeValueLabel(sRough.Value);
        sRough.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) lblRough.Text = sRough.Value.ToString("0.00"); };
        var roughRowInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        roughRowInner.Children.Add(sRough);
        roughRowInner.Children.Add(lblRough);
        props.Children.Add(Labeled("Roughness", roughRowInner));

        // Transparent + cutoff
        var chkTransparent = new CheckBox { Content = "Transparent", IsChecked = transparent };
        props.Children.Add(chkTransparent);
        var sCutoff = new Slider { Minimum = 0, Maximum = 1, Value = cutoff, Width = 180, IsEnabled = (chkTransparent.IsChecked == true) };
        var lblCut = (TextBlock)MakeValueLabel(sCutoff.Value);
        sCutoff.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) lblCut.Text = sCutoff.Value.ToString("0.00"); };
        chkTransparent.Checked += (_, __) => sCutoff.IsEnabled = true;
        chkTransparent.Unchecked += (_, __) => sCutoff.IsEnabled = false;
        var cutRowInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        cutRowInner.Children.Add(sCutoff);
        cutRowInner.Children.Add(lblCut);
        props.Children.Add(Labeled("Alpha Cutoff", cutRowInner));

        // ---------- Textures ----------
        props.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
        props.Children.Add(new TextBlock { Text = "Textures", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

        Action<string> ensureKey = k => { if (!texMap.ContainsKey(k)) texMap[k] = null; };

        Action<string> addTextureRow = slot =>
        {
            ensureKey(slot);
            var initial = texMap[slot] ?? "";

            var tbPath = new TextBox { Text = initial, Width = 240, Watermark = "(none)" };
            var btnPick = new Button { Content = "Browse…" };
            var btnClear = new Button { Content = "Clear" };

            var drop = new Border
            {
                Padding = new Thickness(6, 2, 6, 2),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Child = new TextBlock { Text = "Drop file here", Opacity = 0.7 }
            };
            DragDrop.SetAllowDrop(drop, true);

            // Accept: Explorer/File tree via FileNames; also Files (IStorageItem) from other sources
            drop.AddHandler(DragDrop.DragOverEvent, (s, e) =>
            {
                if (e.Data.Contains(DataFormats.FileNames) || e.Data.Contains(DataFormats.Files))
                    e.DragEffects = DragDropEffects.Copy;
                else
                    e.DragEffects = DragDropEffects.None;
                e.Handled = true;
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            drop.AddHandler(DragDrop.DropEvent, async (s, e) =>
            {
                string pickedPath = null;

                // Plain filenames (also set by our ProjectPanel when dragging files)
                if (e.Data.Contains(DataFormats.FileNames))
                {
                    var names = e.Data.GetFileNames();
                    if (names != null) pickedPath = names.FirstOrDefault();
                }

                // Storage items (other apps / some OS paths)
                if (pickedPath == null && e.Data.Contains(DataFormats.Files))
                {
                    var asEnum = e.Data.Get(DataFormats.Files) as IEnumerable<IStorageItem>;
                    if (asEnum != null)
                    {
                        var it = asEnum.FirstOrDefault();
                        var f = it as IStorageFile;
                        if (f != null)
                        {
                            var local = f.TryGetLocalPath();
                            if (!string.IsNullOrWhiteSpace(local))
                            {
                                pickedPath = local;
                            }
                            else
                            {
                                // Copy stream beside the .material
                                try
                                {
                                    Directory.CreateDirectory(fileDir);
                                    var dst = Path.Combine(fileDir, f.Name);

                                    var dir = Path.GetDirectoryName(dst) ?? fileDir;
                                    var baseNoExt = Path.GetFileNameWithoutExtension(dst);
                                    var ext = Path.GetExtension(dst);
                                    var i = 1;
                                    while (File.Exists(dst))
                                        dst = Path.Combine(dir, baseNoExt + " (" + (i++) + ")" + ext);

                                    using (var inS = await f.OpenReadAsync())
                                    using (var outS = File.Create(dst))
                                    {
                                        await inS.CopyToAsync(outS);
                                    }
                                    pickedPath = dst;
                                }
                                catch { pickedPath = null; }
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(pickedPath)) return;

                // image filter (match your MaterialEditor)
                var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png",".jpg",".jpeg",".bmp",".tga",".dds",".tif",".tiff",".webp" };
                if (!ok.Contains(Path.GetExtension(pickedPath))) return;

                // If outside project, copy beside the .material
                var target = pickedPath;
                if (!isUnderProject(target))
                {
                    try
                    {
                        Directory.CreateDirectory(fileDir);
                        var dst = Path.Combine(fileDir, Path.GetFileName(target));

                        var dir = Path.GetDirectoryName(dst) ?? fileDir;
                        var baseNoExt = Path.GetFileNameWithoutExtension(dst);
                        var ext = Path.GetExtension(dst);
                        var i = 1;
                        while (File.Exists(dst))
                            dst = Path.Combine(dir, baseNoExt + " (" + (i++) + ")" + ext);

                        File.Copy(target, dst);
                        target = dst;
                    }
                    catch { return; }
                }

                tbPath.Text = absToProjectRel(target);
                texMap[slot] = tbPath.Text;
                e.Handled = true;
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            btnPick.Click += async (_, __) =>
            {
                if (owner == null) return;
                var dlg = new OpenFileDialog
                {
                    Title = "Select " + slot + " Texture",
                    AllowMultiple = false,
                    Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "Images", Extensions = new List<string>{ "png","jpg","jpeg","tga","bmp","tif","tiff","webp","dds" } },
                    new FileDialogFilter { Name = "All Files", Extensions = new List<string>{ "*" } },
                }
                };
                var files = await dlg.ShowAsync(owner);
                if (files != null && files.Length > 0)
                {
                    var picked = files[0];
                    if (!isUnderProject(picked))
                    {
                        try
                        {
                            Directory.CreateDirectory(fileDir);
                            var dst = Path.Combine(fileDir, Path.GetFileName(picked));
                            var dir = Path.GetDirectoryName(dst) ?? fileDir;
                            var baseNoExt = Path.GetFileNameWithoutExtension(dst);
                            var ext = Path.GetExtension(dst);
                            var i = 1;
                            while (File.Exists(dst))
                                dst = Path.Combine(dir, baseNoExt + " (" + (i++) + ")" + ext);
                            File.Copy(picked, dst);
                            picked = dst;
                        }
                        catch { return; }
                    }
                    tbPath.Text = absToProjectRel(picked);
                    texMap[slot] = tbPath.Text;
                }
            };

            btnClear.Click += (_, __) => { tbPath.Text = ""; texMap[slot] = null; };

            var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            inner.Children.Add(tbPath);
            inner.Children.Add(btnPick);
            inner.Children.Add(btnClear);
            inner.Children.Add(drop);

            props.Children.Add(Labeled(slot, inner));
        };

        for (var i = 0; i < texturesOrder.Length; i++)
            addTextureRow(texturesOrder[i]);

        // ---------- Save / Revert ----------
        props.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 6) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var btnRevert = new Button { Content = "Revert" };
        var btnSave = new Button { Content = "Save" };
        buttons.Children.Add(btnRevert);
        buttons.Children.Add(btnSave);
        props.Children.Add(buttons);

        btnRevert.Click += (_, __) => { try { OnAssetSelected(path); } catch { } };

        btnSave.Click += (_, __) =>
        {
            var finalName = string.IsNullOrWhiteSpace(tbName.Text) ? "Material" : tbName.Text.Trim();
            var finalShader = tbShader.Text != null ? tbShader.Text.Trim() : "";
            var finalTint = toHex(parseHex(tbTint.Text));
            var m = Math.Max(0, Math.Min(1, sMetal.Value));
            var r = Math.Max(0, Math.Min(1, sRough.Value));
            var tr = chkTransparent.IsChecked == true;
            var ac = Math.Max(0, Math.Min(1, sCutoff.Value));

            var writerOptions = new System.Text.Json.JsonWriterOptions { Indented = true };
            using (var ms = new MemoryStream())
            {
                using (var jw = new System.Text.Json.Utf8JsonWriter(ms, writerOptions))
                {
                    jw.WriteStartObject();
                    jw.WriteString("name", finalName);
                    jw.WriteString("type", "Material");
                    jw.WriteNumber("version", 1);
                    jw.WriteString("shader", finalShader);

                    jw.WritePropertyName("parameters");
                    jw.WriteStartObject();
                    jw.WriteString("Tint", finalTint);
                    jw.WriteNumber("Metallic", m);
                    jw.WriteNumber("Roughness", r);
                    jw.WriteBoolean("Transparent", tr);
                    jw.WriteNumber("AlphaCutoff", ac);
                    jw.WriteEndObject();

                    jw.WritePropertyName("textures");
                    jw.WriteStartObject();
                    for (var i = 0; i < texturesOrder.Length; i++)
                    {
                        var key = texturesOrder[i];
                        var val = texMap.ContainsKey(key) ? texMap[key] : null;
                        if (string.IsNullOrWhiteSpace(val)) jw.WriteNull(key);
                        else jw.WriteString(key, val);
                    }
                    jw.WriteEndObject();

                    jw.WriteEndObject();
                    jw.Flush();
                }

                var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                try
                {
                    File.WriteAllText(path, json);
                    ProjectService.TouchModified();
                    Game_Engine.Core.SceneService.NotifyChanged();
                }
                catch (Exception ex)
                {
                    props.Children.Add(new TextBlock
                    {
                        Text = "Save failed: " + ex.Message,
                        Foreground = Brushes.OrangeRed,
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                }
            }
        };
    }

    // UI-only cache so material path doesn't disappear if there's no sibling path or inner path slot
    static readonly ConditionalWeakTable<object, Dictionary<string, string>> _matPathCache = new();
    static string? GetCachedMatPath(object owner, string propName)
    {
        return _matPathCache.TryGetValue(owner, out var map) && map.TryGetValue(propName, out var v) ? v : null;
    }
    static void SetCachedMatPath(object owner, string propName, string? relPath)
    {
        var map = _matPathCache.GetOrCreateValue(owner);
        if (string.IsNullOrWhiteSpace(relPath)) map.Remove(propName);
        else map[propName] = relPath!;
    }

    Control MaterialEditor(object owner, PropertyInfo prop)
    {
        // ---------- helpers ----------
        static string MakeProjectRelative(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            try
            {
                var abs = System.IO.Path.GetFullPath(fullPath);
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var root = System.IO.Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return System.IO.Path.GetRelativePath(root, abs).Replace('\\', '/');
                }
                return abs.Replace('\\', '/');
            }
            catch { return fullPath.Replace('\\', '/'); }
        }

        static string EnsureInProject(string fullPath, string preferredFolderName)
        {
            try
            {
                var proj = ProjectService.Current;
                if (proj == null) return fullPath;

                var abs = System.IO.Path.GetFullPath(fullPath);
                var root = System.IO.Path.GetFullPath(proj.RootPath);
                if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return abs;

                var baseRoot = string.IsNullOrWhiteSpace(proj.AssetsPath) ? proj.RootPath : proj.AssetsPath;
                var importDir = System.IO.Path.Combine(baseRoot, preferredFolderName);
                Directory.CreateDirectory(importDir);

                var dst = System.IO.Path.Combine(importDir, System.IO.Path.GetFileName(fullPath));
                if (File.Exists(dst))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(dst);
                    var ext = System.IO.Path.GetExtension(dst);
                    int i = 1;
                    while (File.Exists(dst = System.IO.Path.Combine(importDir, name + " (" + (i++) + ")" + ext))) ;
                }
                File.Copy(fullPath, dst, false);
                return dst;
            }
            catch { return fullPath; }
        }

        static Material TryCreateMaterialFromPath(string path)
        {
            var t = typeof(Material);

            var m = t.GetMethod("FromFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                                null, new[] { typeof(string) }, null);
            if (m != null) { try { return (Material)m.Invoke(null, new object[] { path }); } catch { } }

            m = t.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            null, new[] { typeof(string) }, null);
            if (m != null) { try { return (Material)m.Invoke(null, new object[] { path }); } catch { } }

            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) { try { return (Material)ctor.Invoke(new object[] { path }); } catch { } }

            try { return (Material)Activator.CreateInstance(t); } catch { return new Material(); }
        }

        static string ReadInnerPathFromMaterial(Material mat)
        {
            if (mat == null) return null;
            var t = mat.GetType();
            // include RelPath first; then common names
            var names = new[] { "RelPath", "Path", "FilePath", "SourcePath", "AssetPath", "MaterialPath" };
            for (int i = 0; i < names.Length; i++)
            {
                var p = t.GetProperty(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.PropertyType == typeof(string) && p.CanRead)
                {
                    var s = p.GetValue(mat) as string;
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }

        static void WriteInnerPathToMaterial(Material mat, string projectRelPath)
        {
            if (mat == null) return;
            var t = mat.GetType();
            var names = new[] { "RelPath", "Path", "FilePath", "SourcePath", "AssetPath", "MaterialPath" };
            for (int i = 0; i < names.Length; i++)
            {
                var p = t.GetProperty(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.PropertyType == typeof(string) && p.CanWrite)
                {
                    try { p.SetValue(mat, projectRelPath); } catch { }
                    return;
                }
            }
        }

        static string TryGetSiblingPath(object target, PropertyInfo matProp)
        {
            try
            {
                var pp = target.GetType().GetProperty(matProp.Name + "Path",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pp != null && pp.PropertyType == typeof(string) && pp.CanRead)
                {
                    var v = pp.GetValue(target) as string;
                    return string.IsNullOrWhiteSpace(v) ? null : v;
                }
            }
            catch { }
            return null;
        }

        static void TrySetSiblingPath(object target, PropertyInfo matProp, string projectRelPath)
        {
            try
            {
                var pp = target.GetType().GetProperty(matProp.Name + "Path",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pp != null && pp.PropertyType == typeof(string) && pp.CanWrite)
                    pp.SetValue(target, projectRelPath);
            }
            catch { }
        }

        static (string name, string shader, bool transp, float metallic, float rough, string error) ReadMaterialSummary(string absPath)
        {
            try
            {
                var txt = File.ReadAllText(absPath);
                using (var doc = System.Text.Json.JsonDocument.Parse(txt))
                {
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "Material") : "Material";
                    var shader = root.TryGetProperty("shader", out var s) ? (s.GetString() ?? "") : "";

                    bool transp = false; float met = 0f; float rgh = 0.5f;
                    System.Text.Json.JsonElement p;
                    if (root.TryGetProperty("parameters", out p) && p.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        System.Text.Json.JsonElement vT, vM, vR;
                        if (p.TryGetProperty("Transparent", out vT)) transp = vT.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (p.TryGetProperty("Metallic", out vM)) { double md; if (vM.TryGetDouble(out md)) met = (float)md; }
                        if (p.TryGetProperty("Roughness", out vR)) { double rd; if (vR.TryGetDouble(out rd)) rgh = (float)rd; }
                    }
                    return (name, shader, transp, met, rgh, null);
                }
            }
            catch (Exception ex) { return (null, null, false, 0f, 0.5f, ex.Message); }
        }

        // ---------- face constants ----------
        const int S_All = -1;
        const int S_Right = 1, S_Left = 2, S_Top = 4, S_Bottom = 8, S_Back = 16, S_Front = 32;

        static string FaceLabel(int s)
        {
            if (s == S_Right) return "Right (+X)";
            if (s == S_Left) return "Left (-X)";
            if (s == S_Top) return "Top (+Y)";
            if (s == S_Bottom) return "Bottom (-Y)";
            if (s == S_Back) return "Back (+Z)";
            if (s == S_Front) return "Front (-Z)";
            return "All";
        }
        static string FaceKey(int s)
        {
            if (s == S_Right) return "Right";
            if (s == S_Left) return "Left";
            if (s == S_Top) return "Top";
            if (s == S_Bottom) return "Bottom";
            if (s == S_Back) return "Back";
            if (s == S_Front) return "Front";
            return "All";
        }

        // ---------- container ----------
        var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8) };
        var root = new StackPanel { Spacing = 10 };
        box.Child = root;

        void BeginEdit() => BeginPropertyEdit(owner, prop);
        void CommitEdit() => CommitPropertyEdit(owner, prop);

        // ---------- top: single material selector  ----------
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock { Text = "Material (asset)", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var tbPath = new TextBox { Width = 360, IsReadOnly = true };
        var btnBrowse = new Button { Content = "Choose…" };
        var btnNew = new Button { Content = "New…" };
        var btnEdit = new Button { Content = "Edit…" };
        var btnClear = new Button { Content = "Clear" };
        row.Children.Add(tbPath);
        row.Children.Add(btnBrowse);
        row.Children.Add(btnNew);
        row.Children.Add(btnEdit);
        row.Children.Add(btnClear);
        root.Children.Add(row);

        var drop = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            Child = new TextBlock { Text = "Drop .material here…", Opacity = .7 }
        };
        DragDrop.SetAllowDrop(drop, true);
        root.Children.Add(drop);

        var summary = new StackPanel { Spacing = 2 };
        root.Children.Add(summary);

        // ---------- per-side list ----------
        root.Children.Add(new TextBlock { Text = "Per-side materials", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
        var slotsPanel = new StackPanel { Spacing = 6 };
        root.Children.Add(slotsPanel);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var btnAdd = new Button { Content = "Add from file…" };
        var btnAddNew = new Button { Content = "New material…" };
        //var btnClearAll = new Button { Content = "Clear all" };
        toolbar.Children.Add(btnAdd);
        toolbar.Children.Add(btnAddNew);
        //toolbar.Children.Add(btnClearAll);
        root.Children.Add(toolbar);

        // internal model: (Material Mat, string RelPath, int Side)
        var slots = new List<(Material Mat, string RelPath, int Side)>();

        // seed from bound property
        var cur = prop.GetValue(owner) as Material;
        if (cur != null)
        {
            var rel = ReadInnerPathFromMaterial(cur);
            if (string.IsNullOrWhiteSpace(rel))
            {
                var s = TryGetSiblingPath(owner, prop);
                if (!string.IsNullOrWhiteSpace(s)) rel = s;
            }
            if (cur != null || !string.IsNullOrWhiteSpace(rel))
                slots.Add((cur, rel, S_All));
        }

        // seed from optional lists (MaterialPaths / MaterialSides / MaterialSlots)
        try
        {
            var tOwner = owner.GetType();
            var pPaths = tOwner.GetProperty("MaterialPaths", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var pSides = tOwner.GetProperty("MaterialSides", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var pSlots = tOwner.GetProperty("MaterialSlots", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var paths = pPaths != null ? pPaths.GetValue(owner) as System.Collections.IList : null;
            var sides = pSides != null ? pSides.GetValue(owner) as System.Collections.IList : null;
            var mats = pSlots != null ? pSlots.GetValue(owner) as System.Collections.IList : null;

            if (paths != null && sides != null && mats != null)
            {
                int n = Math.Min(paths.Count, Math.Min(sides.Count, mats.Count));
                if (n > 0) slots.Clear();
                for (int i = 0; i < n; i++)
                {
                    var m = mats[i] as Material;
                    var rel = paths[i] as string;
                    int s = S_All; try { s = Convert.ToInt32(sides[i]); } catch { s = S_All; }
                    if (m == null && string.IsNullOrWhiteSpace(rel)) continue; // skip bogus empty rows
                    slots.Add((m, rel, s));
                }
            }
        }
        catch { }

        void UpdateSummary(string projectRelOrAbs)
        {
            summary.Children.Clear();
            if (string.IsNullOrWhiteSpace(projectRelOrAbs) || projectRelOrAbs == "(none)")
            {
                summary.Children.Add(new TextBlock { Text = "No material assigned.", Opacity = .7 });
                return;
            }

            string abs = projectRelOrAbs;
            try
            {
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var rootPath = System.IO.Path.GetFullPath(proj.RootPath);
                    var p = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootPath, projectRelOrAbs));
                    if (File.Exists(p)) abs = p;
                }
            }
            catch { }

            var info = ReadMaterialSummary(abs);
            if (info.error != null)
            {
                summary.Children.Add(new TextBlock { Text = "Failed to read material: " + info.error, Foreground = Brushes.OrangeRed });
                return;
            }

            summary.Children.Add(new TextBlock { Text = "Name: " + (info.name ?? "(unnamed)") });
            summary.Children.Add(new TextBlock { Text = "Shader: " + (string.IsNullOrWhiteSpace(info.shader) ? "(none)" : info.shader) });
            summary.Children.Add(new TextBlock { Text = "Transparent: " + (info.transp ? "Yes" : "No") });
            summary.Children.Add(new TextBlock { Text = "Metallic: " + info.metallic.ToString("0.00") + "   Roughness: " + info.rough.ToString("0.00") });
        }

        // Write back everything to owner (prop + sibling + lists + dicts). Header path is handled separately.
        void WriteBackToOwner()
        {
            try
            {
                var tOwner = owner.GetType();

                // bound prop = first All (or first slot)
                Material first = null;
                for (int i = 0; i < slots.Count; i++) if (slots[i].Side == S_All) { first = slots[i].Mat; break; }
                if (first == null && slots.Count > 0) first = slots[0].Mat;

                if (prop.CanWrite) { try { prop.SetValue(owner, first); } catch { } }

                string firstRel = first != null ? ReadInnerPathFromMaterial(first) : null;

                // sibling "*Path"
                try
                {
                    var pp = tOwner.GetProperty(prop.Name + "Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pp != null && pp.PropertyType == typeof(string) && pp.CanWrite)
                        pp.SetValue(owner, firstRel);
                }
                catch { }

                // cache fallback
                SetCachedMatPath(owner, prop.Name, firstRel);

                // lists: MaterialPaths / MaterialSides / MaterialSlots / Materials
                Action<PropertyInfo, Func<int, object>> writeList = (pInfo, getter) =>
                {
                    if (pInfo == null) return;
                    var curVal = pInfo.GetValue(owner);
                    var il = curVal as System.Collections.IList;
                    if (il == null)
                    {
                        try
                        {
                            if (typeof(System.Collections.IList).IsAssignableFrom(pInfo.PropertyType))
                            {
                                curVal = Activator.CreateInstance(pInfo.PropertyType);
                                il = curVal as System.Collections.IList;
                            }
                        }
                        catch { il = null; }
                    }
                    if (il == null) return;

                    il.Clear();
                    for (int i = 0; i < slots.Count; i++) il.Add(getter(i));
                    try { pInfo.SetValue(owner, il); } catch { }
                };

                var pPaths = tOwner.GetProperty("MaterialPaths", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var pSides = tOwner.GetProperty("MaterialSides", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var pSlots = tOwner.GetProperty("MaterialSlots", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var pMats = tOwner.GetProperty("Materials", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                writeList(pPaths, i => (object)slots[i].RelPath);
                writeList(pSides, i => (object)slots[i].Side);
                writeList(pSlots, i => (object)slots[i].Mat);
                writeList(pMats, i => (object)slots[i].Mat);

                // dicts: MaterialByFace / FaceMaterials  (skip "All")
                foreach (var dictName in new[] { "MaterialByFace", "FaceMaterials" })
                {
                    var pDict = tOwner.GetProperty(dictName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pDict == null) continue;

                    object dictObj = pDict.GetValue(owner);
                    var dict = dictObj as System.Collections.IDictionary;
                    if (dict == null)
                    {
                        try { dict = Activator.CreateInstance(pDict.PropertyType) as System.Collections.IDictionary; } catch { dict = null; }
                    }
                    if (dict == null) continue;

                    dict.Clear();
                    for (int i = 0; i < slots.Count; i++)
                    {
                        if (slots[i].Side == S_All) continue;
                        var key = FaceKey(slots[i].Side);
                        if (dict.Contains(key)) dict[key] = slots[i].Mat;
                        else dict.Add(key, slots[i].Mat);
                    }
                    try { pDict.SetValue(owner, dict); } catch { }
                }
            }
            catch { }
        }

        void RebuildListUI()
        {
            slotsPanel.Children.Clear();

            int[] sideVals = new[] { S_All, S_Right, S_Left, S_Top, S_Bottom, S_Back, S_Front };
            string[] sideLabels = new[] { FaceLabel(S_All), FaceLabel(S_Right), FaceLabel(S_Left), FaceLabel(S_Top), FaceLabel(S_Bottom), FaceLabel(S_Back), FaceLabel(S_Front) };

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var rowSlot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

                var tb = new TextBox { Width = 300, IsReadOnly = true, Text = string.IsNullOrWhiteSpace(slot.RelPath) ? "(unsaved)" : slot.RelPath };
                rowSlot.Children.Add(tb);

                var sideBox = new ComboBox { Width = 140, ItemsSource = sideLabels };
                int sel = Array.IndexOf(sideVals, slot.Side);
                if (sel < 0) sel = 0;
                sideBox.SelectedIndex = sel;

                int rowIndex = i; // capture once per row
                sideBox.SelectionChanged += (_, __) =>
                {
                    int idx = sideBox.SelectedIndex;
                    if (idx < 0 || idx >= sideVals.Length) return;

                    var curTuple = slots[rowIndex];
                    slots[rowIndex] = (curTuple.Mat, curTuple.RelPath, sideVals[idx]);

                    BeginEdit();
                    WriteBackToOwner();
                    SceneService.NotifyChanged();
                    CommitEdit();
                };
                rowSlot.Children.Add(sideBox);

                var btnUp = new Button { Content = "↑" };
                btnUp.Click += (__, ___) =>
                {
                    if (rowIndex > 0)
                    {
                        var tmp = slots[rowIndex - 1];
                        slots[rowIndex - 1] = slots[rowIndex];
                        slots[rowIndex] = tmp;
                        BeginEdit();
                        WriteBackToOwner();
                        SceneService.NotifyChanged();
                        CommitEdit();
                        RebuildListUI();
                    }
                };
                rowSlot.Children.Add(btnUp);

                var btnDown = new Button { Content = "↓" };
                btnDown.Click += (__, ___) =>
                {
                    if (rowIndex >= 0 && rowIndex < slots.Count - 1)
                    {
                        var tmp = slots[rowIndex + 1];
                        slots[rowIndex + 1] = slots[rowIndex];
                        slots[rowIndex] = tmp;
                        BeginEdit();
                        WriteBackToOwner();
                        SceneService.NotifyChanged();
                        CommitEdit();
                        RebuildListUI();
                    }
                };
                rowSlot.Children.Add(btnDown);

                var btnRemove = new Button { Content = "Remove" };
                btnRemove.Click += (__, ___) =>
                {
                    if (rowIndex >= 0 && rowIndex < slots.Count)
                    {
                        slots.RemoveAt(rowIndex);
                        BeginEdit();
                        WriteBackToOwner();
                        SceneService.NotifyChanged();
                        CommitEdit();
                        RebuildListUI();
                    }
                };
                rowSlot.Children.Add(btnRemove);

                slotsPanel.Children.Add(rowSlot);
            }
        }

        // ---------- assigners ----------
        // top-level assignment
        Action<string> AssignFromPathTop = (pickedAbs) =>
        {
            var abs = EnsureInProject(pickedAbs, "Materials");
            var rel = MakeProjectRelative(abs);

            BeginEdit();

            var loaded = TryCreateMaterialFromPath(abs);
            prop.SetValue(owner, loaded); // <— first build direct set

            bool wroteSibling = false;
            try
            {
                var before = TryGetSiblingPath(owner, prop);
                TrySetSiblingPath(owner, prop, rel);
                wroteSibling = TryGetSiblingPath(owner, prop) != null || before != null;
            }
            catch { }

            if (!wroteSibling)
            {
                try { WriteInnerPathToMaterial(loaded, rel); } catch { }
                SetCachedMatPath(owner, prop.Name, rel);
            }

            // keep per-side list in sync: update/insert the All slot
            int idxAll = -1;
            for (int k = 0; k < slots.Count; k++) if (slots[k].Side == S_All) { idxAll = k; break; }
            var s = (loaded, rel, S_All);
            if (idxAll >= 0) slots[idxAll] = s; else slots.Insert(0, s);

            SceneService.NotifyChanged();
            CommitEdit();

            tbPath.Text = rel ?? abs;
            UpdateSummary(tbPath.Text);
            RebuildListUI();
        };

        Action<string, int> AddExtraSlot = (pickedAbs, side) =>
        {
            var abs = EnsureInProject(pickedAbs, "Materials");
            var rel = MakeProjectRelative(abs);
            var loaded = TryCreateMaterialFromPath(abs);
            if (!string.IsNullOrWhiteSpace(rel)) WriteInnerPathToMaterial(loaded, rel);

            slots.Add((loaded, rel, side));

            BeginEdit();
            WriteBackToOwner();
            SceneService.NotifyChanged();
            CommitEdit();
            RebuildListUI();
        };

        // ---------- wire up top row (load header text robustly) ----------
        {
            string initialRel = null;

            //  sibling "*Path"
            if (string.IsNullOrWhiteSpace(initialRel))
                initialRel = TryGetSiblingPath(owner, prop);

            //  inner path of bound prop
            if (string.IsNullOrWhiteSpace(initialRel))
                initialRel = ReadInnerPathFromMaterial(prop.GetValue(owner) as Material);

            // UI cache
            if (string.IsNullOrWhiteSpace(initialRel))
                initialRel = GetCachedMatPath(owner, prop.Name);

            // from per-side slots (prefer All)
            if (string.IsNullOrWhiteSpace(initialRel))
            {
                string fromSlots = null;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].Side == S_All && !string.IsNullOrWhiteSpace(slots[i].RelPath))
                    { fromSlots = slots[i].RelPath; break; }
                }
                if (string.IsNullOrWhiteSpace(fromSlots))
                {
                    for (int i = 0; i < slots.Count; i++)
                        if (!string.IsNullOrWhiteSpace(slots[i].RelPath))
                        { fromSlots = slots[i].RelPath; break; }
                }
                initialRel = fromSlots;
            }

            tbPath.Text = string.IsNullOrWhiteSpace(initialRel) ? "(none)" : initialRel;
            UpdateSummary(tbPath.Text);
        }

        // --- events ---
        btnBrowse.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            var dlg = new OpenFileDialog
            {
                Title = "Select Material",
                AllowMultiple = false,
                Filters =
        {
            new FileDialogFilter { Name = "Material", Extensions = { "material" } },
            new FileDialogFilter { Name = "All Files", Extensions = { "*" } }
        }
            };
            var files = await dlg.ShowAsync(win);
            if (files != null && files.Length > 0 && File.Exists(files[0]))
                AssignFromPathTop(files[0]);
        };

        btnNew.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            string defaultDir =
                (ProjectService.Current != null && !string.IsNullOrWhiteSpace(ProjectService.Current.AssetsPath))
                ? System.IO.Path.Combine(ProjectService.Current.AssetsPath, "Materials")
                : (ProjectService.Current != null
                    ? System.IO.Path.Combine(ProjectService.Current.RootPath, "Assets", "Materials")
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            try { Directory.CreateDirectory(defaultDir); } catch { }

            var sfd = new SaveFileDialog
            {
                Title = "Create Material",
                InitialFileName = "NewMaterial.material",
                Directory = defaultDir,
                Filters = { new FileDialogFilter { Name = "Material", Extensions = { "material" } } }
            };

            var dest = await sfd.ShowAsync(win);
            if (string.IsNullOrWhiteSpace(dest)) return;

            try
            {
                var json =
    @"{
  ""name"": ""New Material"",
  ""type"": ""Material"",
  ""version"": 1,
  ""shader"": """",
  ""parameters"": {
    ""Tint"": ""#FFFFFFFF"",
    ""Metallic"": 0.00,
    ""Roughness"": 0.50,
    ""Transparent"": false,
    ""AlphaCutoff"": 0.50
  },
  ""textures"": { }
}";
                File.WriteAllText(dest, json);
            }
            catch (Exception ex)
            {
                summary.Children.Clear();
                summary.Children.Add(new TextBlock { Text = "Failed to create: " + ex.Message, Foreground = Brushes.OrangeRed });
                return;
            }

            AssignFromPathTop(dest);
        };

        btnEdit.Click += (_, __) =>
        {
            if (string.IsNullOrWhiteSpace(tbPath.Text) || tbPath.Text == "(none)") return;

            string abs = tbPath.Text;
            try
            {
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var rootPath = System.IO.Path.GetFullPath(proj.RootPath);
                    var p = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootPath, tbPath.Text));
                    if (File.Exists(p)) abs = p;
                }
            }
            catch { }

            try
            {
                _assetInspectorActive = true; // reuse asset inspector
                OnAssetSelected(abs);
            }
            catch { }
        };

        btnClear.Click += (_, __) =>
        {
            BeginEdit();
            prop.SetValue(owner, null);
            TrySetSiblingPath(owner, prop, null);
            SetCachedMatPath(owner, prop.Name, null);
            SceneService.NotifyChanged();
            CommitEdit();

            tbPath.Text = "(none)";
            summary.Children.Clear();
            slots.Clear();              // also clear per-side list
            RebuildListUI();
        };

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
            string picked = null;

            if (e.Data.Contains(DataFormats.FileNames))
                picked = e.Data.GetFileNames()?.FirstOrDefault();

            if (picked == null && e.Data.Contains(DataFormats.Files))
            {
                var items = e.Data.Get(DataFormats.Files) as IEnumerable<IStorageItem>;
                var it = items != null ? items.FirstOrDefault() as IStorageFile : null;
                if (it != null)
                {
                    var local = it.TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(local)) picked = local;
                    else
                    {
                        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), it.Name);
                        try
                        {
                            using (var src = await it.OpenReadAsync())
                            using (var dst = File.Create(tmp))
                                await src.CopyToAsync(dst);
                            picked = tmp;
                        }
                        catch { picked = null; }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(picked) &&
                string.Equals(System.IO.Path.GetExtension(picked), ".material", StringComparison.OrdinalIgnoreCase))
            {
                AssignFromPathTop(picked);
                e.Handled = true;
            }
        });

        // per-side toolbar
        btnAdd.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            var ofd = new OpenFileDialog
            {
                Title = "Add Material(s)",
                AllowMultiple = true,
                Filters = { new FileDialogFilter { Name = "Material", Extensions = { "material" } } }
            };
            var files = await ofd.ShowAsync(win);
            if (files == null || files.Length == 0) return;

            for (int i = 0; i < files.Length; i++)
            {
                if (string.Equals(System.IO.Path.GetExtension(files[i]), ".material", StringComparison.OrdinalIgnoreCase))
                    AddExtraSlot(files[i], S_Right); // default side; user can change
            }
        };

        btnAddNew.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            string defaultDir =
                (ProjectService.Current != null && !string.IsNullOrWhiteSpace(ProjectService.Current.AssetsPath))
                ? System.IO.Path.Combine(ProjectService.Current.AssetsPath, "Materials")
                : (ProjectService.Current != null
                    ? System.IO.Path.Combine(ProjectService.Current.RootPath, "Assets", "Materials")
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            try { Directory.CreateDirectory(defaultDir); } catch { }

            var sfd = new SaveFileDialog
            {
                Title = "Create Material",
                InitialFileName = "NewMaterial.material",
                Directory = defaultDir,
                Filters = { new FileDialogFilter { Name = "Material", Extensions = { "material" } } }
            };
            var dest = await sfd.ShowAsync(win);
            if (string.IsNullOrWhiteSpace(dest)) return;

            try
            {
                var json =
                    @"{
                  ""name"": ""New Material"",
                  ""type"": ""Material"",
                  ""version"": 1,
                  ""shader"": """",
                  ""parameters"": {
                    ""Tint"": ""#FFFFFFFF"",
                    ""Metallic"": 0.00,
                    ""Roughness"": 0.50,
                    ""Transparent"": false,
                    ""AlphaCutoff"": 0.50
                  },
                  ""textures"": { }
                }";
                File.WriteAllText(dest, json);
            }
            catch (Exception ex)
            {
                summary.Children.Clear();
                summary.Children.Add(new TextBlock { Text = "" + ex.Message, Foreground = Brushes.OrangeRed });
                return;
            }

            AddExtraSlot(dest, S_Right);
        };

        /*btnClearAll.Click += (_, __) =>
        {
            slots.Clear();
            BeginEdit();
            WriteBackToOwner();
            SceneService.NotifyChanged();
            CommitEdit();
            RebuildListUI();
        };*/

        // ---------- initial render ----------
        RebuildListUI();

        // If header still empty, show first slot’s relpath (defensive).
        if (tbPath.Text == "(none)" && slots.Count > 0 && !string.IsNullOrWhiteSpace(slots[0].RelPath))
        {
            tbPath.Text = slots[0].RelPath;
            UpdateSummary(tbPath.Text);
        }

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

    // --- Terrain editor state (UI-only, not persisted to scene) -------------------
    static readonly ConditionalWeakTable<Terrain, TerrainEditorState> _terrainUi
        = new ConditionalWeakTable<Terrain, TerrainEditorState>();

    sealed class TerrainEditorState
    {
        public int ToolIndex = -1;           // which tool button is active
        public int BrushIndex;         // which brush mask is active
        public double BrushSize = 8;   // logical scene units
        public double Strength = 0.5; // 0..1
        public double Falloff = 0.5; // 0..1
    }

    static TerrainEditorState GetTerrainState(Terrain t)
        => _terrainUi.GetOrCreateValue(t);

    // Small styled label for section titles
    TextBlock SectionTitle(string text) =>
        new TextBlock { Text = text, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) };

    // Neutral panel gray 
    static readonly IBrush PanelBg = new SolidColorBrush(Color.Parse("#3A3D45")); // ≈ gray


    // Pill-ish toolbar container for the tools row
    Border ToolbarShell(Control inner)
    {
        var box = new Border
        {
            Background = PanelBg,                    
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true                       // prevent visual spill
        };
        box.Child = inner;
        return box;
    }

    // --- Terrain: Tools row (icons are unicode; swap to real icons WIP) ----
    Control TerrainToolsRow(GameObject owner, Terrain t)
    {
        var state = GetTerrainState(t);

        // Providers  assigned here; they read from the per-terrain state
        SceneView.TerrainToolIndexProvider = tt => GetTerrainState(tt).ToolIndex;
        SceneView.TerrainBrushRadiusProvider = tt => (float)GetTerrainState(tt).BrushSize;
        SceneView.TerrainBrushStrengthProvider = tt => (float)GetTerrainState(tt).Strength;
        SceneView.TerrainBrushFalloffProvider = tt => (float)GetTerrainState(tt).Falloff;

        var tools = new (int id, string tip, string glyph)[]
        {
        (0,"Raise/Lower","⛰"), (1,"Paint Holes","◯"), (2,"Noise","⋯"),
        (3,"Stitch/Blend","∞"), (4,"Sculpt","🖌"), (5,"Flatten","▭"),
        (6,"Erode","⛏"), (7,"Paint Layers","👤"), (8,"Smooth","〰")
        };

        var bar = new WrapPanel { Orientation = Orientation.Horizontal };

        // Helper to commit selection and keep buttons in sync
        void SetTool(int id)
        {
            state.ToolIndex = id; // id >= 0 selects, -1 clears
            foreach (var tb in bar.Children.OfType<ToggleButton>())
                tb.IsChecked = (id >= 0) && (int)tb.Tag! == id;
            Game_Engine.Core.SceneService.NotifyChanged(); // so SceneView refreshes hover ring, etc.
        }

        foreach (var tool in tools)
        {
            var b = new ToggleButton
            {
                Content = new TextBlock { Text = tool.glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center },
                MinWidth = 32,
                MinHeight = 28,
                Margin = new Thickness(3, 0, 3, 0),
                Tag = tool.id,
                IsChecked = (tool.id == state.ToolIndex)
            };
            ToolTip.SetTip(b, tool.tip);

            // When this button is turned on, it's the only one on
            b.Checked += (_, __) => SetTool(tool.id);

            // If user clicks the already-checked button, it becomes unchecked.
            // Only set -1 if no other tool is currently checked.
            b.Unchecked += (_, __) =>
            {
                if (!bar.Children.OfType<ToggleButton>().Any(x => x.IsChecked == true))
                    SetTool(-1); // OFF
            };

            bar.Children.Add(b);
        }



        // Brush sliders (size / strength / falloff)
        Control SliderRow(string label, double min, double max, Func<double> getter, Action<double> setter)
        {
            var grid = new Avalonia.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // label
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // slider
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // value

            var lb = new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center, Opacity = .8 };
            Avalonia.Controls.Grid.SetColumn(lb, 0);

            var sl = new Slider { Minimum = min, Maximum = max, Value = getter() };
            Avalonia.Controls.Grid.SetColumn(sl, 1);

            var val = new TextBlock { Text = getter().ToString(max <= 1.0 ? "0.00" : "0"), Width = 44, HorizontalAlignment = HorizontalAlignment.Right };
            Avalonia.Controls.Grid.SetColumn(val, 2);

            sl.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    var v = (double)sl.Value;
                    setter(v);
                    val.Text = v.ToString(max <= 1.0 ? "0.00" : "0");

                    // tell the scene to repaint so the ring updates live
                    Game_Engine.Core.SceneService.NotifyChanged();
                }
            };


            grid.Children.Add(lb);
            grid.Children.Add(sl);
            grid.Children.Add(val);
            return grid;
        }


        // Stack: [Tools wrap] + [Sliders block]
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(bar);

        var sliders = new StackPanel { Spacing = 4, Margin = new Thickness(2, 4, 2, 0) };
        sliders.Children.Add(SliderRow("Size", 1, 128, () => state.BrushSize, v => state.BrushSize = v));
        sliders.Children.Add(SliderRow("Strength", 0, 1, () => state.Strength, v => state.Strength = v));
        sliders.Children.Add(SliderRow("Falloff", 0, 1, () => state.Falloff, v => state.Falloff = v));

        content.Children.Add(sliders);

        var shell = new StackPanel { Spacing = 6 };
        shell.Children.Add(SectionTitle("Terrain Tools"));
        shell.Children.Add(ToolbarShell(content));
        return shell;
    }

    // --- Terrain: Brush mask selector --------------------------------------------
    Control TerrainBrushMasks(Terrain t)
    {
        var state = GetTerrainState(t);

        Border BrushButton(string glyph, int idx)
        {
            var tb = new ToggleButton
            {
                Content = new TextBlock { Text = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center },
                MinWidth = 36,
                MinHeight = 36,
                Margin = new Thickness(2)
            };
            tb.IsChecked = idx == state.BrushIndex;
            tb.Checked += (_, __) => { state.BrushIndex = idx; };
            tb.Checked += (_, __) =>
            {
                var parent = tb.Parent as Panel;
                if (parent != null)
                    foreach (var sib in parent.Children.OfType<Border>().Select(b => b.Child).OfType<ToggleButton>())
                        if (!ReferenceEquals(sib, tb)) sib.IsChecked = false;
            };
            return new Border
            {
                Background = PanelBg,               
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                Child = tb
            };
        }

        // Use a WrapPanel so brush chips wrap instead of running off
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        string[] glyphs = { "●", "○", "⬤", "◐", "⬡", "☆", "✦", "✳" };
        for (int i = 0; i < glyphs.Length; i++)
            row.Children.Add(BrushButton(glyphs[i], i));

        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(SectionTitle("Brush Masks"));

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerRow.Children.Add(new TextBlock { Text = "Brushes", VerticalAlignment = VerticalAlignment.Center });
        var newBrush = new Button { Content = "New Brush…" };
        headerRow.Children.Add(newBrush);
        top.Children.Add(headerRow);
        top.Children.Add(ToolbarShell(row)); // wrap row inside same gray shell

        var info = new Border
        {
            Background = PanelBg,                  // match gray
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = new TextBlock { Text = "The Custom brushs are Not Setup Yet.", Opacity = 1 }
        };
        top.Children.Add(info);

        return top;
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
