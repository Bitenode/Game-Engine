# Game Engine — Rendering Pipeline

## Overview

The engine uses a GPU-accelerated forward rendering pipeline built on Silk.NET OpenGL (or OpenGL ES 3.0 via ANGLE on Windows). All rendering happens inside Avalonia's `OpenGlControlBase`, which provides a shared GL context for the Scene View and Game View.

---

## Render Loop

Each frame, both SceneView and GameView execute these passes in order:

```
1. Material Warm-Up     (MaterialRebind.RepairScene)
2. Terrain LOD Update   (UpdateLOD per terrain)
3. Shadow Pass          (depth-only into shadow FBO)
4. Sky Pass             (fullscreen quad with sky shader)
5. Grid Pass            (infinite ground grid)
6. Scene Pass           (opaque → transparent)
7. Gizmo Pass           (editor overlays, SceneView only)
8. GL State Cleanup     (restore Avalonia compositor state)
```

---

## Shadow Mapping

### Setup
- **Resolution**: 4096 x 4096 depth texture
- **Type**: Directional light orthographic projection
- **Implementation**: `ShadowMapGPU` class wraps a depth-only FBO

### Shadow Pass
1. Compute sun direction from `Skybox.SunElevation` and `Skybox.Yaw`
2. Build orthographic light view-projection matrix centered on the scene
3. Bind shadow FBO and render all meshes with the depth-only shader
4. Front-face culling enabled during shadow pass to reduce self-shadowing (Peter Panning)

### Shadow Sampling (Fragment Shader)
- **PCF**: 3x3 Percentage Closer Filtering for soft shadow edges
- **Slope Bias**: Dynamic bias based on surface-to-light angle to prevent shadow acne
- **Edge Fadeout**: Smooth falloff near shadow map borders to hide hard boundaries
- **Minimum Shadow**: 10% minimum to prevent completely black areas

---

## Shaders

All GLSL source is stored as `const string` in `ShaderSources.cs`. The `Adapt()` method converts `#version 330 core` to `#version 300 es` for ANGLE compatibility.

### Standard Shader (StandardVert + StandardFrag)
Used for most objects. Supports:
- **PBR Lighting**: Blinn-Phong with roughness/metallic
- **Directional + Point Lights**: Switchable via `uLightIsPoint`
- **Shadow Mapping**: Via `uShadowMap` texture and `ShadowCalc()` function
- **Alpha Testing**: Configurable cutoff for transparent materials
- **Double-Sided**: Normal flipping for two-sided rendering

**Vertex Attributes:**
| Location | Attribute   | Type  |
|----------|-------------|-------|
| 0        | `aPosition` | vec3  |
| 1        | `aNormal`   | vec3  |
| 2        | `aUV`       | vec2  |

**Key Uniforms:**
| Uniform          | Type      | Description                    |
|------------------|-----------|--------------------------------|
| `uModel`         | mat4      | Model (world) matrix           |
| `uView`          | mat4      | Camera view matrix             |
| `uProj`          | mat4      | Projection matrix              |
| `uNormalMatrix`   | mat4     | transpose(inverse(model))      |
| `uShadowVP`      | mat4     | Light view-projection          |
| `uBaseColor`      | vec4     | Material base color            |
| `uRoughness`      | float    | Surface roughness              |
| `uMetallic`       | float    | Metallic value                 |
| `uAlbedoTex`      | sampler2D| Albedo texture (unit 0)       |
| `uShadowMap`      | sampler2D| Shadow depth texture (unit 3) |
| `uLightDir`       | vec3     | Direction to light             |
| `uCamPos`         | vec3     | Camera world position          |

### Terrain Shader (TerrainVert + TerrainFrag)
Used for terrain objects with splatmap layers. Supports:
- **Up to 8 texture layers** weighted by 2 RGBA splatmap textures
- **Per-layer UV tiling** for texture repetition control
- **Triplanar projection** on steep cliff faces to prevent stretching
- **Full shadow support** (same as standard shader)
- **Fallback** to standard material when no layers are defined

