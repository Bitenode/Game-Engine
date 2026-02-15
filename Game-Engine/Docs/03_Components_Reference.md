# Game Engine — Components Reference

Every component inherits from `Behavior` and attaches to a `GameObject`. Properties marked `[Persist]` are saved with the scene. Components can declare dependencies with `[Require(typeof(OtherComponent))]` to auto-add required sibling components.

The engine includes **27 built-in component types** organized by category below.

---

## Transform

**Always present** on every GameObject. Cannot be removed or disabled.

| Property   | Type      | Default     | Description                          |
|------------|-----------|-------------|--------------------------------------|
| `Position` | `Vector3` | `(0, 0, 0)` | Local position relative to parent   |
| `Rotation` | `Vector3` | `(0, 0, 0)` | Local Euler rotation in degrees     |
| `Scale`    | `Vector3` | `(1, 1, 1)` | Local scale factor per axis         |

**Implementation details:**
- Property changes automatically trigger `SceneService.NotifyChanged()` to refresh all views
- Each `Vector3` sub-property (X, Y, Z) fires individual change notifications for fine-grained UI binding
- The `Enabled` property always returns `true` and the setter is ignored
- Children inherit parent transforms for hierarchical positioning

---

## Camera

Defines a viewpoint for rendering. The Game View uses the first enabled Camera found in the scene (or the one marked `IsMain`).

| Property       | Type           | Default       | Description                                   |
|----------------|----------------|---------------|-----------------------------------------------|
| `Projection`   | `Projection`   | `Perspective` | Projection mode: `Perspective` or `Orthographic` |
| `FieldOfView`  | `float`        | `60`          | Vertical FOV in degrees (clamped 1-179)       |
| `OrthoSize`    | `float`        | `12`          | Orthographic half-height in world units       |
| `Near`         | `float`        | `0.1`         | Near clipping plane distance (min: 0.0001)    |
| `Far`          | `float`        | `1000`        | Far clipping plane distance (min: Near+0.001) |
| `ViewportX`    | `float`        | `0`           | Normalized viewport left edge (0-1)           |
| `ViewportY`    | `float`        | `0`           | Normalized viewport top edge (0-1)            |
| `ViewportW`    | `float`        | `1`           | Normalized viewport width (0-1)               |
| `ViewportH`    | `float`        | `1`           | Normalized viewport height (0-1)              |
| `Clear`        | `ClearFlags`   | `Skybox`      | What to clear: `Skybox`, `SolidColor`, `DepthOnly`, `Nothing` |
| `Background`   | `Color`        | `#202020`     | Background color when Clear is `SolidColor`   |
| `IsMain`       | `bool`         | `false`       | Marks this as the primary game camera          |

**Methods:**
- `GetViewMatrix()` — computes the view matrix from the Transform's position and rotation
- `GetProjectionMatrix(float aspect)` — computes perspective or orthographic projection matrix
- Registers with `CameraService` on enable, unregisters on disable

---

## Light

Illuminates the scene. Supports directional, point, and spot light types.

| Property       | Type        | Default        | Description                              |
|----------------|-------------|----------------|------------------------------------------|
| `Type`         | `LightType` | `Directional`  | Light type: `Directional`, `Point`, `Spot` |
| `Intensity`    | `float`     | `1.0`          | Brightness multiplier                     |
| `Range`        | `float`     | `10`           | Point/Spot light falloff range (world units) |
| `InnerAngle`   | `float`     | `25`           | Spot light inner cone angle (degrees)     |
| `OuterAngle`   | `float`     | `35`           | Spot light outer cone angle (degrees)     |
| `Color`        | `Color`     | `White`        | Light color tint                          |
| `CastShadows`  | `bool`      | `true`         | Whether this light casts shadows          |

**Static registry:** All enabled lights are tracked in `Light.AllLights` (initial capacity: 16).

**Methods:**
- `GetWorldDirection()` — computes world-space direction from Transform rotation
- `GetWorldPosition()` — returns world-space position
- `GetColorRGB()` — returns normalized RGB multiplied by intensity

**Directional lights** use the Transform's forward direction and cast shadows via a 4096x4096 shadow map with PCF soft shadows.

