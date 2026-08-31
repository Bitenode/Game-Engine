#nullable enable
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Game_Engine.Core;

namespace Avalonia.Input
{
    public static class EditorDragFormats
    {
        public static readonly DataFormat<string> ProjectNodePath =
            DataFormat.CreateStringApplicationFormat("project-node-path");

        public static readonly DataFormat<GameObject> GameObjectRef =
            DataFormat.CreateInProcessFormat<GameObject>("gameobject");
    }

    public readonly struct DragPayload
    {
        private readonly IDataTransfer? _data;

        public DragPayload(IDataTransfer? data) => _data = data;

        public bool HasFiles => _data != null && _data.Contains(DataFormat.File);

        public IReadOnlyList<IStorageItem>? GetStorageItems()
            => _data?.TryGetFiles();

        public IEnumerable<string>? GetFilePaths()
        {
            var items = GetStorageItems();
            if (items == null || items.Count == 0) return null;
            return items
                .Select(i => i.TryGetLocalPath() ?? i.Path.LocalPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))!;
        }

        public string? GetProjectNodePath()
            => _data?.TryGetValue(EditorDragFormats.ProjectNodePath);

        public GameObject? GetGameObject()
            => _data?.TryGetValue(EditorDragFormats.GameObjectRef);

        public bool Contains(string format)
        {
            if (_data == null) return false;
            if (IsFileFormat(format)) return HasFiles;
            if (format == "project-node-path") return GetProjectNodePath() != null;
            if (format == "application/x-gameobject") return GetGameObject() != null;
            return false;
        }

        public object? Get(string format)
        {
            if (IsFileFormat(format)) return GetStorageItems();
            if (format == "project-node-path") return GetProjectNodePath();
            if (format == "application/x-gameobject") return GetGameObject();
            return null;
        }

        public IEnumerable<string>? GetFileNames() => GetFilePaths();

        private static bool IsFileFormat(string format)
            => format is "FileNames" or "Files" or "files" or "filenames";
    }

    public static class DragEventArgsCompat
    {
        public static DragPayload Payload(this DragEventArgs e) => new(e.DataTransfer);
    }
}
