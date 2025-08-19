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
using CoreTransform = Game_Engine.Core.Transform;
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

public partial class InspectorPanel : UserControl
{
    private GameObject? _target;   // what THIS inspector is showing
    private bool _isLocked;        // lock state for THIS inspector

    // ---------- Undo snapshots (begin/commit) ----------
    readonly Dictionary<(object target, PropertyInfo prop), object?> _editStart = new();

    static object? SnapshotValue(Type t, object? v)
    {
        if (v is null) return null;
        if (t == typeof(CoreVector3) && v is CoreVector3 vv)
            return new CoreVector3(vv.X, vv.Y, vv.Z);
        return v; // structs, enums, numbers, strings, Mesh refs
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

    private void OnGlobalSelectionChanged(GameObject? go)
    {
        if (_isLocked) return; // ignore changes while locked
        _target = go;
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
            .Where(t => t != typeof(Core.Transform))
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

    Control SectionHeader(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 6, 0, 2)
    };

    Control EditorForTransform(CoreTransform t)
    {
        var grid = new Grid
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
        Grid.SetColumn(c, col);
        Grid.SetRow(c, row);
        if (columnSpan > 1) Grid.SetColumnSpan(c, columnSpan);
        if (rowSpan > 1) Grid.SetRowSpan(c, rowSpan);
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