**Point lights** illuminate a sphere of influence defined by `Range`.

**Spot lights** project a cone of light defined by `InnerAngle` (full intensity) and `OuterAngle` (falloff to zero).

---

## MeshFilter

Holds a reference to mesh geometry data. Paired with `MeshRenderer` for rendering.

| Property         | Type     | Default | Description                               |
|------------------|----------|---------|-------------------------------------------|
| `Mesh`           | `Mesh`   | `null`  | Runtime mesh data (vertices, normals, UVs, triangles) |
| `ModelPath`      | `string` | `""`    | Project-relative path to the source 3D model file |
| `ModelPartIndex` | `string` | `""`    | Which part of a multi-mesh model (0, 1, 2...) |
| `TargetPaths`    | `List<string>` | — | Target paths for mesh references       |

When a Behavior with `[Require(typeof(MeshFilter))]` is added, a default cube mesh is created if `Mesh` is null.

---

## MeshRenderer

Controls how a mesh is rendered. References materials and configures rendering state.

| Property          | Type           | Default  | Description                               |
|-------------------|----------------|----------|-------------------------------------------|
| `Color`           | `Color`        | `White`  | Tint color (multiplied with material)     |
| `Material`        | `Material`     | Default  | Primary surface material (PBR properties + textures) |
| `MaterialPaths`   | `List<string>` | `[]`     | Project-relative paths to `.material` files |
| `Wireframe`       | `bool`         | `false`  | Render as wireframe lines only            |
| `LineWidth`       | `double`       | `1.0`    | Wireframe line width                       |
| `CastShadows`     | `bool`         | `true`   | Whether this mesh casts shadows            |
| `ReceiveShadows`  | `bool`         | `true`   | Whether this mesh receives shadows         |
| `DoubleSided`     | `bool`         | `false`  | Disable backface culling (render both sides) |
| `InvertFrontFace` | `bool`         | `false`  | Swap front/back face winding order        |

**Runtime properties:**
- `ResolvedMaterials` — runtime cache of loaded `Material` objects from `MaterialPaths`

**Methods:**
- `OnEnable()` — resolves materials from paths
- `ResolveMaterials()` — loads `.material` files using `ProjectService`
- `TryLoadRuntimeMaterial(path)` — loads a material asset or simple JSON format
- `DefaultMaterial()` — creates a default unlit white material

**Material resolution** supports both the full asset pipeline format and a simpler JSON format for compatibility.

---

## SkinnedMeshRenderer

GPU-accelerated bone skinning for skeletal animation. Replaces `MeshRenderer` for animated meshes.

| Property | Description |
|----------|-------------|
| Bone matrices | Computed per-frame from `Animator` bone transforms |
| Skeleton | Recovered from `Mesh` bone data |

**Implementation details:**
- Uses a skinned vertex layout: 64 bytes per vertex (Position + Normal + UV + BoneIndices + BoneWeights)
- Bone matrices are uploaded as uniform arrays to the GPU shader
- Integrates with the `Animator` component for pose computation
- Falls back to static rendering when no animation is active

---

## Material

Not a component but defines surface properties used by `MeshRenderer`. Uses a physically-based rendering (PBR) model.

| Property       | Type       | Default         | Description                               |
|----------------|------------|-----------------|-------------------------------------------|
| `BaseColor`    | `Color`    | `White (1,1,1,1)` | Base albedo color (RGBA)               |
| `Metallic`     | `float`    | `0.0`           | 0 = dielectric, 1 = full metal           |
| `Roughness`    | `float`    | `0.5`           | 0 = mirror smooth, 1 = fully rough       |
| `Transparent`  | `bool`     | `false`         | Enable alpha blending                     |
| `AlphaCutoff`  | `float`    | `0.5`           | Pixels below this alpha are discarded     |
| `Textures`     | `List`     | `[]`            | Texture slots with usage assignments      |

### Texture Slots
Each texture slot has:
- **Texture** — the loaded `Texture2D` image data
- **Usage** — `Albedo`, `Normal`, `Metallic`, `Roughness`, `Occlusion`, `Emission`, `Opacity`, `Height`
- **SourcePath** — project-relative path to the image file

