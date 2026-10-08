#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>In-memory checks for the blueprint upgrade (no play mode required).</summary>
    public static class BlueprintSmokeChecks
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            int fail = 0;

            void Check(string name, bool ok, string detail = "")
            {
                if (ok) sb.AppendLine($"PASS  {name}");
                else
                {
                    fail++;
                    sb.AppendLine($"FAIL  {name}{(detail.Length > 0 ? ": " + detail : "")}");
                }
            }

            // v1 load + migrate
            var v1Path = Path.Combine(Path.GetTempPath(), "ge_bp_smoke_v1.blueprint");
            File.WriteAllText(v1Path,
                """
                {"version":1,"graph":{"nodes":[
                {"id":"begin01","kind":"BeginPlay","title":"Begin Play","x":0,"y":0,"properties":{}},
                {"id":"branch01","kind":"Branch","title":"Branch","x":200,"y":0,"properties":{"conditionKey":"flag"}},
                {"id":"print01","kind":"LogMessage","title":"Print","x":400,"y":0,"properties":{"message":"hi"}}
                ],"wires":[
                {"fromNodeId":"begin01","toNodeId":"branch01","fromPin":"ExecOut","toPin":"ExecIn"},
                {"fromNodeId":"branch01","toNodeId":"print01","fromPin":"Then","toPin":"ExecIn"}
                ]}}
                """);
            var v1 = BlueprintPersistence.LoadDocument(v1Path);
            Check("v1 upgrades to version 2", v1.Version >= 2);
            var v1Branch = v1.Graph.Nodes.First(n => n.Kind == "Branch");
            Check("v1 Branch keeps conditionKey", v1Branch.Properties.ContainsKey("conditionKey"));
            Check("v1 Branch migrates condition pin literal", v1Branch.PinLiterals.ContainsKey("condition"));

            // v2 typed branch graph
            var doc = new BlueprintDocument();
            doc.Variables.Add(new BlueprintVariableDecl { Name = "Health", Type = BlueprintPinType.Float, DefaultLiteral = "5" });
            var begin = doc.Graph.AddNode("BeginPlay", "Begin Play", 0, 0);
            var get = doc.Graph.AddNode("GetVar", "Get Health", 100, 80);
            get.VariableName = "Health";
            var less = doc.Graph.AddNode("LessFloat", "Less", 280, 80);
            less.SetPinLiteral("b", "10");
            var branch = doc.Graph.AddNode("Branch", "Branch", 480, 0);
            var print = doc.Graph.AddNode("LogMessage", "Print", 680, 0);
            print.SetPinLiteral("message", "low");
            doc.Graph.Wires.Add(new BlueprintWire
            {
                FromNodeId = begin.Id, ToNodeId = branch.Id,
                FromPin = "ExecOut", ToPin = "ExecIn", Kind = BlueprintWireKind.Exec
            });
            doc.Graph.Wires.Add(new BlueprintWire
            {
                FromNodeId = get.Id, ToNodeId = less.Id,
                FromPin = "value", ToPin = "a", Kind = BlueprintWireKind.Data
            });
            doc.Graph.Wires.Add(new BlueprintWire
            {
                FromNodeId = less.Id, ToNodeId = branch.Id,
                FromPin = "result", ToPin = "condition", Kind = BlueprintWireKind.Data
            });
            doc.Graph.Wires.Add(new BlueprintWire
            {
                FromNodeId = branch.Id, ToNodeId = print.Id,
                FromPin = "Then", ToPin = "ExecIn", Kind = BlueprintWireKind.Exec
            });
            var okIssues = BlueprintValidation.Validate(doc);
            Check("v2 typed Health→Less→Branch validates clean", okIssues.Count == 0,
                string.Join("; ", okIssues.Select(i => i.Message)));

            // Delay pin
            var delayNode = doc.Graph.AddNode("Delay", "Delay", 0, 200);
            delayNode.SetPinLiteral("seconds", "0.5");
            Check("Delay exposes seconds pin",
                BlueprintNodeCatalog.ResolvePins(delayNode, doc).Any(p => p.Id == "seconds"));

            // Type mismatch rejected
            var bad = new BlueprintDocument();
            var n1 = bad.Graph.AddNode("GetVar", "Get", 0, 0);
            n1.VariableName = "Health";
            bad.Variables.Add(new BlueprintVariableDecl { Name = "Health", Type = BlueprintPinType.Float, DefaultLiteral = "1" });
            var n2 = bad.Graph.AddNode("Branch", "Branch", 200, 0);
            bad.Graph.Wires.Add(new BlueprintWire
            {
                FromNodeId = n1.Id, ToNodeId = n2.Id,
                FromPin = "value", ToPin = "condition", Kind = BlueprintWireKind.Data
            });
            var badIssues = BlueprintValidation.Validate(bad);
            Check("float→bool data wire is rejected",
                badIssues.Any(i => i.Message.Contains("type mismatch", StringComparison.OrdinalIgnoreCase)),
                string.Join("; ", badIssues.Select(i => i.Message)));

            // Round-trip save
            var v2Path = Path.Combine(Path.GetTempPath(), "ge_bp_smoke_v2.blueprint");
            BlueprintPersistence.SaveDocument(v2Path, doc);
            var round = BlueprintPersistence.LoadDocument(v2Path);
            Check("v2 round-trip preserves variables", round.Variables.Count == 1 && round.Graph.Nodes.Count >= 5);

            sb.AppendLine(fail == 0 ? "ALL SMOKE CHECKS PASSED" : $"{fail} SMOKE CHECK(S) FAILED");
            return sb.ToString();
        }
    }
}
