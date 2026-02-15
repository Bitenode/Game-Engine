# Game Engine — Architecture Overview

## What Is It?

A full-featured 3D game engine and editor built with **C# (.NET 9.0)**, **Avalonia 11** for the cross-platform UI framework, and **Silk.NET OpenGL** for GPU-accelerated rendering. The architecture follows a component-based design similar to Unity, with a scene graph, inspector, hierarchy panel, play mode, runtime scripting, physics, audio, animation, post-processing, and an extensible plugin system.

---

## Technology Stack

| Layer             | Technology                                    | Version   | Purpose                                    |
|-------------------|-----------------------------------------------|-----------|--------------------------------------------|
| Runtime           | .NET                                          | 9.0       | Core runtime and language features         |
| UI Framework      | Avalonia                                      | 11.*      | Cross-platform desktop UI (Windows/macOS/Linux) |
| Rendering         | Silk.NET OpenGL / OpenGL ES 3.0 (ANGLE)      | 2.23.0    | GPU-accelerated 3D rendering               |
| 3D Import         | AssimpNet                                     | 4.1.0     | FBX, OBJ, glTF/GLB, DAE model loading     |
| Image Loading     | SkiaSharp                                     | 2.88.9    | PNG, JPG, BMP texture decoding             |
| Scripting         | Roslyn (Microsoft.CodeAnalysis.CSharp)        | 4.14.0    | Runtime C# compilation and hot-reload      |
| Audio             | NAudio                                        | 2.2.1     | Audio playback with spatial 3D sound       |
| Reactive          | System.Reactive                               | 6.1.0     | Event-driven programming patterns          |
| Code Generation   | AutoConstructor                               | 5.6.0     | Source-generated constructors               |

### Build Configuration
- **Unsafe Blocks**: Enabled (required for OpenGL interop and pointer manipulation)
- **Language Version**: Latest C# features
- **Nullable**: Enabled project-wide
- **Implicit Usings**: Enabled
- **Target**: .NET 9.0, `WinExe` output type
- **Conditional Defines**: `DEBUG` + `TRACE` (Debug), `TRACE` (Release), `PLAYER` (Engine.Player only)

The solution contains two projects: **Game_Engine** (the editor) and **Engine.Player** (standalone game player with multi-platform publish support). See the [Build Settings](12_Build_Settings.md) document for full details on project configuration, dependencies, publishing, and ANGLE/OpenGL setup.

---

## Project Structure

