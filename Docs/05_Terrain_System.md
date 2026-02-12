# Game Engine — Terrain System

## Overview

The terrain system provides a heightmap-based, editable landscape with multi-material splatmap painting, chunking for performance, and multiple sculpting/painting tools. Terrain data is stored as a `Terrain` component on a GameObject and is persisted to `.terrain.json` files.

---

## Creating a Terrain

1. **Right-click** in the Hierarchy panel
2. Select **Terrain**
3. A new GameObject is created with:
   - `Transform` (position, rotation, scale)
   - `Terrain` component (heightmap data and settings)
   - `MeshFilter` (holds generated mesh)
   - `MeshRenderer` (renders the mesh)
   - `MeshCollider` (provides collision)

### Default Settings
| Setting       | Default | Description                    |
|---------------|---------|--------------------------------|
| Resolution X  | 129     | Vertices along X axis          |
| Resolution Z  | 129     | Vertices along Z axis          |
| Size X        | 100     | World-space width              |
| Size Z        | 100     | World-space depth              |
| Height Scale  | 20      | Height multiplier              |
| Chunk Size    | 65      | Vertices per chunk edge        |
| Use Chunking  | true    | Enable chunk-based rendering   |
| LOD Levels    | 2       | Number of detail levels (1-3)  |

---

## Height Range

Heights are stored as floating-point values in the range **-1.0 to 1.0**:

- **0.0** = the initial flat surface (ground level)
- **Positive values** (0.0 to 1.0) = raised terrain above ground level
- **Negative values** (-1.0 to 0.0) = dug terrain below ground level

The actual world-space Y position is computed as `height * HeightScale`. For example, with `HeightScale = 20`:
- Height 1.0 = 20 units above the terrain's transform position
- Height 0.0 = at the terrain's transform position
- Height -1.0 = 20 units below the terrain's transform position

This allows creating valleys, trenches, riverbeds, and other features below the starting surface.

---

## Terrain Tools

When a Terrain is selected in the Inspector, a toolbar of 10 sculpting and painting tools appears. Each tool uses a circular brush defined by:

| Brush Parameter | Description                          |
|-----------------|--------------------------------------|
| **Radius**      | Brush size in world units            |
| **Strength**    | How powerfully the brush applies     |
| **Falloff**     | Soft edge blending (0 = sharp, 1 = soft) |

**Left-click** applies the tool. **Right-click** applies the inverse (where applicable).

All brush strokes are **auto-saved** to the `.terrain.json` file when the mouse button is released, ensuring data is never lost.

---

### Tool 0: Raise / Lower
Raises or lowers terrain height under the brush.
- **Left-click** — Raise terrain
- **Right-click** — Lower terrain (can go below the starting surface)
- Uses smooth falloff from center to edge

### Tool 1: Paint Holes
Cuts holes in the terrain by marking vertices and removing triangles.
- **Left-click** — Cut holes (remove terrain)
- **Right-click** — Fill holes (restore terrain)
- Useful for cave entrances, gaps, or paths through terrain
- Holes affect rendering only; the logical terrain extent is maintained for brush interaction

### Tool 2: Noise
Applies Perlin noise displacement to the terrain surface.
- **Left-click** — Add noise (random hills/bumps)
- **Right-click** — Subtract noise
- Noise frequency is derived from brush radius
- Good for adding natural-looking detail

### Tool 3: Stitch / Blend
Blends terrain height toward the average of the brush area.
- **Left-click** — Blend heights together
- Smooths out sharp transitions between different elevation areas
- Useful for connecting terrain patches

### Tool 4: Sculpt
Pulls the terrain toward or away from the camera direction.
- **Left-click** — Push terrain along camera view
- **Right-click** — Pull terrain toward camera
- Uses a sharper center, gentler edges than Raise/Lower
- Good for freeform artistic sculpting

### Tool 5: Flatten
Levels terrain to a target height captured when clicking.
- **Left-click** — Sets target height, then flattens to it
- Target height is captured from the first click position
- Drag to expand the flat area

### Tool 6: Erode
Simulates basic hydraulic erosion by moving material downhill.
- **Left-click** — Apply erosion effect
- Finds the steepest descent neighbor and transfers height
- Multiple passes increase the effect

### Tool 7: Paint Layers
Paints terrain texture layers using the splatmap system.
- **Left-click** — Paint the active layer onto the terrain
- **Right-click** — Erase the active layer (restores layer 0 base texture)
- Weights are automatically normalized (all layers sum to 1.0)
- Select the active layer in the Terrain Layers UI section

