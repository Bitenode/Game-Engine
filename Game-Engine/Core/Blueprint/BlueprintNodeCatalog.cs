#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game_Engine.Core.Blueprint
{
    public enum BlueprintNodeCategory
    {
        Comment,
        Event,
        Flow,
        Action,
        Pure,
        Variables,
    }

    /// <summary>Authoring template for visual behavior nodes.</summary>
    public sealed class BlueprintNodeTemplate
    {
        public string Kind { get; init; } = "";
        public BlueprintNodeCategory Category { get; init; }
        public byte HeaderR { get; init; }
        public byte HeaderG { get; init; }
        public byte HeaderB { get; init; }
        public string DefaultTitle { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>True = no exec pins; evaluated when data is pulled.</summary>
        public bool IsPure { get; init; }
        public IReadOnlyList<BlueprintPinDef> Pins { get; init; } = Array.Empty<BlueprintPinDef>();
        /// <summary>Legacy: count of exec inputs (derived from Pins when present).</summary>
        public int ExecIn => Pins.Count(p => p.Type == BlueprintPinType.Exec && p.Direction == BlueprintPinDirection.Input);
        public int ExecOut => Pins.Count(p => p.Type == BlueprintPinType.Exec && p.Direction == BlueprintPinDirection.Output);
        public string[] ExecOutPinNames => Pins
            .Where(p => p.Type == BlueprintPinType.Exec && p.Direction == BlueprintPinDirection.Output)
            .Select(p => p.Id)
            .ToArray();
        public IReadOnlyDictionary<string, string> DefaultProperties { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string[] EditablePropertyKeys { get; init; } = Array.Empty<string>();
        /// <summary>Palette group for context menu.</summary>
        public string PaletteGroup { get; init; } = "Action";
    }

    /// <summary>Built-in node kinds for visual behaviors.</summary>
    public static class BlueprintNodeCatalog
    {
        static readonly Dictionary<string, BlueprintNodeTemplate> ByKind = Create();

        static BlueprintPinDef ExecIn(string id = "ExecIn", string name = "exec") => new()
        {
            Id = id, DisplayName = name, Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Input
        };

        static BlueprintPinDef ExecOut(string id = "ExecOut", string name = "exec") => new()
        {
            Id = id, DisplayName = name, Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Output
        };

        static BlueprintPinDef In(string id, BlueprintPinType t, string name, string def = "") => new()
        {
            Id = id, DisplayName = name, Type = t, Direction = BlueprintPinDirection.Input, DefaultLiteral = def
        };

        static BlueprintPinDef Out(string id, BlueprintPinType t, string name) => new()
        {
            Id = id, DisplayName = name, Type = t, Direction = BlueprintPinDirection.Output
        };

        static BlueprintNodeTemplate T(
            string kind, BlueprintNodeCategory cat, string title, string desc,
            byte r, byte g, byte b, bool pure, string group, params BlueprintPinDef[] pins) => new()
        {
            Kind = kind,
            Category = cat,
            DefaultTitle = title,
            Description = desc,
            HeaderR = r, HeaderG = g, HeaderB = b,
            IsPure = pure,
            PaletteGroup = group,
            Pins = pins,
        };

        static Dictionary<string, BlueprintNodeTemplate> Create()
        {
            var flow = (byte)0x2E; var flowG = (byte)0x4A; var flowB = (byte)0x7A;
            var act = (byte)0x2D; var actG = (byte)0x6A; var actB = (byte)0x3F;
            var ev = (byte)0x8B; var evG = (byte)0x45; var evB = (byte)0x1E;
            var pure = (byte)0x4A; var pureG = (byte)0x6A; var pureB = (byte)0x8A;
            var varC = (byte)0x3A; var varG = (byte)0x5A; var varB = (byte)0x9A;
            var refl = (byte)0x4A; var reflG = (byte)0x3D; var reflB = (byte)0x6E;
            var dang = (byte)0x5C; var dangG = (byte)0x2E; var dangB = (byte)0x2E;

            var d = new Dictionary<string, BlueprintNodeTemplate>(StringComparer.OrdinalIgnoreCase)
            {
                ["Comment"] = T("Comment", BlueprintNodeCategory.Comment, "Comment",
                    "Note only — not executed or wired.", 0x55, 0x58, 0x62, true, "Comment"),

                ["BeginPlay"] = T("BeginPlay", BlueprintNodeCategory.Event, "Begin Play",
                    "Runs once when the scene starts.", ev, evG, evB, false, "Events",
                    ExecOut()),

                ["Tick"] = T("Tick", BlueprintNodeCategory.Event, "Tick",
                    "Runs every frame. DeltaSeconds is available on the data pin.", ev, evG, evB, false, "Events",
                    ExecOut(), Out("deltaSeconds", BlueprintPinType.Float, "Delta Seconds")),

                ["CustomEvent"] = T("CustomEvent", BlueprintNodeCategory.Event, "Custom Event",
                    "Entry point callable by name (Call Custom Event). Set eventName on the node.", ev, evG, evB, false, "Events",
                    ExecOut()),

                ["InputKey"] = T("InputKey", BlueprintNodeCategory.Event, "Input Key",
                    "Fires when the key is pressed (GetKeyDown). Set keyCode pin literal (e.g. Space).", ev, evG, evB, false, "Events",
                    ExecOut(), In("keyCode", BlueprintPinType.String, "Key", "Space")),

                ["InputAction"] = T("InputAction", BlueprintNodeCategory.Event, "Input Action",
                    "Fires when the named input action goes down.", ev, evG, evB, false, "Events",
                    ExecOut(), In("actionName", BlueprintPinType.String, "Action", "Jump")),

                ["TriggerEnter"] = T("TriggerEnter", BlueprintNodeCategory.Event, "Trigger Enter",
                    "OnTriggerEnter — Other is the overlapping object.", ev, evG, evB, false, "Events",
                    ExecOut(), Out("other", BlueprintPinType.Object, "Other")),

                ["TriggerStay"] = T("TriggerStay", BlueprintNodeCategory.Event, "Trigger Stay",
                    "OnTriggerStay while overlap continues.", ev, evG, evB, false, "Events",
                    ExecOut(), Out("other", BlueprintPinType.Object, "Other")),

                ["TriggerExit"] = T("TriggerExit", BlueprintNodeCategory.Event, "Trigger Exit",
                    "OnTriggerExit when overlap ends.", ev, evG, evB, false, "Events",
                    ExecOut(), Out("other", BlueprintPinType.Object, "Other")),

                ["Sequence"] = T("Sequence", BlueprintNodeCategory.Flow, "Sequence",
                    "Fires Then0, Then1, … in order. Add Then pins via DynamicPins (Then0, Then1 by default).",
                    flow, flowG, flowB, false, "Flow",
                    ExecIn(), ExecOut("Then0", "Then 0"), ExecOut("Then1", "Then 1")),

                ["Branch"] = T("Branch", BlueprintNodeCategory.Flow, "Branch",
                    "Then if Condition is true, else Else.", flow, flowG, flowB, false, "Flow",
                    ExecIn(), ExecOut("Then", "Then"), ExecOut("Else", "Else"),
                    In("condition", BlueprintPinType.Bool, "Condition", "true")),

                ["BranchEquals"] = T("BranchEquals", BlueprintNodeCategory.Flow, "Branch (String =)",
                    "Then if A equals B (ignore case).", flow, flowG, flowB, false, "Flow",
                    ExecIn(), ExecOut("Then", "Then"), ExecOut("Else", "Else"),
                    In("a", BlueprintPinType.String, "A", ""), In("b", BlueprintPinType.String, "B", "")),

                ["BranchCompare"] = T("BranchCompare", BlueprintNodeCategory.Flow, "Branch (Number)",
                    "Then if A compareOp B. Op: Lt Lte Eq Gte Gt.", flow, flowG, flowB, false, "Flow",
                    ExecIn(), ExecOut("Then", "Then"), ExecOut("Else", "Else"),
                    In("a", BlueprintPinType.Float, "A", "0"),
                    In("op", BlueprintPinType.String, "Op", "Gte"),
                    In("b", BlueprintPinType.Float, "B", "0")),

                ["RandomBranch"] = T("RandomBranch", BlueprintNodeCategory.Flow, "Random Branch",
                    "Then with probability Chance (0–1).", flow, flowG, flowB, false, "Flow",
                    ExecIn(), ExecOut("Then", "Then"), ExecOut("Else", "Else"),
                    In("chance", BlueprintPinType.Float, "Chance", "0.5")),

                ["Delay"] = T("Delay", BlueprintNodeCategory.Flow, "Delay",
                    "Latent: wait Seconds (game time), then continue.", flow, flowG, flowB, false, "Flow",
                    ExecIn(), ExecOut(), In("seconds", BlueprintPinType.Float, "Seconds", "1")),

                ["ForLoop"] = T("ForLoop", BlueprintNodeCategory.Flow, "For Loop",
                    "Runs Loop Body for Index from First to Last inclusive, then Completed.", flow, flowG, flowB, false, "Flow",
                    ExecIn(),
                    ExecOut("loopBody", "Loop Body"),
                    ExecOut("completed", "Completed"),
                    In("first", BlueprintPinType.Int, "First", "0"),
                    In("last", BlueprintPinType.Int, "Last", "3"),
                    Out("index", BlueprintPinType.Int, "Index")),

                ["DoOnce"] = T("DoOnce", BlueprintNodeCategory.Flow, "Do Once",
                    "Passes execution once; further calls are ignored until Reset.", flow, flowG, flowB, false, "Flow",
                    ExecIn("ExecIn", "In"), ExecIn("reset", "Reset"), ExecOut()),

                ["Gate"] = T("Gate", BlueprintNodeCategory.Flow, "Gate",
                    "When open, Enter passes to Exit. Open/Close control the gate.", flow, flowG, flowB, false, "Flow",
                    ExecIn("enter", "Enter"), ExecIn("open", "Open"), ExecIn("close", "Close"),
                    ExecOut("exit", "Exit"),
                    In("startClosed", BlueprintPinType.Bool, "Start Closed", "false")),

                // ---- Variables ----
                ["GetVar"] = T("GetVar", BlueprintNodeCategory.Pure, "Get Variable",
                    "Pure read of a typed graph variable (set VariableName on the node).", varC, varG, varB, true, "Variables",
                    Out("value", BlueprintPinType.Float, "Value")),

                ["SetVar"] = T("SetVar", BlueprintNodeCategory.Variables, "Set Variable",
                    "Writes a typed graph variable.", varC, varG, varB, false, "Variables",
                    ExecIn(), ExecOut(), In("value", BlueprintPinType.Float, "Value", "0")),

                // Legacy string-map helpers (still supported)
                ["SetVariable"] = T("SetVariable", BlueprintNodeCategory.Action, "Set Variable (String Map)",
                    "Legacy: stores a string on the runner Variables map.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(),
                    In("varName", BlueprintPinType.String, "Key", "flag"),
                    In("value", BlueprintPinType.String, "Value", "true")),

                ["CopyVariable"] = T("CopyVariable", BlueprintNodeCategory.Action, "Copy Variable",
                    "Legacy string map: copy from → to.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(),
                    In("from", BlueprintPinType.String, "From", "src"),
                    In("to", BlueprintPinType.String, "To", "dst")),

                ["AppendVariable"] = T("AppendVariable", BlueprintNodeCategory.Action, "Append Text",
                    "Legacy string map append.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(),
                    In("varName", BlueprintPinType.String, "Key", "buffer"),
                    In("text", BlueprintPinType.String, "Text", "_")),

                ["IncrementVariable"] = T("IncrementVariable", BlueprintNodeCategory.Action, "Add To Number",
                    "Legacy string map numeric add.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(),
                    In("varName", BlueprintPinType.String, "Key", "counter"),
                    In("delta", BlueprintPinType.Float, "Delta", "1")),

                ["MultiplyVariable"] = T("MultiplyVariable", BlueprintNodeCategory.Action, "Multiply Number",
                    "Legacy string map numeric multiply.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(),
                    In("varName", BlueprintPinType.String, "Key", "counter"),
                    In("factor", BlueprintPinType.Float, "Factor", "2")),

                ["ClearVariable"] = T("ClearVariable", BlueprintNodeCategory.Action, "Clear Variable",
                    "Legacy: remove key from string map.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(), In("varName", BlueprintPinType.String, "Key", "temp")),

                ["StoreGameTime"] = T("StoreGameTime", BlueprintNodeCategory.Action, "Store Game Time",
                    "Writes Time.time into the string map.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(), In("varName", BlueprintPinType.String, "Key", "time")),

                ["StoreObjectName"] = T("StoreObjectName", BlueprintNodeCategory.Action, "Store Object Name",
                    "Writes this object's name into the string map.", act, actG, actB, false, "Variables",
                    ExecIn(), ExecOut(), In("varName", BlueprintPinType.String, "Key", "who")),

                // ---- Pure math / string ----
                ["AddFloat"] = T("AddFloat", BlueprintNodeCategory.Pure, "Add Float",
                    "A + B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "0"),
                    Out("result", BlueprintPinType.Float, "Return")),

                ["SubtractFloat"] = T("SubtractFloat", BlueprintNodeCategory.Pure, "Subtract Float",
                    "A − B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "0"),
                    Out("result", BlueprintPinType.Float, "Return")),

                ["MultiplyFloat"] = T("MultiplyFloat", BlueprintNodeCategory.Pure, "Multiply Float",
                    "A × B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "1"),
                    Out("result", BlueprintPinType.Float, "Return")),

                ["DivideFloat"] = T("DivideFloat", BlueprintNodeCategory.Pure, "Divide Float",
                    "A ÷ B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "1"),
                    Out("result", BlueprintPinType.Float, "Return")),

                ["LessFloat"] = T("LessFloat", BlueprintNodeCategory.Pure, "Float <",
                    "A < B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "0"),
                    Out("result", BlueprintPinType.Bool, "Return")),

                ["LessEqualFloat"] = T("LessEqualFloat", BlueprintNodeCategory.Pure, "Float ≤",
                    "A ≤ B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "0"),
                    Out("result", BlueprintPinType.Bool, "Return")),

                ["GreaterFloat"] = T("GreaterFloat", BlueprintNodeCategory.Pure, "Float >",
                    "A > B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "0"),
                    Out("result", BlueprintPinType.Bool, "Return")),

                ["GreaterEqualFloat"] = T("GreaterEqualFloat", BlueprintNodeCategory.Pure, "Float ≥",
                    "A ≥ B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "0"),
                    Out("result", BlueprintPinType.Bool, "Return")),

                ["EqualFloat"] = T("EqualFloat", BlueprintNodeCategory.Pure, "Float ==",
                    "A ≈ B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Float, "A", "0"), In("b", BlueprintPinType.Float, "B", "0"),
                    Out("result", BlueprintPinType.Bool, "Return")),

                ["AddVector"] = T("AddVector", BlueprintNodeCategory.Pure, "Add Vector",
                    "A + B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.Vector, "A", "0,0,0"), In("b", BlueprintPinType.Vector, "B", "0,0,0"),
                    Out("result", BlueprintPinType.Vector, "Return")),

                ["ScaleVector"] = T("ScaleVector", BlueprintNodeCategory.Pure, "Scale Vector",
                    "V × Scale", pure, pureG, pureB, true, "Math",
                    In("v", BlueprintPinType.Vector, "V", "0,0,0"), In("scale", BlueprintPinType.Float, "Scale", "1"),
                    Out("result", BlueprintPinType.Vector, "Return")),

                ["AppendString"] = T("AppendString", BlueprintNodeCategory.Pure, "Append String",
                    "A + B", pure, pureG, pureB, true, "Math",
                    In("a", BlueprintPinType.String, "A", ""), In("b", BlueprintPinType.String, "B", ""),
                    Out("result", BlueprintPinType.String, "Return")),

                ["MakeVector"] = T("MakeVector", BlueprintNodeCategory.Pure, "Make Vector",
                    "Build XYZ", pure, pureG, pureB, true, "Math",
                    In("x", BlueprintPinType.Float, "X", "0"),
                    In("y", BlueprintPinType.Float, "Y", "0"),
                    In("z", BlueprintPinType.Float, "Z", "0"),
                    Out("result", BlueprintPinType.Vector, "Return")),

                ["BreakVector"] = T("BreakVector", BlueprintNodeCategory.Pure, "Break Vector",
                    "Split XYZ", pure, pureG, pureB, true, "Math",
                    In("v", BlueprintPinType.Vector, "V", "0,0,0"),
                    Out("x", BlueprintPinType.Float, "X"),
                    Out("y", BlueprintPinType.Float, "Y"),
                    Out("z", BlueprintPinType.Float, "Z")),

                ["GetSelf"] = T("GetSelf", BlueprintNodeCategory.Pure, "Get Self",
                    "This GameObject.", pure, pureG, pureB, true, "Scene",
                    Out("self", BlueprintPinType.Object, "Self")),

                ["GetLocation"] = T("GetLocation", BlueprintNodeCategory.Pure, "Get Location",
                    "Transform.Position of Target (default Self).", pure, pureG, pureB, true, "Scene",
                    In("target", BlueprintPinType.Object, "Target", "self"),
                    Out("location", BlueprintPinType.Vector, "Return")),

                ["GetRotation"] = T("GetRotation", BlueprintNodeCategory.Pure, "Get Rotation",
                    "Transform.Rotation (Euler degrees) of Target.", pure, pureG, pureB, true, "Scene",
                    In("target", BlueprintPinType.Object, "Target", "self"),
                    Out("rotation", BlueprintPinType.Vector, "Return")),

                // ---- Actions ----
                ["LogMessage"] = T("LogMessage", BlueprintNodeCategory.Action, "Print String",
                    "Writes a message to the console.", act, actG, actB, false, "Action",
                    ExecIn(), ExecOut(), In("message", BlueprintPinType.String, "In String", "Hello")),

                ["FireBlueprintEvent"] = T("FireBlueprintEvent", BlueprintNodeCategory.Action, "Fire Event",
                    "Publishes BlueprintMessageEvent on EventBus.", act, actG, actB, false, "Action",
                    ExecIn(), ExecOut(),
                    In("eventName", BlueprintPinType.String, "Name", "MySignal"),
                    In("payload", BlueprintPinType.String, "Data", "")),

                ["CallCustomEvent"] = T("CallCustomEvent", BlueprintNodeCategory.Action, "Call Custom Event",
                    "Runs Custom Event nodes with matching name on this graph.", act, actG, actB, false, "Action",
                    ExecIn(), ExecOut(), In("eventName", BlueprintPinType.String, "Name", "MySignal")),

                ["CallFunction"] = T("CallFunction", BlueprintNodeCategory.Action, "Call Function",
                    "Runs a blueprint function by FunctionName. Wire data pins matching the signature.",
                    act, actG, actB, false, "Action",
                    ExecIn(), ExecOut()),

                ["FunctionEntry"] = T("FunctionEntry", BlueprintNodeCategory.Event, "Function Entry",
                    "Start of a function graph.", ev, evG, evB, false, "Events", ExecOut()),

                ["FunctionReturn"] = T("FunctionReturn", BlueprintNodeCategory.Action, "Return Node",
                    "Ends a function and writes return pins.", act, actG, actB, false, "Action",
                    ExecIn()),

                ["SetObjectActive"] = T("SetObjectActive", BlueprintNodeCategory.Action, "Set Active",
                    "Enables or disables Target (default Self).", act, actG, actB, false, "Scene",
                    ExecIn(), ExecOut(),
                    In("target", BlueprintPinType.Object, "Target", "self"),
                    In("active", BlueprintPinType.Bool, "Active", "true")),

                ["SetOtherObjectActive"] = T("SetOtherObjectActive", BlueprintNodeCategory.Action, "Set Other Active",
                    "Legacy: resolve by path/name string.", act, actG, actB, false, "Scene",
                    ExecIn(), ExecOut(),
                    In("target", BlueprintPinType.String, "Target", "TargetObject"),
                    In("active", BlueprintPinType.Bool, "Active", "true")),

                ["SetObjectPosition"] = T("SetObjectPosition", BlueprintNodeCategory.Action, "Set Location",
                    "Sets Transform.Position of Target.", act, actG, actB, false, "Scene",
                    ExecIn(), ExecOut(),
                    In("target", BlueprintPinType.Object, "Target", "self"),
                    In("value", BlueprintPinType.Vector, "Location", "0,0,0"),
                    In("relative", BlueprintPinType.Bool, "Relative", "false")),

                ["SetOtherObjectPosition"] = T("SetOtherObjectPosition", BlueprintNodeCategory.Action, "Set Other Location",
                    "Legacy other position by name/path.", act, actG, actB, false, "Scene",
                    ExecIn(), ExecOut(),
                    In("target", BlueprintPinType.String, "Target", "TargetObject"),
                    In("value", BlueprintPinType.Vector, "Location", "0,0,0"),
                    In("relative", BlueprintPinType.Bool, "Relative", "false")),

                ["SetObjectRotation"] = T("SetObjectRotation", BlueprintNodeCategory.Action, "Set Rotation",
                    "Sets Transform.Rotation (Euler) of Target.", act, actG, actB, false, "Scene",
                    ExecIn(), ExecOut(),
                    In("target", BlueprintPinType.Object, "Target", "self"),
                    In("value", BlueprintPinType.Vector, "Rotation", "0,0,0"),
                    In("relative", BlueprintPinType.Bool, "Relative", "false")),

                ["SetOtherObjectRotation"] = T("SetOtherObjectRotation", BlueprintNodeCategory.Action, "Set Other Rotation",
                    "Legacy other rotation by name/path.", act, actG, actB, false, "Scene",
                    ExecIn(), ExecOut(),
                    In("target", BlueprintPinType.String, "Target", "TargetObject"),
                    In("value", BlueprintPinType.Vector, "Rotation", "0,0,0"),
                    In("relative", BlueprintPinType.Bool, "Relative", "false")),

                ["DestroyObject"] = T("DestroyObject", BlueprintNodeCategory.Action, "Destroy Actor",
                    "Destroys Target (default Self).", dang, dangG, dangB, false, "Scene",
                    ExecIn(), ExecOut(),
                    In("target", BlueprintPinType.Object, "Target", "self")),

                ["ReflectGet"] = T("ReflectGet", BlueprintNodeCategory.Pure, "Get Property (Reflect)",
                    "Pure read of a public field/property. Configure mode/scope/memberPath in Properties.",
                    refl, reflG, reflB, true, "Reflection",
                    Out("result", BlueprintPinType.String, "Value")),

                ["ReflectSet"] = new BlueprintNodeTemplate
                {
                    Kind = "ReflectSet",
                    Category = BlueprintNodeCategory.Action,
                    DefaultTitle = "Set Property (Reflect)",
                    Description = "Write a public field/property from Value pin.",
                    HeaderR = refl, HeaderG = reflG, HeaderB = reflB,
                    IsPure = false,
                    PaletteGroup = "Reflection",
                    Pins = new[]
                    {
                        ExecIn(), ExecOut(), In("value", BlueprintPinType.String, "Value", "0")
                    },
                    EditablePropertyKeys = new[]
                    {
                        "mode", "scope", "targetPath", "targetName", "typeName", "componentType", "memberPath"
                    },
                    DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = "Instance",
                        ["scope"] = "Self",
                        ["targetPath"] = "",
                        ["targetName"] = "",
                        ["typeName"] = "",
                        ["componentType"] = "Transform",
                        ["memberPath"] = "Position.X",
                    },
                },

                ["Event"] = T("Event", BlueprintNodeCategory.Event, "Event",
                    "Legacy event node.", 0x7A, 0x52, 0x1C, false, "Events", ExecOut()),
                ["Call"] = T("Call", BlueprintNodeCategory.Action, "Call",
                    "Legacy — treated like Print.", act, actG, actB, false, "Action",
                    ExecIn(), ExecOut(), In("message", BlueprintPinType.String, "Message", "Call")),
                ["Math"] = T("Math", BlueprintNodeCategory.Action, "Math",
                    "Legacy placeholder.", act, actG, actB, false, "Action",
                    ExecIn(), ExecOut(), In("message", BlueprintPinType.String, "Message", "Math")),
            };

            d["ReflectGet"] = new BlueprintNodeTemplate
            {
                Kind = "ReflectGet",
                Category = BlueprintNodeCategory.Pure,
                DefaultTitle = "Get Property (Reflect)",
                Description = "Pure read of a public field/property. Configure mode/scope/memberPath in Properties.",
                HeaderR = refl, HeaderG = reflG, HeaderB = reflB,
                IsPure = true,
                PaletteGroup = "Reflection",
                Pins = new[] { Out("result", BlueprintPinType.String, "Value") },
                EditablePropertyKeys = new[]
                {
                    "mode", "scope", "targetPath", "targetName", "typeName", "componentType", "memberPath"
                },
                DefaultProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = "Instance",
                    ["scope"] = "Self",
                    ["targetPath"] = "",
                    ["targetName"] = "",
                    ["typeName"] = "Game_Engine.Core.Time",
                    ["componentType"] = "Transform",
                    ["memberPath"] = "Position.X",
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
                DefaultTitle = kind,
                Description = "Custom / unknown kind — treated as pass-through action at runtime.",
                Pins = new[]
                {
                    new BlueprintPinDef { Id = "ExecIn", DisplayName = "exec", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Input },
                    new BlueprintPinDef { Id = "ExecOut", DisplayName = "exec", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Output },
                },
            };
        }

        public static IEnumerable<BlueprintNodeTemplate> PaletteNodes() => ByKind.Values;

        /// <summary>Resolve effective pins for a node (catalog + dynamic + GetVar/SetVar type).</summary>
        public static IReadOnlyList<BlueprintPinDef> ResolvePins(BlueprintNode node, BlueprintDocument? doc = null)
        {
            var def = Resolve(node.Kind);
            var list = new List<BlueprintPinDef>();

            if (string.Equals(node.Kind, "GetVar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Kind, "SetVar", StringComparison.OrdinalIgnoreCase))
            {
                var varType = BlueprintPinType.Float;
                var name = node.VariableName ?? "";
                if (doc != null && !string.IsNullOrEmpty(name))
                {
                    var decl = doc.Variables.FirstOrDefault(v =>
                        string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (decl != null) varType = decl.Type;
                }

                if (string.Equals(node.Kind, "GetVar", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new BlueprintPinDef
                    {
                        Id = "value", DisplayName = name.Length > 0 ? name : "Value",
                        Type = varType, Direction = BlueprintPinDirection.Output
                    });
                    return list;
                }

                list.Add(new BlueprintPinDef { Id = "ExecIn", DisplayName = "exec", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Input });
                list.Add(new BlueprintPinDef { Id = "ExecOut", DisplayName = "exec", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Output });
                list.Add(new BlueprintPinDef
                {
                    Id = "value", DisplayName = name.Length > 0 ? name : "Value",
                    Type = varType, Direction = BlueprintPinDirection.Input, DefaultLiteral = "0"
                });
                return list;
            }

            if (string.Equals(node.Kind, "Sequence", StringComparison.OrdinalIgnoreCase)
                && node.DynamicPins is { Count: > 0 })
            {
                list.Add(new BlueprintPinDef { Id = "ExecIn", DisplayName = "exec", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Input });
                foreach (var dp in node.DynamicPins.Where(p => p.Type == BlueprintPinType.Exec && p.Direction == BlueprintPinDirection.Output))
                    list.Add(dp);
                if (list.Count == 1)
                {
                    list.Add(new BlueprintPinDef { Id = "Then0", DisplayName = "Then 0", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Output });
                    list.Add(new BlueprintPinDef { Id = "Then1", DisplayName = "Then 1", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Output });
                }
                return list;
            }

            if (string.Equals(node.Kind, "CallFunction", StringComparison.OrdinalIgnoreCase) && doc != null
                && !string.IsNullOrEmpty(node.FunctionName))
            {
                var fn = doc.Functions.FirstOrDefault(f =>
                    string.Equals(f.Name, node.FunctionName, StringComparison.OrdinalIgnoreCase));
                if (fn != null)
                {
                    list.Add(new BlueprintPinDef { Id = "ExecIn", DisplayName = "exec", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Input });
                    list.Add(new BlueprintPinDef { Id = "ExecOut", DisplayName = "exec", Type = BlueprintPinType.Exec, Direction = BlueprintPinDirection.Output });
                    foreach (var p in fn.Inputs)
                        list.Add(new BlueprintPinDef
                        {
                            Id = p.Id, DisplayName = p.DisplayName, Type = p.Type,
                            Direction = BlueprintPinDirection.Input, DefaultLiteral = p.DefaultLiteral
                        });
                    foreach (var p in fn.Outputs)
                        list.Add(new BlueprintPinDef
                        {
                            Id = p.Id, DisplayName = p.DisplayName, Type = p.Type,
                            Direction = BlueprintPinDirection.Output
                        });
                    return list;
                }
            }

            list.AddRange(def.Pins);
            if (node.DynamicPins is { Count: > 0 })
            {
                foreach (var dp in node.DynamicPins)
                {
                    if (list.Any(p => string.Equals(p.Id, dp.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(dp);
                }
            }
            return list;
        }

        public static BlueprintPinDef? FindPin(BlueprintNode node, string pinId, BlueprintDocument? doc = null) =>
            ResolvePins(node, doc).FirstOrDefault(p =>
                string.Equals(p.Id, pinId, StringComparison.OrdinalIgnoreCase));

        /// <summary>Ordered kinds shown in the Blueprint editor Insert menu and quick-add combo.</summary>
        public static readonly string[] AuthoringPaletteOrdered =
        {
            "BeginPlay", "Tick", "CustomEvent", "InputKey", "InputAction",
            "TriggerEnter", "TriggerStay", "TriggerExit",
            "Sequence", "Branch", "BranchEquals", "BranchCompare", "RandomBranch",
            "Delay", "ForLoop", "DoOnce", "Gate",
            "GetVar", "SetVar",
            "AddFloat", "SubtractFloat", "MultiplyFloat", "DivideFloat",
            "LessFloat", "LessEqualFloat", "GreaterFloat", "GreaterEqualFloat", "EqualFloat",
            "AddVector", "ScaleVector", "MakeVector", "BreakVector", "AppendString",
            "GetSelf", "GetLocation", "GetRotation",
            "LogMessage", "FireBlueprintEvent", "CallCustomEvent", "CallFunction",
            "SetObjectActive", "SetObjectPosition", "SetObjectRotation", "DestroyObject",
            "ReflectGet", "ReflectSet",
            "SetVariable", "CopyVariable", "AppendVariable", "IncrementVariable", "MultiplyVariable",
            "ClearVariable", "StoreGameTime", "StoreObjectName",
            "SetOtherObjectActive", "SetOtherObjectPosition", "SetOtherObjectRotation",
            "Comment",
        };

        public static IReadOnlyList<string> OutboundExecPinNames(BlueprintNodeTemplate t) => t.ExecOutPinNames;

        public static IReadOnlyList<string> OutboundExecPinNames(BlueprintNode node, BlueprintDocument? doc = null) =>
            ResolvePins(node, doc)
                .Where(p => p.Type == BlueprintPinType.Exec && p.Direction == BlueprintPinDirection.Output)
                .Select(p => p.Id)
                .ToList();
    }
}
