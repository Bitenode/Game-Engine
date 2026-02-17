# Game Engine — Model Import and Asset Pipeline

## 3D Model Import

The engine uses **AssimpNet** (the .NET binding for the Open Asset Import Library) to load 3D models from a wide variety of formats, including support for skeletal meshes, bone animations, and multi-material objects.

### Supported Formats
| Format | Extensions        | Notes                                |
|--------|-------------------|--------------------------------------|
| FBX    | `.fbx`            | Autodesk FBX (most common, best skeletal support) |
| OBJ    | `.obj`            | Wavefront OBJ (with `.mtl` material file)        |
| glTF   | `.gltf`, `.glb`   | Khronos glTF 2.0 (modern, PBR-ready)             |
| DAE    | `.dae`            | Collada (exchange format)                          |

AssimpNet supports additional formats beyond these — any format that the Assimp library can read will work.

### How to Import
1. **Right-click** in the Hierarchy Panel
2. Select **Import Model**
3. Choose a 3D model file from the file dialog
4. The model is imported as a GameObject hierarchy with meshes, materials, and optionally skeleton/animations

---

## Import Process

```
Model File (.fbx, .obj, .gltf, .glb, .dae)
    │
    ▼
AssimpNet Library (Post-Processing)
    │
    ├─► Triangulate all faces (quads → triangles)
    ├─► Join identical vertices (deduplication)
    ├─► Generate smooth normals (if missing)
    ├─► Improve cache locality (vertex reordering)
    ├─► Remove redundant materials
    ├─► Limit bone weights (max 4 per vertex)
    │
    ▼
ModelImporter
    │
    ├─► Extract meshes (vertices, normals, UVs, indices)
    ├─► Extract materials (colors, textures, PBR values)
    ├─► Build skeleton (bone hierarchy from node tree)
    ├─► Extract bone animations (save as .boneanim files)
    ├─► Create GameObject per node
    ├─► Assign MeshFilter + MeshRenderer + MeshCollider per mesh
    ├─► Create SkinnedMeshRenderer for skeletal meshes
    ├─► Create Animator with imported animation states
    ├─► Normalize scale (fit to ~1 unit radius)
    ├─► Resolve and save material files
    └─► Convert texture paths to project-relative
    │
    ▼
Scene Graph (ready to use)
```

### Assimp Post-Processing Flags
| Flag | Purpose |
|------|---------|
| `Triangulate` | Convert all polygon faces to triangles |
| `JoinIdenticalVertices` | Merge duplicate vertices |
| `GenerateSmoothNormals` | Compute smooth normals if none exist |
| `ImproveCacheLocality` | Reorder vertices for GPU cache efficiency |
| `RemoveRedundantMaterials` | Eliminate unused materials |
| `LimitBoneWeights` | Clamp to 4 bones per vertex (GPU skinning limit) |

---

## Resulting Hierarchy

### Static Model (No Skeleton)
A multi-part model creates a tree of GameObjects:
```
ModelName (root — Transform only)
├── Part_0 (MeshFilter + MeshRenderer + MeshCollider)
│   └── Material: resolved from model or saved as .material file
├── Part_1 (MeshFilter + MeshRenderer + MeshCollider)
│   └── Material: resolved from model or saved as .material file
└── Part_2 (MeshFilter + MeshRenderer + MeshCollider)
    └── Material: resolved from model or saved as .material file
```

### Skeletal Model (With Bones)
Models with skeletons create a flattened hierarchy to avoid double-transformation:
```
ModelName (root — Transform only)
├── SkinnedMesh_0 (MeshFilter + SkinnedMeshRenderer)
│   └── Bone data embedded in Mesh
├── Animator (animation state machine)
└── (Bone hierarchy is stored internally, not as GameObjects)
```

**Flattening:** Skinned meshes are moved to the root level to prevent the parent transform from being applied twice (once by the hierarchy and once by the bone matrices).

### Per-Part Data
Each mesh part's `MeshFilter` stores:
| Property | Description |
|----------|-------------|
| `ModelPath` | Project-relative path to the source model file |
| `ModelPartIndex` | Which mesh from the model (0, 1, 2, ...) |
| `Mesh` | The loaded vertex/triangle data |

---

## Material Import

For each material in the model file, the importer extracts and converts properties:

### Property Extraction
| Source (Assimp) | Target (Engine) | Notes |
|-----------------|-----------------|-------|
| Diffuse color | `BaseColor` | Near-black colors overridden to white (common FBX issue) |
| Opacity/transparency | `Transparent` | Detected from opacity maps or alpha channels |
| Diffuse texture | `Albedo` slot | Primary color texture |
| Normal map | `Normal` slot | Bump/normal map |
| Specular map | `Specular` slot | Specular highlights |
| Opacity map | `Opacity` slot | Per-pixel transparency |

### Texture Resolution
The importer tries multiple paths to find texture files:

1. **Relative to model** — path as stored in the model file
2. **Filename search** — search the project for a file with the same name
3. **Assets folder** — search in the project's `Assets/` directory
4. **Embedded textures** — extract from the model file and save to disk as separate images