See the Materials and Textures document for full details.

---

## Colliders

All colliders inherit from a `Collider` base class and provide `GetWorldAABB()` for broad-phase collision detection.

### BoxCollider
Axis-aligned box collision shape.

| Property | Type      | Default     | Description                    |
|----------|-----------|-------------|--------------------------------|
| `Center` | `Vector3` | `(0, 0, 0)` | Offset from the Transform origin |
| `Size`   | `Vector3` | `(1, 1, 1)` | Box dimensions (width, height, depth) |

**Methods:**
- `GetLocalCorners(Vector3[])` — computes the 8 local-space corners of the box
- `GetWorldAABB()` — computes the world-space axis-aligned bounding box

**Use cases:** Walls, floors, crates, doors, platforms, triggers.

### CapsuleCollider
Capsule shape (cylinder with hemispherical caps), primarily used for character controllers.

| Property    | Type      | Default     | Description                    |
|-------------|-----------|-------------|--------------------------------|
| `Center`    | `Vector3` | `(0, 1, 0)` | Offset from Transform          |
| `Radius`    | `float`   | `0.4`       | Capsule radius (min: 0.0001)   |
| `Height`    | `float`   | `2.0`       | Total height including caps (clamped to >= 2*Radius) |
| `Direction` | `Axis`    | `Y`         | Up axis: `X`, `Y`, or `Z`     |

**Methods:**
- `GetLocalCapsule(out a, out b, out r)` — returns the two capsule endpoints and clamped radius
- `GetWorldAABB()` — computes world AABB with non-uniform scale support

**Use cases:** Player characters, NPCs, cylindrical objects.

### MeshCollider
Uses mesh geometry for precise triangle-based collision detection.

| Property              | Type           | Default | Description                              |
|-----------------------|----------------|---------|------------------------------------------|
| `TargetPaths`         | `List<string>` | `[]`    | Scene paths to target MeshFilters (`"path#mf:ordinal"` format) |
| `BindToTargetTransform` | `bool`       | `true`  | Use target's world transform for collision |
| `Mesh`                | `Mesh`         | `null`  | Manual override collision mesh            |

**Methods:**
- `AddTarget(MeshFilter)` / `RemoveTarget(MeshFilter)` / `ClearTargets()` — manage collision targets
- `EnumerateTargetMeshesWorld()` — yields `(Mesh, Matrix4x4)` pairs for all target meshes in world space
- `GetWorldAABB()` — computes union AABB of all target meshes

**Fallback behavior:** If no targets are specified, uses all `MeshFilter` components on the same GameObject.

**Use cases:** Complex static geometry (buildings, terrain, irregular shapes).

---

## CharacterController

Physics-based character movement controller with gravity, ground detection, slope limiting, step climbing, coyote time, and continuous collision detection. Does **not** use rigid body dynamics — uses sweep-and-slide collision resolution instead.

| Property             | Type    | Default | Description                             |
|----------------------|---------|---------|-----------------------------------------|
| `UseGravity`         | `bool`  | `true`  | Apply gravity to vertical velocity      |
| `Gravity`            | `float` | `9.81`  | Gravity acceleration (m/s²)             |
| `JumpHeight`         | `float` | `1.2`   | Jump height in meters                   |
| `StepUpMax`          | `float` | `0.5`   | Maximum step height to auto-climb       |
| `GroundSnapDistance`  | `float` | `0.7`   | Distance to snap character to ground    |
| `WallPush`           | `float` | `0`     | Force to push away from walls           |
| `MaxSlopeAngleDeg`   | `float` | `55`    | Maximum walkable slope angle (degrees)  |
| `CoyoteTimeSeconds`  | `float` | `0.12`  | Grace period after leaving ground to still allow jumping |
| `FallbackCapsuleRadius` | `float` | `0.35` | Capsule radius if no CapsuleCollider   |
| `FallbackCapsuleHeight` | `float` | `1.8` | Capsule height if no CapsuleCollider   |
| `UnstickIgnoreHuge`  | `bool`  | `true`  | Ignore oversized colliders during unstick |
| `UnstickMaxExtent`   | `float` | `5`     | Max collider extent for unstick checks  |
| `UnstickSkipIfInside`| `bool`  | `true`  | Skip unstick if fully inside a collider |
| `PushForce`          | `float` | `3.0`   | Force applied to pushed Rigidbody objects |

