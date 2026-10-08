#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Visual scripting graph (event graph or function body).</summary>
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

        public bool HasWire(string fromId, string toId, string fromPin = "ExecOut", string toPin = "ExecIn")
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
                && IsExecWire(w));

        public bool HasExecConnection(string fromId, string fromPin, string toId) =>
            Wires.Any(w =>
                string.Equals(w.FromNodeId, fromId, StringComparison.Ordinal)
                && string.Equals(w.ToNodeId, toId, StringComparison.Ordinal)
                && string.Equals(w.FromPin, fromPin, StringComparison.OrdinalIgnoreCase)
                && IsExecWire(w));

        public static bool IsExecWire(BlueprintWire w) =>
            w.Kind == BlueprintWireKind.Exec
            || (BlueprintFlowRuntime.IsExecOutPin(w.FromPin) && BlueprintFlowRuntime.IsExecInPin(w.ToPin));

        public BlueprintWire? FindIncomingDataWire(string toNodeId, string toPin) =>
            Wires.FirstOrDefault(w =>
                string.Equals(w.ToNodeId, toNodeId, StringComparison.Ordinal)
                && string.Equals(w.ToPin, toPin, StringComparison.OrdinalIgnoreCase)
                && w.Kind == BlueprintWireKind.Data);

        public BlueprintWire? FindOutgoingDataWire(string fromNodeId, string fromPin) =>
            Wires.FirstOrDefault(w =>
                string.Equals(w.FromNodeId, fromNodeId, StringComparison.Ordinal)
                && string.Equals(w.FromPin, fromPin, StringComparison.OrdinalIgnoreCase)
                && w.Kind == BlueprintWireKind.Data);
    }

    public sealed class BlueprintNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Kind { get; set; } = "Comment";
        public string Title { get; set; } = "Node";
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>Legacy v1 property bag (still used for Reflect metadata and migration).</summary>
        public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>v2 pin literal overrides keyed by pin id.</summary>
        public Dictionary<string, string> PinLiterals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Dynamic pins (Sequence ThenN, CustomEvent params, function IO).</summary>
        public List<BlueprintPinDef> DynamicPins { get; set; } = new();

        /// <summary>Variable name for GetVar / SetVar nodes.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VariableName { get; set; }

        /// <summary>Function name for CallFunction / FunctionEntry / FunctionReturn.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? FunctionName { get; set; }

        public string GetPinLiteral(string pinId, string fallback = "")
        {
            if (PinLiterals != null && PinLiterals.TryGetValue(pinId, out var lit) && lit != null)
                return lit;
            if (Properties != null && Properties.TryGetValue(pinId, out var prop) && prop != null)
                return prop;
            return fallback;
        }

        public void SetPinLiteral(string pinId, string value)
        {
            PinLiterals ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PinLiterals[pinId] = value;
        }
    }

    public sealed class BlueprintWire
    {
        public string FromNodeId { get; set; } = "";
        public string ToNodeId { get; set; } = "";
        public string FromPin { get; set; } = "ExecOut";
        public string ToPin { get; set; } = "ExecIn";
        public BlueprintWireKind Kind { get; set; } = BlueprintWireKind.Exec;
    }

    /// <summary>Execution strategy: describe topology for the editor summary.</summary>
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
                sb.AppendLine("Wires:");
            foreach (var w in g.Wires)
                sb.AppendLine($"  {w.FromNodeId}.{w.FromPin} → {w.ToNodeId}.{w.ToPin} ({w.Kind})");
            sb.AppendLine();
            sb.AppendLine("Behavior preview:");
            BlueprintScriptSummary.AppendFlowOverview(sb, g);
            return sb.ToString().TrimEnd();
        }
    }
}