```
Game-Engine/
├── Core/                        # Engine runtime (non-UI)
│   ├── Component/               # All attachable components (27 component types)
│   │   ├── Transform.cs         # Position, rotation, scale (mandatory)
│   │   ├── Camera.cs            # Perspective/orthographic camera
│   │   ├── Light.cs             # Directional, point, spot lights
│   │   ├── MeshFilter.cs        # Mesh geometry holder
│   │   ├── MeshRenderer.cs      # Mesh rendering with materials
│   │   ├── SkinnedMeshRenderer.cs # GPU bone skinning
│   │   ├── Terrain.cs           # Heightmap terrain with splatmaps
│   │   ├── Tree.cs              # Procedural/imported trees with wind
│   │   ├── TreeLOD.cs           # Tree level-of-detail management
│   │   ├── Water.cs             # Gerstner wave water rendering
│   │   ├── Skybox.cs            # Sky gradient + equirectangular texture
│   │   ├── BoxCollider.cs       # Box collision shape
│   │   ├── CapsuleCollider.cs   # Capsule collision shape
│   │   ├── MeshCollider.cs      # Mesh-based collision
│   │   ├── Collider.cs          # Base collider class
│   │   ├── CharacterController.cs # Physics character controller
│   │   ├── PlayerMovement.cs    # FPS/TPS player movement
│   │   ├── Rigidbody.cs         # Physics body
│   │   ├── RigidbodyPlayer.cs   # Player physics controller
│   │   ├── ParticleEmitter.cs   # Particle system
│   │   ├── PostProcessVolume.cs # Post-processing effects
│   │   ├── AudioSource.cs       # 3D spatial audio emitter
│   │   ├── AudioListener.cs     # Audio listener
│   │   ├── Animator.cs          # Skeletal animation state machine
│   │   ├── Decal.cs             # Decal projection
│   │   ├── NavMeshAgent.cs      # Navigation agent
│   │   └── VegetationInstance.cs # Vegetation placement
│   ├── Rendering/               # Scene renderer, materials, overlays
│   │   ├── SceneRenderer.cs     # Main rendering pipeline
│   │   └── GPU/                 # OpenGL wrappers
│   │       ├── GLContext.cs     # OpenGL context (ANGLE detection)
│   │       ├── ResourceCache.cs # Mesh/texture GPU caching
│   │       ├── GPUMesh.cs       # VAO/VBO/EBO management
│   │       ├── GPUTexture.cs    # Texture upload and formats
│   │       ├── ShaderProgram.cs # Shader compilation and uniforms
│   │       ├── GPUFramebuffer.cs# FBO for off-screen rendering
│   │       ├── FullscreenQuad.cs# Fullscreen triangle for post-processing
│   │       └── ShaderSources.cs # All embedded GLSL shaders
│   ├── Extensibility/           # Editor extension system
│   │   ├── ExtensionService.cs  # Extension discovery and loading
│   │   ├── EditorExtension.cs   # Base class for extensions
│   │   ├── EditorUI.cs          # Extension API wrapper
│   │   ├── MenuBuilder.cs       # Fluent menu builder
│   │   └── MenuModel.cs         # Menu tree structure
│   ├── Importers/               # Model import (AssimpNet)
│   │   └── ModelImporter.cs     # Full import pipeline with skeleton/animation
│   ├── Input/                   # Input system
│   │   ├── Input.cs             # Frame-based input manager
│   │   └── Keys.cs              # Platform-agnostic key codes
│   ├── Lighting/                # Shadow mapping
│   ├── Math/                    # Picking, transform utilities, projection
│   ├── Meshes/                  # LOD, mesh utilities
│   │   ├── MeshLod.cs           # Screen-space LOD for procedural meshes
│   │   └── MeshUtil.cs          # Bounding radius, dimension estimation
│   ├── Physics/                 # Collision detection
│   │   ├── CollisionWorld.cs    # Runtime collision manager
│   │   ├── Physics.cs           # Static convenience wrapper (Unity-style)
│   │   └── PhysicsCache.cs      # Per-frame physics query caching
│   ├── UIX/                     # Declarative UI framework (21 widget types)
│   │   ├── UIX.cs               # VNode definitions + static builder API
│   │   ├── UIXRenderer.cs       # VNode → Avalonia control renderer
│   │   └── WindowKit.cs         # Standalone window creation + chrome
│   ├── Behavior.cs              # Base component class
│   ├── GameObject.cs            # Scene entity
│   ├── SceneService.cs          # Scene graph management
│   ├── ProjectService.cs        # Project lifecycle management
│   ├── SelectionService.cs      # Multi-select object tracking
│   ├── UndoService.cs           # Command-pattern undo/redo
│   ├── CameraService.cs         # Active camera tracking
│   ├── CommandRegistry.cs       # Named command system
│   ├── Log.cs                   # Global logging
│   ├── Time.cs                  # Frame timing
│   ├── AudioBackend.cs          # NAudio playback backend
│   ├── AudioManager.cs          # Audio channel management
│   └── SceneSerialization.cs    # JSON scene serialization
├── Views/                       # Editor UI panels (Avalonia controls)
│   ├── SceneView.cs             # 3D scene editing viewport
│   ├── GameView.cs              # Game runtime viewport
│   ├── InspectorPanel.axaml.cs  # Property inspector
│   ├── HierarchyPanel.axaml.cs  # Scene tree view
│   ├── ProjectPanel.axaml.cs    # File browser
│   ├── ConsolePanel.axaml.cs    # Log console
│   ├── AnimationPanel.axaml.cs  # Animation editor
│   ├── GamePanel.axaml.cs       # Game view container
│   ├── ScenePanel.axaml.cs      # Scene view container
│   ├── ScriptEditorWindow.axaml.cs # Code editor
│   ├── InputRemappingWindow.axaml.cs # Input configuration
│   └── ColliderGizmos.cs        # Collider wireframe rendering
├── Docking/                     # Dockable panel system
│   ├── DockManager.cs           # 5-region dock management
│   └── ToolWindow.cs            # Floating window wrapper
├── Standard Assets/             # Built-in assets
│   ├── Skybox/                  # 47 equirectangular sky textures
│   ├── Glass/                   # Glass material textures
│   └── Code Examples/           # Extension code samples
├── Program.cs                   # Application entry point
├── MainWindow.axaml(.cs)        # Main editor window with menus
├── App.axaml(.cs)               # Avalonia application definition
├── Game_Engine.csproj           # Project file with all dependencies
└── Game_Engine.sln              # Solution file
```

---

## Core Concepts

### GameObject

