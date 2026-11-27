# Game Engine — Rendering Pipeline

## Overview

The engine uses a GPU-accelerated rendering pipeline built on Silk.NET OpenGL (or OpenGL ES 3.0 via ANGLE on Windows), supporting both **forward** (Scene View, Xbox UWP, and fallback) and **deferred** (desktop Game View and standalone `Engine.Player`) paths. Editor `GameView` and standalone `PlayerFrameRenderer` share `GameRenderPipeline` helpers so Deferred + SSAO + SSR + Volumetric Fog + TAA + Depth of Field + Post-Processing work identically when a `PostProcessVolume` is active (`#if XBOX_UWP` remains on the lightweight forward path). All editor rendering happens inside Avalonia's `OpenGlControlBase`, where each view maintains its own `ResourceCache` instance tied to its GL context to avoid cross-context resource issues.

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
10. Particle Pass           (Billboard / streak / flame quads, instanced rendering — **Scene View only**; see Game View note below)
11. Gizmo Pass              (Editor overlays, collider wireframes — Scene View only)
12. Volumetric Fog Pass     (Ray-marched scattering with shadow sampling — when enabled)
13. Post-Processing Pass    (Bloom, Fog, Color Grading, FXAA, Vignette, Underwater)
14. GL State Cleanup        (Restore Avalonia compositor state)
```

**Game View / PlayerView (deferred pipeline):** shadow and SSAO passes can leave `glViewport` at half width. Weather precipitation is therefore drawn **after post-processing** as a full-viewport overlay (`SceneRenderer.RenderParticles(..., overlayPass: true)`). The overlay restores `W×H` viewport, disables scissor/depth/cull for the pass, and draws rain/snow with depth test off so streaks cover the full lens. Scene View keeps the step-10 particle pass in the main transparent pipeline and passes explicit pixel viewport size to `RenderParticles`.

**Scene View during Play (dual-view perf):** while Game View is playing, Scene View throttles full renders (~0.45 s) and sets thread-local skip flags so it omits the shadow pass, planet atmosphere/cloud shells, and planet leaf shadow draws. Game View keeps the full stack. Flags reset each frame in `finally` blocks.

**Profiler triangle/draw counts:** `Profiler.ShadowPassDepth` suppresses draw/triangle/batch accumulation during shadow map rendering so cascaded shadow passes do not multiply on-screen HUD stats.

---

## Shadow Mapping

### Setup
- **Desktop Editor & `Engine.Player` (CSM):** 4-cascade texel-snapped `CascadedShadowMap` (CSM) covering up to `MaxShadowDistance` = **160 world units**, with 10% cascade-border blending (`SampleShadowCascaded` in `Common/Shadows.glsl`)
- **Xbox UWP (`#if XBOX_UWP`):** Lightweight single `ShadowMapGPU` (4096×4096 Depth24 FBO)
- **Type:** Directional light orthographic projection per cascade
- **Light direction:** Computed from the active directional `Light` (or `Skybox.SunElevation` / `Skybox.Yaw` fallback)

### Shadow Pass
1. Compute sun direction and split the camera frustum into 4 cascades (texel-snapped to eliminate shimmering during camera movement)
2. Bind each cascade's depth FBO and clear the depth buffer
3. Enable **front-face culling** during the shadow pass to reduce self-shadowing artifacts (Peter Panning)
4. Render all shadow-casting meshes with the **DepthOnly** shader (position-only, no fragment color)
5. GPU skinning is supported in the shadow pass for animated meshes

### Shadow Sampling (Fragment Shader)
| Technique | Description |
|-----------|-------------|
| **Cascaded Sampling** | `SampleShadowCascaded` selects the cascade by view-space depth and blends smoothly across the outer 10% of each cascade border |
| **PCF** | 3x3 Percentage Closer Filtering for soft shadow edges |
| **Slope Bias** | Dynamic bias based on `dot(normal, lightDir)` to prevent shadow acne |
| **Edge Fadeout** | Smooth falloff near shadow map / max-distance borders to hide hard boundary artifacts |
| **Minimum Shadow** | 10% minimum light to prevent completely black shadow areas |

