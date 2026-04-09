# Game Engine

A full-featured 3D game engine and editor built from the ground up in **C# (.NET 9.0)**, using **Avalonia 11** for the cross-platform UI framework and **Silk.NET OpenGL** for GPU-accelerated rendering. The architecture follows a component-based design inspired by Unity, providing a complete scene graph, visual editor, runtime scripting, physics, audio, animation, post-processing, and more.

---

## Key Features

### Core Engine
- **Component-Based Architecture** — GameObject/Behavior system with 34+ built-in component types and lifecycle methods (Awake, Start, Update, FixedUpdate, LateUpdate, OnDestroy)
- **Visual Editor** — Dockable panel layout with Hierarchy, Scene View, Game View, Inspector, Project Browser, Console, Animation, Shader Editor, Blueprint graph editor, Profiler, and Build Settings panels
- **C# Scripting** — Runtime compilation via Roslyn with hot-reload, `[Persist]` attribute for automatic serialization, and `[Require]` for component dependencies
- **Visual Blueprints** — Node graphs (`.blueprint`) on the **Visual Blueprint** component: events, branching, delays, variables, scene actions, reflection get/set, and `BlueprintMessageEvent` for C# subscribers ([docs](Game-Engine/Docs/14_Visual_Blueprints.md))
- **Editor Extensions** — Plugin system for custom menus, commands, custom inspectors, and the UIX declarative UI framework (21 widget types)
- **Undo/Redo** — Full command-pattern undo/redo system across all editor operations
- **Play Mode** — Scene snapshot and restore, ensuring runtime changes don't persist after stopping

### Rendering
- **Real-Time 3D Rendering** — Forward rendering pipeline with PBR materials, shadow mapping (4096x4096 PCF), frustum culling, and multi-level LOD
- **Shader Graph** — Visual node-based shader editor with live preview, compiling to GLSL (nodes: Output, TextureSample, Color, Float, Math, Coordinate, Fresnel, Noise)
- **Custom Shaders** — Hand-written `.shader` files with Cook-Torrance BRDF support, plus built-in shader graph assets (Steel PBR, Crystalline Nebula, Neon Emissive, Gold Mirror, and more)
- **Water Rendering** — Gerstner wave displacement, Fresnel-based transparency, foam, and underwater post-processing effects
- **Particle System** — Billboard particles with emission shapes (Sphere, Cone, Box), sub-emitters, and presets (Fire, Smoke, Sparks, Rain, Snow, Dust)
- **Decal Projection** — Runtime decal rendering on surfaces with lifetime, fade-out, and projection modes (Forward, Up, Down)
- **Post-Processing** — Bloom, Fog, Color Grading, Tone Mapping (Reinhard/ACES), Vignette, FXAA, and underwater effects
- **Vegetation System** — GPU-instanced grass, rocks, and debris with chunked rendering, distance culling, and terrain-aware placement

### World Building
- **Terrain System** — Heightmap terrain with 10 sculpting/painting tools, splatmaps (up to 8 layers), chunking, tunable per-chunk LOD (optional hysteresis), optional **`.terrain.bin`** assets, **`TerrainStreamer`** for camera-centered tile streaming, tree painting, and O(1) heightmap collision
- **3D Model Import** — FBX, OBJ, glTF/GLB, DAE via AssimpNet with automatic material extraction, skeleton building, and bone animation import
- **2D Support** — Camera2D with pixel-perfect rendering, SpriteRenderer, Tilemap with sparse storage and per-tile collision
- **Navigation** — NavMeshAgent with A* pathfinding, navmesh baking from scene geometry, obstacle avoidance, and auto-repath
- **Runtime UI System** — GPU-rendered in-game UI with Canvas (Overlay/Camera/WorldSpace), RectTransform anchor layout, 8 widget types (Text, Image, Button, Panel, Slider, Toggle, InputField), pointer event system, bitmap font rendering (BMFont/SDF), and responsive scaling
- **Scene Management** — Runtime scene loading with deferred transitions, scene queries (`FindByName`, `FindByPath`, `FindBehaviors<T>`), and scene-loaded events

