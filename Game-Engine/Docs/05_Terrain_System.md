# Game Engine — Terrain System

## Overview

The terrain system provides a heightmap-based, editable landscape with multi-material splatmap painting, chunk-based rendering for performance, per-chunk level of detail, and multiple sculpting/painting tools. Terrain data is stored as a `Terrain` component on a GameObject and persisted to `.terrain.json` files for reliable data preservation.

---

## Creating a Terrain

1. **Right-click** in the Hierarchy panel
2. Select **Terrain**
3. A new GameObject is created with the following components (auto-added via `[Require]`):
   - `Transform` — position, rotation, scale
   - `Terrain` — heightmap data, settings, splatmaps, layers
   - `MeshFilter` — holds the generated terrain mesh
   - `MeshRenderer` — renders the terrain with materials
   - `MeshCollider` — provides collision for raycasting and physics

### Default Settings
| Setting         | Default | Description                          | Constraints |
|-----------------|---------|--------------------------------------|-------------|
| Resolution X    | 129     | Vertices along X axis                | Min: 2 |
| Resolution Z    | 129     | Vertices along Z axis                | Min: 2 |
| Size X          | 100     | World-space width (units)            | — |
| Size Z          | 100     | World-space depth (units)            | — |
| Height Scale    | 20      | Height multiplier (Y range)          | — |
| Chunk Size      | 65      | Vertices per chunk edge (pow2+1)     | — |
| Use Chunking    | true    | Enable chunk-based rendering         | — |
| LOD Levels      | 3       | Number of detail levels per chunk    | 1-3 |

### Memory Layout
Heights are stored as a flat `float[]` array in **row-major** order with length `ResX * ResZ`. Each value is in the range 0.0 to 1.0. The index for position (x, z) is `z * ResX + x`.

---

## Height Range

Heights are stored as floating-point values in the range **-1.0 to 1.0**:

| Height Value | Meaning | World-Space Y (HeightScale = 20) |
|--------------|---------|-----------------------------------|
| `1.0` | Maximum raised terrain | +20 units above terrain origin |
| `0.0` | Initial flat surface (ground level) | At the terrain's transform Y position |
| `-1.0` | Maximum dug terrain | -20 units below terrain origin |

The actual world-space Y position is computed as: **`height × HeightScale`**

This bidirectional range allows creating:
- **Mountains and hills** — positive values (0.0 to 1.0)
- **Valleys and trenches** — negative values (-1.0 to 0.0)
- **Riverbeds and caves** — deep negative values
- **Mixed terrain** — seamless transitions between raised and lowered areas

---

## Terrain Tools

When a Terrain is selected in the Inspector, a toolbar of **10 sculpting and painting tools** appears. Each tool uses a circular brush defined by:

| Brush Parameter | Description                              | Range |
|-----------------|------------------------------------------|-------|
| **Radius**      | Brush size in world units                | > 0 |
| **Strength**    | How powerfully the brush applies per stroke | 0-1 |
| **Falloff**     | Soft edge blending (0 = sharp edge, 1 = smooth falloff) | 0-1 |

**Left-click** applies the tool. **Right-click** applies the inverse (where applicable).

All brush strokes are **auto-saved** to the `.terrain.json` file when the mouse button is released, ensuring terrain data is never lost.

---

### Tool 0: Raise / Lower
Raises or lowers terrain height under the brush using smooth falloff.

| Input | Action |
|-------|--------|
| Left-click | Raise terrain (increase height values) |
| Right-click | Lower terrain (decrease height values, can go below ground level) |

The brush applies a smooth falloff from center to edge based on the Falloff parameter. Each vertex within the brush radius receives a weighted height change: `strength × falloff_weight × sign × deltaTime`.

### Tool 1: Paint Holes
Cuts holes in the terrain by marking vertices and removing affected triangles.

| Input | Action |
|-------|--------|
| Left-click | Cut holes (remove terrain triangles) |
| Right-click | Fill holes (restore terrain triangles) |

