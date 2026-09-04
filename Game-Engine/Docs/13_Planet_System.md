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
| `BiomeMap` | Resolves biome blends per sphere direction (altitude lapse, water moisture, rain shadow) |
| `BiomeGraph` / `PlanetRecipe` | Node graph compiles into recipe + LUTs; life/scatter/fauna tables |
| `PlanetClimateAtlas` | Baked 6-face climate/height/biome LUTs (+ optional flow-river mask) |
| `PlanetChunkMeshCache` | In-memory mesh cache keyed by face/lod/uv/seed/`RecipeHash`/editStamp |
| `PlanetAssetIO` | `.planet` JSON plus `.planetvox` sidecar load/save |
| `PlanetCollider` | Broad-phase AABB and gizmo shell (not triangle contact) |
| `Rigidbody` / `CharacterController` | Radial gravity; surface mode uses StandRadiusGrid on crust |
| `RigidbodyPlayer` | FixedUpdate planet motor + SurfaceMode latch; stand-grid camera on crust |
| `PlanetLifeStreaming` / `PlanetFloraSpawner` / `PlanetScatterRenderer` | Life/scatter companions; fauna tables are data-only until AI consumes them |
| `PlanetPlayerSpawner` | Play-mode spawner: RigidbodyPlayer + capsule + camera on the crust |
| `Camera` | Supports custom `WorldUp` so horizon follows local surface normal |

---

## PlanetTerrain Component

`PlanetTerrain` is the entry point for planet worlds and should be attached to the planet root GameObject.

### Persisted properties

| Property | Default | Description |
|----------|---------|-------------|
| `Radius` | `1000` | Base planet radius |
| `SeaLevelFraction` | `0.55` | Fraction of terrain min/max range used when no graph ocean body overrides sea level (see **Planet water**) |
| `MaxLodDepth` | `6` | Max quadtree depth |
| `ChunkSize` | `32` | Voxel/mesh chunk resolution |
| `LodDistanceMultiplier` | `5.0` | LOD split distance tuning |
| `Seed` | `42` | Planet seed |
| `EnableCaves` | `true` | Enables cave carving in the density pipeline |
| `EnableWater` | `true` | Enables planet water rendering (orbit shell + per-chunk patches) |
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
| `RaycastDensity(worldOrigin, worldDirection, maxDistance, out PlanetDensityHit hit)` | Ray-march crust density (caves included). Default quality is **editor** (96 steps / 10 refine) |
| `RaycastDensityGameplay(...)` / `SpherecastGameplay(...)` | Same field, **gameplay** quality (32 steps / 4 refine). Player motors use these |
| `Raycast(...)` | Alias of `RaycastDensity` (Scene View brushes) |
| `RaycastPaintSurface(...)` | Play-mode tool pick: iso crossing, then geometric fallback |
| `Spherecast(worldOrigin, worldDirection, worldRadius, maxDistance, out hit)` | Thick query for capsule/rigidbody contact |
| `TrySampleLocalIsosurface(sphereDir, out localPoint, out localNormal)` | First air→solid crossing inward along a cube-sphere direction (pits, cave mouths) |
| `ResolveDensityPenetration(ref worldPos, worldClearance)` | Push a point out of solid density |
| `SampleCollisionRadius(sphereDir)` | Local stand radius on the **visible** leaf (`FindRenderableAtDirection`), not a prefetch child or neighbor peak |

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
- Climate coupling (runtime + climate LUT bake):
  - `AltitudeLapseRate` — temperature falls with normalized height so peaks become tundra/snow
  - `WaterMoistureBoost` — moisture rises near compiled ocean/river/lake (`PlanetWaterSampler`)
  - `RainShadowStrength` (+ `RidgeStrength`) — moisture drops on the lee side of ridges
  - `ShoreClimateBias` — `ApplyShoreSand` scales sand bands by local moisture
  - Optional `UseFlowAccumulationRivers` (default **false**) — one-shot D8 flow bake on the height LUT; noise rivers stay the default
- `RecipeHash` from compile keys the in-memory chunk mesh cache (`PlanetChunkMeshCache`)
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
- **Output.Water** port accepts `WaterBody`, `WaterPath`, `Shore`, `WaterMerge`, or legacy `River` nodes (compiled up to 8 bodies / 8 paths).
- `WaterBody` kinds: `Ocean`, `Lake`, `Pond` at independent `FillFraction` levels (0–1 of terrain min–max radius). Oceans skip continent land and mid-altitude columns; lakes/ponds fill the local hole, not global sea level. Volcano calderas can emit `PlanetWaterKind.Lava`.
- `WaterPath` / `River` carve channel depth into the heightfield and contribute sand banks via `SandBiome` (e.g. Beach). `FlowToOcean` blends a fraction of the remaining hole by river mask — it does not snap the whole channel to ocean fill.
- `Shore` overrides shore biome/width on upstream bodies; `WaterMerge` combines branches.
- Layer `SpawnWater` still marks ocean biomes for masks when no graph bodies are present.
- Compiled `WaterBodies` / `WaterPaths` are stored on `PlanetConfig` and persisted in `.planet` JSON via `PlanetAssetIO`.
- When an ocean `WaterBody` is present, `SeaLevelFraction` on `PlanetTerrain` is synced from that body's `FillFraction` on compile.