**Read-only runtime properties:**
- `IsGrounded` — whether the character is touching the ground
- `GroundNormal` — the surface normal of the ground beneath
- `VerticalVelocity` — current vertical velocity (positive = up)
- `CapsuleRadius` / `CapsuleHalfCylinder` — resolved capsule dimensions

**Events:**
- `OnTriggerEnter(Collider)` — fired when entering a trigger volume
- `OnTriggerStay(Collider)` — fired each frame while inside a trigger
- `OnTriggerExit(Collider)` — fired when leaving a trigger volume

**Core method: `Simulate(Vector3 desiredHorizontalDelta, bool jump)`**

Called from `FixedUpdate`. Performs the full physics simulation step:
1. Apply gravity to vertical velocity
2. Ground detection using a 5-sample ring probe (5 rays at `CapsuleRadius * 0.6f` around the center)
3. Ceiling detection via upward raycast
4. Jump initiation with coyote time grace period
5. Continuous collision detection (`CCD_AdvanceAndSlide`) with up to 4 iterations
6. Horizontal AABB unstick to resolve penetrations
7. Wall detection via forward raycast
8. Rigidbody push (applies `PushForce` to touched `Rigidbody` components)
9. Trigger volume detection (`OnTriggerEnter/Stay/Exit`)

**Constants:**
- Ground probe ring radius: `CapsuleRadius * 0.6f` (minimum `0.05f`)
- Ray start offset: `StepUpMax + 0.002f` (minimum `0.2f`)
- Ground tolerance: `±0.02f`
- CCD step length: `CapsuleRadius / 4f` (minimum `0.01f`)
- CCD max iterations: `4`
- Skin thickness: `radius * 0.2f` (minimum `0.01f`)

**Requires:** CapsuleCollider (auto-added via `[Require]`)

---

## PlayerMovement

First-person / third-person player controller integrating input, camera control, and physics.

| Property             | Type      | Default          | Description                        |
|----------------------|-----------|------------------|------------------------------------|
| `MoveSpeed`          | `float`   | `4`              | Walking speed (units/sec)          |
| `SprintMultiplier`   | `float`   | `1.75`           | Sprint speed multiplier            |
| `LookSensitivity`    | `float`   | `90`             | Mouse look speed (deg/unit/sec)    |
| `FirstPerson`        | `bool`    | `true`           | First-person mode (vs third-person) |
| `FirstPersonOffset`  | `Vector3` | `(0, 1.7, 0)`   | Camera offset in first-person      |
| `ThirdPersonOffset`  | `Vector3` | `(0, 1.7, -3.5)` | Camera offset in third-person     |
| `CameraFollowLerp`   | `float`   | `12`             | Third-person camera smoothing      |
| `RotateBodyWithLook` | `bool`    | `true`           | Body follows camera yaw            |
| `TurnBodyWhileMoving`| `bool`    | `false`          | Body turns only when moving        |
| `JumpBufferSeconds`  | `float`   | `0.12`           | Jump input buffer (grace period)   |
| `DebugBypassMotor`   | `bool`    | `false`          | Teleport without physics           |

**Controls (default bindings):**
| Action | Input |
|--------|-------|
| Move | WASD / Arrow keys (Horizontal + Vertical axes) |
| Look | Mouse movement (Mouse X + Mouse Y axes) |
| Jump | Space (Jump action, buffered for `JumpBufferSeconds`) |
| Sprint | Left Shift (Sprint action) |
| Fire | Left Mouse Button (Fire1 action) |

**Pitch clamp:** -89° to 89° (prevents gimbal lock at poles)

**Lifecycle:**
- `Awake()` — resolves the Camera component, initializes yaw/pitch from Transform
- `Update()` — collects input, updates mouse look, drives camera position
- `FixedUpdate()` — calls `CharacterController.Simulate()` with the fixed delta time

