using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Game_Engine.Core;

namespace Game_Engine.Views.Inspector;

/// <summary>Shared Import / Clear / drop-zone row for string path properties.</summary>
public sealed class AssetPathEditorOptions
{
    public required string Label { get; init; }
    public required string Watermark { get; init; }
    public required string DropHint { get; init; }
    public required string DialogTitle { get; init; }
    public required IReadOnlyList<FileDialogFilter> Filters { get; init; }
    public required IReadOnlyCollection<string> AcceptedExtensions { get; init; }
}

/// <summary>One Import + drop-zone editor used by AudioSource, Decal, and VegetationPainter path fields.</summary>
public static class AssetPathEditor
{
    static readonly List<FileDialogFilter> ImageDialogFilters = new()
    {
        new() { Name = "Image Files", Extensions = { "png", "jpg", "jpeg", "tga", "bmp", "tiff" } },
        new() { Name = "All Files", Extensions = { "*" } }
    };

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tiff", ".gif", ".webp" };

    public static readonly AssetPathEditorOptions AudioClip = new()
    {
        Label = "ClipPath",
        Watermark = "(none — import or drop audio file)",
        DropHint = "Drop audio file here  (.wav, .ogg, .mp3)",
        DialogTitle = "Import Audio Clip",
        Filters = new List<FileDialogFilter>
        {
            new() { Name = "Audio Files", Extensions = { "wav", "ogg", "mp3", "flac", "aiff" } },
            new() { Name = "All Files", Extensions = { "*" } }
        },
        AcceptedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".wav", ".ogg", ".mp3", ".flac", ".aiff", ".aif", ".wma", ".m4a" }
    };

    public static readonly AssetPathEditorOptions DecalTexture = new()
    {
        Label = "TexturePath",
        Watermark = "(none — import or drop image)",
        DropHint = "Drop image file here  (.png, .jpg, .tga, .bmp)",
        DialogTitle = "Import Decal Texture",
        Filters = ImageDialogFilters,
        AcceptedExtensions = ImageExtensions
    };

    public static readonly AssetPathEditorOptions VegetationMesh = new()
    {
        Label = "CustomMeshPath",
        Watermark = "(none — import or drop 3D model / texture)",
        DropHint = "Drop grass model or texture  (.fbx .obj .glb .png .jpg)",
        DialogTitle = "Import Grass Model or Texture",
        Filters = new List<FileDialogFilter>
        {
            new() { Name = "3D Models", Extensions = { "fbx", "obj", "gltf", "glb", "dae", "3ds" } },
            new() { Name = "Textures", Extensions = { "png", "jpg", "jpeg", "tga", "bmp", "tiff" } },
            new() { Name = "All Files", Extensions = { "*" } }
        },
        AcceptedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds",
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tiff", ".gif", ".webp"
        }
    };

    public static readonly AssetPathEditorOptions VegetationTexture = new()
    {
        Label = "TexturePath",
        Watermark = "(none — import or drop texture)",
        DropHint = "Drop texture here  (.png, .jpg, .tga, .bmp)",
        DialogTitle = "Import Grass Texture",
        Filters = ImageDialogFilters,
        AcceptedExtensions = ImageExtensions
    };

    public static readonly AssetPathEditorOptions Image = new()
    {
        Label = "TexturePath",
        Watermark = "(none — import or drop image)",
        DropHint = "Drop image file here  (.png, .jpg, .tga, .bmp)",
        DialogTitle = "Import Image",
        Filters = ImageDialogFilters,
        AcceptedExtensions = ImageExtensions
    };

    public static AssetPathEditorOptions FromAttribute(AssetPathAttribute attr, string propertyName)
    {
        var preset = attr.Kind switch
        {
            AssetPathKind.Audio => AudioClip,
            AssetPathKind.Image => Image,
            AssetPathKind.ModelOrImage => VegetationMesh,
            _ => FromExtensions(attr, propertyName)
        };

        if (attr.Kind != AssetPathKind.Custom)
        {
            return new AssetPathEditorOptions
            {
                Label = attr.Label ?? propertyName,
                Watermark = attr.Watermark ?? preset.Watermark,
                DropHint = attr.DropHint ?? preset.DropHint,
                DialogTitle = attr.DialogTitle ?? preset.DialogTitle,
                Filters = preset.Filters,
                AcceptedExtensions = preset.AcceptedExtensions
            };
        }

        return new AssetPathEditorOptions
        {
            Label = attr.Label ?? preset.Label,
            Watermark = attr.Watermark ?? preset.Watermark,
            DropHint = attr.DropHint ?? preset.DropHint,
            DialogTitle = attr.DialogTitle ?? preset.DialogTitle,
            Filters = preset.Filters,
            AcceptedExtensions = preset.AcceptedExtensions
        };
    }

    static AssetPathEditorOptions FromExtensions(AssetPathAttribute attr, string propertyName)
    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filterExts = new List<string>();
        foreach (var raw in attr.Extensions)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var ext = raw.Trim();
            if (!ext.StartsWith('.')) ext = "." + ext;
            exts.Add(ext);
            filterExts.Add(ext.TrimStart('.'));
        }

        if (exts.Count == 0)
        {
            return new AssetPathEditorOptions
            {
                Label = attr.Label ?? propertyName,
                Watermark = attr.Watermark ?? "(none — import or drop file)",
                DropHint = attr.DropHint ?? "Drop file here",
                DialogTitle = attr.DialogTitle ?? "Import File",
                Filters = new List<FileDialogFilter>
                {
                    new() { Name = "All Files", Extensions = { "*" } }
                },
                AcceptedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        var filterName = attr.FilterName ?? "Allowed files";
        var allowed = new FileDialogFilter { Name = filterName };
        foreach (var e in filterExts) allowed.Extensions.Add(e);
        return new AssetPathEditorOptions
        {
            Label = attr.Label ?? propertyName,
            Watermark = attr.Watermark ?? "(none — import or drop file)",
            DropHint = attr.DropHint ?? $"Drop file here  ({string.Join(" ", exts)})",
            DialogTitle = attr.DialogTitle ?? "Import File",
            Filters = new List<FileDialogFilter>
            {
                allowed,
                new() { Name = "All Files", Extensions = { "*" } }
            },
            AcceptedExtensions = exts
        };
    }

    /// <summary>Convert an absolute path to a project-relative path, if possible.</summary>
    public static string ToProjectRelative(string abs)
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

    public static Control Create(
        object target,
        PropertyInfo pathProp,
        Window? ownerWindow,
        Action<object, PropertyInfo> beginEdit,
        Action<object, PropertyInfo> commitEdit,
        AssetPathEditorOptions options)
    {
        var accepted = options.AcceptedExtensions as HashSet<string>
                       ?? new HashSet<string>(options.AcceptedExtensions, StringComparer.OrdinalIgnoreCase);

        var container = new StackPanel { Spacing = 4 };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = options.Label,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center
        });

        var tbPath = new TextBox
        {
            Width = 200,
            Watermark = options.Watermark
        };
        tbPath.Bind(TextBox.TextProperty, new Binding(pathProp.Name)
        {
            Source = target,
            Mode = BindingMode.TwoWay
        });
        tbPath.GotFocus += (_, _) => beginEdit(target, pathProp);
        tbPath.LostFocus += (_, _) =>
        {
            pathProp.SetValue(target, tbPath.Text);
            SceneService.NotifyChanged();
            commitEdit(target, pathProp);
        };

        var btnImport = new Button { Content = "Import…", Padding = new Thickness(8, 2) };
        var btnClear = new Button { Content = "Clear", Padding = new Thickness(8, 2) };

        row.Children.Add(tbPath);
        row.Children.Add(btnImport);
        row.Children.Add(btnClear);
        container.Children.Add(row);

        var dropText = new TextBlock
        {
            Text = options.DropHint,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
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

        btnImport.Click += async (_, _) =>
        {
            if (ownerWindow == null) return;

            var dlg = new OpenFileDialog
            {
                Title = options.DialogTitle,
                AllowMultiple = false,
                Directory = ProjectService.Current?.AssetsPath,
                Filters = options.Filters.ToList()
            };
            var files = await dlg.ShowAsync(ownerWindow);
            var picked = files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(picked)) return;

            ApplyPath(target, pathProp, tbPath, picked);
        };

        btnClear.Click += (_, _) =>
        {
            tbPath.Text = "";
            pathProp.SetValue(target, "");
            SceneService.NotifyChanged();
        };

        dropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Payload().HasFiles ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        dropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var pickedPath = TryGetDroppedPath(e);
            if (string.IsNullOrWhiteSpace(pickedPath)) return;
            if (!accepted.Contains(Path.GetExtension(pickedPath))) return;

            ApplyPath(target, pathProp, tbPath, pickedPath);
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        return container;
    }

    static void ApplyPath(object target, PropertyInfo pathProp, TextBox tbPath, string absPath)
    {
        var relPath = ToProjectRelative(absPath);
        tbPath.Text = relPath;
        pathProp.SetValue(target, relPath);
        SceneService.NotifyChanged();
    }

    static string? TryGetDroppedPath(DragEventArgs e)
    {
        if (e.Payload().HasFiles)
        {
            var names = e.Payload().GetFilePaths();
            var fromNames = names?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(fromNames)) return fromNames;
        }

        if (!e.Payload().HasFiles) return null;

        if (e.Payload().GetStorageItems() is IEnumerable<IStorageItem> items)
        {
            if (items.FirstOrDefault() is IStorageFile file)
            {
                var local = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(local)) return local;
            }
        }

        return null;
    }
}
