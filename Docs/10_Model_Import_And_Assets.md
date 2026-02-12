# Game Engine — Model Import and Asset Pipeline

## 3D Model Import

The engine uses **AssimpNet** (the .NET binding for the Open Asset Import Library) to load 3D models from a wide variety of formats.

### Supported Formats
| Format | Extensions          | Notes                          |
|--------|---------------------|--------------------------------|
| FBX    | `.fbx`              | Autodesk FBX (most common)     |
| OBJ    | `.obj`              | Wavefront OBJ (with .mtl)     |
| GLTF   | `.gltf`, `.glb`     | Khronos glTF 2.0              |
| DAE    | `.dae`              | Collada                        |

### How to Import
1. **Right-click** in the Hierarchy Panel
2. Select **Import Model**
3. Choose a 3D model file from the file dialog
4. The model is imported as a GameObject hierarchy

### Import Process

```
Model File (.fbx, .obj, etc.)
    │
    ▼
Assimp Library
    │
    ├─► Parse scene graph (nodes)
    ├─► Triangulate all faces
    ├─► Generate normals (if missing)
    ├─► Extract meshes (vertices, normals, UVs, indices)
    ├─► Extract materials (colors, textures)
    └─► Extract node hierarchy (transforms)
    │
    ▼
ModelImporter
    │
    ├─► Create GameObject per node
    ├─► Create Mesh per Assimp mesh
    ├─► Create Material per Assimp material
    ├─► Assign MeshFilter + MeshRenderer
    ├─► Normalize scale
    └─► Convert texture paths to project-relative
    │
    ▼
Scene Graph (ready to use)
```

### Resulting Hierarchy
A multi-part model creates a tree of GameObjects:
```
ModelName (root)
├── Part_0 (MeshFilter + MeshRenderer + MeshCollider)
│   └── Material: embedded or .material reference
├── Part_1 (MeshFilter + MeshRenderer + MeshCollider)
│   └── Material: embedded or .material reference
└── Part_2 (MeshFilter + MeshRenderer + MeshCollider)
    └── Material: embedded or .material reference
```

Each part's `MeshFilter` stores:
- `ModelPath` — path to the source model file
- `ModelPartIndex` — which mesh from the model (0, 1, 2, ...)
- `Mesh` — the loaded vertex/triangle data

### Material Import
For each material in the model file:
- **Base color** is extracted from the diffuse color
- **Textures** are mapped to the correct slots (Albedo, Normal, Specular, Opacity)
- **Texture paths** are resolved relative to the project root
- **Transparency** is detected from opacity maps or alpha channels

### Scale Normalization
Models are automatically scaled to fit a reasonable size in the scene. The importer normalizes the bounding box so that the largest dimension fits within a standard range.

---

## Built-In Primitive Meshes

The engine can create procedural meshes without importing any files:

| Primitive    | Method               | Parameters                    |
|--------------|----------------------|-------------------------------|
| **Cube**     | `Mesh.CreateCube()`  | Size                          |
| **Sphere**   | `Mesh.CreateUvSphere()` | Radius, segments, rings    |
| **Cylinder** | `Mesh.CreateCylinder()` | Radius, height, segments   |
| **Cone**     | `Mesh.CreateCone()`  | Radius, height, segments      |
| **Quad**     | `Mesh.CreateQuad()`  | Size (single face)            |
| **Plane**    | `Mesh.CreatePlane()` | Size, subdivisions            |

These are accessible from:
- Hierarchy Panel > Right-click > Create (Cube, Sphere, etc.)
- Code: `Mesh.CreateCube()`, etc.

### Mesh Data
Each `Mesh` contains:
- `Vertices` — `Vector3[]` positions
- `Normals` — `Vector3[]` surface normals
- `UVs` — `Vector2[]` texture coordinates
- `TriIndices` — `int[]` triangle index buffer
- `LineIndices` — `int[]` wireframe line index buffer

### Normal Recalculation
`RecalculateNormalsSmooth()` computes area-weighted smooth normals:
1. For each triangle, compute the face normal
2. Weight by triangle area
3. Accumulate weighted normals per vertex
4. Normalize the result

This produces smooth shading across shared vertices.

### Viewport LOD
Procedural meshes support viewport LOD via tessellation hints:
- `MeshKind` identifies the shape type
- `TessA` / `TessB` store tessellation parameters
- `SuggestSphereTesselation()` / `SuggestRadialTessellation()` compute appropriate detail for screen-space size
- The Scene View adjusts tessellation based on object distance

---

## Asset File Types

| Extension     | Type          | Description                         |
|---------------|---------------|-------------------------------------|
| `.scene`      | Scene         | Scene hierarchy and component data  |
| `.material`   | Material      | Material properties and texture refs|
| `.cs`         | Script        | C# source code                      |
| `.fbx`        | Model         | 3D model (FBX format)              |
| `.obj`        | Model         | 3D model (OBJ format)              |
| `.gltf/.glb`  | Model         | 3D model (glTF format)             |
| `.dae`        | Model         | 3D model (Collada format)          |
| `.png`        | Texture       | Image (lossless)                   |
| `.jpg/.jpeg`  | Texture       | Image (lossy)                      |
| `.bmp`        | Texture       | Image (bitmap)                     |
| `.terrain.json` | Terrain     | Terrain heightmap and layer data   |

---

## Project Panel File Operations

| Action                    | How                                     |
|---------------------------|-----------------------------------------|
| **Create script**         | Right-click > New Script                |
| **Create scene**          | Right-click > New Scene                 |
| **Create material**       | Right-click > New Material              |
| **Create folder**         | Right-click > New Folder                |
| **Import files**          | Right-click > Import or drag-and-drop   |
| **Open script**           | Double-click `.cs` file                 |
| **Inspect material**      | Double-click `.material` file           |
| **Load scene**            | Double-click `.scene` file              |
| **Reveal in Explorer**    | Right-click > Show in Explorer          |
| **Refresh**               | Right-click > Refresh                   |

---

## Texture Bridge

`TextureBridge` handles conversion between engine `Texture2D` objects and Avalonia `Bitmap` objects for display in the UI (e.g., texture thumbnails in the Inspector). It uses SkiaSharp for image decoding and format conversion.

---

## Image Utilities

`ImageUtil` provides helpers for image processing:
- Loading images from disk
- Converting between color formats
- Resizing and sampling operations

---

## Resource Caching

The `ResourceCache` system prevents duplicate GPU uploads:
1. When a `Mesh` or `Texture2D` is first needed for rendering, it's uploaded to the GPU
2. The GPU handle (`GPUMesh` or `GPUTexture`) is cached by object reference
3. Subsequent renders reuse the cached GPU resource
4. When a mesh is modified (e.g., terrain editing), `MarkMeshDirty()` triggers re-upload
5. A fallback 1x1 white texture is used for missing textures
