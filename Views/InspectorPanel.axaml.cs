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

public partial class InspectorPanel : UserControl
{
    private GameObject? _target;   // what THIS inspector is showing
    private bool _isLocked;        // lock state for THIS inspector
    private Window? OwnerWindow => this.GetVisualRoot() as Window;


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

    // Load preview (Avalonia Bitmap) and try to build an engine Texture2D via the helper above.
    // No Bitmap.Lock() anywhere — purely path/stream based.
    private static (Texture2D? tex, IImage? preview) TryLoadTexture2D(string path)
    {
        try
        {
            var bmp = new Bitmap(path);  // preview for the UI
            var tex = TryCreateEngineTextureFromPath(path, bmp);
            return (tex, bmp);
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

        // Make name undoable
        var pName = typeof(GameObject).GetProperty(nameof(GameObject.Name))!;
        nameBox.GotFocus += (_, __) => BeginPropertyEdit(go, pName);
        nameBox.LostFocus += (_, __) => CommitPropertyEdit(go, pName);

        Host.Children.Add(nameBox);

        // ---- Transform (mandatory) -----------------------------------------
        Host.Children.Add(SectionHeader("Transform"));
        Host.Children.Add(EditorForTransform(go.Transform));

        // ---- Add Component --------------------------------------------------
        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(LoadableTypes)
            .Where(t => t is not null && t.IsClass && !t.IsAbstract && typeof(Behavior).IsAssignableFrom(t))
            .Where(t => t != typeof(CoreTransform))
            .OrderBy(t => t!.Name)
            .ToList()!;

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var typeBox = new ComboBox
        {
            Width = 220,
            ItemsSource = allTypes,
            SelectedIndex = allTypes.Count > 0 ? 0 : -1,
            DisplayMemberBinding = new Binding("Name"),
        };
        var addBtn = new Button { Content = "Add Component", IsEnabled = allTypes.Count > 0 };
        addBtn.Click += (_, __) =>
        {
            if (typeBox.SelectedItem is Type t)
            {
                var inst = (Behavior)Activator.CreateInstance(t)!;
                go.AddBehavior(inst);
                SceneService.NotifyChanged();
                BuildUI(go);
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

        // Make enabling undoable and repaint immediately
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

        // --- properties area (disabled when b.Enabled == false) ---
        var propsPanel = new StackPanel { Spacing = 8 };
        propsPanel.Bind(IsEnabledProperty,
            new Binding(nameof(Behavior.Enabled)) { Source = b, Mode = BindingMode.OneWay });

        var props = b.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => p.Name is not nameof(Behavior.Enabled) && p.Name is not nameof(Behavior.gameObject))
            .ToList();

        foreach (var p in props)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = p.Name, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(PropertyEditor(b, p));
            propsPanel.Children.Add(row);
        }

        outer.Children.Add(propsPanel);

        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = outer
        };
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
                new FileDialogFilter { Name = "Images", Extensions = { "png","jpg","jpeg","bmp" } },
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
            Child = new TextBlock { Text = "Drop textures here (png/jpg/bmp)…", Opacity = .7 }
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
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) continue;

                var (tex, previewBmp) = TryLoadTexture2D(f);
                var name = Path.GetFileName(f);
                var nameNoExtLower = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();

                var slot = new MaterialTexture
                {
                    Name = name,
                    Texture = tex,
                    Usage = GuessUsageFromName(Path.GetFileNameWithoutExtension(f)),
                    FaceMask = (MaterialTexture.CubeFaceMask)GuessFaceMaskFromName(nameNoExtLower)
                };

                // store a (project-relative when possible) path for serialization
                slot.SourcePath = MakeProjectRelative(f);

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
                Filters = { new FileDialogFilter { Name = "Images", Extensions = { "png", "jpg", "jpeg", "bmp" } } }
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
