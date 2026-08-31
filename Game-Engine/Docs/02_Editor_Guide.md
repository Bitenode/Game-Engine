# Game Engine — Editor Guide

## Editor Layout

The editor window contains dockable panels distributed across five dock regions:

```
┌──────────────┬──────────────────────────┬──────────────┐
│  Hierarchy   │   Scene View / Game View │  Inspector   │
│              │                          │              │
│  (scene      │  (3D viewport with       │  (properties │
│   tree)      │   camera controls and    │   of selected│
│              │   terrain editing)       │   object)    │
│              │                          │              │
├──────────────┴──────────────────────────┴──────────────┤
│  Project Panel              │  Console / Animation /   │
│  (file browser)             │  Timeline Sequencer      │
└─────────────────────────────┴──────────────────────────┘
```

### Panel Management
- **Rearrange** panels by dragging their tab headers between regions
- **Float** any panel into a standalone `ToolWindow` via right-click > Float
- **Dock** floating panels back to any region via right-click > Dock To
- **Duplicate** panels with right-click > New Tab (tabs are auto-numbered, e.g., "Scene View (2)")
- **Close** panels via right-click > Close
- **Reset** to defaults with **Window > Reset Layout**
- **Layout presets** — **Window > Layout Presets** saves or restores three named arrangements (**Save Preset 1–3** / **Load Preset 1–3**). Presets are stored on disk so they survive restarts.
- Each dock region supports multiple tabs
- Reset Layout opens Scene View and Game View side-by-side by default (separate center hosts for live editing + play testing)

### Settings menu
- **Clear Console on Play** — when enabled (**Settings** menu), all Console tabs are cleared when you enter play mode (useful for seeing only runtime logs).
- **Script editor: line numbers** — toggles the script editor gutter (persisted).
- **Script editor: word wrap** — toggles fixed-column soft wrap in the script editor (horizontal scrollbar hidden; find-match highlights, squiggles, bracket highlights, and indent guides are not drawn while wrap is on). Persisted.

### Global shortcuts (main window)

These work when the main editor window has focus (with a project open where noted):

| Shortcut | Action |
|----------|--------|
| **Ctrl+Shift+P** | **Command palette** — fuzzy filter over every command in `CommandRegistry` (editor built-ins such as new panel tabs, save/load scene, compile scripts, plus any commands registered by `EditorExtension` scripts). **Enter** or double-click runs the selection; **Esc** closes; **↓** moves focus from the search box to the list. Matching built-in commands show their global shortcut on the right when one exists. |
| **Ctrl+P** | **Quick open** — fuzzy search project files under the current project root (`.scene`, `.cs`, `.material`, `.prefab`, `.boneanim`, `.shadergraph`; `bin` / `obj` / `.git` are skipped). **Enter** or double-click opens: scripts in the Script Editor, scenes with the usual unsaved-scene prompt, materials/prefabs via `SelectAssetForInspector`, other types with the OS default handler. Requires an open project. |
| **Ctrl+S** | **Save scene** — same as **Project > Save Scene** (silent save when the scene already has a path). Skipped while keyboard focus is in a **TextBox** or **NumericUpDown** on the main editor window so filters and inspector fields keep normal typing. Requires an open project. |
| **F5** | **Toggle Play / Stop** — if any Game View is playing, stops every Game panel; otherwise starts **Play** on the first Game panel in dock order (left → center → center secondary → right → bottom). Skipped while focus is in a **TextBox** or **NumericUpDown** on the main window. Same behavior as the palette command **Game: Toggle Play / Stop**. Requires an open project. |
| **Ctrl+Shift+R** | **Reveal selection in Project** — same as the command palette entry **Project: Reveal Selection in Project Panel**. Skipped while focus is in a **TextBox** or **NumericUpDown** on the main window (so inspector numeric fields keep normal typing). Requires an open project. |

The **command palette** (**Ctrl+Shift+P**) also includes **Project: Reveal Selection in Project Panel**, which selects the asset for the current Hierarchy selection in the Project tree (if it maps to a file under the project root).

Built-in palette commands are registered from the main window before `CommandRegistry.SealBuiltins()`; hot-loaded extensions register additional commands afterward, and the palette always reflects the current set when opened.

While any Game View is playing, the main window uses a subtle play-mode tint and a **▶** prefix in the title bar so you can tell edit vs play at a glance. With an open project, a `*` prefix appears before the project name when the scene has unsaved changes (`SceneService` dirty flag).

Closing the main editor window while the scene is dirty (with a project open) shows the same **Save / Don’t Save / Cancel** prompt used elsewhere so you do not lose edits accidentally.

**Project > Recent Scenes** lists scenes you opened recently for the current project (stored in `project.json`, capped at 15). Choosing one loads it with the same unsaved-scene check as **Load Scene**.

### Project hub (startup)

When **Show this hub when the editor starts** is enabled (default), a **Project hub** modal opens the first time the main window is shown. From it you can:

- **Create project** — pick a parent folder and project name (same as **Project > New Project**). Optionally **Include standard assets in new projects**: the editor copies the shipped `Standard Assets` tree from next to `Game_Engine.exe` into your project’s **`Assets/Standard Assets/`** (same layout under the project’s `Assets` folder as in the editor build output). Older projects may still use `Standard Assets/` at the project root; the UI font path checks `Assets/Standard Assets` first, then that legacy location.
- **Open project** — choose a `project.json` manifest.
- **Recent projects** — lists pinned (★) and recent manifests; select a row and click **Open selected**, or double-click a row.

The same dialog is available anytime via **Project > Project hub…** or the command palette entry **Project: Project hub…**.

Checkboxes at the bottom persist to AppData (`editor_settings.json`): whether to show the hub on startup, and the default for including standard assets (that default also applies when you use **Project > New Project…** from the menu bar).

---

## Scene View

