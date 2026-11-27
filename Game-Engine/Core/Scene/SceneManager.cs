#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Game_Engine.Core;

/// <summary>
/// Controls how a scene is loaded relative to currently loaded scenes.
/// </summary>
public enum SceneLoadMode
{
    /// <summary>Replaces all currently loaded scenes (default).</summary>
    Single = 0,
    /// <summary>Appends the scene's root objects alongside currently loaded scenes.</summary>
    Additive = 1
}

/// <summary>
/// Handle representing an asynchronous scene load or unload operation.
/// Can be polled via <see cref="Progress"/> / <see cref="IsDone"/>, subscribed via
/// <see cref="Completed"/>, or awaited via <see cref="Task"/> (or directly awaited).
/// </summary>
public sealed class AsyncSceneLoadOperation
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile float _progress;
    private volatile bool _isDone;
    private volatile bool _allowSceneActivation = true;

    internal SceneDTO? ParsedDto;
    internal volatile bool IsParsedReady;

    /// <summary>Scene name (without path or extension).</summary>
    public string SceneName { get; }

    /// <summary>Resolved full file path of the scene, if available.</summary>
    public string? ScenePath { get; }

    /// <summary>Whether this operation replaces the scene or loads/unloads additively.</summary>
    public SceneLoadMode Mode { get; }

    /// <summary>Normalized progress in [0, 1] (0.0 start -> 0.5 file read -> 0.9 JSON parsed -> 1.0 activated).</summary>
    public float Progress => _progress;

    /// <summary>True once activation (or failure/unload) has finished.</summary>
    public bool IsDone => _isDone;

    /// <summary>
    /// When false, background reading &amp; JSON parsing still runs up to 0.9,
    /// but main-thread scene-graph activation waits until this is set to true.
    /// </summary>
    public bool AllowSceneActivation
    {
        get => _allowSceneActivation;
        set => _allowSceneActivation = value;
    }

    /// <summary>Error message if the operation failed; otherwise null.</summary>
    public string? Error { get; private set; }

    /// <summary>True if the operation completed with an error.</summary>
    public bool IsFaulted => Error != null;

    /// <summary>Awaitable task that completes when the operation finishes.</summary>
    public Task Task => _tcs.Task;

    /// <summary>Fired once when the operation finishes (on success or error).</summary>
    public event Action<AsyncSceneLoadOperation>? Completed;

    internal AsyncSceneLoadOperation(string sceneName, string? scenePath, SceneLoadMode mode)
    {
        SceneName = sceneName;
        ScenePath = scenePath;
        Mode = mode;
    }

    /// <summary>Allows directly awaiting an <see cref="AsyncSceneLoadOperation"/>.</summary>
    public TaskAwaiter GetAwaiter() => _tcs.Task.GetAwaiter();

    internal void SetProgress(float value)
    {
        _progress = Math.Clamp(value, 0f, 1f);
    }

    internal void CompleteSuccess()
    {
        if (_isDone) return;
        _progress = 1f;
        _isDone = true;
        ParsedDto = null;
        _tcs.TrySetResult();
        Completed?.Invoke(this);
    }

    internal void CompleteError(string error)
    {
        if (_isDone) return;
        Error = error;
        _progress = 1f;
        _isDone = true;
        ParsedDto = null;
        _tcs.TrySetResult();
        Completed?.Invoke(this);
    }
}

/// <summary>
/// Runtime scene management API. Scripts call <see cref="LoadScene(string)"/>,
/// <see cref="LoadScene(string, SceneLoadMode)"/>, or <see cref="LoadSceneAsync"/> to transition
/// between scenes or stream additive scenes at runtime. Activations and unloads are deferred to
/// the start of the next frame so the scene tree is never mutated during iteration.
/// </summary>
public static class SceneManager
{
    private readonly record struct PendingSyncLoad(string Path, SceneLoadMode Mode);
    private readonly record struct PendingUnload(string SceneName, AsyncSceneLoadOperation? Operation);

