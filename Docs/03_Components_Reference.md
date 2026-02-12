# Game Engine — Components Reference

Every component inherits from `Behavior` and attaches to a `GameObject`. Properties marked `[Persist]` are saved with the scene.

---

## Transform

**Always present** on every GameObject. Cannot be removed.

| Property   | Type      | Description                     |
|------------|-----------|---------------------------------|
| `Position` | `Vector3` | Local position (X, Y, Z)       |
| `Rotation` | `Vector3` | Local Euler rotation (degrees)  |
| `Scale`    | `Vector3` | Local scale (default 1, 1, 1)  |

---

## Camera

Defines a viewpoint for rendering. The Game View uses the first enabled Camera found in the scene.

| Property          | Type    | Description                              |
|-------------------|---------|------------------------------------------|
| `FieldOfView`     | `float` | Vertical FOV in degrees (default 60)     |
| `NearClip`        | `float` | Near clipping plane distance             |
| `FarClip`         | `float` | Far clipping plane distance              |
| `IsOrthographic`  | `bool`  | Orthographic vs perspective projection   |
| `OrthographicSize`| `float` | Half-height of orthographic view         |
| `ViewportX/Y/W/H` | `float`| Normalized viewport rectangle (0-1)      |
| `ClearFlags`      | `enum`  | What to clear before rendering           |
| `Depth`           | `int`   | Render order (higher = later)            |

---

## Light

Illuminates the scene. Supports directional and point light types.

| Property      | Type    | Description                            |
|---------------|---------|----------------------------------------|
| `LightType`   | `enum`  | `Directional` or `Point`               |
| `Intensity`   | `float` | Light brightness multiplier            |
| `Range`       | `float` | Point light range (world units)        |
| `Color`       | `Color` | Light color tint                       |

**Directional lights** use the GameObject's Transform forward direction. They cast shadows via a 4096x4096 shadow map with PCF soft shadows.

---

## MeshFilter

Holds a reference to mesh geometry data.

| Property       | Type     | Description                          |
|----------------|----------|--------------------------------------|
| `Mesh`         | `Mesh`   | Runtime mesh data (vertices, normals, UVs, triangles) |
| `ModelPath`    | `string` | Path to the source 3D model file     |
| `ModelPartIndex`| `int`   | Which part of a multi-mesh model     |

---

## MeshRenderer

Controls how a mesh is rendered.

| Property        | Type       | Description                         |
|-----------------|------------|-------------------------------------|
| `Color`         | `Color`    | Tint color (multiplied with material) |
| `Material`      | `Material` | Surface material (textures, PBR)    |
| `MaterialPath`  | `string`   | Path to `.material` asset file      |
| `Wireframe`     | `bool`     | Render as wireframe only            |
| `CastShadows`   | `bool`     | Whether this mesh casts shadows     |
| `ReceiveShadows`| `bool`     | Whether this mesh receives shadows  |
| `DoubleSided`   | `bool`     | Disable backface culling            |
| `InvertFrontFace`| `bool`    | Swap front/back face winding        |

---

## Material

Not a component but defines surface properties for MeshRenderers.

| Property       | Type       | Description                          |
|----------------|------------|--------------------------------------|
| `BaseColor`    | `Color`    | Base albedo color (RGBA)             |
| `Metallic`     | `float`    | Metallic value (0 = dielectric, 1 = metal) |
| `Roughness`    | `float`    | Surface roughness (0 = mirror, 1 = rough) |
| `Transparent`  | `bool`     | Enable alpha blending                |
| `AlphaCutoff`  | `float`    | Alpha test threshold                 |
| `Textures`     | `list`     | Texture slots (Albedo, Normal, etc.) |

### Texture Slots
Each slot has:
- **Texture** — the loaded image data
- **Usage** — Albedo, Normal, Metallic, Roughness, Occlusion, Emission, Opacity, Height
- **SourcePath** — project-relative path to the image file

---

## Colliders

### BoxCollider
Axis-aligned box collision shape.