### Filename Pattern Guessing
When Assimp metadata doesn't specify texture usage, the importer guesses from filename patterns:

| Pattern | Assigned Usage |
|---------|---------------|
| `_col`, `_color`, `_albedo`, `_diffuse`, `_base` | Albedo |
| `_nrm`, `_normal`, `_norm`, `_bump` | Normal |
| `_rough`, `_roughness` | Roughness |
| `_met`, `_metallic`, `_metal` | Metallic |
| `_ao`, `_occlusion`, `_ambient` | Occlusion |
| `_emissive`, `_emission`, `_glow` | Emission |
| `_spec`, `_specular` | Specular |
| `_opacity`, `_alpha`, `_trans` | Opacity |

### Material Saving
Imported materials are saved as `.material` files next to the model in the project, enabling re-editing through the Inspector.

---

## Skeletal Mesh Import

### Skeleton Building
When a model contains bones, the importer builds a skeleton:

1. **Node tree traversal** — Assimp provides a node hierarchy representing the skeleton
2. **Bone identification** — nodes referenced by mesh bone data are marked as bones
3. **Intermediate nodes** — non-bone nodes between bone nodes are preserved in the hierarchy
4. **Offset matrices** — each bone stores an inverse bind pose matrix for skinning

### Bone Weight Handling
| Aspect | Detail |
|--------|--------|
| **Max bones per vertex** | 4 (limited by GPU skinning layout) |
| **Weight normalization** | Weights are normalized so they sum to 1.0 |
| **Translation scaling** | Bone offset matrix translations are scaled when mesh vertices are normalized |
| **Vertex layout** | Skinned: 64 bytes (Position3 + Normal3 + UV2 + BoneIdx4 + BoneWeight4) |

### Flexible Bone Name Matching
The importer handles cross-format bone naming inconsistencies:
- Strips common prefixes (e.g., `mixamorig:`, `Armature|`)
- Case-insensitive matching
- Handles naming differences between FBX, glTF, and Collada exports

---

## Bone Animation Import

### Animation Extraction
When a model contains animations, each animation clip is extracted:

1. **Clip discovery** — Assimp provides named animation clips with duration and keyframes
2. **Channel extraction** — each bone's position, rotation, and scale keyframes are extracted
3. **File saving** — clips are saved as `.boneanim` files alongside the model
4. **Animator creation** — an `Animator` component is auto-created with states for each clip

### .boneanim Format
Each animation clip is stored as a separate file containing:
- Animation name and duration
- Per-bone keyframe data (position, rotation, scale at specific timestamps)
- Ticks per second (for time conversion)

### Animator Auto-Setup
When animations are detected during import:
1. An `Animator` component is added to the root GameObject
2. Each imported animation becomes an animation state
3. The first animation is set as the default state
4. Transitions between states can be configured in the Animation panel

---

## Scale Normalization

Models are automatically scaled to fit a reasonable size in the scene:

1. The importer computes the bounding box of all vertices across all meshes
2. The largest dimension (width, height, or depth) is determined
3. A scale factor is computed to normalize the largest dimension to ~1 unit
4. All vertices are multiplied by this scale factor
5. Bone offset matrix translations are also scaled to match

This ensures that models from different sources (which may use different unit systems — meters, centimeters, inches) appear at a consistent size in the scene.

---

## Built-In Primitive Meshes

The engine can create procedural meshes without importing any files:

| Primitive    | Method                    | Parameters                     |
|--------------|---------------------------|--------------------------------|
| **Cube**     | `Mesh.CreateCube()`       | Size                           |
| **Sphere**   | `Mesh.CreateUvSphere()`   | Radius, segments, rings        |
| **Cylinder** | `Mesh.CreateCylinder()`   | Radius, height, segments       |
| **Cone**     | `Mesh.CreateCone()`       | Radius, height, segments       |
| **Quad**     | `Mesh.CreateQuad()`       | Size (single face)             |
| **Plane**    | `Mesh.CreatePlane()`      | Size, subdivisions             |

### Creating Primitives
**In the Editor:**
- Hierarchy Panel > Right-click > Create > (Cube, Sphere, Cylinder, Cone, Quad, Plane)

**In Code:**
```csharp
var mesh = Mesh.CreateUvSphere(1.0f, 32, 16);  // radius, segments, rings
var mesh = Mesh.CreateCylinder(0.5f, 2.0f, 16); // radius, height, segments
var mesh = Mesh.CreateCone(1.0f, 2.0f, 16);     // radius, height, segments
```

### Mesh Data Structure
Each `Mesh` contains:

| Field | Type | Description |
|-------|------|-------------|
| `Vertices` | `Vector3[]` | Vertex positions |
| `Normals` | `Vector3[]` | Surface normals (per-vertex) |
| `UVs` | `Vector2[]` | Texture coordinates |
| `TriIndices` | `int[]` | Triangle index buffer (groups of 3) |
| `LineIndices` | `int[]` | Wireframe line index buffer (groups of 2) |
| `BoneIndices` | `int[]` | Bone indices per vertex (skinned meshes, 4 per vertex) |
| `BoneWeights` | `float[]` | Bone weights per vertex (skinned meshes, 4 per vertex) |
| `MeshKind` | `enum` | Shape type identifier (for LOD) |
| `TessA` / `TessB` | `int` | Tessellation parameters (for LOD) |

