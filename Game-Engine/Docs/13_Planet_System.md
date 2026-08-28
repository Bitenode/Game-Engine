# Game Engine - Planet System

## Overview

The planet system provides a cube-sphere world with a **solid voxel-filled interior** and procedural **multi-scale caves** carved through it. Each quadtree leaf owns one or more radial `VoxelChunk` shells (U/V on the cube face, Z radial). `DensityGenerator` fills 3D density (surface + interior rock + worm/cavern noise), `PlanetVoxelEditStore` applies dig/build strokes in planet-local space, and `TransvoxelMesher` builds the mesh (regular cells plus LOD transition cells from `TransitionMask`). Water, atmosphere, biome graphs, and radial gravity stay as companion systems.

This is **not** a hollow shell with a fake floor. Land biomes fill rock from near the core (~4% of radius) to slightly above the authored surface. Caves are 3D voids inside that volume; they are not height-subtracted pits on the outer shell.

Core goals:
- Stream chunks around the camera with bounded runtime cost and parent-hold LOD (no holes while children generate)
- Author biome rules visually with the Biome Graph editor
- Query the **same density field** for meshing, editor picking, and cave-aware contact
- Keep movement and camera behavior stable on curved surfaces (gravity still radial)

---

## Main Runtime Pieces

| Type | Role |
|------|------|
| `PlanetTerrain` | User-facing component: config, biome map, chunk manager, density queries, voxel edits, water shell |
| `PlanetChunkManager` | Six face quadtrees, prefetch/split, parent-hold apply, transvoxel remesh, edit commands |
| `PlanetSpace` | World ↔ planet-local unscaled conversion (center subtract, then unscale) |
| `PlanetNoiseCache` | Shared fractal-noise instances per planet (biome, erosion, cave layers) — reused across chunk jobs |
| `PlanetDensitySampler` | Samples crust density + multi-scale caves + edit overlay in local space |
| `PlanetDensityRaycast` | Sphere-marches that field; fills `PlanetDensityHit` |
| `BiomeMap` | Resolves biome blends per sphere direction |
| `BiomeGraph` | Node graph that compiles into biome generation parameters |
| `PlanetAssetIO` | `.planet` JSON plus `.planetvox` sidecar load/save |
| `PlanetCollider` | Broad-phase AABB and gizmo shell (not triangle contact) |
| `Rigidbody` / `CharacterController` | Radial gravity + density ray/spherecast contact |
| `RigidbodyPlayer` | Tangent-plane movement + camera alignment for planets |
| `PlanetPlayerSpawner` | Play-mode spawner: RigidbodyPlayer + capsule + camera on the crust |
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
- Tracks effective world radius from transform scale so LOD, water, and physics stay in sync on scaled planets
- Converts world brushes/queries through `WorldToLocal` / `LocalToWorld` (`PlanetSpace`) so a planet off the origin still edits and hits correctly

### Density queries vs `SampleSurfaceRadius`

Use the **density field** for anything that must hit caves, walls, ceilings, or painted holes on any hemisphere:

| API | What it is |
|-----|------------|
| `RaycastDensity(worldOrigin, worldDirection, maxDistance, out PlanetDensityHit hit)` | Ray-march crust density (caves included) |
| `Raycast(...)` | Alias of `RaycastDensity` (Scene View brushes) |
| `Spherecast(worldOrigin, worldDirection, worldRadius, maxDistance, out hit)` | Thick query for capsule/rigidbody contact |
| `TrySampleLocalIsosurface(sphereDir, out localPoint, out localNormal)` | First air→solid crossing inward along a cube-sphere direction (pits, cave mouths) |
| `ResolveDensityPenetration(ref worldPos, worldClearance)` | Push a point out of solid density |

`PlanetDensityHit` fields: `Point`, `Normal`, `Distance`, `StartedInside`.

`SampleSurfaceRadius(sphereDir)` is the **outermost** crust crossing (air→solid walking inward from outside the shell). It is for water, orbit LOD, atmosphere, vegetation radial estimates, and collider gizmos. It is **not** cave-floor contact. Physics grounding uses `Spherecast` / `RaycastDensity` along `-LocalUp`.