| Property  | Type      | Description              |
|-----------|-----------|--------------------------|
| `Center`  | `Vector3` | Offset from transform    |
| `Size`    | `Vector3` | Box dimensions           |

### CapsuleCollider
Capsule collision shape for character controllers.

| Property    | Type    | Description              |
|-------------|---------|--------------------------|
| `Center`    | `Vector3` | Offset from transform |
| `Radius`    | `float` | Capsule radius           |
| `Height`    | `float` | Total capsule height     |
| `Direction` | `int`   | Axis (0=X, 1=Y, 2=Z)   |

### MeshCollider
Uses mesh geometry for precise collision detection.

| Property     | Type     | Description                      |
|--------------|----------|----------------------------------|
| `Mesh`       | `Mesh`   | Collision mesh data              |
| `TargetPath` | `string` | Path to source MeshFilter        |

---

## CharacterController

Physics-based character movement with gravity, stepping, and collision response.

| Property      | Type    | Description                        |
|---------------|---------|------------------------------------|
| `Height`      | `float` | Controller capsule height          |
| `Radius`      | `float` | Controller capsule radius          |
| `StepOffset`  | `float` | Max step height to climb           |
| `SlopeLimit`  | `float` | Max walkable slope angle           |
| `SkinWidth`   | `float` | Collision skin thickness           |
| `IsGrounded`  | `bool`  | Whether touching ground (read-only)|

**Requires**: CapsuleCollider

---

## PlayerMovement

First-person player controller with mouse look and WASD movement.

| Property       | Type    | Description                        |
|----------------|---------|------------------------------------|
| `MoveSpeed`    | `float` | Walking speed (units/sec)          |
| `SprintSpeed`  | `float` | Sprinting speed (units/sec)        |
| `JumpForce`    | `float` | Jump velocity                      |
| `MouseSensitivity` | `float` | Mouse look sensitivity         |
| `CameraOffset` | `Vector3` | Camera position offset from body |

**Requires**: CharacterController, CapsuleCollider

### Controls (default bindings)
- **WASD** — Move
- **Mouse** — Look around
- **Space** — Jump
- **Shift** — Sprint
- **Mouse 1** — Fire

---

## Skybox

Defines the sky background and ambient lighting.

| Property        | Type    | Description                           |
|-----------------|---------|---------------------------------------|
| `TopColor`      | `Color` | Sky gradient top color                |
| `BottomColor`   | `Color` | Sky gradient bottom color             |
| `TexturePath`   | `string`| Equirectangular sky texture path      |
| `TextureBlend`  | `float` | Blend between gradient and texture (0-1) |
| `Yaw`           | `float` | Sky rotation around Y axis (degrees)  |
| `Ambient`       | `float` | Ambient light level (0-1)             |
| `SunElevation`  | `float` | Sun angle above horizon (degrees)     |

47 built-in skybox textures are included in `Standard Assets/Skybox/`.

---

## Terrain

Heightmap-based terrain with multi-material painting, chunking, and LOD.

| Property       | Type    | Description                            |
|----------------|---------|----------------------------------------|
| `ResX`         | `int`   | Height samples along X (default 129)   |
| `ResZ`         | `int`   | Height samples along Z (default 129)   |
| `SizeX`        | `float` | World width in X (default 100)         |
| `SizeZ`        | `float` | World depth in Z (default 100)         |
| `HeightScale`  | `float` | Height multiplier in Y (default 20)    |
| `Heights`      | `float[]`| Heightmap data (-1 to 1 per sample). Negative values dig below the starting surface. |
| `Holes`        | `bool[]` | Per-vertex hole mask                  |
| `UseChunking`  | `bool`  | Enable chunk-based rendering           |
| `ChunkSize`    | `int`   | Vertices per chunk edge (default 65)   |
| `LodLevels`    | `int`   | LOD levels per chunk (1-3)             |
| `Layers`       | `list`  | Terrain texture layers (up to 8)       |
| `Splatmap0/1`  | `float[]`| Per-vertex layer weights (RGBA x 2)  |
| `TerrainAssetPath` | `string` | Path to the `.terrain.json` data file |
| `AutoLoadOnStart`  | `bool`   | Load terrain data on startup (default true) |
| `AutoSaveOnChange` | `bool`   | Save terrain data automatically on each change |