### Tool 8: Smooth
Averages neighboring vertex heights to smooth bumpy terrain.
- **Left-click** — Smooth terrain
- Uses a 3x3 Gaussian-weighted kernel for high-quality smoothing
- Multiple passes create smoother results

### Tool 9: Paint Trees
Scatters tree objects on the terrain surface.
- **Left-click** — Place trees randomly within brush radius
- **Right-click** — Remove trees within brush radius
- Trees are placed at the correct terrain height and respect holes
- See the Tree Painting section below for full details

---

## Splatmap Painting (Multi-Material Terrain)

The terrain supports up to **8 texture layers**, blended per-vertex using two RGBA splatmap arrays.

### How Splatmaps Work
```
Splatmap0 (RGBA) = weights for layers 0, 1, 2, 3
Splatmap1 (RGBA) = weights for layers 4, 5, 6, 7
```

Each vertex stores a weight (0.0-1.0) for each layer. When painting, the active layer's weight is increased and all other weights are proportionally decreased so the total remains 1.0. When erasing (right-click), the weight shifts back toward layer 0.

### Layer Properties
Each terrain layer has:
| Property       | Description                          |
|----------------|--------------------------------------|
| **Texture**    | Albedo image file path               |
| **Tiling**     | UV repetition scale (default 10)     |
| **Normal Map** | Normal map path (reserved)           |
| **Roughness**  | Surface roughness (reserved)         |
| **Metallic**   | Metallic value (reserved)            |

### Terrain Layers UI (Inspector)
When a Terrain is selected, a "Terrain Layers" section appears below the brush tools:

1. **Layer List** — Shows each layer with:
   - Layer index number (click to select as active paint layer)
   - Texture thumbnail / "..." button to choose a texture file
   - Tiling slider
   - "X" button to remove the layer
2. **+ Add Layer** — Adds a new layer (up to 8 maximum)
3. The active paint layer is highlighted in the UI

### GPU Rendering
- Splatmap data is uploaded to the GPU as RGBA32F textures
- Each view (SceneView, GameView) maintains its own GPU splatmap textures
- A version counter ensures both views re-upload independently when data changes
- The terrain shader samples both splatmaps and blends up to 8 layer textures
- When no layers are defined, the terrain falls back to the standard material

---

## Tree Painting

The Paint Trees tool (Tool 9) scatters tree objects on the terrain, supporting both procedural trees and imported 3D model assets.

### Tree Settings (Inspector)
When the Paint Trees tool is active, additional settings appear:

| Setting            | Default | Description                          |
|--------------------|---------|--------------------------------------|
| **Density**        | 3       | Trees placed per brush stroke (1-20) |
| **Min Scale**      | 0.8     | Minimum random scale factor          |
| **Max Scale**      | 1.2     | Maximum random scale factor          |
| **Random Y Rotation** | true | Apply random Y-axis rotation         |

### Tree Asset List
Below the settings, a **Tree Assets** section allows switching between different tree types:

1. **Procedural (default)** — Uses the built-in procedural tree generator (trunk + canopy shapes)
2. **Imported models** — Click **"+ Add Tree Model"** to browse for a 3D model file (`.obj`, `.fbx`, `.gltf`, `.glb`, `.dae`)
3. Click any entry to select it as the active tree type for painting
4. Click **"X"** to remove an imported tree asset

### How Tree Painting Works
1. On left-click, `density` random points are generated within the brush circle
2. Each point is projected onto the terrain surface to find the correct Y height
3. Points over terrain holes are skipped
4. A `Tree` component is created with the selected model path (or procedural if none)
5. Random scale and optional rotation are applied
6. The tree GameObject is added as a child of the terrain

### Tree Removal
Right-click with the Paint Trees tool erases trees:
1. All child GameObjects with a `Tree` component within the brush radius are found
2. Matching trees are removed from the scene

---

## Chunking

Large terrains are split into rectangular chunks for performance.

### How It Works
1. The terrain is divided into a grid of chunks, each `ChunkSize x ChunkSize` vertices
2. Each chunk becomes a child `GameObject` with its own `MeshFilter` and `MeshRenderer`
3. The parent terrain's material is shared across all chunks
4. The parent terrain's `MeshRenderer` is disabled (chunks handle rendering)
5. The parent terrain's `MeshFilter` retains the full-resolution mesh for raycasting

### Benefits
- **Partial rebuild**: When a brush edits a small area, only affected chunks are rebuilt
- **Frustum culling**: Chunks outside the camera view are not rendered
- **LOD per chunk**: Each chunk can use a different detail level