The Scene View is the primary 3D editing viewport. It renders the scene using the full GPU pipeline (shadows, PBR materials, terrain splatmaps, particles, water) and provides tools for manipulating objects.

### Camera Controls
| Action          | Input                        | Notes |
|-----------------|------------------------------|-------|
| **Orbit**       | Left-click + drag (Hand tool) or Right-click + drag | Orbits around the focus point |
| **Pan**         | Middle-click + drag or Alt + Left-click + drag | Moves the camera laterally |
| **Zoom**        | Scroll wheel (Shift = fine zoom) | Dolly toward/away from focus |
| **Fly**         | Right-click + drag           | Free camera rotation (FPS-style) |

### Camera Bookmarks
- **Save bookmark**: `Ctrl+1..5`
- **Recall bookmark**: `1..5`
- Saved bookmark data includes target, orbit yaw/pitch, and camera distance.

### Toolbar
| Button     | Function                                         |
|------------|--------------------------------------------------|
| **Hand**   | Navigate only — no object manipulation            |
| **Move**   | Translate selected object along axes              |
| **Rotate** | Rotate selected object around axes                |
| **Scale**  | Scale selected object along axes                  |
| **AA**     | Anti-aliasing toggle (FXAA post-processing)       |
| **View**   | View options menu (grid, wireframe, etc.)         |
| **Gizmo**  | Toggle collider gizmo wireframe visibility        |
| **FPS**    | Frames-per-second display                         |

### Transform Gizmos
When an object is selected, colored axis gizmos appear in Scene View (including Hand mode for quick orientation). Transform editing still requires Move/Rotate/Scale tools.
- **Red** = X axis
- **Green** = Y axis
- **Blue** = Z axis

Click and drag an axis handle to constrain movement/rotation/scale to that axis. Gizmos maintain constant screen size regardless of camera distance.

### Transform Shortcuts
- **Local/World toggle**: `L` (toggles gizmo local-space mode)
- **Axis lock**: `X`, `Y`, `Z` (press same key again to clear lock)
- **Precise numeric transform**: `Ctrl+Shift+T`

### Collider Gizmos
Toggle the Gizmo button to show/hide collision shape wireframes in the scene:
- **BoxCollider** — green wireframe cube
- **CapsuleCollider** — green wireframe capsule with hemisphere caps
- **MeshCollider** — green wireframe of the collision mesh

### Object Selection
Click on an object in the Scene View to select it:
1. Mouse position is unprojected into a 3D ray
2. Ray is tested against mesh bounding spheres (broad phase)
3. On hit, ray is tested against individual triangles (Moller-Trumbore)
4. Closest hit determines the selected object
5. `SelectionService.Set()` updates the Inspector, Hierarchy, and gizmo state

**Overlapping objects:** clicking again at the **same screen pixel** (within a short time window) **cycles** through other objects hit by that ray, so you can reach items behind the front-most mesh without nudging the camera.

`F` frames the selected object in Scene View. Framing uses a short smooth camera transition.

### Translate snap
- **Ctrl+G** toggles **snap** for move operations (world grid step; see Scene View log line for the active step).

### Terrain Editing
When a Terrain is selected, the Scene View enters terrain editing mode:
- A circular brush indicator follows the mouse on the terrain surface
- 10 brush tools are available in the Inspector (see Terrain System doc)
- Left-click applies the tool, right-click applies the inverse
- Brush strokes auto-save terrain data on mouse release (to the terrain’s `.terrain.json` / `.terrain.bin` asset path)

### Planet Terrain Editing
When a GameObject with `PlanetTerrain` is selected and a planet brush is active, Scene View paints density (not an XZ heightmap):
- Inspector **Planet brushes (Scene View)**: **Dig**, **Build**, **Smooth**, **Flatten**, plus **Radius**, **Strength**, and **Falloff**
- A ring gizmo follows the mouse on the density surface (camera pick ray → `PlanetTerrain.Raycast`)
- Left-drag applies the tool; right-drag or **Shift** inverts Dig/Build
- Mouse-up saves voxel strokes via `SaveVoxelEdits()` to the `.planetvox` sidecar next to the `.planet`

**Interior fly-cam:** Scene View runs real planet LOD every frame. When the camera is inside the crust, chunk budgets rise so cave walls refine around you (not only when orbiting the outer surface). Give chunks a few seconds to rebuild after flying underground on land biomes.

Play-mode **PlanetTool** (Standard Assets): LMB dig / RMB build along the camera look-ray; `[` `]` radius; `-` `=` strength.

---

## Game View

The Game View shows the game as it would appear to the player, rendered through the first enabled Camera component in the scene.

During Play mode, Scene View remains live and renders the same runtime world from the editor camera (throttled preview). **Planet chunk LOD split/merge is owned by Game View during Play** — Scene View does not drive quadtree updates while playing, which keeps chunk stitching stable when switching focus or taking screenshots.

### Play Controls
| Button    | Function                                           |
|-----------|----------------------------------------------------|
| **Play**  | Start the game (runs Awake → Start → Update loop)  |
| **Pause** | Pause the game loop (freezes Update/FixedUpdate)   |
| **Stop**  | Stop and restore the scene to its pre-play state   |

### Play Mode Lifecycle

**When you press Play:**
1. The current scene graph is serialized to a JSON snapshot (including material texture data)
2. All Behaviors receive `Awake()` then `Start()`
3. The game loop begins:
   - `Update()` runs every frame
   - `FixedUpdate()` runs at fixed time intervals (physics)
   - `LateUpdate()` runs after all Update calls
4. Input is routed to the game (WASD, mouse look, etc.)
5. Physics simulation runs (CharacterController, collision detection)
6. Audio sources begin playback (if `PlayOnAwake` is set)
7. Particle emitters begin emitting
8. Animators play their default state

