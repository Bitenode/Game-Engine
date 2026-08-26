# Game Engine — Architecture Overview

## What Is It?

A full-featured 3D game engine and editor built with **C# (.NET 9.0)**, **Avalonia 11** for the cross-platform UI framework, and **Silk.NET OpenGL** for GPU-accelerated rendering. The architecture follows a component-based design similar to Unity, with a scene graph, inspector, hierarchy panel, play mode, runtime scripting, physics (**triggers**, **`TriggerVolume`** presets, tag/layer filtering), audio, animation, post-processing, and an extensible plugin system. The editor can offload heavy CPU work via **`EditorJobs`** / **`EditorJobScheduler`** (see [Scripting and Extensibility](06_Scripting_And_Extensibility.md)).

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
│   ├── Component/               # All attachable components (37+ types, organized by category)
│   │   ├── Transform.cs         # Position, rotation, scale (mandatory, always present)
│   │   ├── Rendering/           # [ComponentCategory("Rendering")]
│   │   │   ├── Camera.cs        # Perspective/orthographic camera
│   │   │   ├── Light.cs         # Directional, point, spot lights
│   │   │   ├── MeshFilter.cs    # Mesh geometry holder
│   │   │   ├── MeshRenderer.cs  # Mesh rendering with materials
│   │   │   ├── ReflectionProbe.cs # Runtime reflection cubemap / IBL probe
│   │   │   └── SkinnedMeshRenderer.cs # GPU bone skinning
│   │   ├── MeshLodGroup.cs      # [Rendering] Static mesh LOD distances
│   │   ├── Physics/             # [ComponentCategory("Physics")]
│   │   │   ├── Collider.cs      # Base collider class (+ IsTrigger)
│   │   │   ├── BoxCollider.cs   # Box collision shape
│   │   │   ├── CapsuleCollider.cs # Capsule collision shape
│   │   │   ├── MeshCollider.cs  # Mesh-based collision
│   │   │   ├── CharacterController.cs # Physics character controller
│   │   │   ├── PlayerMovement.cs # FPS/TPS player movement
│   │   │   ├── PlanetCollider.cs # Planet collider shell / AABB provider
│   │   │   ├── Rigidbody.cs     # Physics body
│   │   │   ├── RigidbodyPlayer.cs # Player physics controller
│   │   │   └── TriggerVolume.cs # Trigger presets, filters, inspector reactions
│   │   ├── Animation/           # [ComponentCategory("Animation")]
│   │   │   ├── Animator.cs      # Skeletal animation state machine
│   │   │   └── IKConstraint.cs  # Inverse kinematics
│   │   ├── Audio/               # [ComponentCategory("Audio")]
│   │   │   ├── AudioSource.cs   # 3D spatial audio emitter
│   │   │   ├── AudioListener.cs # Audio listener
│   │   │   └── ReverbZone.cs    # Audio reverb zone
│   │   ├── Effects/             # [ComponentCategory("Effects")]
│   │   │   ├── Decal.cs         # Decal projection
│   │   │   ├── ParticleEmitter.cs # Particle system
│   │   │   └── PostProcessVolume.cs # Post-processing effects
│   │   ├── Environment/         # [ComponentCategory("Environment")]
│   │   │   ├── Skybox.cs        # Sky gradient + equirectangular texture
│   │   │   ├── Terrain.cs       # Heightmap terrain with splatmaps
│   │   │   ├── TerrainStreamer.cs # Camera-centered terrain tile streaming
│   │   │   ├── PlanetTerrain.cs # Cube-sphere planet with stacked voxel interior + caves
│   │   │   ├── PlanetVegetationSystem.cs # Biome vegetation streaming
│   │   │   ├── PlanetWeatherController.cs # Biome-blended weather
│   │   │   ├── Tree.cs          # Procedural/imported trees with wind
│   │   │   ├── TreeLOD.cs       # Tree level-of-detail management
│   │   │   ├── VegetationPainter.cs # GPU-instanced vegetation
│   │   │   └── Water.cs         # Gerstner wave water rendering
│   │   ├── Gameplay/            # [ComponentCategory("Gameplay")]
│   │   │   └── PlanetPlayerSpawner.cs # Play-mode player spawn on PlanetTerrain
│   │   ├── Navigation/          # [ComponentCategory("Navigation")]
│   │   │   └── NavMeshAgent.cs  # Navigation agent
│   │   ├── Networking/          # [ComponentCategory("Networking")] — only these 3 are Inspector behaviors
│   │   │   ├── NetworkIdentity.cs   # Network object identity
│   │   │   ├── NetworkTransform.cs  # Network transform sync
│   │   │   └── NetworkAnimator.cs   # Network animation sync
│   │   ├── 2D/                  # [ComponentCategory("2D")]
│   │   │   ├── Camera2D.cs      # 2D camera helper
│   │   │   ├── SpriteRenderer.cs # 2D sprite rendering
│   │   │   └── Tilemap.cs       # 2D tilemap grid
│   │   └── UI/                  # [ComponentCategory("UI")]
│   │       ├── Canvas.cs        # Root UI container (Overlay/Camera/World)
│   │       ├── RectTransform.cs # Anchor-based 2D layout
│   │       ├── UIElement.cs     # Base class for all UI elements
│   │       ├── UIText.cs        # Bitmap font text rendering (BMFont)
│   │       ├── UIImage.cs       # Sprite/texture display (Simple/Sliced/Tiled/Filled)
│   │       ├── UIButton.cs      # Interactive button with color transitions
│   │       ├── UIPanel.cs       # Colored/textured background panel
│   │       ├── UISlider.cs      # Draggable value slider
│   │       ├── UIToggle.cs      # Checkbox/toggle switch
│   │       ├── UIInputField.cs  # Text input with cursor and validation
│   │       └── DefaultFontGenerator.cs # Auto-generates a default bitmap font atlas
│   ├── Rendering/               # Scene renderer, materials, overlays
│   │   ├── SceneRenderer.cs     # Main rendering pipeline
│   │   ├── ShaderGraph/         # Visual shader graph system
│   │   │   ├── ShaderGraph.cs   # Node graph → GLSL compilation
│   │   │   └── ShaderNode.cs    # Node types (Output, Texture, Math, Noise, etc.)
│   │   ├── UI/                  # Runtime UI rendering
│   │   │   ├── CanvasRenderer.cs # Batched quad renderer for UI canvases
│   │   │   └── UIEventSystem.cs # Pointer input dispatch for UI elements
│   │   └── GPU/                 # OpenGL wrappers
│   │       ├── GLContext.cs     # OpenGL context (ANGLE detection)
│   │       ├── ResourceCache.cs # Mesh/texture GPU caching
│   │       ├── GPUMesh.cs       # VAO/VBO/EBO management
│   │       ├── GPUTexture.cs    # Texture upload and formats
│   │       ├── ShaderProgram.cs # Shader compilation and uniforms
│   │       ├── GPUFramebuffer.cs# FBO for off-screen rendering
│   │       ├── FullscreenQuad.cs# Fullscreen triangle for post-processing
│   │       ├── ShaderSources.cs # All embedded GLSL shaders
│   │       └── CustomShaderCache.cs # Custom shader compilation + caching
│   ├── Editor/                  # Editor-only helpers (not runtime components)
│   │   └── EditorJobScheduler.cs # Thread-pool jobs + Avalonia UI post (see EditorJobs façade)
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
│   ├── Planet/                  # Planet terrain generation + chunk streaming
│   │   ├── CubeSphereMath.cs    # Cube<->sphere mapping utilities
│   │   ├── DensityGenerator.cs  # Stacked radial voxel shells + interior bounds
│   │   ├── FaceQuadtree.cs      # Per-face quadtree LOD manager
│   │   ├── PlanetChunkManager.cs# Async chunk generation/apply scheduler
│   │   ├── PlanetConfig.cs      # Planet generation/runtime budgets config
│   │   ├── PlanetDensitySampler.cs # Procedural density + multi-scale caves
│   │   ├── PlanetDensityRaycast.cs # Density ray/spherecast queries
│   │   ├── PlanetMeshGenerator.cs # Heightfield shell + stacked transvoxel
│   │   ├── PlanetNoiseCache.cs  # Shared per-planet noise instances
│   │   ├── PlanetSpace.cs       # World ↔ local unscaled transforms
│   │   ├── PlanetWater.cs       # Planet water shell mesh
│   │   └── QuadNode.cs          # Quadtree node state + interior LOD priority
│   ├── Noise/                   # Procedural noise utilities
│   │   ├── SimplexNoise.cs      # Base 2D/3D simplex noise
│   │   └── FractalNoise.cs      # FBM/ridged/billow noise wrapper
│   ├── Physics/                 # Collision detection and physics
│   │   ├── CollisionWorld.cs    # Runtime collision manager
│   │   ├── Physics.cs           # Static convenience wrapper (Unity-style)
│   │   ├── PhysicsCache.cs      # Per-frame physics query caching
│   │   ├── PhysicsJoint.cs      # Joint constraints (fixed, hinge, spring, slider, ball-socket)
│   │   └── BVH.cs               # Bounding Volume Hierarchy for spatial queries
│   ├── Voxel/                   # Voxel mesh extraction for terrain
│   │   ├── VoxelChunk.cs        # Density/material grid storage
│   │   ├── TransvoxelMesher.cs  # Regular + transition-cell meshing
│   │   └── MarchingCubesTables.cs # Transvoxel lookup tables
│   ├── UIX/                     # Declarative UI framework (21 widget types)
│   │   ├── UIX.cs               # VNode definitions + static builder API
│   │   ├── UIXRenderer.cs       # VNode → Avalonia control renderer
│   │   └── WindowKit.cs         # Standalone window creation + chrome
│   ├── Animation/               # Animation system
│   │   ├── BlendTree.cs         # Animation blend tree
│   │   ├── IKSolver.cs          # Inverse kinematics solvers (TwoBone, FABRIK, LookAt)
│   │   ├── BoneAnimationClip.cs # Bone animation data
│   │   └── AnimationClipAsset.cs# Animation clip asset format
│   ├── Dialogue/                # Dialogue system
│   │   ├── DialogueTree.cs      # Dialogue tree asset (nodes, choices, branches)
│   │   └── DialogueRunner.cs    # Dialogue playback component
│   ├── AI/                      # AI behavior trees
│   │   ├── BehaviorTree.cs      # Behavior tree asset
│   │   ├── BTNode.cs            # Node types (Selector, Sequence, Parallel, etc.)
│   │   ├── BehaviorTreeRunner.cs# Behavior tree executor component
│   │   └── Blackboard.cs        # Key-value data store for AI agents
│   ├── Timeline/                # Timeline / Cutscene sequencer
│   │   ├── Timeline.cs          # TimelineAsset, TimelineTrack, TimelineClip
│   │   └── TimelinePlayer.cs    # Timeline playback component
│   ├── Networking/              # Multiplayer (static API — not Add Component entries)
│   │   ├── NetworkManager.cs           # Static server/client, RPC, registry, message dispatch
│   │   ├── NetworkManager.Spawning.cs  # Runtime spawn/despawn, late-join sync, client input channel
│   │   ├── NetworkManager.Replication.cs # Rate limits, interest filter, disconnect policy, reliable state snap
│   │   ├── NetworkTransport.cs        # Low-level UDP transport layer
│   │   ├── NetworkGameplayRules.cs    # IsAuthoritativePeer, IsRemoteProxy, IsLocallyControlledPlayer
│   │   └── NetworkWorldDiagnostics.cs # Terrain/planet asset fingerprint log for multiplayer
│   ├── Audio/                   # Audio subsystems
│   │   └── AudioMixer.cs        # Hierarchical audio mixing with effects
│   ├── Scene/                   # Runtime scene management
│   │   ├── SceneManager.cs      # Runtime scene loading (deferred, safe transitions)
│   │   └── SceneQuery.cs        # Scene search (FindByName, FindByPath, FindBehaviors)
│   ├── Behavior.cs              # Base component class
│   ├── GameObject.cs            # Scene entity
│   ├── SceneService.cs          # Scene graph management
│   ├── ProjectService.cs        # Project lifecycle management
│   ├── SelectionService.cs      # Multi-select object tracking
│   ├── UndoService.cs           # Command-pattern undo/redo
│   ├── CameraService.cs         # Active camera tracking
│   ├── CommandRegistry.cs       # Named command system (palette lists all; SealBuiltins + extension commands)
│   ├── Log.cs                   # Global logging
│   ├── Time.cs                  # Frame timing
│   ├── AudioBackend.cs          # NAudio playback backend
│   ├── AudioManager.cs          # Audio channel management
│   ├── SceneSerialization.cs    # JSON scene serialization
│   ├── WindSystem.cs            # Global wind for vegetation animation
│   └── Profiler.cs              # Performance profiler with frame statistics
├── Views/                       # Editor UI panels (Avalonia controls)
│   ├── SceneView.cs             # 3D scene editing viewport
│   ├── GameView.cs              # Game runtime viewport
│   ├── InspectorPanel.axaml.cs  # Property inspector
│   ├── HierarchyPanel.axaml.cs  # Scene tree view (name/component filters + collapsible filter strip)
│   ├── EditorCommandPaletteWindow.cs # Ctrl+Shift+P command palette (CommandRegistry)
│   ├── EditorQuickOpenWindow.cs # Ctrl+P project file quick open
│   ├── ProjectPanel.axaml.cs    # File browser
│   ├── ConsolePanel.axaml.cs    # Log console
│   ├── AnimationPanel.axaml.cs  # Animation editor
│   ├── TimelineSequencerPanel.axaml.cs # Timeline / Cutscene sequencer editor
│   ├── GamePanel.axaml.cs       # Game view container
│   ├── ScenePanel.axaml.cs      # Scene view container
│   ├── ScriptEditorWindow.axaml.cs # Code editor
│   ├── InputRemappingWindow.axaml.cs # Input configuration
│   ├── ShaderEditorPanel.axaml.cs  # Visual shader graph editor
│   ├── BiomeGraphPanel.axaml.cs # Biome graph editor for planet generation
│   ├── ProfilerPanel.axaml.cs   # Performance profiler view
│   ├── BuildSettingsWindow.axaml.cs # Build configuration and packaging
│   └── ColliderGizmos.cs        # Collider wireframe rendering
├── Docking/                     # Dockable panel system
│   ├── DockManager.cs           # 5-region dock management
│   └── ToolWindow.cs            # Floating window wrapper
├── Standard Assets/             # Built-in assets
│   ├── Skybox/                  # 47 equirectangular sky textures
│   ├── Glass/                   # Glass material textures
│   ├── Shader/                  # Built-in shaders and shader graphs
│   │   ├── Steel PBR.shader    # Cook-Torrance PBR shader
│   │   └── *.shadergraph       # Pre-built shader graph assets
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
- An **Enabled** flag (default: `true`) — disabled GameObjects and all their children are skipped during Update, rendering, and scene queries. The Hierarchy panel shows disabled objects (and their descendants) in red.
- A mandatory **Transform** component (position, rotation, scale) — always present, cannot be removed
- An `ObservableCollection<Behavior>` of **Behaviors** (components) that provide functionality
- An `ObservableCollection<GameObject>` of **Children** forming the scene graph hierarchy
- An optional **Parent** (prevents circular parent-child relationships)
- Optional **PrefabId** / **PrefabPath** for prefab references