The fundamental entity in the scene. Every object in the scene is a `GameObject`. Each has:
- A **Name** displayed in the Hierarchy panel (editable)
- A mandatory **Transform** component (position, rotation, scale) — always present, cannot be removed
- An `ObservableCollection<Behavior>` of **Behaviors** (components) that provide functionality
- An `ObservableCollection<GameObject>` of **Children** forming the scene graph hierarchy
- An optional **Parent** (prevents circular parent-child relationships)
- Optional **PrefabId** / **PrefabPath** for prefab references

Key methods:
- `AddChild(go)` — adds a child (validates no ancestor cycles)
- `RemoveFromParent()` — detaches from parent
- `AddBehavior<T>()` — adds a component and calls `OnEnable()` if enabled
- `RemoveBehavior(b)` — removes a component (cannot remove Transform)
- `IsAncestorOf(go)` — checks ancestry to prevent cycles

Implements `INotifyPropertyChanged` for UI data binding.

### Behavior (Component)

The base class for all attachable components. Inherits from `ObservableObject` for property change notifications. Behaviors provide functionality to GameObjects through lifecycle methods:

| Method | When Called |
|--------|------------|
| `Awake()` | Once when the behavior is first created during play mode |
| `Start()` | Once before the first Update call |
| `Update()` | Every frame during play mode |
| `FixedUpdate()` | At fixed time intervals (physics simulation) |
| `LateUpdate()` | After all Update calls finish for the frame |
| `OnEnable()` | When the component is enabled or first attached |
| `OnDisable()` | When the component is disabled or detached |
| `OnDestroy()` | When the component is removed or the scene unloads |
| `PostDeserialize()` | After scene deserialization is complete |

Key properties:
- `[Persist] Enabled` — enable/disable the component (default: `true`)
- `[Persist] LogLifecycle` — debug: logs lifecycle calls to console (default: `false`)
- `gameObject` — the owning GameObject
- `Transform` — shortcut to `gameObject.Transform`
- `IsActiveAndEnabled` — whether the component is active

Utility methods:
- `GetComponent<T>()` — find a sibling component by type
- `GetComponentRequired<T>()` — find or throw
- `HasComponent<T>()` — check if a sibling component exists
- `GetOrAddComponent<T>()` — find or auto-create a sibling component
- `EnsureDependenciesNow(notify)` — ensure all `[Require]`-declared components exist
- `LogInfo()`, `LogWarning()`, `LogError()`, `LogSuccess()`, `LogDebug()` — logging shortcuts

Components can declare dependencies with `[Require(typeof(OtherComponent))]`, which automatically adds missing components when the behavior is attached to a GameObject.

### Scene Graph

The scene is a forest of `GameObject` trees. `SceneService.Root` holds the top-level objects as an `ObservableCollection<GameObject>`. Children inherit parent transforms (hierarchical positioning). The hierarchy is displayed in the Hierarchy panel and traversed by the renderer for drawing and culling.

### Services (Static Singletons)

| Service | Purpose |
|---------|---------|
| `SceneService` | Manages the scene root `ObservableCollection`, save/load to `.scene` files, vegetation rebuild, change notifications |
| `ProjectService` | Project lifecycle — create, open, close projects; manages `project.json` manifest, folder structure, asset paths, timestamps |
| `SelectionService` | Tracks currently selected GameObjects with **multi-select** support (`Selected` list, `Current` primary, `Set/Add/Remove/Toggle/Clear` methods) |
| `UndoService` | Command-pattern undo/redo with dual stacks; `ICmd` interface with `Do()`/`Undo()`; `PropertyChangeCmd` for property edits |
| `Input` | Frame-based input polling with axis smoothing, action bindings, mouse delta tracking, and save/load to `input.bindings.json` |
| `CameraService` | Tracks active cameras in the scene |
| `ExtensionService` | Discovers, loads, and hot-reloads editor extensions from compiled assemblies using collectible `AssemblyLoadContext` |
| `CommandRegistry` | Central command registration and invocation system for menus and shortcuts |
| `AudioManager` | Volume channels (Master/Music/SFX), `AudioSource` registry, listener management, global playback control |
| `AudioBackend` | Low-level NAudio playback — per-sound `WaveOutEvent`, `AudioHandle` wrapping, looping, auto-cleanup |
| `Log` | Global logging with severity levels (Info, Warning, Error, Success, Debug); messages appear in the Console panel |
| `Time` | Frame timing — `DeltaTime`, `ElapsedTime`, fixed timestep |

---

## Data Flow

