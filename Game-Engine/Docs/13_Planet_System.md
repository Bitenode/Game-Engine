# Game Engine - Planet System

## Overview

The planet system provides a cube-sphere, transvoxel terrain pipeline with biome-driven surface generation, optional caves, water shell rendering, and planet-relative physics. It is centered on the `PlanetTerrain` component and integrates directly with `Rigidbody`, `RigidbodyPlayer`, and `Camera`.

Core goals:
- Stream chunks around the camera with bounded runtime cost
- Author biome rules visually with the Biome Graph editor
- Keep movement and camera behavior stable on curved planetary surfaces
- Reuse the same biome/noise pipeline for both rendering and physics queries

---

## Main Runtime Pieces

| Type | Role |
|------|------|
| `PlanetTerrain` | User-facing component that owns config, biome map, chunk manager, and water shell |
| `PlanetChunkManager` | Updates 6 face quadtrees, schedules async mesh generation, applies completed meshes on main thread |
| `BiomeMap` | Resolves biome blends per sphere direction |
| `BiomeGraph` | Node graph that compiles into biome generation parameters |
| `PlanetAssetIO` | Reads/writes `.planet` assets (config + graph path + water settings) |
| `PlanetCollider` | Planet collider shell used for broad-phase AABB and gizmo bounds |
| `Rigidbody` | Planet-aware gravity, grounding, and collision response |
| `RigidbodyPlayer` | Tangent-plane movement + camera alignment for planets |
| `Camera` | Supports custom `WorldUp` so horizon follows local surface normal |

---

## PlanetTerrain Component

`PlanetTerrain` is the entry point for planet worlds and should be attached to the planet root GameObject.

### Persisted properties

| Property | Default | Description |
|----------|---------|-------------|
| `Radius` | `1000` | Base planet radius |
| `SeaLevelFraction` | `0.25` | Fraction of terrain min/max range used to compute sea level |
| `MaxLodDepth` | `6` | Max quadtree depth |
| `ChunkSize` | `32` | Voxel/mesh chunk resolution |
| `LodDistanceMultiplier` | `5.0` | LOD split distance tuning |
| `Seed` | `42` | Planet seed |
| `EnableCaves` | `true` | Enables cave carving in the density pipeline |
| `EnableWater` | `true` | Enables ocean shell mesh |
| `MaxActiveChunks` | `120` | Hard cap for active runtime chunk meshes |
| `PlanetAssetPath` | `""` | Project-relative or absolute `.planet` path |
| `BiomeGraphPath` | `""` | Project-relative or absolute `.biomegraph` path |

### Runtime behavior highlights

- Registers itself in `PlanetTerrain.ActivePlanets` for global planet queries
- Loads `.planet` data first (if `PlanetAssetPath` is set), then loads/compiles `BiomeGraphPath`
- Rebuilds biome map, noise caches, chunk manager, and water after graph apply
- Updates chunk streaming on interval and movement threshold (not every frame)
- Exposes `SampleSurfaceRadius(sphereDir)` for accurate physics grounding
- Tracks effective world radius from transform scale so LOD, water, and physics stay in sync on scaled planets

---

## Planet Asset Workflow (`.planet`)

Planet state can now be stored in a dedicated `.planet` file:

- Includes `PlanetConfig`, `SeaLevelFraction`, `EnableWater`, and `BiomeGraphPath`
- Supports project-relative paths and absolute paths
- Uses `PlanetAssetIO` for normalized load/save behavior
- Keeps planet setup portable across scenes while preserving graph-driven generation style

Recommended order:
1. Save/load `.planet` for structural planet settings
2. Save/load `.biomegraph` for biome style authoring
3. Apply/compile graph to rebuild runtime terrain and water

---

## Biome Graph Workflow

The Biome Graph editor (`BiomeGraphPanel`) is a node-based authoring tool that writes `.biomegraph` files and applies compiled results to all `PlanetTerrain` instances in the scene.

### Authoring flow