Key properties:
- `Enabled` — enable/disable the entire GameObject. Toggling this calls `SceneService.NotifyChanged()` to refresh all views immediately.
- `IsActiveInHierarchy` — computed property that returns `true` only when this object **and every ancestor** is enabled. Used by behaviors (`IsActiveAndEnabled`) and the rendering pipeline to determine effective visibility.

Key methods:
- `AddChild(go)` — adds a child (validates no ancestor cycles)
- `RemoveFromParent()` — detaches from parent
- `AddBehavior<T>()` — adds a component and calls `OnEnable()` if enabled
- `RemoveBehavior(b)` — removes a component (cannot remove Transform)
- `IsAncestorOf(go)` — checks ancestry to prevent cycles

Implements `INotifyPropertyChanged` for UI data binding. Changing `Enabled` propagates `IsActiveInHierarchy` change notifications to all descendants so the Hierarchy panel updates their display color in real time.

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
- `IsActiveAndEnabled` — `true` only when the component's own `Enabled` is `true` **and** the owning GameObject's `IsActiveInHierarchy` is `true`. The game loop (`Update`, `FixedUpdate`, `LateUpdate`) and all rendering/query systems use this property to skip behaviors on disabled GameObjects.

Utility methods:
- `GetComponent<T>()` — find a sibling component by type
- `GetComponentRequired<T>()` — find or throw
- `HasComponent<T>()` — check if a sibling component exists
- `GetOrAddComponent<T>()` — find or auto-create a sibling component
- `EnsureDependenciesNow(notify)` — ensure all `[Require]`-declared components exist
- `LogInfo()`, `LogWarning()`, `LogError()`, `LogSuccess()`, `LogDebug()` — logging shortcuts

