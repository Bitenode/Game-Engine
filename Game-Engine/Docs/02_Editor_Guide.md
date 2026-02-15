# Game Engine — Editor Guide

## Editor Layout

The editor window contains seven dockable panels distributed across five dock regions:

```
┌──────────────┬──────────────────────────┬──────────────┐
│  Hierarchy   │   Scene View / Game View │  Inspector   │
│              │                          │              │
│  (scene      │  (3D viewport with       │  (properties │
│   tree)      │   camera controls and    │   of selected│
│              │   terrain editing)       │   object)    │
│              │                          │              │
├──────────────┴──────────────────────────┴──────────────┤
│  Project Panel              │  Console / Animation     │
│  (file browser)             │  (log output / timeline) │
└─────────────────────────────┴──────────────────────────┘
```

### Panel Management
- **Rearrange** panels by dragging their tab headers between regions
- **Float** any panel into a standalone `ToolWindow` via right-click > Float
- **Dock** floating panels back to any region via right-click > Dock To
- **Duplicate** panels with right-click > New Tab (tabs are auto-numbered, e.g., "Scene View (2)")
- **Close** panels via right-click > Close
- **Reset** to defaults with **Window > Reset Layout**
- Each dock region supports multiple tabs — Scene View and Game View share the Center region by default

---

## Scene View

The Scene View is the primary 3D editing viewport. It renders the scene using the full GPU pipeline (shadows, PBR materials, terrain splatmaps, particles, water) and provides tools for manipulating objects.

### Camera Controls
| Action          | Input                        | Notes |
|-----------------|------------------------------|-------|
| **Orbit**       | Left-click + drag            | Orbits around the focus point |
| **Pan**         | Middle-click + drag          | Moves the camera laterally |
| **Zoom**        | Scroll wheel                 | Dolly toward/away from focus |
| **Fly**         | Right-click + drag           | Free camera rotation (FPS-style) |

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
When an object is selected and a tool is active (Move/Rotate/Scale), colored axis gizmos appear:
- **Red** = X axis
- **Green** = Y axis
- **Blue** = Z axis

Click and drag an axis handle to constrain movement/rotation/scale to that axis. Gizmos maintain constant screen size regardless of camera distance.

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

### Terrain Editing
When a Terrain is selected, the Scene View enters terrain editing mode:
- A circular brush indicator follows the mouse on the terrain surface
- 10 brush tools are available in the Inspector (see Terrain System doc)
- Left-click applies the tool, right-click applies the inverse
- Brush strokes auto-save terrain data on mouse release

---

## Game View

The Game View shows the game as it would appear to the player, rendered through the first enabled Camera component in the scene.

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
5. Terrain data is reloaded from `.terrain.json` files

The Game View also displays an FPS counter next to the stop button for performance monitoring.

### Input During Play Mode
During play mode, the Game View captures input and feeds it to the `Input` system:
- Keyboard state (held/down/up transitions per frame)
- Mouse position, delta, and button state
- Axis smoothing is applied (Sensitivity/Gravity parameters)
- Action bindings are evaluated (Jump, Sprint, Fire1, etc.)

---

## Hierarchy Panel

Shows the scene as a tree of GameObjects with expand/collapse nodes for the parent-child hierarchy.

### Actions
| Action | Input | Description |
|--------|-------|-------------|
| **Select** | Click | Select the object (shown in Inspector, highlighted in Scene View) |
| **Context menu** | Right-click | Create objects, import models, delete |
| **Reparent** | Drag and drop | Move objects in the hierarchy (updates parent-child relationships) |
| **Expand/Collapse** | Arrow click | Navigate nested GameObjects |

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
- **GameObject name** — editable text field
- **LogLifecycle** checkbox — debug toggle that logs behavior lifecycle calls to the Console

### Components
Each component (Behavior) on the selected object shows:
- **Enable checkbox** — toggle the component on/off
- **Component name** — type label (e.g., "Transform", "MeshRenderer", "PlayerMovement")
- **Remove button** — delete the component (Transform cannot be removed)
- **Properties** — all `[Persist]`-marked properties with type-appropriate editors

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
Click **"+ Add Component"** at the bottom of the Inspector to open the component picker. Available built-in components include:
- Transform, Camera, Light
- MeshFilter, MeshRenderer, SkinnedMeshRenderer
- BoxCollider, CapsuleCollider, MeshCollider
- CharacterController, PlayerMovement, Rigidbody
- Skybox, Terrain, Tree, TreeLOD
- ParticleEmitter, PostProcessVolume
- AudioSource, AudioListener
- Animator, Decal, NavMeshAgent, Water
- Any custom script behaviors compiled from `Assets/` or `Packages/`

### Terrain Inspector
When a Terrain is selected, the Inspector shows specialized sections:
1. **Terrain Tools** — toolbar of 10 brush tools for sculpting and painting
2. **Brush Settings** — Radius, Strength, and Falloff sliders
3. **Terrain Layers** — multi-material layer management (up to 8 layers) with texture selection and tiling sliders
4. **Tree Painting Settings** — density, scale range, rotation, and a tree asset list for switching between procedural and imported tree models

### Custom Inspectors
Components can implement `ICustomInspector` to provide custom Avalonia UI in the Inspector panel, or use `[CustomInspector(typeof(TargetComponent))]` on a separate class.

---

## Project Panel

File browser for the project directory with asset management capabilities.

### Folder Structure
```
ProjectRoot/
├── Assets/              # Game assets
│   ├── Models/          # 3D models (FBX, OBJ, glTF)
│   ├── Textures/        # Image files (PNG, JPG, BMP)
│   ├── Materials/       # Material definitions (.material)
│   ├── Scripts/         # C# scripts (.cs)
│   └── Terrain/         # Auto-generated terrain data (.terrain.json)
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

---

## Console Panel

Displays log messages from the engine, scripts, and extensions.

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

Built-in C# script editor integrated into the editor:
- **Syntax highlighting** for C# keywords, types, strings, and comments
- **Compile** button (**Ctrl+B**) — compiles all `.cs` files from `Assets/` and `Packages/`
- **Hot-reload** — recompiles and loads the new assembly into a collectible `AssemblyLoadContext` without restarting the editor
- **Error display** — compilation errors appear in the Console panel with file path, line number, and error message
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

The Animation panel provides a timeline-based editor for bone animations:
- **Animation clip selection** — choose which clip to edit
- **Keyframe editing** — add, move, and delete keyframes on the timeline
- **Timeline scrubbing** — drag the playhead to preview animation at any point
- **Bone visualization** — see which bones are affected by each keyframe

Bone animations are imported automatically from 3D model files (FBX, glTF) and stored as `.boneanim` files.

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

---

## Menu Bar

### Project Menu
| Item | Description |
|------|-------------|
| **New Project** | Create a new project folder with structure |
| **Open Project** | Load an existing project from `project.json` |
| **Close** | Close the current project |
| **Save Scene** | Save the current scene to `.scene` file |

### Window Menu
| Item | Description |
|------|-------------|
| **Reset Layout** | Restore default panel arrangement |
| Panel list | Open/focus specific panels |

### Settings Menu
| Item | Description |
|------|-------------|
| **Input** | Open the Input Remapping window |

### Extension Menus
Additional menus appear dynamically when editor extensions are compiled. Each `EditorExtension` class can add top-level menus with items, separators, sub-menus, toggles, and command invocations.
