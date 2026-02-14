# Game Engine

A full-featured 3D game engine and editor built from the ground up in **C# (.NET 9.0)**, using **Avalonia 11** for the cross-platform UI framework and **Silk.NET OpenGL** for GPU-accelerated rendering. The architecture follows a component-based design inspired by Unity, providing a complete scene graph, visual editor, runtime scripting, physics, audio, animation, post-processing, and more.

---

## Key Features

- **Component-Based Architecture** — GameObject/Behavior system with lifecycle methods (Awake, Start, Update, FixedUpdate, LateUpdate, OnDestroy)
- **Visual Editor** — Dockable panel layout with Hierarchy, Scene View, Game View, Inspector, Project Browser, Console, and Animation panels
- **Real-Time 3D Rendering** — Forward rendering pipeline with PBR materials, shadow mapping (4096x4096 PCF), frustum culling, and LOD
- **Terrain System** — Heightmap-based terrain with 10 sculpting/painting tools, multi-material splatmap painting (up to 8 layers), chunking, per-chunk LOD, tree painting, and O(1) heightmap collision
- **Physics & Collision** — BoxCollider, CapsuleCollider, MeshCollider, CharacterController with gravity, slope limiting, step climbing, coyote time, and continuous collision detection
- **C# Scripting** — Runtime compilation via Roslyn with hot-reload, `[Persist]` attribute for automatic serialization, and `[Require]` for component dependencies
- **Editor Extensions** — Plugin system for custom menus, commands, and custom inspectors
- **3D Model Import** — FBX, OBJ, glTF/GLB, DAE via AssimpNet with automatic material extraction, skeleton building, and bone animation import
- **Animation System** — Bone-based skeletal animation with GPU skinning, animation state machine, and keyframe editing
- **Particle System** — Billboard particles with emission shapes (Sphere, Cone, Box), sub-emitters, and presets (Fire, Smoke, Sparks, Rain, Snow, Dust)
- **Audio System** — 3D spatial audio with distance attenuation, stereo panning, channel separation (Master/Music/SFX), and looping via NAudio
- **Water Rendering** — Gerstner wave displacement, Fresnel-based transparency, foam, and underwater post-processing effects
- **Post-Processing** — Bloom, Fog, Color Grading (Brightness/Contrast/Saturation/Exposure), Tone Mapping (Reinhard/ACES), Vignette, FXAA, and underwater effects
- **Decal Projection** — Runtime decal rendering on surfaces
- **Undo/Redo** — Full command-pattern undo/redo system across all editor operations
- **Play Mode** — Scene snapshot and restore, ensuring runtime changes don't persist after stopping

---

## Technology Stack

| Layer             | Technology                             | Version   |
|-------------------|----------------------------------------|-----------|
| Runtime           | .NET                                   | 9.0       |
| UI Framework      | Avalonia                               | 11.*      |
| Rendering         | Silk.NET OpenGL / OpenGL ES 3.0 (ANGLE)| 2.23.0   |
| 3D Model Import   | AssimpNet                              | 4.1.0     |
| Image Loading     | SkiaSharp                              | 2.88.9    |
| Scripting         | Roslyn (Microsoft.CodeAnalysis.CSharp) | 4.14.0    |
| Audio             | NAudio                                 | 2.2.1     |
| Reactive          | System.Reactive                        | 6.1.0     |
| Code Generation   | AutoConstructor                        | 5.6.0     |

---

## Getting Started

1. **Clone** the repository
2. **Open** `Game-Engine/Game_Engine.sln` in Visual Studio 2022+ or Rider
3. **Build & Run** — the editor window will appear with a default scene (Skybox, Camera, Light, Cube)
4. **Create a Project** — File > New Project to set up a project folder
5. **Start Building** — Right-click in the Hierarchy to add objects, sculpt terrain, write scripts, and more

---

## Documentation

Comprehensive documentation is available in the `Docs/` folder & Wiki:

| Document | Topic |
|----------|-------|
| [01 — Engine Overview](Docs/01_Engine_Overview.md) | Architecture, tech stack, core concepts, services, data flow |
| [02 — Editor Guide](Docs/02_Editor_Guide.md) | Editor panels, Scene View, Game View, Inspector, Project Panel, Console, Animation |
| [03 — Components Reference](Docs/03_Components_Reference.md) | Every built-in component with properties, defaults, and usage |
| [04 — Rendering Pipeline](Docs/04_Rendering_Pipeline.md) | Render passes, shaders, GPU resources, post-processing, particles, water |
| [05 — Terrain System](Docs/05_Terrain_System.md) | Terrain creation, 10 brush tools, splatmap painting, tree painting, chunking, LOD |
| [06 — Scripting & Extensibility](Docs/06_Scripting_And_Extensibility.md) | C# scripting, lifecycle, APIs, editor extensions, command registry, custom inspectors |
| [07 — Physics & Collision](Docs/07_Physics_And_Collision.md) | Colliders, CharacterController, CollisionWorld, raycasting, terrain collision |
| [08 — Materials & Textures](Docs/08_Materials_And_Textures.md) | PBR materials, texture slots, material files, transparency, terrain materials |
| [09 — Scene & Project Management](Docs/09_Scene_And_Project_Management.md) | Projects, scenes, serialization, undo/redo, selection, play mode, logging |
| [10 — Model Import & Assets](Docs/10_Model_Import_And_Assets.md) | 3D model import, animation import, skeletal meshes, primitives, asset pipeline |

---

## Community

Join the Discord to ask questions or suggest what to add next!

https://discord.gg/KTVjHfFfP2
