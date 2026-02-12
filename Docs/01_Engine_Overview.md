# Game Engine — Architecture Overview

## What Is It?

A full-featured 3D game engine and editor built with C# (.NET 9.0), Avalonia 11 for the UI framework, and Silk.NET OpenGL for GPU-accelerated rendering. The architecture follows a component-based design similar to Unity, with a scene graph, inspector, hierarchy panel, and play mode.

---

## Technology Stack

| Layer           | Technology                    |
|-----------------|-------------------------------|
| UI Framework    | Avalonia 11 (cross-platform)  |
| Rendering       | Silk.NET OpenGL / OpenGL ES 3.0 (ANGLE) |
| 3D Import       | AssimpNet (FBX, OBJ, GLTF, GLB, DAE)   |
| Image Loading   | SkiaSharp                     |
| Runtime         | .NET 9.0                      |
| Scripting       | Roslyn (runtime C# compilation)         |

---

## Project Structure

```
Game-Engine/
├── Core/                    # Engine runtime (non-UI)
│   ├── Component/           # All attachable components (Transform, Camera, Light, Terrain, Tree, etc.)
│   ├── Rendering/           # Scene renderer, materials, overlays
│   │   └── GPU/             # OpenGL wrappers (shaders, textures, meshes, FBOs)
│   ├── Extensibility/       # Editor extension system
│   ├── Importers/           # Model import (Assimp)
│   ├── Input/               # Input system (keyboard, mouse, axes, actions)
│   ├── Lighting/            # Shadow mapping
│   ├── Math/                # Picking, transform utilities
│   ├── Meshes/              # LOD, mesh utilities
│   ├── Physics/             # Collision detection
│   ├── UIX/                 # UI extension rendering
│   └── ...                  # Services (Scene, Project, Selection, Undo, etc.)
├── Views/                   # Editor UI panels (Avalonia controls)
├── Docking/                 # Dockable panel system
├── Standard Assets/         # Built-in assets (skyboxes, glass textures, code examples)
├── Program.cs               # Application entry point
├── MainWindow.axaml(.cs)    # Main editor window
└── App.axaml(.cs)           # Avalonia application definition
```

---

## Core Concepts

### GameObject
The fundamental entity in the scene. Every object in the scene is a `GameObject`. Each has:
- A **Name** (displayed in Hierarchy)
- A mandatory **Transform** (position, rotation, scale)
- A list of **Behaviors** (components)
- A list of **Children** (scene graph hierarchy)
- An optional **Parent**

### Behavior (Component)
The base class for all attachable components. Behaviors provide functionality to GameObjects through lifecycle methods:
- `Awake()` — Called once when the behavior is first created
- `Start()` — Called once before the first Update
- `Update()` — Called every frame
- `FixedUpdate()` — Called at fixed time intervals (physics)
- `LateUpdate()` — Called after all Update calls
- `OnEnable()` / `OnDisable()` — Called when enabled/disabled
- `OnDestroy()` — Called when the behavior is removed

Components can declare dependencies with `[Require(typeof(OtherComponent))]`, which auto-adds missing components.

### Scene Graph
The scene is a forest of `GameObject` trees. `SceneService.Root` holds the top-level objects. Children inherit parent transforms. The hierarchy is displayed in the Hierarchy panel and traversed by the renderer.

### Services (Static Singletons)
| Service            | Purpose                                    |
|--------------------|--------------------------------------------|
| `SceneService`     | Manages the scene root, save/load          |
| `ProjectService`   | Project lifecycle (create, open, close)     |
| `SelectionService` | Tracks the currently selected GameObject    |
| `UndoService`      | Command-pattern undo/redo                   |
| `Input`            | Frame-based input polling                   |
| `CameraService`    | Tracks active cameras                       |
| `ExtensionService` | Hot-loads editor extensions                 |
| `CommandRegistry`  | Central command registration                |

---

## Data Flow

```
User Action (mouse/keyboard)
    │
    ▼
SceneView / GameView (Avalonia OpenGL controls)
    │
    ├─► Input System (keyboard/mouse state)
    ├─► Selection Service (pick objects)
    ├─► Undo Service (record changes)
    │
    ▼
Scene Graph (GameObjects + Behaviors)
    │
    ▼
SceneRenderer.RenderGPU()
    │
    ├─► Frustum Culling
    ├─► Shadow Pass (depth-only FBO)
    ├─► Opaque Pass (standard/terrain shader)
    ├─► Transparent Pass (back-to-front sorted)
    ├─► Skybox Pass
    ├─► Grid Pass
    └─► Gizmo Pass (editor overlays)
```

---

## Persistence

| Data Type      | Format    | Location                              |
|----------------|-----------|---------------------------------------|
| Project        | JSON      | `project.json` in project root        |
| Scenes         | JSON      | `Scenes/*.scene`                      |
| Materials      | JSON      | `Assets/**/*.material`                |
| Terrain        | JSON      | `Assets/Terrain/*.terrain.json` (auto-saved on brush strokes) |
| Input Bindings | JSON      | Project settings                      |
| Scripts        | C# source | `Assets/**/*.cs`, `Packages/**/*.cs`  |

Properties marked with `[Persist]` on Behaviors are automatically serialized/deserialized by `SceneSerialization`.

---

## Entry Point

`Program.cs` initializes the Avalonia application, wires up scene serialization resolvers (model importers, material loaders), and launches the main window. The `MainWindow` creates the dockable panel layout with all editor panels.