**Requires:** CharacterController, CapsuleCollider (auto-added via `[Require]`)

---

## Rigidbody

Physics body component that can receive forces from `CharacterController` pushes.

Provides velocity and mass for dynamic objects that interact with the character controller's `PushForce` system.

---

## Skybox

Defines the sky background, ambient lighting, and sun direction for the scene.

| Property        | Type       | Default   | Description                              |
|-----------------|------------|-----------|------------------------------------------|
| `Top`           | `Color`    | `#1f1f1f` | Sky gradient top color                   |
| `Bottom`        | `Color`    | `#0a0a0a` | Sky gradient bottom color                |
| `Ambient`       | `float`    | `0.90`    | Global ambient light level (0-1)         |
| `Texture`       | `Texture2D`| `null`    | Equirectangular sky texture              |
| `TexturePath`   | `string`   | `null`    | Path to sky texture file                 |
| `TextureBlend`  | `float`    | `1.0`     | Blend between gradient and texture (0-1) |
| `Yaw`           | `float`    | `0`       | Sky rotation around Y axis (degrees)     |
| `SunElevation`  | `float`    | `45`      | Sun angle above horizon (degrees, 0=horizon, 90=overhead) |

**47 built-in skybox textures** are included in `Standard Assets/Skybox/` (sky_01_2k.png through sky_47_2k.png).

The Sky shader renders:
- A gradient blend between `Top` and `Bottom` colors
- An equirectangular texture mapped to the sky sphere (blended by `TextureBlend`)
- A sun glow effect at the position defined by `SunElevation` and `Yaw`

The `SunElevation` and `Yaw` properties also control the direction of the main directional light for shadow mapping.

---

## Terrain

Heightmap-based terrain with multi-material splatmap painting, chunking for performance, LOD, tree painting, and O(1) heightmap collision. See the Terrain System document for full tool and painting details.

| Property            | Type           | Default   | Description                              |
|---------------------|----------------|-----------|------------------------------------------|
| `ResX`              | `int`          | `129`     | Height samples along X (min: 2)          |
| `ResZ`              | `int`          | `129`     | Height samples along Z (min: 2)          |
| `SizeX`             | `float`        | `100`     | World width in X                         |
| `SizeZ`             | `float`        | `100`     | World depth in Z                         |
| `HeightScale`       | `float`        | `20`      | Height multiplier in Y                   |
| `Heights`           | `float[]`      | `new float[129*129]` | Heightmap data (0 to 1 range, row-major) |
| `Holes`             | `bool[]`       | `null`    | Per-vertex hole mask (null = no holes)   |
| `UseChunking`       | `bool`         | `true`    | Enable chunk-based rendering             |
| `ChunkSize`         | `int`          | `65`      | Vertices per chunk edge (typically pow2+1) |
| `LodLevels`         | `int`          | `3`       | LOD levels per chunk (1-3)               |
| `Layers`            | `List<TerrainLayer>` | `[]` | Terrain texture layers (up to 8)         |
| `Splatmap0`         | `float[]`      | `null`    | Per-vertex layer weights for layers 0-3 (RGBA, length = ResX*ResZ*4) |
| `Splatmap1`         | `float[]`      | `null`    | Per-vertex layer weights for layers 4-7  |
| `TerrainAssetPath`  | `string`       | `""`      | Path to `.terrain.json` data file        |
| `AutoLoadOnStart`   | `bool`         | `true`    | Load terrain data on startup             |
| `AutoSaveOnChange`  | `bool`         | varies    | Auto-save after every modification       |

**Key methods:**
- `SetHeight(x, z, h)` / `GetHeight(x, z)` — individual height sample access
- `SampleHeightWorld(worldX, worldZ, out worldY, out normal)` — O(1) bilinear interpolation height query
- `RebuildMesh()` — full terrain mesh rebuild
- `RebuildArea(minVx, minVz, maxVx, maxVz)` — partial rebuild for brush strokes
- `RebuildDirtyChunks(rebuildCollision)` — rebuild only modified chunks
- `UpdateLOD(cameraPos)` — select LOD level per chunk based on camera distance
- `FinalizeStroke()` — rebuild collision mesh at full resolution after a brush stroke
- `Save()` / `Load()` — persist to/from `.terrain.json` file
- `EnsureSplatmaps()` — allocate splatmap arrays if needed
- `MarkSplatmapDirty()` / `ClearSplatmapDirty()` — GPU re-upload tracking
- `MarkChunksDirty(minVx, minVz, maxVx, maxVz)` / `MarkAllChunksDirty()` — chunk dirty tracking

