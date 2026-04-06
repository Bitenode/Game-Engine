#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Events;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Loads graphs and walks exec wires for <see cref="VisualBlueprintBehavior"/>.</summary>
    public static class BlueprintFlowRuntime
    {
        public const string PinExecInLegacy = "In";
        public const string PinExecOutLegacy = "Out";
        public const string PinExecIn = "ExecIn";
        public const string PinExecOut = "ExecOut";
        public const string PinThen = "Then";
        public const string PinElse = "Else";

        public static bool IsExecOutPin(string? pin) =>
            !string.IsNullOrEmpty(pin)
            && (string.Equals(pin, PinExecOut, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinExecOutLegacy, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinThen, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinElse, StringComparison.OrdinalIgnoreCase));

        public static bool IsExecInPin(string? pin) =>
            !string.IsNullOrEmpty(pin)
            && (string.Equals(pin, PinExecIn, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pin, PinExecInLegacy, StringComparison.OrdinalIgnoreCase));

        public static void NormalizeLegacyPins(BlueprintGraph graph)
        {
            foreach (var w in graph.Wires)
            {
                if (string.Equals(w.FromPin, PinExecOutLegacy, StringComparison.OrdinalIgnoreCase))
                    w.FromPin = PinExecOut;
                if (string.Equals(w.ToPin, PinExecInLegacy, StringComparison.OrdinalIgnoreCase))
                    w.ToPin = PinExecIn;
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
                var doc = BlueprintPersistence.LoadDocument(path);
                NormalizeLegacyPins(doc.Graph);
                return doc.Graph;
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"Failed to load blueprint: {ex.Message}");
                return null;
            }
        }

        public static void RunEventNodes(VisualBlueprintBehavior host, BlueprintGraph? graph, string eventKind)
        {
            if (graph == null || !host.IsActiveAndEnabled) return;
            foreach (var n in graph.Nodes)
            {
                if (!string.Equals(n.Kind, eventKind, StringComparison.OrdinalIgnoreCase)) continue;
                var def = BlueprintNodeCatalog.Resolve(n.Kind);
                if (def.Category != BlueprintNodeCategory.Event) continue;
                RunExecChain(host, graph, n.Id, host.LogSteps);
            }
        }

        public static void RunExecChain(VisualBlueprintBehavior host, BlueprintGraph graph, string startNodeId, bool logSteps)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? cur = startNodeId;
            while (cur != null)
            {
                if (!visited.Add(cur))
                {
                    if (logSteps)
                        Log.Warning($"{BpTag(host)}execution cycle at node {cur}");
                    break;
                }

                var node = graph.Nodes.FirstOrDefault(n => n.Id == cur);
                if (node == null) break;

                if (string.Equals(node.Kind, "Delay", StringComparison.Ordinal))
                {
                    var delayWire = FindNextExecWire(graph, cur, null);
                    if (delayWire != null)
                    {
                        var raw = node.Properties.TryGetValue("seconds", out var s) ? s : "1";
                        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                            sec = 1f;
                        sec = Math.Max(0f, sec);
                        host.ScheduleExec(delayWire.ToNodeId, Time.time + sec, logSteps);
                        if (logSteps)
                            Log.Info($"{BpTag(host)}Delay {sec:0.###}s → next");
                    }
                    break;
                }

                string? pinPick = null;
                if (string.Equals(node.Kind, "Branch", StringComparison.Ordinal))
                    pinPick = EvaluateBranchCondition(host, node) ? PinThen : PinElse;
                else if (string.Equals(node.Kind, "BranchEquals", StringComparison.Ordinal))
                    pinPick = EvaluateBranchEquals(host, node) ? PinThen : PinElse;
                else if (string.Equals(node.Kind, "BranchCompare", StringComparison.Ordinal))
                    pinPick = EvaluateBranchCompare(host, node) ? PinThen : PinElse;
                else if (string.Equals(node.Kind, "RandomBranch", StringComparison.Ordinal))
                    pinPick = EvaluateRandomBranch(node) ? PinThen : PinElse;

                ExecuteNode(host, node, logSteps, pinPick);

                var nextWire = FindNextExecWire(graph, cur, pinPick);
                cur = nextWire?.ToNodeId;
            }
        }

        internal static BlueprintWire? FindNextExecWire(BlueprintGraph graph, string fromNodeId, string? fromPin)
        {
            BlueprintWire? any = null;
            foreach (var w in graph.Wires)
            {
                if (!string.Equals(w.FromNodeId, fromNodeId, StringComparison.Ordinal)) continue;
                if (!IsExecOutPin(w.FromPin) || !IsExecInPin(w.ToPin)) continue;
                if (any == null) any = w;
                if (fromPin != null && string.Equals(w.FromPin, fromPin, StringComparison.OrdinalIgnoreCase))
                    return w;
            }
            return fromPin == null ? any : null;
        }

        static bool EvaluateBranchCondition(VisualBlueprintBehavior host, BlueprintNode node)
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

        static bool EvaluateRandomBranch(BlueprintNode node)
        {
            var raw = node.Properties.TryGetValue("chance", out var c) ? c : "0.5";
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                p = 0.5;
            p = Math.Clamp(p, 0.0, 1.0);
            return Random.Shared.NextDouble() < p;
        }

        static bool EvaluateBranchEquals(VisualBlueprintBehavior host, BlueprintNode node)
        {
            var key = node.Properties.TryGetValue("conditionKey", out var k) ? k.Trim() : "";
            var expect = node.Properties.TryGetValue("equalsValue", out var ev) ? ev.Trim() : "";
            if (string.IsNullOrEmpty(key))
                return string.IsNullOrEmpty(expect);
            if (!host.Variables.TryGetValue(key, out var val) || val == null)
                val = "";
            return string.Equals(val.Trim(), expect, StringComparison.OrdinalIgnoreCase);
        }

        static bool EvaluateBranchCompare(VisualBlueprintBehavior host, BlueprintNode node)
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
            const double eps = 1e-9;
            return op switch
            {
                "lt" or "<" => lhs < rhs,
                "lte" or "<=" => lhs <= rhs,
                "gt" or ">" => lhs > rhs,
                "gte" or ">=" => lhs >= rhs,
                "eq" or "=" or "==" => Math.Abs(lhs - rhs) < eps,
                _ => Math.Abs(lhs - rhs) < eps,
            };
        }

        internal static GameObject? ResolveTargetObject(BlueprintNode node)
        {
            var path = node.Properties.TryGetValue("targetPath", out var tp) ? tp.Trim() : "";
            var name = node.Properties.TryGetValue("targetName", out var tn) ? tn.Trim() : "";
            if (path.Length > 0)
            {
                var byPath = SceneQuery.FindByPath(path);
                if (byPath != null) return byPath;
            }
            if (name.Length > 0)
                return SceneQuery.FindByName(name);
            return null;
        }

        static double ReadDoubleProp(BlueprintNode node, string propKey, double fallback = 0)
        {
            var s = node.Properties.TryGetValue(propKey, out var v) ? v : "";
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        }

        static void ApplyTransformPosition(GameObject? go, BlueprintNode node, bool logSteps, Behavior host, string logLabel)
        {
            if (go == null) return;
            var x = ReadDoubleProp(node, "x");
            var y = ReadDoubleProp(node, "y");
            var z = ReadDoubleProp(node, "z");
            var relRaw = node.Properties.TryGetValue("relative", out var r) ? r : "false";
            var relative = bool.TryParse(relRaw, out var rb) && rb;
            var t = go.Transform;
            if (relative)
            {
                t.Position.X += x;
                t.Position.Y += y;
                t.Position.Z += z;
            }
            else
            {
                t.Position.X = x;
                t.Position.Y = y;
                t.Position.Z = z;
            }
            if (logSteps)
                Log.Info($"{BpTag(host)}{logLabel} {go.Name} → ({t.Position.X:0.###}, {t.Position.Y:0.###}, {t.Position.Z:0.###}){(relative ? " Δ" : "")}");
        }

        static void ApplyTransformRotation(GameObject? go, BlueprintNode node, bool logSteps, Behavior host, string logLabel)
        {
            if (go == null) return;
            var x = ReadDoubleProp(node, "x");
            var y = ReadDoubleProp(node, "y");
            var z = ReadDoubleProp(node, "z");
            var relRaw = node.Properties.TryGetValue("relative", out var r) ? r : "false";
            var relative = bool.TryParse(relRaw, out var rb) && rb;
            var t = go.Transform;
            if (relative)
            {
                t.Rotation.X += x;
                t.Rotation.Y += y;
                t.Rotation.Z += z;
            }
            else
            {
                t.Rotation.X = x;
                t.Rotation.Y = y;
                t.Rotation.Z = z;
            }
            if (logSteps)
                Log.Info($"{BpTag(host)}{logLabel} {go.Name} rot → ({t.Rotation.X:0.###}, {t.Rotation.Y:0.###}, {t.Rotation.Z:0.###})°{(relative ? " Δ" : "")}");
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

        static void ExecuteNode(VisualBlueprintBehavior host, BlueprintNode node, bool logSteps, string? branchPinPick = null)
        {
            var cat = BlueprintNodeCatalog.Resolve(node.Kind).Category;
            if (cat is BlueprintNodeCategory.Event or BlueprintNodeCategory.Comment)
                return;
            if (cat == BlueprintNodeCategory.Flow
                && !string.Equals(node.Kind, "Branch", StringComparison.Ordinal)
                && !string.Equals(node.Kind, "BranchEquals", StringComparison.Ordinal)
                && !string.Equals(node.Kind, "BranchCompare", StringComparison.Ordinal)
                && !string.Equals(node.Kind, "RandomBranch", StringComparison.Ordinal))
            {
                if (logSteps)
                    Log.Debug($"{BpTag(host)}Flow: {node.Kind}");
                return;
            }
            if (string.Equals(node.Kind, "Branch", StringComparison.Ordinal))
            {
                var pick = branchPinPick ?? (EvaluateBranchCondition(host, node) ? PinThen : PinElse);
                if (logSteps)
                    Log.Debug($"{BpTag(host)}Branch → {(string.Equals(pick, PinThen, StringComparison.OrdinalIgnoreCase) ? "Then" : "Else")}");
                return;
            }
            if (string.Equals(node.Kind, "RandomBranch", StringComparison.Ordinal))
            {
                var pick = branchPinPick ?? (EvaluateRandomBranch(node) ? PinThen : PinElse);
                if (logSteps)
                    Log.Debug($"{BpTag(host)}RandomBranch → {(string.Equals(pick, PinThen, StringComparison.OrdinalIgnoreCase) ? "Then" : "Else")}");
                return;
            }
            if (string.Equals(node.Kind, "BranchEquals", StringComparison.Ordinal))
            {
                var pick = branchPinPick ?? (EvaluateBranchEquals(host, node) ? PinThen : PinElse);
                if (logSteps)
                    Log.Debug($"{BpTag(host)}BranchEquals → {(string.Equals(pick, PinThen, StringComparison.OrdinalIgnoreCase) ? "Then" : "Else")}");
                return;
            }
            if (string.Equals(node.Kind, "BranchCompare", StringComparison.Ordinal))
            {
                var pick = branchPinPick ?? (EvaluateBranchCompare(host, node) ? PinThen : PinElse);
                if (logSteps)
                    Log.Debug($"{BpTag(host)}BranchCompare → {(string.Equals(pick, PinThen, StringComparison.OrdinalIgnoreCase) ? "Then" : "Else")}");
                return;
            }

            switch (node.Kind)
            {
                case "LogMessage":
                case "Call":
                case "Math":
                {
                    var msg = node.Properties.TryGetValue("message", out var m) ? m : node.Title;
                    if (logSteps)
                        Log.Info($"{BpTag(host)}{msg}");
                    break;
                }
                case "SetObjectActive":
                {
                    var raw = node.Properties.TryGetValue("active", out var a) ? a : "true";
                    if (!bool.TryParse(raw, out var en)) en = true;
                    if (host.gameObject != null) host.gameObject.Enabled = en;
                    if (logSteps)
                        Log.Info($"{BpTag(host)}SetObjectActive = {en}");
                    break;
                }
                case "SetOtherObjectActive":
                {
                    var path = node.Properties.TryGetValue("targetPath", out var tp) ? tp.Trim() : "";
                    var name = node.Properties.TryGetValue("targetName", out var tn) ? tn.Trim() : "";
                    var raw = node.Properties.TryGetValue("active", out var a) ? a : "true";
                    if (!bool.TryParse(raw, out var en)) en = true;
                    var target = ResolveTargetObject(node);
                    if (target != null)
                    {
                        target.Enabled = en;
                        if (logSteps)
                            Log.Info($"{BpTag(host)}SetOtherObjectActive {target.Name} = {en}");
                    }
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}SetOtherObjectActive: no target (path='{path}', name='{name}')");
                    break;
                }
                case "SetObjectPosition":
                    ApplyTransformPosition(host.gameObject, node, logSteps, host, "SetObjectPosition");
                    break;
                case "SetOtherObjectPosition":
                {
                    var path = node.Properties.TryGetValue("targetPath", out var tp) ? tp.Trim() : "";
                    var nm = node.Properties.TryGetValue("targetName", out var tn) ? tn.Trim() : "";
                    var target = ResolveTargetObject(node);
                    if (target != null)
                        ApplyTransformPosition(target, node, logSteps, host, "SetOtherObjectPosition");
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}SetOtherObjectPosition: no target (path='{path}', name='{nm}')");
                    break;
                }
                case "SetObjectRotation":
                    ApplyTransformRotation(host.gameObject, node, logSteps, host, "SetObjectRotation");
                    break;
                case "SetOtherObjectRotation":
                {
                    var path = node.Properties.TryGetValue("targetPath", out var tp) ? tp.Trim() : "";
                    var nm = node.Properties.TryGetValue("targetName", out var tn) ? tn.Trim() : "";
                    var target = ResolveTargetObject(node);
                    if (target != null)
                        ApplyTransformRotation(target, node, logSteps, host, "SetOtherObjectRotation");
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}SetOtherObjectRotation: no target (path='{path}', name='{nm}')");
                    break;
                }
                case "DestroyObject":
                {
                    var scope = node.Properties.TryGetValue("scope", out var sc) ? sc.Trim() : "Other";
                    GameObject? victim = null;
                    if (string.Equals(scope, "Self", StringComparison.OrdinalIgnoreCase))
                        victim = host.gameObject;
                    else
                        victim = ResolveTargetObject(node);
                    var path = node.Properties.TryGetValue("targetPath", out var tpp) ? tpp.Trim() : "";
                    var name = node.Properties.TryGetValue("targetName", out var tnn) ? tnn.Trim() : "";
                    if (victim != null)
                        DestroyGameObjectTree(victim, publishDestroyedEvent: true, logSteps, host);
                    else if (logSteps)
                        Log.Warning($"{BpTag(host)}DestroyObject: no target (scope='{scope}', path='{path}', name='{name}')");
                    break;
                }
                case "FireBlueprintEvent":
                {
                    var ev = node.Properties.TryGetValue("eventName", out var en) ? en.Trim() : "";
                    if (ev.Length == 0) break;
                    var payload = node.Properties.TryGetValue("payload", out var pl) ? pl : "";
                    EventBus.Publish(new BlueprintMessageEvent
                    {
                        Name = ev,
                        Data = payload,
                        Sender = host.gameObject
                    });
                    if (logSteps)
                        Log.Info($"{BpTag(host)}Event '{ev}'{(string.IsNullOrEmpty(payload) ? "" : $" data={payload}")}");
                    break;
                }
                case "CopyVariable":
                {
                    var fk = node.Properties.TryGetValue("fromKey", out var from) ? from.Trim() : "";
                    var tk = node.Properties.TryGetValue("toKey", out var to) ? to.Trim() : "";
                    if (fk.Length == 0 || tk.Length == 0) break;
                    host.Variables.TryGetValue(fk, out var src);
                    host.Variables[tk] = src ?? "";
                    if (logSteps)
                        Log.Info($"{BpTag(host)}CopyVariable {fk} → {tk}");
                    break;
                }
                case "AppendVariable":
                {
                    var vk = node.Properties.TryGetValue("varKey", out var key) ? key.Trim() : "";
                    var tx = node.Properties.TryGetValue("text", out var t) ? t : "";
                    if (vk.Length == 0) break;
                    var cur = host.Variables.TryGetValue(vk, out var ex) ? ex ?? "" : "";
                    host.Variables[vk] = cur + tx;
                    if (logSteps)
                        Log.Info($"{BpTag(host)}AppendVariable {vk} (len {host.Variables[vk].Length})");
                    break;
                }
                case "StoreGameTime":
                {
                    var vk = node.Properties.TryGetValue("varKey", out var key) ? key.Trim() : "";
                    if (vk.Length == 0) break;
                    host.Variables[vk] = Time.time.ToString(CultureInfo.InvariantCulture);
                    if (logSteps)
                        Log.Info($"{BpTag(host)}StoreGameTime → {vk}={host.Variables[vk]}");
                    break;
                }
                case "StoreObjectName":
                {
                    var vk = node.Properties.TryGetValue("varKey", out var key) ? key.Trim() : "";
                    if (vk.Length == 0) break;
                    host.Variables[vk] = host.gameObject?.Name ?? "";
                    if (logSteps)
                        Log.Info($"{BpTag(host)}StoreObjectName → {vk}={host.Variables[vk]}");
                    break;
                }
                case "SetVariable":
                {
                    var vk = node.Properties.TryGetValue("varKey", out var key) ? key.Trim() : "";
                    var vv = node.Properties.TryGetValue("varValue", out var val) ? val : "";
                    if (vk.Length > 0)
                    {
                        host.Variables[vk] = vv;
                        if (logSteps)
                            Log.Info($"{BpTag(host)}SetVariable {vk} = {vv}");
                    }
                    break;
                }
                case "IncrementVariable":
                {
                    var vk = node.Properties.TryGetValue("varKey", out var key) ? key.Trim() : "";
                    var dRaw = node.Properties.TryGetValue("delta", out var ds) ? ds : "1";
                    if (vk.Length == 0) break;
                    if (!double.TryParse(dRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
                        delta = 1.0;
                    var cur = 0.0;
                    if (host.Variables.TryGetValue(vk, out var ex) && ex != null
                        && double.TryParse(ex.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        cur = parsed;
                    var next = cur + delta;
                    host.Variables[vk] = next.ToString(CultureInfo.InvariantCulture);
                    if (logSteps)
                        Log.Info($"{BpTag(host)}IncrementVariable {vk} += {delta} → {host.Variables[vk]}");
                    break;
                }
                case "MultiplyVariable":
                {
                    var vk = node.Properties.TryGetValue("varKey", out var key) ? key.Trim() : "";
                    var fRaw = node.Properties.TryGetValue("factor", out var fs) ? fs : "1";
                    if (vk.Length == 0) break;
                    if (!double.TryParse(fRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
                        factor = 1.0;
                    var cur = 0.0;
                    if (host.Variables.TryGetValue(vk, out var ex) && ex != null
                        && double.TryParse(ex.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        cur = parsed;
                    var next = cur * factor;
                    host.Variables[vk] = next.ToString(CultureInfo.InvariantCulture);
                    if (logSteps)
                        Log.Info($"{BpTag(host)}MultiplyVariable {vk} × {factor} → {host.Variables[vk]}");
                    break;
                }
                case "ClearVariable":
                {
                    var vk = node.Properties.TryGetValue("varKey", out var key) ? key.Trim() : "";
                    if (vk.Length == 0) break;
                    host.Variables.Remove(vk);
                    if (logSteps)
                        Log.Info($"{BpTag(host)}ClearVariable {vk}");
                    break;
                }
                case "ReflectGet":
                {
                    var outKey = node.Properties.TryGetValue("varKey", out var ok) ? ok.Trim() : "";
                    var mpath = node.Properties.TryGetValue("memberPath", out var mp) ? mp.Trim() : "";
                    if (outKey.Length == 0 || mpath.Length == 0) break;
                    if (BlueprintReflection.IsStaticMode(node))
                    {
                        var tn = node.Properties.TryGetValue("typeName", out var tnm) ? tnm.Trim() : "";
                        if (tn.Length == 0) break;
                        if (!BlueprintReflection.TryReadStaticPath(tn, mpath, out var val, out var err))
                        {
                            if (logSteps)
                                Log.Warning($"{BpTag(host)}ReflectGet {err}");
                            break;
                        }
                        host.Variables[outKey] = BlueprintReflection.FormatValue(val);
                        if (logSteps)
                            Log.Info($"{BpTag(host)}ReflectGet static {tn}.{mpath} → {outKey}");
                    }
                    else
                    {
                        var go = BlueprintReflection.ResolveScopeGameObject(host, node);
                        var ctp = node.Properties.TryGetValue("componentType", out var ct) ? ct.Trim() : "";
                        var root = BlueprintReflection.ResolveMemberRoot(go, ctp);
                        if (root == null)
                        {
                            if (logSteps)
                                Log.Warning($"{BpTag(host)}ReflectGet: no component '{ctp}'");
                            break;
                        }
                        if (!BlueprintReflection.TryReadPath(root, mpath, out var val, out var err))
                        {
                            if (logSteps)
                                Log.Warning($"{BpTag(host)}ReflectGet {err}");
                            break;
                        }
                        host.Variables[outKey] = BlueprintReflection.FormatValue(val);
                        if (logSteps)
                            Log.Info($"{BpTag(host)}ReflectGet {ctp}.{mpath} → {outKey}");
                    }
                    break;
                }
                case "ReflectSet":
                {
                    var mpath = node.Properties.TryGetValue("memberPath", out var mp) ? mp.Trim() : "";
                    if (mpath.Length == 0) break;
                    var valStr = BlueprintReflection.ResolveValueString(host, node);
                    if (BlueprintReflection.IsStaticMode(node))
                    {
                        var tn = node.Properties.TryGetValue("typeName", out var tnm) ? tnm.Trim() : "";
                        if (tn.Length == 0) break;
                        if (!BlueprintReflection.TryWriteStaticPath(tn, mpath, valStr, out var err))
                        {
                            if (logSteps)
                                Log.Warning($"{BpTag(host)}ReflectSet {err}");
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
                            if (logSteps)
                                Log.Warning($"{BpTag(host)}ReflectSet: no component '{ctp}'");
                            break;
                        }
                        if (!BlueprintReflection.TryWritePath(root, mpath, valStr, out var err))
                        {
                            if (logSteps)
                                Log.Warning($"{BpTag(host)}ReflectSet {err}");
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
