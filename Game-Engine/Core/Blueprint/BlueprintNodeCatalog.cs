#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Blueprint
{
    public enum BlueprintNodeCategory
    {
        Comment,
        Event,
        Flow,
        Action,
    }

    /// <summary>Authoring template for visual behavior nodes (Blueprint-style).</summary>
    public sealed class BlueprintNodeTemplate
    {
        public string Kind { get; init; } = "";
        public BlueprintNodeCategory Category { get; init; }
        /// <summary>RGB header bar (dark theme).</summary>
        public byte HeaderR { get; init; }
        public byte HeaderG { get; init; }
        public byte HeaderB { get; init; }
        public int ExecIn { get; init; }
        public int ExecOut { get; init; }
        public string DefaultTitle { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>Initial <see cref="BlueprintNode.Properties"/> entries.</summary>
        public IReadOnlyDictionary<string, string> DefaultProperties { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Property keys shown in the inspector column (values in Properties).</summary>
        public string[] EditablePropertyKeys { get; init; } = Array.Empty<string>();
        /// <summary>If non-empty, length must match <see cref="ExecOut"/> when <see cref="ExecOut"/> &gt; 1. Wires use these as <see cref="BlueprintWire.FromPin"/>.</summary>
        public string[] ExecOutPinNames { get; init; } = Array.Empty<string>();
    }

    /// <summary>Built-in node kinds for visual behaviors.</summary>
    public static class BlueprintNodeCatalog
    {
        static readonly Dictionary<string, BlueprintNodeTemplate> ByKind = Create();

        static Dictionary<string, BlueprintNodeTemplate> Create()
        {
            var d = new Dictionary<string, BlueprintNodeTemplate>(StringComparer.OrdinalIgnoreCase)
            {
                ["Comment"] = new BlueprintNodeTemplate
                {
                    Kind = "Comment",
                    Category = BlueprintNodeCategory.Comment,
                    HeaderR = 0x55, HeaderG = 0x58, HeaderB = 0x62,
                    ExecIn = 0, ExecOut = 0,
                    DefaultTitle = "Comment",
                    Description = "Note only — not executed or wired.",
                },
                ["BeginPlay"] = new BlueprintNodeTemplate
                {
                    Kind = "BeginPlay",
                    Category = BlueprintNodeCategory.Event,
                    HeaderR = 0x8B, HeaderG = 0x45, HeaderB = 0x1E,
                    ExecIn = 0, ExecOut = 1,
                    DefaultTitle = "Begin Play",
                    Description = "Runs once when the scene starts (via Visual Blueprint on the GameObject).",
                },
                ["Tick"] = new BlueprintNodeTemplate
                {
                    Kind = "Tick",
                    Category = BlueprintNodeCategory.Event,
                    HeaderR = 0x8B, HeaderG = 0x45, HeaderB = 0x1E,
                    ExecIn = 0, ExecOut = 1,
                    DefaultTitle = "Tick",
                    Description = "Runs every frame while the component updates (can be disabled on the behavior).",
                },
                ["Sequence"] = new BlueprintNodeTemplate
                {
                    Kind = "Sequence",
                    Category = BlueprintNodeCategory.Flow,
                    HeaderR = 0x2E, HeaderG = 0x4A, HeaderB = 0x7A,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Sequence",
                    Description = "Passes execution to the next node.",
                },
                ["Branch"] = new BlueprintNodeTemplate
                {
                    Kind = "Branch",
                    Category = BlueprintNodeCategory.Flow,
                    HeaderR = 0x2E, HeaderG = 0x4A, HeaderB = 0x7A,
                    ExecIn = 1,
                    ExecOut = 2,
                    ExecOutPinNames = new[] { "Then", "Else" },
                    DefaultTitle = "Branch (Bool var)",
                    Description = "Reads a boolean from the blueprint Variables map (Set Variable node). Top out = Then if true, bottom = Else.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["conditionKey"] = "flag",
                    },
                    EditablePropertyKeys = new[] { "conditionKey" },
                },
                ["Delay"] = new BlueprintNodeTemplate
                {
                    Kind = "Delay",
                    Category = BlueprintNodeCategory.Flow,
                    HeaderR = 0x2E, HeaderG = 0x4A, HeaderB = 0x7A,
                    ExecIn = 1,
                    ExecOut = 1,
                    DefaultTitle = "Delay",
                    Description = "Waits (seconds) using game time, then continues to the next node.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["seconds"] = "1",
                    },
                    EditablePropertyKeys = new[] { "seconds" },
                },
                ["SetVariable"] = new BlueprintNodeTemplate
                {
                    Kind = "SetVariable",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1,
                    ExecOut = 1,
                    DefaultTitle = "Set Variable",
                    Description = "Stores a string on this graph's runner (Visual Blueprint). Use Branch conditionKey to read.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["varKey"] = "flag",
                        ["varValue"] = "true",
                    },
                    EditablePropertyKeys = new[] { "varKey", "varValue" },
                },
                ["IncrementVariable"] = new BlueprintNodeTemplate
                {
                    Kind = "IncrementVariable",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1,
                    ExecOut = 1,
                    DefaultTitle = "Add To Number",
                    Description = "Parses the variable as a number (missing = 0), adds delta, writes back as text.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["varKey"] = "counter",
                        ["delta"] = "1",
                    },
                    EditablePropertyKeys = new[] { "varKey", "delta" },
                },
                ["MultiplyVariable"] = new BlueprintNodeTemplate
                {
                    Kind = "MultiplyVariable",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Multiply Number",
                    Description = "Parses variable as number (missing = 0), multiplies by factor, writes back.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["varKey"] = "counter",
                        ["factor"] = "2",
                    },
                    EditablePropertyKeys = new[] { "varKey", "factor" },
                },
                ["ClearVariable"] = new BlueprintNodeTemplate
                {
                    Kind = "ClearVariable",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Clear Variable",
                    Description = "Removes varKey from the Variables map if present.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["varKey"] = "temp",
                    },
                    EditablePropertyKeys = new[] { "varKey" },
                },
                ["CopyVariable"] = new BlueprintNodeTemplate
                {
                    Kind = "CopyVariable",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Copy Variable",
                    Description = "Sets toKey to the string value of fromKey (empty if missing).",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["fromKey"] = "src",
                        ["toKey"] = "dst",
                    },
                    EditablePropertyKeys = new[] { "fromKey", "toKey" },
                },
                ["AppendVariable"] = new BlueprintNodeTemplate
                {
                    Kind = "AppendVariable",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Append Text",
                    Description = "Appends text to Variables[varKey] (starts empty if unset).",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["varKey"] = "buffer",
                        ["text"] = "_",
                    },
                    EditablePropertyKeys = new[] { "varKey", "text" },
                },
                ["StoreGameTime"] = new BlueprintNodeTemplate
                {
                    Kind = "StoreGameTime",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Store Game Time",
                    Description = "Writes Time.time (seconds) as text into varKey.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["varKey"] = "time",
                    },
                    EditablePropertyKeys = new[] { "varKey" },
                },
                ["StoreObjectName"] = new BlueprintNodeTemplate
                {
                    Kind = "StoreObjectName",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Store Object Name",
                    Description = "Writes this GameObject's name into varKey.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["varKey"] = "who",
                    },
                    EditablePropertyKeys = new[] { "varKey" },
                },
                ["FireBlueprintEvent"] = new BlueprintNodeTemplate
                {
                    Kind = "FireBlueprintEvent",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Fire Event",
                    Description = "Publishes BlueprintMessageEvent on EventBus (subscribe in C#).",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["eventName"] = "MySignal",
                        ["payload"] = "",
                    },
                    EditablePropertyKeys = new[] { "eventName", "payload" },
                },
                ["RandomBranch"] = new BlueprintNodeTemplate
                {
                    Kind = "RandomBranch",
                    Category = BlueprintNodeCategory.Flow,
                    HeaderR = 0x2E, HeaderG = 0x4A, HeaderB = 0x7A,
                    ExecIn = 1,
                    ExecOut = 2,
                    ExecOutPinNames = new[] { "Then", "Else" },
                    DefaultTitle = "Random Branch",
                    Description = "Each run: Then with probability chance (0–1), else Else.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["chance"] = "0.5",
                    },
                    EditablePropertyKeys = new[] { "chance" },
                },
                ["BranchEquals"] = new BlueprintNodeTemplate
                {
                    Kind = "BranchEquals",
                    Category = BlueprintNodeCategory.Flow,
                    HeaderR = 0x2E, HeaderG = 0x4A, HeaderB = 0x7A,
                    ExecIn = 1,
                    ExecOut = 2,
                    ExecOutPinNames = new[] { "Then", "Else" },
                    DefaultTitle = "Branch (String =)",
                    Description = "Then if Variables[conditionKey] equals equalsValue (ignore case, trimmed). Else otherwise.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["conditionKey"] = "state",
                        ["equalsValue"] = "ready",
                    },
                    EditablePropertyKeys = new[] { "conditionKey", "equalsValue" },
                },
                ["BranchCompare"] = new BlueprintNodeTemplate
                {
                    Kind = "BranchCompare",
                    Category = BlueprintNodeCategory.Flow,
                    HeaderR = 0x2E, HeaderG = 0x4A, HeaderB = 0x7A,
                    ExecIn = 1,
                    ExecOut = 2,
                    ExecOutPinNames = new[] { "Then", "Else" },
                    DefaultTitle = "Branch (Number)",
                    Description = "Parses Variables[conditionKey] as a number (missing = 0). Then if comparison to compareValue succeeds. compareOp: Lt Lte Eq Gte Gt.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["conditionKey"] = "score",
                        ["compareOp"] = "Gte",
                        ["compareValue"] = "10",
                    },
                    EditablePropertyKeys = new[] { "conditionKey", "compareOp", "compareValue" },
                },
                ["LogMessage"] = new BlueprintNodeTemplate
                {
                    Kind = "LogMessage",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Print String",
                    Description = "Writes a message to the console.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["message"] = "Hello",
                    },
                    EditablePropertyKeys = new[] { "message" },
                },
                ["SetObjectActive"] = new BlueprintNodeTemplate
                {
                    Kind = "SetObjectActive",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Set GameObject Active",
                    Description = "Enables or disables this GameObject (same as Enabled in hierarchy).",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["active"] = "true",
                    },
                    EditablePropertyKeys = new[] { "active" },
                },
                ["SetOtherObjectActive"] = new BlueprintNodeTemplate
                {
                    Kind = "SetOtherObjectActive",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Set Other Object Active",
                    Description = "Finds an object by hierarchy path (Root/Child/Leaf) or by first matching name in scene, then sets Enabled.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["targetPath"] = "",
                        ["targetName"] = "TargetObject",
                        ["active"] = "true",
                    },
                    EditablePropertyKeys = new[] { "targetPath", "targetName", "active" },
                },
                ["SetObjectPosition"] = new BlueprintNodeTemplate
                {
                    Kind = "SetObjectPosition",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Set This Position",
                    Description = "Sets this object's Transform.Position (or adds if relative=true).",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["x"] = "0",
                        ["y"] = "0",
                        ["z"] = "0",
                        ["relative"] = "false",
                    },
                    EditablePropertyKeys = new[] { "x", "y", "z", "relative" },
                },
                ["SetOtherObjectPosition"] = new BlueprintNodeTemplate
                {
                    Kind = "SetOtherObjectPosition",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Set Other Position",
                    Description = "Resolves target by path or name, then sets Position.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["targetPath"] = "",
                        ["targetName"] = "TargetObject",
                        ["x"] = "0",
                        ["y"] = "0",
                        ["z"] = "0",
                        ["relative"] = "false",
                    },
                    EditablePropertyKeys = new[] { "targetPath", "targetName", "x", "y", "z", "relative" },
                },
                ["SetObjectRotation"] = new BlueprintNodeTemplate
                {
                    Kind = "SetObjectRotation",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Set This Rotation",
                    Description = "Sets this object's Transform.Rotation degrees (Euler X/Y/Z), or adds if relative=true.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["x"] = "0",
                        ["y"] = "0",
                        ["z"] = "0",
                        ["relative"] = "false",
                    },
                    EditablePropertyKeys = new[] { "x", "y", "z", "relative" },
                },
                ["SetOtherObjectRotation"] = new BlueprintNodeTemplate
                {
                    Kind = "SetOtherObjectRotation",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Set Other Rotation",
                    Description = "Resolves target by path or name, then sets Rotation (Euler degrees).",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["targetPath"] = "",
                        ["targetName"] = "TargetObject",
                        ["x"] = "0",
                        ["y"] = "0",
                        ["z"] = "0",
                        ["relative"] = "false",
                    },
                    EditablePropertyKeys = new[] { "targetPath", "targetName", "x", "y", "z", "relative" },
                },
                ["DestroyObject"] = new BlueprintNodeTemplate
                {
                    Kind = "DestroyObject",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x5C, HeaderG = 0x2E, HeaderB = 0x2E,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Destroy Object",
                    Description = "scope Self = destroy this object (and children). Other = resolve by path/name. Runs behavior OnDestroy and removes from scene.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["scope"] = "Other",
                        ["targetPath"] = "",
                        ["targetName"] = "TargetObject",
                    },
                    EditablePropertyKeys = new[] { "scope", "targetPath", "targetName" },
                },
                ["ReflectGet"] = new BlueprintNodeTemplate
                {
                    Kind = "ReflectGet",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x4A, HeaderG = 0x3D, HeaderB = 0x6E,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Get Property (Reflect)",
                    Description = "Instance: mode Instance, scope, componentType (GameObject, Transform, or behavior type name), memberPath e.g. Position.X. Static: mode Static, typeName e.g. Game_Engine.Core.Time, memberPath e.g. time. Writes string into varKey.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = "Instance",
                        ["scope"] = "Self",
                        ["targetPath"] = "",
                        ["targetName"] = "",
                        ["typeName"] = "Game_Engine.Core.Time",
                        ["componentType"] = "Transform",
                        ["memberPath"] = "Position.X",
                        ["varKey"] = "rx",
                    },
                    EditablePropertyKeys = new[] { "mode", "scope", "targetPath", "targetName", "typeName", "componentType", "memberPath", "varKey" },
                },
                ["ReflectSet"] = new BlueprintNodeTemplate
                {
                    Kind = "ReflectSet",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x4A, HeaderG = 0x3D, HeaderB = 0x6E,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Set Property (Reflect)",
                    Description = "Same targeting as Get. Set value from non-empty \"value\" else from Variables[valueVarKey]. Public writable fields/properties only (private setters fail). Vector3: x;y;z",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = "Instance",
                        ["scope"] = "Self",
                        ["targetPath"] = "",
                        ["targetName"] = "",
                        ["typeName"] = "",
                        ["componentType"] = "Transform",
                        ["memberPath"] = "Position.X",
                        ["value"] = "0",
                        ["valueVarKey"] = "",
                    },
                    EditablePropertyKeys = new[] { "mode", "scope", "targetPath", "targetName", "typeName", "componentType", "memberPath", "value", "valueVarKey" },
                },
                // ---- Legacy spike kinds (older graphs) ----
                ["Event"] = new BlueprintNodeTemplate
                {
                    Kind = "Event",
                    Category = BlueprintNodeCategory.Event,
                    HeaderR = 0x7A, HeaderG = 0x52, HeaderB = 0x1C,
                    ExecIn = 0, ExecOut = 1,
                    DefaultTitle = "Event",
                    Description = "Legacy event node.",
                },
                ["Call"] = new BlueprintNodeTemplate
                {
                    Kind = "Call",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Call",
                    Description = "Legacy — treated like Print at runtime.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["message"] = "Call" },
                    EditablePropertyKeys = new[] { "message" },
                },
                ["Math"] = new BlueprintNodeTemplate
                {
                    Kind = "Math",
                    Category = BlueprintNodeCategory.Action,
                    HeaderR = 0x2D, HeaderG = 0x6A, HeaderB = 0x3F,
                    ExecIn = 1, ExecOut = 1,
                    DefaultTitle = "Math",
                    Description = "Legacy placeholder.",
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["message"] = "Math" },
                    EditablePropertyKeys = new[] { "message" },
                },
            };
            return d;
        }

        public static BlueprintNodeTemplate Resolve(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
                kind = "Comment";
            if (ByKind.TryGetValue(kind, out var t)) return t;
            return new BlueprintNodeTemplate
            {
                Kind = kind,
                Category = BlueprintNodeCategory.Action,
                HeaderR = 0x3D, HeaderG = 0x40, HeaderB = 0x48,
                ExecIn = 1,
                ExecOut = 1,
                DefaultTitle = kind,
                Description = "Custom / unknown kind — treated as pass-through action at runtime.",
            };
        }

        public static IEnumerable<BlueprintNodeTemplate> PaletteNodes() => ByKind.Values;

        /// <summary>Ordered kinds shown in the Blueprint editor Insert menu and quick-add combo.</summary>
        public static readonly string[] AuthoringPaletteOrdered =
        {
            "BeginPlay",
            "Tick",
            "Sequence",
            "Branch",
            "BranchEquals",
            "BranchCompare",
            "RandomBranch",
            "Delay",
            "SetVariable",
            "CopyVariable",
            "AppendVariable",
            "IncrementVariable",
            "MultiplyVariable",
            "ClearVariable",
            "StoreGameTime",
            "StoreObjectName",
            "LogMessage",
            "FireBlueprintEvent",
            "SetObjectActive",
            "SetOtherObjectActive",
            "SetObjectPosition",
            "SetOtherObjectPosition",
            "SetObjectRotation",
            "SetOtherObjectRotation",
            "DestroyObject",
            "ReflectGet",
            "ReflectSet",
            "Comment",
        };

        /// <summary>Pin names for <see cref="BlueprintWire.FromPin"/> on outbound exec links.</summary>
        public static IReadOnlyList<string> OutboundExecPinNames(BlueprintNodeTemplate t)
        {
            if (t.ExecOut <= 0) return Array.Empty<string>();
            if (t.ExecOutPinNames.Length == t.ExecOut) return t.ExecOutPinNames;
            return new[] { BlueprintFlowRuntime.PinExecOut };
        }
    }
}