```
User Action (mouse/keyboard)
    │
    ▼
SceneView / GameView (Avalonia OpenGL controls)
    │
    ├─► Input System (keyboard/mouse state, axis smoothing)
    ├─► Selection Service (pick objects via raycasting)
    ├─► Undo Service (record property changes)
    │
    ▼
Scene Graph (GameObjects + Behaviors)
    │
    ├─► Awake → Start → Update → FixedUpdate → LateUpdate (play mode)
    ├─► CharacterController.Simulate() (physics)
    ├─► Animator.Update() (bone animation)
    ├─► ParticleEmitter.Update() (particle simulation)
    ├─► AudioSource (spatial audio updates)
    │
    ▼
SceneRenderer.RenderGPU()
    │
    ├─► Material Warm-Up (MaterialRebind.RepairScene)
    ├─► Terrain LOD Update (per-chunk distance LOD)
    ├─► Shadow Pass (4096x4096 depth-only FBO, front-face culling)
    ├─► Sky Pass (gradient + equirectangular texture + sun glow)
    ├─► Grid Pass (infinite ground grid with distance fade)
    ├─► Opaque Pass (frustum-culled, standard/terrain/skinned shaders)
    ├─► Water Pass (Gerstner waves, Fresnel, foam)
    ├─► Transparent Pass (back-to-front sorted, alpha blending)
    ├─► Particle Pass (billboard quads, instanced rendering)
    ├─► Gizmo Pass (editor overlays, collider wireframes — Scene View only)
    ├─► Post-Processing Pass (Bloom, Fog, Color Grading, FXAA, Vignette, Underwater)
    └─► GL State Cleanup (restore Avalonia compositor state)
```

---

## Persistence

| Data Type       | Format      | Location                                     | Notes |
|-----------------|-------------|----------------------------------------------|-------|
| Project         | JSON        | `project.json` in project root               | ID, name, paths, timestamps |
| Scenes          | JSON        | `Scenes/*.scene`                             | Full hierarchy + component data |
| Materials       | JSON        | `Assets/**/*.material`                       | PBR properties + texture paths |
| Terrain         | JSON        | `Assets/Terrain/*.terrain.json`              | Auto-saved on brush strokes |
| Input Bindings  | JSON        | `ProjectSettings/input.bindings.json`        | Axes, actions, mouse sensitivity |
| Scripts         | C# source   | `Assets/**/*.cs`, `Packages/**/*.cs`         | Compiled by Roslyn at runtime |
| Compiled Scripts| DLL         | `Builds/EditorScripts_<timestamp>.dll`       | Auto-generated, hot-reloaded |
| Animations      | Custom      | `*.boneanim`                                 | Bone animation data |

Properties marked with `[Persist]` on Behaviors are automatically serialized/deserialized by `SceneSerialization`. Supported types include `string`, `int`, `float`, `bool`, `Vector3`, `Color`, enums, `List<T>`, and `float[]`.

---

## Entry Point

`Program.cs` initializes the Avalonia application and wires up scene serialization resolvers:

1. **`WireSceneSerialization()`** — registers three resolvers for deserialization:
   - `ResolveMeshesFromModelPath` — multi-mesh resolver using `ModelImporter.ImportModel()`, collects meshes via DFS from `MeshFilter.Mesh`
   - `ResolveMeshFromModelPath` — single-mesh fallback (returns first mesh from DFS)
   - `ResolveMaterialFromPath` — material resolver via `ProjectService.MaterialsLoad()`

2. **Avalonia startup** — configures platform detection, Inter font, and logging

3. **`MainWindow`** — creates the dockable panel layout using `DockManager` with 5 dock regions:
   - **Left** — Hierarchy panel
   - **Center** — Scene View and Game View (tabbed)
   - **Right** — Inspector panel
   - **Bottom Left** — Project panel
   - **Bottom** — Console panel and Animation panel

The main window also builds the menu bar (Project, Window, Settings menus) and dynamically appends extension menus when scripts are compiled.

---

## Docking System

The editor uses a custom `DockManager` that organizes panels into 5 named regions (`LeftTabs`, `CenterTabs`, `RightTabs`, `BottomLeftTabs`, `BottomTabs`). Each panel is registered with a base title, default region, and factory method.

Features:
- **Tab-based docking** — multiple panels per region as tabs
- **Floating windows** — any panel can be detached into a `ToolWindow`
- **Region transfer** — panels can be moved between regions via context menu
- **Tab numbering** — duplicate panel types get numbered titles (e.g., "Scene View (2)")
- **Context menus** — right-click tab headers for New Tab, Close, Float, Dock To Region options
- **Layout reset** — `ResetLayout()` restores the default arrangement
