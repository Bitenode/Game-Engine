#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Spike data model for a future visual scripting graph.</summary>
    public sealed class BlueprintGraph
    {
        public List<BlueprintNode> Nodes { get; set; } = new();
        public List<BlueprintWire> Wires { get; set; } = new();

        public BlueprintNode AddNode(string kind, string title, double x = 0, double y = 0)
        {
            var n = new BlueprintNode { Kind = kind, Title = title, X = x, Y = y };
            Nodes.Add(n);
            return n;
        }

        /// <summary>Remove all wires attached to a node (call before deleting the node).</summary>
        public void RemoveWiresInvolving(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            Wires.RemoveAll(w => string.Equals(w.FromNodeId, nodeId, StringComparison.Ordinal)
                              || string.Equals(w.ToNodeId, nodeId, StringComparison.Ordinal));
        }

        public bool HasWire(string fromId, string toId, string fromPin = "Out", string toPin = "In")
        {
            return Wires.Any(w =>
                string.Equals(w.FromNodeId, fromId, StringComparison.Ordinal)
                && string.Equals(w.ToNodeId, toId, StringComparison.Ordinal)
                && string.Equals(w.FromPin, fromPin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(w.ToPin, toPin, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True if an exec wire already connects these two nodes (either pin naming).</summary>
        public bool HasExecConnection(string fromId, string toId) =>
            Wires.Any(w =>
                string.Equals(w.FromNodeId, fromId, StringComparison.Ordinal)
                && string.Equals(w.ToNodeId, toId, StringComparison.Ordinal)
                && BlueprintFlowRuntime.IsExecOutPin(w.FromPin)
                && BlueprintFlowRuntime.IsExecInPin(w.ToPin));

        public bool HasExecConnection(string fromId, string fromPin, string toId) =>
            Wires.Any(w =>
                string.Equals(w.FromNodeId, fromId, StringComparison.Ordinal)
                && string.Equals(w.ToNodeId, toId, StringComparison.Ordinal)
                && string.Equals(w.FromPin, fromPin, StringComparison.OrdinalIgnoreCase)
                && BlueprintFlowRuntime.IsExecOutPin(w.FromPin)
                && BlueprintFlowRuntime.IsExecInPin(w.ToPin));
    }

    public sealed class BlueprintNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Kind { get; set; } = "Comment";
        public string Title { get; set; } = "Node";
        public double X { get; set; }
        public double Y { get; set; }
        public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class BlueprintWire
    {
        public string FromNodeId { get; set; } = "";
        public string ToNodeId { get; set; } = "";
        public string FromPin { get; set; } = "Out";
        public string ToPin { get; set; } = "In";
    }

    /// <summary>
    /// Execution strategy (spike): describe topology only. Future: topo-sort + emit C# or small VM.
    /// </summary>
    public static class BlueprintGraphDescribe
    {
        public static string Summarize(BlueprintGraph g)
        {
            if (g.Nodes.Count == 0)
                return "Empty graph. Add Begin Play / Tick and actions, save as .blueprint, then add Visual Blueprint (Scripting) to a GameObject.";
            var sb = new StringBuilder();
            sb.AppendLine($"{g.Nodes.Count} node(s), {g.Wires.Count} wire(s).");
            foreach (var n in g.Nodes.OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  • [{n.Kind}] {n.Title} ({n.Id})");
            if (g.Wires.Count > 0)
                sb.AppendLine("Exec wires:");
            foreach (var w in g.Wires)
                sb.AppendLine($"  {w.FromNodeId}.{w.FromPin} → {w.ToNodeId}.{w.ToPin}");
            sb.AppendLine();
            sb.AppendLine("Behavior preview:");
            BlueprintScriptSummary.AppendFlowOverview(sb, g);
            return sb.ToString().TrimEnd();
        }
    }
}
