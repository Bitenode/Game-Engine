#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game_Engine.Core.Blueprint
{
    /// <summary>Blueprint document on disk (JSON, typically extension .blueprint).</summary>
    public sealed class BlueprintDocument
    {
        public int Version { get; set; } = 2;
        public BlueprintGraph Graph { get; set; } = new();
        public List<BlueprintVariableDecl> Variables { get; set; } = new();
        public List<BlueprintFunctionDecl> Functions { get; set; } = new();
    }

    /// <summary>Load/save <c>.blueprint</c> JSON. Default project folder <c>Assets/Blueprints/</c>; see <c>Docs/14_Visual_Blueprints.md</c>.</summary>
    public static class BlueprintPersistence
    {
        public const int CurrentVersion = 2;

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        public static string BlueprintsFolderAbs(string projectRoot)
            => Path.Combine(projectRoot, "Assets", "Blueprints");

        /// <summary>Ensures Assets/Blueprints exists; returns absolute path.</summary>
        public static string EnsureBlueprintsFolder(string projectRoot)
        {
            var dir = BlueprintsFolderAbs(projectRoot);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Save(string filePathAbs, BlueprintGraph graph)
            => SaveDocument(filePathAbs, new BlueprintDocument { Version = CurrentVersion, Graph = graph });

        public static void SaveDocument(string filePathAbs, BlueprintDocument doc)
        {
            doc.Version = CurrentVersion;
            doc.Graph ??= new();
            doc.Variables ??= new();
            doc.Functions ??= new();
            var json = JsonSerializer.Serialize(doc, JsonOptions);
            var dir = Path.GetDirectoryName(filePathAbs);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePathAbs, json);
        }

        public static BlueprintDocument LoadDocument(string filePathAbs)
        {
            var text = File.ReadAllText(filePathAbs);
            var doc = JsonSerializer.Deserialize<BlueprintDocument>(text, JsonOptions);
            if (doc?.Graph == null)
                throw new InvalidDataException("Invalid blueprint file.");
            doc.Graph.Nodes ??= new();
            doc.Graph.Wires ??= new();
            doc.Variables ??= new();
            doc.Functions ??= new();
            foreach (var n in doc.Graph.Nodes)
            {
                n.Properties ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                n.PinLiterals ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                n.DynamicPins ??= new();
            }
            foreach (var fn in doc.Functions)
            {
                fn.Graph ??= new();
                fn.Graph.Nodes ??= new();
                fn.Graph.Wires ??= new();
                fn.Inputs ??= new();
                fn.Outputs ??= new();
                foreach (var n in fn.Graph.Nodes)
                {
                    n.Properties ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    n.PinLiterals ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    n.DynamicPins ??= new();
                }
            }

            BlueprintFlowRuntime.NormalizeLegacyPins(doc.Graph);
            foreach (var fn in doc.Functions)
                BlueprintFlowRuntime.NormalizeLegacyPins(fn.Graph);

            if (doc.Version < 2)
                MigrateV1ToV2(doc);

            return doc;
        }

        /// <summary>Maps v1 string-property nodes onto pin literals; marks all wires as exec.</summary>
        public static void MigrateV1ToV2(BlueprintDocument doc)
        {
            MigrateGraph(doc.Graph);
            foreach (var fn in doc.Functions)
                MigrateGraph(fn.Graph);
            doc.Version = 2;
        }

        static void MigrateGraph(BlueprintGraph graph)
        {
            foreach (var w in graph.Wires)
            {
                if (w.Kind == BlueprintWireKind.Data) continue;
                if (BlueprintFlowRuntime.IsExecOutPin(w.FromPin) && BlueprintFlowRuntime.IsExecInPin(w.ToPin))
                    w.Kind = BlueprintWireKind.Exec;
            }

            foreach (var n in graph.Nodes)
            {
                n.PinLiterals ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                void Copy(string propKey, string pinId)
                {
                    if (n.Properties.TryGetValue(propKey, out var v) && !n.PinLiterals.ContainsKey(pinId))
                        n.PinLiterals[pinId] = v;
                }

                switch (n.Kind)
                {
                    case "Branch":
                        Copy("conditionKey", "condition"); // v1 used var key; keep as literal hint
                        break;
                    case "BranchEquals":
                        Copy("conditionKey", "a");
                        Copy("equalsValue", "b");
                        break;
                    case "BranchCompare":
                        Copy("conditionKey", "a");
                        Copy("compareValue", "b");
                        Copy("compareOp", "op");
                        break;
                    case "RandomBranch":
                        Copy("chance", "chance");
                        break;
                    case "Delay":
                        Copy("seconds", "seconds");
                        break;
                    case "SetVariable":
                        Copy("varKey", "varName");
                        Copy("varValue", "value");
                        if (n.Properties.TryGetValue("varKey", out var vk) && string.IsNullOrEmpty(n.VariableName))
                            n.VariableName = vk;
                        break;
                    case "LogMessage":
                    case "Call":
                    case "Math":
                        Copy("message", "message");
                        break;
                    case "SetObjectActive":
                        Copy("active", "active");
                        break;
                    case "SetOtherObjectActive":
                        Copy("targetPath", "target");
                        Copy("targetName", "target");
                        Copy("active", "active");
                        break;
                    case "SetObjectPosition":
                    case "SetObjectRotation":
                        if (n.Properties.TryGetValue("x", out var x)
                            && n.Properties.TryGetValue("y", out var y)
                            && n.Properties.TryGetValue("z", out var z)
                            && !n.PinLiterals.ContainsKey("value"))
                            n.PinLiterals["value"] = $"{x},{y},{z}";
                        Copy("relative", "relative");
                        break;
                    case "SetOtherObjectPosition":
                    case "SetOtherObjectRotation":
                        Copy("targetPath", "target");
                        Copy("targetName", "target");
                        if (n.Properties.TryGetValue("x", out var ox)
                            && n.Properties.TryGetValue("y", out var oy)
                            && n.Properties.TryGetValue("z", out var oz)
                            && !n.PinLiterals.ContainsKey("value"))
                            n.PinLiterals["value"] = $"{ox},{oy},{oz}";
                        Copy("relative", "relative");
                        break;
                    case "DestroyObject":
                        Copy("scope", "scope");
                        Copy("targetPath", "target");
                        Copy("targetName", "target");
                        break;
                    case "FireBlueprintEvent":
                        Copy("eventName", "eventName");
                        Copy("payload", "payload");
                        break;
                    case "IncrementVariable":
                        Copy("varKey", "varName");
                        Copy("delta", "delta");
                        break;
                    case "MultiplyVariable":
                        Copy("varKey", "varName");
                        Copy("factor", "factor");
                        break;
                    case "ClearVariable":
                    case "StoreGameTime":
                    case "StoreObjectName":
                        Copy("varKey", "varName");
                        break;
                    case "CopyVariable":
                        Copy("fromKey", "from");
                        Copy("toKey", "to");
                        break;
                    case "AppendVariable":
                        Copy("varKey", "varName");
                        Copy("text", "text");
                        break;
                    case "ReflectGet":
                        Copy("varKey", "result");
                        break;
                    case "ReflectSet":
                        Copy("value", "value");
                        Copy("valueVarKey", "value");
                        break;
                }
            }
        }

        public static string? TryGetDisplayPath(string? absPath, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(absPath)) return null;
            try
            {
                var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(absPath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return Path.GetRelativePath(root, full).Replace('\\', '/');
            }
            catch { /* ignore */ }
            return absPath;
        }

        /// <summary>Serialize a document snapshot for undo stacks.</summary>
        public static string SnapshotJson(BlueprintDocument doc) =>
            JsonSerializer.Serialize(doc, JsonOptions);

        public static BlueprintDocument CloneFromSnapshot(string json)
        {
            var doc = JsonSerializer.Deserialize<BlueprintDocument>(json, JsonOptions)
                      ?? new BlueprintDocument();
            doc.Graph ??= new();
            doc.Variables ??= new();
            doc.Functions ??= new();
            return doc;
        }
    }
}