See **Planet water** below for mesh generation, rendering, and underwater rules.

- Shoreline tinting blends water color toward nearby shore biome colors; per-body shallow/deep/deepest tints are passed to the water shader.
- River settings (`RiverWidth`, `RiverDepth`, `Frequency`, `Meander`, `AllowedBiomes`) remain on `PlanetConfig` as fallbacks when graph water is unconnected.
- Terrain sand bands near rivers/shores bias blend weights toward the configured shore biome index (`PlanetMeshGenerator.ApplyShoreSand`) **only above** the waterline (scaled by climate moisture).
- Vegetation profiles are authored per biome layer in the Biome Graph properties panel:
  - profile selector + `New/Save/Delete/Reload`
  - editable grass/tree type lists with per-item mesh path, weight, density multiplier, and min/max scale

### Life / scatter / climate nodes

The palette also includes authoring nodes that compile into `BiomeGraphResult` / `PlanetRecipe` tables (not evaluated per-voxel at runtime):

| Category | Nodes | Compiled tables |
|----------|-------|-----------------|
| Geology | `Continent`, `Crater`, `Volcano`, `Cliff`, `DomainWarp` | `Continents`, `Craters`, `Volcanoes`, `Cliffs`, `DomainWarps` (+ `MacroFrequency` from Continent) |
| Climate | `Climate`, `RainShadow`, `Season`, `LatitudeBand` | `ClimateNodes`, `RainShadows`, `Seasons`, `LatitudeBands` |
| Life | `FloraLayer`, `ScatterLayer`, `FaunaLayer`, `UnderwaterLife`, `ResourceVein` | `FloraLayers`, `ScatterLayers`, `FaunaLayers`, `UnderwaterLife`, `ResourceVeins` |
| Atmosphere | `Atmosphere`, `WeatherProfile`, `CloudLayer` | `AtmosphereNodes`, `WeatherProfiles`, `CloudLayers` |
| Water extras | `IceSheet`, `Wetland` | `IceSheets`, `Wetlands` |

`BiomeLayer` remains the ground material. Flora/Scatter attach via `Output.Life` / `Output.Scatter` (or stand alone in the graph). `FloraLayer` can push profile id, densities, patchiness, and growth/treeline ranges onto matching layers by `TargetBiome`.

Runtime companions (optional on the planet GameObject):
- `PlanetLifeStreaming` — face/UV cell keys + fauna/underwater/vein recipe bind
- `PlanetScatterRenderer` — GPU-instanced rock/grass buffer hook (`DrawArraysInstanced`-ready)
- `PlanetFloraSpawner` — unique imported-mesh cap for trees

`PlanetVegetationSystem` keeps streaming plants; `UseUniversalLandVegetation` defaults to **false** so per-biome `VegetationProfileId` matters. It honors `VegetationPatchiness`, growth temperature/moisture, and tree slope/altitude reject.

On `ApplyGraphResult`, `PlanetTerrain` binds compiled tables onto companions when they exist: `PlanetFloraSpawner.ApplyRecipes`, `PlanetScatterRenderer.ApplyRecipes`, `PlanetFaunaTableBehavior.Bind`. `PlanetLifeStreaming.BindRecipe` is the same table hook for later fauna/underwater/vein consumers; vegetation already shares its 18×18 face/UV cell keys.

Built-in vegetation profiles: `Default`, `Universal`, `Forest`, `Grassland`, `Desert`, `Alpine`, `Tundra`, `Volcanic`, `Ocean` (with `TreeItems` / `BushItems` / `RockItems` stubs).

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

**Parent-hold LOD:** a node stays a visible leaf (`IsLeaf`) until `TryCommitSplit` succeeds — all four children already have `GeneratedMesh`. Prefetch allocates children and generates their meshes while the parent mesh still renders (`CollectRenderable` keeps the parent if children are incomplete). On merge, **any parent that had visible child meshes must rebuild** — the pre-split parent mesh is stale and is never reused after a budget merge. This avoids missing-face holes while LOD refines and prevents standing on coarse geometry after fine chunks are destroyed.

**Play-mode LOD ownership:** while Play is active, **Game View owns planet split/merge**. Scene View still renders the runtime world for inspection but does **not** drive quadtree LOD updates. `PlanetTerrain.Update()` in play only applies completed async mesh jobs (lightweight path) or runs a full refresh after voxel edits. Game View throttles planet LOD (~**0.40 s** / **18 m** camera move outside crust) and clamps render delta time so unfocus/refocus (screenshot, alt-tab) does not batch destructive LOD work on the first frame back.