**Use cases:** Cave entrances, gaps, tunnels, paths through terrain.

Holes are stored in a `bool[]` array (`Holes`) with one entry per vertex. When a vertex is marked as a hole, all triangles referencing that vertex are excluded from the mesh. The logical terrain extent is maintained for brush interaction — brushes can still operate over holes.

### Tool 2: Noise
Applies Perlin noise displacement to the terrain surface.

| Input | Action |
|-------|--------|
| Left-click | Add noise (random hills and bumps) |
| Right-click | Subtract noise |

Noise frequency is derived from the brush radius — smaller brushes produce finer detail, larger brushes produce broader features. Good for adding natural-looking surface variation.

### Tool 3: Stitch / Blend
Blends terrain height toward the average of the brush area.

| Input | Action |
|-------|--------|
| Left-click | Blend heights together toward the area average |

Smooths out sharp transitions between different elevation areas. Useful for connecting terrain patches or softening hard edges between manually sculpted regions.

### Tool 4: Sculpt
Pulls the terrain toward or away from the camera direction.

| Input | Action |
|-------|--------|
| Left-click | Push terrain along camera view direction |
| Right-click | Pull terrain toward camera |

Uses a sharper center influence and gentler edges compared to Raise/Lower. Good for freeform artistic sculpting where you want to push/pull terrain in the direction you're looking.

### Tool 5: Flatten
Levels terrain to a target height captured on first click.

| Input | Action |
|-------|--------|
| Left-click | Captures target height from click point, then flattens to that height |

The target height is captured from the terrain height at the initial click position. As you drag, all vertices within the brush are pulled toward that target height. Useful for creating flat platforms, roads, or building foundations.

### Tool 6: Erode
Simulates basic hydraulic erosion by moving material downhill.

| Input | Action |
|-------|--------|
| Left-click | Apply erosion effect |

For each vertex in the brush area, finds the steepest descent neighbor and transfers height from the current vertex to the lower neighbor. Multiple passes intensify the effect, creating natural-looking gullies and ridges.

### Tool 7: Paint Layers
Paints terrain texture layers using the splatmap system.

| Input | Action |
|-------|--------|
| Left-click | Paint the active layer onto the terrain |
| Right-click | Erase the active layer (restores base layer 0 weight) |

Weights are automatically normalized — all layers at each vertex sum to 1.0. Select the active paint layer in the Terrain Layers UI section of the Inspector.

### Tool 8: Smooth
Averages neighboring vertex heights to smooth bumpy terrain.

| Input | Action |
|-------|--------|
| Left-click | Smooth terrain heights |

Uses a **3x3 Gaussian-weighted kernel** for high-quality smoothing. Each vertex's height is replaced with the weighted average of its neighbors. Multiple passes create progressively smoother results.

### Tool 9: Paint Trees
Scatters tree objects on the terrain surface.

| Input | Action |
|-------|--------|
| Left-click | Place trees randomly within brush radius |
| Right-click | Remove trees within brush radius |

Trees are placed at the correct terrain height using `SampleHeightWorld()` and respect holes (no trees placed over holes). See the Tree Painting section below for full configuration details.

---

## Splatmap Painting (Multi-Material Terrain)

The terrain supports up to **8 texture layers**, blended per-vertex using two RGBA splatmap arrays.

### How Splatmaps Work
```
Splatmap0 (RGBA float[]) = weights for layers 0, 1, 2, 3
Splatmap1 (RGBA float[]) = weights for layers 4, 5, 6, 7
```

Each vertex stores a weight (0.0 to 1.0) for each layer. The array layout is: `[R0, G0, B0, A0, R1, G1, B1, A1, ...]` where each group of 4 floats represents the RGBA weights for one vertex.

**Painting behavior:**
- When painting, the active layer's weight is increased by `strength × falloff_weight`
- All other layer weights are proportionally decreased so the total remains 1.0
- When erasing (right-click), weight shifts back toward layer 0 (the base layer)
- The `SplatmapVersion` counter is incremented on every paint operation
- `SplatmapDirty` flag triggers GPU re-upload on the next frame