Shadow sampling is shared across Standard, Deferred, and Terrain shaders via `Common/Shadows.glsl`. When only a single shadow map is active (e.g. Xbox UWP), the renderer sets `uCascadeCount = 1`, `uShadowVPC[0]` to the single light VP matrix, and `uCascadeSplits[0]` to a large value.

---

## Shaders

### Modular GLSL architecture

Built-in shaders ship as **47 embedded `.glsl` files** under `Core/Rendering/Shaders/` (folders: `Common/`, `Core/`, `Terrain/`, `Planet/`, `Effects/`, `PostProcess/`, `Editor/`, `UI/`). Shared headers include `FrameGlobals.glsl`, `LightingGlobals.glsl`, `PBR.glsl`, `Shadows.glsl`, and `Skinning.glsl`.

| Component | Role |
|-----------|------|
| **`ShaderPreprocessor`** | Disk-first load in Editor dev (auto-finds `Core/Rendering/Shaders` next to source) with embedded-resource fallback; comment-aware `#include` / `#pragma once`; platform headers (`#version 330 core` vs `#version 300 es` + precision); variant `#define`s; **line-mapped** compiler errors back to the original file |
| **`ShaderSources`** | Backward-compatible facade — `ShaderSources.Paths` logical paths, lazy `LoadDesktop()` properties, `Adapt()` → `ShaderPreprocessor.AdaptSource()`, `TerrainFragForUnits()` → terrain sampler budget variants |
| **`ShaderLibrary`** | Per-`GL` context owner — built-in + custom `.shader` programs, variant cache (`ShaderVariantKey`), `#include` dependency tracking, **safe hot-reload** (compile candidate first; keep last good program on failure) |
| **`GlobalUniforms`** | std140 UBOs: binding **0** `FrameGlobals`, **1** `LightingGlobals`, **2** `BoneGlobals` (`GlobalUniforms` + `UniformBuffer` / `BoneUniformBuffer` on CPU) |
| **`CustomShaderCache`** | Facade for project `.shader` files via `ShaderLibrary` — passes: **Forward**, **GBuffer** (deferred Surface materials), **ShadowCaster** |
| **`ShaderFileParser`** | Parses `.shader` assets — explicit `VERTEX`/`FRAGMENT` blocks or a single **`Surface { void surf(...) }`** block expanded into Forward/GBuffer/Shadow passes |
| **`MaterialBinder`** | Binds `MaterialPropertyBag` + schema defaults; applies `ShaderRenderState` (queue, cull, ZWrite, blend) with deterministic GL restore |
| **`ShaderHotReloader`** | Watches project `*.shader` / `*.glsl` and (dev) engine built-ins; ~150 ms debounce; marks dirty libraries and redraws viewports |

**ANGLE binary cache:** `ShaderProgram.BinaryCacheDirectory` defaults to `%LOCALAPPDATA%\GameEngine\ShaderCache` in the editor and desktop player (`.glbin` keyed by GPU driver, ANGLE DLL stamp, and preprocessed sources). Xbox builds use their own `LocalCache\Shaders` path.

New rendering code should prefer `ShaderLibrary.GetOrCreate(gl, isES)` over compiling raw strings so variants, UBO binding, hot-reload, and caching stay consistent.

### Standard Shader (StandardVert + StandardFrag)

Sources: `Core/Standard.vert.glsl` + `Core/Standard.frag.glsl` (includes Common lighting/PBR/shadows; reads global UBOs when present).

The primary shader for most objects. Full feature set:

**Vertex shader features:**
- Standard MVP transformation (model × view × projection)
- Normal matrix computation for correct normal transformation
- Shadow coordinate output for shadow mapping
- **GPU skinning** — when bone matrices are present, transforms vertices and normals by weighted bone matrices
- **Wind animation** — when `uIsVegetation` is set, applies time-based vertex displacement modulated by vertex height

**Fragment shader features:**
- **Cook-Torrance GGX PBR lighting** (matching the Deferred lighting pass via `Common/PBR.glsl`):
  - GGX normal distribution, Smith geometry term, and Schlick Fresnel with metallic/roughness workflow
  - Mipmapped `ReflectionProbe` cubemap specular IBL (falls back to `ProceduralSkyIBL` when no probe is captured)