### Physics & Animation
- **Physics & Collision** — BoxCollider, CapsuleCollider, MeshCollider, BVH spatial acceleration, CharacterController with gravity, slope limiting, step climbing, coyote time, and CCD
- **Physics Joints** — Fixed, Hinge, Spring, Slider, and Ball-Socket joint constraints
- **Player Controllers** — PlayerMovement (sweep-and-slide) and RigidbodyPlayer (momentum-based with swimming)
- **Animation System** — Bone-based skeletal animation with GPU skinning, animation state machine, blend trees, and keyframe editing
- **Inverse Kinematics** — IKConstraint with TwoBone (arms/legs), LookAt (head tracking), and FABRIK (multi-joint chains)

### Audio & Networking
- **Audio System** — Cross-platform OpenAL backend (via Silk.NET) with native 3D spatial audio, distance attenuation, Doppler effect, and audio occlusion (raycast-based obstruction detection)
- **Audio Mixer** — Hierarchical mixer groups (Master > Music/SFX/UI/Voice), 7 effect types (Reverb, Echo, LowPass, HighPass, Chorus, Distortion, Compressor), snapshots with smooth transitions, and 10 reverb zone presets
- **Networking** — Static `NetworkManager` API (server/client, RPCs, UDP transport with keepalive and idle disconnect) plus Inspector components NetworkIdentity, NetworkTransform (interpolated sync), and NetworkAnimator (state sync); Standard Assets include sample menu/server UI (`MainMenuController`, `ServerHostController` / `Main Menu.scene`, `Server.scene`). The game loop invokes `NetworkManager.Update` automatically in Game View and Engine.Player.

### Tools & Profiling
- **Profiler** — Real-time FPS, frame time, draw call, and vertex/triangle count monitoring
- **Build Settings** — Package games as standalone Engine.Player executables for Windows, macOS, and Linux (x64/ARM64)
- **Wind System** — Global wind parameters driving tree and vegetation animation

### Combined feature notes (this branch)

The following areas were developed together; see the linked docs for detail.

| Area | What changed |
|------|----------------|
| **Terrain** | Optional **`.terrain.bin`** heightmap assets; tunable **LOD distance bands** and **`LodHysteresisWorld`** to reduce popping; **`CollisionLodStep`** for lighter physics meshes while keeping full-res height sampling; **`TerrainStreamer`** component for camera-centered tile load/unload with optional **collision ring**. **Scene View**, **Game View**, and **Engine.Player** call **`TerrainStreamer.SyncAll`** each frame. |
| **Networking** | UDP transport **keepalive** (server ping / client pong), **idle and handshake timeouts**, **IPv4 / IPv4-mapped IPv6** endpoint matching; **`NetworkManager.Update`** driven from **Game View** and **Player View** so sessions survive scene changes; **PlayerWindow** calls **`NetworkManager.Stop`** on close. |
| **Standalone player** | **Canvas** screen-space UI via **`CanvasRenderer`**; **`UIEventSystem`** + **`Input.FeedMousePosition`**; linked **Standard Assets** UI scripts (**`MainMenuController`**, **`ServerHostController`**) so scene types resolve; **BCnEncoder.Net** / **Magick.NET** package parity for shared Core texture paths. |
| **Standard Assets** | **Main Menu**: **Join** button and client connect fields (**`JoinHost`**, **`JoinPort`**). **Server** sample: **`Server.scene`** + **`ServerHostController`** (host UI, optional game scene / save slot, log mirror). |
| **Documentation** | Updates across **01, 02, 03, 04, 05, 07, 09, 12** plus this README to match the above. |

---

## Technology Stack

| Layer             | Technology                             | Version   |
|-------------------|----------------------------------------|-----------|
| Runtime           | .NET                                   | 9.0       |
| UI Framework      | Avalonia                               | 11.*      |
| Rendering         | Silk.NET OpenGL / OpenGL ES 3.0 (ANGLE)| 2.23.0    |
| Audio Playback    | Silk.NET OpenAL (cross-platform)       | 2.23.0    |
| Audio Decoding    | NAudio (WAV/MP3/OGG file reading)      | 2.2.1     |
| 3D Model Import   | AssimpNet                              | 4.1.0     |
| Image Loading     | SkiaSharp                              | 2.88.9    |
| Scripting         | Roslyn (Microsoft.CodeAnalysis.CSharp) | 4.14.0    |
| Reactive          | System.Reactive                        | 6.1.0     |
| Code Generation   | AutoConstructor                        | 5.6.0     |