**LOD thresholds:**
- Near (< `chunkWorldSize * 4f`) → LOD 0 (full detail)
- Medium → LOD 1 (half detail, every other vertex)
- Far (> `chunkWorldSize * 10f`) → LOD 2 (quarter detail, every fourth vertex)

**Requires:** MeshFilter, MeshRenderer, MeshCollider (auto-added via `[Require]`)

---

## Tree

Procedural or imported tree/vegetation component with wind animation support and LOD.

### Procedural Parameters
| Property             | Type           | Default   | Description                        |
|----------------------|----------------|-----------|------------------------------------|
| `TrunkHeight`        | `float`        | `3`       | Trunk height                       |
| `TrunkRadiusBottom`  | `float`        | `0.25`    | Bottom trunk radius                |
| `TrunkRadiusTop`     | `float`        | `0.12`    | Top trunk radius                   |
| `TrunkSegments`      | `int`          | `8`       | Trunk circumference detail         |
| `Shape`              | `CanopyShape`  | `Sphere`  | Canopy shape: `Sphere`, `Cone`, `LayeredCone` |
| `CanopyRadius`       | `float`        | `2`       | Canopy width                       |
| `CanopyHeight`       | `float`        | `2.5`     | Canopy height                      |
| `CanopySegments`     | `int`          | `10`      | Canopy detail level                |
| `CanopyLayers`       | `int`          | `3`       | Layers for LayeredCone shape       |

### Import Mode Parameters
| Property              | Type     | Default | Description                        |
|-----------------------|----------|---------|------------------------------------|
| `ModelPath`           | `string` | `""`    | Path to imported 3D model (overrides procedural) |
| `Lod1Path`            | `string` | `""`    | Path to LOD 1 model               |
| `Lod2Path`            | `string` | `""`    | Path to LOD 2 model               |
| `TrunkMaterialPath`   | `string` | `""`    | Material for trunk                 |
| `CanopyMaterialPath`  | `string` | `""`    | Material for canopy                |

### Wind Parameters
| Property        | Type    | Default | Description                        |
|-----------------|---------|---------|------------------------------------|
| `WindSway`      | `float` | `0.6`   | Wind animation intensity (0-1)     |
| `WindSpeed`     | `float` | `1`     | Wind animation speed multiplier    |
| `IsVegetation`  | `bool`  | `true`  | Enable wind vertex animation       |

**Modes:**
- **Procedural** — generates trunk + canopy meshes from parameters. Three canopy shapes: Sphere (UV sphere), Cone (single cone), LayeredCone (stacked overlapping cones)
- **Imported** — when `ModelPath` is set, loads a 3D model file instead of generating procedurally

**Methods:**
- `MarkDirty()` — mark for rebuild
- `RebuildTree()` — rebuild the tree mesh
- `GenerateProceduralTree(detail)` — generate procedural mesh (0-1 detail multiplier for LOD)

**Wind animation:** Trees marked `IsVegetation = true` receive vertex-based wind displacement in the standard shader, using time-based sine waves modulated by vertex height.

**Requires:** MeshFilter, MeshRenderer, TreeLOD (auto-added via `[Require]`)

---

## TreeLOD

Automatic level-of-detail for tree/vegetation objects based on camera distance, with billboard impostor support.

