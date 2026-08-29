# Game Engine — Rendering Pipeline

## Overview

The engine uses a GPU-accelerated **forward rendering pipeline** built on Silk.NET OpenGL (or OpenGL ES 3.0 via ANGLE on Windows). All rendering happens inside Avalonia's `OpenGlControlBase`, which provides a shared GL context for the Scene View and Game View. Each view maintains its own `ResourceCache` instance tied to its GL context to avoid cross-context resource issues.

---

## Render Loop

Each frame, both SceneView and GameView execute these passes in order:

```
 1. Material Warm-Up        (MaterialRebind.RepairScene — resolve null materials)
 2. Terrain streaming + LOD  (`TerrainStreamer.SyncAll` — tile ring around camera; `UpdateLOD` per terrain — distance-based chunk LOD with optional hysteresis)
 3. Skinned Mesh Update     (Compute bone matrices for SkinnedMeshRenderers)
 4. Shadow Pass             (Depth-only into 4096x4096 shadow FBO)
 5. Sky Pass                (Fullscreen quad — gradient + texture + sun glow)
 6. Grid Pass               (Infinite ground grid with distance fade)
 7. Opaque Pass             (Frustum-culled — standard/terrain/skinned shaders)
 8. Water Pass              (Gerstner wave displacement, Fresnel, foam)
 9. Transparent Pass        (Back-to-front sorted, alpha blending)
10. Particle Pass           (Billboard quads, instanced rendering)
11. Gizmo Pass              (Editor overlays, collider wireframes — Scene View only)
12. Volumetric Fog Pass     (Ray-marched scattering with shadow sampling — when enabled)
13. Post-Processing Pass    (Bloom, Fog, Color Grading, FXAA, Vignette, Underwater)
14. GL State Cleanup        (Restore Avalonia compositor state)
```

---

## Shadow Mapping

### Setup
- **Resolution:** 4096 x 4096 depth texture (Depth24 format)
- **Type:** Directional light orthographic projection
- **Implementation:** `ShadowMapGPU` class wraps a depth-only `GPUFramebuffer`
- **Light direction:** Computed from `Skybox.SunElevation` and `Skybox.Yaw`

### Shadow Pass
1. Compute sun direction from Skybox elevation and yaw angles
2. Build an orthographic light view-projection matrix centered on the visible scene
3. Bind the shadow FBO and clear the depth buffer
4. Enable **front-face culling** during the shadow pass to reduce self-shadowing artifacts (Peter Panning)
5. Render all shadow-casting meshes with the **DepthOnly** shader (position-only, no fragment color)
6. GPU skinning is supported in the shadow pass for animated meshes

### Shadow Sampling (Fragment Shader)
| Technique | Description |
|-----------|-------------|
| **PCF** | 3x3 Percentage Closer Filtering for soft shadow edges |
| **Slope Bias** | Dynamic bias based on `dot(normal, lightDir)` to prevent shadow acne |
| **Edge Fadeout** | Smooth falloff near shadow map borders to hide hard boundary artifacts |
| **Minimum Shadow** | 10% minimum light to prevent completely black shadow areas |

Shadow sampling is shared between the Standard and Terrain shaders via the `ShadowCalc()` GLSL function.

### Cascaded Shadow Map Compatibility
The Standard fragment shader supports cascaded shadow maps (`uShadowVPC[]`, `uCascadeCount`, `uCascadeSplits[]` uniforms). When using the forward pipeline (Scene View), which only produces a single shadow map, the renderer sets `uCascadeCount = 1`, `uShadowVPC[0]` to the single light VP matrix, and `uCascadeSplits[0]` to a large value. This ensures vegetation and other objects using the cascaded shadow path receive correct shadows in both the deferred (Game View) and forward (Scene View) pipelines.

---

## Shaders

All GLSL source code is stored as `const string` fields in `ShaderSources.cs`. The `Adapt()` method converts desktop GLSL (`#version 330 core`) to OpenGL ES (`#version 300 es`) by replacing the version directive and adding `precision mediump float;` qualifiers for ANGLE compatibility.