1. Open/create a graph in the Biome Graph panel
2. Add/connect nodes (Noise, Height, Layer, Cave, River, Output, etc.)
3. Save graph (`.biomegraph`)
4. Compile graph
5. Compiled `BiomeGraphResult` is pushed into all scene planets via `PlanetTerrain.ApplyGraphResult(...)`

### What compile output controls

- Global terrain controls: height amplitude/frequency, cave frequency/threshold
- Climate controls: latitude/noise weighting and moisture scale
- Up to 8 biome layers (`Layer0`...`Layer7`) with:
  - Albedo/normal paths
  - Base color
  - Tiling
  - Per-layer noise mode/octaves
  - Erosion strength/frequency
  - Optional water color overrides

When applied, planet runtime state is rebuilt so generated chunks immediately reflect new biome graph data.

Water output notes:
- Layer `SpawnWater` contributes to biome water masks
- Water mesh uses a continuous shell; shoreline appearance is blended in shader using mask/tint data (avoids patchy mesh holes)
- Shoreline tinting blends water color toward nearby non-water biome colors
- River settings (`RiverWidth`, `RiverDepth`, `Frequency`, `Meander`, `AllowedBiomes`) are compiled into planet runtime config

---

## Chunk Streaming and Budgets

`PlanetChunkManager` controls chunk lifecycle and keeps work bounded each update.

Key limits from `PlanetConfig`:
- `MaxLeafNodes` - total quadtree leaf budget across 6 faces
- `MaxActiveChunks` - loaded chunk cap near camera
- `MaxMeshAppliesPerUpdate` - completed mesh apply budget per tick
- `MaxGenerationSchedulesPerUpdate` - new generation schedules per tick
- `SplitDistanceScale` / `MergeDistanceScale` - split/merge hysteresis controls to reduce LOD churn
- Internal `MaxConcurrentJobs` - async mesh worker limit

High-level update sequence:
1. Update all face quadtrees
2. Enforce leaf budget (merges far leaves first)
3. Apply completed meshes (bounded)
4. Sort leaves by camera distance
5. Unload far leaves beyond active cap
6. Schedule new mesh generation for nearest dirty leaves (bounded)

Hierarchy/runtime note:
- Chunk `GameObject` children are no longer created (no `PlanetChunk_*` scene hierarchy spam)
- Runtime chunk meshes are cached on quadtree leaves and rendered directly
- Rendering reads `PlanetChunkManager.GetRenderableLeaves()` for terrain/water/cloud passes

---

## File Map

### Planet folder (`Core/Planet/`)

| File | Purpose |
|------|---------|
| `PlanetConfig.cs` | Planet generation settings and runtime chunk/job budgets |
| `PlanetChunkManager.cs` | Face quadtree updates, job scheduling, mesh apply/unload |
| `FaceQuadtree.cs` | Per-face split/merge logic and neighbor lookup |
| `QuadNode.cs` | Quadtree node state (`NeedsMeshRebuild`, `TransitionMask`, generated mesh cache) |
| `CubeSphereMath.cs` | Cube-face UV <-> sphere direction conversions |
| `DensityGenerator.cs` | Voxel density/material generation for spherical terrain fields |
| `PlanetMeshGenerator.cs` | Surface mesh generation with biome blends/erosion/caves |
| `PlanetWater.cs` | Planet ocean shell mesh generation |
| `PlanetAssetIO.cs` | `.planet` DTO + load/save + path normalization |
| `PlanetWaterSimulation.cs` | Runtime water simulation state for planet rendering integration |
| `PlanetWaterVoxelGenerator.cs` | Water-related voxel contribution utilities |

### Noise folder (`Core/Noise/`)

| File | Purpose |
|------|---------|
| `SimplexNoise.cs` | Deterministic seeded 2D/3D simplex noise source |
| `FractalNoise.cs` | FBM/ridged/billow fractal wrapper with octaves/lacunarity/persistence/domain warp |

### Voxel folder (`Core/Voxel/`)

| File | Purpose |
|------|---------|
| `VoxelChunk.cs` | Density/material grid + oriented basis/world mapping |
| `TransvoxelMesher.cs` | Regular + transition-cell meshing, outputs engine mesh data |
| `MarchingCubesTables.cs` | Lookup tables for marching cubes/transvoxel topology |