| Property          | Type         | Default | Description                        |
|-------------------|--------------|---------|------------------------------------|
| `Lod0`            | `Mesh`       | `null`  | Full detail mesh                   |
| `Lod1`            | `Mesh`       | `null`  | Medium detail mesh                 |
| `Lod2`            | `Mesh`       | `null`  | Low detail mesh                    |
| `Lod1Start`       | `float`      | `15`    | Distance (meters) to switch to LOD 1 |
| `Lod2Start`       | `float`      | `30`    | Distance to switch to LOD 2       |
| `ImpostorStart`   | `float`      | `55`    | Distance to switch to billboard    |
| `BillboardAtlas`  | `Texture2D`  | `null`  | Billboard texture atlas            |
| `AtlasCols`       | `int`        | `8`     | Number of yaw slices in atlas      |
| `AtlasRows`       | `int`        | `1`     | Number of rows in atlas            |
| `BillboardHeight` | `float`      | `6`     | Billboard quad height              |
| `BillboardWidthMul`| `float`     | `0.6`   | Billboard width multiplier         |
| `UprightYAxis`    | `bool`       | `true`  | Keep billboard upright on Y axis   |

**Runtime properties:**
- `CurrentLod` — 0=full, 1=medium, 2=low, 3=billboard
- `IsBillboard` — true when `CurrentLod == 3`
- `LastYawSlice` — current atlas slice from camera angle

**Methods:**
- `PickMeshOrNullForBillboard(dist, fallback)` — returns the appropriate LOD mesh or null (for billboard)
- `ComputeYawSlice(camPos, objPos)` — computes the atlas column from camera-to-object yaw angle
- `UpdateLOD(cameraPos)` — updates LOD level based on camera distance

---

## Water

Water surface rendering with Gerstner wave displacement, Fresnel-based transparency, foam, and underwater effects.

**Key features:**
- **Two-layer Gerstner waves** — realistic ocean wave displacement computed in the vertex shader
- **Fresnel-based transparency** — more transparent when looking straight down, more reflective at glancing angles
- **Foam rendering** — white foam on wave crests and shore boundaries
- **Sky reflection** — reflects the skybox for realistic water surface appearance
- **Specular highlights** — sun specular on the water surface
- **Underwater post-processing** — color absorption, distance fog, caustic patterns, and wave distortion when the camera is below the water surface

The Water shader computes wave normals per-vertex and passes them to the fragment shader for realistic specular and Fresnel calculations.

---

## ParticleEmitter

Billboard particle system with emission shapes, sub-emitters, and preset configurations.

**Key features:**
- **Billboard particles** — always face the camera
- **Instanced rendering** — efficient GPU drawing via uniform arrays
- **Emission shapes** — `Sphere`, `Cone`, `Box` with configurable dimensions
- **Sub-emitters** — spawn additional particles on collision, death, or other events
- **Soft circular particles** — alpha falloff from center to edge for smooth appearance

### Presets
| Preset   | Description                                |
|----------|--------------------------------------------|
| `Fire`   | Warm orange/yellow upward particles         |
| `Smoke`  | Gray billowing particles with slow rise     |
| `Sparks` | Bright fast-moving particles with gravity   |
| `Rain`   | Downward-falling elongated particles        |
| `Snow`   | Slow-falling white particles with drift     |
| `Dust`   | Small slowly-dispersing particles           |

---

## PostProcessVolume

Post-processing effects applied as a full-screen pass after scene rendering. Supports priority-based volume blending with global and local scope.

**Available effects:**
| Effect | Description |
|--------|-------------|
| **Bloom** | Bright areas glow and bleed into surrounding pixels |
| **Fog** | Distance-based atmospheric fog |
| **Color Grading** | Brightness, Contrast, Saturation, Exposure adjustments |
| **Tone Mapping** | HDR to LDR conversion (Reinhard or ACES methods) |
| **Vignette** | Darkened edges around the screen |
| **FXAA** | Fast approximate anti-aliasing |
| **Underwater** | Distortion, fog, caustics, and color absorption when camera is below water |

**Features:**
- **Priority system** — higher-priority volumes override lower ones
- **Global/Local scope** — global volumes affect the entire scene; local volumes affect a defined region
- **Underwater integration** — automatically activates underwater effects when the camera is below a Water surface

---

## AudioSource

3D spatial audio emitter with distance attenuation, stereo panning, and channel routing.