**When you press Stop:**
1. All Behaviors receive `OnDisable()` then `OnDestroy()`
2. All audio playback is stopped
3. The scene is deserialized from the snapshot, restoring the exact pre-play state
4. Material textures are restored
5. Terrain data is reloaded from per-tile terrain asset files (`.terrain.json` or `.terrain.bin` per `TerrainAssetPath`)

The Game View also displays an FPS counter next to the stop button. When script sampling is enabled, the HUD overlay can show GL frame ms, planet chunk/triangle counts, and top script costs for quick play-mode profiling.

### Input During Play Mode
During play mode, the Game View captures input and feeds it to the `Input` system:
- Keyboard state (held/down/up transitions per frame)
- Mouse position, delta, and button state
- Axis smoothing is applied (Sensitivity/Gravity parameters)
- Action bindings are evaluated (Jump, Sprint, Fire1, etc.)

---

## Hierarchy Panel

Shows the scene as a tree of GameObjects with expand/collapse nodes for the parent-child hierarchy.

### Search & filter strip

Below the title row, optional controls help you find objects in large scenes:

| Control | Purpose |
|---------|---------|
| **Filter by object name** | Substring match on `GameObject.Name` (case-insensitive). |
| **Component type contains** | Optional second filter: any behavior whose **CLR type name** contains the typed substring matches (e.g. `Mesh`, `Camera`). |
| **Match list** | When either filter has text, the tree is hidden and a flat list shows matching objects as **hierarchy paths** (`Parent/Child/Target`). Selecting a row selects that object and requests a Scene View frame. |
| **Circular toggle** (right of the **Hierarchy** title) | **Hide or show** the entire filter strip (both text boxes and the match list). When hidden, filters are not applied: the tree stays visible and filter text is ignored until you show the strip again. The icon is a ring; a **filled dot** inside means the strip is **collapsed**. Tooltip: *Hide search filters* / *Show search filters*. |

Clear both filter fields to return to the normal tree view.

### Visual Indicators
| Color | Meaning |
|-------|---------|
| **Default (theme)** | Normal active GameObject |
| **Blue** (`#5599FF`) | Prefab instance (has a `PrefabId`) |
| **Red** (`#DD4444`) | Disabled GameObject — either its own `Enabled` is `false`, or an ancestor is disabled. The entire subtree under a disabled object appears in red. |

### Actions
| Action | Input | Description |
|--------|-------|-------------|
| **Select** | Click | Select the object (shown in Inspector, highlighted in Scene View) |
| **Auto-frame in Scene View** | Click in Hierarchy | Focuses Scene View on the selected object |
| **Context menu** | Right-click | Create objects, import models, delete, **Reveal in Project** |
| **Reveal in Project** | Hierarchy context menu or command palette | Selects the corresponding asset in the Project panel when the selection maps to a project file (e.g. model or prefab path) |
| **Reparent** | Drag and drop | Move objects in the hierarchy (updates parent-child relationships) |
| **Expand/Collapse** | Arrow click | Navigate nested GameObjects |
| **Expand all / Collapse all** | Buttons in Hierarchy header | Expand or collapse the full hierarchy tree in one action |
| **Keyboard shortcuts** | `Delete`, `Ctrl+D`, `F2`, `Ctrl+F` | Delete selected object, duplicate selected object, rename selected object; **Ctrl+F** expands the filter strip (if hidden) and focuses the **name** filter |

For multi-select from the Hierarchy, Scene View focuses the first selected object.

### Unsaved Scene Prompt
Opening/creating/closing projects now checks for unsaved scene changes and prompts with:
- **Save**
- **Don’t Save**
- **Cancel**

### Context Menu
| Option | Description |
|--------|-------------|
| **Empty** | Create an empty GameObject |
| **Cube** | Create a cube primitive |
| **Cone** | Create a cone primitive |
| **Cylinder** | Create a cylinder primitive |
| **Sphere** | Create a UV sphere primitive |
| **Quad** | Create a single-face quad |
| **Plane** | Create a subdivided plane |
| **Camera** | Create a camera object |
| **Light** | Create a directional light |
| **Terrain** | Create a heightmap terrain (129x129 default) |
| **Import Model** | Open file dialog for FBX, OBJ, glTF, GLB, DAE |
| **Delete** | Remove the selected object from the scene |
| **Reveal in Project** | Jump to the Project panel entry for the selected object’s asset, when resolvable |

### Default Scene
When a new project is opened with no existing scene, a default scene is created with:
| Object | Components |
|--------|------------|
| **Skybox** | Skybox (gradient sky, ambient 0.9) |
| **Main Camera** | Camera (perspective, FOV 60, near 0.1, far 1000) |
| **Directional Light** | Light (directional, white, intensity 1.0, shadows on) |
| **Cube** | MeshFilter (cube mesh) + MeshRenderer (default material) |

---

## Inspector Panel

Displays and edits properties of the selected GameObject. Supports single and multi-selection.

### Header
- **Enabled checkbox** — toggle next to the name field. Unchecking disables the entire GameObject and all its children: they are hidden from the scene, skipped during Update/FixedUpdate/LateUpdate, excluded from scene queries (`SceneQuery.FindBehaviors`), and shown in red in the Hierarchy. This does not change the individual component `Enabled` flags — it overrides them at the GameObject level.
- **GameObject name** — editable text field

### Object (Tag & Layer)
Below **Name**, the **Object** section edits:
- **Tag** — string label (default `Untagged`), saved with the scene. Used by **`TriggerVolume`** filters, gameplay queries, and scripting.
- **Layer** — integer **0–31**, saved with the scene. Used with **`TriggerVolume.LayerMask`** and future physics filtering.

### Components
Each component (Behavior) on the selected object shows:
- **Enable checkbox** — toggle the component on/off
- **Component name** — type label (e.g., "Transform", "MeshRenderer", "PlayerMovement")
- **Copy** — serializes the component with the same rules as scene save; use **Paste component** (below **Add Component**) to add a duplicate instance on this or another GameObject. Transform is not copyable.
- **Remove button** — delete the component (Transform cannot be removed)
- **Properties** — all `[Persist]`-marked properties with type-appropriate editors (plus a few runtime-only rows with custom UI, e.g. **`ReflectionProbe`** **GpuCubemap** status and **Request recapture** — not a texture file slot).

