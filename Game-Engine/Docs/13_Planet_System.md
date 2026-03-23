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
  - Vegetation defaults:
    - `VegetationProfileId`
    - `VegetationDensity` / `TreeDensity`
    - `VegetationPatchiness`
    - `SeasonalGrowthMultiplier`
  - Weather defaults:
    - `WeatherProfileId`
    - `RainChance` / `SnowChance` / `StormChance`
    - `WindBias` / `CloudCoverageBias` / `FogDensityBias`

When applied, planet runtime state is rebuilt so generated chunks immediately reflect new biome graph data.

Water output notes:
- Layer `SpawnWater` contributes to biome water masks
- Water mesh uses a continuous shell; shoreline appearance is blended in shader using mask/tint data (avoids patchy mesh holes)
- Shoreline tinting blends water color toward nearby non-water biome colors
- River settings (`RiverWidth`, `RiverDepth`, `Frequency`, `Meander`, `AllowedBiomes`) are compiled into planet runtime config
- Vegetation profiles are authored per biome layer in the Biome Graph properties panel:
  - profile selector + `New/Save/Delete/Reload`
  - editable grass/tree type lists with per-item mesh path, weight, density multiplier, and min/max scale

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

### Day/Night Cycle (PlanetAtmosphere)

`PlanetAtmosphere` now includes a built-in optional day/night cycle:

- `EnableDayNightCycle` toggles cycle logic
- `DayLengthMinutes` and `TimeOfDay` control temporal progression
- `AxisX/Y/Z` + `NoonDirectionX/Y/Z` define orbital sun path
- Optional automatic lighting curves:
  - `AutoAdjustSunIntensity` (`DaySunIntensity` / `NightSunIntensity`)
  - `AutoAdjustAmbient` (`DayAmbient` / `NightAmbient`)
- Optional automatic sky tint transition:
  - `AutoAdjustSkyTint` toggles day->night sky color blending
  - `NightZenithTintR/G/B` and `NightHorizonTintR/G/B` define target night sky colors
  - `NightSkyHueShiftDegrees` and `NightSkyBrightness` add additional nighttime hue/value shaping

Cycle output feeds existing planet terrain/water/atmosphere shaders via `SunDirectionOverride`, `SunIntensity`, and dynamic day/night sky tints.

---

## Vegetation + Weather Runtime Controllers

Planet ecosystem/weather runtime is implemented with companion components on the planet root:

| Type | Role |
|------|------|
| `PlanetVegetationSystem` | Chunk-aware biome vegetation spawning/despawning, growth/decay lifecycle, weather response |
| `PlanetWeatherController` | Hybrid biome-blended weather state machine (`Clear/Cloudy/Rain/Snow/Storm`) driving atmosphere/fog/wind/precipitation |

### Vegetation Profiles

Vegetation profile data is stored in:

- `Assets/Biomes/vegetation-profiles.json`

Each profile supports:

- global biome-level tuning (`VegetationDensity`, `TreeDensity`, `Patchiness`, `SeasonalGrowthMultiplier`)
- multiple weighted grass types (`GrassItems`)
- multiple weighted tree types (`TreeItems`)
- per-item controls:
  - mesh/model path
  - selection weight
  - density multiplier
  - min/max scale multipliers

Runtime spawning uses weighted selection from the active biome profile.

### PlanetVegetationSystem Manual Spawn Modes

`PlanetVegetationSystem` now supports two explicit manual spawn behaviors via
`FullBiomePopulate` (shown in the Inspector under **Planet Vegetation**):

- `FullBiomePopulate = true` (default):
  - `Spawn Vegetation` / `Respawn` performs a full biome population pass across all renderable leaves
  - ignores near-camera streaming distance filtering for that manual pass
  - uses an expanded one-shot spawn budget for large initial fills
  - scales per-leaf target counts by leaf area so coarse leaves are not underpopulated
- `FullBiomePopulate = false`:
  - manual spawn follows streaming-style behavior (tracked leaves + distance/budget constraints)
  - useful when iterating close to the camera without filling the whole planet

Notes:
- Automatic runtime updates still use normal streaming behavior (`AutoSpawn` update loop).
- `Spawn Vegetation` and `Respawn` always guarantee minimum visible spawn when a biome profile has valid grass/tree entries.

### Saved vegetation in `.planet` files

`PlanetTerrain` writes a `Vegetation` block into the planet asset JSON (see `PlanetVegetationAssetData` / `PlanetVegetationPlacement`).

- **`DirX` / `DirY` / `DirZ`**: unit direction from the **planet pivot** toward the instance. World position is reconstructed with `SampleSurfaceRadius(dir)` (not raw `PosX/Y/Z` in the file).
- **`ModelPath` / `PrefabPath` / `TexturePath`**: project-relative asset references. **Imported trees** often store `MeshFilter.ModelPath` only on **child** objects (multi-part FBX); export walks the hierarchy so the `.planet` file still gets the correct model path.
- **`UseStoredPlacements`**: mirrors `PlanetVegetationSystem.UsePlanetAssetPlacements`. Enable **Use .planet Vegetation Placements** in the inspector if you want spawn logic to prefer saved rows; `AutoUseSavedPlacementsWhenPresent` can still opt in when the flag was off but the file contains placements.
- **Spawn budget**: `MaxAssetSpawnsPerUpdate` limits how many saved placements materialize per tick. Grass is spawned **before** trees in that tick so grass is not starved when the budget is small. After scene load, a **one-shot warmup** applies a larger budget so grass and trees both appear quickly.
- **Saving during deferred import**: When synchronous load skips applying vegetation, `PlanetTerrain` keeps a **clone of the vegetation block** and a **snapshot flag** so `SavePlanetAsset` substitutes that clone whenever live export is still empty (including if async hydrate aborts and `AsyncVegetationHydrationPending` is cleared early). The snapshot is dropped only after `PlanetVegetationSystem.ImportAssetData` loads rows into memory or imports an explicit empty `Placements` array.

### Planet Grass Attachment and Rendering Notes

Planet grass spawned through `VegetationPainter.BuildOnPlanetPatch(...)` includes
planet-specific grounding and rendering protections:

- blade placement blends local slope normal with radial-up for stable terrain contact
- roots are embedded into the sampled surface to reduce floating on steep relief
- planet grass chunk culling uses 3D world distance (X/Y/Z), not flat XZ distance
- grass materials are forced to alpha-cutout behavior to avoid opaque card fallback
- if external texture decode fails, a generated cutout fallback blade texture is used

### Planet Precipitation Notes

Planet weather precipitation uses layered particle emitters around the camera:

- supports multiple vertical layers for continuous volume coverage
- supports visibility polling so precipitation work is skipped when the volume is not near/in view
- rain/snow emission remains continuous while state is active and visible
- by default, weather uses a performance budget profile (layer cap + particle cap + emission cap)
- optional planet surface-hit termination can be disabled for weather emitters to reduce script cost
- emitters support planet gravity alignment (nearest active planet center)

Recommended runtime tuning for weak CPUs:

- `UsePrecipitationPerformanceBudget = true`
- `MaxActivePrecipitationLayers = 1`
- `DisableSurfaceHitForWeatherPrecipitation = true`
- lower `RainEmissionRatePerLayer` and `SnowEmissionRatePerLayer` first before adding layers