### Standard Shader (StandardVert + StandardFrag)

The primary shader for most objects. Full feature set:

**Vertex shader features:**
- Standard MVP transformation (model × view × projection)
- Normal matrix computation for correct normal transformation
- Shadow coordinate output for shadow mapping
- **GPU skinning** — when bone matrices are present, transforms vertices and normals by weighted bone matrices
- **Wind animation** — when `uIsVegetation` is set, applies time-based vertex displacement modulated by vertex height

**Fragment shader features:**
- **PBR-like lighting:** Blinn-Phong specular with roughness/metallic workflow
  - Diffuse: `baseColor * max(dot(N, L), 0)` with shadow attenuation
  - Specular: `pow(max(dot(N, H), 0), shininess)` where shininess = `(1 - roughness) * 128`
  - Metallic blending between dielectric and metallic reflectance
- **Directional + Point lights:** Switchable via `uLightIsPoint` uniform
- **Shadow mapping:** PCF shadow sampling via `ShadowCalc()` function
- **Alpha testing:** Configurable `uAlphaCutoff` for transparent materials
- **Double-sided rendering:** Normal flipping based on `gl_FrontFacing` when `uDoubleSided` is set
- **Ambient lighting:** Global ambient from Skybox `Ambient` property

**Vertex attributes:**

| Location | Attribute     | Type  | Layout   |
|----------|---------------|-------|----------|
| 0        | `aPosition`   | vec3  | Static + Skinned |
| 1        | `aNormal`     | vec3  | Static + Skinned |
| 2        | `aUV`         | vec2  | Static + Skinned |
| 3        | `aBoneIdx`    | ivec4 | Skinned only |
| 4        | `aBoneWeight` | vec4  | Skinned only |

**Key uniforms:**

| Uniform            | Type       | Description                        |
|--------------------|------------|------------------------------------|
| `uModel`           | mat4       | Model (world) matrix               |
| `uView`            | mat4       | Camera view matrix                 |
| `uProj`            | mat4       | Projection matrix                  |
| `uNormalMatrix`    | mat4       | `transpose(inverse(model))`        |
| `uShadowVP`       | mat4       | Light view-projection matrix       |
| `uBaseColor`       | vec4       | Material base color                |
| `uRoughness`       | float      | Surface roughness (0-1)            |
| `uMetallic`        | float      | Metallic value (0-1)               |
| `uAlphaCutoff`     | float      | Alpha test threshold               |
| `uDoubleSided`     | int        | Enable double-sided rendering      |
| `uLightDir`        | vec3       | Direction to light                 |
| `uLightColor`      | vec3       | Light color × intensity            |
| `uLightIsPoint`    | int        | 0 = directional, 1 = point        |
| `uLightPos`        | vec3       | Point light world position         |
| `uLightRange`      | float      | Point light falloff range          |
| `uCamPos`          | vec3       | Camera world position              |
| `uAmbient`         | float      | Global ambient level               |
| `uIsVegetation`    | int        | Enable wind animation              |
| `uTime`            | float      | Elapsed time for animation         |
| `uBones[N]`        | mat4[]     | Bone matrices for skinning         |
| `uAlbedoTex`       | sampler2D  | Albedo texture                     |
| `uNormalTex`       | sampler2D  | Normal map texture                 |
| `uShadowMap`       | sampler2D  | Shadow depth texture               |

### Terrain Shader (TerrainVert + TerrainFrag)

Specialized shader for terrain objects with splatmap-based multi-material blending.

**Features:**
- **Up to 8 texture layers** — weighted by 2 RGBA splatmap textures
- **Per-layer UV tiling** — independent texture repetition scale per layer
- **Triplanar projection** — on steep cliff faces (where the surface normal is mostly horizontal), texture is projected from the side to prevent stretching
- **Full shadow support** — same PCF shadow sampling as the Standard shader
- **Fallback** — uses standard material color when no layers are defined