### TerrainLayer Properties
Each terrain layer has:

| Property       | Type     | Default | Description                          |
|----------------|----------|---------|--------------------------------------|
| `TexturePath`  | `string` | `""`    | Albedo image file path               |
| `Tiling`       | `float`  | `10`    | UV repetition scale                  |
| `NormalMapPath` | `string`| `""`    | Normal map path (reserved for future)|
| `Roughness`    | `float`  | `0.8`   | Surface roughness (reserved)         |
| `Metallic`     | `float`  | `0`     | Metallic value (reserved)            |

### Terrain Layers UI (Inspector)
When a Terrain is selected, a "Terrain Layers" section appears below the brush tools:

1. **Layer List** — shows each layer with:
   - Layer index number (click to select as the active paint layer)
   - Texture thumbnail / "..." button to browse for a texture file
   - Tiling slider for texture repetition
   - "X" button to remove the layer
2. **+ Add Layer** — adds a new layer (up to 8 maximum)
3. The active paint layer is highlighted in the UI
4. Layer changes (add/remove/texture change) auto-save to `.terrain.json`

### GPU Rendering
- Splatmap data is uploaded to the GPU as **RGBA32F** float textures
- Each view (SceneView, GameView) maintains its own GPU splatmap textures
- A `SplatmapVersion` counter ensures both views re-upload independently when data changes
- The Terrain shader samples both splatmaps and blends up to 8 layer textures with per-layer tiling
- **Triplanar projection:** On steep cliff faces (where the surface normal is mostly horizontal), textures are projected from the side to prevent stretching artifacts
- **Fallback:** When no layers are defined, the terrain uses the standard material color from its `MeshRenderer`

---

## Tree Painting

The Paint Trees tool (Tool 9) scatters tree objects on the terrain, supporting both procedural trees and imported 3D model assets.

### Tree Settings (Inspector)
When the Paint Trees tool is active, additional settings appear:

| Setting            | Default | Range  | Description                          |
|--------------------|---------|--------|--------------------------------------|
| **Density**        | 3       | 1-20   | Trees placed per brush stroke        |
| **Min Scale**      | 0.8     | —      | Minimum random scale factor          |
| **Max Scale**      | 1.2     | —      | Maximum random scale factor          |
| **Random Y Rotation** | true | —      | Apply random Y-axis rotation         |

### Tree Asset List
Below the settings, a **Tree Assets** section allows switching between different tree types:

1. **Procedural (default)** — uses the built-in procedural tree generator (trunk + canopy with 3 shape options: Sphere, Cone, LayeredCone)
2. **Imported models** — click **"+ Add Tree Model"** to browse for a 3D model file (`.obj`, `.fbx`, `.gltf`, `.glb`, `.dae`)
3. Click any entry to select it as the active tree type for painting
4. Click **"X"** to remove an imported tree asset

### How Tree Painting Works
1. On left-click, `density` random points are generated within the brush circle
2. Each point is projected onto the terrain surface using `SampleHeightWorld()` to find the correct Y height
3. Points over terrain holes are skipped
4. A `Tree` component is created with the selected model path (or procedural parameters)
5. Random scale (between Min Scale and Max Scale) and optional Y rotation are applied
6. The tree GameObject is added as a **child of the terrain** in the hierarchy
7. `TreeLOD` is auto-attached for distance-based LOD management

### Tree Removal
Right-click with the Paint Trees tool erases trees:
1. All child GameObjects with a `Tree` component within the brush radius are found
2. Matching trees are removed from the scene hierarchy

---

## Chunking

Large terrains are split into rectangular chunks for performance. Chunking enables partial rebuilds, frustum culling per chunk, and per-chunk LOD selection.

### How It Works
1. The terrain is divided into a grid of `ChunksX × ChunksZ` chunks, each `ChunkSize × ChunkSize` vertices
2. Each chunk becomes a **child GameObject** with its own `MeshFilter` and `MeshRenderer`
3. The parent terrain's material is shared across all chunks
4. The parent terrain's `MeshRenderer` is disabled (chunks handle rendering)
5. The parent terrain's `MeshFilter` retains the full-resolution mesh for raycasting and collision