**Paste component** appears above **+ Add Component** and applies the last copied component to the current GameObject.

### Property Editors
| Type | Editor | Notes |
|------|--------|-------|
| `string` | Text field | Single-line text input |
| `int` | Number field | Integer spinner |
| `float` | Number field with decimal | Floating-point spinner |
| `bool` | Checkbox | Toggle switch |
| `Vector3` | Three number fields (X, Y, Z) | Labeled axis inputs |
| `Color` | Color picker | RGBA color selector with hex display |
| `enum` | Dropdown | Lists all enum values |
| `Material` | Material editor | Color picker + PBR sliders + texture slots |
| `Mesh` | Read-only display | Shows vertex/triangle counts |
| `List<>` | Expandable list | Add/remove items |

### Adding Components
Click the **"+ Add Component"** button at the bottom of the Inspector to open a hierarchical popup menu. Components are organized into category submenus, similar to Unity's component picker:

| Category | Components |
|----------|-----------|
| **2D** | Camera2D, SpriteRenderer, Tilemap |
| **AI** | BehaviorTreeRunner |
| **Animation** | Animator, IKConstraint |
| **Audio** | AudioListener, AudioSource, ReverbZone |
| **Dialogue** | DialogueRunner |
| **Effects** | Decal, ParticleEmitter, PostProcessVolume |
| **Environment** | Skybox, Terrain, TerrainStreamer, Tree, TreeLOD, VegetationPainter, Water |
| **Misc** | Any components without a category annotation |
| **Navigation** | NavMeshAgent |
| **Networking** | NetworkAnimator, NetworkIdentity, NetworkTransform — hosting uses static `Game_Engine.Core.Networking.NetworkManager` (or Standard Assets `ServerHostController`), not a fourth Inspector component. Helpers `NetworkGameplayRules` and `NetworkWorldDiagnostics` live in the same namespace but are code-only (see [09 — Scene & Project](09_Scene_And_Project_Management.md#networking)). |
| **Physics** | BoxCollider, CapsuleCollider, CharacterController, MeshCollider, PlayerMovement, Rigidbody, RigidbodyPlayer, TriggerVolume |
| **Rendering** | Camera, Light, MeshFilter, MeshRenderer, MeshLodGroup, ReflectionProbe, SkinnedMeshRenderer |
| **Timeline** | TimelinePlayer |
| **UI** | Canvas, RectTransform, UIButton, UIElement, UIImage, UIInputField, UIPanel, UIProgressBar, UISlider, UIText, UIToggle |
| **Scripts** | Any custom Behavior scripts compiled from `Assets/` or `Packages/` |

Each category expands into a submenu listing its components alphabetically. The **Scripts** submenu appears below a separator at the bottom. Scripts that are present in source but not yet compiled show a "(source only)" label.

Components are assigned to categories using the `[ComponentCategory("Name")]` attribute on their class declaration.
For faster keyboard-first insertion, use **Quick Add...** above **+ Add Component** and type to filter component names across categories and scripts.

### Runtime UI components
`UIElement` and derived UI behaviors show grouped Inspector sections: **UI (common)** (raycast, color, opacity sliders, focusable flag, optional opacity target easing), plus type-specific blocks for **Button**, **Slider**, **Toggle**, **Input field**, and **Progress bar** (with a **Value** slider tied to min/max). Runtime-only pointer flags are hidden from the default property list.

### Terrain Inspector
When a Terrain is selected, the Inspector shows specialized sections:
1. **Terrain Tools** — toolbar of 10 brush tools for sculpting and painting
2. **Brush Settings** — Radius, Strength, and Falloff sliders
3. **Terrain Layers** — multi-material layer management (up to 8 layers) with texture selection and tiling sliders
4. **Tree Painting Settings** — density, scale range, rotation, and a tree asset list for switching between procedural and imported tree models

### Planet Terrain Inspector
When a `PlanetTerrain` is selected, the Inspector includes **Planet brushes (Scene View)** above the usual component properties: tool toggles (**Dig**, **Build**, **Smooth**, **Flatten**) and **Radius** / **Strength** / **Falloff** sliders. Scene View uses those settings for density painting (see Planet Terrain Editing above).

### Custom Inspectors
Components can implement `ICustomInspector` to provide custom Avalonia UI in the Inspector panel, or use `[CustomInspector(typeof(TargetComponent))]` on a separate class.

Several built-in components have dedicated custom inspectors:

| Component | Inspector Features |
|-----------|-------------------|
| **PlanetVegetationSystem** | Planet Vegetation runtime controls — live `Leaf Groups` / `Instances` stats, `Full Biome Populate` mode toggle, and one-click `Spawn Vegetation (Scene View)` / `Respawn (Clear + Spawn)` actions |
| **PlanetPlayerSpawner** | Gameplay — one-click play-mode player spawn on the crust (`RigidbodyPlayer` + capsule + camera) |
| **ReflectionProbe** | **GpuCubemap** — explains runtime GPU cubemap allocation (not an importable 2D texture); **Request recapture** sets `NeedsCapture` |
| **TriggerVolume** | **On enter** / **On exit** reaction rows (`LoadScene`, `SetObjectEnabled`, `PublishChannel`) with parallel list persistence |
| **DialogueRunner** | Dialogue tree editor — node list with type/speaker/text, choice linking, variable store, voice clip paths per node, dialogue mode selector (Text / Voice / Both) |
| **BehaviorTreeRunner** | Behavior tree editor — hierarchical node view with type selectors, child management, blackboard key-value editor, tick interval and running state |
| **TimelinePlayer** | Timeline asset editor — name/duration/loop, playback status, track list with type badges and mute toggles, per-clip start/duration/blend/speed editors, track-type-specific fields |

### List Property Editor
`List<T>` properties are rendered with a dedicated expandable editor that supports:
- **Add/Remove** items with +/- buttons
- **Reorder** items with up/down arrows
- **Sub-inspectors** for complex element types (nested property editors for non-primitive types)
- Adapts automatically to the element type (`string`, `int`, `float`, custom objects, etc.)

---

## Project Panel

File browser for the project directory with asset management capabilities.

For keyboard-driven access to many of the same file types from anywhere in the editor, use **Quick open** (**Ctrl+P**) on the main window (see **Global shortcuts** under Editor Layout).

### Search filter
- The **Filter** field above the tree narrows entries by **file or folder name** (case-insensitive substring). Empty filter shows the full **Assets**, **Scenes**, **Packages**, and **Builds** roots.
- **Ctrl+F** while the Project panel (or its children) has focus focuses the filter and selects its text.

### Folder Structure
```
ProjectRoot/
├── Assets/              # Game assets
│   ├── Models/          # 3D models (FBX, OBJ, glTF)
│   ├── Textures/        # Image files (PNG, JPG, BMP)
│   ├── Materials/       # Material definitions (.material)
│   ├── Scripts/         # C# scripts (.cs)
│   └── Terrain/         # Terrain data (`.terrain.json` and optional `.terrain.bin`)
├── Scenes/              # Scene files (.scene)
├── Packages/            # Editor extensions and reusable scripts
├── Builds/              # Compiled script assemblies (auto-generated)
│   └── EditorScripts_<timestamp>.dll
└── Temp/                # Temporary working files
```

### File Actions
| Action | How | Description |
|--------|-----|-------------|
| **Open script** | Double-click `.cs` file | Opens the built-in Script Editor |
| **Inspect material** | Double-click `.material` file | Shows material properties in Inspector |
| **Load scene** | Double-click `.scene` file | Loads the scene into the editor |
| **Create script** | Right-click > New Script | Creates a new C# file with Behavior template |
| **Create scene** | Right-click > New Scene | Creates a new empty `.scene` file |
| **Create material** | Right-click > New Material | Creates a new `.material` file |
| **Create folder** | Right-click > New Folder | Creates a subdirectory |
| **Import files** | Right-click > Import | Opens file dialog for external assets |
| **Reveal in Explorer** | Right-click > Show in Explorer | Opens the folder in the OS file manager |
| **Refresh** | Right-click > Refresh | Reloads the file tree |
| **Quick open** | **Ctrl+P** (main window) | Fuzzy-open `.scene` / `.cs` / `.material` / `.prefab` / `.boneanim` / `.shadergraph` anywhere under the project (see **Global shortcuts**) |
| **Focus filter** | **Ctrl+F** (panel focus) | Focuses the project filter field |

---

## Console Panel

Displays log messages from the engine, scripts, and extensions.

### Filters and search
- Toggle **Info / Warning / Error / …** chips to show or hide severities.
- Use the **search** box to filter visible lines by substring (combined with severity toggles).
- Use **Clear**, **Copy Selected**, and **Auto-scroll** controls above the filter row for faster log triage.
- **Ctrl+L** (while the Console panel or its children have focus) clears the log output, same as **Clear**.

### Open log location in Script Editor
- **Double-click** a line that contains a C# path in compiler style (e.g. `C:\path\File.cs(12,5)` or `in File.cs:12`) to open the **Script Editor** at that line (file must exist on disk).

### Message Types
| Icon/Color | Level     | Source                           |
|------------|-----------|----------------------------------|
| Blue       | Info      | General information messages     |
| Yellow     | Warning   | Non-critical issues and alerts   |
| Red        | Error     | Errors, exceptions, compilation failures |
| Green      | Success   | Completed operations             |
| Gray       | Debug     | Debug output from LogLifecycle   |

### Commands
Type in the input field at the bottom of the Console and press Enter:
| Command | Description |
|---------|-------------|
| `help` | List all available commands |
| `clear` | Clear the console output |
| `log <message>` | Output an info message |

### Script Logging
Scripts can write to the Console from any Behavior:
```csharp
LogInfo("General information");
LogWarning("Something to watch");
LogError("Something broke");
LogSuccess("Task completed");
LogDebug("Debug details");
```

Or use the global `Log` class from any code:
```csharp
Log.Info("Global log message");
Log.Warning("Global warning");
Log.Error("Global error");
```

---

## Script Editor

Built-in C# script editor integrated into the editor (default window about **1280×800**; size, position, maximized state, and **script tree** column width are restored from `%AppData%/GameEngine/editor_settings.json` when you reopen the Script Editor).

### Menu bar
- **File** — **New C# Script…** (toolbar **New** or **Ctrl+N**), Save, Save As, Reload, Close Tab, **Recent Scripts**, Exit
- **Edit** — Undo, Redo, Cut, Copy, Paste, Select All, Find, Replace, Go to Line…, Format Document (same behavior as keyboard shortcuts below)
- **View** — toggles for minimap, line numbers, and word wrap (same as toolbar; ✓ in the menu shows the current state)
- **Build** — Build All (**Ctrl+Shift+B**)

### New scripts
- **File → New C# Script…** or **Ctrl+N** creates a `.cs` file with a minimal `Behavior` subclass template under the **selected folder** in the left script tree if that selection is a folder (or the folder containing a selected file); otherwise under the first indexed **Scripts** root (usually `Assets/Scripts`), creating that folder if needed.
- Right-click the script **tree** — **New C# Script…** or **Open containing folder** (OS file explorer).

### Editing shortcuts (when the code editor has focus)

| Shortcut | Action |
|----------|--------|
| **Ctrl+Z** / **Ctrl+Y** | Undo / Redo |
| **Ctrl+X** / **Ctrl+C** / **Ctrl+V** | Cut / Copy / Paste |
| **Ctrl+A** | Select all |
| **Ctrl+F** / **Ctrl+H** | Find / Replace |
| **Ctrl+G** | Go to line |
| **Ctrl+D** | Duplicate line |
| **Ctrl+/** | Toggle line comment |
| **Ctrl+L** | Select line |
| **Ctrl+Shift+K** | Delete line |
| **F12** / **Shift+Click** | Go to definition |
| **Shift+F12** | Find references |
| **Ctrl+Shift+R** | Rename symbol |
| **Ctrl+Shift+O** | Definition Files list |
| **F8** / **Shift+F8** | Next / previous diagnostic |
| **Ctrl+Tab** / **Ctrl+Shift+Tab** | Next / previous tab |
| **Ctrl+W** | Close current tab |
| **Ctrl++** / **Ctrl+−** / **Ctrl+0** | Font size up / down / reset |

### Other features
- **Syntax highlighting** for C# keywords, types, strings, and comments
- **Format** (toolbar or **Edit → Format Document**) — Roslyn **Format Document** on the current buffer
- **Diagnostics strip** — below the editor, live Roslyn diagnostic counts and the first error/warning message when present
- **Code folding** — fold/unfold supported blocks from gutter fold controls
- **Minimap** / **Line numbers** / **Wrap** — toolbar or **View** menu; same persisted flags as **Settings** → script editor toggles (`editor_settings.json`)
- **Compile** — **Build All** compiles all `.cs` files from `Assets/` and `Packages/` (same pipeline as **Scripts: Compile and Reload Extensions** in the **command palette**, **Ctrl+Shift+P**)
- **Quick open** (**Ctrl+P**) on the main window — jump to a `.cs` file under the project without browsing the Project panel
- **Hot-reload** — recompiles and loads the new assembly into a collectible `AssemblyLoadContext` without restarting the editor
- **Error display** — compilation errors appear in the Console panel with file path, line number, and error message (double-click to open here)
- **Multi-file** — all scripts are compiled together into a single DLL (`Builds/EditorScripts_<timestamp>.dll`)

### Compilation Process
1. All `.cs` files from `Assets/` and `Packages/` are collected
2. Roslyn compiles them into a DLL with references to the engine assembly
3. The assembly is loaded into a collectible `AssemblyLoadContext`
4. New `Behavior` types become available in the "Add Component" dropdown
5. New `EditorExtension` types are discovered and their menus are built
6. Old assemblies are unloaded (previous `AssemblyLoadContext` is collected)

---

## Animation Panel

The Animation panel provides a timeline-based editor for bone animations and a state machine graph for the Animator component.

### Animation Clip Editor
- **Animation clip selection** — choose which clip to edit
- **Keyframe editing** — add, move, and delete keyframes on the timeline
- **Timeline scrubbing** — drag the playhead to preview animation at any point
- **Bone visualization** — see which bones are affected by each keyframe

Bone animations are imported automatically from 3D model files (FBX, glTF) and stored as `.boneanim` files.

### Animator State Machine
When a GameObject with an `Animator` component is selected, the Animation panel displays an interactive state machine graph:
- **States** — rectangular nodes positioned on a canvas, draggable for layout
- **Transitions** — directed arrows between states, shown as lines with arrowheads
- **Add State** — right-click the canvas or use the "Add State" button to create new animation states with a clip reference
- **Add Transition** — click a state, then click another state to create a transition between them
- **Delete** — select a state or transition and press Delete to remove it
- **Selection** — click states or transitions to select them (highlighted with a distinct color)
- **Inspector integration** — selected states show their clip assignment and transition conditions in the Inspector

Changes are automatically persisted via DTO synchronization.

---

## Timeline / Cutscene Sequencer Panel

The Timeline Sequencer panel provides a visual editor for creating and editing cinematic sequences, cutscenes, and scripted events. Access via **Window > New Timeline Tab**.

### Overview
```
┌──────────────────────────────────────────────────────────┐
│  [▶ Play] [⏸ Pause] [⏹ Stop]  Loop ☐  Duration: 10.0  │
│  Speed: 1.0  Time: 0.00 / 10.00  Timeline: My Cutscene │
├─────────────────┬────────────────────────────────────────┤
│  Track List     │  Time Ruler + Clip Canvas              │
│  ┌───────────┐  │  0s    2s    4s    6s    8s   10s     │
│  │ Anim Track│  │  ██████████░░░░░░░░░░░░░░░░░░░        │
│  │ Audio     │  │  ░░░░░░░░░██████████████░░░░░░░░      │
│  │ Camera    │  │  ████░░░░░░░░░░░░░██████████████      │
│  │ Activation│  │  ██████████████████████████████        │
│  │ Event     │  │  ░░░░░░░█░░░░░░░░░░░░░░░░░░█░░       │
│  └───────────┘  │                    ▼ (playhead)        │
├─────────────────┴────────────────────────────────────────┤
│  [+ Add Track]                                           │
└──────────────────────────────────────────────────────────┘
```

### GameObject Binding
- Select a GameObject from the dropdown to bind the TimelinePlayer to it
- If the selected GameObject has no `TimelinePlayer` component, click **"Add Player"** to attach one
- Click **"New Timeline"** to create a fresh `TimelineAsset` on the bound player

### Playback Controls
| Control | Description |
|---------|-------------|
| **Play** | Start timeline playback from the current time |
| **Pause** | Pause playback |
| **Stop** | Stop and reset to time 0 |
| **Loop** | Toggle looping behavior |
| **Duration** | Set the total timeline duration (seconds) |
| **Speed** | Playback speed multiplier |
| **Time** | Current playback time (editable for seeking) |

### Track Types
| Type | Color Badge | Description |
|------|-------------|-------------|
| **Animation** | Blue | Plays bone animations on target GameObjects via their Animator component |
| **Camera** | Green | Enables/disables camera GameObjects to switch between viewpoints |
| **Audio** | Orange | Plays audio clips (`.wav`, `.mp3`, `.ogg`) at specified times |
| **Activation** | Purple | Enables/disables target GameObjects during clip time ranges |
| **Event** | Red | Fires named events via the EventBus at clip start times |

### Track Operations
- **Add Track** — click "+ Add Track" and select a track type from the context menu
- **Rename** — edit the track name directly in the track list
- **Mute** — toggle the mute checkbox to silence a track without removing it
- **Delete** — click the X button to remove a track and all its clips

### Clip Operations
- **Add Clip** — right-click on a track in the canvas, or use the "+ Add Clip" button
- **Drag** — click and drag clips to reposition them on the timeline
- **Resize** — drag the left or right edge of a clip to change its start time or duration
- **Edit** — right-click a clip to open a detail editor with all clip properties (start, duration, blend in/out, speed, and type-specific fields)
- **Duplicate** — right-click > Duplicate to copy a clip
- **Delete** — right-click > Delete to remove a clip

### Clip Properties
| Property | Description |
|----------|-------------|
| **Start Time** | When the clip begins (seconds) |
| **Duration** | How long the clip lasts (seconds) |
| **Blend In** | Crossfade-in duration at the start |
| **Blend Out** | Crossfade-out duration at the end |
| **Speed** | Playback speed multiplier for this clip |
| **Asset Path** | Animation or audio file path (Animation/Audio tracks) |
| **Target Name** | Target GameObject name (Camera/Activation/Animation tracks) |
| **Event Name** | Event identifier (Event tracks) |
| **Event Data** | String payload for events (Event tracks) |

### Canvas Interaction
| Action | Input |
|--------|-------|
| **Scrub playhead** | Click on the time ruler |
| **Drag clip** | Click and drag a clip body |
| **Resize clip** | Drag the left or right edge of a clip |
| **Zoom** | Mouse scroll wheel to zoom the time scale |
| **Context menu** | Right-click a clip for edit/duplicate/delete options |

---

## Blueprint panel

The **Blueprint** panel edits **visual behavior graphs** saved as `.blueprint` JSON (typically under `Assets/Blueprints/`). Open it via **Window → New Blueprint Tab** or the command palette entry **Window: New Blueprint Tab**.

### Workflow
- **File** — New / Open / Save / Save As; works on `.blueprint` documents relative to the current project.
- **Nodes** — List of graph nodes; shows a short **behavior preview** summary (linear outline per event).
- **Add node** — Combo box of node kinds + **Add node** button; **Insert** menu has the same palette.
- **Canvas** — Pan (middle drag), zoom (Ctrl + wheel). **Exec flow:** drag from an **out** pin on the right to an **in** pin on the left. Branch nodes expose **Then** and **Else** pins (top/bottom).
- **Inspector column** — Selected node **Title**, **Kind**, description, wire counts, and **Parameters**. For **Get/Set Property (Reflect)** nodes, **mode** and **scope** use **dropdowns**; **type name**, **component type**, and **member path** use **searchable autocomplete** lists (you can still type values manually).

Graphs are assigned to GameObjects with the **Visual Blueprint** component (**Scripting → Visual Blueprint**) via **Blueprint Asset Path**. See [14 — Visual Blueprints](14_Visual_Blueprints.md) for the full node reference, reflect rules, and EventBus details.

---

## Shader Editor

The Shader Editor panel provides a visual node-based interface for creating custom shaders:

### Features
- **Node graph canvas** — drag and drop shader nodes, connect inputs/outputs with wires
- **Live preview** — material preview sphere updates in real-time as nodes are edited
- **Node palette** — Output, TextureSample, Color, Float, Math, Coordinate, Fresnel, Noise nodes
- **GLSL compilation** — the node graph compiles to GLSL vertex + fragment shaders
- **Save/Load** — shader graphs are saved as `.shadergraph` JSON files
- **Custom .shader files** — hand-written GLSL shaders (like `Steel PBR.shader`) are also supported

### Workflow
1. Open via **Window > Shader Editor** or double-click a `.shadergraph` file
2. Add nodes from the palette (right-click canvas)
3. Connect node outputs to inputs by dragging wires
4. The Output node defines the final surface properties (BaseColor, Normal, Metallic, Roughness, Emission, Opacity)
5. Click **Compile** to generate the GLSL shader
6. Assign the compiled shader to materials via the Inspector

### Built-In Shader Graph Assets
Located in `Standard Assets/Shader/`:
| File | Description |
|------|-------------|
| `Steel PBR.shadergraph` | Metallic steel with Cook-Torrance BRDF |
| `Crystalline Nebula.shadergraph` | Animated nebula effect |
| `Neon Emissive.shadergraph` | Bright neon glow material |
| `Matte Concrete.shadergraph` | Rough concrete surface |
| `Gold Mirror.shadergraph` | Highly reflective gold |
| `Blue Fresnel Glow.shadergraph` | Fresnel-based edge glow |
| `Shiny Red Metal.shadergraph` | Polished red metallic surface |

---

## Biome Graph Panel

The Biome Graph panel provides a node-based biome authoring workflow for `PlanetTerrain`.

### Features
- **Node graph editing** — add/move/connect biome nodes on a zoomable canvas
- **Undo/Redo** — graph-level history (`Ctrl+Z` / `Ctrl+Y`)
- **Validation** — checks for missing output wiring, missing biome layers, and circular links
- **Preview** — equirectangular biome color preview generated from compiled graph data
- **Compile & apply** — compiles graph and applies it to all scene `PlanetTerrain` components
- **File workflow** — save/load `.biomegraph` files
- **Vegetation profile management** — select or create vegetation profiles per biome layer (`New`, `Save`, `Delete`, `Reload`)
- **Per-biome vegetation tuning** — `VegetationDensity`, `TreeDensity`, `Patchiness`, and `SeasonalGrowthMultiplier` are exposed directly under profile controls
- **Multi-item grass/tree authoring** — each profile supports multiple weighted grass and tree entries with per-item model path, density multiplier, and scale range
- **Water graph nodes** — `WaterBody` (Ocean / Lake / Pond), `WaterPath`, legacy `River`, `Shore`, `WaterMerge`; wire into **Output.Water** (up to 8 bodies and 8 paths). Compile rebuilds terrain carving, shore sand, orbit shell, and per-chunk water meshes on all scene planets.

### Typical workflow
1. Open via **Window > Biome Graph**
2. Build or load a graph (`.biomegraph`)
3. Wire biome layers to **Output**, and water nodes to **Output.Water** (or leave disconnected for legacy sea-level-only water)
4. Click **Validate** to catch graph issues
5. Click **Compile** to apply updated biome and water settings to active planets

---

## Profiler Panel

The Profiler panel and Game View HUD display real-time performance metrics:

### Metrics Displayed
| Metric | Description |
|--------|-------------|
| **FPS** | Frames per second (current and average) |
| **Frame Time** | Time per frame in milliseconds |
| **Draw Calls** | Number of GPU draw calls per frame |
| **Vertices** | Total vertex count rendered |
| **Triangles** | Total triangle count rendered |
| **Planet Chunks** | Active planet quadtree leaves with meshes (Game View HUD when playing) |
| **Script costs** | Per-behavior Update ms when sampling is enabled (Game View HUD + Profiler) |

Access the full Profiler panel via **Window > Profiler**.

---

## Build Settings Window

The Build Settings window configures game packaging and platform targeting:

### Features
- **Scene list** — select which scenes to include in the build
- **Platform selection** — target Windows, macOS, or Linux (x64/ARM64)
- **Build configuration** — Debug or Release
- **Output path** — choose the build destination folder
- **Build button** — packages the game as a standalone Engine.Player executable

Access via **Project > Build Settings**.

---

## Input Remapping

Access via **Settings > Input** or the Input Remapping window.

### Default Axis Bindings
| Axis | Positive Keys | Negative Keys | Type | Sensitivity | Gravity | Snap |
|------|---------------|---------------|------|-------------|---------|------|
| Horizontal | D, Right Arrow | A, Left Arrow | Key | 6.0 | 12.0 | true |
| Vertical | W, Up Arrow | S, Down Arrow | Key | 6.0 | 12.0 | true |
| Mouse X | — | — | Mouse | 1.0 | 0 | false |
| Mouse Y | — | — | Mouse | 1.0 | 0 | false |

### Default Action Bindings
| Action | Key/Button |
|--------|------------|
| Jump | Space |
| Sprint | Left Shift |
| Fire1 | Left Mouse Button |

### Axis Smoothing
Axes use acceleration-based smoothing:
- **Sensitivity** — how fast the axis value moves toward the target (default: 6.0)
- **Gravity** — how fast the axis returns to zero when released (default: 12.0)
- **Snap** — if true, axis snaps to zero when input direction reverses (default: true for key axes)
- Mouse axes use raw per-frame deltas with no smoothing

### Persistence
Bindings are saved per-project to `ProjectSettings/input.bindings.json` in JSON format, including all axes, actions, and mouse sensitivity settings. They can be customized through the Input Remapping UI or programmatically via `Input.SetAxis()` and `Input.SetAction()`.

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| **Ctrl+S** | Save scene |
| **Ctrl+Z** | Undo |
| **Ctrl+Y** | Redo |
| **Ctrl+B** | Compile scripts |
| **Delete** | Delete selected object |
| **F5** | Play / Stop game |
| **F6** | Pause game |

---

## Menu Bar

### Project Menu
| Item | Description |
|------|-------------|
| **New Project** | Create a new project folder with structure |
| **Open Project** | Load an existing project from `project.json` and auto-restore `lastOpenedScenePath` if available |
| **Close** | Close the current project |
| **Save Scene** | Save the current scene to `.scene` file |
| **Load Scene** | Open and load a `.scene` file (also updates `lastOpenedScenePath` in `project.json`) |
| **Autosave** | Toggle autosave and set 1/5/10 minute intervals (per-project) |
| **Recent** | Open recent/pinned projects and pin/unpin current project |
| **Validate Project** | Run missing-reference checks and print results to Console |
| **Build Settings** | Open the Build Settings window |

### Window Menu
| Item | Description |
|------|-------------|
| **Reset Layout** | Restore default panel arrangement |
| **Shader Editor** | Open the visual shader graph editor |
| **Biome Graph** | Open the biome graph editor for `PlanetTerrain` |
| **New Animation Tab** | Open a new Animation panel tab |
| **New Timeline Tab** | Open a new Timeline Sequencer panel tab |
| **New Blueprint Tab** | Open a new Visual Blueprint graph editor tab (`.blueprint` assets) |
| **Profiler** | Open the performance profiler panel |
| Panel list | Open/focus specific panels |

### Settings Menu
| Item | Description |
|------|-------------|
| **Input** | Open the Input Remapping window |

### Extension Menus
Additional menus appear dynamically when editor extensions are compiled. Each `EditorExtension` class can add top-level menus with items, separators, sub-menus, toggles, and command invocations.

---

## Planet Atmosphere Authoring

To author atmosphere and clouds for a planet:

1. Select your planet root object with `PlanetTerrain`
2. Add `PlanetAtmosphere` from the Environment component list
3. Tune atmosphere properties (height, blend, scattering controls, tints)
4. Tune cloud properties (coverage, density, detail, speed, softness)
5. Optionally set `UseDirectionalLight` or provide a per-planet sun override

Important:
- Planet atmosphere/clouds are not driven by `Skybox`.
- `Skybox` still controls world background sky only.
- You can keep a `Skybox` for scene backdrop while each planet has distinct atmosphere settings.