### Voxel edits and `.planetvox`

`DigSphere` / `BuildSphere` take a **world** brush center and radius. Internally they store strokes in **planet-local unscaled** space (`PlanetVoxelEditStore`). Positive density delta removes solid (dig); negative adds solid (build). Fast path: if the leaf already has a `VoxelChunk`, the stroke is splatted into the grid and remeshed; otherwise the leaf is marked dirty.

Persistence:

- `SavePlanetAsset()` writes `.planet` JSON (`PlanetAssetData.Version` = 2) and then `SaveVoxelEdits()`.
- `SaveVoxelEdits()` / `LoadVoxelEdits()` read/write a sidecar named `<planet>.planetvox` next to the `.planet` (or `PlanetAssetData.VoxelEditsPath` if set). Strokes may be baked into sparse `BakedCells` when the list grows.
- Only the offline editor or a network **server** writes those files (`OwnsPlanetAssetForPersist`). Clients send `PlanetVoxelEdit` RPCs; the server broadcasts `PlanetVoxelInvalidate`.

`ClearVoxelEdits(rebuildNow)` clears the live overlay; the sidecar updates on the next save.

---

## Planet Asset Workflow (`.planet`)

Planet state is stored in a dedicated `.planet` file plus an optional voxel sidecar:

- `.planet`: `PlanetConfig`, `SeaLevelFraction`, `EnableWater`, `BiomeGraphPath`, vegetation placements, `VoxelEditsPath`
- `.planetvox`: sphere strokes and optional baked cell deltas in planet-local unscaled space
- Project-relative or absolute paths; `PlanetAssetIO` normalizes load/save

Recommended order:
1. Save/load `.planet` for structural planet settings
2. Save/load `.biomegraph` for biome style authoring
3. Apply/compile graph to rebuild runtime terrain and water
4. Keep `.planetvox` next to the asset so painted caves survive reload

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
1. Update all face quadtrees (prefetch children before committing a split)
2. Enforce leaf budget (merges far leaves first)
3. Apply completed meshes (bounded)
4. Sort leaves by camera distance
5. Unload far leaves beyond active cap
6. Schedule new mesh generation for nearest dirty leaves (bounded)

**Parent-hold LOD:** a node stays a visible leaf (`IsLeaf`) until `TryCommitSplit` succeeds — all four children already have `GeneratedMesh`. Prefetch allocates children and generates their meshes while the parent mesh still renders (`CollectRenderable` keeps the parent if children are incomplete). On merge, the parent mesh is kept when it still exists; only dirty parents rebuild. This avoids missing-face holes while LOD refines.

### Interior fill and stacked voxel shells

`DensityGenerator.ComputeInteriorBounds` defines solid fill from `radialMin` (~4% of `Radius`, minimum 16) out to `radialMax` (surface + amplitude/brush padding).

Fine leaves do **not** stretch one 32³ grid across the entire radius (that would make 20 m+ cells and shred caves). Instead `PlanetMeshGenerator` stacks **1–4 radial shells** per leaf (`DensityGenerator.RadialLayerCount`), each ~320 m thick with 32³ samples, then merges the transvoxel meshes with `TransvoxelMeshData.Append`.

| Leaf tangential cell | Mesh mode |
|----------------------|-----------|
| `> VolumetricMaxCellSize` | Smooth **heightfield shell** (orbit / coarse Scene View) |
| `≤ VolumetricMaxCellSize` | **Stacked transvoxel** shells (caves + interior rock) |

`VolumetricMaxCellSize` defaults to **3.5** at orbit. When the camera is inside the planet (`camR < 1.08 × EffectiveWorldRadius`), `PlanetTerrain.ApplyChunkBudgets` raises it to **11**, increases leaf/chunk caps, and boosts mesh job budgets so interior cave walls refine while you fly around — in **both Play and editor** (editor gets slightly higher caps when underground).