### Benefits
| Benefit | Description |
|---------|-------------|
| **Partial rebuild** | When a brush edits a small area, only affected chunks are rebuilt |
| **Frustum culling** | Chunks outside the camera view are not rendered (per-chunk bounding sphere test) |
| **LOD per chunk** | Each chunk can independently use a different detail level based on camera distance |
| **Batched rendering** | Chunks from the same terrain share splatmap bindings (bound once per terrain, not per chunk) |

### Dirty Tracking
When a brush edits terrain, `MarkChunksDirty(minVx, minVz, maxVx, maxVz)` determines which chunks overlap the edited area and marks only those as dirty. `RebuildDirtyChunks()` then regenerates only the dirty chunk meshes, avoiding a full terrain rebuild.

### Chunk Lifecycle
- **Scene load:** Stale `Chunk_*` child GameObjects are cleaned up before rebuilding fresh chunks
- **Safety:** Chunks use actual array dimensions (not cached values) to prevent index out-of-range errors
- **Collision:** The full-resolution collision mesh is rebuilt only at the end of each brush stroke via `FinalizeStroke()`, not during continuous painting

---

## Level of Detail (LOD)

Each chunk maintains multiple mesh resolutions that trade geometric detail for rendering performance.

| LOD Level | Vertex Step | Detail              | Usage |
|-----------|-------------|---------------------|-------|
| 0         | 1 (full)    | Every vertex        | Near the camera |
| 1         | 2 (half)    | Every other vertex  | Medium distance |
| 2         | 4 (quarter) | Every fourth vertex | Far from camera |

### LOD Selection
`UpdateLOD(cameraPos)` is called each frame for each terrain. For each chunk, the distance from the camera to the chunk center determines which LOD level to use:

| Distance | LOD Level | Threshold |
|----------|-----------|-----------|
| Near     | LOD 0 (full detail) | < `chunkWorldSize × 4` |
| Medium   | LOD 1 (half detail) | < `chunkWorldSize × 10` |
| Far      | LOD 2 (quarter detail) | >= `chunkWorldSize × 10` |

The threshold distances scale proportionally with chunk size and terrain dimensions, so larger terrains have proportionally larger LOD bands.

---

## Persistence

Terrain data is saved in two locations for reliability:

### 1. Scene File (`.scene`)
The `[Persist]`-marked properties on the Terrain component are serialized with the scene, including `TerrainAssetPath`, `ResX`, `ResZ`, `Heights`, `Layers`, `Splatmap0`, `Splatmap1`, etc.

### 2. Terrain Asset File (`.terrain.json`)
The full terrain data is also saved to a separate JSON file:

```json
{
  "ResX": 129,
  "ResZ": 129,
  "SizeX": 100.0,
  "SizeZ": 100.0,
  "HeightScale": 20.0,
  "Heights": [0.0, 0.1, -0.2, ...],
  "Holes": [false, false, true, ...],
  "Layers": [
    {
      "TexturePath": "Assets/grass.png",
      "Tiling": 10.0,
      "NormalMapPath": "",
      "Roughness": 0.8,
      "Metallic": 0.0
    }
  ],
  "Splatmap0": [1.0, 0.0, 0.0, 0.0, ...],
  "Splatmap1": [0.0, 0.0, 0.0, 0.0, ...]
}
```

### Auto-Save Behavior
The `.terrain.json` file is automatically saved:
- After each brush stroke (when the mouse button is released)
- When terrain layers are added or removed in the Inspector
- When layer texture paths are changed
- When `AutoSaveOnChange` is enabled, on every modification

### Load Priority
When a terrain loads, data sources are prioritized:
1. If a `.terrain.json` file exists at `TerrainAssetPath`, heights and dimensions are loaded from it
2. Layers and splatmaps are loaded from the `.terrain.json` file if present; otherwise, scene-deserialized data is preserved
3. If no `.terrain.json` exists, a flat terrain is created and saved