**Additional uniforms:**

| Uniform        | Type       | Description                         |
|----------------|------------|-------------------------------------|
| `uSplatmap0`   | sampler2D  | Layer weights 0-3 (RGBA float)     |
| `uSplatmap1`   | sampler2D  | Layer weights 4-7 (RGBA float)     |
| `uLayerCount`  | int        | Number of active layers (0-8)      |
| `uLayer0..7`   | sampler2D  | Layer albedo textures               |
| `uTiling0..7`  | float      | Per-layer UV tiling scale           |

### Water Shader (WaterVert + WaterFrag)

Renders water surfaces with realistic wave simulation.

**Vertex shader:**
- **Gerstner wave displacement** — two wave layers with configurable amplitude, frequency, steepness, and direction
- **Wave normal computation** — analytical normals derived from wave derivatives

**Fragment shader:**
- **Fresnel effect** — `pow(1.0 - dot(viewDir, normal), 5.0)` for angle-dependent transparency
- **Sky reflection** — samples the skybox for reflections
- **Specular highlights** — Blinn-Phong sun specular on the water surface
- **Foam** — white foam on wave crests based on displacement height
- **Color blending** — deep vs shallow water color based on depth

### Particle Shader (ParticleVert + ParticleFrag)

Billboard particle rendering with instanced data.

**Vertex shader:**
- Billboard quads that always face the camera
- Per-particle position, size, color, and alpha from uniform arrays
- Instancing via uniform arrays (not vertex instancing)

**Fragment shader:**
- Soft circular particles with alpha falloff from center to edge
- `smoothstep` distance-based alpha for soft edges

### PostProcess Shader (PostProcessVert + PostProcessFrag)

Full-screen post-processing composite pass.

**Features (all optional, controlled by uniforms):**

| Effect | Implementation |
|--------|---------------|
| **FXAA** | Fast approximate anti-aliasing — edge detection and directional blur |
| **Bloom** | Simplified single-pass bloom — bright pixel extraction + Gaussian-like blur |
| **Fog** | Depth-based atmospheric fog with configurable color and density |
| **Color Grading** | Brightness, Contrast, Saturation, Exposure adjustments |
| **Tone Mapping** | HDR to LDR conversion — Reinhard or ACES filmic methods |
| **Vignette** | Darkened edges — configurable intensity and smoothness |
| **Underwater** | Wave distortion, underwater fog, caustic patterns, color absorption (blue channel boosted, red absorbed) |

### Volumetric Fog Shader (VolumetricFogVert + VolumetricFogFrag)

Fullscreen ray-marching shader for volumetric light scattering.

**Fragment shader features:**
- **Ray marching** — steps along the view ray from camera through each pixel, reconstructing world position from depth
- **Height-based density** — exponential falloff above `uFogBaseHeight` controlled by `uFogHeightFalloff`
- **3D noise modulation** — animated 3D noise pattern adds natural density variation
- **Henyey-Greenstein phase function** — directional scattering controlled by `uFogAnisotropy` (positive = forward scattering toward the light, negative = back-scattering)
- **Shadow sampling** — at each ray step, the world position is projected into shadow map space and sampled to occlude in-scattered light in shadowed regions
- **Transmittance accumulation** — Beer-Lambert law for light absorption along the ray

**Key uniforms:**