Components can declare dependencies with `[Require(typeof(OtherComponent))]`, which automatically adds missing components when the behavior is attached to a GameObject.

Components are organized into categories using the `[ComponentCategory("Name")]` attribute. The Inspector's **+ Add Component** button opens a hierarchical popup menu where each category is a submenu (e.g., Rendering, Physics, Animation, Audio, Effects, Environment, Navigation, Networking, 2D, UI, AI, Dialogue, Timeline). Custom scripts appear in a separate **Scripts** submenu.

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
| `AudioBackend` | Cross-platform OpenAL playback (via Silk.NET) — native 3D spatial audio, Doppler, distance attenuation; NAudio for file decoding |
| `Log` | Global logging with severity levels (Info, Warning, Error, Success, Debug); messages appear in the Console panel |
| `SceneManager` | Runtime scene loading — deferred to next frame, safe tear-down/rebuild, `SceneLoaded` event |
| `SceneQuery` | Scene search utilities — `FindByName()`, `FindByPath()`, `FindBehaviors<T>()`. Traversal skips disabled GameObjects; `FindBehaviors<T>()` only returns behaviors where `IsActiveAndEnabled` is true |
| `UIEventSystem` | Pointer input dispatch for runtime UI — raycasts screen-space canvases, delivers hover/click/drag events |
| `Time` | Frame timing — `DeltaTime`, `ElapsedTime`, fixed timestep |
| `NetworkManager` | Server/client lifecycle, object registry, RPC system, state broadcast |
| `WindSystem` | Global wind direction and strength for vegetation animation |
| `Profiler` | Frame timing statistics, FPS tracking, per-system performance metrics |
| `AudioMixer` | Hierarchical audio group mixing with volume, effects, and routing |
| `NavMesh` | Static navigation mesh baking, A* pathfinding, and spatial queries |

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
    ├─► DialogueRunner.Update() (dialogue advancement / voice playback)
    ├─► BehaviorTreeRunner.Update() (AI behavior tree ticking)
    ├─► TimelinePlayer.Update() (timeline/cutscene playback)
    ├─► AudioSource (spatial audio updates)
    ├─► IKConstraint (inverse kinematics overrides)
    ├─► NetworkTransform (network state interpolation)
    ├─► NavMeshAgent (pathfinding movement)
    │
    ▼