**Play-mode merge safety:** budget merges skip any parent quad whose children touch within **~150 m** of the play camera. Merge hysteresis tolerates up to **~6** chunks over the leaf cap before merging, reducing churn when hovering near the budget (e.g. 63/64 leaves). Never merge a sibling leaf’s parent if you are standing on another child of that parent — that was a common cause of pits and broken shards while idle.

**Transition / stitch remesh in play:** `UpdateTransitionMasks` still records new seam masks when neighbor LOD changes, but in **Play mode it does not force an immediate live remesh** of existing leaves. Stitch parameters apply on the next natural rebuild (split commit, merge rebuild, edit, or first generation). This stops slope/peak T-junction snaps from rewriting the mesh under a stationary player. Editor Scene View still remeshes on mask change.

### Interior fill and stacked voxel shells

`DensityGenerator.ComputeInteriorBounds` defines solid fill from `radialMin` (~4% of `Radius`, minimum 16) out to `radialMax` (surface + amplitude/brush padding).

Fine leaves do **not** stretch one 32³ grid across the entire radius (that would make 20 m+ cells and shred caves). Instead `PlanetMeshGenerator` stacks **1–4 radial shells** per leaf (`DensityGenerator.RadialLayerCount`), each ~320 m thick with 32³ samples, then merges the transvoxel meshes with `TransvoxelMeshData.Append`.

| Leaf tangential cell | Mesh mode |
|----------------------|-----------|
| `> VolumetricMaxCellSize` | Smooth **heightfield shell** (orbit / coarse Scene View) |
| `≤ VolumetricMaxCellSize` | **Stacked transvoxel** shells (caves + interior rock) |

`VolumetricMaxCellSize` defaults to **3.5** at orbit. When **`CameraBelowCrust`** is latched true (density probe at the camera with hysteresis, throttled ~**0.25 s** / **6 m** move), `PlanetTerrain.ApplyChunkBudgets` raises it to **11**, increases leaf/chunk caps, and boosts mesh job budgets so interior cave walls refine — in **both Play and editor** (editor gets slightly higher caps when underground).

**Play-mode interior profile** (when `CameraBelowCrust` latch is true in Play): `MaxLodDepth` up to **6**, `MaxActiveChunks` / `MaxLeafNodes` toward **120–160**, `MaxGenerationSchedulesPerUpdate` **~14**, `VolumetricMaxCellSize` **11**. Orbit play outside the crust band keeps tighter caps (depth **4–5**, **32–64** leaves, **6** schedules). Walking the outer shell no longer false-triggers interior LOD (the old `camR < 1.08 × radius` heuristic remeshed whole hills as volumetric and spiked triangle count).

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

Fine volumetric leaves set `TransitionMask` on edges that border a coarser neighbor. `TransvoxelMesher.GenerateMesh` calls `GenerateTransitionCells` when the mask is non-zero (toggle with `PlanetConfig.EnableTransvoxelTransitions`, default **true**). Heightfield shells also run `SnapLodTJunctions` so odd edge verts align to coarser neighbors (T-junction crack prevention). Transition cells use the full Lengyel **13-corner** layout (`MarchingCubesTables.TransitionVertexData` / `TransitionCellData`) — not the old 9-sample ring interpolation. Only the outer radial shell layer currently receives the mask (enough for cave mouths and crust LOD boundaries).

**Play-mode stitch policy:** mask/stride updates are recorded every LOD tick, but existing rendered meshes are **not** torn down for stitch-only changes during Play. Editor orbit continues to remesh immediately when seams change so you can inspect LOD boundaries while flying the Scene camera.

### In-memory chunk mesh cache

`PlanetChunkManager` keeps a `PlanetChunkMeshCache` (256 entries). The key is face / LOD / quantized UV / seed / `Config.RecipeHash` / edit stamp (`SphereEditCount` + `BakedCellCount`). A revisited leaf reuses terrain mesh, water patch, voxel chunk, and stand grid instead of remeshing. `RequestFullShellRebuild` and `ClearMeshCache()` drop the cache. Graph compile writes a new `RecipeHash`, so the next generate miss rebuilds under the new recipe.

### Play-mode voxel edits

Dig/build in Play no longer forces every leaf back to a heightfield shell after the first stroke. `PlanetMeshGenerator.ShouldUseVolumetric` keeps volumetric remesh on overlapping leaves (small-brush coarse-leaf exception: `MaxRadius ≤ 2.5 m` and `cell > VolumetricMaxCellSize` may stay shell-only). `PlanetChunkManager.ApplyPlayModeEditVisual` dirtys overlapping leaves and schedules async remesh with a play budget (**4–8** nodes); coarse non-volumetric leaves may get a one-frame `PlanetShellDeformer` preview.