    private static readonly object s_lock = new();
    private static readonly List<PendingSyncLoad> _pendingSyncLoads = new();
    private static readonly List<AsyncSceneLoadOperation> _pendingAsyncLoads = new();
    private static readonly List<PendingUnload> _pendingUnloads = new();
    private static readonly Dictionary<string, List<GameObject>> _additiveScenes = new(StringComparer.OrdinalIgnoreCase);

    private static string? _currentSceneName;

    /// <summary>The name of the currently loaded primary scene (without path or extension), or null.</summary>
    public static string? CurrentSceneName => _currentSceneName;

    /// <summary>Names of all currently loaded additive scenes.</summary>
    public static IReadOnlyList<string> LoadedAdditiveScenes
    {
        get
        {
            lock (s_lock)
                return new List<string>(_additiveScenes.Keys);
        }
    }

    /// <summary>Returns true if the given scene name or path is currently loaded as the primary or an additive scene.</summary>
    public static bool IsSceneLoaded(string sceneNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(sceneNameOrPath)) return false;
        string name = NormalizeSceneName(sceneNameOrPath);
        if (!string.IsNullOrEmpty(_currentSceneName) &&
            string.Equals(_currentSceneName, name, StringComparison.OrdinalIgnoreCase))
            return true;

        lock (s_lock)
            return _additiveScenes.ContainsKey(name);
    }

    /// <summary>True when a scene load, ready async activation, or scene unload is queued for the next frame.</summary>
    public static bool HasPendingLoad
    {
        get
        {
            lock (s_lock)
            {
                if (_pendingSyncLoads.Count > 0 || _pendingUnloads.Count > 0)
                    return true;

                for (int i = 0; i < _pendingAsyncLoads.Count; i++)
                {
                    var op = _pendingAsyncLoads[i];
                    if (op.IsParsedReady && op.AllowSceneActivation)
                        return true;
                }
                return false;
            }
        }
    }

    /// <summary>Fired after a scene (single or additive) has finished loading and its behaviors have been started.</summary>
    public static event Action<string>? SceneLoaded;

    /// <summary>Fired after an additive scene has been unloaded and its root objects destroyed.</summary>
    public static event Action<string>? SceneUnloaded;

    /// <summary>
    /// Queue a scene to load by name (Single mode). The name is matched to a <c>.scene</c> file
    /// inside the project's <c>Scenes/</c> folder.
    /// <para>Example: <c>SceneManager.LoadScene("Main Menu")</c></para>
    /// The actual load is deferred and processed at the start of the next game frame.
    /// </summary>
    public static void LoadScene(string sceneName)
        => LoadScene(sceneName, SceneLoadMode.Single);

    /// <summary>
    /// Queue a scene to load by name or path in <see cref="SceneLoadMode.Single"/> or <see cref="SceneLoadMode.Additive"/> mode.
    /// The actual load is deferred and processed at the start of the next game frame.
    /// </summary>
    public static void LoadScene(string sceneNameOrPath, SceneLoadMode mode)
    {
        string? resolved = ResolveScenePath(sceneNameOrPath);
        if (resolved == null)
        {
            if (ProjectService.Current == null && !Path.IsPathRooted(sceneNameOrPath))
                Log.Warning($"[SceneManager] Cannot load scene '{sceneNameOrPath}' — no project is open.");
            else
                Log.Warning($"[SceneManager] Scene file not found: {sceneNameOrPath}");
            return;
        }

        lock (s_lock)
        {
            if (mode == SceneLoadMode.Single)
                _pendingSyncLoads.RemoveAll(p => p.Mode == SceneLoadMode.Single);
            _pendingSyncLoads.Add(new PendingSyncLoad(resolved, mode));
        }

        Log.Info($"[SceneManager] Queued scene load ({mode}): {Path.GetFileNameWithoutExtension(resolved)}");
    }

    /// <summary>
    /// Queue a scene to load by its full or project-relative file path (Single mode).
    /// The actual load is deferred and processed at the start of the next game frame.
    /// </summary>
    public static void LoadSceneByPath(string path)
        => LoadSceneByPath(path, SceneLoadMode.Single);

    /// <summary>
    /// Queue a scene to load by its full or project-relative file path in the specified <see cref="SceneLoadMode"/>.
    /// The actual load is deferred and processed at the start of the next game frame.
    /// </summary>
    public static void LoadSceneByPath(string path, SceneLoadMode mode)
        => LoadScene(path, mode);

    /// <summary>
    /// Asynchronously reads and parses a scene file on a background thread, then activates it on
    /// the main thread during <see cref="ProcessPendingLoad"/> once <see cref="AsyncSceneLoadOperation.AllowSceneActivation"/> is true.
    /// </summary>
    public static AsyncSceneLoadOperation LoadSceneAsync(string sceneNameOrPath, SceneLoadMode mode = SceneLoadMode.Single)
    {
        string fallbackName = NormalizeSceneName(sceneNameOrPath);
        string? resolved = ResolveScenePath(sceneNameOrPath);
        if (resolved == null)
        {
            string msg = ProjectService.Current == null && !Path.IsPathRooted(sceneNameOrPath)
                ? $"Cannot load scene '{sceneNameOrPath}' — no project is open."
                : $"Scene file not found: {sceneNameOrPath}";
            Log.Warning($"[SceneManager] {msg}");

            var failed = new AsyncSceneLoadOperation(fallbackName, null, mode);
            failed.CompleteError(msg);
            return failed;
        }

        string sceneName = Path.GetFileNameWithoutExtension(resolved);
        var op = new AsyncSceneLoadOperation(sceneName, resolved, mode);
        op.SetProgress(0f);

        lock (s_lock)
            _pendingAsyncLoads.Add(op);

        Log.Info($"[SceneManager] Starting async scene load ({mode}): {sceneName}");

        _ = Task.Run(() =>
        {
            try
            {
                string json = SceneSerialization.ReadSceneJson(resolved);
                op.SetProgress(0.5f);

                SceneDTO dto = SceneSerialization.ParseSceneJson(json);
                op.ParsedDto = dto;
                op.SetProgress(0.9f);
                op.IsParsedReady = true;
            }
            catch (Exception ex)
            {
                lock (s_lock)
                    _pendingAsyncLoads.Remove(op);
                Log.Error($"[SceneManager] Async load failed for '{sceneName}': {ex.Message}");
                op.CompleteError(ex.Message);
            }
        });

        return op;
    }

    /// <summary>
    /// Queue an additive scene to be unloaded at the start of the next frame.
    /// Only the root <see cref="GameObject"/>s added by that additive scene are destroyed.
    /// </summary>
    public static void UnloadScene(string sceneNameOrPath)
    {
        string name = NormalizeSceneName(sceneNameOrPath);
        if (string.IsNullOrWhiteSpace(name)) return;

        lock (s_lock)
            _pendingUnloads.Add(new PendingUnload(name, null));

        Log.Info($"[SceneManager] Queued additive scene unload: {name}");
    }

    /// <summary>
    /// Queue an additive scene to be unloaded at the start of the next frame and return an
    /// <see cref="AsyncSceneLoadOperation"/> handle that completes when teardown finishes.
    /// </summary>
    public static AsyncSceneLoadOperation UnloadSceneAsync(string sceneNameOrPath)
    {
        string name = NormalizeSceneName(sceneNameOrPath);
        var op = new AsyncSceneLoadOperation(name, null, SceneLoadMode.Additive);

        if (string.IsNullOrWhiteSpace(name))
        {
            op.CompleteError("Scene name is empty.");
            return op;
        }

        lock (s_lock)
            _pendingUnloads.Add(new PendingUnload(name, op));

        Log.Info($"[SceneManager] Queued async additive scene unload: {name}");
        return op;
    }

    /// <summary>
    /// Called by the game loop (GameView / PlayerSimulation) at a safe point to process any pending
    /// scene loads, ready async activations, or additive scene unloads. Returns true if any operation was processed.
    /// </summary>
    internal static bool ProcessPendingLoad(
        Action callOnDestroyAll,
        Action clearRegistries,
        Action rebuildCaches,
        Action callAwakeStart)
    {
        List<PendingUnload>? unloads = null;
        List<PendingSyncLoad>? syncLoads = null;
        List<AsyncSceneLoadOperation>? readyAsync = null;

        lock (s_lock)
        {
            if (_pendingUnloads.Count > 0)
            {
                unloads = new List<PendingUnload>(_pendingUnloads);
                _pendingUnloads.Clear();
            }

            if (_pendingSyncLoads.Count > 0)
            {
                syncLoads = new List<PendingSyncLoad>(_pendingSyncLoads);
                _pendingSyncLoads.Clear();
            }

            if (_pendingAsyncLoads.Count > 0)
            {
                for (int i = _pendingAsyncLoads.Count - 1; i >= 0; i--)
                {
                    var op = _pendingAsyncLoads[i];
                    if (op.IsParsedReady && op.AllowSceneActivation)
                    {
                        readyAsync ??= new List<AsyncSceneLoadOperation>();
                        readyAsync.Add(op);
                        _pendingAsyncLoads.RemoveAt(i);
                    }
                }
                readyAsync?.Reverse();
            }
        }

        if (unloads == null && syncLoads == null && readyAsync == null)
            return false;

        // 1. Process additive unloads
        if (unloads != null)
        {
            foreach (var unload in unloads)
                ExecuteUnload(unload, rebuildCaches);
        }

        // 2. Process synchronous loads
        if (syncLoads != null)
        {
            foreach (var load in syncLoads)
                ExecuteSyncLoad(load, callOnDestroyAll, clearRegistries, rebuildCaches, callAwakeStart);
        }

        // 3. Process ready async activations
        if (readyAsync != null)
        {
            foreach (var op in readyAsync)
                ExecuteAsyncActivation(op, callOnDestroyAll, clearRegistries, rebuildCaches, callAwakeStart);
        }

        return true;
    }

    private static void ExecuteUnload(PendingUnload unload, Action rebuildCaches)
    {
        List<GameObject>? roots;
        lock (s_lock)
        {
            if (!_additiveScenes.TryGetValue(unload.SceneName, out roots))
            {
                string msg = $"Additive scene '{unload.SceneName}' is not currently loaded.";
                Log.Warning($"[SceneManager] {msg}");
                unload.Operation?.CompleteError(msg);
                return;
            }
            _additiveScenes.Remove(unload.SceneName);
        }

        try
        {
            for (int i = 0; i < roots.Count; i++)
                SceneService.Destroy(roots[i]);

            rebuildCaches();
            SceneUnloaded?.Invoke(unload.SceneName);
            unload.Operation?.CompleteSuccess();
            Log.Info($"[SceneManager] Unloaded additive scene '{unload.SceneName}'.");
        }
        catch (Exception ex)
        {
            Log.Error($"[SceneManager] Failed to unload scene '{unload.SceneName}': {ex.Message}");
            unload.Operation?.CompleteError(ex.Message);
        }
    }

    private static void ExecuteSyncLoad(
        PendingSyncLoad load,
        Action callOnDestroyAll,
        Action clearRegistries,
        Action rebuildCaches,
        Action callAwakeStart)
    {
        string sceneName = Path.GetFileNameWithoutExtension(load.Path);
        Log.Info($"[SceneManager] Loading scene ({load.Mode}): {sceneName}");

        try
        {
            if (load.Mode == SceneLoadMode.Single)
            {
                lock (s_lock)
                    _additiveScenes.Clear();

                callOnDestroyAll();
                clearRegistries();

                SceneService.LoadFromFile(load.Path);

                rebuildCaches();
                callAwakeStart();

                _currentSceneName = sceneName;
                SceneLoaded?.Invoke(sceneName);
                Log.Info($"[SceneManager] Scene '{sceneName}' loaded successfully.");
            }
            else
            {
                var addedRoots = SceneService.LoadAdditiveFromFile(load.Path);
                RegisterAdditiveRoots(sceneName, addedRoots);

                rebuildCaches();
                callAwakeStart();

                SceneLoaded?.Invoke(sceneName);
                Log.Info($"[SceneManager] Additive scene '{sceneName}' loaded ({addedRoots.Count} root objects).");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SceneManager] Failed to load scene '{sceneName}': {ex.Message}");
        }
    }

    private static void ExecuteAsyncActivation(
        AsyncSceneLoadOperation op,
        Action callOnDestroyAll,
        Action clearRegistries,
        Action rebuildCaches,
        Action callAwakeStart)
    {
        var dto = op.ParsedDto;
        if (dto == null || op.ScenePath == null)
        {
            op.CompleteError("Parsed scene data is missing.");
            return;
        }

        Log.Info($"[SceneManager] Activating async scene ({op.Mode}): {op.SceneName}");

        try
        {
            if (op.Mode == SceneLoadMode.Single)
            {
                lock (s_lock)
                    _additiveScenes.Clear();

                callOnDestroyAll();
                clearRegistries();

                SceneService.LoadFromParsedDto(dto, op.ScenePath);

                rebuildCaches();
                callAwakeStart();

                _currentSceneName = op.SceneName;
                op.CompleteSuccess();
                SceneLoaded?.Invoke(op.SceneName);
                Log.Info($"[SceneManager] Async scene '{op.SceneName}' activated successfully.");
            }
            else
            {
                var addedRoots = SceneService.LoadAdditiveFromParsedDto(dto);
                RegisterAdditiveRoots(op.SceneName, addedRoots);

                rebuildCaches();
                callAwakeStart();

                op.CompleteSuccess();
                SceneLoaded?.Invoke(op.SceneName);
                Log.Info($"[SceneManager] Async additive scene '{op.SceneName}' activated ({addedRoots.Count} root objects).");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SceneManager] Failed to activate async scene '{op.SceneName}': {ex.Message}");
            op.CompleteError(ex.Message);
        }
    }

    private static void RegisterAdditiveRoots(string sceneName, List<GameObject> addedRoots)
    {
        lock (s_lock)
        {
            if (_additiveScenes.TryGetValue(sceneName, out var existing))
                existing.AddRange(addedRoots);
            else
                _additiveScenes[sceneName] = new List<GameObject>(addedRoots);
        }
    }

    private static string? ResolveScenePath(string sceneNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(sceneNameOrPath))
            return null;

        if (File.Exists(sceneNameOrPath))
            return Path.GetFullPath(sceneNameOrPath);

        var project = ProjectService.Current;
        if (project != null)
        {
            string inScenesWithExt = Path.Combine(project.ScenesPath, sceneNameOrPath + ".scene");
            if (File.Exists(inScenesWithExt))
                return Path.GetFullPath(inScenesWithExt);

            string inScenes = Path.Combine(project.ScenesPath, sceneNameOrPath);
            if (File.Exists(inScenes))
                return Path.GetFullPath(inScenes);

            string inRoot = Path.Combine(project.RootPath, sceneNameOrPath);
            if (File.Exists(inRoot))
                return Path.GetFullPath(inRoot);

            string inRootWithExt = Path.Combine(project.RootPath, sceneNameOrPath + ".scene");
            if (File.Exists(inRootWithExt))
                return Path.GetFullPath(inRootWithExt);
        }

        return null;
    }

    private static string NormalizeSceneName(string sceneNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(sceneNameOrPath))
            return string.Empty;
        return Path.GetFileNameWithoutExtension(sceneNameOrPath.Trim());
    }

    /// <summary>Reset all state (called when play mode stops).</summary>
    internal static void Reset()
    {
        lock (s_lock)
        {
            _pendingSyncLoads.Clear();
            _pendingAsyncLoads.Clear();
            _pendingUnloads.Clear();
            _additiveScenes.Clear();
        }
        _currentSceneName = null;
        SaveSystem.SaveManager.CancelPendingLoads();
        Component.PlanetTerrain.ClearLaunchRequest();
    }
}
