#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Game_Engine.Core.Editor;

/// <summary>Maps to Avalonia <see cref="DispatcherPriority"/> for <see cref="EditorJobScheduler.PostToUi"/> without exposing Avalonia in script-facing APIs.</summary>
public enum EditorUiPostPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Render = 3
}

/// <summary>
/// Editor-wide CPU job queue: run heavy work off the UI thread, then marshal results back via the UI dispatcher.
/// OpenGL and scene mutation must stay on the UI thread; use <see cref="RunAsync{T}"/> only for CPU-only work.
/// </summary>
public static class EditorJobScheduler
{
    static readonly SemaphoreSlim HeavyJobSlots = new(initialCount: 2, maxCount: 2);
    static Dispatcher? _dispatcher;

    /// <summary>Prefer calling from <see cref="Avalonia.Controls.Window.Opened"/> so a concrete dispatcher is captured.</summary>
    public static void AttachDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    static Dispatcher UiDispatcher => _dispatcher ?? Dispatcher.UIThread;

    static DispatcherPriority MapPriority(EditorUiPostPriority p) => p switch
    {
        EditorUiPostPriority.Low => DispatcherPriority.Background,
        EditorUiPostPriority.Normal => DispatcherPriority.Normal,
        EditorUiPostPriority.High => DispatcherPriority.Send,
        EditorUiPostPriority.Render => DispatcherPriority.Render,
        _ => DispatcherPriority.Normal
    };

    /// <summary>Queue <paramref name="action"/> on the UI thread (does not wait for completion).</summary>
    public static void PostToUi(Action action, EditorUiPostPriority priority = EditorUiPostPriority.Normal)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        UiDispatcher.Post(action, MapPriority(priority));
    }

    /// <summary>Run <paramref name="work"/> on the UI thread and await completion.</summary>
    public static async Task InvokeOnUiAsync(Action work, EditorUiPostPriority priority = EditorUiPostPriority.Normal)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        await UiDispatcher.InvokeAsync(work, MapPriority(priority));
    }

    /// <summary>Run <paramref name="work"/> on the UI thread and await its result.</summary>
    public static async Task<T> RunOnUiAsync<T>(Func<T> work, EditorUiPostPriority priority = EditorUiPostPriority.Normal)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        return await UiDispatcher.InvokeAsync(work, MapPriority(priority));
    }

    /// <summary>
    /// Runs CPU-bound work on the thread pool (max two concurrent heavy jobs). Capture scene/UI only after awaiting, then use <see cref="InvokeOnUiAsync"/> to apply.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken = default)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        await HeavyJobSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => work(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            HeavyJobSlots.Release();
        }
    }

    /// <summary>CPU-bound work with no return value.</summary>
    public static async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        await HeavyJobSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => work(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            HeavyJobSlots.Release();
        }
    }
}

/// <summary>
/// Script- and extension-friendly entry points for background jobs (forwards to <see cref="EditorJobScheduler"/>).
/// Do not mutate the scene graph or touch GL from inside <see cref="RunCpuAsync{T}"/> callbacks.
/// </summary>
public static class EditorJobs
{
    public static Task<T> RunCpuAsync<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken = default)
        => EditorJobScheduler.RunAsync(work, cancellationToken);

    public static Task RunCpuAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => EditorJobScheduler.RunAsync(work, cancellationToken);

    public static void PostToUi(Action action, EditorUiPostPriority priority = EditorUiPostPriority.Normal)
        => EditorJobScheduler.PostToUi(action, priority);

    public static Task InvokeOnUiAsync(Action work, EditorUiPostPriority priority = EditorUiPostPriority.Normal)
        => EditorJobScheduler.InvokeOnUiAsync(work, priority);

    public static Task<T> RunOnUiAsync<T>(Func<T> work, EditorUiPostPriority priority = EditorUiPostPriority.Normal)
        => EditorJobScheduler.RunOnUiAsync(work, priority);

    /// <summary>CPU phase then UI phase with a result payload (e.g. parsed file → apply to selection).</summary>
    public static async Task<T> RunCpuThenUiAsync<T>(Func<CancellationToken, T> cpuWork, Action<T> onUi, CancellationToken cancellationToken = default)
    {
        var r = await RunCpuAsync(cpuWork, cancellationToken).ConfigureAwait(false);
        await InvokeOnUiAsync(() => onUi(r)).ConfigureAwait(false);
        return r;
    }
}
