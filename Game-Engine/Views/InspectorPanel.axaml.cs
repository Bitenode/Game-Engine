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
using Game_Engine.Core.AI;
using Game_Engine.Core.Dialogue;
using Game_Engine.Core.Rendering;
using Game_Engine.Core.Timeline;
using System;
using System.Collections;
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
using SNVector3 = System.Numerics.Vector3;

namespace Game_Engine.Views;

file sealed class PrimitiveChoice
{
    public string Name { get; init; } = "";
    public Func<Game_Engine.Core.Mesh?> Factory { get; init; } = () => null;
    public override string ToString() => Name;
}

file sealed class DialogueNodeLinkItem
{
    public string Id { get; }
    public string Label { get; }
    public DialogueNodeLinkItem(string id, string label) { Id = id; Label = label; }
    public override string ToString() => Label;
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
                         p.Name != nameof(MeshCollider.Mesh)))
            // hide BehaviorTreeRunner properties handled by the custom BT editor
            .Where(p => !(b is BehaviorTreeRunner) ||
                        (p.Name != nameof(BehaviorTreeRunner.Tree) &&
                         p.Name != nameof(BehaviorTreeRunner.Blackboard) &&
                         p.Name != nameof(BehaviorTreeRunner.LastStatus)))
            // hide DialogueRunner properties handled by the custom Dialogue Tree editor
            .Where(p => !(b is DialogueRunner) ||
                        (p.Name != nameof(DialogueRunner.Tree) &&
                         p.Name != nameof(DialogueRunner.Variables) &&
                         p.Name != nameof(DialogueRunner.IsRunning) &&
                         p.Name != nameof(DialogueRunner.IsWaitingForInput) &&
                         p.Name != nameof(DialogueRunner.CurrentNode) &&
                         p.Name != nameof(DialogueRunner.Mode) &&
                         p.Name != nameof(DialogueRunner.VoiceVolume) &&
                         p.Name != nameof(DialogueRunner.AutoAdvanceOnVoiceEnd)))
            // hide TimelinePlayer properties handled by the custom timeline editor
            .Where(p => !(b is TimelinePlayer) ||
                        (p.Name != nameof(TimelinePlayer.Timeline) &&
                         p.Name != nameof(TimelinePlayer.CurrentTime) &&
                         p.Name != nameof(TimelinePlayer.IsPlaying) &&
                         p.Name != nameof(TimelinePlayer.IsFinished)));
    }

    // Default property panel (what we previously inlined)
    Control DefaultPropsPanel(Behavior b)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var p in InspectableProps(b))
        {
            // ── AudioSource.ClipPath: custom row with Import + drag-and-drop ──
            if (b is Game_Engine.Core.Component.AudioSource && p.Name == "ClipPath")
            {
                panel.Children.Add(BuildAudioClipRow(b, p));
                continue;
            }

            // ── Decal.TexturePath: custom row with Import + drag-and-drop ──
            if (b is Game_Engine.Core.Component.Decal && p.Name == "TexturePath")
            {
                panel.Children.Add(BuildDecalTextureRow(b, p));
                continue;
            }

            // ── VegetationPainter.CustomMeshPath: custom row with Import + drag-and-drop ──
            if (b is Game_Engine.Core.Component.VegetationPainter && p.Name == "CustomMeshPath")
            {
                panel.Children.Add(BuildVegetationMeshRow(b, p));
                continue;
            }

            // ── VegetationPainter.TexturePath: custom row with Import + drag-and-drop ──
            if (b is Game_Engine.Core.Component.VegetationPainter && p.Name == "TexturePath")
            {
                panel.Children.Add(BuildVegetationTextureRow(b, p));
                continue;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = p.Name, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(PropertyEditor(b, p));
            panel.Children.Add(row);
        }

        // ── VegetationPainter: Build / Rebuild / Clear buttons + instance count ──
        if (b is Game_Engine.Core.Component.VegetationPainter vp)
        {
            panel.Children.Add(BuildVegetationActionsPanel(vp));
        }
        else if (b is Game_Engine.Core.Component.PlanetAtmosphere pa)
        {
            panel.Children.Add(BuildPlanetAtmospherePresetPanel(pa));
        }

        return panel;
    }

    /// <summary>Convert an absolute path to a project-relative path, if possible.</summary>
    static string AudioAbsToRel(string abs)
    {
        var root = ProjectService.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root)) return abs.Replace('\\', '/');
        try
        {
            var full = Path.GetFullPath(abs);
            var projFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            if (full.StartsWith(projFull, StringComparison.OrdinalIgnoreCase))
                return full.Substring(projFull.Length).Replace('\\', '/');
            return full.Replace('\\', '/');
        }
        catch { return abs.Replace('\\', '/'); }
    }

    /// <summary>Builds a custom Inspector row for AudioSource.ClipPath with Import button + drag-and-drop.</summary>
    Control BuildAudioClipRow(Behavior audioSource, PropertyInfo clipPathProp)
    {
        var container = new StackPanel { Spacing = 4 };

        // ── Row 1: Label + path text + Import + Clear ──
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "ClipPath", Width = 120, VerticalAlignment = VerticalAlignment.Center });

        var tbPath = new TextBox
        {
            Width = 200,
            Watermark = "(none — import or drop audio file)",
            Text = (clipPathProp.GetValue(audioSource) as string) ?? ""
        };
        tbPath.GotFocus += (_, __) => BeginPropertyEdit(audioSource, clipPathProp);
        tbPath.LostFocus += (_, __) =>
        {
            clipPathProp.SetValue(audioSource, tbPath.Text);
            SceneService.NotifyChanged();
            CommitPropertyEdit(audioSource, clipPathProp);
        };

        var btnImport = new Button { Content = "Import…", Padding = new Thickness(8, 2) };
        var btnClear = new Button { Content = "Clear", Padding = new Thickness(8, 2) };

        row.Children.Add(tbPath);
        row.Children.Add(btnImport);
        row.Children.Add(btnClear);
        container.Children.Add(row);

        // ── Row 2: Drag-and-drop zone ──
        var dropText = new TextBlock
        {
            Text = "Drop audio file here  (.wav, .ogg, .mp3)",
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var dropZone = new Border
        {
            Margin = new Thickness(120, 0, 0, 0),  // indent to match label column
            Padding = new Thickness(10, 6),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            MinWidth = 280,
            MinHeight = 32,
            Child = dropText
        };
        DragDrop.SetAllowDrop(dropZone, true);
        container.Children.Add(dropZone);

        // Audio file extensions
        var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".wav", ".ogg", ".mp3", ".flac", ".aiff", ".aif", ".wma", ".m4a" };

        // ── Import button handler ──
        btnImport.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            // Start in the project's Assets folder (like all other importers)
            var assetsDir = ProjectService.Current?.AssetsPath;

            var dlg = new OpenFileDialog
            {
                Title = "Import Audio Clip",
                AllowMultiple = false,
                Directory = assetsDir,
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "Audio Files", Extensions = { "wav", "ogg", "mp3", "flac", "aiff" } },
                    new FileDialogFilter { Name = "All Files", Extensions = { "*" } }
                }
            };
            var files = await dlg.ShowAsync(win);
            var picked = files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(picked)) return;

            var relPath = AudioAbsToRel(picked);
            tbPath.Text = relPath;
            clipPathProp.SetValue(audioSource, relPath);
            SceneService.NotifyChanged();
        };

        // ── Clear button handler ──
        btnClear.Click += (_, __) =>
        {
            tbPath.Text = "";
            clipPathProp.SetValue(audioSource, "");
            SceneService.NotifyChanged();
        };

        // ── Drag-over handler ──
        dropZone.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            if (e.Data.Contains(DataFormats.FileNames) || e.Data.Contains(DataFormats.Files))
                e.DragEffects = DragDropEffects.Copy;
            else
                e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // ── Drop handler ──
        dropZone.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            string? pickedPath = null;

            if (e.Data.Contains(DataFormats.FileNames))
            {
                var names = e.Data.GetFileNames();
                if (names != null) pickedPath = names.FirstOrDefault();
            }

            if (pickedPath == null && e.Data.Contains(DataFormats.Files))
            {
                var items = e.Data.Get(DataFormats.Files) as IEnumerable<Avalonia.Platform.Storage.IStorageItem>;
                if (items != null)
                {
                    var file = items.FirstOrDefault() as Avalonia.Platform.Storage.IStorageFile;
                    if (file != null)
                    {
                        var local = file.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(local)) pickedPath = local;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(pickedPath)) return;
            if (!audioExts.Contains(System.IO.Path.GetExtension(pickedPath))) return;

            var relPath = AudioAbsToRel(pickedPath);
            tbPath.Text = relPath;
            clipPathProp.SetValue(audioSource, relPath);
            SceneService.NotifyChanged();
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        return container;
    }

    /// <summary>Builds a custom Inspector row for Decal.TexturePath with Import button + drag-and-drop.</summary>
    Control BuildDecalTextureRow(Behavior decal, PropertyInfo texPathProp)
    {
        var container = new StackPanel { Spacing = 4 };

        // ── Row 1: Label + path text + Import + Clear ──
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "TexturePath", Width = 120, VerticalAlignment = VerticalAlignment.Center });

        var tbPath = new TextBox
        {
            Width = 200,
            Watermark = "(none — import or drop image)",
            Text = (texPathProp.GetValue(decal) as string) ?? ""
        };
        tbPath.GotFocus += (_, __) => BeginPropertyEdit(decal, texPathProp);
        tbPath.LostFocus += (_, __) =>
        {
            texPathProp.SetValue(decal, tbPath.Text);
            SceneService.NotifyChanged();
            CommitPropertyEdit(decal, texPathProp);
        };

        var btnImport = new Button { Content = "Import…", Padding = new Thickness(8, 2) };
        var btnClear = new Button { Content = "Clear", Padding = new Thickness(8, 2) };

        row.Children.Add(tbPath);
        row.Children.Add(btnImport);
        row.Children.Add(btnClear);
        container.Children.Add(row);

        // ── Row 2: Drag-and-drop zone ──
        var dropText = new TextBlock
        {
            Text = "Drop image file here  (.png, .jpg, .tga, .bmp)",
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var dropZone = new Border
        {
            Margin = new Thickness(120, 0, 0, 0),
            Padding = new Thickness(10, 6),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            MinWidth = 280,
            MinHeight = 32,
            Child = dropText
        };
        DragDrop.SetAllowDrop(dropZone, true);
        container.Children.Add(dropZone);

        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tiff", ".gif", ".webp" };

        // ── Import button handler ──
        btnImport.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            var assetsDir = ProjectService.Current?.AssetsPath;

            var dlg = new OpenFileDialog
            {
                Title = "Import Decal Texture",
                AllowMultiple = false,
                Directory = assetsDir,
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "Image Files", Extensions = { "png", "jpg", "jpeg", "tga", "bmp", "tiff" } },
                    new FileDialogFilter { Name = "All Files", Extensions = { "*" } }
                }
            };
            var files = await dlg.ShowAsync(win);
            var picked = files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(picked)) return;

            var relPath = AudioAbsToRel(picked);
            tbPath.Text = relPath;
            texPathProp.SetValue(decal, relPath);
            SceneService.NotifyChanged();
        };

        // ── Clear button handler ──
        btnClear.Click += (_, __) =>
        {
            tbPath.Text = "";
            texPathProp.SetValue(decal, "");
            SceneService.NotifyChanged();
        };

        // ── Drag-over handler ──
        dropZone.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            if (e.Data.Contains(DataFormats.FileNames) || e.Data.Contains(DataFormats.Files))
                e.DragEffects = DragDropEffects.Copy;
            else
                e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // ── Drop handler ──
        dropZone.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            string? pickedPath = null;

            if (e.Data.Contains(DataFormats.FileNames))
            {
                var names = e.Data.GetFileNames();
                if (names != null) pickedPath = names.FirstOrDefault();
            }

            if (pickedPath == null && e.Data.Contains(DataFormats.Files))
            {
                var items = e.Data.Get(DataFormats.Files) as IEnumerable<Avalonia.Platform.Storage.IStorageItem>;
                if (items != null)
                {
                    var file = items.FirstOrDefault() as Avalonia.Platform.Storage.IStorageFile;
                    if (file != null)
                    {
                        var local = file.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(local)) pickedPath = local;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(pickedPath)) return;
            if (!imageExts.Contains(System.IO.Path.GetExtension(pickedPath))) return;

            var relPath = AudioAbsToRel(pickedPath);
            tbPath.Text = relPath;
            texPathProp.SetValue(decal, relPath);
            SceneService.NotifyChanged();
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        return container;
    }

    /// <summary>Builds action buttons for VegetationPainter: Build / Rebuild / Clear + instance count.</summary>
    Control BuildVegetationActionsPanel(Game_Engine.Core.Component.VegetationPainter vp)
    {
        var container = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };

        // ── Separator ──
        container.Children.Add(new Border
        {
            Height = 1,
            Background = Brushes.Gray,
            Opacity = 0.4,
            Margin = new Thickness(0, 2)
        });

        // ── Instance count label ──
        var lblCount = new TextBlock
        {
            Text = $"Instances: {vp.InstanceCount}",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Thickness(0, 2)
        };
        container.Children.Add(lblCount);

        // ── Button row ──
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var btnBuild = new Button
        {
            Content = "Build Grass",
            Padding = new Thickness(12, 4),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(40, 120, 40))
        };
        var btnRebuild = new Button
        {
            Content = "Rebuild",
            Padding = new Thickness(12, 4)
        };
        var btnClear = new Button
        {
            Content = "Clear All",
            Padding = new Thickness(12, 4),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(160, 50, 50))
        };

        btnRow.Children.Add(btnBuild);
        btnRow.Children.Add(btnRebuild);
        btnRow.Children.Add(btnClear);
        container.Children.Add(btnRow);

        // ── Build handler ──
        btnBuild.Click += (_, __) =>
        {
            int count = vp.BuildOnTerrain();
            lblCount.Text = $"Instances: {count}";
        };

        // ── Rebuild handler (clear + build) ──
        btnRebuild.Click += (_, __) =>
        {
            vp.ClearAll();
            int count = vp.BuildOnTerrain();
            lblCount.Text = $"Instances: {count}";
        };

        // ── Clear handler ──
        btnClear.Click += (_, __) =>
        {
            vp.ClearAll();
            lblCount.Text = $"Instances: {vp.InstanceCount}";
        };

        return container;
    }

    Control BuildPlanetAtmospherePresetPanel(Game_Engine.Core.Component.PlanetAtmosphere pa)
    {
        var container = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };

        container.Children.Add(new Border
        {
            Height = 1,
            Background = Brushes.Gray,
            Opacity = 0.4,
            Margin = new Thickness(0, 2)
        });

        container.Children.Add(new TextBlock
        {
            Text = "Quick Presets",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Thickness(0, 2)
        });

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        Button MakePresetButton(string title, Game_Engine.Core.Component.PlanetAtmospherePreset preset)
        {
            var btn = new Button { Content = title, Padding = new Thickness(10, 3), MinWidth = 90 };
            btn.Click += (_, __) =>
            {
                pa.ApplyPreset(preset);
                SceneService.NotifyChanged();
            };
            return btn;
        }

        row1.Children.Add(MakePresetButton("Thin", Game_Engine.Core.Component.PlanetAtmospherePreset.Thin));
        row1.Children.Add(MakePresetButton("EarthLike", Game_Engine.Core.Component.PlanetAtmospherePreset.EarthLike));
        row2.Children.Add(MakePresetButton("Dense", Game_Engine.Core.Component.PlanetAtmospherePreset.Dense));
        row2.Children.Add(MakePresetButton("AlienViolet", Game_Engine.Core.Component.PlanetAtmospherePreset.AlienViolet));

        container.Children.Add(row1);
        container.Children.Add(row2);
        return container;
    }

    /// <summary>Builds a custom Inspector row for VegetationPainter.CustomMeshPath with Import button + drag-and-drop.</summary>
    Control BuildVegetationMeshRow(Behavior painter, PropertyInfo meshPathProp)
    {
        var container = new StackPanel { Spacing = 4 };

        // ── Row 1: Label + path text + Import + Clear ──
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "CustomMeshPath", Width = 120, VerticalAlignment = VerticalAlignment.Center });

        var tbPath = new TextBox
        {
            Width = 200,
            Watermark = "(none — import or drop 3D model / texture)",
            Text = (meshPathProp.GetValue(painter) as string) ?? ""
        };
        tbPath.GotFocus += (_, __) => BeginPropertyEdit(painter, meshPathProp);
        tbPath.LostFocus += (_, __) =>
        {
            meshPathProp.SetValue(painter, tbPath.Text);
            SceneService.NotifyChanged();
            CommitPropertyEdit(painter, meshPathProp);
        };

        var btnImport = new Button { Content = "Import…", Padding = new Thickness(8, 2) };
        var btnClear = new Button { Content = "Clear", Padding = new Thickness(8, 2) };

        row.Children.Add(tbPath);
        row.Children.Add(btnImport);
        row.Children.Add(btnClear);
        container.Children.Add(row);

        // ── Row 2: Drag-and-drop zone ──
        var dropText = new TextBlock
        {
            Text = "Drop grass model or texture  (.fbx .obj .glb .png .jpg)",
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var dropZone = new Border
        {
            Margin = new Thickness(120, 0, 0, 0),
            Padding = new Thickness(10, 6),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            MinWidth = 280,
            MinHeight = 32,
            Child = dropText
        };
        DragDrop.SetAllowDrop(dropZone, true);
        container.Children.Add(dropZone);

        // Accept both 3D models and texture images
        var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 3D models
            ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds",
            // Textures
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tiff", ".gif", ".webp"
        };

        // ── Import button handler ──
        btnImport.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            var assetsDir = ProjectService.Current?.AssetsPath;

            var dlg = new OpenFileDialog
            {
                Title = "Import Grass Model or Texture",
                AllowMultiple = false,
                Directory = assetsDir,
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "3D Models", Extensions = { "fbx", "obj", "gltf", "glb", "dae", "3ds" } },
                    new FileDialogFilter { Name = "Textures", Extensions = { "png", "jpg", "jpeg", "tga", "bmp", "tiff" } },
                    new FileDialogFilter { Name = "All Files", Extensions = { "*" } }
                }
            };
            var files = await dlg.ShowAsync(win);
            var picked = files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(picked)) return;

            var relPath = AudioAbsToRel(picked);
            tbPath.Text = relPath;
            meshPathProp.SetValue(painter, relPath);
            SceneService.NotifyChanged();
        };

        // ── Clear button handler ──
        btnClear.Click += (_, __) =>
        {
            tbPath.Text = "";
            meshPathProp.SetValue(painter, "");
            SceneService.NotifyChanged();
        };

        // ── Drag-over handler ──
        dropZone.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            if (e.Data.Contains(DataFormats.FileNames) || e.Data.Contains(DataFormats.Files))
                e.DragEffects = DragDropEffects.Copy;
            else
                e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // ── Drop handler ──
        dropZone.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            string? pickedPath = null;

            if (e.Data.Contains(DataFormats.FileNames))
            {
                var names = e.Data.GetFileNames();
                if (names != null) pickedPath = names.FirstOrDefault();
            }

            if (pickedPath == null && e.Data.Contains(DataFormats.Files))
            {
                var items = e.Data.Get(DataFormats.Files) as IEnumerable<Avalonia.Platform.Storage.IStorageItem>;
                if (items != null)
                {
                    var file = items.FirstOrDefault() as Avalonia.Platform.Storage.IStorageFile;
                    if (file != null)
                    {
                        var local = file.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(local)) pickedPath = local;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(pickedPath)) return;
            if (!validExts.Contains(System.IO.Path.GetExtension(pickedPath))) return;

            var relPath = AudioAbsToRel(pickedPath);
            tbPath.Text = relPath;
            meshPathProp.SetValue(painter, relPath);
            SceneService.NotifyChanged();
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        return container;
    }

    /// <summary>Builds a custom Inspector row for VegetationPainter.TexturePath with Import button + drag-and-drop.</summary>
    Control BuildVegetationTextureRow(Behavior painter, PropertyInfo texPathProp)
    {
        var container = new StackPanel { Spacing = 4 };

        // ── Row 1: Label + path text + Import + Clear ──
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "TexturePath", Width = 120, VerticalAlignment = VerticalAlignment.Center });

        var tbPath = new TextBox
        {
            Width = 200,
            Watermark = "(none — import or drop texture)",
            Text = (texPathProp.GetValue(painter) as string) ?? ""
        };
        tbPath.GotFocus += (_, __) => BeginPropertyEdit(painter, texPathProp);
        tbPath.LostFocus += (_, __) =>
        {
            texPathProp.SetValue(painter, tbPath.Text);
            SceneService.NotifyChanged();
            CommitPropertyEdit(painter, texPathProp);
        };

        var btnImport = new Button { Content = "Import…", Padding = new Thickness(8, 2) };
        var btnClear = new Button { Content = "Clear", Padding = new Thickness(8, 2) };

        row.Children.Add(tbPath);
        row.Children.Add(btnImport);
        row.Children.Add(btnClear);
        container.Children.Add(row);

        // ── Row 2: Drag-and-drop zone ──
        var dropText = new TextBlock
        {
            Text = "Drop texture here  (.png, .jpg, .tga, .bmp)",
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var dropZone = new Border
        {
            Margin = new Thickness(120, 0, 0, 0),
            Padding = new Thickness(10, 6),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            MinWidth = 280,
            MinHeight = 32,
            Child = dropText
        };
        DragDrop.SetAllowDrop(dropZone, true);
        container.Children.Add(dropZone);

        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tiff", ".gif", ".webp" };

        // ── Import button handler ──
        btnImport.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            var assetsDir = ProjectService.Current?.AssetsPath;

            var dlg = new OpenFileDialog
            {
                Title = "Import Grass Texture",
                AllowMultiple = false,
                Directory = assetsDir,
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "Image Files", Extensions = { "png", "jpg", "jpeg", "tga", "bmp", "tiff" } },
                    new FileDialogFilter { Name = "All Files", Extensions = { "*" } }
                }
            };
            var files = await dlg.ShowAsync(win);
            var picked = files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(picked)) return;

            var relPath = AudioAbsToRel(picked);
            tbPath.Text = relPath;
            texPathProp.SetValue(painter, relPath);
            SceneService.NotifyChanged();
        };

        // ── Clear button handler ──
        btnClear.Click += (_, __) =>
        {
            tbPath.Text = "";
            texPathProp.SetValue(painter, "");
            SceneService.NotifyChanged();
        };

        // ── Drag-over handler ──
        dropZone.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            if (e.Data.Contains(DataFormats.FileNames) || e.Data.Contains(DataFormats.Files))
                e.DragEffects = DragDropEffects.Copy;
            else
                e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // ── Drop handler ──
        dropZone.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            string? pickedPath = null;

            if (e.Data.Contains(DataFormats.FileNames))
            {
                var names = e.Data.GetFileNames();
                if (names != null) pickedPath = names.FirstOrDefault();
            }

            if (pickedPath == null && e.Data.Contains(DataFormats.Files))
            {
                var items = e.Data.Get(DataFormats.Files) as IEnumerable<Avalonia.Platform.Storage.IStorageItem>;
                if (items != null)
                {
                    var file = items.FirstOrDefault() as Avalonia.Platform.Storage.IStorageFile;
                    if (file != null)
                    {
                        var local = file.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(local)) pickedPath = local;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(pickedPath)) return;
            if (!imageExts.Contains(System.IO.Path.GetExtension(pickedPath))) return;

            var relPath = AudioAbsToRel(pickedPath);
            tbPath.Text = relPath;
            texPathProp.SetValue(painter, relPath);
            SceneService.NotifyChanged();
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        return container;
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
        public string Category { get; set; } = "";  // category for grouping
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
        if (t == typeof(SNVector3) && v is SNVector3 sv)
            return new SNVector3(sv.X, sv.Y, sv.Z);
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
        if (t == typeof(SNVector3) && a is SNVector3 sa && b is SNVector3 sb)
            return sa.X == sb.X && sa.Y == sb.Y && sa.Z == sb.Z;
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
            var all = SelectionService.Selected;
            if (all.Count > 1)
            {
                // Multi-select: show all GameObjects
                var snapshot = new List<GameObject>(all);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => BuildMultiUI(snapshot));
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => BuildUI(_target));
            }
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

        var nameRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };

        var enabledCb = new CheckBox
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0)
        };
        enabledCb.Bind(CheckBox.IsCheckedProperty,
            new Binding(nameof(GameObject.Enabled)) { Source = go, Mode = BindingMode.TwoWay });
        var pEnabled = typeof(GameObject).GetProperty(nameof(GameObject.Enabled))!;
        enabledCb.GotFocus += (_, __) => BeginPropertyEdit(go, pEnabled);
        enabledCb.IsCheckedChanged += (_, __) => { SceneService.NotifyChanged(); CommitPropertyEdit(go, pEnabled); };
        nameRow.Children.Add(enabledCb);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 6), MinWidth = 140 };
        nameBox.Bind(TextBox.TextProperty, new Binding("Name") { Source = go, Mode = BindingMode.TwoWay });

        var pName = typeof(GameObject).GetProperty(nameof(GameObject.Name))!;
        nameBox.GotFocus += (_, __) => BeginPropertyEdit(go, pName);
        nameBox.LostFocus += (_, __) => CommitPropertyEdit(go, pName);
        nameRow.Children.Add(nameBox);

        Host.Children.Add(nameRow);

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
            .ToList();

        // Project scripts discovered from source files
        var scriptInfos = DiscoverProjectBehaviorScripts();
        scriptInfos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var scriptFullNames = new HashSet<string>(scriptInfos.Select(s => s.FullName), StringComparer.Ordinal);

        static string GetCategory(Type t)
        {
            var attr = t.GetCustomAttributes(typeof(ComponentCategoryAttribute), false);
            if (attr.Length > 0) return ((ComponentCategoryAttribute)attr[0]).Category;
            return "Misc";
        }

        var categoryMap = new SortedDictionary<string, List<ComboItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in builtInTypes)
        {
            var fn = t.FullName ?? t.Name;
            if (scriptFullNames.Contains(fn)) continue;
            var cat = GetCategory(t);
            if (!categoryMap.TryGetValue(cat, out var list)) { list = new List<ComboItem>(); categoryMap[cat] = list; }
            list.Add(new ComboItem { Display = t.Name, Category = cat, Type = t });
        }
        foreach (var list in categoryMap.Values)
            list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));


        var addComponentMenu = BuildAddComponentMenu(go, categoryMap, scriptInfos);

        var addBtn = new Button
        {
            Content = "+ Add Component",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 6),
            Margin = new Thickness(4, 2),
        };

        addBtn.Click += (_, __) => addComponentMenu.Open(addBtn);
        Host.Children.Add(addBtn);

        // ---- Separator before components ------------------------------------
        Host.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        // ---- Other behaviors -----------------------------------------------
        var behaviors = go.Behaviors.ToList();
        for (int i = 0; i < behaviors.Count; i++)
        {
            Host.Children.Add(EditorForBehavior(go, behaviors[i]));

            // Add a separator between components (not after the last one)
            if (i < behaviors.Count - 1)
                Host.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        }
    }

    ContextMenu BuildAddComponentMenu(GameObject go,
        SortedDictionary<string, List<ComboItem>> categoryMap,
        List<ScriptInfo> scriptInfos)
    {
        var menu = new ContextMenu();
        foreach (var kvp in categoryMap)
        {
            var subMenu = new MenuItem { Header = kvp.Key };
            foreach (var item in kvp.Value)
            {
                var ci = item;
                var mi = new MenuItem { Header = ci.Display };
                mi.Click += (_, __) =>
                {
                    if (ci.Type == null) return;
                    try
                    {
                        go.AddBehavior((Behavior)Activator.CreateInstance(ci.Type)!);
                        SceneService.NotifyChanged();
                        BuildUI(go);
                    }
                    catch (Exception ex) { ShowInfo("Failed to add component:\n" + ex.Message); }
                };
                subMenu.Items.Add(mi);
            }
            menu.Items.Add(subMenu);
        }

        if (scriptInfos.Count > 0)
        {
            menu.Items.Add(new Separator());
            var scriptSub = new MenuItem { Header = "Scripts" };
            foreach (var s in scriptInfos)
            {
                var loaded = TryResolveLoadedType(s.FullName);
                var sLabel = loaded != null ? s.Name : $"{s.Name}  (source only)";
                var sInfo = s; var sType = loaded;
                var mi = new MenuItem { Header = sLabel };
                mi.Click += (_, __) =>
                {
                    if (sType != null)
                    {
                        try
                        {
                            go.AddBehavior((Behavior)Activator.CreateInstance(sType)!);
                            SceneService.NotifyChanged();
                            BuildUI(go);
                        }
                        catch (Exception ex) { ShowInfo("Failed to add component:\n" + ex.Message); }
                    }
                    else
                    {
                        ShowInfo("Script not compiled yet:\n\n" + sInfo.FullName +
                                 "\n\nBuild to make it available.");
                        try { ScriptEditorWindow.Open(OwnerWindow, sInfo.FilePath); } catch { }
                    }
                };
                scriptSub.Items.Add(mi);
            }
            menu.Items.Add(scriptSub);
        }

        return menu;
    }

    /// <summary>Build the inspector for multiple selected GameObjects, each with a separator between them.</summary>
    void BuildMultiUI(List<GameObject> objects)
    {
        Host.Children.Clear();

        if (objects.Count == 0)
        {
            Host.Children.Add(new TextBlock { Text = "No selection", Opacity = 0.6, Margin = new Thickness(6) });
            return;
        }

        Host.Children.Add(new TextBlock
        {
            Text = $"{objects.Count} objects selected",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            Margin = new Thickness(6, 2, 6, 6)
        });

        for (int idx = 0; idx < objects.Count; idx++)
        {
            var go = objects[idx];

            // ---- Thick divider line between GameObjects ----
            Host.Children.Add(new Border
            {
                Height = 2,
                Background = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Margin = new Thickness(0, 8),
                CornerRadius = new CornerRadius(1)
            });

            // ---- Name header (bold, slightly larger) ----
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            var enabledCb = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0)
            };
            enabledCb.Bind(CheckBox.IsCheckedProperty,
                new Binding(nameof(GameObject.Enabled)) { Source = go, Mode = BindingMode.TwoWay });
            enabledCb.IsCheckedChanged += (_, __) => SceneService.NotifyChanged();
            nameRow.Children.Add(enabledCb);

            var nameLabel = new TextBlock
            {
                Text = go.Name,
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameRow.Children.Add(nameLabel);

            // Small editable name box
            var nameBox = new TextBox { Width = 160, FontSize = 12 };
            nameBox.Bind(TextBox.TextProperty, new Binding("Name") { Source = go, Mode = BindingMode.TwoWay });
            nameRow.Children.Add(nameBox);

            Host.Children.Add(nameRow);

            // ---- Transform ----
            Host.Children.Add(SectionHeader("Transform"));
            Host.Children.Add(EditorForTransform(go.Transform));

            // ---- Behaviors ----
            var behaviors = go.Behaviors.ToList();
            if (behaviors.Count > 0)
                Host.Children.Add(new Separator { Margin = new Thickness(0, 4) });

            for (int i = 0; i < behaviors.Count; i++)
            {
                Host.Children.Add(EditorForBehavior(go, behaviors[i]));

                if (i < behaviors.Count - 1)
                    Host.Children.Add(new Separator { Margin = new Thickness(0, 4) });
            }
        }
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

            // Tint: support both hex string "#RRGGBBAA" and float array [r,g,b,a]
            string tintHex = "#FFFFFFFF";
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Tint", out var tEl))
            {
                if (tEl.ValueKind == JsonValueKind.String)
                {
                    tintHex = tEl.GetString() ?? "#FFFFFFFF";
                }
                else if (tEl.ValueKind == JsonValueKind.Array && tEl.GetArrayLength() >= 3)
                {
                    float tr = (float)tEl[0].GetDouble();
                    float tg = (float)tEl[1].GetDouble();
                    float tb = (float)tEl[2].GetDouble();
                    float ta = tEl.GetArrayLength() >= 4 ? (float)tEl[3].GetDouble() : 1f;
                    int ir = Math.Clamp((int)(tr * 255f), 0, 255);
                    int ig = Math.Clamp((int)(tg * 255f), 0, 255);
                    int ib = Math.Clamp((int)(tb * 255f), 0, 255);
                    int ia = Math.Clamp((int)(ta * 255f), 0, 255);
                    // Format as #RRGGBBAA to match parseHex in BuildMaterialInspectorUI
                    tintHex = $"#{ir:X2}{ig:X2}{ib:X2}{ia:X2}";
                }
            }

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

    Control SNVector3ColorEditor(object owner, PropertyInfo p)
    {
        var v = (SNVector3)(p.GetValue(owner) ?? new SNVector3());
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var swatch = new Border
        {
            Width = 24, Height = 24,
            CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                (byte)Math.Clamp(v.X * 255f, 0, 255),
                (byte)Math.Clamp(v.Y * 255f, 0, 255),
                (byte)Math.Clamp(v.Z * 255f, 0, 255)))
        };

        TextBox MakeChannel(string label, float val)
        {
            var tb = new TextBox
            {
                Width = 52, FontSize = 11,
                Text = val.ToString("F3", CultureInfo.InvariantCulture),
                Watermark = label
            };
            return tb;
        }

        var tbR = MakeChannel("R", v.X);
        var tbG = MakeChannel("G", v.Y);
        var tbB = MakeChannel("B", v.Z);

        void Commit()
        {
            if (float.TryParse(tbR.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) &&
                float.TryParse(tbG.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) &&
                float.TryParse(tbB.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                p.SetValue(owner, new SNVector3(r, g, b));
                swatch.Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                    (byte)Math.Clamp(r * 255f, 0, 255),
                    (byte)Math.Clamp(g * 255f, 0, 255),
                    (byte)Math.Clamp(b * 255f, 0, 255)));
                SceneService.NotifyChanged();
            }
        }

        tbR.GotFocus += (_, __) => BeginPropertyEdit(owner, p);
        tbG.GotFocus += (_, __) => BeginPropertyEdit(owner, p);
        tbB.GotFocus += (_, __) => BeginPropertyEdit(owner, p);
        tbR.LostFocus += (_, __) => { Commit(); CommitPropertyEdit(owner, p); };
        tbG.LostFocus += (_, __) => { Commit(); CommitPropertyEdit(owner, p); };
        tbB.LostFocus += (_, __) => { Commit(); CommitPropertyEdit(owner, p); };
        tbR.PropertyChanged += (_, __) => Commit();
        tbG.PropertyChanged += (_, __) => Commit();
        tbB.PropertyChanged += (_, __) => Commit();

        row.Children.Add(swatch);
        row.Children.Add(new TextBlock { Text = "R", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        row.Children.Add(tbR);
        row.Children.Add(new TextBlock { Text = "G", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        row.Children.Add(tbG);
        row.Children.Add(new TextBlock { Text = "B", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        row.Children.Add(tbB);
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

        // Terrain extra UI (tools + brush masks + layers)
        if (b is Terrain terr)
        {
            outer.Children.Add(TerrainToolsRow(owner, terr));
            outer.Children.Add(TerrainBrushMasks(terr));
            outer.Children.Add(TerrainLayersUI(owner, terr));
        }

        // Tree extra UI (procedural / import settings)
        if (b is Tree treeComp)
        {
            outer.Children.Add(TreeInspectorUI(owner, treeComp));
        }

        // DialogueRunner: full dialogue tree editor
        if (b is DialogueRunner dialogueRunner)
        {
            outer.Children.Add(DialogueRunnerInspectorUI(dialogueRunner));
        }

        // BehaviorTreeRunner: visual behavior tree editor
        if (b is BehaviorTreeRunner btRunner)
        {
            outer.Children.Add(BehaviorTreeRunnerInspectorUI(btRunner));
        }

        // TimelinePlayer: inline timeline asset editor
        if (b is TimelinePlayer tlPlayer)
        {
            outer.Children.Add(TimelinePlayerInspectorUI(tlPlayer));
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
        var texturesOrder = new[] { "Albedo", "Normal", "Metallic", "Roughness", "Specular", "AmbientOcclusion", "Emissive", "Opacity" };
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
                    // First seed with standard slots (so they appear in order)
                    foreach (var k in texturesOrder)
                        texMap[k] = null;

                    // Read all keys from the file (handles both standard and custom names)
                    foreach (var prop2 in t.EnumerateObject())
                    {
                        if (prop2.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                            texMap[prop2.Name] = prop2.Value.GetString();
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

        // ── Material Preview Sphere ──
        var previewImage = new Image { Width = 200, Height = 200, Stretch = Avalonia.Media.Stretch.Fill };
        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(13, 13, 26)),
            CornerRadius = new CornerRadius(100),
            Width = 200,
            Height = 200,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = previewImage
        };
        var previewSection = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 8)
        };
        previewSection.Children.Add(new TextBlock
        {
            Text = "Material Preview",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        previewSection.Children.Add(previewBorder);

        // Shader name label below the sphere
        var shaderLabel = new TextBlock
        {
            Text = !string.IsNullOrWhiteSpace(shader) ? $"Shader: {System.IO.Path.GetFileNameWithoutExtension(shader)}" : "",
            FontSize = 10,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };
        if (!string.IsNullOrWhiteSpace(shader))
            previewSection.Children.Add(shaderLabel);

        previewSection.Children.Add(new TextBlock
        {
            Text = "Drag to rotate",
            FontSize = 9,
            Opacity = 0.35,
            FontStyle = Avalonia.Media.FontStyle.Italic,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        props.Children.Add(previewSection);

        // Forward-declare captured references (assigned after controls are created below)
        TextBox? tbTintRef = null;
        TextBox? tbShaderRef = null;
        Slider? sMetalRef = null;
        Slider? sRoughRef = null;

        // Sphere rotation state for drag interaction
        float previewRotY = 0f, previewRotX = 0f;
        bool previewDragging = false;
        Point previewDragStart = default;
        float previewDragStartRotY = 0f, previewDragStartRotX = 0f;

        // Helper: render the material preview sphere using current values
        Action renderMaterialPreview = null!;
        renderMaterialPreview = () =>
        {
            try
            {
                // Parse tint color
                float tR = 1f, tG = 1f, tB = 1f;
                try
                {
                    var hex = (tbTintRef?.Text ?? tintHex ?? "#FFFFFFFF").Trim();
                    if (hex.StartsWith("#")) hex = hex.Substring(1);
                    if (hex.Length >= 6)
                    {
                        tR = Convert.ToByte(hex.Substring(0, 2), 16) / 255f;
                        tG = Convert.ToByte(hex.Substring(2, 2), 16) / 255f;
                        tB = Convert.ToByte(hex.Substring(4, 2), 16) / 255f;
                    }
                }
                catch { }

                float curMetallic = (float)(sMetalRef?.Value ?? metallic);
                float curRoughness = (float)(sRoughRef?.Value ?? rough);

                var pbr = new MaterialPreviewRenderer.PBRParams
                {
                    Albedo = new System.Numerics.Vector3(tR, tG, tB),
                    Metallic = Math.Clamp(curMetallic, 0f, 1f),
                    Roughness = Math.Clamp(curRoughness, 0f, 1f),
                    Emission = System.Numerics.Vector3.Zero,
                    AO = 1f,
                    RotationY = previewRotY,
                    RotationX = previewRotX
                };

                // Load texture maps from project-relative paths
                if (texMap != null)
                {
                    foreach (var kvp in texMap)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Value)) continue;
                        string absTexPath = string.IsNullOrWhiteSpace(projRoot)
                            ? kvp.Value
                            : System.IO.Path.Combine(projRoot, kvp.Value.Replace('/', System.IO.Path.DirectorySeparatorChar));

                        int tw, th;
                        switch (kvp.Key.ToLowerInvariant())
                        {
                            case "albedo":
                                pbr.AlbedoPixels = MaterialPreviewRenderer.LoadTexturePixels(absTexPath, out tw, out th);
                                pbr.AlbedoWidth = tw; pbr.AlbedoHeight = th;
                                break;
                            case "normal":
                                pbr.NormalPixels = MaterialPreviewRenderer.LoadTexturePixels(absTexPath, out tw, out th);
                                pbr.NormalWidth = tw; pbr.NormalHeight = th;
                                break;
                            case "roughness":
                                pbr.RoughnessPixels = MaterialPreviewRenderer.LoadTexturePixels(absTexPath, out tw, out th);
                                pbr.RoughnessWidth = tw; pbr.RoughnessHeight = th;
                                break;
                            case "specular":
                                pbr.SpecularPixels = MaterialPreviewRenderer.LoadTexturePixels(absTexPath, out tw, out th);
                                pbr.SpecularWidth = tw; pbr.SpecularHeight = th;
                                break;
                            case "metallic":
                                pbr.MetallicPixels = MaterialPreviewRenderer.LoadTexturePixels(absTexPath, out tw, out th);
                                pbr.MetallicWidth = tw; pbr.MetallicHeight = th;
                                break;
                            case "emissive":
                                pbr.EmissivePixels = MaterialPreviewRenderer.LoadTexturePixels(absTexPath, out tw, out th);
                                pbr.EmissiveWidth = tw; pbr.EmissiveHeight = th;
                                break;
                            case "ambientocclusion":
                                pbr.AOPixels = MaterialPreviewRenderer.LoadTexturePixels(absTexPath, out tw, out th);
                                pbr.AOWidth = tw; pbr.AOHeight = th;
                                break;
                        }
                    }
                }

                // Blend shader graph PBR data if a shader is assigned
                string curShader = tbShaderRef?.Text ?? shader ?? "";
                if (!string.IsNullOrWhiteSpace(curShader))
                {
                    string absShaderPath = string.IsNullOrWhiteSpace(projRoot)
                        ? curShader
                        : System.IO.Path.Combine(projRoot, curShader.Replace('/', System.IO.Path.DirectorySeparatorChar));

                    var shaderPbr = MaterialPreviewRenderer.ExtractPBRFromShaderGraph(absShaderPath);
                    if (shaderPbr.HasValue)
                    {
                        var sp = shaderPbr.Value;
                        // Blend shader albedo with material tint
                        pbr.Albedo = new System.Numerics.Vector3(
                            pbr.Albedo.X * sp.Albedo.X,
                            pbr.Albedo.Y * sp.Albedo.Y,
                            pbr.Albedo.Z * sp.Albedo.Z);
                        // Use shader values where they provide non-default values
                        if (sp.Metallic > 0.01f) pbr.Metallic = Math.Max(pbr.Metallic, sp.Metallic);
                        if (sp.Roughness != 0.5f) pbr.Roughness = sp.Roughness;
                        if (sp.Emission.Length() > 0.01f) pbr.Emission = sp.Emission;
                        if (sp.HasFresnel) { pbr.HasFresnel = true; pbr.FresnelColor = sp.FresnelColor; pbr.FresnelPower = sp.FresnelPower; }
                        if (sp.HasNoiseAlbedo) { pbr.HasNoiseAlbedo = true; pbr.AlbedoBase = sp.AlbedoBase; pbr.NoiseScale = sp.NoiseScale; }
                    }
                }

                var bitmap = MaterialPreviewRenderer.Render(pbr, 200);
                previewImage.Source = bitmap;
            }
            catch { }
        };

        previewBorder.PointerPressed += (_, e) =>
        {
            previewDragging = true;
            previewDragStart = e.GetPosition(previewBorder);
            previewDragStartRotY = previewRotY;
            previewDragStartRotX = previewRotX;
            e.Pointer.Capture((Avalonia.Input.IInputElement)previewBorder);
            e.Handled = true;
        };
        previewBorder.PointerMoved += (_, e) =>
        {
            if (!previewDragging) return;
            var pos = e.GetPosition(previewBorder);
            float dx = (float)(pos.X - previewDragStart.X);
            float dy = (float)(pos.Y - previewDragStart.Y);
            previewRotY = previewDragStartRotY + dx * 0.015f;
            previewRotX = previewDragStartRotX + dy * 0.015f;
            previewRotX = Math.Clamp(previewRotX, -1.4f, 1.4f);
            renderMaterialPreview();
        };
        previewBorder.PointerReleased += (_, e) =>
        {
            previewDragging = false;
            e.Pointer.Capture(null);
        };
        previewBorder.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

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

        // Wire captured refs for preview renderer and add live-update callbacks
        tbTintRef = tbTint;
        tbShaderRef = tbShader;
        sMetalRef = sMetal;
        sRoughRef = sRough;

        sMetal.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) renderMaterialPreview(); };
        sRough.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) renderMaterialPreview(); };
        tbTint.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                renderMaterialPreview();
        };
        tbShader.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                // Update the shader label when shader changes
                shaderLabel.Text = !string.IsNullOrWhiteSpace(tbShader.Text)
                    ? $"Shader: {System.IO.Path.GetFileNameWithoutExtension(tbShader.Text)}" : "";
                renderMaterialPreview();
            }
        };

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
                renderMaterialPreview();
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
                    renderMaterialPreview();
                }
            };

            btnClear.Click += (_, __) => { tbPath.Text = ""; texMap[slot] = null; renderMaterialPreview(); };

            // Re-render preview when texture path changes
            tbPath.LostFocus += (_, __) =>
            {
                texMap[slot] = string.IsNullOrWhiteSpace(tbPath.Text) ? null : tbPath.Text;
                renderMaterialPreview();
            };

            var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            inner.Children.Add(tbPath);
            inner.Children.Add(btnPick);
            inner.Children.Add(btnClear);
            inner.Children.Add(drop);

            props.Children.Add(Labeled(slot, inner));
        };

        for (var i = 0; i < texturesOrder.Length; i++)
            addTextureRow(texturesOrder[i]);

        // Also show any extra texture keys not in the standard list
        var standardSet = new HashSet<string>(texturesOrder, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in texMap)
        {
            if (!standardSet.Contains(kvp.Key))
                addTextureRow(kvp.Key);
        }

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
                    // Write standard keys first (in order)
                    for (var i = 0; i < texturesOrder.Length; i++)
                    {
                        var key = texturesOrder[i];
                        var val = texMap.ContainsKey(key) ? texMap[key] : null;
                        if (string.IsNullOrWhiteSpace(val)) jw.WriteNull(key);
                        else jw.WriteString(key, val);
                    }
                    // Write any extra keys not in the standard list
                    var stdSet = new HashSet<string>(texturesOrder, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in texMap)
                    {
                        if (!stdSet.Contains(kvp.Key))
                        {
                            if (string.IsNullOrWhiteSpace(kvp.Value)) jw.WriteNull(kvp.Key);
                            else jw.WriteString(kvp.Key, kvp.Value);
                        }
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

        // Initial preview render
        renderMaterialPreview();
    }

    // UI-only cache so material path doesn't disappear if there's no sibling path or inner path slot
    private static readonly ConditionalWeakTable<object, Dictionary<string, string>> _matPathCache
        = new ConditionalWeakTable<object, Dictionary<string, string>>();

    private static string GetCachedMatPath(object owner, string propName)
    {
        Dictionary<string, string> map;
        if (_matPathCache.TryGetValue(owner, out map))
        {
            string v;
            if (map.TryGetValue(propName, out v)) return v;
        }
        return null;
    }

    private static void SetCachedMatPath(object owner, string propName, string relPath)
    {
        var map = _matPathCache.GetOrCreateValue(owner);
        if (string.IsNullOrWhiteSpace(relPath)) map.Remove(propName);
        else map[propName] = relPath;
    }



    Control MaterialEditor(object owner, PropertyInfo prop)
    {
        // ---------- helpers ----------
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;


        // Prefer operating on a MeshRenderer when present
        object GetListTarget(object own, PropertyInfo pr)
        {
            var mr = own as Game_Engine.Core.Component.MeshRenderer;
            if (mr != null) return mr;

            var go = own as Game_Engine.Core.GameObject;
            if (go != null)
            {
                var bs = go.Behaviors;
                for (int i = 0; i < bs.Count; i++)
                {
                    var r = bs[i] as Game_Engine.Core.Component.MeshRenderer;
                    if (r != null) return r;
                }
            }

            var beh = own as Game_Engine.Core.Behavior;
            if (beh != null && beh.gameObject != null)
            {
                var bs = beh.gameObject.Behaviors;
                for (int i = 0; i < bs.Count; i++)
                {
                    var r = bs[i] as Game_Engine.Core.Component.MeshRenderer;
                    if (r != null) return r;
                }
            }

            try
            {
                var propObj = pr != null ? pr.GetValue(own) : null;
                var r = propObj as Game_Engine.Core.Component.MeshRenderer;
                if (r != null) return r;
            }
            catch { }

            return own;
        }

        static string MakeProjectRelative(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            try
            {
                var abs = System.IO.Path.GetFullPath(fullPath);
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var rootDir = System.IO.Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                        return System.IO.Path.GetRelativePath(rootDir, abs).Replace('\\', '/');
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
                var rootDir = System.IO.Path.GetFullPath(proj.RootPath);
                if (abs.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase)) return abs;

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

        static Material TryCreateMaterialFromPath(string absOrRelPath)
        {
            try
            {
                var rel = MakeProjectRelative(absOrRelPath);
                var mat = ProjectService.MaterialsLoad(rel);
                if (mat != null) return mat;
            }
            catch { }

            try { return new Material(); }
            catch { return null; }
        }

        static string TryGetSiblingPath(object target, PropertyInfo matProp)
        {
            try
            {
                var name = matProp.Name + "Path";
                var pp = target.GetType().GetProperty(name, BF);
                if (pp != null && pp.PropertyType == typeof(string))
                {
                    var get = pp.GetGetMethod(true);
                    if (get != null)
                    {
                        var v = pp.GetValue(target) as string;
                        return string.IsNullOrWhiteSpace(v) ? null : v;
                    }
                }
                var ff = target.GetType().GetField(name, BF);
                if (ff != null && ff.FieldType == typeof(string))
                {
                    var v = ff.GetValue(target) as string;
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
                var pp = target.GetType().GetProperty(matProp.Name + "Path", BF);
                if (pp != null && pp.PropertyType == typeof(string))
                {
                    var set = pp.GetSetMethod(true);
                    if (set != null) pp.SetValue(target, projectRelPath);
                }
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
                    var rootEl = doc.RootElement;
                    var name = rootEl.TryGetProperty("name", out var n) ? (n.GetString() ?? "Material") : "Material";
                    var shader = rootEl.TryGetProperty("shader", out var s) ? (s.GetString() ?? "") : "";

                    bool transp = false; float met = 0f; float rgh = 0.5f;
                    System.Text.Json.JsonElement p;
                    if (rootEl.TryGetProperty("parameters", out p) && p.ValueKind == System.Text.Json.JsonValueKind.Object)
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

        // ====== NEW: auto-bind helpers ===========================================
        static string GetPathFromMaterial(Material m)
        {
            try
            {
                var t = m.GetType();
                var names = new[] { "AssetPath", "MaterialPath", "SourcePath", "Path", "FilePath" };
                for (int i = 0; i < names.Length; i++)
                {
                    var pi = t.GetProperty(names[i], BF);
                    if (pi != null && pi.PropertyType == typeof(string))
                    {
                        var v = pi.GetValue(m) as string;
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                    var fi = t.GetField(names[i], BF);
                    if (fi != null && fi.FieldType == typeof(string))
                    {
                        var v = fi.GetValue(m) as string;
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        static string GetMaterialName(Material m)
        {
            try
            {
                var t = m.GetType();
                var pi = t.GetProperty("Name", BF) ?? t.GetProperty("name", BF);
                if (pi != null && pi.PropertyType == typeof(string))
                {
                    var v = pi.GetValue(m) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            catch { }
            return null;
        }

        static IEnumerable<string> EnumerateProjectMaterials()
        {
            var results = new List<string>();
            try
            {
                var proj = ProjectService.Current;
                if (proj == null) return results;

                string rootDir = proj.RootPath;
                string assets = string.IsNullOrWhiteSpace(proj.AssetsPath) ? Path.Combine(rootDir, "Assets") : proj.AssetsPath;

                // common places first (ranked)
                var likely = new List<string>();
                var matsDir = Path.Combine(assets, "Materials");
                if (Directory.Exists(matsDir)) likely.Add(matsDir);
                if (Directory.Exists(assets)) likely.Add(assets);

                // unique & walk
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < likely.Count; i++)
                {
                    var d = likely[i];
                    if (!seen.Add(d)) continue;
                    try { results.AddRange(Directory.GetFiles(d, "*.material", SearchOption.AllDirectories)); }
                    catch { }
                }
            }
            catch { }
            return results;
        }

        static IEnumerable<string> TextureDirsFromMaterial(Material m)
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var t = m.GetType();
                var members = t.GetMembers(BF);
                for (int i = 0; i < members.Length; i++)
                {
                    var mi = members[i];
                    Type mt;
                    object val = null;
                    if (mi.MemberType == MemberTypes.Property)
                    {
                        var pi = (PropertyInfo)mi;
                        if (!pi.CanRead) continue;
                        mt = pi.PropertyType;
                        try { val = pi.GetValue(m); } catch { val = null; }
                    }
                    else if (mi.MemberType == MemberTypes.Field)
                    {
                        var fi = (FieldInfo)mi;
                        mt = fi.FieldType;
                        try { val = fi.GetValue(m); } catch { val = null; }
                    }
                    else continue;

                    // string "*Path"
                    if (mt == typeof(string) && mi.Name.EndsWith("Path", StringComparison.OrdinalIgnoreCase))
                    {
                        var s = val as string;
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            var abs = s;
                            try { abs = Path.GetFullPath(abs); } catch { }
                            try
                            {
                                if (!Path.IsPathRooted(s))
                                {
                                    var proj = ProjectService.Current;
                                    if (proj != null) abs = Path.GetFullPath(Path.Combine(proj.RootPath, s));
                                }
                            }
                            catch { }
                            try
                            {
                                var d = Path.GetDirectoryName(abs);
                                if (!string.IsNullOrWhiteSpace(d)) dirs.Add(d);
                            }
                            catch { }
                        }
                    }

                    // Texture2D with SourcePath/AssetPath
                    if (typeof(Texture2D).IsAssignableFrom(mt) && val is Texture2D tex)
                    {
                        try
                        {
                            var tp = tex.GetType().GetProperty("SourcePath", BF) ?? tex.GetType().GetProperty("AssetPath", BF);
                            var sp = tp != null ? tp.GetValue(tex) as string : null;
                            if (!string.IsNullOrWhiteSpace(sp))
                            {
                                var d = Path.GetDirectoryName(sp);
                                if (!string.IsNullOrWhiteSpace(d)) dirs.Add(d);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return dirs;
        }

        static string TryAutoFindMaterialAsset(Material mat)
        {
            if (mat == null) return null;

            // If material already knows its path, use it.
            var fromMat = GetPathFromMaterial(mat);
            if (!string.IsNullOrWhiteSpace(fromMat)) return fromMat;

            var name = GetMaterialName(mat);
            var candidates = new List<string>();

            // Look near textures used by this material
            foreach (var d in TextureDirsFromMaterial(mat))
            {
                try
                {
                    var hits = Directory.GetFiles(d, "*.material", SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < hits.Length; i++) candidates.Add(hits[i]);
                }
                catch { }
            }

            // Look under Assets/Materials/** (ranked primary bucket)
            foreach (var f in EnumerateProjectMaterials()) candidates.Add(f);

            // De-dup
            var uniq = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < candidates.Count; i++)
                if (seen.Add(candidates[i])) uniq.Add(candidates[i]);

            // Scoring: exact name match first, else first available
            if (!string.IsNullOrWhiteSpace(name))
            {
                for (int i = 0; i < uniq.Count; i++)
                {
                    var fn = Path.GetFileNameWithoutExtension(uniq[i]);
                    if (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase))
                        return uniq[i];
                }
            }

            return uniq.Count > 0 ? uniq[0] : null;
        }

        // ---------- container ----------
        var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8) };
        var panel = new StackPanel { Spacing = 10 };
        box.Child = panel;

        void BeginEdit() => BeginPropertyEdit(owner, prop);
        void CommitEdit() => CommitPropertyEdit(owner, prop);

        // ---------- header ----------
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock { Text = "Material (asset)", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);

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
        panel.Children.Add(row);

        var drop = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            Child = new TextBlock { Text = "Drop .material here…", Opacity = .7 }
        };
        DragDrop.SetAllowDrop(drop, true);
        panel.Children.Add(drop);

        var summary = new StackPanel { Spacing = 2 };
        panel.Children.Add(summary);

        // ---------- per-side list ----------
        panel.Children.Add(new TextBlock { Text = "Per-side materials", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });

        // Toolbar row: slots drop-down + side selector + arrows + remove
        var slotsToolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var slotsPanel = new StackPanel { Spacing = 6 };
        panel.Children.Add(slotsPanel);

        var btnAdd = new Button { Content = "Add from file…" };
        var btnAddNew = new Button { Content = "New material…" };
        var btnSaveAsset = new Button { Content = "Save as Asset…" };
        ToolTip.SetTip(btnSaveAsset, "Save the current runtime material as a .material file");
        slotsToolbar.Children.Add(btnAdd);
        slotsToolbar.Children.Add(btnAddNew);
        slotsToolbar.Children.Add(btnSaveAsset);
        panel.Children.Add(slotsToolbar);

        // internal model: (Material Mat, string RelPath, int Side)
        var slots = new List<(Material Mat, string RelPath, int Side)>();

        // seed from optional lists (MaterialPaths / MaterialSides / MaterialSlots)
        try
        {
            var listTarget = GetListTarget(owner, prop);
            var tTarget = listTarget.GetType();

            var pPaths = tTarget.GetProperty("MaterialPaths", BF);
            var pSides = tTarget.GetProperty("MaterialSides", BF);
            var pSlots = tTarget.GetProperty("MaterialSlots", BF);

            var fPaths = tTarget.GetField("MaterialPaths", BF);
            var fSides = tTarget.GetField("MaterialSides", BF);
            var fSlots = tTarget.GetField("MaterialSlots", BF);

            var paths = (pPaths != null ? pPaths.GetValue(listTarget) : (fPaths != null ? fPaths.GetValue(listTarget) : null)) as System.Collections.IList;
            var sides = (pSides != null ? pSides.GetValue(listTarget) : (fSides != null ? fSides.GetValue(listTarget) : null)) as System.Collections.IList;
            var mats = (pSlots != null ? pSlots.GetValue(listTarget) : (fSlots != null ? fSlots.GetValue(listTarget) : null)) as System.Collections.IList;

            if (paths != null && sides != null && mats != null)
            {
                int n = Math.Min(paths.Count, Math.Min(sides.Count, mats.Count));
                for (int i = 0; i < n; i++)
                {
                    var m = mats[i] as Material;
                    var rel = paths[i] as string;
                    int s = -1; try { s = Convert.ToInt32(sides[i]); } catch { s = -1; }
                    if (m == null && string.IsNullOrWhiteSpace(rel)) continue;

                    if (m == null && !string.IsNullOrWhiteSpace(rel))
                    {
                        try
                        {
                            var abs = ProjectService.Current != null ? Path.Combine(ProjectService.Current.RootPath, rel) : rel;
                            if (File.Exists(abs)) m = TryCreateMaterialFromPath(abs);
                        }
                        catch { }
                    }
                    slots.Add((m, rel, s));
                }
            }
        }
        catch { }

        void UpdateSummary(string projectRelOrAbs)
        {
            summary.Children.Clear();
            if (string.IsNullOrWhiteSpace(projectRelOrAbs) || projectRelOrAbs == "(none)" || projectRelOrAbs == "(unsaved)")
            {
                summary.Children.Add(new TextBlock { Text = "Runtime material (not saved)\nUse New… or Choose… to create/assign a .material file.", Opacity = .7 });
                return;
            }

            string abs = projectRelOrAbs;
            try
            {
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var rootDir = System.IO.Path.GetFullPath(proj.RootPath);
                    var p = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootDir, projectRelOrAbs));
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

        void WriteBackToOwner()
        {
            try
            {
                var listTarget = GetListTarget(owner, prop);
                var tTarget = listTarget.GetType();

                void AssignList(PropertyInfo pInfo, FieldInfo fInfo, Func<int, object> getter)
                {
                    object curVal = null; Type listType = null; Action<object> assign = null; bool canAssign = false;

                    if (pInfo != null)
                    {
                        listType = pInfo.PropertyType;
                        canAssign = pInfo.CanWrite;
                        if (canAssign) assign = v => { try { pInfo.SetValue(listTarget, v); } catch { } };
                        else curVal = pInfo.GetValue(listTarget);
                    }
                    else if (fInfo != null)
                    {
                        listType = fInfo.FieldType;
                        assign = v => { try { fInfo.SetValue(listTarget, v); } catch { } };
                        curVal = fInfo.GetValue(listTarget);
                    }
                    else return;

                    object newList = null;
                    System.Collections.IList newIList = null;
                    try
                    {
                        newList = Activator.CreateInstance(listType);
                        newIList = newList as System.Collections.IList;
                    }
                    catch { newList = null; newIList = null; }

                    if (newIList != null && assign != null)
                    {
                        for (int i = 0; i < slots.Count; i++) newIList.Add(getter(i));
                        assign(newList);
                        return;
                    }

                    var curIList = curVal as System.Collections.IList;
                    if (curIList != null)
                    {
                        lock (curIList)
                        {
                            curIList.Clear();
                            for (int i = 0; i < slots.Count; i++) curIList.Add(getter(i));
                        }
                    }
                }

                var pPathsProp = tTarget.GetProperty("MaterialPaths", BF);
                var pSidesProp = tTarget.GetProperty("MaterialSides", BF);
                var pSlotsProp = tTarget.GetProperty("MaterialSlots", BF);
                var pMatsProp = tTarget.GetProperty("Materials", BF);

                var pPathsField = tTarget.GetField("MaterialPaths", BF);
                var pSidesField = tTarget.GetField("MaterialSides", BF);
                var pSlotsField = tTarget.GetField("MaterialSlots", BF);
                var pMatsField = tTarget.GetField("Materials", BF);

                AssignList(pPathsProp, pPathsField, i => (object)slots[i].RelPath);
                AssignList(pSidesProp, pSidesField, i => (object)slots[i].Side);
                AssignList(pSlotsProp, pSlotsField, i => (object)slots[i].Mat);
                AssignList(pMatsProp, pMatsField, i => (object)slots[i].Mat);

                string firstRel = null;
                Material primaryMat = null;
                for (int i = 0; i < slots.Count; i++)
                    if (slots[i].Side == S_All)
                    { if (firstRel == null) firstRel = slots[i].RelPath; if (primaryMat == null) primaryMat = slots[i].Mat; }
                if (firstRel == null && slots.Count > 0) firstRel = slots[0].RelPath;
                if (primaryMat == null && slots.Count > 0) primaryMat = slots[0].Mat;

                try
                {
                    var name = prop.Name + "Path";
                    var pp = tTarget.GetProperty(name, BF);
                    if (pp != null && pp.PropertyType == typeof(string))
                    {
                        var set = pp.GetSetMethod(true);
                        if (set != null) pp.SetValue(listTarget, firstRel);
                    }
                    else
                    {
                        var ff = tTarget.GetField(name, BF);
                        if (ff != null && ff.FieldType == typeof(string))
                            ff.SetValue(listTarget, firstRel);
                    }
                }
                catch { }

                try
                {
                    if (primaryMat != null)
                    {
                        if (prop != null && prop.CanWrite && prop.PropertyType.IsAssignableFrom(typeof(Material)))
                        {
                            object targetForProp = listTarget;
                            if (!prop.DeclaringType.IsInstanceOfType(targetForProp) && prop.DeclaringType.IsInstanceOfType(owner))
                                targetForProp = owner;
                            prop.SetValue(targetForProp, primaryMat);
                        }
                        else
                        {
                            var mp = tTarget.GetProperty("Material", BF);
                            if (mp != null && mp.CanWrite && mp.PropertyType.IsAssignableFrom(typeof(Material)))
                                mp.SetValue(listTarget, primaryMat);
                            else
                            {
                                var mf = tTarget.GetField("Material", BF);
                                if (mf != null && typeof(Material).IsAssignableFrom(mf.FieldType))
                                    mf.SetValue(listTarget, primaryMat);
                            }
                        }
                    }
                }
                catch { }

                // keep UI cache
                SetCachedMatPath(owner, prop.Name, firstRel);

                // ensure renderer re-resolves
                try
                {
                    var mr2 = listTarget as Game_Engine.Core.Component.MeshRenderer;
                    if (mr2 != null) mr2.ResolveMaterials();
                }
                catch { }

                SceneService.NotifyChanged();

                try
                {
                    var il = (pPathsProp != null ? pPathsProp.GetValue(listTarget) :
                             (pPathsField != null ? pPathsField.GetValue(listTarget) : null)) as System.Collections.IList;
                    System.Diagnostics.Debug.WriteLine("[MatTrace:Inspector] Wrote MR lists: paths=" + (il != null ? il.Count : 0) +
                                                       " slots=" + slots.Count + " sides=" + slots.Count);
                }
                catch { }
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

                int rowIndex = i;
                sideBox.SelectionChanged += (_, __) =>
                {
                    int idx = sideBox.SelectedIndex;
                    if (idx < 0 || idx >= sideVals.Length) return;

                    var curTuple = slots[rowIndex];
                    slots[rowIndex] = (curTuple.Mat, curTuple.RelPath, sideVals[idx]);

                    BeginEdit();
                    WriteBackToOwner();
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
                        CommitEdit();
                        RebuildListUI();
                    }
                };
                rowSlot.Children.Add(btnRemove);

                slotsPanel.Children.Add(rowSlot);
            }
        }

        // ---------- assigners ----------
        Action<string> AssignFromPathTop = (pickedAbs) =>
        {
            var abs = EnsureInProject(pickedAbs, "Materials");
            var rel = MakeProjectRelative(abs);

            BeginEdit();

            var loaded = TryCreateMaterialFromPath(abs);

            try
            {
                var targetForProp = owner;
                if (!prop.DeclaringType.IsInstanceOfType(targetForProp))
                {
                    var lt = GetListTarget(owner, prop);
                    if (prop.DeclaringType.IsInstanceOfType(lt))
                        targetForProp = lt;
                }

                if (prop.CanWrite && loaded != null && prop.PropertyType.IsAssignableFrom(typeof(Material)))
                    prop.SetValue(targetForProp, loaded);
            }
            catch { }

            SceneService.NotifyChanged();

            int idxAll = -1;
            for (int k = 0; k < slots.Count; k++) if (slots[k].Side == S_All) { idxAll = k; break; }
            var s = (loaded, rel, S_All);
            if (idxAll >= 0) slots[idxAll] = s; else slots.Insert(0, s);

            WriteBackToOwner();
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

            slots.Add((loaded, rel, side));

            BeginEdit();
            WriteBackToOwner();
            CommitEdit();
            RebuildListUI();
        };

        // Find the primary Material on the MeshRenderer-ish target
        static Material GetPrimaryMaterial(object listTarget, PropertyInfo propInfo)
        {
            Material mat = null;
            try
            {
                if (propInfo != null && propInfo.CanRead && propInfo.DeclaringType.IsInstanceOfType(listTarget))
                    mat = propInfo.GetValue(listTarget) as Material;
            }
            catch { }

            if (mat == null)
            {
                try
                {
                    var mp = listTarget.GetType().GetProperty("Material", BF);
                    if (mp != null && mp.PropertyType.IsAssignableFrom(typeof(Material)))
                        mat = mp.GetValue(listTarget) as Material;
                }
                catch { }
            }

            if (mat == null)
            {
                try
                {
                    var matsObj = (listTarget.GetType().GetProperty("Materials", BF)?.GetValue(listTarget))
                                  ?? (listTarget.GetType().GetField("Materials", BF)?.GetValue(listTarget));
                    var matsIL = matsObj as System.Collections.IList;
                    if (matsIL != null && matsIL.Count > 0)
                        mat = matsIL[0] as Material;
                }
                catch { }
            }

            return mat;
        }

        // ---------- initial header text ----------
        {
            string initialRel = null;

            var listTargetHeader = GetListTarget(owner, prop);
            if (string.IsNullOrWhiteSpace(initialRel))
                initialRel = TryGetSiblingPath(listTargetHeader, prop);

            if (string.IsNullOrWhiteSpace(initialRel))
                initialRel = GetCachedMatPath(owner, prop.Name);

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

            // Final fallback — inspect the runtime material and try to auto-bind a .material file
            if (string.IsNullOrWhiteSpace(initialRel))
            {
                var matForSeed = GetPrimaryMaterial(listTargetHeader, prop);
                if (matForSeed != null)
                {
                    var guessAbs = TryAutoFindMaterialAsset(matForSeed);
                    if (!string.IsNullOrWhiteSpace(guessAbs) && File.Exists(guessAbs))
                    {
                        System.Diagnostics.Debug.WriteLine("[MatTrace:Inspector] Auto-bound material asset: " + guessAbs);
                        AssignFromPathTop(guessAbs); // this updates lists + tbPath + summary
                                                     // AssignFromPathTop already set everything; stop further init header work
                        return box;
                    }
                }
            }

            tbPath.Text = string.IsNullOrWhiteSpace(initialRel) ? "(unsaved)" : initialRel;
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
  ""name"": ""NewMaterial"",
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
            if (string.IsNullOrWhiteSpace(tbPath.Text) || tbPath.Text == "(none)" || tbPath.Text == "(unsaved)") return;

            string abs = tbPath.Text;
            try
            {
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var rootDir = System.IO.Path.GetFullPath(proj.RootPath);
                    var p = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootDir, tbPath.Text));
                    if (File.Exists(p)) abs = p;
                }
            }
            catch { }

            try
            {
                _assetInspectorActive = true;
                OnAssetSelected(abs);
            }
            catch { }
        };

        btnClear.Click += (_, __) =>
        {
            BeginEdit();

            slots.Clear();
            WriteBackToOwner();

            try
            {
                if (prop != null && prop.CanWrite &&
                    prop.PropertyType.IsAssignableFrom(typeof(Material)))
                    prop.SetValue(owner, null);

                TrySetSiblingPath(GetListTarget(owner, prop), prop, null);
                SetCachedMatPath(owner, prop.Name, null);
            }
            catch { }

            CommitEdit();

            tbPath.Text = "(none)";
            summary.Children.Clear();
            RebuildListUI();
            SceneService.NotifyChanged();
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
                    AddExtraSlot(files[i], S_Right); // default side
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
  ""name"": ""NewMaterial"",
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

        // Save current runtime material as a .material asset file
        btnSaveAsset.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            // Get the primary material from the MeshRenderer
            var matForSave = GetPrimaryMaterial(GetListTarget(owner, prop), prop);
            if (matForSave == null)
            {
                summary.Children.Clear();
                summary.Children.Add(new TextBlock { Text = "No runtime material to save.", Foreground = Brushes.OrangeRed });
                return;
            }

            // Determine default directory and name
            string defaultDir = null;
            string defaultName = matForSave.Name ?? "SavedMaterial";
            try
            {
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    defaultDir = System.IO.Path.Combine(proj.RootPath, "Assets", "Materials");
                    if (!System.IO.Directory.Exists(defaultDir))
                        System.IO.Directory.CreateDirectory(defaultDir);
                }
            }
            catch { }

            var sfd = new SaveFileDialog
            {
                Title = "Save Material As",
                InitialFileName = defaultName + ".material",
                Directory = defaultDir,
                Filters = { new FileDialogFilter { Name = "Material", Extensions = { "material" } } }
            };

            var dest = await sfd.ShowAsync(win);
            if (string.IsNullOrWhiteSpace(dest)) return;

            try
            {
                // Build texture dictionary from RuntimeTexSlots
                var texDict = new Dictionary<string, string>();
                if (matForSave.Textures != null)
                {
                    foreach (var slot in matForSave.Textures)
                    {
                        if (slot is RuntimeTexSlot rts && !string.IsNullOrWhiteSpace(rts.SourcePath))
                        {
                            var usage = rts.Usage ?? "Albedo";
                            if (!texDict.ContainsKey(usage))
                                texDict[usage] = rts.SourcePath;
                        }
                    }
                }

                var jsonObj = new Dictionary<string, object>
                {
                    ["name"] = matForSave.Name ?? defaultName,
                    ["type"] = "Material",
                    ["version"] = 1,
                    ["shader"] = matForSave.ShaderAssetPath ?? "",
                    ["parameters"] = new Dictionary<string, object>
                    {
                        ["Tint"] = new float[] { matForSave.BaseColor.R / 255f, matForSave.BaseColor.G / 255f, matForSave.BaseColor.B / 255f, matForSave.BaseColor.A / 255f },
                        ["Metallic"] = matForSave.Metallic,
                        ["Roughness"] = matForSave.Roughness,
                        ["Transparent"] = matForSave.Transparent,
                        ["AlphaCutoff"] = matForSave.AlphaCutoff
                    },
                    ["textures"] = texDict
                };

                var json = System.Text.Json.JsonSerializer.Serialize(jsonObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dest, json);

                // Make project-relative and update MaterialPaths on the renderer
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var relPath = System.IO.Path.GetRelativePath(proj.RootPath, System.IO.Path.GetFullPath(dest));
                    tbPath.Text = relPath;
                    UpdateSummary(relPath);

                    // Update the owner's MaterialPaths if possible
                    try
                    {
                        var listTarget = GetListTarget(owner, prop);
                        var mr = listTarget as MeshRenderer;
                        if (mr != null)
                        {
                            if (mr.MaterialPaths.Count == 0)
                                mr.MaterialPaths.Add(relPath);
                            else
                                mr.MaterialPaths[0] = relPath;
                        }
                    }
                    catch { }
                }
                else
                {
                    tbPath.Text = dest;
                    UpdateSummary(dest);
                }
            }
            catch (Exception ex)
            {
                summary.Children.Clear();
                summary.Children.Add(new TextBlock { Text = "Save failed: " + ex.Message, Foreground = Brushes.OrangeRed });
            }
        };

        // ---------- initial render ----------
        RebuildListUI();

        if ((tbPath.Text == "(none)" || tbPath.Text == "(unsaved)") && slots.Count > 0 && !string.IsNullOrWhiteSpace(slots[0].RelPath))
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
        public int ActivePaintLayer;  // splatmap layer for Paint Layers tool (0-7)
        // Paint Trees tool settings
        public int TreeDensity = 3;          // trees per stroke (1-20)
        public double TreeMinScale = 0.8;    // minimum random scale
        public double TreeMaxScale = 1.2;    // maximum random scale
        public bool TreeRandomRotation = true;  // random Y rotation
        // Multi-asset tree painting
        public List<TreeAssetEntry> TreeAssets = new(); // list of tree model paths
        public int ActiveTreeAsset;                      // index of selected tree asset (-1 or 0+ = imported, no entry = procedural)
    }

    /// <summary>A tree model asset entry for multi-asset tree painting.</summary>
    sealed class TreeAssetEntry
    {
        public string ModelPath { get; set; } = "";
        public string DisplayName => string.IsNullOrEmpty(ModelPath) ? "(procedural)" : System.IO.Path.GetFileNameWithoutExtension(ModelPath);
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
        SceneView.TerrainActivePaintLayerProvider = tt => GetTerrainState(tt).ActivePaintLayer;
        // Paint Trees providers
        SceneView.TerrainTreeDensityProvider = tt => GetTerrainState(tt).TreeDensity;
        SceneView.TerrainTreeMinScaleProvider = tt => (float)GetTerrainState(tt).TreeMinScale;
        SceneView.TerrainTreeMaxScaleProvider = tt => (float)GetTerrainState(tt).TreeMaxScale;
        SceneView.TerrainTreeRandomRotProvider = tt => GetTerrainState(tt).TreeRandomRotation;
        SceneView.TerrainTreeModelPathProvider = tt =>
        {
            var st = GetTerrainState(tt);
            if (st.TreeAssets.Count > 0 && st.ActiveTreeAsset >= 0 && st.ActiveTreeAsset < st.TreeAssets.Count)
                return st.TreeAssets[st.ActiveTreeAsset].ModelPath;
            return null; // procedural
        };

        var tools = new (int id, string tip, string glyph)[]
        {
        (0,"Raise/Lower","⛰"), (1,"Paint Holes","◯"), (2,"Noise","⋯"),
        (3,"Stitch/Blend","∞"), (4,"Sculpt","🖌"), (5,"Flatten","▭"),
        (6,"Erode","⛏"), (7,"Paint Layers","👤"), (8,"Smooth","〰"),
        (9,"Paint Trees","🌲")
        };

        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        StackPanel? _treeSettingsPanel = null; // set later, referenced in SetTool

        // Helper to commit selection and keep buttons in sync
        void SetTool(int id)
        {
            state.ToolIndex = id; // id >= 0 selects, -1 clears
            foreach (var tb in bar.Children.OfType<ToggleButton>())
                tb.IsChecked = (id >= 0) && (int)tb.Tag! == id;
            if (_treeSettingsPanel != null) _treeSettingsPanel.IsVisible = (id == 9);
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

        // Paint Trees extra settings (visible when tool #9 is active)
        var treeSettings = new StackPanel { Spacing = 4, Margin = new Thickness(2, 4, 2, 0), IsVisible = state.ToolIndex == 9 };
        _treeSettingsPanel = treeSettings; // so SetTool can toggle visibility
        treeSettings.Children.Add(new TextBlock { Text = "Tree Painting", FontWeight = FontWeight.Bold, Opacity = 0.9 });
        treeSettings.Children.Add(SliderRow("Density", 1, 20, () => state.TreeDensity, v => state.TreeDensity = Math.Max(1, (int)v)));
        treeSettings.Children.Add(SliderRow("Min Scale", 0.1, 3.0, () => state.TreeMinScale, v => state.TreeMinScale = v));
        treeSettings.Children.Add(SliderRow("Max Scale", 0.1, 3.0, () => state.TreeMaxScale, v => state.TreeMaxScale = v));

        // Random rotation checkbox
        var rotCheck = new CheckBox { Content = "Random Y Rotation", IsChecked = state.TreeRandomRotation, Margin = new Thickness(0, 2, 0, 0) };
        rotCheck.IsCheckedChanged += (_, _) =>
        {
            state.TreeRandomRotation = rotCheck.IsChecked == true;
        };
        treeSettings.Children.Add(rotCheck);

        // ── Tree Asset List (switch between different 3D model files) ──
        treeSettings.Children.Add(new TextBlock { Text = "Tree Assets", FontWeight = FontWeight.Bold, Opacity = 0.9, Margin = new Thickness(0, 6, 0, 2) });

        var treeAssetsPanel = new StackPanel { Spacing = 2 };

        void RebuildTreeAssetList()
        {
            treeAssetsPanel.Children.Clear();

            // "Procedural" entry (always first)
            {
                bool isActive = state.TreeAssets.Count == 0 || state.ActiveTreeAsset < 0 || state.ActiveTreeAsset >= state.TreeAssets.Count;
                var procRow = new Border
                {
                    Background = isActive ? new SolidColorBrush(Color.FromArgb(50, 80, 200, 80)) : Brushes.Transparent,
                    CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 2), Margin = new Thickness(0, 1)
                };
                var procBtn = new Button
                {
                    Content = new TextBlock { Text = "Procedural (default)", FontSize = 11 },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 2)
                };
                procBtn.Click += (_, __) => { state.ActiveTreeAsset = -1; RebuildTreeAssetList(); };
                procRow.Child = procBtn;
                treeAssetsPanel.Children.Add(procRow);
            }

            // Imported model entries
            for (int ai = 0; ai < state.TreeAssets.Count; ai++)
            {
                int assetIdx = ai;
                var asset = state.TreeAssets[assetIdx];
                bool isActive = state.ActiveTreeAsset == assetIdx;

                var assetRow = new Border
                {
                    Background = isActive ? new SolidColorBrush(Color.FromArgb(50, 80, 200, 80)) : Brushes.Transparent,
                    CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 2), Margin = new Thickness(0, 1)
                };

                var rowStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

                // Select button
                var selBtn = new Button
                {
                    Content = new TextBlock { Text = asset.DisplayName, FontSize = 11, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis },
                    Padding = new Thickness(4, 2)
                };
                ToolTip.SetTip(selBtn, asset.ModelPath);
                selBtn.Click += (_, __) => { state.ActiveTreeAsset = assetIdx; RebuildTreeAssetList(); };
                rowStack.Children.Add(selBtn);

                // Remove button
                var rmBtn = new Button
                {
                    Content = new TextBlock { Text = "X", FontSize = 10 },
                    MinWidth = 22, MinHeight = 20, Padding = new Thickness(2)
                };
                rmBtn.Click += (_, __) =>
                {
                    state.TreeAssets.RemoveAt(assetIdx);
                    if (state.ActiveTreeAsset >= state.TreeAssets.Count)
                        state.ActiveTreeAsset = state.TreeAssets.Count - 1;
                    RebuildTreeAssetList();
                };
                rowStack.Children.Add(rmBtn);

                assetRow.Child = rowStack;
                treeAssetsPanel.Children.Add(assetRow);
            }

            // Add button
            var addBtn = new Button
            {
                Content = new TextBlock { Text = "+ Add Tree Model", FontSize = 11 },
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 2, 0, 0)
            };
            addBtn.Click += async (_, __) =>
            {
                var dlg = new Avalonia.Controls.OpenFileDialog
                {
                    Title = "Select Tree 3D Model",
                    AllowMultiple = false,
                    Filters = new System.Collections.Generic.List<Avalonia.Controls.FileDialogFilter>
                    {
                        new() { Name = "3D Models", Extensions = { "obj", "fbx", "gltf", "glb", "dae" } },
                        new() { Name = "All files", Extensions = { "*" } }
                    }
                };
                var win = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
                if (win == null) return;
                var files = await dlg.ShowAsync(win);
                if (files == null || files.Length == 0) return;
                string abs = files[0];
                // Make project-relative if possible
                var proj = Game_Engine.Core.ProjectService.Current;
                string rel = abs;
                if (proj != null && abs.StartsWith(proj.RootPath, StringComparison.OrdinalIgnoreCase))
                    rel = Path.GetRelativePath(proj.RootPath, abs);
                state.TreeAssets.Add(new TreeAssetEntry { ModelPath = rel });
                state.ActiveTreeAsset = state.TreeAssets.Count - 1;
                RebuildTreeAssetList();
            };
            treeAssetsPanel.Children.Add(addBtn);
        }

        RebuildTreeAssetList();
        treeSettings.Children.Add(treeAssetsPanel);

        content.Children.Add(treeSettings);

        var shell = new StackPanel { Spacing = 6 };
        shell.Children.Add(SectionTitle("Terrain Tools"));
        shell.Children.Add(ToolbarShell(content));
        return shell;
    }

    // --- Terrain: Layers UI (multi-material painting) ---------------------------
    Control TerrainLayersUI(GameObject owner, Terrain t)
    {
        var state = GetTerrainState(t);
        var layersPanel = new StackPanel { Spacing = 4 };

        void RebuildLayerList()
        {
            layersPanel.Children.Clear();
            for (int li = 0; li < t.Layers.Count && li < 8; li++)
            {
                int idx = li; // capture
                var layer = t.Layers[idx];
                var row = new Border
                {
                    Background = (state.ActivePaintLayer == idx)
                        ? new SolidColorBrush(Color.FromArgb(50, 100, 180, 255))
                        : Brushes.Transparent,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4),
                    Margin = new Thickness(0, 1)
                };

                var grid = new Avalonia.Controls.Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28)));  // select
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));     // texture path
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(80)));  // tiling
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28)));  // remove

                // Select button (click to set active paint layer)
                var selectBtn = new Button
                {
                    Content = new TextBlock { Text = $"{idx}", FontSize = 11 },
                    MinWidth = 24, MinHeight = 24,
                    Padding = new Thickness(2),
                    Tag = idx,
                };
                ToolTip.SetTip(selectBtn, $"Select Layer {idx} for painting");
                selectBtn.Click += (_, __) =>
                {
                    state.ActivePaintLayer = idx;
                    RebuildLayerList(); // refresh highlighting
                };
                Avalonia.Controls.Grid.SetColumn(selectBtn, 0);
                grid.Children.Add(selectBtn);

                // Texture path label + choose button
                var texStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var texLabel = new TextBlock
                {
                    Text = string.IsNullOrEmpty(layer.TexturePath) ? "(none)" : Path.GetFileName(layer.TexturePath),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    MaxWidth = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                ToolTip.SetTip(texLabel, layer.TexturePath);
                var chooseBtn = new Button
                {
                    Content = new TextBlock { Text = "...", FontSize = 10 },
                    MinWidth = 24, MinHeight = 20,
                    Padding = new Thickness(2)
                };
                chooseBtn.Click += async (_, __) =>
                {
                    var dlg = new Avalonia.Controls.OpenFileDialog
                    {
                        Title = $"Choose texture for Layer {idx}",
                        Filters = new List<Avalonia.Controls.FileDialogFilter>
                        {
                            new Avalonia.Controls.FileDialogFilter { Name = "Images", Extensions = { "png", "jpg", "jpeg", "bmp", "tga" } }
                        }
                    };
                    var win = TopLevel.GetTopLevel(this) as Window;
                    var result = await dlg.ShowAsync(win);
                    if (result != null && result.Length > 0)
                    {
                        string abs = result[0];
                        var proj = ProjectService.Current;
                        if (proj != null)
                        {
                            try { abs = Path.GetRelativePath(proj.RootPath, abs).Replace('\\', '/'); }
                            catch { }
                        }
                        layer.TexturePath = abs;
                        texLabel.Text = Path.GetFileName(abs);
                        ToolTip.SetTip(texLabel, abs);
                        // Invalidate layer texture cache
                        t.MarkSplatmapDirty();
                        t.Save(); // keep .terrain.json in sync
                        Game_Engine.Core.SceneService.NotifyChanged();
                    }
                };
                texStack.Children.Add(texLabel);
                texStack.Children.Add(chooseBtn);
                Avalonia.Controls.Grid.SetColumn(texStack, 1);
                grid.Children.Add(texStack);

                // Tiling slider
                var tilingSlider = new Slider
                {
                    Minimum = 0.1, Maximum = 100, Value = layer.Tiling,
                    MinWidth = 60
                };
                ToolTip.SetTip(tilingSlider, "UV Tiling");
                tilingSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property == RangeBase.ValueProperty)
                    {
                        layer.Tiling = (float)tilingSlider.Value;
                        Game_Engine.Core.SceneService.NotifyChanged();
                    }
                };
                Avalonia.Controls.Grid.SetColumn(tilingSlider, 2);
                grid.Children.Add(tilingSlider);

                // Remove button
                var removeBtn = new Button
                {
                    Content = new TextBlock { Text = "✕", FontSize = 11 },
                    MinWidth = 24, MinHeight = 24,
                    Padding = new Thickness(2)
                };
                ToolTip.SetTip(removeBtn, "Remove Layer");
                removeBtn.Click += (_, __) =>
                {
                    if (t.Layers.Count > idx) t.Layers.RemoveAt(idx);
                    if (state.ActivePaintLayer >= t.Layers.Count)
                        state.ActivePaintLayer = Math.Max(0, t.Layers.Count - 1);
                    t.MarkSplatmapDirty();
                    t.Save(); // keep .terrain.json in sync
                    RebuildLayerList();
                    Game_Engine.Core.SceneService.NotifyChanged();
                };
                Avalonia.Controls.Grid.SetColumn(removeBtn, 3);
                grid.Children.Add(removeBtn);

                row.Child = grid;
                layersPanel.Children.Add(row);
            }

            // Add Layer button
            if (t.Layers.Count < 8)
            {
                var addBtn = new Button
                {
                    Content = new TextBlock { Text = "+ Add Layer", FontSize = 11 },
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0),
                    Padding = new Thickness(8, 4)
                };
                addBtn.Click += (_, __) =>
                {
                    t.Layers.Add(new TerrainLayer());
                    t.EnsureSplatmaps();
                    t.MarkSplatmapDirty();
                    t.Save(); // keep .terrain.json in sync
                    RebuildLayerList();
                    Game_Engine.Core.SceneService.NotifyChanged();
                };
                layersPanel.Children.Add(addBtn);
            }
        }

        RebuildLayerList();

        var shell = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        shell.Children.Add(SectionTitle("Terrain Layers"));
        shell.Children.Add(ToolbarShell(layersPanel));
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

        // ---- System.Numerics.Vector3 (color-like RGB or raw XYZ) ----------
        if (t == typeof(SNVector3))
            return SNVector3ColorEditor(target, p);

        // ---- string -----------------------------------------------------------
        if (t == typeof(string))
        {
            static string NormalizeInspectorPath(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return raw;
                try
                {
                    var proj = ProjectService.Current;
                    if (proj == null) return raw.Replace('\\', '/');

                    var abs = Path.GetFullPath(raw);
                    var root = Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return Path.GetRelativePath(root, abs).Replace('\\', '/');

                    return raw.Replace('\\', '/');
                }
                catch
                {
                    return raw.Replace('\\', '/');
                }
            }

            var tb = new TextBox { Width = 240 };
            tb.Bind(TextBox.TextProperty, new Binding(p.Name) { Source = target, Mode = BindingMode.TwoWay });
            tb.GotFocus += (_, __) => BeginPropertyEdit(target, p);

            // Normalize absolute paths to project-relative for all *Path fields.
            bool isPathField = p.Name.EndsWith("Path", StringComparison.OrdinalIgnoreCase);
            if (isPathField)
            {
                try
                {
                    var cur = p.GetValue(target) as string;
                    var norm = NormalizeInspectorPath(cur ?? "");
                    if (!string.Equals(cur, norm, StringComparison.Ordinal))
                    {
                        p.SetValue(target, norm);
                        tb.Text = norm;
                    }
                }
                catch { }
            }

            tb.LostFocus += (_, __) =>
            {
                if (isPathField)
                {
                    try
                    {
                        var cur = p.GetValue(target) as string;
                        var norm = NormalizeInspectorPath(cur ?? "");
                        if (!string.Equals(cur, norm, StringComparison.Ordinal))
                        {
                            p.SetValue(target, norm);
                            tb.Text = norm;
                            SceneService.NotifyChanged();
                        }
                    }
                    catch { }
                }
                CommitPropertyEdit(target, p);
            };
            return tb;
        }
        
        // ---- textures ----------------------------------------------------------
        if (typeof(Texture2D).IsAssignableFrom(t))
            return Texture2DEditor(target, p);

        if (t == typeof(Material))
            return MaterialEditor(target, p);

        // ---- GameObject reference ----------------------------------------------
        if (t == typeof(GameObject))
            return GameObjectRefEditor(target, p);

        // ---- List<T> -----------------------------------------------------------
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            return ListPropertyEditor(target, p);

        // ---- fallback: read-only type name -----------------------------------
        return new TextBlock { Text = t.Name, Opacity = 0.6 };
    }

    /// <summary>
    /// Builds a collapsible inspector panel for List&lt;T&gt; properties.
    /// Shows element count, per-element editors, and Add / Remove / Move buttons.
    /// </summary>
    Control ListPropertyEditor(object target, PropertyInfo prop)
    {
        var list = prop.GetValue(target) as IList;
        var elementType = prop.PropertyType.GetGenericArguments()[0];

        // If the list is null, create an empty one and assign it
        if (list == null)
        {
            list = (IList)Activator.CreateInstance(prop.PropertyType)!;
            prop.SetValue(target, list);
        }

        var container = new StackPanel { Spacing = 4 };

        // ── Header row: element count + Add button ──
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var countLabel = new TextBlock
        {
            Text = $"{list.Count} items",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            FontSize = 12,
            MinWidth = 60
        };
        headerRow.Children.Add(countLabel);

        var btnAdd = new Button
        {
            Content = "+",
            Padding = new Thickness(8, 2),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerRow.Children.Add(btnAdd);

        var btnClearAll = new Button
        {
            Content = "Clear",
            Padding = new Thickness(6, 2),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        };
        headerRow.Children.Add(btnClearAll);

        container.Children.Add(headerRow);

        // ── Elements panel ──
        var elementsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
        container.Children.Add(elementsPanel);

        // Rebuild the elements UI
        void RebuildElements()
        {
            elementsPanel.Children.Clear();
            countLabel.Text = $"{list.Count} items";

            for (int i = 0; i < list.Count; i++)
            {
                int idx = i; // capture for closures
                var elemRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

                // Index label
                var idxLabel = new TextBlock
                {
                    Text = $"[{idx}]",
                    Width = 32,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.6,
                    FontSize = 11
                };
                elemRow.Children.Add(idxLabel);

                // Per-element editor
                elemRow.Children.Add(ListElementEditor(target, prop, list, elementType, idx, RebuildElements));

                // Move up button
                if (idx > 0)
                {
                    var btnUp = new Button
                    {
                        Content = "\u25B2",
                        Padding = new Thickness(4, 1),
                        FontSize = 9,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    btnUp.Click += (_, __) =>
                    {
                        var item = list[idx]!;
                        list.RemoveAt(idx);
                        list.Insert(idx - 1, item);
                        SceneService.NotifyChanged();
                        RebuildElements();
                    };
                    elemRow.Children.Add(btnUp);
                }

                // Move down button
                if (idx < list.Count - 1)
                {
                    var btnDown = new Button
                    {
                        Content = "\u25BC",
                        Padding = new Thickness(4, 1),
                        FontSize = 9,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    btnDown.Click += (_, __) =>
                    {
                        var item = list[idx]!;
                        list.RemoveAt(idx);
                        list.Insert(idx + 1, item);
                        SceneService.NotifyChanged();
                        RebuildElements();
                    };
                    elemRow.Children.Add(btnDown);
                }

                // Remove button
                var btnRemove = new Button
                {
                    Content = "\u2212",
                    Padding = new Thickness(6, 1),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.IndianRed
                };
                btnRemove.Click += (_, __) =>
                {
                    list.RemoveAt(idx);
                    SceneService.NotifyChanged();
                    RebuildElements();
                };
                elemRow.Children.Add(btnRemove);

                elementsPanel.Children.Add(elemRow);
            }
        }

        RebuildElements();

        // ── Add button handler ──
        btnAdd.Click += (_, __) =>
        {
            object newItem = CreateDefaultForType(elementType);
            list.Add(newItem);
            SceneService.NotifyChanged();
            RebuildElements();
        };

        // ── Clear All handler ──
        btnClearAll.Click += (_, __) =>
        {
            list.Clear();
            SceneService.NotifyChanged();
            RebuildElements();
        };

        return container;
    }

    /// <summary>
    /// Creates an inline editor control for a single element inside a List.
    /// </summary>
    Control ListElementEditor(object target, PropertyInfo listProp, IList list, Type elementType, int index, Action rebuild)
    {
        // ── bool ──
        if (elementType == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)(list[index] ?? false) };
            cb.IsCheckedChanged += (_, __) =>
            {
                list[index] = cb.IsChecked ?? false;
                SceneService.NotifyChanged();
            };
            return cb;
        }

        // ── enum ──
        if (elementType.IsEnum)
        {
            var cb = new ComboBox { ItemsSource = Enum.GetValues(elementType), SelectedItem = list[index] };
            cb.SelectionChanged += (_, __) =>
            {
                list[index] = cb.SelectedItem;
                SceneService.NotifyChanged();
            };
            return cb;
        }

        // ── numbers ──
        if (elementType == typeof(int) || elementType == typeof(float) || elementType == typeof(double) ||
            elementType == typeof(decimal) || elementType == typeof(long) || elementType == typeof(short))
        {
            var tb = new TextBox { Width = 100, Text = list[index]?.ToString() ?? "0" };
            tb.LostFocus += (_, __) =>
            {
                var s = tb.Text?.Trim() ?? "";
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    if (elementType == typeof(int)) list[index] = (int)d;
                    else if (elementType == typeof(float)) list[index] = (float)d;
                    else if (elementType == typeof(double)) list[index] = d;
                    else if (elementType == typeof(long)) list[index] = (long)d;
                    else if (elementType == typeof(short)) list[index] = (short)d;
                    else if (elementType == typeof(decimal)) list[index] = (decimal)d;
                    SceneService.NotifyChanged();
                }
            };
            return tb;
        }

        // ── Vector3 ──
        if (elementType == typeof(CoreVector3))
        {
            var v = (CoreVector3)(list[index] ?? new CoreVector3());
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };

            var tbX = new TextBox { Width = 60, Text = v.X.ToString(CultureInfo.InvariantCulture), Watermark = "X" };
            var tbY = new TextBox { Width = 60, Text = v.Y.ToString(CultureInfo.InvariantCulture), Watermark = "Y" };
            var tbZ = new TextBox { Width = 60, Text = v.Z.ToString(CultureInfo.InvariantCulture), Watermark = "Z" };

            void CommitVec3()
            {
                if (double.TryParse(tbX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(tbY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    double.TryParse(tbZ.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    list[index] = new CoreVector3(x, y, z);
                    SceneService.NotifyChanged();
                }
            }

            tbX.LostFocus += (_, __) => CommitVec3();
            tbY.LostFocus += (_, __) => CommitVec3();
            tbZ.LostFocus += (_, __) => CommitVec3();

            panel.Children.Add(tbX);
            panel.Children.Add(tbY);
            panel.Children.Add(tbZ);
            return panel;
        }

        // ── string ──
        if (elementType == typeof(string))
        {
            var tb = new TextBox { Width = 200, Text = (list[index] as string) ?? "" };
            tb.LostFocus += (_, __) =>
            {
                list[index] = tb.Text ?? "";
                SceneService.NotifyChanged();
            };
            return tb;
        }

        // ── Color ──
        if (elementType == typeof(Color))
        {
            var c = (Color)(list[index] ?? new Color());
            var tb = new TextBox { Width = 140, Text = c.ToString(), Watermark = "#RRGGBB" };
            tb.LostFocus += (_, __) =>
            {
                try
                {
                    list[index] = Color.Parse(tb.Text ?? "");
                    SceneService.NotifyChanged();
                }
                catch { }
            };
            return tb;
        }

        // ── Complex objects with [Persist] properties: inline sub-inspector ──
        if (elementType.IsClass && elementType != typeof(string))
        {
            var item = list[index];
            if (item == null)
            {
                return new TextBlock { Text = "(null)", Opacity = 0.5, FontSize = 11 };
            }

            var subProps = elementType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToArray();

            if (subProps.Length == 0)
                return new TextBlock { Text = item.ToString() ?? elementType.Name, Opacity = 0.6, FontSize = 11 };

            var subPanel = new StackPanel { Spacing = 3 };
            var headerBtn = new Button
            {
                Content = $"{elementType.Name}",
                Padding = new Thickness(4, 1),
                FontSize = 11,
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };

            var fieldsPanel = new StackPanel { Spacing = 3, Margin = new Thickness(12, 2, 0, 2), IsVisible = false };

            headerBtn.Click += (_, __) =>
            {
                fieldsPanel.IsVisible = !fieldsPanel.IsVisible;
                headerBtn.Content = (fieldsPanel.IsVisible ? "\u25BC " : "\u25B6 ") + elementType.Name;
            };
            headerBtn.Content = "\u25B6 " + elementType.Name;

            foreach (var sp in subProps)
            {
                var fieldRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                fieldRow.Children.Add(new TextBlock
                {
                    Text = sp.Name,
                    Width = 90,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    Opacity = 0.8
                });
                fieldRow.Children.Add(SubPropertyEditor(item, sp, () => SceneService.NotifyChanged()));
                fieldsPanel.Children.Add(fieldRow);
            }

            subPanel.Children.Add(headerBtn);
            subPanel.Children.Add(fieldsPanel);
            return subPanel;
        }

        // ── Fallback ──
        return new TextBlock { Text = list[index]?.ToString() ?? "(null)", Opacity = 0.6, FontSize = 11 };
    }

    /// <summary>
    /// Creates an inline editor for a sub-property on a complex list element.
    /// </summary>
    Control SubPropertyEditor(object item, PropertyInfo sp, Action onChanged)
    {
        var val = sp.GetValue(item);
        var t = sp.PropertyType;

        if (t == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)(val ?? false) };
            cb.IsCheckedChanged += (_, __) =>
            {
                sp.SetValue(item, cb.IsChecked ?? false);
                onChanged();
            };
            return cb;
        }

        if (t.IsEnum)
        {
            var cb = new ComboBox { ItemsSource = Enum.GetValues(t), SelectedItem = val };
            cb.SelectionChanged += (_, __) =>
            {
                sp.SetValue(item, cb.SelectedItem);
                onChanged();
            };
            return cb;
        }

        if (t == typeof(int) || t == typeof(float) || t == typeof(double) ||
            t == typeof(decimal) || t == typeof(long) || t == typeof(short))
        {
            var tb = new TextBox { Width = 80, Text = val?.ToString() ?? "0", FontSize = 11 };
            tb.LostFocus += (_, __) =>
            {
                var s = tb.Text?.Trim() ?? "";
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    if (t == typeof(int)) sp.SetValue(item, (int)d);
                    else if (t == typeof(float)) sp.SetValue(item, (float)d);
                    else if (t == typeof(double)) sp.SetValue(item, d);
                    else if (t == typeof(long)) sp.SetValue(item, (long)d);
                    else if (t == typeof(short)) sp.SetValue(item, (short)d);
                    else if (t == typeof(decimal)) sp.SetValue(item, (decimal)d);
                    onChanged();
                }
            };
            return tb;
        }

        if (t == typeof(CoreVector3))
        {
            var v = (CoreVector3)(val ?? new CoreVector3());
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var tbX = new TextBox { Width = 50, Text = v.X.ToString(CultureInfo.InvariantCulture), FontSize = 11 };
            var tbY = new TextBox { Width = 50, Text = v.Y.ToString(CultureInfo.InvariantCulture), FontSize = 11 };
            var tbZ = new TextBox { Width = 50, Text = v.Z.ToString(CultureInfo.InvariantCulture), FontSize = 11 };

            void Commit()
            {
                if (double.TryParse(tbX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(tbY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    double.TryParse(tbZ.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    sp.SetValue(item, new CoreVector3(x, y, z));
                    onChanged();
                }
            }

            tbX.LostFocus += (_, __) => Commit();
            tbY.LostFocus += (_, __) => Commit();
            tbZ.LostFocus += (_, __) => Commit();

            panel.Children.Add(tbX);
            panel.Children.Add(tbY);
            panel.Children.Add(tbZ);
            return panel;
        }

        if (t == typeof(SNVector3))
        {
            var v = (SNVector3)(val ?? new SNVector3());
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

            var swatchSub = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(2),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                    (byte)Math.Clamp(v.X * 255f, 0, 255),
                    (byte)Math.Clamp(v.Y * 255f, 0, 255),
                    (byte)Math.Clamp(v.Z * 255f, 0, 255)))
            };
            var tbR = new TextBox { Width = 46, Text = v.X.ToString("F3", CultureInfo.InvariantCulture), FontSize = 11, Watermark = "R" };
            var tbG = new TextBox { Width = 46, Text = v.Y.ToString("F3", CultureInfo.InvariantCulture), FontSize = 11, Watermark = "G" };
            var tbB = new TextBox { Width = 46, Text = v.Z.ToString("F3", CultureInfo.InvariantCulture), FontSize = 11, Watermark = "B" };

            void CommitSNV()
            {
                if (float.TryParse(tbR.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) &&
                    float.TryParse(tbG.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) &&
                    float.TryParse(tbB.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                {
                    sp.SetValue(item, new SNVector3(r, g, b));
                    swatchSub.Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                        (byte)Math.Clamp(r * 255f, 0, 255),
                        (byte)Math.Clamp(g * 255f, 0, 255),
                        (byte)Math.Clamp(b * 255f, 0, 255)));
                    onChanged();
                }
            }

            tbR.LostFocus += (_, __) => CommitSNV();
            tbG.LostFocus += (_, __) => CommitSNV();
            tbB.LostFocus += (_, __) => CommitSNV();

            panel.Children.Add(swatchSub);
            panel.Children.Add(tbR);
            panel.Children.Add(tbG);
            panel.Children.Add(tbB);
            return panel;
        }

        if (t == typeof(string))
        {
            var tb = new TextBox { Width = 160, Text = (val as string) ?? "", FontSize = 11 };
            tb.LostFocus += (_, __) =>
            {
                sp.SetValue(item, tb.Text ?? "");
                onChanged();
            };
            return tb;
        }

        // Fallback: read-only
        return new TextBlock { Text = val?.ToString() ?? "(null)", Opacity = 0.5, FontSize = 11 };
    }

    /// <summary>
    /// Creates a sensible default value for the given type, used when adding new list elements.
    /// </summary>
    static object CreateDefaultForType(Type t)
    {
        if (t == typeof(string)) return "";
        if (t == typeof(int)) return 0;
        if (t == typeof(float)) return 0f;
        if (t == typeof(double)) return 0.0;
        if (t == typeof(long)) return 0L;
        if (t == typeof(short)) return (short)0;
        if (t == typeof(decimal)) return 0m;
        if (t == typeof(bool)) return false;
        if (t == typeof(CoreVector3)) return new CoreVector3(0, 0, 0);
        if (t == typeof(SNVector3)) return new SNVector3(0, 0, 0);
        if (t == typeof(Color)) return new Color();
        if (t.IsEnum)
        {
            var vals = Enum.GetValues(t);
            return vals.Length > 0 ? vals.GetValue(0)! : Activator.CreateInstance(t)!;
        }
        if (t.IsValueType) return Activator.CreateInstance(t)!;
        try { return Activator.CreateInstance(t)!; }
        catch { return null!; }
    }

    /// <summary>
    /// Builds a GameObject reference editor: dropdown of all scene objects (+ prefabs), drag-and-drop zone, and clear button.
    /// </summary>
    Control GameObjectRefEditor(object target, PropertyInfo prop)
    {
        var container = new StackPanel { Spacing = 4 };

        // ── Row 1: Dropdown + Clear ──
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        // Gather all GameObjects in the scene (flat list with path labels)
        var goList = new List<GameObjectRefItem> { new() { Label = "(None)", GO = null } };
        foreach (var root in SceneService.Root)
            CollectGameObjectsRecursive(root, "", goList);

        var combo = new ComboBox
        {
            Width = 220,
            ItemsSource = goList,
            DisplayMemberBinding = new Binding(nameof(GameObjectRefItem.Label))
        };

        // Pre-select current value
        var current = prop.GetValue(target) as GameObject;
        if (current == null)
            combo.SelectedIndex = 0;
        else
        {
            var idx = goList.FindIndex(i => ReferenceEquals(i.GO, current));
            combo.SelectedIndex = idx >= 0 ? idx : 0;
        }

        combo.DropDownOpened += (_, __) =>
        {
            // Refresh the list each time the dropdown opens to pick up new objects
            var fresh = new List<GameObjectRefItem> { new() { Label = "(None)", GO = null } };
            foreach (var root in SceneService.Root)
                CollectGameObjectsRecursive(root, "", fresh);
            combo.ItemsSource = fresh;

            // Re-select current
            var cur = prop.GetValue(target) as GameObject;
            if (cur == null) combo.SelectedIndex = 0;
            else
            {
                var i = fresh.FindIndex(x => ReferenceEquals(x.GO, cur));
                combo.SelectedIndex = i >= 0 ? i : 0;
            }

            BeginPropertyEdit(target, prop);
        };

        combo.SelectionChanged += (_, __) =>
        {
            var sel = combo.SelectedItem as GameObjectRefItem;
            prop.SetValue(target, sel?.GO);
            SceneService.NotifyChanged();
            CommitPropertyEdit(target, prop);
        };

        var btnClear = new Button { Content = "Clear", Padding = new Thickness(6, 2) };
        btnClear.Click += (_, __) =>
        {
            prop.SetValue(target, null);
            combo.SelectedIndex = 0;
            SceneService.NotifyChanged();
        };

        row.Children.Add(combo);
        row.Children.Add(btnClear);
        container.Children.Add(row);

        // ── Row 2: Current reference display ──
        var refLabel = new TextBlock
        {
            Text = current != null ? $"→ {current.Name}" : "(no reference)",
            Opacity = 0.6,
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 0)
        };
        container.Children.Add(refLabel);

        // Update the label when selection changes
        combo.SelectionChanged += (_, __) =>
        {
            var sel = combo.SelectedItem as GameObjectRefItem;
            refLabel.Text = sel?.GO != null ? $"→ {sel.GO.Name}" : "(no reference)";
        };

        // ── Row 3: Drag-and-drop zone ──
        var dropText = new TextBlock
        {
            Text = "Drop GameObject or .prefab here",
            Opacity = 0.5,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var dropZone = new Border
        {
            Padding = new Thickness(8, 4),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            MinWidth = 240,
            MinHeight = 28,
            Child = dropText
        };
        DragDrop.SetAllowDrop(dropZone, true);

        dropZone.AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            // Accept GameObjects from hierarchy
            if (e.Data.Contains("application/x-gameobject"))
            {
                e.DragEffects = DragDropEffects.Link;
                e.Handled = true;
                return;
            }
            // Accept .prefab files from project panel or OS
            if (e.Data.Contains(DataFormats.FileNames))
            {
                var files = e.Data.GetFileNames()?.ToList();
                if (files != null && files.Any(f => f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                {
                    e.DragEffects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }
            if (e.Data.Contains("project-node-path"))
            {
                var path = e.Data.Get("project-node-path") as string;
                if (path != null && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    e.DragEffects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        dropZone.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            // Handle GameObject drop from hierarchy
            if (e.Data.Contains("application/x-gameobject"))
            {
                var go = e.Data.Get("application/x-gameobject") as GameObject;
                if (go != null)
                {
                    prop.SetValue(target, go);
                    refLabel.Text = $"→ {go.Name}";
                    // Update combo selection
                    var items = combo.ItemsSource as List<GameObjectRefItem>;
                    if (items != null)
                    {
                        var idx = items.FindIndex(x => ReferenceEquals(x.GO, go));
                        if (idx >= 0) combo.SelectedIndex = idx;
                    }
                    SceneService.NotifyChanged();
                    e.Handled = true;
                    return;
                }
            }

            // Handle .prefab file drop — instantiate and assign
            string prefabPath = null;
            if (e.Data.Contains(DataFormats.FileNames))
            {
                var files = e.Data.GetFileNames()?.ToList();
                prefabPath = files?.FirstOrDefault(f => f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            }
            if (prefabPath == null && e.Data.Contains("project-node-path"))
            {
                var p2 = e.Data.Get("project-node-path") as string;
                if (p2 != null && p2.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    prefabPath = p2;
            }

            if (prefabPath != null)
            {
                // Make project-relative
                string relPath = prefabPath;
                var proj = ProjectService.Current;
                if (proj != null)
                {
                    var abs = Path.GetFullPath(prefabPath);
                    var root = Path.GetFullPath(proj.RootPath);
                    if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        relPath = Path.GetRelativePath(root, abs);
                }

                var prefab = Prefab.Load(relPath);
                if (prefab != null)
                {
                    var instance = prefab.Instantiate();
                    if (instance != null)
                    {
                        prop.SetValue(target, instance);
                        refLabel.Text = $"→ {instance.Name} (prefab)";
                        SceneService.NotifyChanged();
                        Log.Info($"Instantiated prefab '{prefab.Name}' and assigned to {prop.Name}");
                    }
                }
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        container.Children.Add(dropZone);
        return container;
    }

    /// <summary>Helper item for the GameObject reference dropdown.</summary>
    class GameObjectRefItem
    {
        public string Label { get; set; } = "";
        public GameObject? GO { get; set; }
    }

    /// <summary>Recursively collect all GameObjects in the scene with indented path labels.</summary>
    static void CollectGameObjectsRecursive(GameObject go, string prefix, List<GameObjectRefItem> list)
    {
        var label = string.IsNullOrEmpty(prefix) ? go.Name : $"{prefix}/{go.Name}";
        list.Add(new GameObjectRefItem { Label = label, GO = go });
        foreach (var child in go.Children)
            CollectGameObjectsRecursive(child, label, list);
    }

    // ═══════════════════════ Behavior Tree Runner Inspector UI ═══════════════════════

    // Node type color coding
    static IBrush BTNodeTypeBrush(BTNode node) => node switch
    {
        SelectorNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(180, 130, 40)),
        SequenceNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(50, 140, 70)),
        ParallelNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(80, 120, 180)),
        InverterNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(160, 60, 60)),
        RepeaterNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(140, 80, 160)),
        SucceederNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(100, 160, 100)),
        ActionNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(60, 130, 170)),
        ConditionNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(170, 130, 60)),
        WaitNode => new SolidColorBrush(Avalonia.Media.Color.FromRgb(120, 120, 140)),
        _ => Brushes.Gray
    };

    static string BTNodeTypeLabel(BTNode node) => node switch
    {
        SelectorNode => "Selector",
        SequenceNode => "Sequence",
        ParallelNode => "Parallel",
        InverterNode => "Inverter",
        RepeaterNode => "Repeater",
        SucceederNode => "Succeeder",
        ActionNode => "Action",
        ConditionNode => "Condition",
        WaitNode => "Wait",
        _ => node.GetType().Name.Replace("Node", "")
    };

    Control BehaviorTreeRunnerInspectorUI(BehaviorTreeRunner runner)
    {
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(SectionTitle("Behavior Tree"));

        if (runner.Tree == null)
            runner.Tree = new BehaviorTree();

        var tree = runner.Tree;

        // ── Tree name ──
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameRow.Children.Add(new TextBlock { Text = "Name", Width = 80, VerticalAlignment = VerticalAlignment.Center });
        var nameBox = new TextBox { Width = 200, Text = tree.Name };
        nameBox.LostFocus += (_, __) => { tree.Name = nameBox.Text ?? "Untitled"; SceneService.NotifyChanged(); };
        nameRow.Children.Add(nameBox);
        root.Children.Add(nameRow);

        // ── Status display ──
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        statusRow.Children.Add(new TextBlock { Text = "Status", Width = 80, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var statusLabel = new TextBlock
        {
            Text = runner.LastStatus.ToString(),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = runner.LastStatus switch
            {
                BTStatus.Success => Brushes.LimeGreen,
                BTStatus.Failure => Brushes.IndianRed,
                _ => Brushes.Orange
            }
        };
        statusRow.Children.Add(statusLabel);
        root.Children.Add(statusRow);

        root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 2) });

        // ── Tree structure panel ──
        var treePanel = new StackPanel { Spacing = 2 };
        root.Children.Add(treePanel);

        void RebuildTree()
        {
            treePanel.Children.Clear();
            if (tree.Root == null)
            {
                treePanel.Children.Add(new TextBlock
                {
                    Text = "No root node. Set root below.",
                    Opacity = 0.5, FontSize = 11, Margin = new Thickness(4)
                });
            }
            else
            {
                treePanel.Children.Add(BTNodeEditorUI(tree.Root, null, -1, tree, RebuildTree, 0));
            }
        }

        RebuildTree();

        // ── Set Root button (if no root) ──
        if (tree.Root == null)
        {
            var setRootPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            setRootPanel.Children.Add(new TextBlock { Text = "Set Root Node:", FontSize = 11, FontWeight = FontWeight.SemiBold });
            var rootBtnRow = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var nodeType in BTNodeTypes())
            {
                var btn = new Button
                {
                    Content = nodeType.label,
                    Padding = new Thickness(8, 3),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 4, 4)
                };
                var factory = nodeType.factory;
                btn.Click += (_, __) =>
                {
                    tree.Root = factory();
                    SceneService.NotifyChanged();
                    RebuildTree();
                };
                rootBtnRow.Children.Add(btn);
            }
            setRootPanel.Children.Add(rootBtnRow);
            root.Children.Add(setRootPanel);
        }

        // ── Blackboard ──
        root.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 2) });
        root.Children.Add(SectionTitle("Blackboard"));
        root.Children.Add(BlackboardEditorUI(runner.Blackboard));

        return root;
    }

    static (string label, Func<BTNode> factory)[] BTNodeTypes() => new (string, Func<BTNode>)[]
    {
        ("Selector", () => new SelectorNode { Name = "Selector" }),
        ("Sequence", () => new SequenceNode { Name = "Sequence" }),
        ("Parallel", () => new ParallelNode { Name = "Parallel" }),
        ("Inverter", () => new InverterNode { Name = "Inverter" }),
        ("Repeater", () => new RepeaterNode { Name = "Repeater" }),
        ("Succeeder", () => new SucceederNode { Name = "Succeeder" }),
        ("Action", () => new ActionNode { Name = "Action" }),
        ("Condition", () => new ConditionNode { Name = "Condition" }),
        ("Wait", () => new WaitNode { Name = "Wait" }),
    };

    Control BTNodeEditorUI(BTNode node, object? parent, int childIndex, BehaviorTree tree, Action rebuild, int depth)
    {
        var container = new StackPanel { Spacing = 1 };
        var indent = new Thickness(depth * 16, 0, 0, 0);

        // ── Node header border ──
        var nodeBorder = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = BTNodeTypeBrush(node),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4),
            Margin = new Thickness(indent.Left, 2, 0, 2)
        };

        var nodePanel = new StackPanel { Spacing = 3 };

        // ── Header row: type badge + name + delete ──
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var typeBadge = new Border
        {
            Background = BTNodeTypeBrush(node),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            Child = new TextBlock
            {
                Text = BTNodeTypeLabel(node),
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White
            }
        };
        headerRow.Children.Add(typeBadge);

        // Editable name
        var nameBox = new TextBox { Text = node.Name, Width = 120, FontSize = 11 };
        nameBox.LostFocus += (_, __) =>
        {
            node.Name = nameBox.Text ?? "";
            SceneService.NotifyChanged();
        };
        headerRow.Children.Add(nameBox);

        // Delete button
        var deleteBtn = new Button
        {
            Content = "\u2715",
            Padding = new Thickness(4, 1),
            FontSize = 10,
            Foreground = Brushes.IndianRed
        };
        deleteBtn.Click += (_, __) =>
        {
            if (parent == null)
            {
                tree.Root = null;
            }
            else if (parent is SelectorNode sel)
            {
                if (childIndex >= 0 && childIndex < sel.Children.Count)
                    sel.Children.RemoveAt(childIndex);
            }
            else if (parent is SequenceNode seq)
            {
                if (childIndex >= 0 && childIndex < seq.Children.Count)
                    seq.Children.RemoveAt(childIndex);
            }
            else if (parent is ParallelNode par)
            {
                if (childIndex >= 0 && childIndex < par.Children.Count)
                    par.Children.RemoveAt(childIndex);
            }
            else if (parent is InverterNode inv) inv.Child = null;
            else if (parent is RepeaterNode rep) rep.Child = null;
            else if (parent is SucceederNode suc) suc.Child = null;
            SceneService.NotifyChanged();
            rebuild();
        };
        headerRow.Children.Add(deleteBtn);
        nodePanel.Children.Add(headerRow);

        // ── Type-specific properties ──
        var propsPanel = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2, 0, 0) };

        if (node is WaitNode waitNode)
        {
            var durRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            durRow.Children.Add(new TextBlock { Text = "Duration", Width = 60, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            var durBox = new TextBox { Width = 60, Text = waitNode.Duration.ToString(CultureInfo.InvariantCulture), FontSize = 10 };
            durBox.LostFocus += (_, __) =>
            {
                if (float.TryParse(durBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    waitNode.Duration = v;
                    SceneService.NotifyChanged();
                }
            };
            durRow.Children.Add(durBox);
            propsPanel.Children.Add(durRow);
        }

        if (node is ParallelNode parallelNode)
        {
            var reqRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            reqRow.Children.Add(new TextBlock { Text = "Required", Width = 60, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            var reqBox = new TextBox { Width = 40, Text = parallelNode.RequiredSuccesses.ToString(), FontSize = 10 };
            reqBox.LostFocus += (_, __) =>
            {
                if (int.TryParse(reqBox.Text, out var v))
                {
                    parallelNode.RequiredSuccesses = v;
                    SceneService.NotifyChanged();
                }
            };
            reqRow.Children.Add(reqBox);
            propsPanel.Children.Add(reqRow);
        }

        if (node is RepeaterNode repeaterNode)
        {
            var cntRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            cntRow.Children.Add(new TextBlock { Text = "Count", Width = 60, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            var cntBox = new TextBox { Width = 40, Text = repeaterNode.Count.ToString(), FontSize = 10 };
            cntBox.LostFocus += (_, __) =>
            {
                if (int.TryParse(cntBox.Text, out var v))
                {
                    repeaterNode.Count = v;
                    SceneService.NotifyChanged();
                }
            };
            cntRow.Children.Add(cntBox);
            cntRow.Children.Add(new TextBlock { Text = "(-1 = forever)", FontSize = 9, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            propsPanel.Children.Add(cntRow);
        }

        if (node is ActionNode actionNode)
        {
            propsPanel.Children.Add(new TextBlock
            {
                Text = actionNode.Action != null ? "Delegate bound" : "No action (set in code)",
                FontSize = 10, Opacity = 0.6
            });
        }

        if (node is ConditionNode condNode)
        {
            propsPanel.Children.Add(new TextBlock
            {
                Text = condNode.Condition != null ? "Condition bound" : "No condition (set in code)",
                FontSize = 10, Opacity = 0.6
            });
        }

        if (propsPanel.Children.Count > 0)
            nodePanel.Children.Add(propsPanel);

        // ── Children (for composite nodes) ──
        if (node is SelectorNode selectorNode)
        {
            nodePanel.Children.Add(BTChildrenEditor(selectorNode.Children, node, tree, rebuild, depth));
        }
        else if (node is SequenceNode sequenceNode)
        {
            nodePanel.Children.Add(BTChildrenEditor(sequenceNode.Children, node, tree, rebuild, depth));
        }
        else if (node is ParallelNode parallelNodeChildren)
        {
            nodePanel.Children.Add(BTChildrenEditor(parallelNodeChildren.Children, node, tree, rebuild, depth));
        }
        // ── Single child (for decorator nodes) ──
        else if (node is InverterNode inverterNode)
        {
            nodePanel.Children.Add(BTSingleChildEditor(inverterNode.Child, node, "Child",
                child => { inverterNode.Child = child; SceneService.NotifyChanged(); rebuild(); },
                tree, rebuild, depth));
        }
        else if (node is RepeaterNode repeaterNodeChild)
        {
            nodePanel.Children.Add(BTSingleChildEditor(repeaterNodeChild.Child, node, "Child",
                child => { repeaterNodeChild.Child = child; SceneService.NotifyChanged(); rebuild(); },
                tree, rebuild, depth));
        }
        else if (node is SucceederNode succeederNode)
        {
            nodePanel.Children.Add(BTSingleChildEditor(succeederNode.Child, node, "Child",
                child => { succeederNode.Child = child; SceneService.NotifyChanged(); rebuild(); },
                tree, rebuild, depth));
        }

        nodeBorder.Child = nodePanel;
        container.Children.Add(nodeBorder);
        return container;
    }

    Control BTChildrenEditor(List<BTNode> children, BTNode parentNode, BehaviorTree tree, Action rebuild, int depth)
    {
        var panel = new StackPanel { Spacing = 1, Margin = new Thickness(4, 2, 0, 0) };

        for (int i = 0; i < children.Count; i++)
        {
            int idx = i;
            var childRow = new StackPanel { Spacing = 0 };

            // Move up/down buttons
            var moveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness((depth + 1) * 16, 0, 0, 0) };
            if (idx > 0)
            {
                var upBtn = new Button { Content = "\u25B2", Padding = new Thickness(3, 0), FontSize = 8 };
                upBtn.Click += (_, __) =>
                {
                    var item = children[idx];
                    children.RemoveAt(idx);
                    children.Insert(idx - 1, item);
                    SceneService.NotifyChanged();
                    rebuild();
                };
                moveRow.Children.Add(upBtn);
            }
            if (idx < children.Count - 1)
            {
                var downBtn = new Button { Content = "\u25BC", Padding = new Thickness(3, 0), FontSize = 8 };
                downBtn.Click += (_, __) =>
                {
                    var item = children[idx];
                    children.RemoveAt(idx);
                    children.Insert(idx + 1, item);
                    SceneService.NotifyChanged();
                    rebuild();
                };
                moveRow.Children.Add(downBtn);
            }
            if (moveRow.Children.Count > 0)
                childRow.Children.Add(moveRow);

            childRow.Children.Add(BTNodeEditorUI(children[i], parentNode, i, tree, rebuild, depth + 1));
            panel.Children.Add(childRow);
        }

        // Add child button
        var addPanel = new StackPanel { Margin = new Thickness((depth + 1) * 16, 4, 0, 2) };
        var addBtn = new Button
        {
            Content = "+ Add Child",
            Padding = new Thickness(8, 3),
            FontSize = 10
        };
        addBtn.Click += (_, __) =>
        {
            BTShowAddNodeMenu(addBtn, newNode =>
            {
                children.Add(newNode);
                SceneService.NotifyChanged();
                rebuild();
            });
        };
        addPanel.Children.Add(addBtn);
        panel.Children.Add(addPanel);

        return panel;
    }

    Control BTSingleChildEditor(BTNode? child, BTNode parentNode, string label, Action<BTNode> setChild, BehaviorTree tree, Action rebuild, int depth)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2, 0, 0) };

        if (child != null)
        {
            panel.Children.Add(BTNodeEditorUI(child, parentNode, 0, tree, rebuild, depth + 1));
        }
        else
        {
            var addPanel = new StackPanel { Margin = new Thickness((depth + 1) * 16, 4, 0, 2) };
            var addBtn = new Button
            {
                Content = $"+ Set {label}",
                Padding = new Thickness(8, 3),
                FontSize = 10
            };
            addBtn.Click += (_, __) =>
            {
                BTShowAddNodeMenu(addBtn, newNode => setChild(newNode));
            };
            addPanel.Children.Add(addBtn);
            panel.Children.Add(addPanel);
        }

        return panel;
    }

    void BTShowAddNodeMenu(Control target, Action<BTNode> onAdd)
    {
        var menu = new ContextMenu();
        foreach (var (label, factory) in BTNodeTypes())
        {
            var item = new MenuItem { Header = label };
            var f = factory;
            item.Click += (_, __) =>
            {
                onAdd(f());
            };
            menu.Items.Add(item);
        }
        menu.Open(target);
    }

    Control BlackboardEditorUI(Blackboard board)
    {
        var panel = new StackPanel { Spacing = 4 };
        var entryList = new StackPanel { Spacing = 3 };
        panel.Children.Add(entryList);

        void RebuildEntries()
        {
            entryList.Children.Clear();
            foreach (var key in board.Keys)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(new TextBlock { Text = key, Width = 100, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

                // Display current value (read-only since types vary)
                var val = board.GetString(key, "");
                float fVal = board.GetFloat(key);
                int iVal = board.GetInt(key);
                bool bVal = board.GetBool(key);

                string display;
                if (!string.IsNullOrEmpty(val) && val != "0" && val != "False")
                    display = val;
                else if (fVal != 0) display = fVal.ToString(CultureInfo.InvariantCulture);
                else if (iVal != 0) display = iVal.ToString();
                else if (bVal) display = "true";
                else display = val;

                var valBox = new TextBox { Width = 120, Text = display, FontSize = 11 };
                valBox.LostFocus += (_, __) =>
                {
                    var t = valBox.Text ?? "";
                    if (bool.TryParse(t, out var b)) board.Set(key, b);
                    else if (int.TryParse(t, out var i)) board.Set(key, i);
                    else if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) board.Set(key, f);
                    else board.Set(key, t);
                    SceneService.NotifyChanged();
                };
                row.Children.Add(valBox);

                var removeBtn = new Button { Content = "\u2212", Padding = new Thickness(4, 1), FontSize = 10, Foreground = Brushes.IndianRed };
                removeBtn.Click += (_, __) =>
                {
                    board.Remove(key);
                    SceneService.NotifyChanged();
                    RebuildEntries();
                };
                row.Children.Add(removeBtn);
                entryList.Children.Add(row);
            }

            if (board.Count == 0)
            {
                entryList.Children.Add(new TextBlock
                {
                    Text = "No entries. Add keys below or they are set at runtime.",
                    Opacity = 0.5, FontSize = 11
                });
            }
        }

        RebuildEntries();

        // Add new entry
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        var newKeyBox = new TextBox { Width = 100, Watermark = "key", FontSize = 11 };
        var newValBox = new TextBox { Width = 100, Watermark = "value", FontSize = 11 };
        var addBtn = new Button { Content = "+ Add", Padding = new Thickness(6, 2), FontSize = 11 };
        addBtn.Click += (_, __) =>
        {
            var k = newKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(k)) return;
            var v = newValBox.Text ?? "";
            if (bool.TryParse(v, out var bv)) board.Set(k, bv);
            else if (int.TryParse(v, out var iv)) board.Set(k, iv);
            else if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv)) board.Set(k, fv);
            else board.Set(k, v);
            newKeyBox.Text = "";
            newValBox.Text = "";
            SceneService.NotifyChanged();
            RebuildEntries();
        };
        addRow.Children.Add(newKeyBox);
        addRow.Children.Add(newValBox);
        addRow.Children.Add(addBtn);
        panel.Children.Add(addRow);

        return panel;
    }

    // ═══════════════════════ Timeline Player Inspector UI ═══════════════════════

    Control TimelinePlayerInspectorUI(TimelinePlayer player)
    {
        var root = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };

        if (player.Timeline == null)
        {
            player.Timeline = new TimelineAsset();
            SceneService.NotifyChanged();
        }
        var timeline = player.Timeline;

        // ── Header: Timeline Name ──
        var nameRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        nameRow.Children.Add(new TextBlock
        {
            Text = "Timeline",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x8C, 0xFF)),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center
        });
        var nameBox = new TextBox
        {
            Text = timeline.Name, FontSize = 11, Height = 24, MinWidth = 120,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48))
        };
        nameBox.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) { timeline.Name = nameBox.Text; SceneService.NotifyChanged(); } };
        nameRow.Children.Add(nameBox);
        root.Children.Add(nameRow);

        // ── Settings row: Duration, Loop ──
        var settingsRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };

        settingsRow.Children.Add(new TextBlock { Text = "Duration:", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0xAA, 0xBB)), VerticalAlignment = VerticalAlignment.Center });
        var durBox = new TextBox
        {
            Text = timeline.Duration.ToString("F1", CultureInfo.InvariantCulture),
            FontSize = 11, Height = 22, Width = 55,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48))
        };
        durBox.LostFocus += (_, _) =>
        {
            if (float.TryParse(durBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
            {
                timeline.Duration = Math.Max(0.1f, d);
                SceneService.NotifyChanged();
            }
        };
        settingsRow.Children.Add(durBox);

        var loopCb = new CheckBox { Content = "Loop", IsChecked = timeline.Loop, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE0, 0xE6)), VerticalAlignment = VerticalAlignment.Center };
        loopCb.IsCheckedChanged += (_, _) => { timeline.Loop = loopCb.IsChecked == true; SceneService.NotifyChanged(); };
        settingsRow.Children.Add(loopCb);

        root.Children.Add(settingsRow);

        // ── Status row ──
        var statusRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        statusRow.Children.Add(new TextBlock { Text = "Time:", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0xAA, 0xBB)), VerticalAlignment = VerticalAlignment.Center });
        statusRow.Children.Add(new TextBlock { Text = player.CurrentTime.ToString("F2", CultureInfo.InvariantCulture), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)), VerticalAlignment = VerticalAlignment.Center });
        statusRow.Children.Add(new TextBlock
        {
            Text = player.IsPlaying ? "Playing" : player.IsFinished ? "Finished" : "Stopped",
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(player.IsPlaying ? Color.FromRgb(0x66, 0xDD, 0x66)
                : player.IsFinished ? Color.FromRgb(0xFF, 0xAA, 0x33) : Color.FromRgb(0x99, 0xAA, 0xBB))
        });
        root.Children.Add(statusRow);

        root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48)), Margin = new Thickness(0, 2) });

        // ── Tracks ──
        root.Children.Add(new TextBlock
        {
            Text = $"Tracks ({timeline.Tracks.Count})",
            FontWeight = FontWeight.SemiBold, FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA))
        });

        var trackList = new StackPanel { Spacing = 4 };
        root.Children.Add(trackList);

        void RebuildTracks()
        {
            trackList.Children.Clear();
            for (int ti = 0; ti < timeline.Tracks.Count; ti++)
            {
                var track = timeline.Tracks[ti];
                int trackIdx = ti;

                var trackPanel = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x28)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 4),
                    Margin = new Thickness(0, 1)
                };

                var trackStack = new StackPanel { Spacing = 3 };

                // Track header: type badge + name + mute + delete
                var trackHeader = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };

                var typeBadge = new Border
                {
                    Background = TimelineTrackTypeBrush(track.Type),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1),
                    Child = new TextBlock
                    {
                        Text = track.Type.ToString(),
                        FontSize = 9, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold
                    }
                };
                trackHeader.Children.Add(typeBadge);

                var trackNameBox = new TextBox
                {
                    Text = track.Name, FontSize = 11, Height = 22, MinWidth = 100,
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48)),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                trackNameBox.LostFocus += (_, _) => { track.Name = trackNameBox.Text ?? "Track"; SceneService.NotifyChanged(); };
                trackHeader.Children.Add(trackNameBox);

                var muteCb = new CheckBox
                {
                    Content = "Muted", IsChecked = track.Muted, FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xBB, 0x99)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                muteCb.IsCheckedChanged += (_, _) => { track.Muted = muteCb.IsChecked == true; SceneService.NotifyChanged(); };
                trackHeader.Children.Add(muteCb);

                var delTrackBtn = new Button
                {
                    Content = "x", Width = 22, Height = 22, Padding = new Thickness(0),
                    Background = new SolidColorBrush(Color.FromRgb(0x44, 0x22, 0x22)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                delTrackBtn.Click += (_, _) =>
                {
                    timeline.RemoveTrack(track);
                    SceneService.NotifyChanged();
                    RebuildTracks();
                };
                trackHeader.Children.Add(delTrackBtn);

                trackStack.Children.Add(trackHeader);

                // Clips
                for (int ci = 0; ci < track.Clips.Count; ci++)
                {
                    var clip = track.Clips[ci];
                    int clipIdx = ci;

                    var clipBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(6, 3),
                        Margin = new Thickness(8, 1, 0, 1)
                    };

                    var clipStack = new StackPanel { Spacing = 2 };

                    // Clip header: time range + delete
                    var clipHeader = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
                    clipHeader.Children.Add(new TextBlock
                    {
                        Text = $"Clip {ci}:",
                        FontSize = 10, FontWeight = FontWeight.SemiBold,
                        Foreground = TimelineTrackTypeBrush(track.Type),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    clipHeader.Children.Add(new TextBlock
                    {
                        Text = $"{clip.StartTime:F2}s - {clip.EndTime:F2}s ({clip.Duration:F2}s)",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0xAA, 0xBB)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    var delClipBtn = new Button
                    {
                        Content = "x", Width = 18, Height = 18, Padding = new Thickness(0), FontSize = 9,
                        Background = new SolidColorBrush(Color.FromRgb(0x44, 0x22, 0x22)),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    delClipBtn.Click += (_, _) =>
                    {
                        track.Clips.RemoveAt(clipIdx);
                        SceneService.NotifyChanged();
                        RebuildTracks();
                    };
                    clipHeader.Children.Add(delClipBtn);
                    clipStack.Children.Add(clipHeader);

                    // Clip fields
                    clipStack.Children.Add(TimelineClipFieldRow("Start", clip.StartTime, v => { clip.StartTime = Math.Max(0, v); }));
                    clipStack.Children.Add(TimelineClipFieldRow("Duration", clip.Duration, v => { clip.Duration = Math.Max(0.01f, v); }));
                    clipStack.Children.Add(TimelineClipFieldRow("Blend In", clip.BlendIn, v => { clip.BlendIn = Math.Max(0, v); }));
                    clipStack.Children.Add(TimelineClipFieldRow("Blend Out", clip.BlendOut, v => { clip.BlendOut = Math.Max(0, v); }));
                    clipStack.Children.Add(TimelineClipFieldRow("Speed", clip.Speed, v => { clip.Speed = v; }));

                    if (track.Type == TrackType.Animation || track.Type == TrackType.Audio)
                        clipStack.Children.Add(TimelineClipTextRow("Asset/State", clip.AssetPath, v => clip.AssetPath = v));
                    if (track.Type == TrackType.Animation || track.Type == TrackType.Camera || track.Type == TrackType.Activation)
                        clipStack.Children.Add(TimelineClipTextRow("Target", clip.TargetName, v => clip.TargetName = v));
                    if (track.Type == TrackType.Event)
                    {
                        clipStack.Children.Add(TimelineClipTextRow("Event Name", clip.EventName, v => clip.EventName = v));
                        clipStack.Children.Add(TimelineClipTextRow("Event Data", clip.EventData, v => clip.EventData = v));
                    }

                    clipBorder.Child = clipStack;
                    trackStack.Children.Add(clipBorder);
                }

                // Add clip button
                var addClipBtn = new Button
                {
                    Content = "+ Add Clip",
                    FontSize = 10, Height = 22, Padding = new Thickness(6, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x6A, 0xBF)),
                    Foreground = Brushes.White,
                    Margin = new Thickness(8, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                addClipBtn.Click += (_, _) =>
                {
                    float start = 0f;
                    if (track.Clips.Count > 0)
                        start = track.Clips[^1].EndTime + 0.1f;
                    track.Clips.Add(new TimelineClip { StartTime = start, Duration = 1f });
                    SceneService.NotifyChanged();
                    RebuildTracks();
                };
                trackStack.Children.Add(addClipBtn);

                trackPanel.Child = trackStack;
                trackList.Children.Add(trackPanel);
            }
        }

        RebuildTracks();

        // ── Add Track button ──
        var addTrackBtn = new Button
        {
            Content = "+ Add Track",
            FontSize = 11, Height = 26, Padding = new Thickness(8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x6A, 0xBF)),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        };
        addTrackBtn.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (TrackType tt in Enum.GetValues<TrackType>())
            {
                var captured = tt;
                var mi = new MenuItem { Header = tt.ToString() };
                mi.Click += (_, _) =>
                {
                    timeline.AddTrack($"{captured} Track", captured);
                    SceneService.NotifyChanged();
                    RebuildTracks();
                };
                menu.Items.Add(mi);
            }
            menu.Open(addTrackBtn);
        };
        root.Children.Add(addTrackBtn);

        return root;
    }

    static IBrush TimelineTrackTypeBrush(TrackType t) => t switch
    {
        TrackType.Animation => new SolidColorBrush(Color.FromRgb(0x4A, 0x8C, 0xFF)),
        TrackType.Camera => new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x33)),
        TrackType.Audio => new SolidColorBrush(Color.FromRgb(0x66, 0xDD, 0x66)),
        TrackType.Activation => new SolidColorBrush(Color.FromRgb(0xDD, 0x66, 0xDD)),
        TrackType.Event => new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
        _ => Brushes.Gray
    };

    Control TimelineClipFieldRow(string label, float value, Action<float> onChange)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label + ":", Width = 60, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center
        });
        var tb = new TextBox
        {
            Text = value.ToString("F3", CultureInfo.InvariantCulture),
            FontSize = 10, Height = 20, Width = 65,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48)),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        tb.LostFocus += (_, _) =>
        {
            if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                onChange(v);
                SceneService.NotifyChanged();
            }
        };
        row.Children.Add(tb);
        return row;
    }

    Control TimelineClipTextRow(string label, string value, Action<string> onChange)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label + ":", Width = 60, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center
        });
        var tb = new TextBox
        {
            Text = value, FontSize = 10, Height = 20, MinWidth = 100,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48)),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        tb.LostFocus += (_, _) =>
        {
            onChange(tb.Text ?? "");
            SceneService.NotifyChanged();
        };
        row.Children.Add(tb);
        return row;
    }

    // ═══════════════════════ Dialogue Runner Inspector UI ═══════════════════════

    Control DialogueRunnerInspectorUI(DialogueRunner runner)
    {
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(SectionTitle("Dialogue Tree"));

        // Ensure the runner has a tree to edit
        if (runner.Tree == null)
            runner.Tree = new DialogueTree();

        var tree = runner.Tree;

        // ── Tree name ──
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameRow.Children.Add(new TextBlock { Text = "Name", Width = 80, VerticalAlignment = VerticalAlignment.Center });
        var nameBox = new TextBox { Width = 200, Text = tree.Name };
        nameBox.LostFocus += (_, __) => { tree.Name = nameBox.Text ?? "Untitled"; SceneService.NotifyChanged(); };
        nameRow.Children.Add(nameBox);
        root.Children.Add(nameRow);

        // ── Voice / Text Mode ──
        root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 2) });
        root.Children.Add(SectionTitle("Dialogue Mode"));

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        modeRow.Children.Add(new TextBlock { Text = "Mode", Width = 80, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var modeCb = new ComboBox
        {
            ItemsSource = Enum.GetValues(typeof(DialogueMode)),
            SelectedItem = runner.Mode,
            MinWidth = 140,
            FontSize = 11
        };
        modeCb.SelectionChanged += (_, __) =>
        {
            if (modeCb.SelectedItem is DialogueMode m)
            {
                runner.Mode = m;
                SceneService.NotifyChanged();
            }
        };
        modeRow.Children.Add(modeCb);
        root.Children.Add(modeRow);

        var volRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        volRow.Children.Add(new TextBlock { Text = "Voice Vol", Width = 80, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var volBox = new TextBox { Width = 60, Text = runner.VoiceVolume.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11 };
        volBox.LostFocus += (_, __) =>
        {
            if (float.TryParse(volBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                runner.VoiceVolume = Math.Clamp(v, 0f, 2f);
                SceneService.NotifyChanged();
            }
        };
        volRow.Children.Add(volBox);
        root.Children.Add(volRow);

        var autoAdvRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        autoAdvRow.Children.Add(new TextBlock { Text = "Auto-advance on voice end", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var autoAdvCb = new CheckBox { IsChecked = runner.AutoAdvanceOnVoiceEnd };
        autoAdvCb.IsCheckedChanged += (_, __) =>
        {
            runner.AutoAdvanceOnVoiceEnd = autoAdvCb.IsChecked ?? false;
            SceneService.NotifyChanged();
        };
        autoAdvRow.Children.Add(autoAdvCb);
        root.Children.Add(autoAdvRow);

        root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 2) });
        root.Children.Add(SectionTitle("Nodes"));

        // ── Node list panel (rebuilt dynamically) ──
        var nodesPanel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(nodesPanel);

        // ── Add node buttons ──
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };

        void AddNodeBtn(string label, DialogueNodeType type)
        {
            var btn = new Button { Content = $"+ {label}", Padding = new Thickness(8, 3), FontSize = 11 };
            btn.Click += (_, __) =>
            {
                var node = tree.AddNode(type);
                if (type == DialogueNodeType.Start && string.IsNullOrEmpty(tree.StartNodeId))
                    tree.StartNodeId = node.Id;
                SceneService.NotifyChanged();
                RebuildDialogueNodeList(tree, nodesPanel, runner);
            };
            addRow.Children.Add(btn);
        }

        AddNodeBtn("Start", DialogueNodeType.Start);
        AddNodeBtn("Dialogue", DialogueNodeType.Dialogue);
        AddNodeBtn("Choice", DialogueNodeType.Choice);
        AddNodeBtn("Branch", DialogueNodeType.Branch);
        AddNodeBtn("End", DialogueNodeType.End);
        root.Children.Add(addRow);

        // Initial build
        RebuildDialogueNodeList(tree, nodesPanel, runner);

        // ── Variables section ──
        root.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 2) });
        root.Children.Add(SectionTitle("Dialogue Variables"));
        root.Children.Add(DialogueVariablesUI(runner.Variables));

        return root;
    }

    void RebuildDialogueNodeList(DialogueTree tree, StackPanel nodesPanel, DialogueRunner runner)
    {
        nodesPanel.Children.Clear();

        if (tree.Nodes.Count == 0)
        {
            nodesPanel.Children.Add(new TextBlock
            {
                Text = "No nodes. Add nodes above to build a dialogue.",
                Opacity = 0.5, FontSize = 11, Margin = new Thickness(4, 0, 0, 0)
            });
            return;
        }

        foreach (var node in tree.Nodes)
        {
            var nodePanel = DialogueNodeEditor(tree, node, nodesPanel, runner);
            nodesPanel.Children.Add(nodePanel);
        }
    }

    Control DialogueNodeEditor(DialogueTree tree, DialogueNode node, StackPanel nodesPanel, DialogueRunner runner)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = NodeTypeBrush(node.Type),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 2, 0, 2)
        };

        var panel = new StackPanel { Spacing = 4 };

        // ── Header: type badge + ID + collapse toggle + delete ──
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var typeBadge = new Border
        {
            Background = NodeTypeBrush(node.Type),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            Child = new TextBlock
            {
                Text = node.Type.ToString(),
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White
            }
        };
        headerRow.Children.Add(typeBadge);

        headerRow.Children.Add(new TextBlock
        {
            Text = $"#{node.Id}",
            FontSize = 10,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center
        });

        // Is this the start node?
        if (tree.StartNodeId == node.Id)
        {
            headerRow.Children.Add(new TextBlock
            {
                Text = "[START]",
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.LimeGreen,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else if (node.Type == DialogueNodeType.Start || node.Type == DialogueNodeType.Dialogue)
        {
            var setStart = new Button { Content = "Set Start", Padding = new Thickness(4, 1), FontSize = 10 };
            setStart.Click += (_, __) =>
            {
                tree.StartNodeId = node.Id;
                SceneService.NotifyChanged();
                RebuildDialogueNodeList(tree, nodesPanel, runner);
            };
            headerRow.Children.Add(setStart);
        }

        var deleteBtn = new Button
        {
            Content = "\u2715",
            Padding = new Thickness(4, 1),
            FontSize = 11,
            Foreground = Brushes.IndianRed,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        deleteBtn.Click += (_, __) =>
        {
            tree.RemoveNode(node.Id);
            if (tree.StartNodeId == node.Id) tree.StartNodeId = "";
            SceneService.NotifyChanged();
            RebuildDialogueNodeList(tree, nodesPanel, runner);
        };
        headerRow.Children.Add(deleteBtn);
        panel.Children.Add(headerRow);

        // ── Body: expandable fields panel ──
        var fieldsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(4, 4, 0, 0) };

        switch (node.Type)
        {
            case DialogueNodeType.Dialogue:
                fieldsPanel.Children.Add(DialogueFieldRow("Speaker", node.Speaker, 120, v => { node.Speaker = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueTextAreaRow("Text", node.Text, v => { node.Text = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueVoiceClipRow(node));
                fieldsPanel.Children.Add(DialogueFloatRow("Duration", node.Duration, v => { node.Duration = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueNodeLinkRow("Next Node", node.NextNodeId, tree, v => { node.NextNodeId = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueActionsEditor(node, runner));
                break;

            case DialogueNodeType.Choice:
                fieldsPanel.Children.Add(DialogueFieldRow("Speaker", node.Speaker, 120, v => { node.Speaker = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueTextAreaRow("Text", node.Text, v => { node.Text = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueVoiceClipRow(node));
                fieldsPanel.Children.Add(DialogueChoicesEditor(node, tree, runner));
                break;

            case DialogueNodeType.Branch:
                fieldsPanel.Children.Add(DialogueVarDropdownRow("Variable", node.BranchVariable, runner, v => { node.BranchVariable = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueFieldRow("Value", node.BranchValue, 160, v => { node.BranchValue = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueNodeLinkRow("True \u2192", node.TrueNextId, tree, v => { node.TrueNextId = v; SceneService.NotifyChanged(); }));
                fieldsPanel.Children.Add(DialogueNodeLinkRow("False \u2192", node.FalseNextId, tree, v => { node.FalseNextId = v; SceneService.NotifyChanged(); }));
                break;

            case DialogueNodeType.Start:
                fieldsPanel.Children.Add(DialogueNodeLinkRow("Next Node", node.NextNodeId, tree, v => { node.NextNodeId = v; SceneService.NotifyChanged(); }));
                break;

            case DialogueNodeType.End:
                fieldsPanel.Children.Add(new TextBlock { Text = "End of dialogue.", Opacity = 0.5, FontSize = 11 });
                break;
        }

        panel.Children.Add(fieldsPanel);
        border.Child = panel;
        return border;
    }

    // ── Dialogue editor helper controls ──

    static IBrush NodeTypeBrush(DialogueNodeType type) => type switch
    {
        DialogueNodeType.Start => new SolidColorBrush(Avalonia.Media.Color.FromRgb(50, 140, 70)),
        DialogueNodeType.Dialogue => new SolidColorBrush(Avalonia.Media.Color.FromRgb(55, 100, 160)),
        DialogueNodeType.Choice => new SolidColorBrush(Avalonia.Media.Color.FromRgb(160, 120, 40)),
        DialogueNodeType.Branch => new SolidColorBrush(Avalonia.Media.Color.FromRgb(140, 60, 140)),
        DialogueNodeType.End => new SolidColorBrush(Avalonia.Media.Color.FromRgb(140, 50, 50)),
        _ => Brushes.Gray
    };

    Control DialogueFieldRow(string label, string value, double width, Action<string> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var tb = new TextBox { Width = width, Text = value, FontSize = 11 };
        tb.LostFocus += (_, __) => onChange(tb.Text ?? "");
        row.Children.Add(tb);
        return row;
    }

    Control DialogueTextAreaRow(string label, string text, Action<string> onChange)
    {
        var col = new StackPanel { Spacing = 2 };
        col.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.8 });
        var tb = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 48,
            MaxHeight = 120,
            FontSize = 11
        };
        tb.LostFocus += (_, __) => onChange(tb.Text ?? "");
        col.Children.Add(tb);
        return col;
    }

    Control DialogueVoiceClipRow(DialogueNode node)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = "Voice",
            Width = 70,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11
        });

        var pathBox = new TextBox
        {
            Width = 160,
            Text = node.VoiceClipPath,
            FontSize = 11,
            Watermark = "(none - import audio)"
        };
        pathBox.LostFocus += (_, __) =>
        {
            node.VoiceClipPath = pathBox.Text ?? "";
            SceneService.NotifyChanged();
        };
        row.Children.Add(pathBox);

        var btnImport = new Button { Content = "...", Padding = new Thickness(6, 2), FontSize = 11 };
        btnImport.Click += async (_, __) =>
        {
            var win = OwnerWindow;
            if (win == null) return;

            var assetsDir = ProjectService.Current?.AssetsPath;
            var dlg = new OpenFileDialog
            {
                Title = "Import Voice Clip",
                AllowMultiple = false,
                Directory = assetsDir,
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "Audio Files", Extensions = { "wav", "mp3", "ogg", "flac", "aiff" } },
                    new FileDialogFilter { Name = "All Files", Extensions = { "*" } }
                }
            };
            var files = await dlg.ShowAsync(win);
            var picked = files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(picked)) return;

            var relPath = AudioAbsToRel(picked);
            pathBox.Text = relPath;
            node.VoiceClipPath = relPath;
            SceneService.NotifyChanged();
        };
        row.Children.Add(btnImport);

        var btnClear = new Button
        {
            Content = "\u2715",
            Padding = new Thickness(4, 2),
            FontSize = 10,
            Foreground = Brushes.IndianRed
        };
        btnClear.Click += (_, __) =>
        {
            pathBox.Text = "";
            node.VoiceClipPath = "";
            SceneService.NotifyChanged();
        };
        row.Children.Add(btnClear);

        return row;
    }

    Control DialogueFloatRow(string label, float value, Action<float> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var tb = new TextBox { Width = 80, Text = value.ToString(CultureInfo.InvariantCulture), FontSize = 11 };
        tb.LostFocus += (_, __) =>
        {
            if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                onChange(f);
        };
        row.Children.Add(tb);
        return row;
    }

    Control DialogueNodeLinkRow(string label, string currentId, DialogueTree tree, Action<string> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });

        // Build descriptive items: "(none)", then "type: preview (#id)" for each node
        var items = new List<DialogueNodeLinkItem> { new("", "(none)") };
        foreach (var n in tree.Nodes)
        {
            string desc = n.Type switch
            {
                DialogueNodeType.Dialogue => $"Dialogue: {Truncate(n.Speaker, 10)} - {Truncate(n.Text, 20)}",
                DialogueNodeType.Choice => $"Choice: {Truncate(n.Speaker, 10)} - {Truncate(n.Text, 20)}",
                DialogueNodeType.Branch => $"Branch: {n.BranchVariable}={n.BranchValue}",
                DialogueNodeType.Start => "Start",
                DialogueNodeType.End => "End",
                _ => n.Type.ToString()
            };
            items.Add(new(n.Id, $"{desc}  (#{n.Id})"));
        }

        var combo = new ComboBox
        {
            ItemsSource = items,
            MinWidth = 200,
            FontSize = 11,
            DisplayMemberBinding = new Binding(nameof(DialogueNodeLinkItem.Label))
        };
        combo.SelectedItem = items.FirstOrDefault(i => i.Id == currentId) ?? items[0];
        combo.SelectionChanged += (_, __) =>
        {
            onChange((combo.SelectedItem as DialogueNodeLinkItem)?.Id ?? "");
        };
        row.Children.Add(combo);
        return row;
    }

    Control DialogueVarDropdownRow(string label, string currentValue, DialogueRunner runner, Action<string> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });

        var varNames = new List<string> { "" };
        foreach (var key in runner.Variables.Keys)
            varNames.Add(key);

        var combo = new ComboBox { MinWidth = 120, FontSize = 11, IsEditable = true };
        combo.ItemsSource = varNames;
        combo.Text = currentValue;
        if (varNames.Contains(currentValue))
            combo.SelectedItem = currentValue;
        combo.SelectionChanged += (_, __) =>
        {
            var sel = combo.SelectedItem as string;
            if (sel != null) onChange(sel);
        };
        combo.LostFocus += (_, __) =>
        {
            if (!string.IsNullOrEmpty(combo.Text))
                onChange(combo.Text);
        };
        row.Children.Add(combo);
        return row;
    }

    static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "...";
    }

    Control DialogueChoicesEditor(DialogueNode node, DialogueTree tree, DialogueRunner runner)
    {
        var panel = new StackPanel { Spacing = 4 };
        var choicesList = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Choices", FontSize = 11, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(choicesList);

        void RebuildChoices()
        {
            choicesList.Children.Clear();
            for (int i = 0; i < node.Choices.Count; i++)
            {
                int idx = i;
                var choice = node.Choices[i];
                var choiceBorder = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(80, 80, 50)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 4),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                var choicePanel = new StackPanel { Spacing = 3 };

                // Choice text
                var textRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                textRow.Children.Add(new TextBlock { Text = $"[{idx}]", Width = 24, FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
                var textBox = new TextBox { Width = 180, Text = choice.Text, FontSize = 11, Watermark = "Choice text..." };
                textBox.LostFocus += (_, __) => { choice.Text = textBox.Text ?? ""; SceneService.NotifyChanged(); };
                textRow.Children.Add(textBox);

                var removeBtn = new Button { Content = "\u2212", Padding = new Thickness(4, 1), FontSize = 11, Foreground = Brushes.IndianRed };
                removeBtn.Click += (_, __) =>
                {
                    node.Choices.RemoveAt(idx);
                    SceneService.NotifyChanged();
                    RebuildChoices();
                };
                textRow.Children.Add(removeBtn);
                choicePanel.Children.Add(textRow);

                // Next node link (descriptive dropdown)
                choicePanel.Children.Add(DialogueNodeLinkRow("Goes to", choice.NextNodeId, tree,
                    v => { choice.NextNodeId = v; SceneService.NotifyChanged(); }));

                // Condition (optional) - variable dropdown + value
                var condRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                condRow.Children.Add(new TextBlock { Text = "If", Width = 20, FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });

                // Variable dropdown (editable, shows existing vars)
                var varNames = new List<string> { "" };
                foreach (var key in runner.Variables.Keys)
                    varNames.Add(key);
                var condVar = new ComboBox { MinWidth = 80, FontSize = 10, IsEditable = true, ItemsSource = varNames };
                condVar.Text = choice.ConditionVariable;
                if (varNames.Contains(choice.ConditionVariable))
                    condVar.SelectedItem = choice.ConditionVariable;
                condVar.SelectionChanged += (_, __) =>
                {
                    var sel = condVar.SelectedItem as string;
                    if (sel != null) { choice.ConditionVariable = sel; SceneService.NotifyChanged(); }
                };
                condVar.LostFocus += (_, __) =>
                {
                    choice.ConditionVariable = condVar.Text ?? "";
                    SceneService.NotifyChanged();
                };

                var condVal = new TextBox { Width = 60, Text = choice.ConditionValue, FontSize = 10, Watermark = "value" };
                condVal.LostFocus += (_, __) => { choice.ConditionValue = condVal.Text ?? ""; SceneService.NotifyChanged(); };
                condRow.Children.Add(condVar);
                condRow.Children.Add(new TextBlock { Text = "=", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
                condRow.Children.Add(condVal);
                choicePanel.Children.Add(condRow);

                choiceBorder.Child = choicePanel;
                choicesList.Children.Add(choiceBorder);
            }
        }

        RebuildChoices();

        var addChoice = new Button { Content = "+ Choice", Padding = new Thickness(6, 2), FontSize = 11, Margin = new Thickness(8, 2, 0, 0) };
        addChoice.Click += (_, __) =>
        {
            node.Choices.Add(new DialogueChoice());
            SceneService.NotifyChanged();
            RebuildChoices();
        };
        panel.Children.Add(addChoice);

        return panel;
    }

    Control DialogueActionsEditor(DialogueNode node, DialogueRunner runner)
    {
        var panel = new StackPanel { Spacing = 3 };
        var actionsList = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = "Actions", FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.8 });
        panel.Children.Add(actionsList);

        void RebuildActions()
        {
            actionsList.Children.Clear();
            for (int i = 0; i < node.Actions.Count; i++)
            {
                int idx = i;
                var action = node.Actions[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(new TextBlock { Text = "Set", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });

                // Variable: editable dropdown of existing variables
                var varNames = new List<string> { "" };
                foreach (var key in runner.Variables.Keys)
                    varNames.Add(key);
                var varBox = new ComboBox { MinWidth = 80, FontSize = 10, IsEditable = true, ItemsSource = varNames };
                varBox.Text = action.Variable;
                if (varNames.Contains(action.Variable))
                    varBox.SelectedItem = action.Variable;
                varBox.SelectionChanged += (_, __) =>
                {
                    var sel = varBox.SelectedItem as string;
                    if (sel != null) { action.Variable = sel; SceneService.NotifyChanged(); }
                };
                varBox.LostFocus += (_, __) =>
                {
                    action.Variable = varBox.Text ?? "";
                    SceneService.NotifyChanged();
                };

                var valBox = new TextBox { Width = 60, Text = action.Value, FontSize = 10, Watermark = "value" };
                valBox.LostFocus += (_, __) => { action.Value = valBox.Text ?? ""; SceneService.NotifyChanged(); };
                row.Children.Add(varBox);
                row.Children.Add(new TextBlock { Text = "=", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(valBox);
                var removeBtn = new Button { Content = "\u2212", Padding = new Thickness(4, 1), FontSize = 10, Foreground = Brushes.IndianRed };
                removeBtn.Click += (_, __) =>
                {
                    node.Actions.RemoveAt(idx);
                    SceneService.NotifyChanged();
                    RebuildActions();
                };
                row.Children.Add(removeBtn);
                actionsList.Children.Add(row);
            }
        }

        RebuildActions();

        var addAction = new Button { Content = "+ Action", Padding = new Thickness(4, 1), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
        addAction.Click += (_, __) =>
        {
            node.Actions.Add(new VariableAction());
            SceneService.NotifyChanged();
            RebuildActions();
        };
        panel.Children.Add(addAction);

        return panel;
    }

    Control DialogueVariablesUI(DialogueVariableStore store)
    {
        var panel = new StackPanel { Spacing = 4 };
        var varsList = new StackPanel { Spacing = 3 };
        panel.Children.Add(varsList);

        void RebuildVars()
        {
            varsList.Children.Clear();
            foreach (var key in store.Keys)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(new TextBlock { Text = key, Width = 100, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                var valBox = new TextBox { Width = 120, Text = store.Get(key), FontSize = 11 };
                valBox.LostFocus += (_, __) => { store.Set(key, valBox.Text ?? ""); SceneService.NotifyChanged(); };
                row.Children.Add(valBox);
                var removeBtn = new Button { Content = "\u2212", Padding = new Thickness(4, 1), FontSize = 10, Foreground = Brushes.IndianRed };
                removeBtn.Click += (_, __) =>
                {
                    store.Remove(key);
                    SceneService.NotifyChanged();
                    RebuildVars();
                };
                row.Children.Add(removeBtn);
                varsList.Children.Add(row);
            }
        }

        RebuildVars();

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        var newKeyBox = new TextBox { Width = 100, Watermark = "key", FontSize = 11 };
        var newValBox = new TextBox { Width = 100, Watermark = "value", FontSize = 11 };
        var addBtn = new Button { Content = "+ Add", Padding = new Thickness(6, 2), FontSize = 11 };
        addBtn.Click += (_, __) =>
        {
            var k = newKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(k)) return;
            store.Set(k, newValBox.Text ?? "");
            newKeyBox.Text = "";
            newValBox.Text = "";
            SceneService.NotifyChanged();
            RebuildVars();
        };
        addRow.Children.Add(newKeyBox);
        addRow.Children.Add(newValBox);
        addRow.Children.Add(addBtn);
        panel.Children.Add(addRow);

        return panel;
    }

    // ═══════════════════════ Tree Inspector UI ═══════════════════════

    Control TreeInspectorUI(GameObject owner, Tree tree)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(SectionTitle("Tree Settings"));

        bool isImport = tree.IsImportMode;

        // ── Mode indicator ──
        var modeLbl = new TextBlock
        {
            Text = isImport ? "Mode: Imported Model" : "Mode: Procedural",
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 4)
        };
        panel.Children.Add(modeLbl);

        // ── Import path (only if in import mode or to switch) ──
        var importRow = new StackPanel { Spacing = 4 };
        var importLbl = new TextBlock { Text = "Model Path:", Opacity = 0.8, Width = 80 };
        var importTxt = new TextBox { Text = tree.ModelPath, Width = 200 };
        importTxt.LostFocus += (_, _) =>
        {
            tree.ModelPath = importTxt.Text ?? "";
            tree.MarkDirty();
        };
        var importGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        importGrid.Children.Add(importLbl);
        importGrid.Children.Add(importTxt);
        panel.Children.Add(importGrid);

        // ── Procedural section (only when not in import mode) ──
        if (!isImport)
        {
            var procSection = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            procSection.Children.Add(new TextBlock { Text = "Trunk", FontWeight = FontWeight.SemiBold, Opacity = 0.9 });

            procSection.Children.Add(TreeSliderRow("Height", 0.5, 20, tree.TrunkHeight, v => { tree.TrunkHeight = (float)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));
            procSection.Children.Add(TreeSliderRow("Bottom Radius", 0.05, 2, tree.TrunkRadiusBottom, v => { tree.TrunkRadiusBottom = (float)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));
            procSection.Children.Add(TreeSliderRow("Top Radius", 0.01, 1, tree.TrunkRadiusTop, v => { tree.TrunkRadiusTop = (float)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));
            procSection.Children.Add(TreeSliderRow("Segments", 3, 24, tree.TrunkSegments, v => { tree.TrunkSegments = (int)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));

            procSection.Children.Add(new TextBlock { Text = "Canopy", FontWeight = FontWeight.SemiBold, Opacity = 0.9, Margin = new Thickness(0, 6, 0, 0) });

            // Shape dropdown
            var shapeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            shapeRow.Children.Add(new TextBlock { Text = "Shape", Width = 80, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
            var shapeCb = new ComboBox { Width = 120 };
            shapeCb.Items.Add("Sphere");
            shapeCb.Items.Add("Cone");
            shapeCb.Items.Add("Layered Cone");
            shapeCb.SelectedIndex = (int)tree.Shape;
            shapeCb.SelectionChanged += (_, _) =>
            {
                tree.Shape = (CanopyShape)(shapeCb.SelectedIndex >= 0 ? shapeCb.SelectedIndex : 0);
                tree.RebuildTree();
                SceneService.NotifyChanged();
            };
            shapeRow.Children.Add(shapeCb);
            procSection.Children.Add(shapeRow);

            procSection.Children.Add(TreeSliderRow("Radius", 0.5, 10, tree.CanopyRadius, v => { tree.CanopyRadius = (float)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));
            procSection.Children.Add(TreeSliderRow("Height", 0.5, 10, tree.CanopyHeight, v => { tree.CanopyHeight = (float)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));
            procSection.Children.Add(TreeSliderRow("Segments", 4, 24, tree.CanopySegments, v => { tree.CanopySegments = (int)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));

            if (tree.Shape == CanopyShape.LayeredCone)
                procSection.Children.Add(TreeSliderRow("Layers", 1, 6, tree.CanopyLayers, v => { tree.CanopyLayers = (int)v; tree.RebuildTree(); SceneService.NotifyChanged(); }));

            panel.Children.Add(ToolbarShell(procSection));
        }

        // ── Wind section ──
        var windSection = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        windSection.Children.Add(new TextBlock { Text = "Wind", FontWeight = FontWeight.SemiBold, Opacity = 0.9 });

        var vegCheck = new CheckBox { Content = "Vegetation Wind", IsChecked = tree.IsVegetation, Margin = new Thickness(0, 2, 0, 0) };
        vegCheck.IsCheckedChanged += (_, _) =>
        {
            tree.IsVegetation = vegCheck.IsChecked == true;
            SceneService.NotifyChanged();
        };
        windSection.Children.Add(vegCheck);
        windSection.Children.Add(TreeSliderRow("Sway", 0, 1, tree.WindSway, v => { tree.WindSway = (float)v; SceneService.NotifyChanged(); }));
        windSection.Children.Add(TreeSliderRow("Speed", 0.1, 5, tree.WindSpeed, v => { tree.WindSpeed = (float)v; SceneService.NotifyChanged(); }));

        panel.Children.Add(ToolbarShell(windSection));

        // ── Rebuild button ──
        var rebuildBtn = new Button { Content = "Rebuild Tree", Margin = new Thickness(0, 6, 0, 0) };
        rebuildBtn.Click += (_, _) => { tree.RebuildTree(); SceneService.NotifyChanged(); };
        panel.Children.Add(rebuildBtn);

        return panel;
    }

    static Control TreeSliderRow(string label, double min, double max, double initial, Action<double> onChange)
    {
        var grid = new Avalonia.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var lb = new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        Avalonia.Controls.Grid.SetColumn(lb, 0);

        var sl = new Slider { Minimum = min, Maximum = max, Value = initial };
        Avalonia.Controls.Grid.SetColumn(sl, 1);

        var val = new TextBlock { Text = initial.ToString(max <= 1.0 ? "0.00" : "0.0"), Width = 44, HorizontalAlignment = HorizontalAlignment.Right };
        Avalonia.Controls.Grid.SetColumn(val, 2);

        sl.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                var v = sl.Value;
                val.Text = v.ToString(max <= 1.0 ? "0.00" : "0.0");
                onChange(v);
            }
        };

        grid.Children.Add(lb);
        grid.Children.Add(sl);
        grid.Children.Add(val);
        return grid;
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