### Normal Recalculation
`RecalculateNormalsSmooth()` computes area-weighted smooth normals:
1. For each triangle, compute the **face normal** (cross product of edges)
2. Weight the normal by the **triangle area** (larger triangles contribute more)
3. Accumulate weighted normals per vertex (shared vertices average their face normals)
4. Normalize the result to unit length

This produces smooth shading across shared vertices, avoiding hard edges.

### Viewport LOD for Primitives
Procedural meshes support dynamic tessellation based on screen-space size:

| Mesh Kind | LOD Method | Behavior |
|-----------|------------|----------|
| Sphere | `SuggestSphereTesselation()` | Returns (longitude, latitude) segment counts based on projected radius |
| Cylinder | `SuggestRadialTessellation()` | Returns side count based on projected radius |
| Cone | `SuggestRadialTessellation()` | Returns side count based on projected radius |

**How it works:**
1. `Projection.EstimateProjectedRadiusPx()` computes the on-screen pixel radius of the mesh
2. Based on the pixel size, appropriate tessellation parameters are chosen
3. If the current mesh has fewer segments than needed, `MeshFilter.Mesh` is replaced with a higher-detail version
4. Default projection surface: 1920 × 1080 pixels

---

## Asset File Types

| Extension        | Type         | Description                          |
|------------------|--------------|--------------------------------------|
| `.scene`         | Scene        | Scene hierarchy and component data   |
| `.material`      | Material     | Material properties and texture references |
| `.cs`            | Script       | C# source code (game logic, extensions) |
| `.fbx`           | Model        | 3D model (Autodesk FBX)             |
| `.obj`           | Model        | 3D model (Wavefront OBJ)            |
| `.gltf` / `.glb` | Model       | 3D model (Khronos glTF 2.0)         |
| `.dae`           | Model        | 3D model (Collada)                   |
| `.png`           | Texture      | Image (lossless compression)         |
| `.jpg` / `.jpeg` | Texture      | Image (lossy compression)            |
| `.bmp`           | Texture      | Image (uncompressed bitmap)          |
| `.terrain.json`  | Terrain      | Terrain heightmap, layers, and splatmap data |
| `.boneanim`      | Animation    | Bone animation clip data             |
| `.dll`           | Assembly     | Compiled script assembly (auto-generated) |
| `.shader`        | Shader       | Custom GLSL shader (Cook-Torrance PBR, etc.) |
| `.shadergraph`   | Shader Graph | Visual shader node graph (JSON format) |
| `input.bindings.json` | Config  | Input axis and action bindings            |

---

## Project Panel File Operations

| Action                    | How                                      | Opens With |
|---------------------------|------------------------------------------|------------|
| **Open script**           | Double-click `.cs` file                  | Script Editor |
| **Inspect material**      | Double-click `.material` file            | Inspector |
| **Load scene**            | Double-click `.scene` file               | Editor (replaces current scene) |
| **Create script**         | Right-click > New Script                 | Creates template `.cs` file |
| **Create scene**          | Right-click > New Scene                  | Creates empty `.scene` file |
| **Create material**       | Right-click > New Material               | Creates default `.material` file |
| **Create folder**         | Right-click > New Folder                 | Creates subdirectory |
| **Import files**          | Right-click > Import                     | Opens file dialog |
| **Reveal in Explorer**    | Right-click > Show in Explorer           | OS file manager |
| **Refresh**               | Right-click > Refresh                    | Reloads file tree |

---

## TextureBridge

`TextureBridge` handles conversion between engine `Texture2D` objects and Avalonia `Bitmap` objects for display in the UI. This is used for:
- Texture thumbnails in the Inspector's material editor
- Terrain layer texture previews
- Tree asset preview images

Uses SkiaSharp for image decoding and format conversion between GPU textures and UI bitmaps.

---

## ImageUtil

`ImageUtil` provides helpers for image processing:
- Loading images from disk paths
- Converting between color formats (BGRA ↔ RGBA)
- Resizing and sampling operations
- Image format detection

---

## Resource Caching

The `ResourceCache` system prevents duplicate GPU uploads and manages GPU resource lifetimes:

| Step | Description |
|------|-------------|
| 1. **First render** | When a `Mesh` or `Texture2D` is first needed, it's uploaded to the GPU |
| 2. **Caching** | The GPU handle (`GPUMesh` or `GPUTexture`) is cached by object reference |
| 3. **Reuse** | Subsequent renders reuse the cached GPU resource (zero upload cost) |
| 4. **Dirty tracking** | When a mesh is modified (e.g., terrain editing), `MarkMeshDirty()` triggers re-upload |
| 5. **Fallback** | Missing textures use a 1x1 white fallback texture |
| 6. **Per-context** | Each GL context (SceneView, GameView) has its own cache to avoid cross-context issues |
| 7. **Eviction** | Orphaned resources (no longer referenced) are periodically cleaned up |
