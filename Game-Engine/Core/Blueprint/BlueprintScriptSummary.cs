#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Human-readable behavior outline for the blueprint editor.</summary>
    public static class BlueprintScriptSummary
    {
        public static void AppendFlowOverview(StringBuilder sb, BlueprintGraph g)
        {
            var events = g.Nodes.Where(n => BlueprintNodeCatalog.Resolve(n.Kind).Category == BlueprintNodeCategory.Event)
                .ToList();
            if (events.Count == 0)
            {
                sb.AppendLine("No event nodes (add Begin Play or Tick).");
                return;
            }

            foreach (var ev in events)
            {
                sb.Append($"{ev.Kind}: ");
                var chain = LinearizeFrom(g, ev.Id);
                if (chain.Count <= 1)
                {
                    sb.AppendLine("(no actions wired)");
                    continue;
                }
                sb.AppendLine(string.Join(" → ", chain.Skip(1).Select(FormatStep)));
            }
        }

        static string FormatStep(BlueprintNode n)
        {
            var d = BlueprintNodeCatalog.Resolve(n.Kind);
            if (n.Kind == "LogMessage" && n.Properties.TryGetValue("message", out var m)) return $"Print(\"{m}\")";
            if (n.Kind == "SetObjectActive" && n.Properties.TryGetValue("active", out var a)) return $"SetActive(self, {a})";
            if (n.Kind == "Delay" && n.Properties.TryGetValue("seconds", out var sec)) return $"Delay({sec}s)";
            if (n.Kind == "SetVariable" && n.Properties.TryGetValue("varKey", out var vk)
                && n.Properties.TryGetValue("varValue", out var vv)) return $"Var[{vk}]={vv}";
            if (n.Kind == "IncrementVariable" && n.Properties.TryGetValue("varKey", out var ik)
                && n.Properties.TryGetValue("delta", out var del)) return $"Var[{ik}]+={del}";
            if (n.Kind == "Branch" && n.Properties.TryGetValue("conditionKey", out var ck)) return $"Branch({ck})→…";
            if (n.Kind == "BranchEquals" && n.Properties.TryGetValue("conditionKey", out var bek)
                && n.Properties.TryGetValue("equalsValue", out var bev)) return $"StrEq({bek},{bev})→…";
            if (n.Kind == "BranchCompare" && n.Properties.TryGetValue("conditionKey", out var nck)
                && n.Properties.TryGetValue("compareOp", out var nop)
                && n.Properties.TryGetValue("compareValue", out var ncv)) return $"Num({nck} {nop} {ncv})→…";
            if (n.Kind == "RandomBranch" && n.Properties.TryGetValue("chance", out var ch)) return $"Random({ch})→…";
            if (n.Kind == "MultiplyVariable" && n.Properties.TryGetValue("varKey", out var mk)
                && n.Properties.TryGetValue("factor", out var fac)) return $"Var[{mk}]×={fac}";
            if (n.Kind == "ClearVariable" && n.Properties.TryGetValue("varKey", out var clk)) return $"Clear[{clk}]";
            if (n.Kind == "CopyVariable" && n.Properties.TryGetValue("fromKey", out var fk)
                && n.Properties.TryGetValue("toKey", out var tk)) return $"Var[{fk}]→[{tk}]";
            if (n.Kind == "AppendVariable" && n.Properties.TryGetValue("varKey", out var apk)
                && n.Properties.TryGetValue("text", out var apt)) return $"Var[{apk}]+=\"{apt}\"";
            if (n.Kind == "StoreGameTime" && n.Properties.TryGetValue("varKey", out var gtk)) return $"time→[{gtk}]";
            if (n.Kind == "StoreObjectName" && n.Properties.TryGetValue("varKey", out var onk)) return $"name→[{onk}]";
            if (n.Kind == "FireBlueprintEvent" && n.Properties.TryGetValue("eventName", out var fev)) return $"Event({fev})";
            if (n.Kind == "SetObjectPosition") return "Pos(self)";
            if (n.Kind == "SetOtherObjectPosition") return "Pos(other)";
            if (n.Kind == "SetObjectRotation") return "Rot(self)";
            if (n.Kind == "SetOtherObjectRotation") return "Rot(other)";
            if (n.Kind == "DestroyObject" && n.Properties.TryGetValue("scope", out var ds))
                return string.Equals(ds, "Self", System.StringComparison.OrdinalIgnoreCase) ? "Destroy(self)" : "Destroy(other)";
            if (n.Kind == "ReflectGet" && n.Properties.TryGetValue("memberPath", out var rg))
                return n.Properties.TryGetValue("mode", out var rgm) && string.Equals(rgm, "Static", System.StringComparison.OrdinalIgnoreCase)
                    ? $"Get[static]({rg})" : $"Get({rg})";
            if (n.Kind == "ReflectSet" && n.Properties.TryGetValue("memberPath", out var rs))
                return n.Properties.TryGetValue("mode", out var rsm) && string.Equals(rsm, "Static", System.StringComparison.OrdinalIgnoreCase)
                    ? $"Set[static]({rs})" : $"Set({rs})";
            if (n.Kind == "SetOtherObjectActive")
            {
                var path = n.Properties.TryGetValue("targetPath", out var tp) ? tp : "";
                var nm = n.Properties.TryGetValue("targetName", out var tn) ? tn : "";
                var ac = n.Properties.TryGetValue("active", out var act) ? act : "?";
                var who = !string.IsNullOrWhiteSpace(path) ? $"path:{path}" : nm;
                return $"SetActive({who}, {ac})";
            }
            return d.DefaultTitle.Length > 0 ? d.DefaultTitle : n.Kind;
        }

        /// <summary>Walk exec edges; first node is the root id's node.</summary>
        public static List<BlueprintNode> LinearizeFrom(BlueprintGraph g, string startId)
        {
            var list = new List<BlueprintNode>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            string? cur = startId;
            while (cur != null)
            {
                if (!seen.Add(cur)) break;
                var n = g.Nodes.FirstOrDefault(x => x.Id == cur);
                if (n == null) break;
                list.Add(n);
                BlueprintWire? w = null;
                foreach (var edge in g.Wires)
                {
                    if (edge.FromNodeId != cur || !BlueprintFlowRuntime.IsExecOutPin(edge.FromPin)) continue;
                    w = edge;
                    break;
                }
                cur = w?.ToNodeId;
            }
            return list;
        }
    }
}