**Requires**: MeshFilter, MeshRenderer, MeshCollider

### Terrain Data Persistence
Terrain data is stored in two locations:
1. **Scene file** (`.scene`) — serializes `[Persist]` properties via scene serialization
2. **Terrain asset file** (`.terrain.json`) — stores the full heightmap, layers, and splatmaps

Brush strokes are auto-saved to the `.terrain.json` file when the mouse is released after painting.

### O(1) Heightmap Collision
The terrain provides `SampleHeightWorld()` for fast height queries without brute-force ray-triangle tests. The `CharacterController` uses this for efficient ground detection on terrain surfaces.

See the Terrain System document for full details on tools and painting.

---

## Tree

Procedural or imported tree/vegetation component with wind animation support.

| Property            | Type     | Description                              |
|---------------------|----------|------------------------------------------|
| `TrunkHeight`       | `float`  | Procedural trunk height (default 3)      |
| `TrunkRadiusBottom` | `float`  | Bottom radius (default 0.25)             |
| `TrunkRadiusTop`    | `float`  | Top radius (default 0.12)                |
| `TrunkSegments`     | `int`    | Trunk detail level (default 8)           |
| `Shape`             | `enum`   | Canopy shape: Sphere, Cone, LayeredCone  |
| `CanopyRadius`      | `float`  | Canopy width (default 2)                 |
| `CanopyHeight`      | `float`  | Canopy height (default 2.5)              |
| `CanopySegments`    | `int`    | Canopy detail level (default 10)         |
| `CanopyLayers`      | `int`    | Layers for LayeredCone shape (default 3) |
| `ModelPath`         | `string` | Path to imported 3D model (overrides procedural) |
| `Lod1Path`          | `string` | Path to LOD 1 model                     |
| `Lod2Path`          | `string` | Path to LOD 2 model                     |
| `TrunkMaterialPath` | `string` | Material for trunk                       |
| `CanopyMaterialPath`| `string` | Material for canopy                      |
| `WindSway`          | `float`  | Wind animation intensity (0-1)           |
| `WindSpeed`         | `float`  | Wind animation speed multiplier          |
| `IsVegetation`      | `bool`   | Enable wind vertex animation (default true) |

**Requires**: MeshFilter, MeshRenderer, TreeLOD

### Modes
- **Procedural**: Generates trunk + canopy meshes from parameters. Three canopy shapes available: Sphere, Cone, and LayeredCone (stacked overlapping cones).
- **Imported**: When `ModelPath` is set, loads a 3D model file instead of generating procedurally.

### Wind Animation
Trees marked `IsVegetation = true` receive vertex-based wind animation in the shader. Wind direction and strength are controlled by the global `WindSystem`.

---

## TreeLOD

Automatic level-of-detail for tree/vegetation objects based on camera distance.

| Property | Type   | Description                          |
|----------|--------|--------------------------------------|
| `Lod0`   | `Mesh` | Full detail mesh                     |
| `Lod1`   | `Mesh` | Medium detail mesh (half segments)   |
| `Lod2`   | `Mesh` | Low detail mesh (quarter segments)   |

LOD meshes are automatically generated by the `Tree` component at different detail levels. The renderer selects the appropriate LOD based on screen-space size.

---

## Custom Script Components

Any C# class inheriting from `Behavior` in the project's `Assets/` or `Packages/` folders becomes a component that can be added to GameObjects. Mark properties with `[Persist]` to save them with the scene.

```csharp
public class MyComponent : Behavior
{
    [Persist] public float Speed { get; set; } = 5f;
    [Persist] public string Label { get; set; } = "Hello";

    public override void Update()
    {
        // Runs every frame during play mode
        var pos = gameObject.Transform.Position;
        pos.Y += Speed * Time.DeltaTime;
        gameObject.Transform.Position = pos;
    }
}
```