---

## Getting Started

1. **Clone** the repository
2. **Open** `Game-Engine/Game_Engine.sln` in Visual Studio 2022+ or Rider
3. **Build & Run** — the editor window will appear with a default scene (Skybox, Camera, Light, Cube)
4. **Create a Project** — File > New Project to set up a project folder
5. **Start Building** — Right-click in the Hierarchy to add objects, sculpt terrain, write C# scripts, author **visual blueprints** (**Window → New Blueprint Tab**), and more

---

## Project Structure

```
Github Engine/
├── Game-Engine/          # Editor application (Game_Engine)
│   ├── Core/             # Shared engine runtime (components, rendering, physics, audio, networking)
│   ├── Views/            # Editor UI panels (Scene View, Inspector, Shader Editor, Profiler, etc.)
│   ├── Docking/          # Dockable panel system
│   ├── Standard Assets/  # Built-in shaders, skyboxes, code examples
│   └── Docs/             # Comprehensive documentation (14 guides)
│
├── Engine.Player/        # Standalone game player (multi-platform)
│   └── (Loads pre-compiled games without the editor)
│
└── README.md             # This file
```

---

## Documentation

Comprehensive documentation is available in the `Game-Engine/Docs/` folder:

| Document | Topic |
|----------|-------|
| [01 — Engine Overview](Game-Engine/Docs/01_Engine_Overview.md) | Architecture, tech stack, core concepts, services, data flow |
| [02 — Editor Guide](Game-Engine/Docs/02_Editor_Guide.md) | Editor panels, Scene View, Game View, Inspector, Shader Editor, Blueprint panel, Profiler, Build Settings |
| [03 — Components Reference](Game-Engine/Docs/03_Components_Reference.md) | 34+ built-in components with properties, defaults, and usage |
| [04 — Rendering Pipeline](Game-Engine/Docs/04_Rendering_Pipeline.md) | Render passes, shaders, shader graph, GPU resources, post-processing, particles, water |
| [05 — Terrain System](Game-Engine/Docs/05_Terrain_System.md) | Terrain creation, 10 brush tools, splatmap painting, tree painting, chunking, LOD, binary assets, `TerrainStreamer` |
| [06 — Scripting & Extensibility](Game-Engine/Docs/06_Scripting_And_Extensibility.md) | C# scripting, lifecycle, APIs, editor extensions, command registry, custom inspectors |
| [07 — Physics & Collision](Game-Engine/Docs/07_Physics_And_Collision.md) | Colliders, CharacterController, physics joints, BVH, raycasting, terrain collision |
| [08 — Materials & Textures](Game-Engine/Docs/08_Materials_And_Textures.md) | PBR materials, shader graph materials, texture slots, transparency, custom shaders |
| [09 — Scene & Project Management](Game-Engine/Docs/09_Scene_And_Project_Management.md) | Projects, scenes, serialization, undo/redo, play mode, audio mixer, networking, profiler |
| [10 — Model Import & Assets](Game-Engine/Docs/10_Model_Import_And_Assets.md) | 3D model import, animation import, skeletal meshes, primitives, asset pipeline |
| [11 — UIX Framework](Game-Engine/Docs/11_UIX_Framework.md) | Declarative UI framework, 21 widget types, WindowKit, builder API, custom tool windows |
| [12 — Build Settings](Game-Engine/Docs/12_Build_Settings.md) | Solution structure, project configuration, dependencies, Engine.Player, publishing, ANGLE/OpenGL setup |
| [13 — Planet System](Game-Engine/Docs/13_Planet_System.md) | PlanetTerrain, biome graph workflow, chunk streaming, and planet-aware Rigidbody/Camera behavior |
| [14 — Visual Blueprints](Game-Engine/Docs/14_Visual_Blueprints.md) | Visual behavior graphs (`.blueprint`), Visual Blueprint component, nodes, reflection, EventBus integration |

---

## Community

Join the Discord to ask questions or suggest what to add next!

https://discord.gg/KTVjHfFfP2