| Uniform | Type | Description |
|---------|------|-------------|
| `uSceneColor` | sampler2D | Scene color texture |
| `uSceneDepth` | sampler2D | Scene depth texture |
| `uShadowMap` | sampler2D | Shadow depth texture |
| `uInvVP` | mat4 | Inverse view-projection matrix |
| `uShadowVP` | mat4 | Light view-projection matrix |
| `uCamPos` | vec3 | Camera world position |
| `uNear` / `uFar` | float | Camera clipping planes |
| `uSunDir` | vec3 | Directional light direction |
| `uSunColor` | vec3 | Light color × intensity |
| `uFogDensity` | float | Base fog density |
| `uFogAnisotropy` | float | Scattering anisotropy (-1 to 1) |
| `uFogScattering` | float | In-scattered light multiplier |
| `uFogHeightFalloff` | float | Height-based density falloff |
| `uFogBaseHeight` | float | Base height of the fog volume |
| `uFogNoiseScale` | float | 3D noise spatial scale |
| `uFogNoiseSpeed` | float | Noise animation speed |
| `uFogMaxDist` | float | Maximum ray march distance |
| `uFogColor` | vec3 | Fog color tint |
| `uFogSteps` | int | Number of ray march steps |
| `uTime` | float | Elapsed time for noise animation |

### Other Shaders

| Shader        | Purpose                                                |
|---------------|--------------------------------------------------------|
| **DepthOnly** | Shadow map generation — position only, dummy fragment output. Supports GPU skinning for animated shadow casters. |
| **Sky**       | Fullscreen sky rendering — gradient blend + equirectangular texture sampling + sun glow disc at configurable elevation/yaw |
| **Grid**      | Infinite ground grid — per-pixel raycast to Y=0 plane, distance fade, colored axis lines (red X, blue Z), major/minor grid lines |
| **Wireframe** | Solid color lines — used for collider gizmos, selection outlines, and wireframe mode |
| **Blit**      | Fullscreen texture copy — used for post-processing ping-pong and final output |

---

## Shader Graph System

The engine includes a visual **node-based shader graph** system that compiles to GLSL shaders at runtime.

### Architecture
```
ShaderGraph (JSON .shadergraph file)
    │ contains
    ▼
ShaderNodes (connected graph of operations)
    │ compiled by
    ▼
ShaderGraph.Compile()
    │ produces
    ▼
GLSL Vertex + Fragment source code
    │ compiled by
    ▼
CustomShaderCache → ShaderProgram (GPU-ready shader)
```

### Node Types
| Node | Description |
|------|-------------|
| **OutputNode** | Terminal node — defines surface BaseColor, Normal, Metallic, Roughness, Emission, Opacity |
| **TextureSampleNode** | Samples a 2D texture at UV coordinates |
| **ColorNode** | Constant color value (RGBA) |
| **FloatNode** | Constant float value with configurable range |
| **MathNode** | Math operations: Add, Subtract, Multiply, Divide, Power, Lerp, Clamp, Abs, Step, SmoothStep |
| **CoordinateNode** | UV coordinates, world position, view direction, normal |
| **FresnelNode** | Fresnel effect (angle-dependent reflection/glow) |
| **NoiseNode** | Procedural noise generation (Perlin, Simplex) |

### Custom Shader Files (.shader)
Hand-written shaders using a custom format with GLSL vertex and fragment sections:
```
SHADER "My PBR Shader"

PROPERTIES {
    _BaseColor (Color) = (1, 1, 1, 1)
    _Metallic (Float) = 0.5
    _Roughness (Float) = 0.5
    _MainTex (Texture2D)
    _NormalMap (Texture2D)
}

VERTEX { ... GLSL code ... }
FRAGMENT { ... GLSL code ... }
```

The `Steel PBR.shader` in Standard Assets demonstrates a full Cook-Torrance BRDF implementation with GGX distribution, Smith geometry, and Schlick Fresnel approximation.

### CustomShaderCache
Compiled custom shaders are cached per GL context. The `CustomShaderCache` manages:
- Compilation of `.shader` files and shader graph output to GPU programs
- Per-context caching to avoid recompilation
- Fallback to the standard shader on compilation errors

---

## GPU Resource Management

### GLContext
Wraps the Silk.NET `GL` instance obtained from Avalonia's OpenGL control. Detects whether the context is OpenGL ES (ANGLE on Windows) or desktop OpenGL by checking the version string. The `Adapt()` method on `ShaderSources` uses this detection to generate the correct GLSL version directive.