Hierarchy/runtime note:
- Chunk `GameObject` children are no longer created (no `PlanetChunk_*` scene hierarchy spam)
- Runtime chunk meshes are cached on quadtree leaves and rendered directly
- Rendering reads `PlanetChunkManager.GetRenderableLeaves()` for terrain passes; each leaf may also carry `GeneratedWaterMesh` for close-up water

---

## Graph geology and climate (runtime)

Compile tables are not decoration — `PlanetSurfaceUtility` and `BiomeMap` apply them while meshing and classifying.

### Geology (`PlanetSurfaceUtility.ApplyGraphGeology`)

When `PlanetConfig.Continents` is non-empty:
- `SampleContinentLand` builds a 0–1 land mask from continent frequency / threshold / strength.
- Ocean floor sits ~**10 m** below radius; a **narrow coastal band** (`Smooth01(0.50, 0.57, land)`) lifts land instead of a 200 m ramp that LOD would flatten into two triangles.
- Extra range noise (~42 m) applies only on land (`land > 0.42`).

Other compiled features:
- **Craters** — bowl subtract + rim add from nearest-feature samples.
- **Volcanoes** — cone height on land; **lava lakes** only inside the caldera (`TryGetLavaLake`). Outer cone walls and the inner rim above the pool stay rock.
- **Cliffs** — ocean-side drop + land-side lip on the coastal land band.

Biome amplitude is compressed in `SampleHeight` so Ocean(5) next to Mountains(85) cannot build a one-triangle pyramid. Height is accumulated, then **reclassified with altitude** (`biomeMap.GetBiomes(dir, altitude)`) and accumulated again.

### Climate atlas (`PlanetClimateAtlas`)

On graph apply, `PlanetTerrain` bakes a 6-face LUT (default **256²** per face): temperature, moisture, height, top-two biome indices/weights, and optional flow-river mask. Runtime samples bilinearly instead of allocating biome lists per voxel.

`BiomeMap` climate coupling:
- Temperature falls with normalized altitude (`AltitudeLapseRate`).
- Moisture rises near compiled ocean / river / lake (`WaterMoistureBoost`).
- Moisture drops on the lee of ridges (`RainShadowStrength` × `RidgeStrength`).
- `SampleShoreClimateWeight` scales beach sand by local moisture (`ShoreClimateBias`).
- `UseSelectClassifier` (from `BiomeSelect` compile) uses climate-box rules instead of the default blend.

Optional `UseFlowAccumulationRivers` (default **false**) bakes a one-shot D8 flow channel on the height LUT. Noise rivers stay the default. When enabled, `PlanetWaterSampler.ApplyWaterCarving` subtracts `FlowRiverDepth` where the atlas flow mask is high.

---

## Planet Water

Planet water is a **two-tier** system: a low-resolution **orbit shell** for distant silhouettes and **per-chunk water patches** that follow terrain LOD. Chunk patches are the shoreline you walk on.

### Water table sampling (`PlanetWaterSampler`)

| API | Role |
|-----|------|
| `SampleWaterSurface(sphereDir, config, biomeMap, terrainRadius, …)` | Returns local water radius, mask, shore biome index, kind (`Ocean` / `Lake` / `Pond` / `River` / `Lava`), and body index |
| `GetOceanFillRadius(config)` | Resolves ocean fill from graph bodies; clamps so sea level covers the ocean biome floor (not below `Radius + ocean HeightAmplitude`) |
| `ResolveSeaLevel(config, seaLevelFraction)` | Authoritative sea level for legacy or multi-level setups |
| `ApplyWaterCarving(...)` | Lowers heightfield for rivers, optional flow-accumulation channels, and pond/lake basins |
| `SampleSandWeight(...)` | Dry shoreline / river-bank sand blend (never paints Beach under the water mesh) |

**Lava** is evaluated first: if `TryGetLavaLake` reports magma `> 0.18` and the pool sits above terrain, the sample is `PlanetWaterKind.Lava` (body index **6**). Lava is **not** swim water.

**Multi-level water** (when `PlanetConfig.WaterBodies` is non-empty):
- Classification uses **altitude** (`NormalizeAltitude`). Ignoring it marked wet midland as Ocean and flooded hillsides around deep basins.
- **Ocean** — flood only where the column is actually a basin. Skip when continent land `> 0.38` or altitude `> 0.11`. Continents stay dry even if the classifier still says Ocean.
- **Lake / Pond** — sit **in the hole**, not at global sea level. Require a hole deeper than `MinBasinDepth`; fill radius is `terrainRadius + min(4.5, hole × 0.22)`. A grassland valley is not a second ocean.
- **Scoring** picks the body that **fits this column** (`match × (1 + basinDepth × 0.05)`, ocean +0.5). Highest water table always won before and put lakes at mountain mid-height.
- **River** — noise-line mask × allowed biomes; water sits at carved bed + 0.35 m. `FlowToOcean` no longer snaps the whole channel to ocean fill — it blends a fraction of the remaining hole by river mask.

