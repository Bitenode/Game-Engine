#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Events;
using CoreVector3 = Game_Engine.Core.Vector3;
using EngineInput = Game_Engine.Core.Input.Input;
using KeyCode = Game_Engine.Core.Input.KeyCode;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Loads graphs and walks exec + data wires for <see cref="VisualBlueprintBehavior"/>.</summary>
    public static class BlueprintFlowRuntime
    {
        public const string PinExecInLegacy = "In";
        public const string PinExecOutLegacy = "Out";
        public const string PinExecIn = "ExecIn";
        public const string PinExecOut = "ExecOut";
        public const string PinThen = "Then";
        public const string PinElse = "Else";
        public const int MaxForLoopIterations = 10_000;
        public const int MaxPureDepth = 64;

        public static bool IsExecOutPin(string? pin)
        {
            if (string.IsNullOrEmpty(pin)) return false;
            if (string.Equals(pin, PinExecOut, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinExecOutLegacy, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinThen, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinElse, StringComparison.OrdinalIgnoreCase))
                return true;
            // Sequence ThenN, ForLoop loopBody/completed, Gate exit
            if (pin.StartsWith("Then", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(pin, "loopBody", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(pin, "completed", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(pin, "exit", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsExecInPin(string? pin) =>
            !string.IsNullOrEmpty(pin)
            && (string.Equals(pin, PinExecIn, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinExecInLegacy, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, "enter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, "open", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, "close", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, "reset", StringComparison.OrdinalIgnoreCase));

        public static void NormalizeLegacyPins(BlueprintGraph graph)
        {
            foreach (var w in graph.Wires)
            {
                if (string.Equals(w.FromPin, PinExecOutLegacy, StringComparison.OrdinalIgnoreCase))
                    w.FromPin = PinExecOut;
                if (string.Equals(w.ToPin, PinExecInLegacy, StringComparison.OrdinalIgnoreCase))
                    w.ToPin = PinExecIn;
                if (w.Kind == BlueprintWireKind.Exec
                    || (IsExecOutPin(w.FromPin) && IsExecInPin(w.ToPin)))
                {
                    if (IsExecOutPin(w.FromPin) && IsExecInPin(w.ToPin))
                        w.Kind = BlueprintWireKind.Exec;
                }
            }
        }

        public static string? ResolveBlueprintPath(string? relativeOrAbs)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbs)) return null;
            var s = relativeOrAbs.Trim().Replace('\\', Path.DirectorySeparatorChar);
            try
            {
                if (Path.IsPathRooted(s))
                    return Path.GetFullPath(s);
                var proj = ProjectService.Current;
                if (proj != null)
                    return Path.GetFullPath(Path.Combine(proj.RootPath, s));
                return Path.GetFullPath(s);
            }
            catch
            {
                return null;
            }
        }

        public static BlueprintGraph? TryLoadBlueprintGraph(string? relativeOrAbs, Action<string>? logWarning)
        {
            var doc = TryLoadDocument(relativeOrAbs, logWarning);
            return doc?.Graph;
        }

        public static BlueprintDocument? TryLoadDocument(string? relativeOrAbs, Action<string>? logWarning)
        {
            var path = ResolveBlueprintPath(relativeOrAbs);
            if (path == null)
            {
                logWarning?.Invoke("Blueprint path is empty.");
                return null;
            }
            if (!File.Exists(path))
            {
                logWarning?.Invoke($"Blueprint file not found: {path}");
                return null;
            }
            try
            {
                return BlueprintPersistence.LoadDocument(path);
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"Failed to load blueprint: {ex.Message}");
                return null;
            }
        }

        sealed class ExecContext
        {
            public required VisualBlueprintBehavior Host;
            public required BlueprintDocument Doc;
            public required BlueprintGraph Graph;
            public bool LogSteps;
            public readonly Dictionary<(string nodeId, string pinId), BlueprintValue> ImpureCache = new();
            public readonly Dictionary<string, BlueprintValue> EventOutputs = new(StringComparer.OrdinalIgnoreCase);
            public int PureDepth;
            public Stack<(string fnName, Dictionary<string, BlueprintValue> args)>? FunctionStack;
        }

        public static void RunEventNodes(VisualBlueprintBehavior host, BlueprintDocument? doc, string eventKind)
        {
            if (doc?.Graph == null || !host.IsActiveAndEnabled) return;
            foreach (var n in doc.Graph.Nodes)
            {
                if (!string.Equals(n.Kind, eventKind, StringComparison.OrdinalIgnoreCase)) continue;
                var def = BlueprintNodeCatalog.Resolve(n.Kind);
                if (def.Category != BlueprintNodeCategory.Event) continue;
                RunExecChain(host, doc, doc.Graph, n.Id, host.LogSteps, null);
            }
        }

        /// <summary>Back-compat overload used by older call sites.</summary>
        public static void RunEventNodes(VisualBlueprintBehavior host, BlueprintGraph? graph, string eventKind)
        {
            if (graph == null || host.Document == null) return;
            // Prefer host document; if graph is the main event graph, use it.
            var doc = host.Document;
            if (!ReferenceEquals(graph, doc.Graph))
            {
                // Temporary wrapper for ad-hoc graphs
                var tmp = new BlueprintDocument { Graph = graph, Variables = doc.Variables, Functions = doc.Functions };
                foreach (var n in graph.Nodes)
                {
                    if (!string.Equals(n.Kind, eventKind, StringComparison.OrdinalIgnoreCase)) continue;
                    RunExecChain(host, tmp, graph, n.Id, host.LogSteps, null);
                }
                return;
            }
            RunEventNodes(host, doc, eventKind);
        }

        public static void RunEventNodeWithOutputs(
            VisualBlueprintBehavior host, BlueprintDocument doc, string eventKind,
            IReadOnlyDictionary<string, BlueprintValue>? outputs)
        {
            if (!host.IsActiveAndEnabled) return;
            foreach (var n in doc.Graph.Nodes)
            {
                if (!string.Equals(n.Kind, eventKind, StringComparison.OrdinalIgnoreCase)) continue;
                RunExecChain(host, doc, doc.Graph, n.Id, host.LogSteps, outputs);
            }
        }

        public static void RunCustomEvent(VisualBlueprintBehavior host, BlueprintDocument doc, string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName) || !host.IsActiveAndEnabled) return;
            foreach (var n in doc.Graph.Nodes)
            {
                if (!string.Equals(n.Kind, "CustomEvent", StringComparison.OrdinalIgnoreCase)) continue;
                var name = n.GetPinLiteral("eventName", n.Properties.TryGetValue("eventName", out var p) ? p : n.Title);
                if (!string.Equals(name.Trim(), eventName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                RunExecChain(host, doc, doc.Graph, n.Id, host.LogSteps, null);
            }
        }

        public static void PollInputEvents(VisualBlueprintBehavior host, BlueprintDocument doc)
        {
            if (!host.IsActiveAndEnabled) return;
            foreach (var n in doc.Graph.Nodes)
            {
                if (string.Equals(n.Kind, "InputKey", StringComparison.OrdinalIgnoreCase))
                {
                    var keyName = n.GetPinLiteral("keyCode", "Space");
                    if (TryParseKeyCode(keyName, out var key) && EngineInput.GetKeyDown(key))
                        RunExecChain(host, doc, doc.Graph, n.Id, host.LogSteps, null);
                }
                else if (string.Equals(n.Kind, "InputAction", StringComparison.OrdinalIgnoreCase))
                {
                    var action = n.GetPinLiteral("actionName", "Jump");
                    if (EngineInput.GetActionDown(action))
                        RunExecChain(host, doc, doc.Graph, n.Id, host.LogSteps, null);
                }
            }
        }

        static bool TryParseKeyCode(string name, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrWhiteSpace(name)) return false;
            return Enum.TryParse(name.Trim(), ignoreCase: true, out key);
        }

        public static void RunExecChain(
            VisualBlueprintBehavior host, BlueprintDocument doc, BlueprintGraph graph,
            string startNodeId, bool logSteps,
            IReadOnlyDictionary<string, BlueprintValue>? eventOutputs)
        {
            var ctx = new ExecContext
            {
                Host = host,
                Doc = doc,
                Graph = graph,
                LogSteps = logSteps,
            };
            if (eventOutputs != null)
            {
                foreach (var kv in eventOutputs)
                    ctx.EventOutputs[kv.Key] = kv.Value;
            }
            RunExecChainCore(ctx, startNodeId);
        }

        public static void RunExecChain(VisualBlueprintBehavior host, BlueprintGraph graph, string startNodeId, bool logSteps)
        {
            var doc = host.Document ?? new BlueprintDocument { Graph = graph };
            RunExecChain(host, doc, graph, startNodeId, logSteps, null);
        }

        static void RunExecChainCore(ExecContext ctx, string startNodeId, string? arrivalPin = null)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? cur = startNodeId;
            string? enterPin = arrivalPin;

            while (cur != null)
            {
                if (!visited.Add(cur))
                {
                    if (ctx.LogSteps)
                        Log.Warning($"{BpTag(ctx.Host)}execution cycle at node {cur}");
                    break;
                }

                var node = ctx.Graph.Nodes.FirstOrDefault(n => n.Id == cur);
                if (node == null) break;

                // Gate / DoOnce secondary exec entries (Open / Close / Reset)
                if (enterPin != null
                    && (string.Equals(enterPin, "open", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(enterPin, "close", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(enterPin, "reset", StringComparison.OrdinalIgnoreCase)))
                {
                    HandleSecondaryExec(ctx, node, enterPin);
                    break;
                }
                enterPin = null;

                if (string.Equals(node.Kind, "Delay", StringComparison.OrdinalIgnoreCase))
                {
                    var sec = ReadFloat(ctx, node, "seconds", 1f);
                    sec = Math.Max(0f, sec);
                    var delayWire = FindNextExecWire(ctx.Graph, cur, null);
                    if (delayWire != null)
                    {
                        ctx.Host.ScheduleExec(delayWire.ToNodeId, Time.time + sec, ctx.LogSteps);
                        if (ctx.LogSteps)
                            Log.Info($"{BpTag(ctx.Host)}Delay {sec:0.###}s → next");
                    }
                    break;
                }

                if (string.Equals(node.Kind, "Sequence", StringComparison.OrdinalIgnoreCase))
                {
                    var outs = BlueprintNodeCatalog.OutboundExecPinNames(node, ctx.Doc);
                    foreach (var pin in outs)
                    {
                        var w = FindNextExecWire(ctx.Graph, cur, pin);
                        if (w != null)
                            RunExecChainCore(ctx, w.ToNodeId);
                    }
                    break;
                }

                if (string.Equals(node.Kind, "ForLoop", StringComparison.OrdinalIgnoreCase))
                {
                    var first = (int)ReadFloat(ctx, node, "first", 0);
                    var last = (int)ReadFloat(ctx, node, "last", 0);
                    var body = FindNextExecWire(ctx.Graph, cur, "loopBody");
                    var done = FindNextExecWire(ctx.Graph, cur, "completed");
                    int count = 0;
                    if (first <= last)
                    {
                        for (int i = first; i <= last && count < MaxForLoopIterations; i++, count++)
                        {
                            ctx.ImpureCache[(node.Id, "index")] = BlueprintValue.FromInt(i);
                            if (body != null)
                                RunExecChainCore(ctx, body.ToNodeId);
                        }
                    }
                    else
                    {
                        for (int i = first; i >= last && count < MaxForLoopIterations; i--, count++)
                        {
                            ctx.ImpureCache[(node.Id, "index")] = BlueprintValue.FromInt(i);
                            if (body != null)
                                RunExecChainCore(ctx, body.ToNodeId);
                        }
                    }
                    if (done != null)
                        RunExecChainCore(ctx, done.ToNodeId);
                    break;
                }

                if (string.Equals(node.Kind, "DoOnce", StringComparison.OrdinalIgnoreCase))
                {
                    if (ctx.Host.IsDoOnceSpent(node.Id))
                        break;
                    ctx.Host.MarkDoOnceSpent(node.Id);
                    var next = FindNextExecWire(ctx.Graph, cur, null);
                    cur = next?.ToNodeId;
                    continue;
                }

                if (string.Equals(node.Kind, "Gate", StringComparison.OrdinalIgnoreCase))
                {
                    // Enter path only when open
                    if (!ctx.Host.IsGateOpen(node.Id, ReadBool(ctx, node, "startClosed", false)))
                        break;
                    var exit = FindNextExecWire(ctx.Graph, cur, "exit")
                               ?? FindNextExecWire(ctx.Graph, cur, PinExecOut);
                    cur = exit?.ToNodeId;
                    continue;
                }

                string? pinPick = null;
                if (string.Equals(node.Kind, "Branch", StringComparison.OrdinalIgnoreCase))
                    pinPick = ReadBool(ctx, node, "condition", true) ? PinThen : PinElse;
                else if (string.Equals(node.Kind, "BranchEquals", StringComparison.OrdinalIgnoreCase))
                {
                    var a = ReadString(ctx, node, "a", "");
                    var b = ReadString(ctx, node, "b", "");
                    // v1 fallback: conditionKey / equalsValue via string map
                    if (UsesLegacyVarBranch(node))
                        pinPick = EvaluateBranchEqualsLegacy(ctx.Host, node) ? PinThen : PinElse;
                    else
                        pinPick = string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) ? PinThen : PinElse;
                }
                else if (string.Equals(node.Kind, "BranchCompare", StringComparison.OrdinalIgnoreCase))
                {
                    if (UsesLegacyVarBranch(node) && node.Properties.ContainsKey("conditionKey"))
                        pinPick = EvaluateBranchCompareLegacy(ctx.Host, node) ? PinThen : PinElse;
                    else
                    {
                        var a = ReadFloat(ctx, node, "a", 0);
                        var b = ReadFloat(ctx, node, "b", 0);
                        var op = ReadString(ctx, node, "op", "eq").Trim().ToLowerInvariant();
                        pinPick = Compare(a, b, op) ? PinThen : PinElse;
                    }
                }
                else if (string.Equals(node.Kind, "RandomBranch", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Math.Clamp(ReadFloat(ctx, node, "chance", 0.5f), 0f, 1f);
                    pinPick = Random.Shared.NextDouble() < p ? PinThen : PinElse;
                }
                else if (string.Equals(node.Kind, "Branch", StringComparison.OrdinalIgnoreCase) == false
                         && UsesLegacyBranchCondition(node))
                {
                    pinPick = EvaluateBranchConditionLegacy(ctx.Host, node) ? PinThen : PinElse;
                }

                // v1 Branch with only conditionKey and no data wire
                if (string.Equals(node.Kind, "Branch", StringComparison.OrdinalIgnoreCase)
                    && ctx.Graph.FindIncomingDataWire(node.Id, "condition") == null
                    && node.Properties.ContainsKey("conditionKey")
                    && !node.PinLiterals.ContainsKey("condition"))
                {
                    pinPick = EvaluateBranchConditionLegacy(ctx.Host, node) ? PinThen : PinElse;
                }

                ExecuteNode(ctx, node, pinPick);

                if (string.Equals(node.Kind, "FunctionReturn", StringComparison.OrdinalIgnoreCase))
                    break;

                var nextWire = FindNextExecWire(ctx.Graph, cur, pinPick);
                enterPin = nextWire?.ToPin;
                cur = nextWire?.ToNodeId;
            }
        }

        static void HandleSecondaryExec(ExecContext ctx, BlueprintNode node, string pin)
        {
            if (string.Equals(node.Kind, "DoOnce", StringComparison.OrdinalIgnoreCase)
                && string.Equals(pin, "reset", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Host.ResetDoOnce(node.Id);
                return;
            }
            if (string.Equals(node.Kind, "Gate", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(pin, "open", StringComparison.OrdinalIgnoreCase))
                    ctx.Host.SetGateOpen(node.Id, true);
                else if (string.Equals(pin, "close", StringComparison.OrdinalIgnoreCase))
                    ctx.Host.SetGateOpen(node.Id, false);
            }
        }

        static bool UsesLegacyVarBranch(BlueprintNode node) =>
            node.Properties.ContainsKey("conditionKey") && node.PinLiterals.Count == 0
            && node.Properties.ContainsKey("conditionKey");

        static bool UsesLegacyBranchCondition(BlueprintNode node) =>
            string.Equals(node.Kind, "Branch", StringComparison.OrdinalIgnoreCase)
            && node.Properties.ContainsKey("conditionKey");

        static bool Compare(double lhs, double rhs, string op)
        {
            const double eps = 1e-9;
            return op switch
            {
                "lt" or "<" => lhs < rhs,
                "lte" or "<=" or "le" => lhs <= rhs,
                "gt" or ">" => lhs > rhs,
                "gte" or ">=" or "ge" => lhs >= rhs,
                "eq" or "=" or "==" => Math.Abs(lhs - rhs) < eps,
                _ => Math.Abs(lhs - rhs) < eps,
            };
        }

        internal static BlueprintWire? FindNextExecWire(BlueprintGraph graph, string fromNodeId, string? fromPin)
        {
            BlueprintWire? any = null;
            foreach (var w in graph.Wires)
            {
                if (!string.Equals(w.FromNodeId, fromNodeId, StringComparison.Ordinal)) continue;
                if (w.Kind == BlueprintWireKind.Data) continue;
                if (!IsExecOutPin(w.FromPin) && w.Kind != BlueprintWireKind.Exec) continue;
                if (!IsExecInPin(w.ToPin) && w.Kind == BlueprintWireKind.Exec
                    && !string.Equals(w.ToPin, PinExecIn, StringComparison.OrdinalIgnoreCase)
                    && !IsExecInPin(w.ToPin))
                {
                    // allow any exec-in named pin
                }
                if (w.Kind != BlueprintWireKind.Exec && !IsExecOutPin(w.FromPin)) continue;

                if (any == null) any = w;
                if (fromPin != null && string.Equals(w.FromPin, fromPin, StringComparison.OrdinalIgnoreCase))
                    return w;
            }
            return fromPin == null ? any : null;
        }

        #region Pin evaluation

        static BlueprintValue ReadPin(ExecContext ctx, BlueprintNode node, string pinId, BlueprintPinType expected)
        {
            var wire = ctx.Graph.FindIncomingDataWire(node.Id, pinId);
            if (wire != null)
            {
                var src = ctx.Graph.Nodes.FirstOrDefault(n => n.Id == wire.FromNodeId);
                if (src != null)
                {
                    var v = EvaluateOutputPin(ctx, src, wire.FromPin);
                    return v.CoerceTo(expected);
                }
            }

            var pinDef = BlueprintNodeCatalog.FindPin(node, pinId, ctx.Doc);
            var lit = node.GetPinLiteral(pinId, pinDef?.DefaultLiteral ?? "");
            if (BlueprintValue.TryParse(expected, lit, out var parsed))
            {
                if (expected == BlueprintPinType.Object && parsed.ObjectValue == null
                    && (string.IsNullOrEmpty(lit) || string.Equals(lit, "self", StringComparison.OrdinalIgnoreCase)))
                    return BlueprintValue.FromObject(ctx.Host.gameObject);
                return parsed;
            }

            // v1: fall back to Properties bag / string Variables
            if (node.Properties.TryGetValue(pinId, out var prop))
            {
                if (BlueprintValue.TryParse(expected, prop, out var p2)) return p2.CoerceTo(expected);
            }
            return BlueprintValue.DefaultFor(expected);
        }

        static BlueprintValue EvaluateOutputPin(ExecContext ctx, BlueprintNode node, string pinId)
        {
            if (ctx.EventOutputs.TryGetValue(pinId, out var ev))
                return ev;
            if (ctx.ImpureCache.TryGetValue((node.Id, pinId), out var cached))
                return cached;

            var def = BlueprintNodeCatalog.Resolve(node.Kind);
            if (def.IsPure || def.Category == BlueprintNodeCategory.Pure)
                return EvaluatePureNode(ctx, node, pinId);

            // Impure outputs only available after execution
            return BlueprintValue.DefaultFor(
                BlueprintNodeCatalog.FindPin(node, pinId, ctx.Doc)?.Type ?? BlueprintPinType.Float);
        }

        static BlueprintValue EvaluatePureNode(ExecContext ctx, BlueprintNode node, string requestedPin)
        {
            if (ctx.PureDepth++ > MaxPureDepth)
            {
                ctx.PureDepth--;
                if (ctx.LogSteps)
                    Log.Warning($"{BpTag(ctx.Host)}pure eval depth exceeded at {node.Kind}");
                return BlueprintValue.DefaultFor(BlueprintPinType.Float);
            }

            try
            {
                switch (node.Kind)
                {
                    case "GetVar":
                    {
                        var name = node.VariableName ?? "";
                        if (ctx.Host.TypedVariables.TryGet(name, out var v)) return v;
                        return BlueprintValue.DefaultFor(ctx.Host.TypedVariables.GetTypeOr(name, BlueprintPinType.Float));
                    }
                    case "GetSelf":
                        return BlueprintValue.FromObject(ctx.Host.gameObject);
                    case "GetLocation":
                    {
                        var go = ReadPin(ctx, node, "target", BlueprintPinType.Object).AsObject(ctx.Host.gameObject);
                        var p = go?.Transform.Position ?? new CoreVector3(0, 0, 0);
                        return BlueprintValue.FromVector(new CoreVector3(p.X, p.Y, p.Z));
                    }
                    case "GetRotation":
                    {
                        var go = ReadPin(ctx, node, "target", BlueprintPinType.Object).AsObject(ctx.Host.gameObject);
                        var r = go?.Transform.Rotation ?? new CoreVector3(0, 0, 0);
                        return BlueprintValue.FromVector(new CoreVector3(r.X, r.Y, r.Z));
                    }
                    case "AddFloat":
                        return BlueprintValue.FromFloat(ReadFloat(ctx, node, "a", 0) + ReadFloat(ctx, node, "b", 0));
                    case "SubtractFloat":
                        return BlueprintValue.FromFloat(ReadFloat(ctx, node, "a", 0) - ReadFloat(ctx, node, "b", 0));
                    case "MultiplyFloat":
                        return BlueprintValue.FromFloat(ReadFloat(ctx, node, "a", 0) * ReadFloat(ctx, node, "b", 1));
                    case "DivideFloat":
                    {
                        var b = ReadFloat(ctx, node, "b", 1);
                        return BlueprintValue.FromFloat(Math.Abs(b) < 1e-12 ? 0 : ReadFloat(ctx, node, "a", 0) / b);
                    }
                    case "LessFloat":
                        return BlueprintValue.FromBool(ReadFloat(ctx, node, "a", 0) < ReadFloat(ctx, node, "b", 0));
                    case "LessEqualFloat":
                        return BlueprintValue.FromBool(ReadFloat(ctx, node, "a", 0) <= ReadFloat(ctx, node, "b", 0));
                    case "GreaterFloat":
                        return BlueprintValue.FromBool(ReadFloat(ctx, node, "a", 0) > ReadFloat(ctx, node, "b", 0));
                    case "GreaterEqualFloat":
                        return BlueprintValue.FromBool(ReadFloat(ctx, node, "a", 0) >= ReadFloat(ctx, node, "b", 0));
                    case "EqualFloat":
                        return BlueprintValue.FromBool(Math.Abs(ReadFloat(ctx, node, "a", 0) - ReadFloat(ctx, node, "b", 0)) < 1e-9);
                    case "AddVector":
                    {
                        var a = ReadPin(ctx, node, "a", BlueprintPinType.Vector).AsVector();
                        var b = ReadPin(ctx, node, "b", BlueprintPinType.Vector).AsVector();
                        return BlueprintValue.FromVector(new CoreVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z));
                    }
                    case "ScaleVector":
                    {
                        var v = ReadPin(ctx, node, "v", BlueprintPinType.Vector).AsVector();
                        var s = ReadFloat(ctx, node, "scale", 1);
                        return BlueprintValue.FromVector(new CoreVector3(v.X * s, v.Y * s, v.Z * s));
                    }
                    case "AppendString":
                        return BlueprintValue.FromString(ReadString(ctx, node, "a", "") + ReadString(ctx, node, "b", ""));
                    case "MakeVector":
                        return BlueprintValue.FromVector(new CoreVector3(
                            ReadFloat(ctx, node, "x", 0),
                            ReadFloat(ctx, node, "y", 0),
                            ReadFloat(ctx, node, "z", 0)));
                    case "BreakVector":
                    {
                        var v = ReadPin(ctx, node, "v", BlueprintPinType.Vector).AsVector();
                        if (string.Equals(requestedPin, "y", StringComparison.OrdinalIgnoreCase))
                            return BlueprintValue.FromFloat(v.Y);
                        if (string.Equals(requestedPin, "z", StringComparison.OrdinalIgnoreCase))
                            return BlueprintValue.FromFloat(v.Z);
                        return BlueprintValue.FromFloat(v.X);
                    }
                    case "ReflectGet":
                    {
                        var mpath = node.Properties.TryGetValue("memberPath", out var mp) ? mp.Trim() : "";
                        if (mpath.Length == 0) return BlueprintValue.FromString("");
                        object? val = null;
                        string? err = null;
                        if (BlueprintReflection.IsStaticMode(node))
                        {
                            var tn = node.Properties.TryGetValue("typeName", out var tnm) ? tnm.Trim() : "";
                            if (tn.Length > 0)
                                BlueprintReflection.TryReadStaticPath(tn, mpath, out val, out err);
                        }
                        else
                        {
                            var go = BlueprintReflection.ResolveScopeGameObject(ctx.Host, node);
                            var ctp = node.Properties.TryGetValue("componentType", out var ct) ? ct.Trim() : "";
                            var root = BlueprintReflection.ResolveMemberRoot(go, ctp);
                            if (root != null)
                                BlueprintReflection.TryReadPath(root, mpath, out val, out err);
                        }
                        if (err != null && ctx.LogSteps)
                            Log.Warning($"{BpTag(ctx.Host)}ReflectGet {err}");
                        return BlueprintValue.FromString(BlueprintReflection.FormatValue(val));
                    }
                    case "Tick":
                        if (string.Equals(requestedPin, "deltaSeconds", StringComparison.OrdinalIgnoreCase))
                            return BlueprintValue.FromFloat(Time.deltaTime);
                        break;
                }

                // Cache multi-output pures
                if (string.Equals(node.Kind, "BreakVector", StringComparison.OrdinalIgnoreCase))
                {
                    var v = ReadPin(ctx, node, "v", BlueprintPinType.Vector).AsVector();
                    ctx.ImpureCache[(node.Id, "x")] = BlueprintValue.FromFloat(v.X);
                    ctx.ImpureCache[(node.Id, "y")] = BlueprintValue.FromFloat(v.Y);
                    ctx.ImpureCache[(node.Id, "z")] = BlueprintValue.FromFloat(v.Z);
                    return EvaluateOutputPin(ctx, node, requestedPin);
                }

                return BlueprintValue.DefaultFor(
                    BlueprintNodeCatalog.FindPin(node, requestedPin, ctx.Doc)?.Type ?? BlueprintPinType.Float);
            }
            finally
            {
                ctx.PureDepth--;
            }
        }

        static float ReadFloat(ExecContext ctx, BlueprintNode node, string pin, float fallback)
        {
            var v = ReadPin(ctx, node, pin, BlueprintPinType.Float);
            return (float)v.AsFloat();
        }

        static bool ReadBool(ExecContext ctx, BlueprintNode node, string pin, bool fallback)
        {
            var wire = ctx.Graph.FindIncomingDataWire(node.Id, pin);
            if (wire == null && !node.PinLiterals.ContainsKey(pin) && !node.Properties.ContainsKey(pin))
                return fallback;
            return ReadPin(ctx, node, pin, BlueprintPinType.Bool).AsBool();
        }

        static string ReadString(ExecContext ctx, BlueprintNode node, string pin, string fallback)
        {
            var wire = ctx.Graph.FindIncomingDataWire(node.Id, pin);
            if (wire == null && !node.PinLiterals.ContainsKey(pin) && !node.Properties.ContainsKey(pin))
                return fallback;
            return ReadPin(ctx, node, pin, BlueprintPinType.String).AsString();
        }

        #endregion

        #region Legacy branch helpers

        static bool EvaluateBranchConditionLegacy(VisualBlueprintBehavior host, BlueprintNode node)
        {
            var key = node.Properties.TryGetValue("conditionKey", out var k) ? k.Trim() : "";
            if (string.IsNullOrEmpty(key)) return true;
            if (!host.Variables.TryGetValue(key, out var val) || val == null) return false;
            val = val.Trim();
            if (bool.TryParse(val, out var b)) return b;
            if (string.Equals(val, "1", StringComparison.Ordinal)) return true;
            if (string.Equals(val, "0", StringComparison.Ordinal)) return false;
            return val.Length > 0;
        }

        static bool EvaluateBranchEqualsLegacy(VisualBlueprintBehavior host, BlueprintNode node)
        {
            var key = node.Properties.TryGetValue("conditionKey", out var k) ? k.Trim() : "";
            var expect = node.Properties.TryGetValue("equalsValue", out var ev) ? ev.Trim() : "";
            if (string.IsNullOrEmpty(key))
                return string.IsNullOrEmpty(expect);
            if (!host.Variables.TryGetValue(key, out var val) || val == null)
                val = "";
            return string.Equals(val.Trim(), expect, StringComparison.OrdinalIgnoreCase);
        }

        static bool EvaluateBranchCompareLegacy(VisualBlueprintBehavior host, BlueprintNode node)
        {
            var key = node.Properties.TryGetValue("conditionKey", out var k) ? k.Trim() : "";
            var rhsRaw = node.Properties.TryGetValue("compareValue", out var cv) ? cv : "0";
            if (!double.TryParse(rhsRaw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rhs))
                rhs = 0;
            var lhs = 0.0;
            if (!string.IsNullOrEmpty(key) && host.Variables.TryGetValue(key, out var vs) && vs != null
                && double.TryParse(vs.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lv))
                lhs = lv;
            var op = node.Properties.TryGetValue("compareOp", out var o) ? o.Trim().ToLowerInvariant() : "eq";
            return Compare(lhs, rhs, op);
        }

        #endregion

        internal static GameObject? ResolveTargetObject(BlueprintNode node)
        {
            var path = node.Properties.TryGetValue("targetPath", out var tp) ? tp.Trim() : "";
            var name = node.Properties.TryGetValue("targetName", out var tn) ? tn.Trim() : "";
            if (path.Length == 0 && node.PinLiterals.TryGetValue("target", out var tLit))
            {
                path = tLit;
                name = tLit;
            }
            if (path.Length > 0)
            {
                var byPath = SceneQuery.FindByPath(path);
                if (byPath != null) return byPath;
            }
            if (name.Length > 0)
                return SceneQuery.FindByName(name);
            return null;
        }

        /// <summary>Removes an object from the scene after calling behavior teardown (OnDestroy) on the hierarchy.</summary>
        public static void DestroyGameObjectTree(GameObject? go, bool publishDestroyedEvent, bool logSteps, Behavior logHost)
        {
            if (go == null) return;
            var nm = go.Name;
            if (publishDestroyedEvent)
            {
                try { EventBus.Publish(new ObjectDestroyedEvent { Object = go }); } catch { /* ignore subscriber errors */ }
            }
            foreach (var child in go.Children.ToList())
                DestroyGameObjectTree(child, publishDestroyedEvent: false, logSteps, logHost);
            foreach (var b in go.Behaviors.ToList())
            {
                try { b.__OnDestroy(); }
                catch { /* ignore */ }
            }
            if (go.Parent == null)
                SceneService.Remove(go);
            else
                go.RemoveFromParent();
            SceneService.NotifyChanged();
            if (logSteps && publishDestroyedEvent)
                Log.Info($"{BpTag(logHost)}DestroyObject '{nm}'");
        }

        static void ApplyTransformPosition(GameObject? go, CoreVector3 value, bool relative, bool logSteps, Behavior host, string logLabel)
        {
            if (go == null) return;
            var t = go.Transform;
            if (relative)
            {
                t.Position.X += value.X;
                t.Position.Y += value.Y;
                t.Position.Z += value.Z;
            }
            else
            {
                t.Position.X = value.X;
                t.Position.Y = value.Y;
                t.Position.Z = value.Z;
            }
            if (logSteps)
                Log.Info($"{BpTag(host)}{logLabel} {go.Name} → ({t.Position.X:0.###}, {t.Position.Y:0.###}, {t.Position.Z:0.###}){(relative ? " Δ" : "")}");
        }

        static void ApplyTransformRotation(GameObject? go, CoreVector3 value, bool relative, bool logSteps, Behavior host, string logLabel)
        {
            if (go == null) return;
            var t = go.Transform;
            if (relative)
            {
                t.Rotation.X += value.X;
                t.Rotation.Y += value.Y;
                t.Rotation.Z += value.Z;
            }
            else
            {
                t.Rotation.X = value.X;
                t.Rotation.Y = value.Y;
                t.Rotation.Z = value.Z;
            }
            if (logSteps)
                Log.Info($"{BpTag(host)}{logLabel} {go.Name} rot → ({t.Rotation.X:0.###}, {t.Rotation.Y:0.###}, {t.Rotation.Z:0.###})°{(relative ? " Δ" : "")}");
        }

        static void ExecuteNode(ExecContext ctx, BlueprintNode node, string? branchPinPick)
        {
            var host = ctx.Host;
            var logSteps = ctx.LogSteps;
            var cat = BlueprintNodeCatalog.Resolve(node.Kind).Category;
            if (cat is BlueprintNodeCategory.Event or BlueprintNodeCategory.Comment)
            {
                if (string.Equals(node.Kind, "Tick", StringComparison.OrdinalIgnoreCase))
                    ctx.ImpureCache[(node.Id, "deltaSeconds")] = BlueprintValue.FromFloat(Time.deltaTime);
                return;
            }

            if (string.Equals(node.Kind, "Branch", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Kind, "BranchEquals", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Kind, "BranchCompare", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Kind, "RandomBranch", StringComparison.OrdinalIgnoreCase))
            {
                if (logSteps)
                    Log.Debug($"{BpTag(host)}{node.Kind} → {(string.Equals(branchPinPick, PinThen, StringComparison.OrdinalIgnoreCase) ? "Then" : "Else")}");
                return;
            }

            switch (node.Kind)
            {
                case "LogMessage":
                case "Call":
                case "Math":
                {
                    var msg = ReadString(ctx, node, "message", node.Title);
                    if (node.Properties.TryGetValue("message", out var m) && ctx.Graph.FindIncomingDataWire(node.Id, "message") == null
                        && !node.PinLiterals.ContainsKey("message"))
                        msg = m;
                    Log.Info($"{BpTag(host)}{msg}");
                    break;
                }
                case "SetVar":
                {
                    var name = node.VariableName ?? "";
                    if (name.Length == 0) break;
                    var t = host.TypedVariables.GetTypeOr(name, BlueprintPinType.Float);
                    var val = ReadPin(ctx, node, "value", t);
                    host.TypedVariables.Set(name, val);
                    if (logSteps)
                        Log.Info($"{BpTag(host)}SetVar {name} = {val.ToLiteral()}");
                    break;
                }
                case "SetObjectActive":
                {
                    var go = ReadPin(ctx, node, "target", BlueprintPinType.Object).AsObject(host.gameObject);
                    var en = ReadBool(ctx, node, "active", true);
                    if (go != null) go.Enabled = en;
                    if (logSteps)
                        Log.Info($"{BpTag(host)}SetObjectActive {go?.Name} = {en}");
                    break;
                }
                case "SetOtherObjectActive":
                {
                    var target = ResolveTargetObject(node)
                                 ?? SceneQuery.FindByName(ReadString(ctx, node, "target", ""));
                    var en = ReadBool(ctx, node, "active", true);
                    if (target != null)
                    {
                        target.Enabled = en;
                        if (logSteps)
                            Log.Info($"{BpTag(host)}SetOtherObjectActive {target.Name} = {en}");
                    }
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}SetOtherObjectActive: no target");
                    break;
                }
                case "SetObjectPosition":
                {
                    var go = ReadPin(ctx, node, "target", BlueprintPinType.Object).AsObject(host.gameObject);
                    var val = ReadPin(ctx, node, "value", BlueprintPinType.Vector).AsVector();
                    var rel = ReadBool(ctx, node, "relative", false);
                    ApplyTransformPosition(go, val, rel, logSteps, host, "SetObjectPosition");
                    break;
                }
                case "SetOtherObjectPosition":
                {
                    var target = ResolveTargetObject(node)
                                 ?? SceneQuery.FindByName(ReadString(ctx, node, "target", ""));
                    var val = ReadPin(ctx, node, "value", BlueprintPinType.Vector).AsVector();
                    var rel = ReadBool(ctx, node, "relative", false);
                    if (target != null)
                        ApplyTransformPosition(target, val, rel, logSteps, host, "SetOtherObjectPosition");
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}SetOtherObjectPosition: no target");
                    break;
                }
                case "SetObjectRotation":
                {
                    var go = ReadPin(ctx, node, "target", BlueprintPinType.Object).AsObject(host.gameObject);
                    var val = ReadPin(ctx, node, "value", BlueprintPinType.Vector).AsVector();
                    var rel = ReadBool(ctx, node, "relative", false);
                    ApplyTransformRotation(go, val, rel, logSteps, host, "SetObjectRotation");
                    break;
                }
                case "SetOtherObjectRotation":
                {
                    var target = ResolveTargetObject(node)
                                 ?? SceneQuery.FindByName(ReadString(ctx, node, "target", ""));
                    var val = ReadPin(ctx, node, "value", BlueprintPinType.Vector).AsVector();
                    var rel = ReadBool(ctx, node, "relative", false);
                    if (target != null)
                        ApplyTransformRotation(target, val, rel, logSteps, host, "SetOtherObjectRotation");
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}SetOtherObjectRotation: no target");
                    break;
                }
                case "DestroyObject":
                {
                    GameObject? victim;
                    if (node.Properties.TryGetValue("scope", out var sc)
                        && string.Equals(sc, "Other", StringComparison.OrdinalIgnoreCase)
                        && ctx.Graph.FindIncomingDataWire(node.Id, "target") == null)
                        victim = ResolveTargetObject(node);
                    else
                        victim = ReadPin(ctx, node, "target", BlueprintPinType.Object).AsObject(host.gameObject);
                    if (victim != null)
                        DestroyGameObjectTree(victim, publishDestroyedEvent: true, logSteps, host);
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}DestroyObject: no target");
                    break;
                }
                case "FireBlueprintEvent":
                {
                    var ev = ReadString(ctx, node, "eventName", "");
                    if (ev.Length == 0 && node.Properties.TryGetValue("eventName", out var en)) ev = en.Trim();
                    if (ev.Length == 0) break;
                    var payload = ReadString(ctx, node, "payload", "");
                    EventBus.Publish(new BlueprintMessageEvent
                    {
                        Name = ev,
                        Data = payload,
                        Sender = host.gameObject
                    });
                    if (logSteps)
                        Log.Info($"{BpTag(host)}Event '{ev}'");
                    break;
                }
                case "CallCustomEvent":
                {
                    var name = ReadString(ctx, node, "eventName", "");
                    if (name.Length > 0)
                        RunCustomEvent(host, ctx.Doc, name);
                    break;
                }
                case "CallFunction":
                {
                    var fnName = node.FunctionName ?? "";
                    if (fnName.Length == 0) break;
                    var fn = ctx.Doc.Functions.FirstOrDefault(f =>
                        string.Equals(f.Name, fnName, StringComparison.OrdinalIgnoreCase));
                    if (fn == null)
                    {
                        if (logSteps)
                            Log.Warning($"{BpTag(host)}CallFunction: unknown '{fnName}'");
                        break;
                    }
                    var args = new Dictionary<string, BlueprintValue>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in fn.Inputs)
                        args[p.Id] = ReadPin(ctx, node, p.Id, p.Type);
                    var entry = fn.Graph.Nodes.FirstOrDefault(n =>
                        string.Equals(n.Kind, "FunctionEntry", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n.Kind, "BeginPlay", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        if (logSteps)
                            Log.Warning($"{BpTag(host)}CallFunction '{fnName}': no FunctionEntry");
                        break;
                    }
                    var nested = new ExecContext
                    {
                        Host = host,
                        Doc = ctx.Doc,
                        Graph = fn.Graph,
                        LogSteps = logSteps,
                        FunctionStack = ctx.FunctionStack ?? new Stack<(string, Dictionary<string, BlueprintValue>)>()
                    };
                    nested.FunctionStack.Push((fnName, args));
                    foreach (var kv in args)
                        nested.EventOutputs[kv.Key] = kv.Value;
                    RunExecChainCore(nested, entry.Id);
                    foreach (var p in fn.Outputs)
                    {
                        if (nested.ImpureCache.TryGetValue((entry.Id, p.Id), out var ov)
                            || nested.EventOutputs.TryGetValue(p.Id, out ov))
                            ctx.ImpureCache[(node.Id, p.Id)] = ov;
                    }
                    // Also pick FunctionReturn cached outs
                    foreach (var rn in fn.Graph.Nodes.Where(n => string.Equals(n.Kind, "FunctionReturn", StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (var p in fn.Outputs)
                        {
                            if (nested.ImpureCache.TryGetValue((rn.Id, p.Id), out var ov))
                                ctx.ImpureCache[(node.Id, p.Id)] = ov;
                        }
                    }
                    break;
                }
                case "FunctionReturn":
                {
                    // Values already readable via pin literals / wires into return node — cache them
                    var fnName = node.FunctionName
                                 ?? ctx.FunctionStack?.Peek().fnName;
                    var fn = ctx.Doc.Functions.FirstOrDefault(f =>
                        string.Equals(f.Name, fnName, StringComparison.OrdinalIgnoreCase));
                    if (fn != null)
                    {
                        foreach (var p in fn.Outputs)
                            ctx.ImpureCache[(node.Id, p.Id)] = ReadPin(ctx, node, p.Id, p.Type);
                    }
                    break;
                }
                case "SetVariable":
                {
                    var vk = ReadString(ctx, node, "varName", "");
                    if (vk.Length == 0 && node.Properties.TryGetValue("varKey", out var k)) vk = k.Trim();
                    var vv = ReadString(ctx, node, "value", "");
                    if (vv.Length == 0 && node.Properties.TryGetValue("varValue", out var v)) vv = v;
                    if (vk.Length > 0)
                    {
                        host.Variables[vk] = vv;
                        if (logSteps)
                            Log.Info($"{BpTag(host)}SetVariable {vk} = {vv}");
                    }
                    break;
                }
                case "CopyVariable":
                {
                    var fk = ReadString(ctx, node, "from", node.Properties.TryGetValue("fromKey", out var f) ? f : "");
                    var tk = ReadString(ctx, node, "to", node.Properties.TryGetValue("toKey", out var t) ? t : "");
                    if (fk.Length == 0 || tk.Length == 0) break;
                    host.Variables.TryGetValue(fk, out var src);
                    host.Variables[tk] = src ?? "";
                    break;
                }
                case "AppendVariable":
                {
                    var vk = ReadString(ctx, node, "varName", node.Properties.TryGetValue("varKey", out var k) ? k : "");
                    var tx = ReadString(ctx, node, "text", node.Properties.TryGetValue("text", out var t) ? t : "");
                    if (vk.Length == 0) break;
                    var cur = host.Variables.TryGetValue(vk, out var ex) ? ex ?? "" : "";
                    host.Variables[vk] = cur + tx;
                    break;
                }
                case "StoreGameTime":
                {
                    var vk = ReadString(ctx, node, "varName", node.Properties.TryGetValue("varKey", out var k) ? k : "");
                    if (vk.Length == 0) break;
                    host.Variables[vk] = Time.time.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                case "StoreObjectName":
                {
                    var vk = ReadString(ctx, node, "varName", node.Properties.TryGetValue("varKey", out var k) ? k : "");
                    if (vk.Length == 0) break;
                    host.Variables[vk] = host.gameObject?.Name ?? "";
                    break;
                }
                case "IncrementVariable":
                {
                    var vk = ReadString(ctx, node, "varName", node.Properties.TryGetValue("varKey", out var k) ? k : "");
                    var delta = ReadFloat(ctx, node, "delta", 1);
                    if (vk.Length == 0) break;
                    var cur = 0.0;
                    if (host.Variables.TryGetValue(vk, out var ex) && ex != null
                        && double.TryParse(ex.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        cur = parsed;
                    host.Variables[vk] = (cur + delta).ToString(CultureInfo.InvariantCulture);
                    break;
                }
                case "MultiplyVariable":
                {
                    var vk = ReadString(ctx, node, "varName", node.Properties.TryGetValue("varKey", out var k) ? k : "");
                    var factor = ReadFloat(ctx, node, "factor", 1);
                    if (vk.Length == 0) break;
                    var cur = 0.0;
                    if (host.Variables.TryGetValue(vk, out var ex) && ex != null
                        && double.TryParse(ex.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        cur = parsed;
                    host.Variables[vk] = (cur * factor).ToString(CultureInfo.InvariantCulture);
                    break;
                }
                case "ClearVariable":
                {
                    var vk = ReadString(ctx, node, "varName", node.Properties.TryGetValue("varKey", out var k) ? k : "");
                    if (vk.Length == 0) break;
                    host.Variables.Remove(vk);
                    break;
                }
                case "ReflectSet":
                {
                    var mpath = node.Properties.TryGetValue("memberPath", out var mp) ? mp.Trim() : "";
                    if (mpath.Length == 0) break;
                    var valStr = ReadString(ctx, node, "value", "");
                    if (valStr.Length == 0)
                        valStr = BlueprintReflection.ResolveValueString(host, node);
                    if (BlueprintReflection.IsStaticMode(node))
                    {
                        var tn = node.Properties.TryGetValue("typeName", out var tnm) ? tnm.Trim() : "";
                        if (tn.Length == 0) break;
                        if (!BlueprintReflection.TryWriteStaticPath(tn, mpath, valStr, out var err))
                        {
                            if (logSteps) Log.Warning($"{BpTag(host)}ReflectSet {err}");
                        }
                        else if (logSteps)
                            Log.Info($"{BpTag(host)}ReflectSet static {tn}.{mpath}");
                    }
                    else
                    {
                        var go = BlueprintReflection.ResolveScopeGameObject(host, node);
                        var ctp = node.Properties.TryGetValue("componentType", out var ct) ? ct.Trim() : "";
                        var root = BlueprintReflection.ResolveMemberRoot(go, ctp);
                        if (root == null)
                        {
                            if (logSteps) Log.Warning($"{BpTag(host)}ReflectSet: no component '{ctp}'");
                            break;
                        }
                        if (!BlueprintReflection.TryWritePath(root, mpath, valStr, out var err))
                        {
                            if (logSteps) Log.Warning($"{BpTag(host)}ReflectSet {err}");
                        }
                        else if (logSteps)
                            Log.Info($"{BpTag(host)}ReflectSet {ctp}.{mpath}");
                    }
                    break;
                }
                default:
                    if (logSteps)
                        Log.Debug($"{BpTag(host)}{node.Kind}");
                    break;
            }
        }

        static string BpTag(Behavior host) =>
            host.gameObject?.Name is { Length: > 0 } g ? $"[Blueprint:{g}] " : "[Blueprint] ";
    }
}
