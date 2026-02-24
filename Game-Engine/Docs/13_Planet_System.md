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
| `MaxActiveChunks` | `120` | Hard cap for loaded chunk GameObjects |
| `BiomeGraphPath` | `""` | Project-relative or absolute `.biomegraph` path |

### Runtime behavior highlights

- Registers itself in `PlanetTerrain.ActivePlanets` for global planet queries
- Loads and compiles `BiomeGraphPath` in `TryLoadBiomeGraph()`
- Rebuilds biome map, noise caches, chunk manager, and water after graph apply
- Updates chunk streaming on interval and movement threshold (not every frame)
- Exposes `SampleSurfaceRadius(sphereDir)` for accurate physics grounding

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

---

## Chunk Streaming and Budgets

`PlanetChunkManager` controls chunk lifecycle and keeps work bounded each update.

Key limits from `PlanetConfig`:
- `MaxLeafNodes` - total quadtree leaf budget across 6 faces
- `MaxActiveChunks` - loaded chunk cap near camera
- `MaxMeshAppliesPerUpdate` - completed mesh apply budget per tick
- `MaxGenerationSchedulesPerUpdate` - new generation schedules per tick
- Internal `MaxConcurrentJobs` - async mesh worker limit

High-level update sequence:
1. Update all face quadtrees
2. Enforce leaf budget (merges far leaves first)
3. Apply completed meshes (bounded)
4. Sort leaves by camera distance
5. Unload far leaves beyond active cap
6. Schedule new mesh generation for nearest dirty leaves (bounded)

---

## File Map

### Planet folder (`Core/Planet/`)

| File | Purpose |
|------|---------|
| `PlanetConfig.cs` | Planet generation settings and runtime chunk/job budgets |
| `PlanetChunkManager.cs` | Face quadtree updates, job scheduling, mesh apply/unload |
| `FaceQuadtree.cs` | Per-face split/merge logic and neighbor lookup |
| `QuadNode.cs` | Quadtree node state (`NeedsMeshRebuild`, `TransitionMask`, `ChunkGO`) |
| `CubeSphereMath.cs` | Cube-face UV <-> sphere direction conversions |
| `DensityGenerator.cs` | Voxel density/material generation for spherical terrain fields |
| `PlanetMeshGenerator.cs` | Surface mesh generation with biome blends/erosion/caves |
| `PlanetWater.cs` | Planet ocean shell mesh generation |

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

Additional runtime state:
- `LocalUp`
- `IsGrounded`, `GroundNormal`
- `IsUnderwater`, `UnderwaterDepth`

### PlanetCollider

`PlanetCollider` complements `PlanetTerrain` for broad-phase and tooling:

- Computes planet world AABB from max radius (`base radius + biome max amplitude`)
- Exposes `BaseRadius`, `MaxRadius`, and optional `RadiusOverride`
- Provides debug shell bounds for collider visualization
- Defers exact terrain-conforming contact to `PlanetTerrain.SampleSurfaceRadius(...)`

### RigidbodyPlayer

`RigidbodyPlayer` is planet-aware and uses `Rigidbody.LocalUp`:

- Builds move axes from camera forward projected onto local tangent plane
- Applies acceleration and drag in tangent space on planets
- Jumps along `LocalUp` (not always world +Y)
- Smooths camera up-vector transitions to reduce horizon jitter
- Writes smoothed up-vector into `Camera.WorldUp`
- Supports both first-person and third-person camera offsets on curved surfaces

---

## Camera Integration

`Camera` exposes `WorldUp` (default `Vector3.UnitY`), which is used in `GetViewMatrix()`.

For planet traversal:
- Controllers such as `RigidbodyPlayer` set `Camera.WorldUp` each frame
- View matrix uses this vector in `CreateLookAt(...)`
- Result: the horizon aligns with the local planet surface instead of snapping to global Y-up

---

## Editor and Data Notes

- Biome graphs are saved as `.biomegraph` JSON files
- Graph paths are stored as project-relative paths when possible
- `PlanetTerrain.TryLoadBiomeGraph()` resolves both relative and absolute paths
- Scene compile applies graph results live and triggers `SceneService.NotifyChanged()`

Recommended setup:
1. Add `PlanetTerrain` to a root GameObject
2. Add a player with `RigidbodyPlayer` + `Rigidbody` + `CapsuleCollider`
3. Ensure there is a `Camera` for the player/controller
4. Author and compile a biome graph, then assign/verify `BiomeGraphPath`