| Property        | Type           | Default  | Description                        |
|-----------------|----------------|----------|------------------------------------|
| `Volume`        | `float`        | `1.0`    | Playback volume (0-1)              |
| `Pitch`         | `float`        | `1.0`    | Playback pitch multiplier          |
| `Loop`          | `bool`         | `false`  | Loop the audio clip                |
| `PlayOnAwake`   | `bool`         | `false`  | Start playback when entering play mode |
| `Mute`          | `bool`         | `false`  | Mute the audio source              |
| `SpatialBlend`  | `float`        | `1.0`    | 0 = 2D (no spatialization), 1 = full 3D |
| `MinDistance`    | `float`        | varies   | Distance at which volume starts attenuating |
| `MaxDistance`    | `float`        | varies   | Distance at which volume reaches minimum |
| `DopplerLevel`  | `float`        | varies   | Doppler effect intensity           |
| `Channel`       | —              | —        | Audio channel: Master, Music, or SFX |

**Volume computation:** Final volume = source volume × channel volume × master volume × distance attenuation

**Pan computation:** Stereo panning based on the dot product of the listener's right vector and the source-to-listener direction.

**Runtime:** Uses `AudioHandle` for playback control (play, pause, resume, stop).

---

## AudioListener

Audio listener component that provides the reference point for spatial audio calculations. Only one `AudioListener` should be active in the scene at a time.

**Provides:**
- World position for distance attenuation calculations
- World orientation (forward + right vectors) for stereo panning

---

## Animator

Skeletal animation state machine with bone-based animation support and GPU skinning integration.

**Features:**
- **Animation states** — each state references a bone animation clip
- **State transitions** — switch between animation states
- **Bone matrix computation** — computes per-bone transformation matrices each frame
- **GPU skinning integration** — passes bone matrices to `SkinnedMeshRenderer` for vertex deformation
- **Flexible bone matching** — handles bone name prefixes (e.g., "mixamorig:") for cross-format compatibility

Animation clips are imported automatically from 3D model files (FBX, glTF) and stored as `.boneanim` files. The `Animator` component is auto-created during model import when animations are detected.

---

## Decal

Decal projection component for rendering textures onto surfaces.

Decals project a texture from a box volume onto underlying geometry, useful for bullet holes, footprints, graffiti, blood splatters, and other surface detail.

---

## NavMeshAgent

Navigation mesh agent component for AI pathfinding on baked navigation meshes.

---

## VegetationInstance

Vegetation placement component for grass, flowers, and other small vegetation instances scattered across terrain or other surfaces.

---

## Custom Script Components

Any C# class inheriting from `Behavior` in the project's `Assets/` or `Packages/` folders becomes a component that can be added to GameObjects via the "Add Component" dropdown in the Inspector.

### Example
```csharp
using Game_Engine.Core;
using System.Numerics;

public class Spinner : Behavior
{
    [Persist] public float Speed { get; set; } = 90f;
    [Persist] public string Label { get; set; } = "Hello";
    [Persist] public bool Active { get; set; } = true;

    public override void Update()
    {
        if (!Active) return;

        var rot = gameObject.Transform.Rotation;
        rot.Y += Speed * Time.DeltaTime;
        gameObject.Transform.Rotation = rot;
    }
}
```

### Supported Persist Types
`string`, `int`, `float`, `bool`, `Vector3`, `Color`, enums, `List<T>`, `float[]`

### Available APIs in Scripts
```csharp
// Current game object
gameObject.Name
gameObject.Transform.Position / Rotation / Scale
gameObject.Children
gameObject.Parent

// Components
var cam = GetComponent<Camera>();
var renderer = GetOrAddComponent<MeshRenderer>();
bool hasMesh = HasComponent<MeshFilter>();

// Input (play mode only)
float h = Input.GetAxis("Horizontal");
float v = Input.GetAxis("Vertical");
bool jumped = Input.GetActionDown("Jump");
Vector2 mouse = Input.MouseDelta;

// Time
float dt = Time.DeltaTime;
float elapsed = Time.ElapsedTime;

// Logging
LogInfo("Something happened");
LogWarning("Watch out");
LogError("Something broke");
LogSuccess("Completed!");
LogDebug("Debug info");

// Scene
SceneService.Root   // top-level GameObjects
SceneService.Add(newGameObject);
SceneService.Remove(gameObject);
```