- **Directional + up to 4 Point lights:** Main directional light plus up to 4 nearest point lights
- **Cascaded shadow mapping:** 4-cascade `SampleShadowCascaded` with 10% border blend and PCF
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
- **Up to 8 texture layers via `GL_TEXTURE_2D_ARRAY`** — weighted by 2 RGBA splatmap textures (`uSplatmap0/1` on units 0–1), with all 8 albedo layers packed into `uLayerAlbedos` (unit 2) and all 8 tangent-space normal maps packed into `uLayerNormals` (unit 3). Layers are resampled on CPU and cached per-`ResourceCache` via `GetOrCreateTexture2DArray` (capped at 512 on ES/Xbox, 1024 on desktop)
- **Per-layer UV tiling & PBR scalars** — independent UV tiling, roughness, and metallic values per layer
- **Parallax** — driven by normal-map alpha for any active normal-mapped layer
- **4-cascade CSM shadows** — uses `Common/Shadows.glsl` (`SampleShadowCascaded`, units 7–10) matching `Standard.frag.glsl` and `DeferredLighting.frag.glsl`, plus `uHdrOutput` for linear HDR scene targets
- **Total texture-unit budget:** **8 units** (`0, 1, 2, 3, 7, 8, 9, 10`) — well below the 16-sampler OpenGL ES 3.0 / ANGLE limit, with all 8 normal-mapped layers active on every platform
- **Fallback** — uses standard material color when no layers are defined

**Additional uniforms:**

| Uniform        | Type            | Unit  | Description                                         |
|----------------|-----------------|-------|-----------------------------------------------------|
| `uSplatmap0`   | sampler2D       | 0     | Layer weights 0–3 (RGBA float)                      |
| `uSplatmap1`   | sampler2D       | 1     | Layer weights 4–7 (RGBA float)                      |
| `uLayerAlbedos`| sampler2DArray  | 2     | 8-layer terrain albedo array (`GL_TEXTURE_2D_ARRAY`)|
| `uLayerNormals`| sampler2DArray  | 3     | 8-layer tangent-space normal array (alpha = height) |
| `uShadowMap`, `uShadowMapC1..C3` | sampler2D | 7–10 | 4-cascade CSM shadow maps (`Common/Shadows.glsl`) |
| `uLayerCount`  | int             | —     | Number of active layers (0–8)                       |
| `uTiling0..7`  | float           | —     | Per-layer UV tiling scale                           |
| `uRough0..7` / `uMetal0..7` | float | —  | Per-layer PBR roughness / metallic                  |
| `uHasNormal0..7` | int           | —     | 1 when that layer has a bound normal map            |
| `uHdrOutput`   | int             | —     | 1 when writing linear HDR to `SceneFbo`             |

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
| **SSAO** | Screen-space ambient occlusion pass before the composite (optional via `PostProcessVolume.SSAOEnabled`) |
| **SSR** | Screen-space reflections (ray-marched in deferred pipeline) |
| **TAA** | Temporal anti-aliasing with frame history blend and sharpening |
| **Depth of Field** | 2-pass separable bokeh DoF (`PostProcess/DepthOfField.frag.glsl`) with focus distance, aperture, focal length, and near/far blur |
| **Bloom** | Simplified single-pass bloom — bright pixel extraction + Gaussian-like blur |
| **Fog** | World-position Distance & Height Fog reconstructed from `uDepthTex` with configurable color, density, start/end, and height falloff |
| **Color Grading** | Brightness, Contrast, Saturation, Exposure adjustments |
| **Tone Mapping** | HDR to LDR conversion — Reinhard or ACES filmic methods |
| **Vignette** | Darkened edges — configurable intensity and smoothness |
| **Underwater** | Wave distortion, underwater fog, caustic patterns, color absorption (blue channel boosted, red absorbed). Planet Game View enables this only while `RigidbodyPlayer` is swimming **and** the head/camera is under the water table — surface float and weather fog do not turn it on |

**Local volume blending:** `PostProcessVolume.SampleBlended(...)` evaluates all active global and oriented-box local volumes around the camera position, blending settings smoothly by priority and `BlendDistance`.

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

The engine includes a visual **node-based shader graph** (Shader Graph v2) that compiles to the unified **`Surface` `.shader` format**, then to GPU programs through the same path as hand-authored shaders.