### ResourceCache
Maps engine objects to GPU resources, handles lazy upload and disposal. **Each view** (SceneView, GameView) has its own `ResourceCache` instance tied to its OpenGL context.

| Method | Returns | Description |
|--------|---------|-------------|
| `GetMesh(Mesh)` | `GPUMesh` | Upload mesh to VAO/VBO/EBO (auto-upload on first use) |
| `GetTexture(Texture2D)` | `GPUTexture` | Upload RGBA8 texture with mipmaps |
| `GetWhiteTexture()` | `GPUTexture` | 1x1 white fallback texture |
| `MarkMeshDirty(Mesh)` | — | Force re-upload after terrain edit |
| `GetTerrainSplatTextures(Terrain)` | GPU textures | Per-context splatmap textures with version tracking |
| `SetTerrainSplatVersion(Terrain, v)` | — | Mark a context as up-to-date |

**Orphan eviction:** Periodically cleans up cached resources for meshes/textures that are no longer referenced.

### Per-Context Splatmap Versioning
Terrain splatmap textures are managed **per-GL-context** to avoid cross-context OpenGL handle issues. Each context tracks the last uploaded `SplatmapVersion` counter. When the terrain's version advances (i.e., the user painted new data), the context independently re-uploads the splatmap. This ensures both SceneView and GameView always display the correct splatmap data without one view's upload "stealing" the dirty flag from the other.

### GPUMesh
Manages VAO (Vertex Array Object), VBO (Vertex Buffer Object), and EBO (Element Buffer Object) for mesh rendering.

**Two vertex layouts:**

| Layout | Stride | Attributes | Usage |
|--------|--------|------------|-------|
| **Static** | 32 bytes | Position(3f) + Normal(3f) + UV(2f) | Standard meshes |
| **Skinned** | 64 bytes | Position(3f) + Normal(3f) + UV(2f) + BoneIdx(4i) + BoneWeight(4f) | Skeletal meshes |

**Index buffers:**
- Triangle EBO — `GL_TRIANGLES` for filled rendering
- Line EBO — `GL_LINES` for wireframe rendering

The `Upload()` method auto-detects whether the mesh has bone data and selects the appropriate layout.

### GPUTexture
Supports multiple upload modes for different texture types:

| Method | Format | Description |
|--------|--------|-------------|
| `Upload(Texture2D)` | RGBA8 | Standard textures with mipmap generation |
| `UploadFloat(float[], w, h)` | RGBA32F | Float textures for splatmap data |
| `CreateDepth(w, h)` | Depth24 | Depth textures for shadow maps |
| `CreateColor(w, h)` | RGBA8 | Color textures for FBO attachments |

**Default filtering:** Trilinear (linear mipmap for minification, linear for magnification)
**Default wrapping:** Repeat on both axes

### GPUFramebuffer
Off-screen render targets for shadow mapping and post-processing.

| Configuration | Usage |
|---------------|-------|
| Depth-only | Shadow maps — `DrawBuffers(None)` for no color output |
| Color + Depth | Post-processing ping-pong buffers |
| OpenGL ES 3.0 compatible | Works with ANGLE on Windows |

### FullscreenQuad
A single triangle that covers the entire screen, used for:
- Sky rendering
- Post-processing passes
- Blit (texture copy) operations
- Grid rendering (per-pixel raycast)

Using a single oversized triangle instead of a quad avoids the diagonal seam artifact.

### ShaderProgram
Compiles and links GLSL vertex + fragment shaders. Provides:
- Uniform location caching (dictionary lookup)
- Type-safe uniform setters: `SetInt`, `SetFloat`, `SetVec2/3/4`, `SetMat4`, `SetTexture`
- Matrix upload with `transpose=false` (row-major to column-major conversion)

---

## Rendering Passes Detail

