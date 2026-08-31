#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Avalonia.Controls
{
    /// <summary>Legacy file-dialog filter type shimmed for Avalonia 12 editor code.</summary>
    public sealed class FileDialogFilter
    {
        public string Name { get; set; } = "";
        public List<string> Extensions { get; set; } = new();
    }

    internal static class FileDialogShim
    {
        public static FilePickerFileType[] ToTypes(IList<FileDialogFilter> filters)
        {
            if (filters.Count == 0) return Array.Empty<FilePickerFileType>();
            return filters.Select(f => new FilePickerFileType(string.IsNullOrWhiteSpace(f.Name) ? "Files" : f.Name)
            {
                Patterns = f.Extensions.Select(ToPattern).ToArray()
            }).ToArray();
        }

        public static string ToPattern(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext) || ext == "*") return "*.*";
            ext = ext.Trim();
            if (ext.StartsWith("*.")) return ext;
            if (ext.StartsWith(".")) return "*" + ext;
            return "*." + ext;
        }

        public static TopLevel? ResolveTopLevel(Visual? host)
            => host as TopLevel ?? (host != null ? TopLevel.GetTopLevel(host) : null);

        public static async Task<IStorageFolder?> TryFolderAsync(IStorageProvider provider, string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;
            try { return await provider.TryGetFolderFromPathAsync(directory); }
            catch { return null; }
        }

        public static string? LocalPath(IStorageItem? item)
            => item?.TryGetLocalPath() ?? item?.Path.LocalPath;
    }

    public sealed class OpenFileDialog
    {
        public string? Title { get; set; }
        public string? Directory { get; set; }
        public bool AllowMultiple { get; set; }
        public List<FileDialogFilter> Filters { get; set; } = new();

        public async Task<string[]?> ShowAsync(Visual? parent)
        {
            var top = FileDialogShim.ResolveTopLevel(parent);
            if (top == null) return null;

            var folder = await FileDialogShim.TryFolderAsync(top.StorageProvider, Directory);
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Title,
                AllowMultiple = AllowMultiple,
                SuggestedStartLocation = folder,
                FileTypeFilter = Filters.Count > 0 ? FileDialogShim.ToTypes(Filters) : null
            });

            if (files == null || files.Count == 0) return Array.Empty<string>();
            return files.Select(FileDialogShim.LocalPath).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray()!;
        }
    }

    public sealed class SaveFileDialog
    {
        public string? Title { get; set; }
        public string? Directory { get; set; }
        public string? DefaultExtension { get; set; }
        public string? InitialFileName { get; set; }
        public List<FileDialogFilter> Filters { get; set; } = new();

        public async Task<string?> ShowAsync(Visual? parent)
        {
            var top = FileDialogShim.ResolveTopLevel(parent);
            if (top == null) return null;

            var folder = await FileDialogShim.TryFolderAsync(top.StorageProvider, Directory);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Title,
                SuggestedStartLocation = folder,
                SuggestedFileName = InitialFileName,
                DefaultExtension = DefaultExtension,
                FileTypeChoices = Filters.Count > 0 ? FileDialogShim.ToTypes(Filters) : null
            });

            return FileDialogShim.LocalPath(file);
        }
    }

    public sealed class OpenFolderDialog
    {
        public string? Title { get; set; }
        public string? Directory { get; set; }

        public async Task<string?> ShowAsync(Visual? parent)
        {
            var top = FileDialogShim.ResolveTopLevel(parent);
            if (top == null) return null;

            var start = await FileDialogShim.TryFolderAsync(top.StorageProvider, Directory);
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Title,
                AllowMultiple = false,
                SuggestedStartLocation = start
            });

            return folders.Count > 0 ? FileDialogShim.LocalPath(folders[0]) : null;
        }
    }
}

namespace Avalonia.VisualTree
{
    public static class VisualRootCompatExtensions
    {
        public static TopLevel? GetVisualRoot(this Visual visual) => TopLevel.GetTopLevel(visual);
    }
}

namespace Avalonia.Input.Platform
{
    public static class ClipboardGetTextCompat
    {
        public static Task<string?> GetTextAsync(this IClipboard clipboard) => clipboard.TryGetTextAsync();
    }
}
