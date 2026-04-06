# Game Engine — Visual Blueprints

Visual blueprints are **node graphs** (saved as `.blueprint` JSON) that run on a GameObject via the **Visual Blueprint** (`VisualBlueprintBehavior`) component. They complement C# `Behavior` scripts: you can wire **events**, **flow** (branch, delay), **actions** (transform, activate objects, variables), **reflection** (read/write public members), and **EventBus** messages without recompiling.

**Full editor workflow:** [Editor Guide — Blueprint panel](02_Editor_Guide.md#blueprint-panel).  
**C# integration:** [Scripting — Visual Blueprints](06_Scripting_And_Extensibility.md#visual-blueprints).

---

## Quick start

1. Add component **Scripting → Visual Blueprint** to a GameObject.
2. Open **Window → New Blueprint Tab** (or command palette: **Window: New Blueprint Tab**).
3. Add nodes (**Add node** / **Insert** menu), connect **exec** wires: drag from the **right** exec pin to the **left** exec pin of the next node.
4. Save the graph under `Assets/Blueprints/*.blueprint` (the editor can create this folder).
5. Assign **Blueprint Asset Path** on the component (project-relative, e.g. `Assets/Blueprints/MyGraph.blueprint`).
6. Enter **Play** — **Begin Play** runs once; **Tick** runs every frame unless **Run Tick Graph** is disabled on the component.

---

## Asset format

| Item | Detail |
|------|--------|
| **File** | JSON document: `{ "version": 1, "graph": { "nodes": [...], "wires": [...] } }` |
| **Default folder** | `Assets/Blueprints/` (created on demand) |
| **Pins** | Exec: `ExecIn` / `ExecOut`, or **Then** / **Else** for dual-output branches |

After editing the file on disk, use **Reload from disk** on the component or reopen the blueprint tab as needed.

---

## Runtime model

- **Variables:** Case-insensitive `Dictionary<string,string>` on the runner. Use **Set Variable**, **Copy Variable**, math/text helpers, **ReflectGet** (writes into a `varKey`), etc. Editable in the Inspector (multiline `key=value`).
- **Events:** **Begin Play** and **Tick** entry nodes; each starts its own exec chain. **Tick** is skipped when **Run Tick Graph** is false.
- **Delay:** Schedules the next node on the same `VisualBlueprintBehavior` using `Time.time`.
- **Branches:** **Branch** (truthy variable), **BranchEquals** (string compare), **BranchCompare** (numeric ops), **RandomBranch** (probability) — all use **Then** / **Else** pins; wire each pin you use.
- **Destroy:** **Destroy Object** tears down behaviors (`OnDestroy`) and removes the object from the scene hierarchy; **Self** can invalidate the runner mid-frame — use with care.

---

## Node reference (built-in)

| Kind | Category | Summary |
|------|----------|---------|
| **BeginPlay** | Event | Runs once at start. |
| **Tick** | Event | Every frame (optional). |
| **Comment** | Comment | Not executed. |
| **Sequence** | Flow | Pass-through. |
| **Branch** | Flow | Then/Else from boolean-ish `Variables[conditionKey]`. |
| **BranchEquals** | Flow | Then if string equals `equalsValue` (trimmed, ignore case). |
| **BranchCompare** | Flow | Then if `conditionKey` compares to `compareValue`; `compareOp`: Lt, Lte, Eq, Gte, Gt (or `<`, `<=`, …). |
| **RandomBranch** | Flow | Then with probability `chance` (0–1). |
| **Delay** | Flow | Wait `seconds` (game time), then continue. |
| **SetVariable** | Action | Set string variable. |
| **CopyVariable** | Action | Copy `fromKey` → `toKey`. |
| **AppendVariable** | Action | Append `text` to `varKey`. |
| **IncrementVariable** | Action | Add `delta` to numeric text. |
| **MultiplyVariable** | Action | Multiply numeric text by `factor`. |
| **ClearVariable** | Action | Remove `varKey`. |
| **StoreGameTime** | Action | `Time.time` → `varKey`. |
| **StoreObjectName** | Action | This object name → `varKey`. |
| **LogMessage** | Action | Print `message` to log. |
| **FireBlueprintEvent** | Action | `EventBus.Publish(new BlueprintMessageEvent { … })`. |
| **SetObjectActive** | Action | Enable/disable **this** GameObject. |
| **SetOtherObjectActive** | Action | Resolve by `targetPath` / `targetName`, set **Enabled**. |
| **SetObjectPosition** / **SetOtherObjectPosition** | Action | `x,y,z` and `relative`. |
| **SetObjectRotation** / **SetOtherObjectRotation** | Action | Euler degrees; `relative`. |
| **DestroyObject** | Action | `scope` Self or Other; `targetPath` / `targetName` for Other. |
| **ReflectGet** | Action | Read public field/property path into `varKey` (instance or static). |
| **ReflectSet** | Action | Write from `value` or `Variables[valueVarKey]`. |

Legacy kinds **Event**, **Call**, **Math** are still supported for old graphs.

---

## Reflect nodes (Get / Set Property)

Use these to reach **public** fields and properties without dedicated nodes.

- **Instance mode:** `scope` **Self** or **Other**; **componentType** = `GameObject`, `Transform`, or a **behavior** type name; **memberPath** = dotted path (e.g. `Position.X`, `Enabled`).
- **Static mode:** **typeName** = full or short type name in a **Game_Engine** assembly (e.g. `Game_Engine.Core.Time`); **memberPath** starts at a static member (e.g. `time`, `deltaTime`).
- **Inspector UX:** **mode** and **scope** use dropdowns; **typeName**, **componentType**, and **memberPath** use **searchable autocomplete** lists (you can still type custom paths).
- **Limits:** No indexers; expand depth is capped; `private set`, init-only fields, and types like `GameObject` in the middle of a path are excluded from browsing where needed to avoid cycles. **Set** requires a public setter or writable field.

**Vector3** literals for set: `x;y;z` or `x,y,z` (invariant numbers).

---

## EventBus: `BlueprintMessageEvent`

Subscribe from C#:

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

The **Fire Event** node sets `Name`, optional `Data`, and `Sender` to the GameObject running the blueprint.

---

## Tips

- Use **Log Steps** on the component while authoring; disable in production if noisy.
- **Summary** text in the blueprint panel shows a linear outline per event (branch side branches are approximate).
- Prefer dedicated nodes for hot paths; use **Reflect** for glue and prototyping.

---

## Source layout

| Area | Path |
|------|------|
| Models / catalog | `Core/Blueprint/BlueprintNodeCatalog.cs`, `BlueprintGraphModel.cs` |
| Persistence | `Core/Blueprint/BlueprintPersistence.cs` |
| Runtime | `Core/Blueprint/BlueprintFlowRuntime.cs`, `VisualBlueprintBehavior.cs` |
| Reflection | `Core/Blueprint/BlueprintReflection.cs`, `BlueprintReflectionBrowse.cs` |
| Editor UI | `Views/BlueprintGraphPanel.axaml(.cs)` |

