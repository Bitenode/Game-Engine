#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Watches project shader files (<c>*.shader</c>, <c>*.glsl</c>) and, in Editor dev builds,
/// the engine's <c>Core/Rendering/Shaders/</c> directory. Debounces rapid save bursts (~150 ms),
/// notifies all active <see cref="ShaderLibrary"/> instances, and triggers viewport redraws.
/// </summary>
public static class ShaderHotReloader
{
    private const int DebounceMs = 150;

    private static readonly object s_lock = new();
    private static readonly HashSet<string> s_pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? s_debounceTimer;

    private static FileSystemWatcher? s_projectWatcher;
    private static FileSystemWatcher? s_builtinWatcher;
    private static bool s_started;

    /// <summary>Start watching project shaders and (when running from source) built-in engine shaders.</summary>
    public static void Start()
    {
        lock (s_lock)
        {
            if (s_started) return;
            s_started = true;

            s_debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

            ProjectService.ProjectOpened += OnProjectChanged;
            ProjectService.ProjectClosed += OnProjectChanged;

            StartBuiltinWatcherLocked();
            RefreshProjectWatcherLocked();
        }
    }

    /// <summary>Stop all file watchers and release timers.</summary>
    public static void Stop()
    {
        lock (s_lock)
        {
            if (!s_started) return;
            s_started = false;

            ProjectService.ProjectOpened -= OnProjectChanged;
            ProjectService.ProjectClosed -= OnProjectChanged;

            DisposeWatcher(ref s_projectWatcher);
            DisposeWatcher(ref s_builtinWatcher);

            s_debounceTimer?.Dispose();
            s_debounceTimer = null;
            s_pendingPaths.Clear();
        }
    }

    private static void OnProjectChanged()
    {
        lock (s_lock)
        {
            if (!s_started) return;
            RefreshProjectWatcherLocked();
        }
    }

    private static void StartBuiltinWatcherLocked()
    {
        DisposeWatcher(ref s_builtinWatcher);
        string? devDir = ShaderPreprocessor.DevShadersDirectory;
        if (string.IsNullOrEmpty(devDir) || !Directory.Exists(devDir))
            return;

        s_builtinWatcher = CreateWatcher(devDir);
        if (s_builtinWatcher != null)
            Log.Info($"[ShaderHotReloader] Watching built-in shaders: {devDir}");
    }

    private static void RefreshProjectWatcherLocked()
    {
        DisposeWatcher(ref s_projectWatcher);
        string? root = ProjectService.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        s_projectWatcher = CreateWatcher(root);
        if (s_projectWatcher != null)
            Log.Info($"[ShaderHotReloader] Watching project shaders: {root}");
    }

    private static FileSystemWatcher? CreateWatcher(string directory)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnFsEvent;
            watcher.Created += OnFsEvent;
            watcher.Renamed += OnFsRenamed;
            return watcher;
        }
        catch (Exception ex)
        {
            Log.Warning($"[ShaderHotReloader] Could not watch '{directory}': {ex.Message}");
            return null;
        }
    }

    private static void OnFsEvent(object sender, FileSystemEventArgs e)
        => EnqueuePath(e.FullPath);

    private static void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        EnqueuePath(e.OldFullPath);
        EnqueuePath(e.FullPath);
    }

    private static bool IsShaderFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnqueuePath(string path)
    {
        if (!IsShaderFile(path)) return;

        lock (s_lock)
        {
            if (!s_started) return;
            s_pendingPaths.Add(path);
            s_debounceTimer?.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private static void OnDebounceElapsed(object? state)
    {
        string[] paths;
        lock (s_lock)
        {
            if (s_pendingPaths.Count == 0) return;
            paths = new string[s_pendingPaths.Count];
            s_pendingPaths.CopyTo(paths);
            s_pendingPaths.Clear();
        }

        int totalMarked = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            try
            {
                totalMarked += ShaderLibrary.NotifyFileChanged(paths[i]);
            }
            catch (Exception ex)
            {
                Log.Warning($"[ShaderHotReloader] Error notifying change for '{paths[i]}': {ex.Message}");
            }
        }

        if (totalMarked > 0)
            SceneService.RequestFrame();
    }

    private static void DisposeWatcher(ref FileSystemWatcher? watcher)
    {
        if (watcher == null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFsEvent;
            watcher.Created -= OnFsEvent;
            watcher.Renamed -= OnFsRenamed;
            watcher.Dispose();
        }
        catch { }
        watcher = null;
    }
}
