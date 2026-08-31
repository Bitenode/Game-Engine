# Game Engine

A full-featured 3D game engine and editor built in C# (.NET 9.0) with Avalonia 12 and Silk.NET OpenGL. Features 34+ built-in components, a visual shader graph editor, physics with joints and BVH, skeletal animation with IK and blend trees, 2D support (Camera2D, SpriteRenderer, Tilemap), a GPU-rendered runtime UI system (Canvas, Text, Image, Button, Slider, Toggle, InputField), runtime scene loading, networking, audio mixing with reverb zones, and multi-platform build publishing.

See the [main README](../README.md) at the repository root for the full project description, features, and getting started guide.

## Quick Start

1. Open `Game_Engine.sln` in Visual Studio 2022+ or Rider
2. Build & Run — the editor appears with a default scene
3. File > New Project to create a project folder
4. Right-click Hierarchy to add objects, sculpt terrain, write scripts, and more

## Documentation

All docs are in the `Docs/` folder:

| Document | Topic |
|----------|-------|
| [01 — Engine Overview](Docs/01_Engine_Overview.md) | Architecture, tech stack, core concepts, services, data flow |
| [02 — Editor Guide](Docs/02_Editor_Guide.md) | Editor panels, Scene View, Game View, Inspector, Shader Editor, Profiler, Build Settings |
| [03 — Components Reference](Docs/03_Components_Reference.md) | 34+ built-in components with properties, defaults, and usage |
| [04 — Rendering Pipeline](Docs/04_Rendering_Pipeline.md) | Render passes, shaders, shader graph, GPU resources, post-processing, particles, water |
| [05 — Terrain System](Docs/05_Terrain_System.md) | Terrain creation, 10 brush tools, splatmap painting, tree painting, chunking, LOD, `.terrain.bin`, `TerrainStreamer` |
| [06 — Scripting & Extensibility](Docs/06_Scripting_And_Extensibility.md) | C# scripting, lifecycle, APIs, editor extensions, command registry, custom inspectors |
| [07 — Physics & Collision](Docs/07_Physics_And_Collision.md) | Colliders, CharacterController, physics joints, BVH, raycasting, terrain collision |
| [08 — Materials & Textures](Docs/08_Materials_And_Textures.md) | PBR materials, shader graph materials, texture slots, transparency, custom shaders |
| [09 — Scene & Project Management](Docs/09_Scene_And_Project_Management.md) | Projects, scenes, serialization, undo/redo, play mode, audio mixer, networking, profiler |
| [10 — Model Import & Assets](Docs/10_Model_Import_And_Assets.md) | 3D model import, animation import, skeletal meshes, primitives, asset pipeline |
| [11 — UIX Framework](Docs/11_UIX_Framework.md) | Declarative UI framework, 21 widget types, WindowKit, builder API, custom tool windows |
| [12 — Build Settings](Docs/12_Build_Settings.md) | Solution structure, project config, dependencies, Engine.Player, publishing, ANGLE setup |
| [13 — Planet System](Docs/13_Planet_System.md) | PlanetTerrain, biome graph workflow, play/editor chunk LOD, seam stitching, swimming, vegetation budgets |
| [14 — Visual Blueprints](Docs/14_Visual_Blueprints.md) | Node graphs (.blueprint), Visual Blueprint component, flow/actions, reflection nodes, EventBus integration |

## Community

https://discord.gg/KTVjHfFfP2
