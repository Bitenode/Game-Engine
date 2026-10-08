#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Core.Blueprint
{
    public sealed class BlueprintValidationIssue
    {
        public string Message { get; init; } = "";
        public string? NodeId { get; init; }
        public bool IsError { get; init; } = true;
    }

    /// <summary>Static checks for type mismatches, pure cycles, and unknown variables.</summary>
    public static class BlueprintValidation
    {
        public static List<BlueprintValidationIssue> Validate(BlueprintDocument doc)
        {
            var issues = new List<BlueprintValidationIssue>();
            ValidateGraph(doc, doc.Graph, "Event Graph", issues);
            foreach (var fn in doc.Functions)
                ValidateGraph(doc, fn.Graph, $"Function '{fn.Name}'", issues);
            return issues;
        }

        static void ValidateGraph(BlueprintDocument doc, BlueprintGraph graph, string scope, List<BlueprintValidationIssue> issues)
        {
            var nodeById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

            foreach (var w in graph.Wires)
            {
                if (!nodeById.TryGetValue(w.FromNodeId, out var from) || !nodeById.TryGetValue(w.ToNodeId, out var to))
                {
                    issues.Add(new BlueprintValidationIssue
                    {
                        Message = $"{scope}: wire references missing node.",
                        IsError = true
                    });
                    continue;
                }

                var fromPin = BlueprintNodeCatalog.FindPin(from, w.FromPin, doc);
                var toPin = BlueprintNodeCatalog.FindPin(to, w.ToPin, doc);
                if (fromPin == null || toPin == null)
                {
                    issues.Add(new BlueprintValidationIssue
                    {
                        Message = $"{scope}: unknown pin on wire {from.Title}.{w.FromPin} → {to.Title}.{w.ToPin}",
                        NodeId = from.Id,
                        IsError = true
                    });
                    continue;
                }

                if (fromPin.Direction != BlueprintPinDirection.Output || toPin.Direction != BlueprintPinDirection.Input)
                {
                    issues.Add(new BlueprintValidationIssue
                    {
                        Message = $"{scope}: pin direction wrong on {from.Title} → {to.Title}",
                        NodeId = from.Id,
                        IsError = true
                    });
                    continue;
                }

                var expectKind = fromPin.Type == BlueprintPinType.Exec ? BlueprintWireKind.Exec : BlueprintWireKind.Data;
                if (w.Kind != expectKind && !(expectKind == BlueprintWireKind.Exec && BlueprintGraph.IsExecWire(w)))
                {
                    issues.Add(new BlueprintValidationIssue
                    {
                        Message = $"{scope}: wire kind mismatch ({w.Kind}) for {fromPin.Type} pin",
                        NodeId = from.Id,
                        IsError = true
                    });
                }

                if (fromPin.Type == BlueprintPinType.Exec || toPin.Type == BlueprintPinType.Exec)
                {
                    if (fromPin.Type != BlueprintPinType.Exec || toPin.Type != BlueprintPinType.Exec)
                    {
                        issues.Add(new BlueprintValidationIssue
                        {
                            Message = $"{scope}: cannot mix exec and data on one wire ({from.Title} → {to.Title})",
                            NodeId = from.Id,
                            IsError = true
                        });
                    }
                    continue;
                }

                if (!BlueprintValue.AreTypesCompatible(fromPin.Type, toPin.Type))
                {
                    issues.Add(new BlueprintValidationIssue
                    {
                        Message = $"{scope}: type mismatch {fromPin.Type} → {toPin.Type} ({from.Title}.{fromPin.Id} → {to.Title}.{toPin.Id})",
                        NodeId = from.Id,
                        IsError = true
                    });
                }

                var toDef = BlueprintNodeCatalog.Resolve(to.Kind);
                if (toDef.IsPure && fromPin.Type == BlueprintPinType.Exec)
                {
                    issues.Add(new BlueprintValidationIssue
                    {
                        Message = $"{scope}: exec wired into pure node '{to.Title}'",
                        NodeId = to.Id,
                        IsError = true
                    });
                }
            }

            foreach (var n in graph.Nodes)
            {
                if (string.Equals(n.Kind, "GetVar", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.Kind, "SetVar", StringComparison.OrdinalIgnoreCase))
                {
                    var name = n.VariableName ?? "";
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        issues.Add(new BlueprintValidationIssue
                        {
                            Message = $"{scope}: {n.Kind} missing VariableName",
                            NodeId = n.Id,
                            IsError = true
                        });
                    }
                    else if (!doc.Variables.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add(new BlueprintValidationIssue
                        {
                            Message = $"{scope}: unknown variable '{name}' on {n.Kind}",
                            NodeId = n.Id,
                            IsError = true
                        });
                    }
                }
            }

            DetectPureCycles(doc, graph, scope, issues);
        }

        static void DetectPureCycles(BlueprintDocument doc, BlueprintGraph graph, string scope, List<BlueprintValidationIssue> issues)
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var done = new HashSet<string>(StringComparer.Ordinal);

            bool Dfs(string nodeId)
            {
                if (done.Contains(nodeId)) return false;
                if (!visiting.Add(nodeId)) return true;
                var node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
                if (node == null) { visiting.Remove(nodeId); return false; }
                var def = BlueprintNodeCatalog.Resolve(node.Kind);
                if (!def.IsPure && def.Category != BlueprintNodeCategory.Pure)
                {
                    visiting.Remove(nodeId);
                    done.Add(nodeId);
                    return false;
                }

                foreach (var pin in BlueprintNodeCatalog.ResolvePins(node, doc))
                {
                    if (pin.Direction != BlueprintPinDirection.Input || pin.Type == BlueprintPinType.Exec) continue;
                    var w = graph.FindIncomingDataWire(node.Id, pin.Id);
                    if (w == null) continue;
                    if (Dfs(w.FromNodeId))
                    {
                        issues.Add(new BlueprintValidationIssue
                        {
                            Message = $"{scope}: pure-node cycle involving '{node.Title}'",
                            NodeId = node.Id,
                            IsError = true
                        });
                        visiting.Remove(nodeId);
                        return true;
                    }
                }

                visiting.Remove(nodeId);
                done.Add(nodeId);
                return false;
            }

            foreach (var n in graph.Nodes)
            {
                var def = BlueprintNodeCatalog.Resolve(n.Kind);
                if (def.IsPure || def.Category == BlueprintNodeCategory.Pure)
                    Dfs(n.Id);
            }
        }
    }
}