**Play-mode interior profile** (when `cameraInside` in Play): `MaxLodDepth` up to **6**, `MaxActiveChunks` / `MaxLeafNodes` toward **120–160**, `MaxGenerationSchedulesPerUpdate` **~14**, `VolumetricMaxCellSize` **11**. Orbit play outside the crust band keeps tighter caps (depth **4–5**, **32–64** leaves, **6** schedules). `QuadNode.Merge` keeps a valid parent mesh on budget merge unless the subtree was edited (`NeedsMeshRebuild` only when children were dirty), reducing underground pop-in.

### Multi-scale cave carving

When `EnableCaves` is on, `PlanetDensitySampler.ApplyCaveCarve` runs only on biomes with `CavesEnabled` (ocean/beach presets default to **off**). Carving:

- Starts **12 m** below the local surface (thin roof so caves do not punch through the crust)
- Continues through the **full interior** (no 280 m depth cap)
- Stops at a tiny solid core (`r < max(16, 0.035 × Radius)`) so cube-sphere samples never collapse at the origin
- Blends four noise scales:
  - **Small tunnels** — high-frequency worm noise at every depth
  - **Medium passages** — mid-scale worm corridors
  - **Large caverns** — low-frequency FBM, slightly more open toward the core
  - **Huge chambers** — sparse inner-half mega-rooms

Cave density and biome `CaveDensity` scale how aggressively each scale opens rock.

### Interior LOD and rendering

When the camera is inside or just under the crust:

- `QuadNode.CameraPriorityDistance` samples the patch at the camera radius (not only outer-surface corners), so fly-cam refines walls you are looking at
- `FaceQuadtree` splits **2.3×** more aggressively near the crust
- `SceneRenderer` disables backface culling and frustum culling in the crust band (`camRadial < 1.08 × radius`)
- Planet terrain shader uses `slope = abs(dot(N, radialDir))` so steep cave walls still get rock textures, not grey undersides
- **Interior lighting:** `evalAtmosphere` is skipped below the crust (`distFromCenter < uPlanetRadius`); inward faces keep biome under-color (no grey floor clamp); cavity AO darkens ceilings (`ao = mix(1.0, 0.35, -nDotRadial)`); interior ambient is slightly reduced
- **Form shadows:** `RenderPlanetLeafShadows` draws `GetRenderableLeaves()` into the depth shadow pass so cave mouths and crater rims cast shadows (vegetation already did; planet leaves are GPU caches, not scene nodes)

### Transvoxel LOD seams

Fine volumetric leaves set `TransitionMask` on edges that border a coarser neighbor. `TransvoxelMesher.GenerateMesh` calls `GenerateTransitionCells` when the mask is non-zero (toggle with `PlanetConfig.EnableTransvoxelTransitions`, default **true**). Transition cells use the full Lengyel **13-corner** layout (`MarchingCubesTables.TransitionVertexData` / `TransitionCellData`) — not the old 9-sample ring interpolation. Only the outer radial shell layer currently receives the mask (enough for cave mouths and crust LOD boundaries).

### Play-mode voxel edits

Dig/build in Play no longer forces every leaf back to a heightfield shell after the first stroke. `PlanetMeshGenerator.ShouldUseVolumetric` keeps volumetric remesh on overlapping leaves (small-brush coarse-leaf exception: `MaxRadius ≤ 2.5 m` and `cell > VolumetricMaxCellSize` may stay shell-only). `PlanetChunkManager.ApplyPlayModeEditVisual` dirtys overlapping leaves and schedules async remesh with a play budget (**4–8** nodes); coarse non-volumetric leaves may get a one-frame `PlanetShellDeformer` preview.

Hierarchy/runtime note:
- Chunk `GameObject` children are no longer created (no `PlanetChunk_*` scene hierarchy spam)
- Runtime chunk meshes are cached on quadtree leaves and rendered directly
- Rendering reads `PlanetChunkManager.GetRenderableLeaves()` for terrain/water/cloud passes

---

## File Map

### Planet folder (`Core/Planet/`)

