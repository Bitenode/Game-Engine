# Game Engine — Visual Blueprints

Visual blueprints are **node graphs** (saved as `.blueprint` JSON) that run on a GameObject via the **Visual Blueprint** (`VisualBlueprintBehavior`) component. They complement C# `Behavior` scripts with an **Event Graph**: white **exec** wires for control flow, colored **data** pins for typed values, pure nodes, typed variables, functions, and engine events.

**Full editor workflow:** [Editor Guide — Blueprint panel](02_Editor_Guide.md#blueprint-panel).  
**C# integration:** [Scripting — Visual Blueprints](06_Scripting_And_Extensibility.md#visual-blueprints).

---

## Quick start

1. Add component **Scripting → Visual Blueprint** to a GameObject.
2. Open **Window → New Blueprint Tab** (or command palette: **Window: New Blueprint Tab**).
3. Add nodes (**Add node**, **Insert** menu, or **right‑click** the canvas to search). Connect **exec** wires (white) from right → left; connect **data** wires (colored) the same way.
4. Optionally declare **Variables** and **Functions** under **My Blueprint**.
5. Save under `Assets/Blueprints/*.blueprint`.
6. Assign **Blueprint Asset Path** on the component.
7. Enter **Play** — **Begin Play** runs once; **Tick** runs every frame unless **Run Tick Graph** is disabled. Use **Graph → Validate** before play.

### Shipped starter graphs (Standard Assets)

When **Include standard assets in new projects** is checked at project creation, the editor copies starter graphs into **`Assets/Standard Assets/Blueprints/`**:

| Asset | Path (after install) |
|-------|----------------------|
| Starter graphs (10) | `Assets/Standard Assets/Blueprints/Starter_*.blueprint` |
| v1 compatibility sample | `Assets/Standard Assets/Blueprints/Legacy_V1_Branch.blueprint` |
| Demo scene | `Assets/Standard Assets/Scenes/Blueprint Demos.scene` |
| Folder README | `Assets/Standard Assets/Blueprints/README.txt` |

Open **Blueprint Demos.scene**, press **Play**, and watch the console — ten empty GameObjects each run one starter graph (Begin Play, typed branch, delay/sequence, for loop, input, function, transform, custom event, random branch, legacy v1). Assign any starter to your own objects via **Blueprint Asset Path**, e.g. `Assets/Standard Assets/Blueprints/Starter_BeginPlay.blueprint`.

Save project-specific graphs under **`Assets/Blueprints/`** (created automatically when you save from the Blueprint panel).

---

## Asset format (version 2)

| Item | Detail |
|------|--------|
| **File** | JSON: `{ "version": 2, "graph": {…}, "variables": […], "functions": […] }` |
| **Default folder** | `Assets/Blueprints/` |
| **Exec pins** | `ExecIn` / `ExecOut`, or named outs (`Then`, `Else`, `Then0`, `loopBody`, …) |
| **Data pins** | Typed literals on the node (`pinLiterals`) or wires (`kind: "data"`) |
| **Variables** | Typed decls: bool, int, float, string, vector, object |
| **Functions** | Named nested graphs with input/output pins |

**Version 1** files still load. Legacy string `Properties` (e.g. `conditionKey`) are migrated to pin literals at load; the string `Variables` map on the component remains for old graphs.

After editing on disk, use **Reload from disk** on the component or reopen the blueprint tab.

---

## Pins and evaluation

| Kind | Role |
|------|------|
| **Exec** (white) | Control flow. Impure nodes run when exec reaches them. |
| **Data** (colored) | Values. Unwired inputs use the pin **literal**. Wired inputs pull from the source. |
| **Pure** nodes | No exec pins. Evaluated when something reads an output (with cycle detection). |
| **Impure** nodes | Have exec; outputs are cached after the node runs in that chain. |

**Type colors:** exec white · bool red · int cyan · float green · string magenta · vector yellow · object blue. **Int → float** wires are allowed.

---

## Runtime model

- **Typed variables:** `GetVar` (pure) / `SetVar` (impure) against the document variable list. Instance overrides: `TypedVariableOverrides` on the component.
- **Legacy string map:** `Variables` on the component still drives older Set Variable / Branch(conditionKey) nodes.
- **Events:** Begin Play, Tick (`deltaSeconds` float out), Custom Event, Input Key / Action, Trigger Enter / Stay / Exit (`other` object out).
- **Delay:** Latent — schedules the next node via game time.
- **For Loop:** Runs **Loop Body** from First…Last (cap 10 000), then **Completed**. **Index** is an impure output.
- **Sequence:** Fires Then0, Then1, … in order (dynamic Then pins supported).
- **Do Once / Gate:** Secondary exec pins Reset / Open / Close.
- **Functions:** **Call Function** pushes args, runs the function graph from **Function Entry**, returns via **Return Node**.
- **Destroy:** Tears down behaviors and removes the object — **Self** can invalidate the runner mid-frame.

---

## Node reference (built-in)

### Events
| Kind | Summary |
|------|---------|
| **BeginPlay** | Once at start. |
| **Tick** | Every frame; **Delta Seconds** out. |
| **CustomEvent** | Entry by name (`eventName` literal / title). |
| **InputKey** | `GetKeyDown` for **Key** literal (e.g. `Space`). |
| **InputAction** | `GetActionDown` for **Action** name. |
| **TriggerEnter / Stay / Exit** | Collider overlap; **Other** object out. |

### Flow
| Kind | Summary |
|------|---------|
| **Branch** | Bool **Condition** → Then / Else. |
| **BranchEquals / BranchCompare / RandomBranch** | String / numeric / chance branches. |
| **Sequence** | Ordered Then pins. |
| **Delay** | Wait **Seconds**. |
| **ForLoop** | First / Last / Index / Loop Body / Completed. |
| **DoOnce** | Pass once; **Reset** clears. |
| **Gate** | **Enter** passes when open; **Open** / **Close**. |

### Pure (Math / Scene)
Add / Subtract / Multiply / Divide Float · comparisons · Add / Scale Vector · Make / Break Vector · Append String · Get Self · Get Location · Get Rotation · **ReflectGet**.

### Actions
Print String · Fire Event · Call Custom Event · Call Function · Set Active / Location / Rotation · Destroy · **SetVar** · **ReflectSet** · legacy string-map helpers (Set/Copy/Append/Increment Variable, …).

Legacy kinds **Event**, **Call**, **Math** still load as pass-through / print.

---

## Reflect nodes (Get / Set Property)

- **Instance:** scope Self/Other; **componentType**; **memberPath** (e.g. `Position.X`).
- **Static:** **typeName** in a Game_Engine assembly; **memberPath** from a static member.
- **Get** is pure (string **result** pin). **Set** takes a **value** data pin (or legacy `value` / `valueVarKey` properties).

**Vector3** literals: `x;y;z` or `x,y,z` (invariant).

---

## EventBus: `BlueprintMessageEvent`

```csharp
using Game_Engine.Core.Events;

EventBus.Subscribe<BlueprintMessageEvent>(e =>
{
    if (e.Name == "DoorOpened")
    {
        // e.Data, e.Sender (GameObject?)
    }
});
```

**Fire Event** sets `Name`, optional `Data`, and `Sender` to the running GameObject.

---

## Editor tips

- **My Blueprint** — add typed variables; context menu **Add Get** / **Add Set**. Add functions with **+ Function** (tabs switch graphs).
- **Validate** — type mismatches, exec→pure, pure cycles, unknown variables.
- **Undo / Redo** — Ctrl+Z / Ctrl+Y (full document snapshots).
- **Log Steps** on the component while authoring.
- Prefer dedicated / pure nodes for hot paths; Reflect for glue.

---

## Source layout

| Area | Path |
|------|------|
| Types / values | `Core/Blueprint/BlueprintTypes.cs` |
| Models | `Core/Blueprint/BlueprintGraphModel.cs` |
| Catalog | `Core/Blueprint/BlueprintNodeCatalog.cs` |
| Persistence | `Core/Blueprint/BlueprintPersistence.cs` |
| Validation | `Core/Blueprint/BlueprintValidation.cs` |
| Runtime | `Core/Blueprint/BlueprintFlowRuntime.cs`, `VisualBlueprintBehavior.cs` |
| Reflection | `Core/Blueprint/BlueprintReflection.cs`, `BlueprintReflectionBrowse.cs` |
| Editor UI | `Views/BlueprintGraphPanel.axaml(.cs)` |
