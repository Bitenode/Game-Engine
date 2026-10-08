#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using Collider = Game_Engine.Core.Component.Collider;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>
    /// Runs a <c>.blueprint</c> visual script on this GameObject (Begin Play + optional Tick chains).
    /// Author graphs via Window → New Blueprint Tab; assign <see cref="BlueprintAssetPath"/> relative to the project root (e.g. under <c>Assets/Blueprints/</c>).
    /// See shipped documentation: <c>Docs/14_Visual_Blueprints.md</c>.
    /// </summary>
    [ComponentCategory("Scripting")]
    public sealed class VisualBlueprintBehavior : Behavior
    {
        /// <summary>Project-relative path, e.g. <c>Assets/Blueprints/MyBehavior.blueprint</c> or absolute.</summary>
        [Persist, HideInInspector] public string? BlueprintAssetPath { get; set; }

        [Persist, HideInInspector] public bool LogSteps { get; set; } = true;

        /// <summary>If false, Tick event nodes are not run (Begin Play still runs).</summary>
        [Persist, HideInInspector] public bool RunTickGraph { get; set; } = true;

        /// <summary>Legacy string key/value store (v1 graphs and string-map helpers).</summary>
        [Persist, HideInInspector] public Dictionary<string, string> Variables { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Instance overrides for typed variables (name → literal), applied after asset defaults.</summary>
        [Persist, HideInInspector] public Dictionary<string, string> TypedVariableOverrides { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        BlueprintDocument? _document;
        readonly List<(double fireTime, string nodeId, bool logSteps)> _pendingExec = new();
        readonly HashSet<string> _doOnceSpent = new(StringComparer.Ordinal);
        readonly Dictionary<string, bool> _gateOpen = new(StringComparer.Ordinal);

        /// <summary>Loaded document (event graph + variables + functions).</summary>
        public BlueprintDocument? Document => _document;

        /// <summary>Typed variable store for GetVar / SetVar.</summary>
        public BlueprintTypedStore TypedVariables { get; } = new();

        internal void ScheduleExec(string nodeId, double fireTime, bool logSteps)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            _pendingExec.Add((fireTime, nodeId, logSteps));
        }

        internal bool IsDoOnceSpent(string nodeId) => _doOnceSpent.Contains(nodeId);
        internal void MarkDoOnceSpent(string nodeId) => _doOnceSpent.Add(nodeId);
        internal void ResetDoOnce(string nodeId) => _doOnceSpent.Remove(nodeId);

        internal bool IsGateOpen(string nodeId, bool startClosed)
        {
            if (!_gateOpen.TryGetValue(nodeId, out var open))
            {
                open = !startClosed;
                _gateOpen[nodeId] = open;
            }
            return open;
        }

        internal void SetGateOpen(string nodeId, bool open) => _gateOpen[nodeId] = open;

        public override void PostDeserialize()
        {
            base.PostDeserialize();
            Reload();
        }

        public override void OnEnable()
        {
            base.OnEnable();
            Reload();
        }

        public override void OnDisable()
        {
            _pendingExec.Clear();
            base.OnDisable();
        }

        public override void Start()
        {
            Reload();
            if (_document != null)
                BlueprintFlowRuntime.RunEventNodes(this, _document, "BeginPlay");
        }

        public override void Update()
        {
            ProcessPendingExec();
            if (_document == null) return;
            BlueprintFlowRuntime.PollInputEvents(this, _document);
            if (!RunTickGraph) return;
            BlueprintFlowRuntime.RunEventNodes(this, _document, "Tick");
        }

        public override void OnTriggerEnter(Collider? other)
        {
            if (_document == null || other?.gameObject == null) return;
            BlueprintFlowRuntime.RunEventNodeWithOutputs(this, _document, "TriggerEnter",
                new Dictionary<string, BlueprintValue>
                {
                    ["other"] = BlueprintValue.FromObject(other.gameObject)
                });
        }

        public override void OnTriggerStay(Collider? other)
        {
            if (_document == null || other?.gameObject == null) return;
            BlueprintFlowRuntime.RunEventNodeWithOutputs(this, _document, "TriggerStay",
                new Dictionary<string, BlueprintValue>
                {
                    ["other"] = BlueprintValue.FromObject(other.gameObject)
                });
        }

        public override void OnTriggerExit(Collider? other)
        {
            if (_document == null || other?.gameObject == null) return;
            BlueprintFlowRuntime.RunEventNodeWithOutputs(this, _document, "TriggerExit",
                new Dictionary<string, BlueprintValue>
                {
                    ["other"] = BlueprintValue.FromObject(other.gameObject)
                });
        }

        void ProcessPendingExec()
        {
            if (_pendingExec.Count == 0 || _document?.Graph == null || !IsActiveAndEnabled) return;
            var t = Time.time;
            for (int i = _pendingExec.Count - 1; i >= 0; i--)
            {
                var p = _pendingExec[i];
                if (p.fireTime <= t)
                {
                    _pendingExec.RemoveAt(i);
                    BlueprintFlowRuntime.RunExecChain(this, _document, _document.Graph, p.nodeId, p.logSteps, null);
                }
            }
        }

        /// <summary>Reload from disk (call after editing the .blueprint file).</summary>
        public void Reload()
        {
            _doOnceSpent.Clear();
            _gateOpen.Clear();
            _document = BlueprintFlowRuntime.TryLoadDocument(BlueprintAssetPath,
                LogSteps ? m => LogWarning(m) : null);
            SeedTypedVariables();
        }

        void SeedTypedVariables()
        {
            TypedVariables.Clear();
            if (_document?.Variables != null)
                TypedVariables.SeedFromDecls(_document.Variables);
            if (TypedVariableOverrides != null)
            {
                foreach (var kv in TypedVariableOverrides)
                {
                    var t = TypedVariables.GetTypeOr(kv.Key, BlueprintPinType.String);
                    if (BlueprintValue.TryParse(t, kv.Value, out var v))
                        TypedVariables.Set(kv.Key, v);
                }
            }
        }

        /// <summary>Resolved absolute path, or null.</summary>
        public string? ResolvedBlueprintPath => BlueprintFlowRuntime.ResolveBlueprintPath(BlueprintAssetPath);
    }
}