**Legacy fallback** (no compiled bodies): uses `SpawnWater` biome weight + optional `HasRiver`; single `SeaLevel` shell.

**Shore sand:** only **above** the waterline (`terrainRadius >= fillR − 0.25`). Submerged columns stay the underwater mesh — painting Beach there was the tan “fake water.” Weight is then scaled by `SampleShoreClimateWeight`.

### Mesh generation

| Mesh | Source | When used |
|------|--------|-----------|
| **Orbit shell** | `PlanetWater` — uniform cube-sphere at `GetOceanFillRadius`, 56 subdivisions per face | Far-orbit silhouette only; also reused as the atmosphere proxy mesh |
| **Chunk patches** | `PlanetMeshGenerator.GenerateWaterPatch` — same UV grid as the terrain leaf | **Always preferred** whenever a renderable leaf has `GeneratedWaterMesh` |

Chunk water rules:
- Built asynchronously with terrain in `PlanetChunkManager` → stored on `QuadNode.GeneratedWaterMesh`.
- Vertices sample `SampleWaterSurface` at each grid point; dry verts are omitted from the index buffer.
- Ocean/lake/pond triangles use the water-table radius; rivers use bed + offset; lava uses the caldera pool radius.
- Shore verts are placed on the **visible** terrain-edge / sea-sphere intersection so the waterline matches the LOD you stand on (not a planet-wide 48-subdiv mesh).
- Triangles spanning more than ~1.75× the leaf cell size are dropped to avoid sky-spike shards.

`PlanetTerrain.SetupWater()` creates the orbit shell child `PlanetWater` with a `MeshFilter` / `MeshRenderer`. `RebuildWater()` runs after biome graph apply and on init when `EnableWater` is true.

### Rendering (`SceneRenderer.RenderPlanetWater`)

Draw order (Game View deferred path **and** standalone `PlayerView`, after planet terrain):
1. Planar `Water` components (legacy flat water)
2. **Planet atmosphere** shell
3. **Planet clouds**
4. **Planet water** — drawn **after** atmosphere/clouds so haze does not cover the surface

Water pass state: double-sided (`CullFace` off), `DepthFunc.Lequal`, **depth write off**, alpha blend.

`RenderPlanetWater` draws **every** renderable leaf water mesh. It does **not** skip far-hemisphere patches, coarse parents, or frustum-sphere tests that used to leave grassland “oceans” from the orbit shell. The uniform `PlanetWater` GameObject is drawn only when:
- the camera is **not** inside crust density,
- the camera is **not** near-surface,
- **no** chunk patches were drawn, and
- camera distance `> 1.6 ×` planet radius (far-orbit silhouette).

Near the surface, wave amplitude in the vertex shader is reduced (~0.08 vs 0.4 orbit) to limit mesh swimming artifacts.

Shader: `PlanetWaterVert` / `PlanetWaterFrag` — Gerstner-style radial waves, Fresnel sky/atmosphere reflection, per-body color arrays (`uBodyShallow/Deep/Deepest[8]`), shore biome tint from packed UV.x, mask discard when `waterMask < 0.02`. Slot **6** is lava (orange/black). Slot **7** is a reserved dark fallback. The renderer always binds **8** biome albedo slots (white texture + default tiling when a layer is missing) so unused indices do not sample garbage.

### Underwater (`UnderwaterQuery`)

Used by post-processing, `Rigidbody`, and `RigidbodyPlayer`. Swim **physics** and the **underwater post pass** are separate.

**Enter swim (`RigidbodyPlayer` + `PlanetTerrain.TryGetWaterColumn`):** the body is in an ocean / lake / pond column (lava bowls are not swim water). The player lies on the waterline (`IsPlanetSwimming`). Dry banks, caves under the crust, and standing on land above the table do not start swim.

**Surface swim (working default):**
- Chest on the water table; WASD is tangent (along the surface).
- **Space** rises / returns to the waterline.
- **Ctrl** (crouch) dives toward the planet center. Look-down + W is **not** dive.
- Releasing Ctrl **hovers at the current depth** — it does not auto-surface. Only Space pulls you up.
- Head and camera stay **above** the water mesh. Land `_surfaceMode` eye snap (crust + eye height) is skipped so the camera is not pulled to the seabed.
- Underwater post is **off**.

**Submerged (`IsPlanetSubmerged`):** the first-person `LookEye` (or eye stand) is ≥ **0.30 m** under the water table (clears at ≤ **0.10 m**). Holding Ctrl on the surface is not enough.