### Opaque Pass
1. Enable depth test (`GL_LESS`), depth write ON, blending OFF
2. Set the Standard shader + global uniforms (view, projection, light direction/color, shadow VP, camera position, ambient)
3. For each visible opaque mesh (frustum-culled):
   - **If terrain with layers** → switch to Terrain shader, bind splatmaps (units 0-1) + layer textures (units 4-11), set per-layer tiling uniforms
   - **If skinned mesh** → compute and upload bone matrices, use skinned vertex layout
   - **Else** → use Standard shader, bind albedo/normal/metallic/roughness/AO/emissive textures
   - Set per-object uniforms (model matrix, normal matrix, material properties)
   - Set cull face mode (back-face culling, or disabled for double-sided materials)
   - Draw call

**Terrain batching optimization:** Terrain chunks are grouped by their parent `Terrain` reference. Splatmap textures and layer textures are bound once per terrain, not per chunk. Only per-chunk model matrix changes between draw calls.

### Water Pass
1. Bind the Water shader
2. Set wave parameters (amplitude, frequency, steepness, direction for 2 wave layers)
3. Set elapsed time for wave animation
4. Set camera position and sky texture for reflections
5. Enable alpha blending for water transparency
6. Draw the water surface mesh

### Transparent Pass
1. Sort all transparent items **back-to-front** by view-space Z distance
2. Enable blending (`GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA`), depth write OFF
3. Draw each item with the Standard shader
4. Restore depth write after all transparent items are drawn

### Particle Pass
1. Bind the Particle shader
2. For each active `ParticleEmitter`:
   - Upload per-particle data (positions, sizes, colors, alphas) as uniform arrays
   - Set billboard orientation from camera view matrix
   - Draw instanced billboard quads
3. Particles are rendered with alpha blending enabled

### Volumetric Fog Pass
Rendered when `PostProcessVolume.VolumetricFogEnabled` is `true`. Uses a dedicated ray-marching shader applied as a fullscreen pass between the main scene render and post-processing.

**Pipeline:**
1. Bind the Volumetric Fog shader and a dedicated FBO
2. Read the scene color texture (unit 0) and scene depth texture (unit 1)
3. Bind the shadow map (unit 2) and set the shadow view-projection matrix
4. Set camera uniforms: inverse view-projection matrix, camera position, near/far planes
5. Set lighting uniforms: sun direction, sun color
6. Set fog parameter uniforms from `PostProcessVolume`:
   - Density, Anisotropy, Scattering intensity, Height falloff, Base height
   - Noise scale, Noise speed, Max distance, Color tint, Step count
7. Set elapsed time for noise animation
8. Draw a fullscreen quad — the fragment shader ray-marches from the camera through each pixel:
   - Steps along the view ray, sampling fog density at each point
   - Applies height-based density falloff (`exp(-heightFalloff * (y - baseHeight))`)
   - Modulates density with 3D noise for natural variation
   - Computes Henyey-Greenstein phase function for directional scattering
   - Samples the shadow map at each step to occlude in-scattered light in shadows
   - Accumulates transmittance and in-scattered light, composites with the scene color
9. Blit the result back to the scene framebuffer

**Supported in both Game View (deferred pipeline) and Scene View (forward pipeline).**

### Post-Processing Pass
1. The scene is rendered to an off-screen framebuffer (color + depth)
2. Bind the PostProcess shader
3. Read the scene color texture and depth texture
4. Apply enabled effects in order: FXAA → Bloom → Fog → Color Grading → Tone Mapping → Vignette → Underwater
5. Blit the final result to the screen

### Frustum Culling
Each mesh has a bounding sphere computed from its vertices. Before drawing, the sphere (transformed to world space) is tested against the 6 frustum planes extracted from the view-projection matrix. Meshes outside the frustum are skipped entirely.

**Optimization:** Frustum sphere results are cached. Vegetation chunks (with `Chunk_` prefix) are fast-skipped if culled.

### Level of Detail (LOD)

**Procedural Mesh LOD (`MeshLod`):**
- Uses `Projection.EstimateProjectedRadiusPx()` to determine screen-space size
- Default surface size: 1920 x 1080
- Adjusts tessellation for Sphere, Cylinder, and Cone primitives
- `MeshFilter.Mesh` is upgraded in-place when the projected size increases

