using System;
using System.Linq;

namespace Game_Engine.Core;

/// Base class for attachable scripts/components
public abstract class Behavior : ObservableObject
{
    // -- Persisted flags ---------------------------------------------------------
    [Persist] bool _enabled = true;

    /// Enable/disable this component. Triggers OnEnable/OnDisable and refreshes UI.
    [Persist]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value))
            {
                if (_enabled) { SafeCall(OnEnable, nameof(OnEnable)); if (LogLifecycle) LogDebug("Enabled"); }
                else { SafeCall(OnDisable, nameof(OnDisable)); if (LogLifecycle) LogDebug("Disabled"); }

                EnabledChanged?.Invoke(this, _enabled);
                // Inspector updates bind to SceneService; this does NOT affect logging.
                SceneService.NotifyChanged();
            }
        }
    }

    /// Engine sets this when the component is attached.
    [Persist] public GameObject? gameObject { get; internal set; }

    ///  write lifecycle events (Awake/Start/Enable/Disable/Destroy) to the console.
    [Persist] public bool LogLifecycle { get; set; } = false;

    public bool IsActiveAndEnabled => Enabled;
    public event Action<Behavior, bool>? EnabledChanged;

    // -- Runtime lifecycle (engine drives these internal calls) ------------------
    bool _awoken, _started, _destroyed;

    internal void __Awake()
    {
        if (_awoken) return;
        _awoken = true;
        SafeCall(Awake, nameof(Awake));
        if (LogLifecycle) LogDebug("Awake");
    }

    internal void __Start()
    {
        if (!_awoken) __Awake();
        if (_started) return;
        _started = true;
        SafeCall(Start, nameof(Start));
        if (LogLifecycle) LogDebug("Start");
    }

    internal void __Update()
    {
        if (!IsActiveAndEnabled) return;
        SafeCall(Update, nameof(Update));
    }

    internal void __FixedUpdate()
    {
        if (!IsActiveAndEnabled) return;
        SafeCall(FixedUpdate, nameof(FixedUpdate));
    }

    internal void __LateUpdate()
    {
        if (!IsActiveAndEnabled) return;
        SafeCall(LateUpdate, nameof(LateUpdate));
    }

    internal void __OnDestroy()
    {
        if (_destroyed) return;
        _destroyed = true;
        SafeCall(OnDestroy, nameof(OnDestroy));
        if (LogLifecycle) LogDebug("OnDestroy");
    }

    // -- Overridables ------------------------------------------------------------
    public virtual void Awake() { }
    public virtual void Start() { }
    public virtual void Update() { }
    public virtual void FixedUpdate() { }
    public virtual void LateUpdate() { }
    public virtual void OnEnable() { }
    public virtual void OnDisable() { }
    public virtual void OnDestroy() { }

    // -- Logging helpers (writes to the built-in console via Log.Logged) ---------
    protected void LogInfo(string msg) => Log.Info(Tag(msg));
    protected void LogWarning(string msg) => Log.Warning(Tag(msg));
    protected void LogError(string msg) => Log.Error(Tag(msg));
    protected void LogError(Exception ex, string? where = null)
        => Log.Error(ex, Context(where));
    protected void LogSuccess(string msg) => Log.Success(Tag(msg));
    protected void LogDebug(string msg) => Log.Debug(Tag(msg));

    string Tag(string msg)
    {
        var go = gameObject?.Name;
        return go is { Length: > 0 }
            ? $"[{GetType().Name}@{go}] {msg}"
            : $"[{GetType().Name}] {msg}";
    }

    string Context(string? where)
    {
        var owner = gameObject?.Name is { Length: > 0 }
            ? $"{GetType().Name}@{gameObject!.Name}"
            : GetType().Name;
        return string.IsNullOrWhiteSpace(where) ? owner : $"{owner}::{where}";
    }

    static void SafeCall(Action call, string where)
    {
        try { call(); }
        catch (Exception ex) { Log.Error(ex, where); } 
    }

    // -- QoL ---------------------------------------------------------------------
    protected T? GetComponent<T>() where T : Behavior
        => gameObject?.Behaviors?.OfType<T>().FirstOrDefault();

    protected T GetComponentRequired<T>() where T : Behavior
        => GetComponent<T>() ?? throw new InvalidOperationException(
            $"{typeof(T).Name} not found on {gameObject?.Name ?? "<null>"}");
}