### Lifecycle Events
| Event | Action |
|-------|--------|
| `OnEnable()` | Validates dimensions, ensures arrays/path, creates or loads `.terrain.json`, rebuilds mesh |
| `PostDeserialize()` | Reloads from `.terrain.json` if it exists, rebuilds chunks and mesh |
| `Awake()` | Ensures valid dimensions, arrays, and asset path (runtime fallback) |

---

## O(1) Heightmap Collision

The terrain provides an efficient height query method that replaces brute-force ray-triangle intersection.

### `SampleHeightWorld(float worldX, float worldZ, out float worldY, out Vector3 normal)`

Returns the terrain height and approximate surface normal at any world-space XZ position using **bilinear interpolation** of the heightmap grid.

### Algorithm
1. Transform world position to terrain local space (subtract terrain Transform position)
2. Convert to heightmap UV coordinates (0-1 range: `localX / SizeX`, `localZ / SizeZ`)
3. Convert UV to grid coordinates and find the four surrounding height samples
4. **Bilinear interpolate** the height value from the four samples
5. Compute an approximate surface normal from height differences in X and Z
6. Transform the height and normal back to world space

### Performance Impact
| Terrain Size | Triangle Count | Brute-Force Cost | Heightmap Lookup Cost |
|--------------|----------------|-------------------|-----------------------|
| 129 × 129 | ~32,000 | ~32K triangle tests/frame | 1 array lookup + interpolation |
| 257 × 257 | ~131,000 | ~131K triangle tests/frame | 1 array lookup + interpolation |

This reduces per-frame collision from O(n) triangle tests to **O(1)** constant-time lookups. The `CharacterController` uses this for ground detection on terrain surfaces, bringing frame times from 500ms+ down to <16ms on large terrains.

### Limitations
- Does not account for terrain holes in collision (height is interpolated even over holes)
- Returns an approximate surface normal (finite-difference gradient, not exact triangle normal)
- Only works within the terrain's XZ bounds (returns false for out-of-bounds queries)

---

## Brush Interaction

### How Brushes Find the Terrain
1. Mouse position is unprojected into a 3D world-space ray from the camera
2. The ray is tested against terrain mesh triangles using **Moller-Trumbore** ray-triangle intersection
3. If no triangle hit (e.g., mouse is over a hole), falls back to a ray-plane intersection at Y=0 (the default flat terrain height)
4. The world-space hit point determines which vertices are within the brush radius
5. Each vertex within radius receives a weight based on its distance from the center and the falloff curve

### BrushParams
All tools use a shared `BrushParams` struct containing:
| Field | Description |
|-------|-------------|
| Hit position | World-space intersection point in terrain local coordinates |
| Min/Max vertex indices | Bounding rectangle of affected vertices in grid coordinates |
| Radius | Brush radius in world units |
| Strength | Brush intensity (0-1) |
| Falloff | Edge softness (0-1) |
| Sign | +1 for left-click, -1 for right-click |

### Stroke Lifecycle
1. **Mouse down:** Tool is activated, first brush application occurs at the hit point
2. **Mouse move:** Continued brush applications as the mouse drags across the terrain
3. **Mouse up:** Stroke is finalized:
   - `FinalizeStroke()` rebuilds the full-resolution collision mesh (expensive operation, done once per stroke instead of per-frame)
   - `Save()` writes the terrain data to `.terrain.json`
   - `SceneService.NotifyChanged()` refreshes all views

### Partial Rebuild
After applying a brush, `RebuildArea(minVx, minVz, maxVx, maxVz)` is called instead of rebuilding the entire terrain. Combined with chunk dirty tracking (`MarkChunksDirty`), this ensures smooth editor performance even on large terrains — only the chunks overlapping the brush area are regenerated. The full collision mesh (`MeshCollider`) is only rebuilt at stroke end via `FinalizeStroke()`.