### Architecture
```
ShaderGraph (JSON .shadergraph file)
    │ contains
    ▼
ShaderNodes (typed ports, ShaderGraph v2 nodes)
    │ compiled by
    ▼
ShaderGraphCompiler.Compile() → Surface { surf(...) } .shader text
    │ parsed by
    ▼
ShaderFileParser.GeneratePassSources() → Forward / GBuffer / Shadow
    │ owned by
    ▼
ShaderLibrary (per GL context) → ShaderProgram
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
| **PropertyNode** | Exposes a shader graph property as a data port |
| **SplitNode** / **CombineNode** | Swizzle / pack vector components |
| **UvTransformNode** | Scale / offset UV coordinates |
| **TimeNode** | Elapsed time and sine wave for animation |
| **BlendNode** | Lerp / multiply / add blend modes |

### Custom Shader Files (.shader)

Two authoring styles share one parser:

1. **Surface shader** (preferred) — implement `void surf(in SurfaceInput IN, inout SurfaceOutput OUT)` inside a `Surface { ... }` block; the engine generates Forward, deferred **GBuffer**, and ShadowCaster stages and injects `FrameGlobals` / lighting includes.
2. **Explicit stages** — legacy `VERTEX { ... }` and `FRAGMENT { ... }` blocks for full manual GLSL.

Common header fields: `SHADER "Name"`, `PROPERTIES { _Foo (Type) = default }`, and optional **`RenderState`** (queue, cull, ZWrite, lighting mode).

```
SHADER "My PBR Shader"

PROPERTIES {
    _BaseColor (Color) = (1, 1, 1, 1)
    _Metallic (Float) = 0.5
    _Roughness (Float) = 0.5
    _MainTex (Texture2D)
}

