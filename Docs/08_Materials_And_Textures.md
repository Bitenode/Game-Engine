# Game Engine — Materials and Textures

## Material System

Materials define how surfaces appear when rendered. They support a physically-based rendering (PBR) workflow with metallic/roughness properties, multiple texture slots, transparency, and alpha cutoff.

---

## Material Properties

| Property       | Type    | Default           | Description                               |
|----------------|---------|--------------------|-------------------------------------------|
| `BaseColor`    | `Color` | `White (1,1,1,1)` | Base albedo color (RGBA)                  |
| `Metallic`     | `float` | `0.0`             | 0 = dielectric (plastic, wood), 1 = full metal (gold, silver) |
| `Roughness`    | `float` | `0.5`             | 0 = mirror smooth, 1 = fully rough/matte |
| `Transparent`  | `bool`  | `false`           | Enable alpha blending                     |
| `AlphaCutoff`  | `float` | `0.5`             | Pixels below this alpha are discarded     |

### PBR Lighting Model
The Standard shader uses a **Blinn-Phong approximation** with metallic/roughness workflow:

| Component | Formula | Description |
|-----------|---------|-------------|
| **Diffuse** | `baseColor × max(dot(N, L), 0) × shadowAttenuation` | Lambertian diffuse with shadow |
| **Specular** | `pow(max(dot(N, H), 0), shininess)` | Blinn-Phong specular highlight |
| **Shininess** | `(1 - roughness) × 128` | Converted from roughness to specular exponent |
| **Metallic** | Blends dielectric ↔ metallic reflectance | Metals tint their specular with base color |
| **Ambient** | `baseColor × Skybox.Ambient` | Global ambient from the Skybox component |

The lighting model supports both **directional lights** (sun) and **point lights** (lamps), switchable per-light via the `uLightIsPoint` uniform.

---

## Texture Slots

Each material can have multiple texture slots with different usage types:

| Usage       | Texture Unit | Description                                    |
|-------------|-------------|------------------------------------------------|
| `Albedo`    | 0           | Base color texture (multiplied with BaseColor) |
| `Normal`    | 1           | Normal map for per-pixel surface detail        |
| `Specular`  | 2           | Specular intensity/color map                   |
| `Metallic`  | 3           | Per-pixel metallic map (grayscale)             |
| `Roughness` | 4           | Per-pixel roughness map (grayscale)            |
| `Occlusion` | 5           | Ambient occlusion map (darkens crevices)       |
| `Emission`  | 6           | Self-illumination map (glows without light)    |
| `Opacity`   | —           | Alpha/transparency map                         |
| `Height`    | —           | Heightmap for parallax (reserved for future)   |

### Texture Slot Structure
Each slot contains:
- **Texture** — the loaded `Texture2D` image data (RGBA bytes)
- **Usage** — the slot type from the table above
- **SourcePath** — project-relative path to the image file on disk

Both `RuntimeTexSlot` and `MaterialTexture` formats are supported for texture binding.

### Texture Loading Pipeline
Textures are loaded via **SkiaSharp** from image files:

1. Image file is read from disk (PNG, JPG, BMP, etc.)
2. SkiaSharp decodes the image to RGBA byte array
3. The `Texture2D` object is created with width, height, and pixel data
4. On first render, `ResourceCache.GetTexture()` uploads to the GPU:
   - Format: `GL_RGBA8` (8 bits per channel)
   - Mipmaps are auto-generated (`GL_GenerateMipmap`)
   - Filtering: **Trilinear** (linear mipmap for minification, linear for magnification)
   - Wrapping: **Repeat** on both S and T axes
5. The `GPUTexture` handle is cached — subsequent renders reuse the cached GPU resource

### Fallback Texture
When a texture is missing or fails to load, a **1x1 white fallback texture** is used (`ResourceCache.GetWhiteTexture()`). This ensures rendering continues without visual artifacts.

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
    },
    {
      "Usage": "Roughness",
      "Path": "Assets/Textures/brick_roughness.png"
    }
  ]
}
```

### Referencing Materials
`MeshRenderer` components reference materials by path:
```csharp
MaterialPaths = ["Assets/Materials/Brick.material"]
```

**Multi-material support:** `MeshRenderer.MaterialPaths` is a `List<string>`, allowing multiple material references for multi-part objects.

### Material Resolution
Materials are resolved at scene load time through the `MaterialRebind` system:
1. `MeshRenderer.OnEnable()` calls `ResolveMaterials()`
2. Each path in `MaterialPaths` is loaded via `ProjectService`
3. The loaded `Material` objects are cached in `ResolvedMaterials`
4. Both the full asset pipeline format and a simpler JSON format are supported

---

## Creating Materials

### In the Project Panel
1. **Right-click** in the Project Panel → **New Material**
2. Name the `.material` file
3. The file is created with default PBR properties

### In the Inspector
When a `MeshRenderer` is selected, the Inspector shows:
- **Color picker** — base tint color (multiplied with material BaseColor)
- **PBR sliders** — Metallic and Roughness values
- **Texture slots** — "..." browse buttons for each texture usage type
- **Material path** — displays the referenced `.material` file
- **Wireframe toggle** — render as wireframe lines only
- **Line width** — wireframe line thickness (default: 1.0)
- **Shadow toggles** — Cast Shadows and Receive Shadows checkboxes
- **Double-sided toggle** — disable backface culling
- **Invert front face** — swap front/back face winding order

### Assigning Materials
1. Select a MeshRenderer in the Inspector
2. Click the material path field or "..." button
3. Browse to a `.material` file
4. The material is loaded and applied to the mesh

---

## Transparency

When `Transparent` is enabled on a material, the rendering pipeline changes:

### Transparent Rendering Flow
```
Opaque Pass (front-to-back, depth test enabled, depth write ON)
    │ • All opaque objects rendered first
    │ • Establishes the depth buffer for correct occlusion
    │
    ▼