SceneRenderer.RenderGPU()
    │
    ├─► Material Warm-Up (MaterialRebind.RepairScene)
    ├─► TerrainStreamer.SyncAll + Terrain LOD Update (streaming tiles, per-chunk distance LOD)
    ├─► Shadow Pass (4096x4096 depth-only FBO, front-face culling)
    ├─► Sky Pass (gradient + equirectangular texture + sun glow)
    ├─► Grid Pass (infinite ground grid with distance fade)
    ├─► Opaque Pass (frustum-culled, standard/terrain/skinned shaders)
    ├─► Water Pass (Gerstner waves, Fresnel, foam)
    ├─► Transparent Pass (back-to-front sorted, alpha blending)
    ├─► Particle Pass (billboard quads, instanced rendering)
    ├─► Gizmo Pass (editor overlays, collider wireframes — Scene View only)
    ├─► Volumetric Fog Pass (ray-marched scattering with shadow sampling)
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
| Terrain         | JSON / binary | `Assets/Terrain/*.terrain.json` or `.terrain.bin` | JSON default; binary optional; brush strokes; `TerrainStreamer` tile unload |
| Input Bindings  | JSON        | `ProjectSettings/input.bindings.json`        | Axes, actions, mouse sensitivity |
| Scripts         | C# source   | `Assets/**/*.cs`, `Packages/**/*.cs`         | Compiled by Roslyn at runtime |
| Compiled Scripts| DLL         | `Builds/EditorScripts_<timestamp>.dll`       | Auto-generated, hot-reloaded |
| Animations      | Custom      | `*.boneanim`                                 | Bone animation data |
| Shaders         | Custom      | `*.shader`                                   | Custom GLSL shaders |
| Shader Graphs   | JSON        | `*.shadergraph`                              | Visual shader node graphs |
| Blueprints      | JSON        | `Assets/Blueprints/*.blueprint`              | Visual behavior graphs (Visual Blueprint component) |

Properties marked with `[Persist]` on Behaviors are automatically serialized/deserialized by `SceneSerialization`. Supported types include `string`, `int`, `float`, `bool`, `Vector3`, `Color`, enums, `List<T>`, and `float[]`.

The `GameObject.Enabled` state is also serialized in `.scene` files. To keep files clean, the `enabled` field is only written when `false` (omitted when `true`, which is the default).

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
   - **Bottom** — Console panel, Animation panel, Timeline Sequencer panel, and Blueprint graph tabs (when opened)

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
