using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core.Component;
using Game_Engine.Core.Events;
using Collider = Game_Engine.Core.Component.Collider;

namespace Game_Engine.Core
{
    /// <summary>
    /// Decorate components with [Require(typeof(SomeBehavior), typeof(OtherBehavior))].
    /// When the owner GameObject awakens or the component enables, any missing required
    /// siblings are automatically added. Works in the editor and at runtime.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class RequireAttribute : Attribute
    {
        public Type[] Types { get; }
        public RequireAttribute(params Type[] types) { Types = types ?? Array.Empty<Type>(); }
    }

    /// <summary>
    /// Assigns a component to a named category for the Add Component dropdown in the Inspector.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ComponentCategoryAttribute : Attribute
    {
        public string Category { get; }
        public ComponentCategoryAttribute(string category) => Category = category;
    }

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
                    if (_enabled)
                    {
                        EnsureRequiredComponents();      // still enforced on enable
                        SafeCall(OnEnable, nameof(OnEnable));
                        if (LogLifecycle) LogDebug("Enabled");
                    }
                    else
                    {
                        SafeCall(OnDisable, nameof(OnDisable));
                        if (LogLifecycle) LogDebug("Disabled");
                    }
                    SceneService.NotifyChanged();
                    EnabledChanged?.Invoke(this, _enabled);
                }
            }
        }

        /// <summary>
        /// Set Enabled without firing OnEnable/OnDisable, SceneService.NotifyChanged, or EnabledChanged.
        /// Used by culling systems (e.g. VegetationPainter) that toggle visibility every frame.
        /// </summary>
        internal void SetEnabledSilent(bool value)
        {
            _enabled = value;
        }

        // ---- attach-time auto require (editor-time) --------------------------
        GameObject? _owner;
        [Persist]
        public GameObject? gameObject
        {
            get => _owner;
            internal set
            {
                if (ReferenceEquals(_owner, value)) return;
                _owner = value;

                // When a component is attached in the editor, add its required siblings immediately.
                if (_owner != null)
                {
                    EnsureRequiredComponents();

                    // if this is a MeshFilter and it has no mesh, default to Cube
                    var selfFilter = this as Game_Engine.Core.Component.MeshFilter;
                    if (selfFilter != null && selfFilter.Mesh == null)
                        selfFilter.Mesh = global::Game_Engine.Core.Mesh.CreateCube();

                    SceneService.NotifyChanged();
                }
            }
        }

        ///  write lifecycle events (Awake/Start/Enable/Disable/Destroy) to the console.
        [Persist] public bool LogLifecycle { get; set; } = false;

        public Transform Transform => gameObject?.Transform ?? new Transform();
        public bool IsActiveAndEnabled => Enabled && (gameObject?.IsActiveInHierarchy ?? true);
        public event Action<Behavior, bool>? EnabledChanged;

        // -- Runtime lifecycle (engine drives these internal calls) ------------------
        bool _awoken, _started, _destroyed;

        internal void __Awake()
        {
            if (_awoken) return;
            _awoken = true;
            EnsureRequiredComponents(); // still enforced at runtime start
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

        internal void __Update() { if (IsActiveAndEnabled) SafeCall(Update, nameof(Update)); }
        internal void __FixedUpdate() { if (IsActiveAndEnabled) SafeCall(FixedUpdate, nameof(FixedUpdate)); }
        internal void __LateUpdate() { if (IsActiveAndEnabled) SafeCall(LateUpdate, nameof(LateUpdate)); }

        internal void __OnDestroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            // Ensure OnDisable runs before OnDestroy so that components
            // can clean up static registries (e.g., PostProcessVolume._volumes).
            if (_enabled)
            {
                _enabled = false;
                SafeCall(OnDisable, nameof(OnDisable));
                if (LogLifecycle) LogDebug("Disabled (via Destroy)");
            }
            EventBus.UnsubscribeAll(this);
            SafeCall(OnDestroy, nameof(OnDestroy));
            if (LogLifecycle) LogDebug("OnDestroy");
        }

        // -- Overridable ------------------------------------------------------------

        public virtual void Awake() { }
        public virtual void Start() { }
        public virtual void Update() { }
        public virtual void FixedUpdate() { }
        public virtual void LateUpdate() { }
        public virtual void OnEnable() { }
        public virtual void OnDisable() { }
        public virtual void OnDestroy() { }

        /// <summary>
        /// Called by the scene deserializer AFTER all [Persist] properties have been applied.
        /// Override this when a component needs to reconcile scene-file data with external
        /// asset files (e.g., Terrain reloading from .terrain.json).
        /// During normal editor usage (adding a component manually), this is NOT called —
        /// OnEnable() handles that case.
        /// </summary>
        public virtual void PostDeserialize() { }

        /// <summary>Called when a trigger volume overlaps this object's collider (listener side), or when this object's trigger is overlapped.</summary>
        public virtual void OnTriggerEnter(Collider? other) { }

        /// <summary>Called each fixed step while the trigger overlap continues.</summary>
        public virtual void OnTriggerStay(Collider? other) { }

        /// <summary>Called when the overlap with a trigger ends.</summary>
        public virtual void OnTriggerExit(Collider? other) { }

        // -------- Runtime helper you can call explicitly ----------------------
        public void EnsureDependenciesNow(bool notify = true)
        {
            EnsureRequiredComponents();
            if (notify) SceneService.NotifyChanged();
        }

        // -------- logging helpers ---------------------------------------------
        protected void LogInfo(string msg) => Log.Info(Tag(msg));
        protected void LogWarning(string msg) => Log.Warning(Tag(msg));
        protected void LogError(string msg) => Log.Error(Tag(msg));
        protected void LogError(Exception ex, string? where = null) => Log.Error(ex, Context(where));
        protected void LogSuccess(string msg) => Log.Success(Tag(msg));
        protected void LogDebug(string msg) => Log.Debug(Tag(msg));
        string Tag(string msg) => (gameObject?.Name is { Length: > 0 } go) ? $"[{GetType().Name}@{go}] {msg}" : $"[{GetType().Name}] {msg}";
        string Context(string? where)
        {
            var owner = gameObject?.Name is { Length: > 0 } go ? $"{GetType().Name}@{go}" : GetType().Name;
            return string.IsNullOrWhiteSpace(where) ? owner : $"{owner}::{where}";
        }
        static void SafeCall(Action call, string where) { try { call(); } catch (Exception ex) { Log.Error(ex, where); } }

        // -------- QoL ----------------------------------------------------------
        protected T? GetComponent<T>() where T : Behavior => gameObject?.Behaviors?.OfType<T>().FirstOrDefault();
        protected T GetComponentRequired<T>() where T : Behavior => GetComponent<T>() ?? throw new InvalidOperationException($"{typeof(T).Name} not found on {gameObject?.Name ?? "<null>"}");
        protected bool HasComponent<T>() where T : Behavior => GetComponent<T>() != null;
        protected T GetOrAddComponent<T>() where T : Behavior, new()
        {
            var c = GetComponent<T>();
            if (c != null) return c;

            c = new T();

            // if we just created a MeshFilter and it has no mesh, default to Cube
            var mf = c as Game_Engine.Core.Component.MeshFilter;
            if (mf != null && mf.Mesh == null)
                mf.Mesh = global::Game_Engine.Core.Mesh.CreateCube();

            gameObject?.AddBehavior(c);
            SceneService.NotifyChanged();
            LogInfo($"Added required component: {typeof(T).Name}");
            return c;
        }


        // -------- Require enforcement (guarded & idempotent) -------------------
        static readonly HashSet<string> s_guard = new(StringComparer.Ordinal);

        protected void EnsureRequiredComponents()
        {
            var go = gameObject;
            if (go == null) return;

            var meType = GetType();
            var guardKey = $"{RuntimeHelpersGetId(go)}|{meType.FullName}";
            if (!s_guard.Add(guardKey)) return;

            try
            {
                var reqs = meType.GetCustomAttributes(typeof(RequireAttribute), inherit: true)
                                 .OfType<RequireAttribute>()
                                 .SelectMany(a => a.Types ?? Array.Empty<Type>())
                                 .Where(t => t != null && typeof(Behavior).IsAssignableFrom(t))
                                 .Distinct()
                                 .ToList();

                bool addedAny = false;
                foreach (var t in reqs)
                {
                    bool exists = go.Behaviors.Any(b => t.IsAssignableFrom(b.GetType()));
                    if (exists) continue;

                    Behavior inst;
                    try { inst = (Behavior)Activator.CreateInstance(t)!; }
                    catch (Exception ex) { LogWarning($"Require: failed to create {t.FullName}: {ex.Message}"); continue; }

                    go.AddBehavior(inst);

                    //  default any newly-required MeshFilter to a Cube mesh
                    var reqMf = inst as MeshFilter;
                    if (reqMf != null && reqMf.Mesh == null)
                    {
                        try { reqMf.Mesh = Mesh.CreateCube(); }
                        catch (Exception ex) { LogWarning($"Require: failed to assign Cube mesh to MeshFilter: {ex.Message}"); }
                    }

                    addedAny = true;
                    LogInfo($"Added required component: {t.Name}");
                }

                if (addedAny) SceneService.NotifyChanged();
            }
            finally
            {
                s_guard.Remove(guardKey);
            }
        }


        static int RuntimeHelpersGetId(object o)
        {
            try
            {
                var t = Type.GetType("System.Runtime.CompilerServices.RuntimeHelpers");
                var m = t?.GetMethod("GetHashCode", new[] { typeof(object) });
                if (m != null) return (int)m.Invoke(null, new object[] { o });
            }
            catch { }
            return o.GetHashCode();
        }
    }
}