| File | Purpose |
|------|---------|
| `PlanetConfig.cs` | Planet generation settings and runtime chunk/job budgets (`VolumetricMaxCellSize`, cave globals, vegetation caps) |
| `PlanetSpace.cs` | World ↔ planet-local unscaled transforms |
| `PlanetNoiseCache.cs` | Shared per-planet noise instances (biome, erosion, cave worm/cavern/detail) |
| `PlanetChunkManager.cs` | Face quadtree updates, job scheduling, parent-hold apply, sphere edits |
| `FaceQuadtree.cs` | Per-face split/merge/prefetch and neighbor lookup |
| `QuadNode.cs` | Leaf/`VoxelChunk`/`GeneratedMesh`, `TransitionMask`, interior-aware camera priority |
| `CubeSphereMath.cs` | Cube-face UV <-> sphere direction conversions |
| `DensityGenerator.cs` | Fills radial `VoxelChunk` shells; `ComputeInteriorBounds`, `RadialLayerCount` |
| `PlanetDensitySampler.cs` | Density at a local point (procedural + multi-scale caves + edits) |
| `PlanetDensityRaycast.cs` | `Raycast` / `Spherecast` / local isosurface / penetration |
| `PlanetMeshGenerator.cs` | Heightfield shell (coarse) or stacked `VoxelChunk` → transvoxel (fine) |
| `PlanetVoxelEditStore.cs` / `PlanetVoxelEditAsset.cs` | Live strokes + sidecar DTO |
| `PlanetManipulationApi.cs` | Static `DigSphere` / `BuildSphere` helpers |
| `PlanetWater.cs` | Planet ocean shell mesh generation |
| `PlanetAssetIO.cs` | `.planet` DTO + `.planetvox` sidecar + path normalization |
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
| `TransvoxelMesher.cs` | Regular + transition-cell meshing; `TransvoxelMeshData.Append` merges radial shells |
| `MarchingCubesTables.cs` | Lookup tables for marching cubes/transvoxel topology, including full 512-case `TransitionVertexData` and 56-class `TransitionCellData` |

---

## Planet-Aware Physics

### Rigidbody

`Rigidbody` now supports curved-world behavior:

- Finds nearest active planet each fixed tick
- Computes `LocalUp` from planet center to body position
- Applies gravity along `-LocalUp` when on a planet (fallback: world `-Y`)
- Grounds with `Spherecast` / `RaycastDensity` along `-LocalUp` and `ResolveDensityPenetration` (cave floors, walls, ceilings)
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
- Provides debug shell bounds for collider visualization (gizmos still sample `SampleSurfaceRadius` for the outer shell)
- Exact player/body contact is density ray/spherecast on `PlanetTerrain`, not the AABB shell

### RigidbodyPlayer

`RigidbodyPlayer` is planet-aware and uses `Rigidbody.LocalUp`:

- Builds move axes from a tangent basis derived from `LocalUp`
- Applies acceleration and drag in tangent space on planets
- Jumps along `LocalUp` (not always world +Y)
- **Density grounding:** after tangent move, `ResolveDensityPenetration` then a short `Spherecast` / `RaycastDensity` probe along `-LocalUp` (capsule height + step-up + ground snap — not a ray to the core). Stands on the density hit; goes airborne when contact is lost. `SampleHeightfieldRadius` is only a last-resort fallback near the outer shell when chunks are not ready
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
- Planet assets are saved as `.planet` JSON files; voxel strokes as `.planetvox` sidecars
- Graph paths are stored as project-relative paths when possible
- `PlanetTerrain.TryLoadBiomeGraph()` resolves both relative and absolute paths
- `PlanetTerrain` normalizes and persists `PlanetAssetPath`/`BiomeGraphPath` project-relative when possible
- Scene compile applies graph results live and triggers `SceneService.NotifyChanged()`

Recommended setup:
1. Add `PlanetTerrain` to a root GameObject
2. Add `PlanetPlayerSpawner` to the planet (or any scene object) **or** manually add a player with `RigidbodyPlayer` + `Rigidbody` + `CapsuleCollider`
3. Ensure there is a `Camera` for the player/controller
4. Author and compile a biome graph, then assign/verify `BiomeGraphPath`
5. On land biomes, enable `CavesEnabled` in the Biome Graph layer properties (ocean/beach default off)