**Underwater post (`GetState` + Game View):**
1. Sample `SampleWaterSurface` along the camera radial; require `Mask ≥ 0.04`. Skip lava.
2. Camera must be ≥ **0.28 m** under that table (not merely “in the swim volume”).
3. Skip deep cave air (`camera < crust − 2.5 m`).
4. If a live `RigidbodyPlayer` exists, require `IsPlanetSwimming && IsPlanetSubmerged`. Scene View with no player still uses the camera vs the water table.
5. Weather wetness / snow / land fog do **not** run in this pass.

Per-body underwater tint comes from the matching `PlanetWaterBody` deep colors when kind is ocean/lake/pond; rivers use the planet `OceanBiome` preset.

**Game View note:** Paused play mode still renders the last frame; only **Stopped** clears to the dark editor backdrop.

---

## File Map

### Planet folder (`Core/Planet/`)

| File | Purpose |
|------|---------|
| `PlanetConfig.cs` | Planet generation settings and runtime chunk/job budgets (`VolumetricMaxCellSize`, cave globals, vegetation caps) |
| `PlanetSpace.cs` | World ↔ planet-local unscaled transforms |
| `PlanetNoiseCache.cs` | Shared per-planet noise instances (biome, erosion, cave worm/cavern/detail) |
| `PlanetChunkManager.cs` | Face quadtree updates, job scheduling, parent-hold apply, mesh cache, `FindRenderableAtDirection`, play merge safe zone, sphere edits, `ApplyCompletedMeshJobs()` |
| `FaceQuadtree.cs` | Per-face split/merge/prefetch, neighbor lookup, `CommitReadySplits`, transition masks |
| `QuadNode.cs` | Leaf/`VoxelChunk`/`GeneratedMesh`, `TransitionMask`, interior-aware camera priority |
| `CubeSphereMath.cs` | Cube-face UV <-> sphere direction conversions |
| `DensityGenerator.cs` | Fills radial `VoxelChunk` shells; `ComputeInteriorBounds`, `RadialLayerCount` |
| `PlanetDensitySampler.cs` | Density at a local point (procedural + multi-scale caves + edits) |
| `PlanetDensityRaycast.cs` | `Raycast` / `Spherecast` / local isosurface / penetration |
| `PlanetMeshGenerator.cs` | Heightfield shell (coarse) or stacked `VoxelChunk` → transvoxel (fine) |
| `PlanetVoxelEditStore.cs` / `PlanetVoxelEditAsset.cs` | Live strokes + sidecar DTO |
| `PlanetManipulationApi.cs` | Static `DigSphere` / `BuildSphere` helpers |
| `PlanetSurfaceUtility.cs` | Height accumulation, continent/crater/volcano/cliff geology, lava-lake query |
| `PlanetClimateAtlas.cs` | Baked climate/height/biome LUTs; optional flow-accumulation river bake |
| `PlanetChunkMeshCache.cs` | RecipeHash-keyed in-memory mesh cache |
| `PlanetWater.cs` | Orbit-only uniform sea-level shell (atmosphere proxy mesh) |
| `PlanetWaterTypes.cs` | `PlanetWaterBody` / `PlanetWaterPath` / `PlanetWaterSurfaceSample` (`Lava = 5`) |
| `PlanetWaterSampler.cs` | Water table, river/flow carve, dry-only shore sand, ocean/continent/lava rules |
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

### Biome graph (`Core/Biome/` and `Core/Biome/Graph/`)

| File | Purpose |
|------|---------|
| `BiomeMap.cs` | Altitude-aware blends, lapse / moisture / rain shadow, shore climate weight, optional `UseSelectClassifier` |
| `BiomeGraph.cs` | Compile to `BiomeGraphResult` + `PlanetRecipe` + `RecipeHash` |
| `BiomeGraphEvaluator.cs` | Compile-time float walk (`BiomeEvalContext`) — not per-voxel at runtime |
| `PlanetRecipe.cs` | Climate / geology / classifier / cave / life-scatter-atmosphere tables |
| `BiomeNode.cs` | Layer, water, geology, climate, life, atmosphere node types |

---

## Planet-Aware Physics

### Rigidbody

`Rigidbody` now supports curved-world behavior:

- Finds nearest active planet each fixed tick (`FindNearestPlanetCached` — rebind after **~48 m** move or planet-count change)
- Computes `LocalUp` from planet center to body position
- Applies gravity along `-LocalUp` when on a planet (fallback: world `-Y`)
- **Surface mode** (`RefreshPlanetSurfaceMode`): walk the outer crust stand radius when radial ≥ crust − **6 m**; leave when radial < crust − **10 m** or `CameraBelowCrust`. Surface mode snaps to `SampleCollisionRadius` (the visible leaf). Interior / cave motion uses density probes
- Grounds with `SpherecastGameplay` / `RaycastDensityGameplay` along `-LocalUp` and `ResolveDensityPenetration` (cave floors, walls, ceilings)
- Keeps tangent velocity when grounded (removes into-surface component)
- Preserves existing non-planet collision paths (terrain, mesh, AABB, triggers)
- Resolves underwater state via `UnderwaterQuery` (local water table; post FX only while the head is under — see **Planet water**)

