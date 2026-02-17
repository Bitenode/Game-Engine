#nullable enable
using System;
using System.IO;

namespace Game_Engine.Core;

/// <summary>
/// Runtime scene management API. Scripts call <see cref="LoadScene"/> to transition
/// between scenes at runtime. Loads are deferred to the start of the next frame so
/// the scene tree is never mutated during iteration.
/// </summary>
public static class SceneManager
{
    private static string? _pendingScenePath;
    private static string? _currentSceneName;

    /// <summary>The name of the currently loaded scene (without path or extension), or null.</summary>
    public static string? CurrentSceneName => _currentSceneName;

    /// <summary>True when a scene load has been queued but not yet processed.</summary>
    public static bool HasPendingLoad => _pendingScenePath != null;

    /// <summary>Fired after a new scene has finished loading and its behaviors have been started.</summary>
    public static event Action<string>? SceneLoaded;

    /// <summary>
    /// Queue a scene to load by name. The name is matched to a <c>.scene</c> file
    /// inside the project's <c>Scenes/</c> folder.
    /// <para>Example: <c>SceneManager.LoadScene("Main Menu")</c></para>
    /// The actual load is deferred and processed at the start of the next game frame.
    /// </summary>
    public static void LoadScene(string sceneName)
    {
        var project = ProjectService.Current;
        if (project == null)
        {
            Log.Warning($"[SceneManager] Cannot load scene '{sceneName}' — no project is open.");
            return;
        }

        string path = Path.Combine(project.ScenesPath, sceneName + ".scene");
        if (!File.Exists(path))
        {
            Log.Warning($"[SceneManager] Scene file not found: {path}");
            return;
        }

        _pendingScenePath = path;
        Log.Info($"[SceneManager] Queued scene load: {sceneName}");
    }

    /// <summary>
    /// Queue a scene to load by its full or project-relative file path.
    /// The actual load is deferred and processed at the start of the next game frame.
    /// </summary>
    public static void LoadSceneByPath(string path)
    {
        if (!File.Exists(path))
        {
            // Try resolving relative to project root
            var project = ProjectService.Current;
            if (project != null)
            {
                string resolved = Path.Combine(project.RootPath, path);
                if (File.Exists(resolved))
                {
                    _pendingScenePath = resolved;
                    Log.Info($"[SceneManager] Queued scene load: {path}");
                    return;
                }
            }

            Log.Warning($"[SceneManager] Scene file not found: {path}");
            return;
        }

        _pendingScenePath = path;
        Log.Info($"[SceneManager] Queued scene load: {path}");
    }

    /// <summary>
    /// Called by the game loop (GameView) at a safe point to process any pending
    /// scene load. Returns true if a load was processed.
    /// </summary>
    internal static bool ProcessPendingLoad(
        Action callOnDestroyAll,
        Action clearRegistries,
        Action rebuildCaches,
        Action callAwakeStart)
    {
        if (_pendingScenePath == null) return false;

        string path = _pendingScenePath;
        _pendingScenePath = null;

        string sceneName = Path.GetFileNameWithoutExtension(path);
        Log.Info($"[SceneManager] Loading scene: {sceneName}");

        try
        {
            // 1. Tear down current scene
            callOnDestroyAll();
            clearRegistries();

            // 2. Load the new scene
            SceneService.LoadFromFile(path);

            // 3. Rebuild caches and start new behaviors
            rebuildCaches();
            callAwakeStart();

            _currentSceneName = sceneName;
            SceneLoaded?.Invoke(sceneName);
            Log.Info($"[SceneManager] Scene '{sceneName}' loaded successfully.");
        }
        catch (Exception ex)
        {
            Log.Error($"[SceneManager] Failed to load scene '{sceneName}': {ex.Message}");
        }

        return true;
    }

    /// <summary>Reset all state (called when play mode stops).</summary>
    internal static void Reset()
    {
        _pendingScenePath = null;
        _currentSceneName = null;
    }
}
