# Game Engine — Editor Guide

## Editor Layout

The editor window contains six dockable panels:

```
┌──────────────┬──────────────────────────┬──────────────┐
│  Hierarchy   │     Scene / Game View    │  Inspector   │
│              │                          │              │
│  (scene      │  (3D viewport with       │  (properties │
│   tree)      │   camera controls)       │   of selected│
│              │                          │   object)    │
├──────────────┴──────────────────────────┴──────────────┤
│  Project Panel              │  Console Panel           │
│  (file browser)             │  (log output)            │
└─────────────────────────────┴──────────────────────────┘
```

Panels can be rearranged by dragging their tab headers. Use **Window > Reset Layout** to restore defaults.

---

## Scene View

The Scene View is the primary 3D editing viewport.

### Camera Controls
| Action          | Input                        |
|-----------------|------------------------------|
| **Orbit**       | Left-click + drag            |
| **Pan**         | Middle-click + drag          |
| **Zoom**        | Scroll wheel                 |
| **Fly**         | Right-click + drag (orbit)   |

### Toolbar
| Button     | Function                                    |
|------------|---------------------------------------------|
| **Hand**   | Navigate only (no object manipulation)      |
| **Move**   | Translate selected object along axes         |
| **Rotate** | Rotate selected object                       |
| **Scale**  | Scale selected object                        |
| **AA**     | Anti-aliasing toggle                         |
| **View**   | View options menu                            |
| **FPS**    | Frames per second display                    |

### Gizmos
When an object is selected and a tool is active (Move/Rotate/Scale), colored axis gizmos appear:
- **Red** = X axis
- **Green** = Y axis
- **Blue** = Z axis

Click and drag an axis to constrain movement/rotation/scale to that axis. The gizmo scales to maintain constant screen size regardless of camera distance.

### Collider Gizmos
Toggle the gizmo button to show/hide collider wireframes (green outlines) for BoxColliders, CapsuleColliders, and MeshColliders in the scene.

---

## Game View

The Game View shows the game as it would appear to the player.

### Play Controls
| Button   | Function                                     |
|----------|----------------------------------------------|
| **Play** | Start the game (runs Awake → Start → Update) |
| **Pause**| Pause the game loop                          |
| **Stop** | Stop and restore the scene to its pre-play state |

When you press **Play**:
1. The current scene is snapshotted (including material textures)
2. All Behaviors receive `Awake()` then `Start()`
3. `Update()`, `FixedUpdate()`, and `LateUpdate()` run each frame
4. Input is routed to the game (WASD, mouse look, etc.)

When you press **Stop**:
1. All Behaviors receive `OnDestroy()`
2. The scene is restored from the snapshot
3. Material textures are restored

The Game View also displays an FPS counter next to the stop button.

---

## Hierarchy Panel

Shows the scene as a tree of GameObjects.

### Actions
- **Click** an object to select it (shown in Inspector)
- **Right-click** for context menu:
  - Create primitives (Cube, Cone, Cylinder, Sphere, Quad, Plane)
  - Create empty GameObject
  - Create Camera, Light, Terrain
  - Import 3D model (FBX, OBJ, GLTF, etc.)
  - Delete selected
- **Drag and drop** objects to reparent them
- **Expand/Collapse** nodes to navigate the tree

### Default Scene
When a new project is opened, a default scene is created with:
- Skybox
- Camera
- Directional Light
- Cube

---

## Inspector Panel

Displays and edits properties of the selected GameObject.

### Header
- **GameObject name** (editable text field)
- **LogLifecycle** checkbox (debug: logs behavior lifecycle calls)

### Components
Each component (Behavior) shows:
- **Enable checkbox** — toggle the component on/off
- **Component name** — type label
- **Remove button** — delete the component
- **Properties** — all `[Persist]`-marked properties with appropriate editors

### Property Editors
| Type       | Editor                          |
|------------|---------------------------------|
| `string`   | Text field                      |
| `int`      | Number field                    |
| `float`    | Number field with decimal       |
| `bool`     | Checkbox                        |
| `Vector3`  | Three number fields (X, Y, Z)  |
| `Color`    | Color picker                    |
| `enum`     | Dropdown                        |
| `Material` | Material editor with texture slots |

### Adding Components
Click **"+ Add Component"** at the bottom of the inspector to add a new behavior. Available components include Transform, Camera, Light, MeshFilter, MeshRenderer, MeshCollider, BoxCollider, CapsuleCollider, CharacterController, PlayerMovement, Skybox, Terrain, Tree, TreeLOD, and any custom script behaviors.

### Terrain Inspector
When a Terrain is selected, the Inspector shows specialized sections:
- **Terrain Tools** — 10 brush tools for sculpting and painting (see Terrain System doc)
- **Brush Settings** — Radius, Strength, Falloff sliders
- **Terrain Layers** — Multi-material layer management with texture selection
- **Tree Painting Settings** — Density, scale, rotation, and a multi-asset tree list for switching between procedural and imported tree models

---

## Project Panel

File browser for the project directory.

### Folder Structure
```
ProjectRoot/
├── Assets/          # Game assets (models, textures, scripts, materials)
│   └── Terrain/     # Auto-generated terrain data files
├── Scenes/          # Scene files (.scene)
├── Packages/        # Extension packages and scripts
├── Builds/          # Compiled script assemblies
└── Temp/            # Temporary files
```

### Actions
- **Double-click** a `.cs` file to open the Script Editor
- **Double-click** a `.material` file to inspect it
- **Right-click** for context menu:
  - New Script, Scene, Material, Folder
  - Import external files
  - Reveal in Explorer
  - Refresh

---

## Console Panel

Displays log messages from the engine and scripts.

### Message Types
| Icon/Color | Level     | Source                    |
|------------|-----------|---------------------------|
| Blue       | Info      | General information       |
| Yellow     | Warning   | Non-critical issues       |
| Red        | Error     | Errors and exceptions     |
| Green      | Success   | Operation completed       |
| Gray       | Debug     | Debug output              |

### Commands
Type in the input field and press Enter:
- `help` — List available commands
- `clear` — Clear the console
- `log <message>` — Output a message

---

## Script Editor

Built-in C# script editor with:
- Syntax highlighting
- **Compile** button (Ctrl+B) — compiles all project scripts
- Hot-reload — recompiles and loads new assembly without restarting
- Error display in the console

Scripts are standard C# classes that inherit from `Behavior` and use the lifecycle methods (Update, Start, etc.).

---

## Input Remapping

Access via **Settings > Input** or the Input Remapping window.

### Default Bindings
| Action     | Keys              |
|------------|-------------------|
| Horizontal | A/D or Left/Right |
| Vertical   | W/S or Up/Down    |
| Mouse X    | Mouse movement    |
| Mouse Y    | Mouse movement    |
| Jump       | Space             |
| Sprint     | Left Shift        |
| Fire1      | Left mouse button |

Bindings are saved per-project and can be customized through the remapping UI.