Additional runtime state:
- `LocalUp`
- `IsGrounded`, `GroundNormal`
- `IsUnderwater`, `UnderwaterDepth`

### PlanetCollider

`PlanetCollider` complements `PlanetTerrain` for broad-phase and tooling:

- Computes planet world AABB from max radius (`base radius + biome max amplitude`) with world-scale awareness
- Exposes `BaseRadius`, `MaxRadius`, and optional `RadiusOverride`
- Provides debug shell bounds for collider visualization (gizmos still sample `SampleSurfaceRadius` for the outer shell)
- Exact player/body contact on the outer crust uses the visible-leaf stand radius; caves use gameplay density ray/spherecast — not the AABB shell

### RigidbodyPlayer

`RigidbodyPlayer` is planet-aware and uses `Rigidbody.LocalUp`:

- Builds move axes from a tangent basis derived from `LocalUp`
- Applies acceleration and drag in tangent space on planets
- Jumps along `LocalUp` (not always world +Y)
- **Density grounding:** after tangent move, `ResolveDensityPenetration` then a short `SpherecastGameplay` / `RaycastDensityGameplay` probe along `-LocalUp` (capsule height + step-up + ground snap — not a ray to the core). On the outer crust, **surface mode** stands on `SampleCollisionRadius` (visible leaf stand grid). Interior uses the density hit. `SampleCollisionRadius` is cached once per frame; heightfield radius is only a last-resort fallback near the outer shell when chunks are not ready
- **Planet swimming:** `TryGetWaterColumn` on the body starts `SwimOnPlanet()` instead of crust walking. Surface float (chest on the table, head/camera dry), WASD tangent, **Space** up, **Ctrl** dive. Releasing Ctrl hovers; look-down + W does not dive. `IsPlanetSwimming` / `IsPlanetSubmerged` / `PlanetSubmergeDepth` expose the mode. `Rigidbody` keeps underwater state only while actually submerged
- Avoids pole-specific movement mode switching to prevent axis flips/discontinuities
- Smooths camera up-vector transitions to reduce horizon jitter
- Writes smoothed up-vector into `Camera.WorldUp` and first-person `LookEye` (Game View must use the look override, not the nested camera transform)
- Supports both first-person and third-person camera offsets on curved surfaces
- **Surface-swim camera:** lifts the eye to `water table + 0.35 m` and never snaps to the land crust stand (that stand is the seabed)

---

## Camera Integration

`Camera` exposes `WorldUp` (default `Vector3.UnitY`), which is used in `GetViewMatrix()`.

For planet traversal:
- Controllers such as `RigidbodyPlayer` set `Camera.WorldUp` each frame
- First-person / third-person planet cameras set `UseLookOverride` + `LookEye` so Game View is not stuck at a nested local offset (which sat inside the water sphere)
- View matrix uses this vector in `CreateLookAt(...)`
- `Camera.GetViewMatrix()` includes forward/up collinearity safeguards for stability
- Result: the horizon aligns with the local planet surface instead of snapping to global Y-up
- While surface swimming, the eye stays above the water mesh; while diving, it follows the body under the table

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
2. Add `PlanetPlayerSpawner` to the planet (or any scene object) **or** manually add a player with `RigidbodyPlayer` + `Rigidbody` + `CapsuleCollider`. Spawn stands on `SampleCollisionRadius` (same radius the motor snaps to) — not an isosurface / density ray that can hit a pit. `EnsureSunLight` **enables** an existing directional light (and turns on shadows) instead of ignoring it or spawning a second sun
3. Ensure there is a `Camera` for the player/controller
4. Author and compile a biome graph, then assign/verify `BiomeGraphPath`
5. On land biomes, enable `CavesEnabled` in the Biome Graph layer properties (ocean/beach default off)

---

## Scene View LOD, Play LOD, and editor brushes

**Editor (Scene View)** runs the real quadtree LOD on its own schedule (`PlanetTerrain.UpdateSceneViewLod` / `RefreshLodAroundCamera` when not playing):

- Authored `MaxLodDepth` is kept; active-chunk / leaf / apply budgets are raised for editor orbit
- When the fly camera is **inside** the planet, interior chunk budgets apply (higher `VolumetricMaxCellSize`, more leaves, faster mesh apply)
- Parent-hold LOD still applies while orbiting or underground
- Transition mask changes trigger immediate remesh in editor (stitch preview while orbiting)

**Play mode (Game View only for LOD split/merge):**

