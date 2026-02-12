# Game Engine — Materials and Textures

## Material System

Materials define how surfaces look when rendered. They support physically-based rendering (PBR) properties and multiple texture slots.

---

## Material Properties

| Property       | Type    | Default         | Description                     |
|----------------|---------|-----------------|---------------------------------|
| `BaseColor`    | `Color` | White (1,1,1,1) | Base albedo color (RGBA)        |
| `Metallic`     | `float` | 0.0             | 0 = dielectric, 1 = full metal |
| `Roughness`    | `float` | 0.5             | 0 = mirror smooth, 1 = fully rough |
| `Transparent`  | `bool`  | false           | Enable alpha blending           |
| `AlphaCutoff`  | `float` | 0.5             | Pixels below this alpha are discarded |

### PBR Lighting Model
The standard shader uses a Blinn-Phong approximation with metallic/roughness:
- **Diffuse**: `baseColor * max(dot(N, L), 0)` with shadow attenuation
- **Specular**: `pow(max(dot(N, H), 0), shininess)` where shininess = `(1 - roughness) * 128`
- **Metallic**: Blends between dielectric and metallic reflectance
- **Ambient**: Global ambient from Skybox `Ambient` property

---

## Texture Slots

Each material can have multiple texture slots with different usages:

| Usage       | Description                                    |
|-------------|------------------------------------------------|
| `Albedo`    | Base color texture (multiplied with BaseColor) |
| `Normal`    | Normal map for surface detail                  |
| `Metallic`  | Per-pixel metallic map                         |
| `Roughness` | Per-pixel roughness map                        |
| `Occlusion` | Ambient occlusion map                          |
| `Emission`  | Self-illumination map                          |
| `Opacity`   | Alpha/transparency map                         |
| `Height`    | Heightmap for parallax (reserved)              |

### Texture Loading
Textures are loaded via SkiaSharp from image files (PNG, JPG, BMP, etc.):
1. Image is decoded to RGBA byte array
2. Uploaded to GPU as an OpenGL texture with mipmaps
3. Cached by `ResourceCache` to avoid duplicate uploads
4. Filtering: Trilinear (Linear mipmap + Linear magnification)
5. Wrapping: Repeat

---

## Material Files (.material)

Materials can be saved as standalone asset files in JSON format:

```json
{
  "BaseColor": "#FFFFFFFF",
  "Metallic": 0.0,
  "Roughness": 0.5,
  "Transparent": false,
  "AlphaCutoff": 0.5,
  "Textures": [
    {
      "Usage": "Albedo",
      "Path": "Assets/Textures/brick_albedo.png"
    },
    {
      "Usage": "Normal",
      "Path": "Assets/Textures/brick_normal.png"
    }
  ]
}
```

### Referencing Materials
`MeshRenderer` components reference materials by path:
```
MaterialPath = "Assets/Materials/Brick.material"
```

Materials are resolved at scene load time. The `MaterialRebind` system scans the scene after load and resolves any null materials from their persisted paths.

---

## Creating Materials

### In the Editor
1. **Right-click** in the Project Panel → **New Material**
2. Name the `.material` file
3. Select a MeshRenderer in the Inspector
4. Click the material slot to assign the material

### In the Inspector
When a `MeshRenderer` is selected, the Inspector shows:
- **Color picker** for the tint color
- **Texture slots** with "..." browse buttons for each usage type
- **Material path** display
- **Wireframe toggle**
- **Shadow cast/receive toggles**
- **Double-sided toggle**

---

## Transparency

When `Transparent` is enabled on a material:
1. The object is sorted back-to-front relative to the camera
2. Alpha blending is enabled: `SRC_ALPHA, ONE_MINUS_SRC_ALPHA`
3. Depth writing is disabled (objects behind show through)
4. `AlphaCutoff` discards pixels below the threshold (useful for foliage)

### Transparent Render Order
```
Opaque Pass (front-to-back, depth test enabled)
    │
    ▼
Transparent Pass (back-to-front, blending enabled)
```

This ordering prevents visual artifacts from overlapping transparent surfaces.

---

## Built-In Textures

### Glass Textures
Located in `Standard Assets/Glass/`:
| File                              | Description            |
|-----------------------------------|------------------------|
| `glass_plate_albedo.png`          | Glass base color       |
| `glass_plate_opacity.png`         | Glass transparency map |
| `simple_alpha_square_Specular.png`| Specular highlight map |

### Skybox Textures
47 equirectangular sky images in `Standard Assets/Skybox/` (sky_01_2k.png through sky_47_2k.png). Assign to the Skybox component's `TexturePath`.

---

## Model Import Materials

When importing 3D models (FBX, OBJ, GLTF), materials are imported automatically:
1. Assimp extracts material properties (color, textures) from the model
2. Engine creates `Material` instances with appropriate texture slots
3. Texture paths are converted to project-relative paths
4. Each mesh part gets its own `MeshRenderer` with the assigned material

### Multi-Material Objects
Models with multiple materials create multiple child GameObjects, one per material:
```
ImportedModel (parent)
├── Part_0 (MeshFilter + MeshRenderer with Material A)
├── Part_1 (MeshFilter + MeshRenderer with Material B)
└── Part_2 (MeshFilter + MeshRenderer with Material C)
```

---

## Material Warm-Up (MaterialRebind)

After scenes load or play mode starts, materials may need time to resolve:
1. `MaterialRebind.RepairScene()` runs for several frames after scene changes
2. It scans all `MeshRenderer` components for null materials
3. Resolves `MaterialPath` references to loaded `Material` objects
4. Retries loading deferred textures that weren't ready initially
5. Ensures all materials have valid GPU textures before rendering

This system handles the asynchronous nature of texture loading and ensures no missing materials after scene transitions.

---

## Terrain Materials

Terrain uses a separate material system — see the Terrain System document for details on:
- Splatmap-based multi-layer texturing (up to 8 layers)
- Per-layer tiling control
- Triplanar projection on steep slopes
- Dedicated terrain fragment shader