---

## Scene View LOD, Play LOD, and editor brushes

Scene View runs the real quadtree LOD every editor frame (`PlanetTerrain.UpdateSceneViewLod` / `RefreshLodAroundCamera`):

- Authored `MaxLodDepth` is kept; active-chunk / leaf / apply budgets are raised for editor orbit
- When the fly camera is **inside** the planet, interior chunk budgets apply (higher `VolumetricMaxCellSize`, more leaves, faster mesh apply)
- Parent-hold LOD still applies while orbiting or underground

**Play mode** uses the same `RefreshLodAroundCamera` path with play-specific budgets (see **Interior LOD and rendering** above). Interior refinement is no longer editor-only.

**Scene View planet brushes** (Inspector **Planet brushes (Scene View)** when a GameObject with `PlanetTerrain` is selected):

- Tools: **Dig**, **Build**, **Smooth**, **Flatten**, plus **Radius** / **Strength** / **Falloff** sliders
- Hover shows a ring gizmo on the density surface
- Left-drag paints; right-drag or **Shift** inverts Dig/Build
- Picking: camera ray → `PlanetTerrain.Raycast` (density). Hits the side you clicked, including cave walls — not an XZ heightmap and not player-underfoot radial projection
- Dig/Build call `DigSphere` / `BuildSphere`; Smooth/Flatten call `SmoothSphere` / `FlattenSphere`
- Mouse-up runs `SaveVoxelEdits()` to the `.planetvox` sidecar

Play-mode **PlanetTool** (Standard Assets) uses the same look-ray path: LMB dig / RMB build along the camera look-ray (`Raycast`), `[` `]` radius, `-` `=` strength. On mouse-up it also calls `SaveVoxelEdits()`.

`PlanetManipulator.AutoApply` rays from the manipulator GameObject toward the planet center, then away if needed, and paints the **surface hit** (not the planet pivot).

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

- **`DirX` / `DirY` / `DirZ`**: unit direction from the **planet pivot** toward the instance. World position is reconstructed with `SampleSurfaceRadius(dir)` (outer crust; not raw `PosX/Y/Z` in the file). For a painted pit or cave mouth, prefer `TrySampleLocalIsosurface` at runtime.
- **`ModelPath` / `PrefabPath` / `TexturePath`**: project-relative asset references. **Imported trees** often store `MeshFilter.ModelPath` only on **child** objects (multi-part FBX); export walks the hierarchy so the `.planet` file still gets the correct model path.
- **`UseStoredPlacements`**: mirrors `PlanetVegetationSystem.UsePlanetAssetPlacements`. Enable **Use .planet Vegetation Placements** in the inspector if you want spawn logic to prefer saved rows; `AutoUseSavedPlacementsWhenPresent` can still opt in when the flag was off but the file contains placements.
- **Spawn budget**: `MaxAssetSpawnsPerUpdate` limits how many saved placements materialize per tick. Grass is spawned **before** trees in that tick so grass is not starved when the budget is small. After scene load, a **one-shot warmup** applies a larger budget so grass and trees both appear quickly.
- **Saving during deferred import**: When synchronous load skips applying vegetation, `PlanetTerrain` keeps a **clone of the vegetation block** and a **snapshot flag** so `SavePlanetAsset` substitutes that clone whenever live export is still empty (including if async hydrate aborts and `AsyncVegetationHydrationPending` is cleared early). The snapshot is dropped only after `PlanetVegetationSystem.ImportAssetData` loads rows into memory or imports an explicit empty `Placements` array.

### Planet Grass Attachment and Rendering Notes

Planet grass spawned through `VegetationPainter.BuildOnPlanetPatch(...)` and biome/asset grass include planet-specific grounding:

- blade placement blends local slope normal with radial-up (`ResolvePlanetGrassWorldUp`, `SeatGrassOnSurface`) for stable terrain contact
- roots are embedded into the sampled surface to reduce floating on steep relief
- trees sink along radial AABB (`TreeRadialSurfaceBias`) so trunks seat into the rendered shell
- planet grass chunk culling uses 3D world distance (X/Y/Z), not flat XZ distance
- grass materials are forced to alpha-cutout behavior to avoid opaque card fallback
- if external texture decode fails, a generated cutout fallback blade texture is used
- `MaxVegetationSpawnsPerUpdate` is capped during play (32) to keep frame time stable
- **Default fallbacks** when a profile item has no texture/model: `Assets/Standard Assets/Planet Vegetation/Simple Grass_01.psd` and `Meadow_Grass_01_Var4.FBX`
- **Grass scale/height**: planet batches clamp `GrassHeight` to roughly **2.0–4.5 m** (via `GrassBaseHeight` and per-item min/max scale) so imported tufts read at landscape scale instead of lawn scale
- **Multi-type grass per leaf**: when `BatchGrassPerLeaf` is on, up to **4** weighted grass items from the profile can spawn as separate patches in the same leaf (offset patch directions), improving variety without extra draw-call churn per blade

### Streaming Stability (LOD-Safe Keys)

Vegetation streaming keys are **not** tied to quadtree leaf IDs (which change on every LOD split/merge). Instead, each plant group is keyed by a fixed **18×18 UV grid per cube face** (`face:iu:iv`). That keeps grass and trees in place when the planet refines or coarsens around the camera.

Near-camera culling uses **hysteresis**: despawn distance is ~**1.45×** the spawn distance so instances do not flicker in/out at the stream boundary. Leave `CullVegetationWhenLeafNotActive = false` unless you explicitly want leaf-key-based culling (it will fight LOD).

### Imported Tree / Grass FBX Pipeline

Imported biome and asset vegetation goes through a bake step before spawn:

1. **`LooksLikeImportedGrassModel`** — classifies grass by **filename** (`grass`, `meadow`, `fern`, …) and **excludes** trees (`pine`, `tree`, `oak`, …). Folder names like `new trees and grass` must not treat pines as grass.
2. **`ReorientImportedTreeMeshesToYUp`** — for non-grass FBX, measures AABB and applies a single 90° rotation so the tallest axis is local **+Y** (Z-up → Rx(-90°), X-up → Rz(+90°)). Planet spawn assumes +Y is the trunk.
3. **`TransformUtil.AlignLocalUp`** — after import bake, tree spawn rotation uses the same surface-alignment path as the player capsule on planets (stable on slopes, parent-space aware).
4. **Spawn order** — align rotation to surface normal first, then **`SinkTreeRootsToSurface`** along trunk-up and radial so feet seat into the rendered crust.
5. **Template cache** — imported tree templates are cached by absolute model path + suffix (`|hier_v25_filename`). Bump the suffix when import/orientation logic changes so old wrongly-baked meshes are not reused.

Use **`ImportedTreeMeshEulerCorrection`** only for one-off asset fixes (e.g. trunk authored along `-Y`); most Unity/Megascans Z-up trees should need `(0,0,0)` after the stand-up step.

### Planet Precipitation Notes

Planet weather precipitation uses layered particle emitters around the camera:

- supports multiple vertical layers for continuous volume coverage
- supports visibility polling so precipitation work is skipped when the volume is not near/in view
- rain/snow emission remains continuous while state is active and visible
- by default, weather uses a performance budget profile (layer cap + particle cap + emission cap)
- optional planet surface-hit termination can be disabled for weather emitters to reduce script cost
- emitters support planet gravity alignment (nearest active planet center)

**Underwater on planets:** `UnderwaterQuery` treats deep interior and caves as **dry** unless the point is below sea level **and** inside an ocean column (between crust and sea sphere) or clipped into the sea mesh. Caves under land stay walkable without swim physics.

Recommended runtime tuning for weak CPUs:

- `UsePrecipitationPerformanceBudget = true`
- `MaxActivePrecipitationLayers = 1`
- `DisableSurfaceHitForWeatherPrecipitation = true`
- lower `RainEmissionRatePerLayer` and `SnowEmissionRatePerLayer` first before adding layers

