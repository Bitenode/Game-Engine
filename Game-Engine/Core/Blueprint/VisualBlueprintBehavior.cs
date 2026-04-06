#nullable enable
using System;
using System.Collections.Generic;

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
        [Persist] public string? BlueprintAssetPath { get; set; }

        [Persist] public bool LogSteps { get; set; } = true;

        /// <summary>If false, Tick event nodes are not run (Begin Play still runs).</summary>
        [Persist] public bool RunTickGraph { get; set; } = true;

        /// <summary>String key/value store readable by Branch (<c>conditionKey</c>) and set by <c>Set Variable</c>.</summary>
        [Persist] public Dictionary<string, string> Variables { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        BlueprintGraph? _graph;
        readonly List<(double fireTime, string nodeId, bool logSteps)> _pendingExec = new();

        internal void ScheduleExec(string nodeId, double fireTime, bool logSteps)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            _pendingExec.Add((fireTime, nodeId, logSteps));
        }

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
            BlueprintFlowRuntime.RunEventNodes(this, _graph, "BeginPlay");
        }

        public override void Update()
        {
            ProcessPendingExec();
            if (!RunTickGraph) return;
            BlueprintFlowRuntime.RunEventNodes(this, _graph, "Tick");
        }

        void ProcessPendingExec()
        {
            if (_pendingExec.Count == 0 || _graph == null || !IsActiveAndEnabled) return;
            var t = Time.time;
            for (int i = _pendingExec.Count - 1; i >= 0; i--)
            {
                var p = _pendingExec[i];
                if (p.fireTime <= t)
                {
                    _pendingExec.RemoveAt(i);
                    BlueprintFlowRuntime.RunExecChain(this, _graph, p.nodeId, p.logSteps);
                }
            }
        }

        /// <summary>Reload from disk (call after editing the .blueprint file).</summary>
        public void Reload()
        {
            _graph = BlueprintFlowRuntime.TryLoadBlueprintGraph(BlueprintAssetPath,
                LogSteps ? m => LogWarning(m) : null);
        }

        /// <summary>Resolved absolute path, or null.</summary>
        public string? ResolvedBlueprintPath => BlueprintFlowRuntime.ResolveBlueprintPath(BlueprintAssetPath);
    }
}