**Terrain LOD:**
- Per-chunk, up to 3 mesh levels (LOD 0 = full, LOD 1 = half, LOD 2 = quarter vertex step)
- Selected by camera distance to chunk center; thresholds are **`LodDistanceNearChunks`** and **`LodDistanceMidChunks`** times chunk world size
- Optional **`LodHysteresisWorld`** reduces LOD popping at band boundaries
- **`TerrainStreamer.SyncAll`** runs before LOD so streamed tiles exist for the same frame

**Tree LOD (`TreeLOD`):**
- 4 levels: LOD 0 (full mesh), LOD 1 (medium), LOD 2 (low), LOD 3 (billboard impostor)
- Distance thresholds: configurable per tree (default 15m, 30m, 55m)
- Billboard uses yaw-sliced texture atlas for view-dependent appearance

---

## Texture Units Layout

### Standard Shader
| Unit | Texture Type       |
|------|--------------------|
| 0    | Albedo             |
| 1    | Normal map         |
| 2    | Specular           |
| 3    | Metallic           |
| 4    | Roughness          |
| 5    | Ambient Occlusion  |
| 6    | Emissive           |
| 7    | Shadow map         |

### Terrain Shader
| Unit   | Texture Type                |
|--------|-----------------------------|
| 0      | Splatmap 0 (layers 0-3)    |
| 1      | Splatmap 1 (layers 4-7)    |
| 2      | Shadow map                  |
| 4-11   | Layer albedo textures (0-7) |

---

## Rendering Optimizations

| Optimization | Description |
|--------------|-------------|
| **GameObject culling** | Disabled GameObjects (`Enabled = false`) and their entire subtree are skipped early in all render traversals — `GatherDrawItems`, `RenderShadowNode`, `RenderParticlesRecursive`, `RenderWaterRecursive`, and Canvas `GatherElements`. This is a simple `if (!go.Enabled) return;` check before any matrix or material work. |
| **Frustum culling** | Bounding sphere test against 6 frustum planes |
| **Thread-static buffers** | Reuse draw-item buffers to reduce GC pressure |
| **Terrain batching** | Group chunks by terrain, bind splatmaps once per terrain |
| **Vegetation fast-skip** | Skip culled vegetation chunks by `Chunk_` prefix check |
| **LOD** | Per-mesh procedural LOD, per-chunk terrain LOD, per-tree billboard LOD |
| **Lazy upload** | Meshes and textures are uploaded to GPU on first use |
| **Dirty tracking** | Only re-upload modified meshes (terrain edits, splatmap changes) |
| **Splatmap versioning** | Per-context version counter avoids redundant GPU uploads |
| **Index tracking** | MeshRenderer/MeshFilter pairing via index for fast component lookup |
| **UI batching** | CanvasRenderer merges consecutive quads sharing a texture into single draw calls |

---

## Runtime UI Rendering (CanvasRenderer)

The engine includes a dedicated GPU-accelerated UI rendering pipeline for in-game interfaces. The `CanvasRenderer` draws all active `Canvas` hierarchies after the main 3D scene.

### Render Order

```
1. Shadow pass (depth-only)
2. Opaque pass (MeshRenderers, SkinnedMeshRenderers, Terrain)
3. Transparent pass (Water, Particles, Decals, World-Space Canvases)
4. Volumetric Fog pass (ray-marched scattering — when enabled)
5. Post-processing (Bloom, Fog, Color Grading, Tone Mapping, Vignette, FXAA)
6. UI Overlay pass (CanvasRenderer — ScreenSpaceOverlay canvases)
7. Editor overlays (Grid, Gizmos, Collider wireframes)
```

### Architecture