---

## Planet-Aware Physics

### Rigidbody

`Rigidbody` now supports curved-world behavior:

- Finds nearest active planet each fixed tick
- Computes `LocalUp` from planet center to body position
- Applies gravity along `-LocalUp` when on a planet (fallback: world `-Y`)
- Grounds against sampled planet surface radius (`PlanetTerrain.SampleSurfaceRadius`)
- Keeps tangent velocity when grounded (removes into-surface component)
- Preserves existing non-planet collision paths (terrain, mesh, AABB, triggers)
- Resolves underwater state against world-space sea level (including planet transform scale)

Additional runtime state:
- `LocalUp`
- `IsGrounded`, `GroundNormal`
- `IsUnderwater`, `UnderwaterDepth`

### PlanetCollider

`PlanetCollider` complements `PlanetTerrain` for broad-phase and tooling:

- Computes planet world AABB from max radius (`base radius + biome max amplitude`) with world-scale awareness
- Exposes `BaseRadius`, `MaxRadius`, and optional `RadiusOverride`
- Provides debug shell bounds for collider visualization
- Defers exact terrain-conforming contact to `PlanetTerrain.SampleSurfaceRadius(...)`

### RigidbodyPlayer

`RigidbodyPlayer` is planet-aware and uses `Rigidbody.LocalUp`:

- Builds move axes from a robust tangent basis derived from `LocalUp`
- Applies acceleration and drag in tangent space on planets
- Jumps along `LocalUp` (not always world +Y)
- Avoids pole-specific movement mode switching to prevent axis flips/discontinuities
- Smooths camera up-vector transitions to reduce horizon jitter
- Writes smoothed up-vector into `Camera.WorldUp`
- Supports both first-person and third-person camera offsets on curved surfaces

---

## Camera Integration

`Camera` exposes `WorldUp` (default `Vector3.UnitY`), which is used in `GetViewMatrix()`.

For planet traversal:
- Controllers such as `RigidbodyPlayer` set `Camera.WorldUp` each frame
- View matrix uses this vector in `CreateLookAt(...)`
- `Camera.GetViewMatrix()` includes forward/up collinearity safeguards for stability
- Result: the horizon aligns with the local planet surface instead of snapping to global Y-up

---

## Editor and Data Notes

- Biome graphs are saved as `.biomegraph` JSON files
- Planet assets are saved as `.planet` JSON files
- Graph paths are stored as project-relative paths when possible
- `PlanetTerrain.TryLoadBiomeGraph()` resolves both relative and absolute paths
- `PlanetTerrain` normalizes and persists `PlanetAssetPath`/`BiomeGraphPath` project-relative when possible
- Scene compile applies graph results live and triggers `SceneService.NotifyChanged()`

Recommended setup:
1. Add `PlanetTerrain` to a root GameObject
2. Add a player with `RigidbodyPlayer` + `Rigidbody` + `CapsuleCollider`
3. Ensure there is a `Camera` for the player/controller
4. Author and compile a biome graph, then assign/verify `BiomeGraphPath`

---

## Scene View LOD Behavior

Scene View uses an orbit-aware profile so whole-planet visibility is stable while orbiting:

- Near-orbit to far-orbit range scales LOD depth/split aggressiveness smoothly
- Fill/apply budgets increase when farther out to populate full-planet coverage quickly
- Close-range no longer forces always-loaded safety locks; normal streaming can merge/unload near-camera chunks

---

## Atmosphere and Clouds

Planet visuals support a dedicated atmosphere workflow via `PlanetAtmosphere`.

- Add `PlanetAtmosphere` beside `PlanetTerrain` on the planet root object
- Atmosphere settings are persisted and editable in Inspector
- Terrain and planet-water shading consume atmosphere uniforms per planet
- Clouds are rendered in a dedicated planet cloud pass (`PlanetClouds*` shaders)

Separation contract:
- Planet atmosphere/clouds are independent from `Skybox`
- `Skybox` remains a background sky system only
- Multi-planet scenes can use different atmosphere/cloud presets per planet