- `GameView` calls `RefreshLodAroundCamera` on a throttled interval (~**0.40 s**) and when the play camera moves ~**18 m** (tighter caps outside crust; see **Interior LOD** above)
- Render delta is clamped (~**0.10 s** max); after a long render pause (> **0.25 s**, e.g. unfocus for screenshot), the first LOD tick **applies finished meshes and commits ready splits only** — no split/merge decisions that frame
- `PlanetChunkManager.Update` runs at most **once per frame**; completed mesh jobs can also be applied from `PlanetTerrain.Update()` without a full LOD pass
- **Scene View during Play** renders the same runtime world but **does not** call `RefreshLodAroundCamera` (avoids dual-view LOD fights that caused chunk splits and refocus glitches)
- Game View HUD shows FPS, GL ms, planet chunk/triangle counts, and top script costs when script sampling is enabled

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

Weather ↔ ground coupling:
- `PlanetWeatherController` runs `ApplyHeldWeatherIntensities` + `PublishRuntimeWeather` **every frame** (not only on `StepWeather` ~0.3s ticks). `_cachedColdness` / `_cachedGrowthMul` refresh on weather steps; wetness/snow still track live rain between steps
- Rain / storm **holds** `Wetness ≥ 0.9` and `RainIntensity = 1`; snow holds `SnowCoverage ≥ 0.85`. Leaving the state damps toward 0 (it no longer chases a low intensity target)
- `SceneRenderer.ResolveWeatherOverlays` also boosts terrain `uWetness` from live `RainIntensity` (`max(wetness, RainIntensity × 0.92)`) and enables overlays when `RainIntensity > 0.05`, so ground wetness appears as soon as rain starts
- Precipitation volumes **within** `PrecipitationHeight` of the camera are always treated as visible. A FOV test treated “straight up the radial” as off-screen and killed rain in under a second
- Vegetation vitality always updates using authored `VegetationRegrowthRate` / `VegetationDecayRate` (wetness helps growth; snow stresses plants)
- Planet terrain shader (`PlanetTerrainFrag`) samples `uWetness` / `uSnowCoverage` / `uWeatherEnabled` for wet tint, **meter-scale FBM puddles** (`groundFlat = smoothstep(0.18, 0.58, slope)`), spec/Fresnel sheen, and sky-tinted puddle reflections — it **scales the lit texel** and never replaces grass with a flat color or hash sparkle
- While a player is planet-submerged (`UnderwaterQuery.AnyPlayerPlanetSubmerged`), weather overlay, weather-driven land fog, and atmosphere lighting attenuation are disabled so rain does not drive the underwater post pass. Weather land fog uses a grey color and **does not** enable volumetric fog

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
- `MaxVegetationSpawnsPerUpdate` is capped during play (**8** trees / **8** grass batches per tick by default via `ApplyChunkBudgets`) to keep frame time stable
- **Play spawn caps:** ~**24** trees and ~**32** grass batches near camera; activation distance ~**220 m** for procedural spawn
- **Deferred FBX import:** imported tree templates load on a background thread; spawn waits until the template is ready instead of blocking the main thread (~1 s+ spikes)
- **Play refresh budget:** `RefreshVegetation` spends ~**4 ms**/frame in play; prefers cheaper tree LOD meshes when available
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
- **camera-frustum spawn:** when an active `Camera` is resolved, each layer calls `ParticleEmitter.SetCameraFrustumSpawn` with axes from the inverted `GetViewMatrix()` (origin, forward, right, up), `FieldOfView`, aspect from `Input.ViewportSize` (same DIP size Game View uses for projection), and near/far clamped for the layer lift. Particles spawn across the full lens with ~1.18× horizontal/vertical margin — **do not** change the `Camera` projection API for this
- **render path:** Game View and PlayerView draw precipitation **after post-processing** via `RenderParticles(..., overlayPass: true)` so SSAO/shadow half-width viewports do not clip rain to the left half of the screen. Scene View draws particles in the normal transparent pass and skips `BiomeWeatherPrecipitation_*` GOs while Game View is playing
- supports visibility polling so precipitation work is skipped when the volume is not near/in view (near-camera overhead rain is always visible — see weather coupling above)
- rain/snow emission remains continuous while state is active and visible; rain uses velocity-stretched streaks (`StretchAlongVelocity`)
- by default, weather uses a performance budget profile (layer cap + particle cap + emission cap)
- optional planet surface-hit termination can be disabled for weather emitters to reduce script cost
- emitters support planet gravity alignment (nearest active planet center)

**Underwater on planets:** swim starts from `TryGetWaterColumn` (body in a basin). The underwater post pass uses `SampleWaterSurface` vs the **camera** and only while `IsPlanetSwimming && IsPlanetSubmerged` (head ≥ **0.30 m** under the table). Surface float stays dry. See **Planet water** for full rules.

Recommended runtime tuning for weak CPUs:

- `UsePrecipitationPerformanceBudget = true`
- `MaxActivePrecipitationLayers = 1`
- `DisableSurfaceHitForWeatherPrecipitation = true`
- lower `RainEmissionRatePerLayer` and `SnowEmissionRatePerLayer` first before adding layers