Surface {
    void surf(in SurfaceInput IN, inout SurfaceOutput OUT) {
        OUT.Albedo = _BaseColor.rgb * texture(_MainTex, IN.uv).rgb;
        OUT.Metallic = _Metallic;
        OUT.Roughness = _Roughness;
    }
}
```

`Standard Assets/Shader/Steel PBR.shader` demonstrates a Cook-Torrance BRDF (GGX / Smith / Schlick) in the Surface style.

### CustomShaderCache
Delegates to **`ShaderLibrary`** per GL context:
- Loads and caches `.shader` files and shader-graph output for Forward, GBuffer, and Shadow passes
- `Invalidate(path)` / `ShaderHotReloader` for live reload; failed compiles keep the last valid program
- `TryGetSchema()` exposes PROPERTY metadata for the Inspector without an active GL context

---

## GPU Resource Management

### GLContext
Wraps the Silk.NET `GL` instance obtained from Avalonia's OpenGL control. Detects whether the context is OpenGL ES (ANGLE on Windows) or desktop OpenGL by checking the version string. `ShaderCompileOptions` (`IsES`, `MaxBones`, variant defines) flow into `ShaderPreprocessor` and `ShaderLibrary` for the correct GLSL dialect and skinning limits (48 bones on ES, 128 on desktop).

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

The `Upload()` method auto-detects whether the mesh has bone data and selects the appropriate layout. `Standard.vert.glsl` `uInstanced`: **`0`** = single draw (`uModel`), **`1`** = planet-aligned packed instance attrs (`aInstPosScale` / `aInstUpYaw` / `aInstTint` at locations `5–7`, used by `PlanetGpuTrees` and `PlanetScatterRenderer`), **`2`** = full per-instance world `mat4` (locations `5–8`, used by `SceneRenderer` auto-instancing). `DrawInstanced(instanceVbo, instanceCount, strideBytes, byteOffset)` binds the mode-`1` packed layout with `VertexAttribDivisor = 1` (chunked in groups of 64 on `XBOX_UWP`). `DrawInstancedMatrices(instanceVbo, instanceCount, strideBytes, byteOffset)` binds mode `2` (`4 × vec4`, 64 bytes/instance). Both methods reset divisors to `0` and disable locations `5–8` in a `finally` block (as does `Draw()`) so ANGLE/Xbox never leak instanced divisor state onto non-instanced draws.

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
Compiles and links preprocessed GLSL (with optional `ShaderSourceMap` for diagnostics). Provides:
- Uniform location caching (dictionary lookup)
- Type-safe uniform setters: `SetInt`, `SetFloat`, `SetVec2/3/4`, `SetMat4`, `SetTexture`
- Matrix upload with `transpose=false` (row-major to column-major conversion)
- `BindStandardUniformBlocks()` wires `FrameGlobals`, `LightingGlobals`, and `BoneGlobals` when present
- Optional on-disk **program binary** cache via `BinaryCacheDirectory` (see modular architecture above)

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
   - Rain presets may stretch quads along velocity (`StretchAlongVelocity`, `StretchLength`)
   - Draw instanced billboard quads
3. Particles are rendered with alpha blending enabled

**Overlay pass (Game View / PlayerView):** called after post-processing blits to the default framebuffer. `overlayPass: true` sets the full viewport (`viewportW` × `viewportH`), disables scissor/depth/cull for the pass, and skips depth test per weather emitter (`BiomeWeatherPrecipitation_*`) so rain/snow cover the entire screen. Do **not** move weather particles back into the deferred G-buffer path — mid-pipeline draws inherit the half-width viewport from SSAO/shadow FBOs.

**Scene View during Play:** `SkipPlanetVegetationDraws` hides `BiomeWeatherPrecipitation_*` GOs in the editor viewport while Game View is playing so precipitation is not drawn twice.

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

### ReflectionProbe Cubemap Capture & IBL

`SceneRenderer.CaptureReflectionProbes(...)` renders real 6-face RGBA8 cubemaps for active `ReflectionProbe` components:
- **Budget:** Captures at most **1 probe per frame** (`Baked` on demand / `NeedsCapture`, `Realtime` every `RealtimeRefreshFrames`)
- **Resources:** Reuses a shared FBO/RBO pair per context and generates **GPU mipmaps** for roughness-based specular IBL sampling
- **Feedback safety:** Disables probe cubemap sampling while capturing so a probe never samples itself
- **Fallback:** When no probe cubemap is available, both Forward (`Standard.frag.glsl`) and Deferred (`DeferredLighting.frag.glsl`) fall back to `ProceduralSkyIBL`

### Post-Processing Pass (True HDR `RGBA16F` & Single-Pass Tone Mapping)
1. `SceneFbo` is allocated via `GPUFramebuffer.SetupHdrColorDepth` (`RGBA16F` linear color + depth, `IsHdr = true`). If a driver/ES context lacks float color-buffer renderability, `SetupHdrColorDepth` detects FBO incompleteness and falls back automatically to `RGBA8` (`IsHdr = false`).
2. When rendering into an HDR `SceneFbo`, `SceneRenderer.HdrOutputActive` sets `uHdrOutput = 1` on `Standard.frag.glsl`, `DeferredLighting.frag.glsl`, `PlanetTerrain.frag.glsl`, and Surface Forward shaders so they output linear HDR radiance without premature Reinhard compression. LDR targets (ReflectionProbe cubemaps, previews, and `RGBA8` fallback) keep `uHdrOutput = 0`.
3. `PostProcessVolume.SampleBlended(...)` resolves blended global + oriented-box local settings. Optional pre-composite passes run in order (Deferred pipeline in both Editor `GameView` and standalone `PlayerFrameRenderer` via `GameRenderPipeline`): **SSAO** → **SSR** → **Volumetric Fog** → **TAA** → **2-pass separable bokeh Depth of Field** (`DepthOfField.frag.glsl`). These intermediate FBOs still use `GPUFramebuffer.SetupColorDepth` (`RGBA8`); when `SceneFbo.IsHdr` is true, bright highlights can clamp when a pass reads HDR scene color and writes back through an LDR target (full HDR is preserved through the final `PostProcess.frag.glsl` composite from the HDR `SceneFbo` when effects are off or the chain ends on linear input).
4. Bind `PostProcess.frag.glsl` with scene color and `uDepthTex`, applying: FXAA → Linear HDR 9-tap Bloom (`> uBloomThreshold`) → World-Position Distance & Height Fog → Exposure + Single-Pass Tone Mapping (Reinhard / ACES) → Display-Space Color Grading (Brightness, Contrast, Saturation) → Vignette → Underwater. ACES uses the Narkowicz fit with `x *= 0.6` pre-exposure inside `ACESFilm()` before the rational polynomial so default exposure does not blow out typical PBR highlights.
5. When `SceneFbo.IsHdr` is true and no `PostProcessVolume` (or color grading) is active, `ApplyPostProcessing` automatically applies default single-pass ACES tone mapping (`Exposure = 1.0`) so linear HDR never clips.

### Rendering Debug Views (`RenderDebugMode`)
`SceneRenderer.DebugViewMode` (exposed as a compact **Debug View** dropdown in the Scene View toolbar, shared with Game View) supports 10 live diagnostic modes via `uDebugViewMode` in `Standard.frag.glsl` / `DeferredLighting.frag.glsl` and `uToneMapCompare` in `PostProcess.frag.glsl`:
- `Lit`, `Wireframe` (Desktop `glPolygonMode(FrontAndBack, Line)`; ES/ANGLE falls back to `RenderWireframeGL` without `GL_INVALID_ENUM`), `Albedo`, `Normals`, `Roughness`, `Metallic`, `Depth`, `SSAO`, `CsmCascades` (4-color view-depth tint for CSM split inspection), and `ToneMapCompare` (3-way vertical split in the same frame: Left = None, Middle = Reinhard, Right = ACES).

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
3. Transparent pass (Water, Particles except Game View weather overlay, Decals, World-Space Canvases)
4. Volumetric Fog pass (ray-marched scattering — when enabled)
5. Post-processing (Bloom, Fog, Color Grading, Tone Mapping, Vignette, FXAA)
6. Particle overlay (Game View / PlayerView weather precipitation only — full viewport, after post)
7. UI Overlay pass (CanvasRenderer — ScreenSpaceOverlay canvases)
8. Editor overlays (Grid, Gizmos, Collider wireframes)
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
- **Terrain pass:** `PlanetTerrainFrag` applies atmosphere blend on top of biome lighting (radial slope for cave-wall rock texturing) and uses **`GL_TEXTURE_2D_ARRAY`** + **4-cascade CSM shadows** in **9 texture units** (`0, 1, 2, 3, 4, 7, 8, 9, 10`; 8 units on Xbox without dig atlas): `uBiomeTexArray` (unit 0, 8 top biome layers), `uUnderTexArray` (unit 1, 4 under/cliff layers), **splat atlases** (`uPlanetSplat0/1`, units 2–3), **dig atlas** (`uPlanetDigAtlas`, unit 4), and **4-cascade CSM** (`uShadowMap`, `uShadowMapC1..C3`, units 7–10 via `Common/Shadows.glsl` `SampleShadowCascaded`). While a graph bake is still pending and only a scratch cubemap exists, splat atlases are skipped (`uUsePlanetSplat = 0`) and per-vertex biome blend weights from mesh generation are used instead — so the first dig cannot flatten the planet to a single dirt layer. **Triplanar albedo** blends projection axes by slope: flat ground uses **radial** axes (stable at cube-face poles); steep faces use the **surface normal** so top-layer textures do not smear along cliff walls. Below the crust, atmosphere tint is skipped; inward cave faces keep biome under-color; cavity AO darkens ceilings and enclosed walls. **Weather overlays** (`uWetness` / `uSnowCoverage` / `uWeatherEnabled`) tint and add puddle spec / light snow on the **lit texel** — they never replace grass with a flat color. Wetness is resolved every frame in `SceneRenderer.ResolveWeatherOverlays` (including live `RainIntensity`, not only held `Wetness`). Puddles use tangent-space **FBM** (`wetFbm`) at meter scale — not per-pixel hash noise — with `groundFlat = smoothstep(0.18, 0.58, slope)` so planet mesh normals still receive wet sheen. Pooled water adds spec/Fresnel and a simple sky reflection from `uAtmoHorizonTint` / `uAtmoZenithTint`. On OpenGL ES / ANGLE, avoid GLSL identifiers named `flat` (reserved); the terrain shader uses `groundFlat` instead. Overlays are off while any player is planet-submerged. Missing biome slots default to solid white in `uBiomeTexArray` / `uUnderTexArray` so unused layers never sample garbage
- **Crust-band rendering:** when `CameraBelowCrust` or the camera is clearly inside the planet, backface culling may be disabled for cave interiors; on the outer crust, terrain chunks are frustum-culled like other meshes
- **Planet shadows:** renderable planet leaf meshes are drawn in the shadow depth pass (`RenderPlanetLeafShadows`) unless `SkipPlanetLeafShadows` is set (Scene View during Play). Crust-band `GeneratedCaveMesh` draws in the shadow pass only when `CameraBelowCrust`
- **Atmosphere/cloud proxy:** when no dedicated atmosphere shell mesh exists, the renderer does **not** fall back to drawing all terrain leaves as a cloud shell (removed — that duplicated a full planet draw)
- **Planet water pass:** `PlanetWaterFrag` — atmosphere-driven reflection, per-body tint arrays (slot **6** = lava), shore biome blend, mask discard. **Always prefers** `QuadNode.GeneratedWaterMesh` patches (same LOD grid as terrain, shoreline on the visible edge). The uniform `PlanetWater` orbit shell is a **far-orbit silhouette only** (no chunk patches, camera farther than ~1.6× radius, not inside crust) — it is not a fallback that floods grassland. Rendered **after** planet atmosphere and clouds so haze does not cover the surface. Double-sided, alpha blend, `DepthFunc.Lequal`, **depth write off**, reduced wave amplitude when near the crust. Standalone `PlayerView` compiles the same planet terrain / atmosphere / cloud / water shaders and calls `RefreshLodAroundCamera` plus these passes (the old player only drew heightmap terrain + planar water).
- **Cloud pass:** `PlanetCloudsFrag` is rendered as a dedicated planet pass
- **Prop lighting fill:** when the camera is inside a planet atmosphere shell, Standard and deferred shaders set `uHasPlanetFill` / `uPlanetCenter` so placed props get the same radial sky-fill Lambert term as terrain (fixes props and campfires reading nearly black beside the ground)
- **Planet point lights:** terrain pass binds up to four nearest enabled point lights (`BindNearestPointLights`) for campfires and local lamps on the crust
- **Forward scene opaques:** after planet terrain (and optional dedicated vegetation pass), `SceneRenderer.RenderForwardSceneOpaques` redraws opaque scene meshes (cubes, pickups, props) so they are not buried by the planet pass depth. This runs only when `PlanetTerrain.ActivePlanets.Count > 0`; scenes without planets skip it so deferred shading is not overwritten by a second forward pass.
- **Automatic Static-Mesh GPU Instancing (`SceneRenderer`):** When `SceneRenderer.EnableAutoInstancing` is enabled (default `true`), repeated opaque static props (non-skinned, non-terrain, non-tree, non-water, no custom `.shader`) sharing the same `(Mesh, Material, Color, DoubleSided, InvertFrontFace)` with count `>= SceneRenderer.AutoInstancingMinBatchSize` (default `4`) are automatically batched into a single `GPUMesh.DrawInstancedMatrices` call (`Standard.vert.glsl` `uInstanced = 2`, full per-instance `mat4` across locations `5–8`) across `RenderGPU`, `RenderGBufferPass`, and `RenderForwardSceneOpaques`. Uses pooled per-frame scratch lists, a per-`ResourceCache` dynamic instance VBO released in `ResourceCache.FlushAll()` / `Dispose()`, and increments `Profiler.CountBatch()`.
- **2-Tier GPU-instanced planet forests, rocks & grass:** `SceneRenderer.RenderPlanetVegetationAfterTerrain` renders `PlanetWholePlanetForest` (`PlanetForestCanopy.vert/frag.glsl`, 1 static-VBO draw call per planet at all camera altitudes with smooth Tier 1 ↔ Tier 2 crossfade), `PlanetGpuTrees` (`Standard.vert.glsl` `uInstanced = 1` + `GPUMesh.DrawInstanced`, 1 draw call per unique tree mesh/material batch), `PlanetScatterRenderer` (`Standard.vert.glsl` `uInstanced = 1` + `GPUMesh.DrawInstanced` with CPU frustum culling and lifted Xbox cap of 512 / desktop 1024), and `PlanetGpuGrass` (`PlanetGpuGrass.vert/frag.glsl` with smooth `uAltitudeFade` transition band). All four key their GPU VAOs/VBOs per `ResourceCache` for multi-GL-context safety (`SceneView` vs `GameView`) and release them in `ResourceCache.FlushAll()` / `Dispose()`.

`Skybox` still controls only the world background sky pass. Changing `Skybox` values should not change planet terrain/water/cloud shading.

---

## Stabilization Notes

- **World-space transforms:** `PostProcessVolume` box-distance blending, `SceneRenderer.CaptureReflectionProbes`, `MeshLodGroup.UpdateLOD`, `TreeLOD.UpdateLOD`, `PlayerFrameRenderer` primary light setup, and `AudioManager.UpdateListenerTransform` now use `SceneGraphUtil.AccumulateWorld(...)` instead of local-only `TransformUtil.WorldFromTransform(...)` so child GameObjects compute accurate world positions and orientations.
- **Custom `TextureCube` properties & Surface GBuffer `uEnvMap`:** `MaterialBinder.BindProperties` handles `ShaderPropertyType.TextureCube` by assigning a dedicated custom texture unit (`3, 5, 6, 11–14`) bound to a 1×1 black fallback cubemap (`ResourceCache.GetBlackCubemap()`), preventing `samplerCube` / `sampler2D` collisions on unit 0 under OpenGL ES / ANGLE. `SceneRenderer.DrawSurfaceToGBuffer` also binds `uEnvMap` to unit 15 (matching `DrawWithCustomShader` and disjoint from `MaterialBinder` custom units).
- **ES 3.0 depth texture wrap:** `GPUTexture.CreateDepth` and `CreateDepthStencil24` check `IsESContext()` (not only `#if XBOX_UWP`) so `GL_CLAMP_TO_EDGE` is used on any OpenGL ES 3.0 context (including Windows ANGLE), keeping `GL_CLAMP_TO_BORDER` + white border color on desktop GL.
- **GL state restoration:** Fullscreen post/effect passes (`ApplyPostProcessing`, `RenderDepthOfField`, `RenderDepthTextureToFramebufferDepth`, `RenderSSAO`, `RenderSSAOBlur`, `RenderSSR`, `RenderTemporalAA`, `RenderVolumetricFog`) and planet shell passes (`RenderPlanetClouds`, `RenderPlanetAtmosphere`) now restore `EnableCap.CullFace`; `RenderPlanetTerrain` and `RenderPlanetWater` restore `DepthFunction.Less`.
- **Custom-shader shadows:** `RenderForwardOverlays` passes `CascadedShadow` into `customCtx`, and `DrawWithCustomShader` binds `uShadowMapC1..C3` alongside `uShadowMap` in its single-shadow-map fallback to match `BindShadowMapsToShader`.
- **LOD & tiled light efficiency:** `GameRenderPipeline.UpdateLod` skips disabled GameObject subtrees (`!go.Enabled`) in `WalkTerrain`, `WalkTree`, and `WalkMeshLod`. `TiledLightTextureSystem.BuildAndUpload` reuses grow-only CPU scratch buffers and short-circuits when `localOut.Count == 0` (clearing tile metadata once when dirty).
- **Resilient optional shader init:** `ViewRenderResources.CompileBuiltinShaders` compiles optional post/effect shaders (`DepthOfField`, `VolFog`, `Ssao`, `SsaoBlur`, `Ssr`, `TaaResolve`) with `throwOnInitialError: false` so a single optional shader failure degrades gracefully instead of aborting viewport init. `GameView.OnOpenGlInit` logs initialization exceptions via `Log.Error` in addition to `Debug.WriteLine`.
- **Per-GL-context `ReflectionProbe` cubemaps:** `ReflectionProbe` stores cubemap textures and capture state (`ProbeGpuState`) per `GL` context via `ConditionalWeakTable<GL, ProbeGpuState>` so `SceneView`, `GameView`, and `PlayerView` each capture and sample their own valid cubemap handle instead of sharing a single handle across OpenGL contexts. `ReflectionProbe.GetBestForPosition(worldCam, gl)` also scores probes using world-space transforms (`SceneGraphUtil.AccumulateWorld`).