**Additional Uniforms:**
| Uniform        | Type      | Description                      |
|----------------|-----------|----------------------------------|
| `uSplatmap0`   | sampler2D | Layer weights 0-3 (RGBA float)  |
| `uSplatmap1`   | sampler2D | Layer weights 4-7 (RGBA float)  |
| `uLayerCount`  | int       | Number of active layers          |
| `uLayer0..7`   | sampler2D | Layer albedo textures (units 4-11)|
| `uTiling0..7`  | float     | Per-layer UV tiling scale        |

### Other Shaders
| Shader         | Purpose                                     |
|----------------|---------------------------------------------|
| **DepthOnly**  | Shadow map generation (position only)        |
| **Sky**        | Gradient + equirectangular texture + sun glow|
| **Grid**       | Infinite ground grid with depth writing      |
| **Wireframe**  | Solid color lines (collider gizmos, etc.)    |

---

## GPU Resource Management

### ResourceCache
Maps engine objects to GPU resources, handles lazy upload and disposal. Each view (SceneView, GameView) has its own `ResourceCache` instance tied to its OpenGL context.

- `GetMesh(Mesh)` → `GPUMesh` (VAO/VBO/EBO, auto-upload)
- `GetTexture(Texture2D)` → `GPUTexture` (RGBA8, mipmapped)
- `GetWhiteTexture()` → 1x1 white fallback
- `MarkMeshDirty(Mesh)` → force re-upload after terrain edit
- `GetTerrainSplatTextures(Terrain)` → per-context splatmap GPU textures with version tracking
- `SetTerrainSplatVersion(Terrain, version)` → mark a context as up-to-date

### Per-Context Splatmap Versioning
Terrain splatmap textures are managed per-GL-context to avoid cross-context OpenGL handle issues. Each context tracks the last uploaded `SplatmapVersion` counter. When the terrain's version is ahead (i.e., the user painted new data), the context re-uploads the splatmap independently. This ensures both SceneView and GameView always display the correct splatmap data without one view's upload "stealing" the dirty flag from the other.

### GPUMesh
Interleaved vertex buffer: Position (3f) + Normal (3f) + UV (2f) = 32 bytes/vertex.
Separate index buffers for triangles (GL_TRIANGLES) and wireframe lines (GL_LINES).

### GPUTexture
Supports three upload modes:
- `Upload(Texture2D)` — RGBA8 bytes with mipmaps (standard textures)
- `UploadFloat(float[], w, h)` — RGBA32F floats (splatmap data)
- `CreateDepth(w, h)` — Depth24 for shadow maps

### GPUFramebuffer
Off-screen render targets. Supports:
- Depth-only (shadow maps) with `DrawBuffers(None)`
- Color + Depth (post-processing)
- OpenGL ES 3.0 compatible

---

## Rendering Passes Detail

### Opaque Pass
1. Enable depth test (LESS), depth write ON, blending OFF
2. Set standard shader + global uniforms (view, proj, light, shadow)
3. For each visible opaque mesh:
   - If terrain with layers → switch to terrain shader, bind splatmaps + layer textures
   - Else → use standard shader, bind albedo texture
   - Set per-object uniforms (model matrix, normal matrix, material properties)
   - Set cull face mode (back-face or disabled for double-sided)
   - Draw

### Transparent Pass
1. Sort transparent items back-to-front by view-space Z
2. Enable blending (SRC_ALPHA, ONE_MINUS_SRC_ALPHA), depth write OFF
3. Draw each item with the standard shader
4. Restore depth write

### Frustum Culling
Each mesh has a bounding sphere. Before drawing, the sphere is tested against the 6 frustum planes extracted from the view-projection matrix. Meshes outside the frustum are skipped entirely.

### LOD (Level of Detail)
- **MeshLod**: Procedural LOD for standard meshes based on screen-space size
- **Terrain LOD**: Per-chunk LOD with 3 levels (full, half, quarter resolution) selected by camera distance

---

## Texture Units Layout

| Unit    | Standard Shader | Terrain Shader          |
|---------|-----------------|-------------------------|
| 0       | Albedo texture  | Splatmap 0 (layers 0-3) |
| 1       | (unused)        | Splatmap 1 (layers 4-7) |
| 2       | (unused)        | Shadow map              |
| 3       | Shadow map      | (unused)                |
| 4-11    | (unused)        | Layer albedo textures   |