Transparent Pass (back-to-front, blending enabled, depth write OFF)
    │ • Transparent objects sorted by distance from camera
    │ • Alpha blending: SRC_ALPHA, ONE_MINUS_SRC_ALPHA
    │ • Depth reading is enabled but writing is disabled
    │ • Objects behind transparent surfaces show through
```

### Alpha Cutoff
`AlphaCutoff` discards pixels with alpha below the threshold. This is useful for:
- **Foliage** — tree leaves, grass blades (hard edges, no blending)
- **Fences/Gates** — chain-link, iron bars
- **Decals** — stickers, labels with transparent backgrounds

The alpha test happens in the fragment shader: `if (color.a < uAlphaCutoff) discard;`

---

## Built-In Textures

### Glass Textures
Located in `Standard Assets/Glass/`:

| File | Description |
|------|-------------|
| `glass_plate_albedo.png` | Glass base color (slightly blue-tinted) |
| `glass_plate_opacity.png` | Glass transparency map (white = opaque, black = transparent) |
| `simple_alpha_square_Specular.png` | Specular highlight map for glass |

### Skybox Textures
**47 equirectangular sky images** in `Standard Assets/Skybox/`:
- Named `sky_01_2k.png` through `sky_47_2k.png`
- Resolution: 2K equirectangular projection
- Variety: Daytime, sunset, overcast, night, space, alien environments
- Assign to the Skybox component's `TexturePath` property

---

## Model Import Materials

When importing 3D models (FBX, OBJ, glTF), materials are extracted and converted automatically:

### Import Process
1. **AssimpNet** extracts material properties from the model file (colors, textures, PBR values)
2. The engine creates `Material` instances with appropriate settings:
   - **Base color** extracted from the diffuse color property
   - **Near-black override** — if the imported base color is near-black (common in FBX exports), it's overridden to white
   - **Transparency** detected from opacity maps or alpha channels
3. **Textures** are mapped to the correct slots:
   - Assimp texture types → engine usage mapping (Diffuse → Albedo, Normals → Normal, etc.)
   - **Path resolution** — tries multiple paths: relative to model, filename search, project Assets folder
   - **Embedded textures** — extracted from model files and saved to disk as separate image files
4. **Filename pattern guessing** — texture usage is guessed from filename patterns when Assimp metadata is insufficient:
   - `_col`, `_color`, `_albedo`, `_diffuse` → Albedo
   - `_nrm`, `_normal`, `_norm` → Normal
   - `_rough`, `_roughness` → Roughness
   - `_met`, `_metallic` → Metallic
   - `_ao`, `_occlusion` → Occlusion
5. **Material files** are saved as `.material` files next to the imported model

### Multi-Material Objects
Models with multiple materials create multiple child GameObjects, one per material group:
```
ImportedModel (parent)
├── Part_0 (MeshFilter + MeshRenderer + MeshCollider)
│   └── Material: Assets/Materials/ModelName_Material0.material
├── Part_1 (MeshFilter + MeshRenderer + MeshCollider)
│   └── Material: Assets/Materials/ModelName_Material1.material
└── Part_2 (MeshFilter + MeshRenderer + MeshCollider)
    └── Material: Assets/Materials/ModelName_Material2.material
```

---

## Material Warm-Up (MaterialRebind)

After scenes load or play mode starts, materials may need time to resolve due to the asynchronous nature of texture loading.

### MaterialRebind.RepairScene()
Runs for **several frames** after scene changes:

1. Scans all `MeshRenderer` components in the scene
2. Finds components with null or unresolved materials
3. Resolves `MaterialPath` references to loaded `Material` objects
4. Retries loading deferred textures that weren't ready on initial load
5. Ensures all materials have valid GPU textures before rendering

This system handles:
- **Async texture loading** — textures may not be decoded on the first frame
- **Scene transitions** — materials may be invalidated when switching scenes
- **Play/Stop** — material state is restored from snapshots after play mode
- **Missing materials** — falls back to `DefaultMaterial()` (white, unlit)

---

## Terrain Materials

Terrain uses a separate material system based on splatmaps — see the [Terrain System](05_Terrain_System.md) document for full details.

Key differences from standard materials:
- **Splatmap-based blending** — up to 8 texture layers weighted per-vertex
- **Per-layer tiling** — independent UV repetition scale per layer
- **Triplanar projection** — prevents stretching on steep cliff faces
- **Dedicated shader** — the Terrain shader handles splatmap sampling and layer blending
- **No PBR per-layer** (yet) — roughness and metallic per-layer are reserved for future use

---

## Water Materials

Water surfaces use a specialized material system — see the Water component in the [Components Reference](03_Components_Reference.md).

Key features:
- **Gerstner wave displacement** computed in the vertex shader
- **Fresnel-based transparency** — angle-dependent see-through vs reflection
- **Foam rendering** on wave crests
- **Sky reflection** for realistic surface appearance

---

## Texture Formats and GPU Details

| Format | GL Internal | Usage |
|--------|-------------|-------|
| RGBA8 | `GL_RGBA8` | Standard color textures (albedo, normal, etc.) |
| RGBA32F | `GL_RGBA32F` | Float textures (terrain splatmaps) |
| Depth24 | `GL_DEPTH_COMPONENT24` | Shadow map depth textures |

### GPU Texture Management
- Each GL context (SceneView, GameView) maintains its own texture cache
- Textures are uploaded on first use (lazy initialization)
- `ResourceCache` prevents duplicate GPU uploads for shared textures
- Terrain splatmaps use per-context versioning for independent uploads
- A 1x1 white fallback texture is always available for missing textures