### Dirty Tracking
When a brush edits terrain, `RebuildArea()` marks only the overlapping chunks as dirty. `RebuildDirtyChunks()` then regenerates only those meshes, avoiding a full terrain rebuild.

### Chunk Lifecycle
- On scene load, any stale `Chunk_*` child GameObjects are cleaned up before rebuilding
- Chunks use the actual array dimensions (not cached values) to prevent index out-of-range errors
- The collision mesh is rebuilt at full resolution at the end of each brush stroke via `FinalizeStroke()`

---

## Level of Detail (LOD)

Each chunk maintains multiple mesh resolutions:

| LOD Level | Vertex Step | Detail                    |
|-----------|-------------|---------------------------|
| 0         | 1 (full)    | Every vertex rendered     |
| 1         | 2 (half)    | Every other vertex        |
| 2         | 4 (quarter) | Every fourth vertex       |

### LOD Selection
`UpdateLOD(cameraPos)` is called each frame. For each chunk, the distance from the camera to the chunk center determines which LOD level to use:
- **Near** (< threshold) -> LOD 0 (full detail)
- **Medium** -> LOD 1 (half detail)
- **Far** (> threshold) -> LOD 2 (quarter detail)

The threshold distances scale with chunk size and terrain dimensions.

---

## Persistence

Terrain data is saved in two locations:

### 1. Scene File (`.scene`)
The `[Persist]`-marked properties on the Terrain component are serialized with the scene, including `TerrainAssetPath`, `ResX`, `ResZ`, `Heights`, `Layers`, `Splatmap0`, `Splatmap1`, etc.

### 2. Terrain Asset File (`.terrain.json`)
The full terrain data is also saved to a separate JSON file for reliable persistence:

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
      "Tiling": 10.0
    }
  ],
  "Splatmap0": [1.0, 0.0, 0.0, 0.0, ...],
  "Splatmap1": [0.0, 0.0, 0.0, 0.0, ...]
}
```

### Auto-Save Behavior
The `.terrain.json` file is automatically saved:
- After each brush stroke (when the mouse is released)
- When terrain layers are added or removed in the Inspector
- When layer texture paths are changed
- When `AutoSaveOnChange` is enabled, on every modification

### Load Priority
When a terrain loads, data sources are prioritized:
1. If a `.terrain.json` file exists, heights and dimensions are loaded from it
2. Layers and splatmaps are loaded from the `.terrain.json` file if present; otherwise, scene-deserialized data is preserved
3. If no `.terrain.json` exists, a flat terrain is created and saved

---

## O(1) Heightmap Collision

The terrain provides an efficient height query method `SampleHeightWorld(worldX, worldZ)` that returns the terrain height and surface normal at any world-space XZ position using bilinear interpolation of the heightmap grid.

### How It Works
1. Transform world position to terrain local space
2. Convert to heightmap UV coordinates (0-1)
3. Look up the four surrounding height samples
4. Bilinear interpolate the height value
5. Compute an approximate surface normal from height differences
6. Transform back to world space

### Performance
This replaces brute-force ray-triangle intersection against 131K+ triangles (for a 257x257 terrain) with a simple array lookup + interpolation. The `CharacterController` uses this for ground detection on terrain surfaces, avoiding the massive performance cost of `MeshCollider` raycasting on terrain meshes.

---

## Brush Interaction

### How Brushes Find the Terrain
1. Mouse position is unprojected into a 3D ray
2. Ray is tested against terrain mesh triangles (Moller-Trumbore intersection)
3. If no triangle hit (e.g., over a hole), falls back to ray-plane intersection at Y=0 (the default flat height)
4. The world-space hit point determines which vertices are within brush radius
5. Each vertex within radius gets a weight based on distance and falloff curve

### BrushParams
All tools use a shared `BrushParams` struct containing:
- Hit position in local terrain space
- Min/Max vertex indices for the affected area
- Radius, strength, falloff values
- Sign (+1 for left-click, -1 for right-click)

### Stroke Lifecycle
1. **Mouse down**: Tool is activated, first brush application occurs
2. **Mouse move**: Continued brush applications as the mouse drags
3. **Mouse up**: Stroke is finalized:
   - `FinalizeStroke()` rebuilds the collision mesh (expensive, done once per stroke)
   - `Save()` writes the terrain data to `.terrain.json`
   - `SceneService.NotifyChanged()` refreshes all views

### Partial Rebuild
After applying a brush, `RebuildArea(minVx, minVz, maxVx, maxVz)` is called instead of rebuilding the entire terrain, ensuring smooth editor performance even on large terrains. The full collision mesh is only rebuilt at stroke end.