| Component | Role |
|-----------|------|
| `Canvas` | Root component defining render mode, scale mode, and sort order |
| `RectTransform` | Anchor-based 2D layout (relative to parent or canvas root) |
| `UIElement` subclasses | Generate `UIQuad` draw data (position, UV, color, texture) |
| `CanvasRenderer` | Collects quads, batches by texture, uploads to GPU, draws |
| `UIEventSystem` | Per-frame pointer raycasting and event dispatch |

### Rendering Pipeline

1. **Canvas filtering** — Only canvases where `IsActiveAndEnabled` is `true` are processed. This respects both the Canvas component's own `Enabled` flag and the owning GameObject's `IsActiveInHierarchy`.
2. **Canvas traversal** — Canvases are sorted by `SortOrder` (ascending). Each is traversed depth-first. During traversal, disabled GameObjects (`Enabled = false`) and their entire subtree are skipped, so disabling a UI panel hides all its children automatically.
3. **Quad collection** — Each `UIElement` emits `UIDrawData` (array of `UIQuad` structs) given its computed rect.
4. **Texture batching** — Consecutive quads sharing a texture handle + shader type are merged into a single draw batch.
5. **GPU upload** — Vertices (pos2 + uv2 + color4 = 32 bytes each) and indices are streamed to dynamic VBO/EBO.
6. **Draw calls** — Each batch binds its texture and issues one `glDrawElements` call.

### Shaders

| Shader | Purpose |
|--------|---------|
| `UIVert` | Vertex shader — transforms canvas-space positions by an orthographic or world-space MVP matrix |
| `UIFrag` | Fragment shader — samples texture and multiplies by vertex color |
| `UITextFrag` | Fragment shader — SDF text rendering with alpha-tested font atlas |

### World-Space Canvases

World-space canvases are rendered during the transparent pass. The canvas rect is mapped to a billboard in 3D space using the GameObject's transform. Canvas pixels are scaled to world units via `WorldSizeX`/`WorldSizeY`.

### Screen-Space Scaling

The `CanvasScaleMode.ScaleWithScreenSize` mode computes a scale factor from the viewport vs. reference resolution using a logarithmic blend between width and height matching (controlled by `MatchWidthOrHeight`). This ensures UI elements look consistent across different screen sizes.

---

## Planet Atmosphere and Clouds (Skybox-Decoupled)

Planet atmosphere rendering is now an isolated path and does not depend on `Skybox` runtime values.

- **Planet data source:** `PlanetTerrain` + `PlanetAtmosphere` component state
- **Resolver:** `SceneRenderer.ResolvePlanetAtmosphere(...)` produces per-planet render params
- **Terrain pass:** `PlanetTerrainFrag` applies atmosphere blend on top of biome lighting (radial slope for cave-wall rock texturing). **Triplanar albedo** blends projection axes by slope: flat ground uses **radial** axes (stable at cube-face poles); steep faces use the **surface normal** so top-layer textures do not smear along cliff walls. Below the crust, atmosphere tint is skipped; inward cave faces keep biome under-color; cavity AO darkens ceilings and enclosed walls
- **Interior rendering:** when the camera is inside the crust band, backface and frustum culling are disabled so cave interiors stay visible while LOD refines
- **Planet shadows:** renderable planet leaf meshes are drawn in the shadow depth pass (`RenderPlanetLeafShadows`) for form shadows at cave mouths and rims
- **Planet water pass:** `PlanetWaterFrag` — atmosphere-driven reflection, per-body tint arrays, shore biome blend, mask discard. **Near camera:** draws `QuadNode.GeneratedWaterMesh` patches (same LOD grid as terrain). **Far / orbit:** draws the uniform `PlanetWater` orbit shell on `PlanetTerrain.WaterGO`. Rendered **after** planet atmosphere and clouds so haze does not cover the surface. Double-sided, alpha blend, `DepthFunc.Lequal`, reduced wave amplitude when near the crust.
- **Cloud pass:** `PlanetCloudsFrag` is rendered as a dedicated planet pass

`Skybox` still controls only the world background sky pass. Changing `Skybox` values should not change planet terrain/water/cloud shading.
