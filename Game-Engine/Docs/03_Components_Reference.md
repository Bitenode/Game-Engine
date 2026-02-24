# Game Engine — Components Reference

Every component inherits from `Behavior` and attaches to a `GameObject`. Properties marked `[Persist]` are saved with the scene. Components can declare dependencies with `[Require(typeof(OtherComponent))]` to auto-add required sibling components.

A component's `IsActiveAndEnabled` property is `true` only when both its own `Enabled` flag and the owning GameObject's `IsActiveInHierarchy` are `true`. All engine systems (game loop, rendering, physics queries, scene queries) use `IsActiveAndEnabled` to skip components on disabled GameObjects. Disabling a GameObject effectively silences all its components without changing their individual `Enabled` flags.

The engine includes **34+ built-in component types** organized by category below.

### Component Categories

Components are assigned to categories using the `[ComponentCategory("Name")]` attribute. The Inspector's **+ Add Component** button opens a hierarchical popup menu where each category is a submenu. Components without the attribute default to the **Misc** category.

| Category | Components | Directory |
|----------|-----------|-----------|
| **Rendering** | Camera, Light, MeshFilter, MeshRenderer, SkinnedMeshRenderer | `Core/Component/Rendering/` |
| **Physics** | Collider, BoxCollider, CapsuleCollider, MeshCollider, PlanetCollider, CharacterController, PlayerMovement, Rigidbody, RigidbodyPlayer | `Core/Component/Physics/` |
| **Animation** | Animator, IKConstraint | `Core/Component/Animation/` |
| **Audio** | AudioSource, AudioListener, ReverbZone | `Core/Component/Audio/` |
| **Effects** | Decal, ParticleEmitter, PostProcessVolume | `Core/Component/Effects/` |
| **Environment** | Skybox, Terrain, PlanetTerrain, Tree, TreeLOD, VegetationPainter, Water | `Core/Component/Environment/` |
| **Navigation** | NavMeshAgent | `Core/Component/Navigation/` |
| **Networking** | NetworkIdentity, NetworkTransform, NetworkAnimator | `Core/Component/Networking/` |
| **2D** | Camera2D, SpriteRenderer, Tilemap | `Core/Component/2D/` |
| **UI** | Canvas, RectTransform, UIElement, UIText, UIImage, UIButton, UIPanel, UISlider, UIToggle, UIInputField | `Core/Component/UI/` |
| **AI** | BehaviorTreeRunner | `Core/AI/` |
| **Dialogue** | DialogueRunner | `Core/Dialogue/` |
| **Timeline** | TimelinePlayer | `Core/Timeline/` |

Custom script components compiled from `Assets/` or `Packages/` appear in a separate **Scripts** submenu.

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
| `WorldUp`      | `Vector3`      | `(0, 1, 0)`   | Runtime up-vector used by `GetViewMatrix()` for planet-aware horizon alignment |

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

### PlanetCollider
Planet-specific collider shell used for broad-phase queries and debug visualization.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RadiusOverride` | `float` | `0` | Optional forced radius; when 0, radius comes from attached `PlanetTerrain` config |

**Runtime properties:**
- `MaxRadius` — base radius plus max biome amplitude (or `RadiusOverride`)
- `BaseRadius` — planet base radius without biome displacement
- `EffectiveRadius` — compatibility alias for `MaxRadius`
- `WorldCenter` — center from the planet GameObject world transform

**Behavior:**
- Provides world AABB for broad-phase collision systems
- Actual surface-conforming collision uses `PlanetTerrain.SampleSurfaceRadius(...)` inside physics components
- Gizmo drawing uses base/max radii to show inner/outer collision shell bounds

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

Physics body component with force/impulse integration, trigger events, collider response, underwater behavior, and planet-relative gravity support.

| Property          | Type    | Default | Description |
|-------------------|---------|---------|-------------|
| `Mass`            | `float` | `1`     | Body mass used for force integration |
| `Drag`            | `float` | `0.05`  | Linear damping |
| `AngularDrag`     | `float` | `0.1`   | Angular damping |
| `UseGravity`      | `bool`  | `true`  | Applies gravity each fixed tick |
| `IsKinematic`     | `bool`  | `false` | Skip simulation and move manually |
| `Bounciness`      | `float` | `0.3`   | Bounce response on impact |
| `Friction`        | `float` | `0.5`   | Tangential energy loss on impact |
| `FreezeRotation`  | `bool`  | `false` | Disable angular rotation integration |
| `FreezePositionX` | `bool`  | `false` | Lock X translation |
| `FreezePositionY` | `bool`  | `false` | Lock Y translation |
| `FreezePositionZ` | `bool`  | `false` | Lock Z translation |

**Runtime state:**
- `Velocity`, `AngularVelocity`
- `IsGrounded`, `GroundNormal`
- `IsUnderwater`, `UnderwaterDepth`
- `LocalUp` — world up relative to nearest planet (falls back to global +Y)

**Planet integration:**
- Finds nearest active `PlanetTerrain`
- Applies gravity along `-LocalUp`
- Grounds against `PlanetTerrain.SampleSurfaceRadius(...)`

**Events:**
- `OnTriggerEnter(Collider)`, `OnTriggerStay(Collider)`, `OnTriggerExit(Collider)`
- `OnCollisionEnter(Collider, Vector3 normal)`

**Methods:**
- `AddForce(force)`, `AddImpulse(impulse)`, `AddForceAtPosition(force, worldPoint)`
- `WakeUp()` — wakes sleeping rigidbodies

---

## PlanetTerrain

Planet terrain component for cube-sphere planetary worlds with transvoxel chunking and biome graph-driven generation.

| Property                | Type    | Default | Description |
|-------------------------|---------|---------|-------------|
| `Radius`                | `float` | `1000`  | Base planet radius |
| `SeaLevelFraction`      | `float` | `0.25`  | Sea level fraction of terrain min/max range |
| `MaxLodDepth`           | `int`   | `6`     | Maximum quadtree LOD depth |
| `ChunkSize`             | `int`   | `32`    | Chunk mesh/voxel resolution |
| `LodDistanceMultiplier` | `float` | `5.0`   | LOD split tuning |
| `Seed`                  | `int`   | `42`    | Planet generation seed |
| `EnableCaves`           | `bool`  | `true`  | Enable cave carving |
| `EnableWater`           | `bool`  | `true`  | Spawn ocean shell mesh |
| `MaxActiveChunks`       | `int`   | `120`   | Hard cap of active chunks |
| `BiomeGraphPath`        | `string`| `""`    | `.biomegraph` path to load/auto-apply |

**Key methods:**
- `TryLoadBiomeGraph()` — load, compile, and apply graph data
- `ApplyGraphResult(result, graphPath)` — apply graph output from the biome editor
- `SampleSurfaceRadius(sphereDir)` — sample runtime surface radius for physics grounding
- `UpdateLOD(cameraPos)` — updates camera position used by chunk streamer

See the Planet System doc for full pipeline details.

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
| **Volumetric Fog** | Ray-marched volumetric scattering with shadow sampling, height falloff, and 3D noise |
| **Color Grading** | Brightness, Contrast, Saturation, Exposure adjustments |
| **Tone Mapping** | HDR to LDR conversion (Reinhard or ACES methods) |
| **Vignette** | Darkened edges around the screen |
| **FXAA** | Fast approximate anti-aliasing |
| **Underwater** | Distortion, fog, caustics, and color absorption when camera is below water |

### Volumetric Fog Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `VolumetricFogEnabled` | `bool` | `false` | Enable volumetric fog rendering |
| `VolumetricFogDensity` | `float` | `0.02` | Base fog density for scattering |
| `VolumetricFogAnisotropy` | `float` | `0.3` | Henyey-Greenstein scattering anisotropy (-1 to 1; positive = forward scattering) |
| `VolumetricFogScattering` | `float` | `1.0` | In-scattered light intensity multiplier |
| `VolumetricFogHeightFalloff` | `float` | `0.1` | Height-based density falloff rate |
| `VolumetricFogBaseHeight` | `float` | `0` | Base height of the fog volume (world Y) |
| `VolumetricFogNoiseScale` | `float` | `0.1` | Scale of 3D noise applied to fog density |
| `VolumetricFogNoiseSpeed` | `float` | `0.5` | Animation speed of the noise pattern |
| `VolumetricFogMaxDistance` | `float` | `200` | Maximum ray march distance |
| `VolumetricFogColor` | `Vector3` | `(1,1,1)` | Color tint for the volumetric fog (RGB) |
| `VolumetricFogSteps` | `int` | `32` | Number of ray march steps (higher = better quality, lower performance) |

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

## DialogueRunner

Component that walks a `DialogueTree` asset, publishing events via the `EventBus` for UI display. Supports text subtitles, voice line audio, or both simultaneously.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Mode` | `DialogueMode` | `TextAndVoice` | Presentation mode: `TextOnly`, `VoiceOnly`, or `TextAndVoice` |
| `VoiceVolume` | `float` | `1.0` | Volume for voice line playback (0-1) |
| `AutoAdvanceOnVoiceEnd` | `bool` | `true` | Auto-advance to next node when voice clip finishes |

**Read-only runtime properties:**
- `IsRunning` — whether dialogue is currently active
- `IsWaitingForInput` — whether the runner is waiting for player input (advance or choice selection)
- `CurrentNode` — the current `DialogueNode` being displayed
- `IsVoicePlaying` — whether a voice clip is currently playing

**Methods:**
- `StartDialogue()` — begin the dialogue from the tree's start node
- `StartDialogue(DialogueTree tree)` — set a tree and begin
- `StopDialogue()` — immediately end the dialogue
- `Advance()` — advance to the next node (for dialogue lines waiting for input)
- `SelectChoice(int index)` — select a choice by index (for choice nodes)
- `StopVoice()` — stop the currently playing voice clip

### DialogueTree Asset

A graph of `DialogueNode` objects representing a conversation flow.

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Display name of the dialogue tree |
| `StartNodeId` | `string` | ID of the entry node |
| `Nodes` | `List<DialogueNode>` | All nodes in the tree |

### DialogueNode

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` | Unique node identifier |
| `Type` | `DialogueNodeType` | Node type (see below) |
| `Speaker` | `string` | Speaker name for dialogue lines |
| `Text` | `string` | Dialogue text content |
| `Duration` | `float` | Auto-advance duration (0 = wait for input) |
| `VoiceClipPath` | `string` | Path to voice audio file (`.wav`, `.mp3`, `.ogg`) |
| `Choices` | `List<DialogueChoice>` | Available choices (for Choice nodes) |
| `BranchVariable` / `BranchValue` | `string` | Variable condition (for Branch nodes) |
| `TrueNextId` / `FalseNextId` | `string` | Conditional next nodes (for Branch nodes) |
| `NextNodeId` | `string` | Next node for linear flow |
| `Actions` | `List<VariableAction>` | Variable assignments executed on node entry |

**Node Types:**

| Type | Description |
|------|-------------|
| `Dialogue` | Displays speaker text (and optionally plays a voice clip) |
| `Choice` | Presents player choices with optional conditions |
| `Branch` | Checks a variable and routes to true/false paths |
| `Start` | Entry point of the dialogue tree |
| `End` | Terminates the dialogue |

**Events published via EventBus:**

| Event | When |
|-------|------|
| `DialogueStartedEvent` | Dialogue begins (`TreeName`) |
| `DialogueLineEvent` | A dialogue line is shown (`Speaker`, `Text`, `Duration`, `VoiceClipPath`, `ShowText`, `PlayVoice`) |
| `DialogueChoiceEvent` | Choices are presented (`Options`, `NodeId`) |
| `DialogueEndedEvent` | Dialogue ends (`TreeName`) |

### Dialogue Modes

| Mode | Text Subtitles | Voice Audio |
|------|----------------|-------------|
| `TextOnly` | Shown | Not played |
| `VoiceOnly` | Hidden | Played |
| `TextAndVoice` | Shown | Played |

When `AutoAdvanceOnVoiceEnd` is enabled and a voice clip is playing, the runner automatically advances to the next node when the clip finishes instead of waiting for manual input.

---

## BehaviorTreeRunner

Component that ticks a `BehaviorTree` asset each frame to drive AI behavior. Each agent has its own `Blackboard` for sharing data between tree nodes.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `IsRunning` | `bool` | `true` | Whether the tree is actively ticking |
| `TickInterval` | `float` | `0` | Seconds between ticks (0 = every frame) |

**Read-only runtime properties:**
- `Tree` — the `BehaviorTree` asset being executed
- `Blackboard` — per-agent key-value data store
- `LastStatus` — result of the last tick (`Running`, `Success`, or `Failure`)

**Events:**
- `OnTick` — `Action<BTStatus>` fired after each tick with the result status

**Methods:**
- `Restart()` — reset the tree and begin from scratch
- `SetBlackboardValue<T>(key, value)` — set a blackboard value (convenience)
- `GetBlackboardValue<T>(key, default)` — get a blackboard value (convenience)

**Lifecycle:**
- `Start()` — initializes the blackboard with a `"Self"` key pointing to the owning `GameObject`
- `Update()` — ticks the tree (respecting `TickInterval`); when the tree completes (Success or Failure), it is automatically reset for the next tick

### BehaviorTree Asset

A root node that can be ticked each frame. Returns `BTStatus` (Running, Success, Failure).

**Builder helpers:**
```csharp
var tree = BehaviorTree.Sequence("Patrol", 
    new WaitNode(2f),
    new ActionNode("Move", (bb, dt) => { /* move logic */ return BTStatus.Success; })
);
```

### BTNode Types

**Composite Nodes** (have multiple children):

| Node | Behavior |
|------|----------|
| `SelectorNode` | Ticks children left-to-right. Succeeds on first child success. Fails if all children fail. (OR logic) |
| `SequenceNode` | Ticks children left-to-right. Fails on first child failure. Succeeds if all children succeed. (AND logic) |
| `ParallelNode` | Ticks all children every frame. Succeeds when `RequiredSuccesses` children succeed. Fails when success becomes impossible. |

**Decorator Nodes** (wrap a single child):

| Node | Behavior |
|------|----------|
| `InverterNode` | Inverts the child's result (Success ↔ Failure) |
| `RepeaterNode` | Repeats the child N times (`Count`), or forever if `Count < 0` |
| `SucceederNode` | Always returns Success regardless of child result (unless Running) |

**Leaf Nodes** (no children):

| Node | Behavior |
|------|----------|
| `ActionNode` | Executes a `Func<Blackboard, float, BTStatus>` delegate |
| `ConditionNode` | Checks a `Func<Blackboard, bool>` predicate — returns Success if true, Failure if false |
| `WaitNode` | Waits for `Duration` seconds then succeeds |

### Blackboard

Per-agent key-value data store for sharing state between behavior tree nodes.

| Method | Description |
|--------|-------------|
| `Set<T>(key, value)` | Store a value |
| `Get<T>(key, default)` | Retrieve a value (returns default if missing or wrong type) |
| `Has(key)` | Check if a key exists |
| `Remove(key)` | Remove a key |
| `Clear()` | Remove all entries |
| `GetFloat/GetInt/GetBool/GetString/GetVector3` | Typed convenience helpers |
| `Keys` | Enumerate all keys |
| `Count` | Number of entries |

**Script example:**
```csharp
public class EnemyAI : Behavior
{
    public override void Start()
    {
        var runner = GetComponent<BehaviorTreeRunner>();
        runner.Blackboard.Set("PatrolSpeed", 3.5f);
        runner.Blackboard.Set("Target", (GameObject?)null);

        var tree = BehaviorTree.Selector("Root",
            new SequenceNode { Name = "Attack", Children = {
                new ConditionNode("HasTarget", bb => bb.Get<GameObject?>("Target") != null),
                new ActionNode("Chase", (bb, dt) => { /* chase logic */ return BTStatus.Running; })
            }},
            new SequenceNode { Name = "Patrol", Children = {
                new ActionNode("Wander", (bb, dt) => { /* patrol logic */ return BTStatus.Running; }),
                new WaitNode(2f)
            }}
        );
        runner.Tree = tree;
    }
}
```

---

## TimelinePlayer

Component that plays a `TimelineAsset` for cutscenes and scripted sequences. Controls playback (play, pause, seek, speed) and processes multiple track types each frame.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PlayOnAwake` | `bool` | `false` | Start playback automatically when entering play mode |
| `Speed` | `float` | `1.0` | Playback speed multiplier |

**Read-only runtime properties:**
- `Timeline` — the `TimelineAsset` being played
- `CurrentTime` — current playback position (seconds)
- `IsPlaying` — whether the timeline is actively playing
- `IsFinished` — whether the timeline has reached the end (non-looping only)

**Events:**
- `OnComplete` — `Action` fired when playback finishes

**Methods:**
- `Play()` — start or resume playback
- `Pause()` — pause playback
- `Stop()` — stop playback and reset to time 0 (restores all activation changes)
- `Seek(float time)` — jump to a specific time

### TimelineAsset

An ordered list of tracks, each containing clips on a shared time ruler.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | `string` | `"New Timeline"` | Display name |
| `Duration` | `float` | `10` | Total duration in seconds |
| `Loop` | `bool` | `false` | Whether the timeline loops |
| `Tracks` | `List<TimelineTrack>` | `[]` | Ordered list of tracks |

### TimelineTrack

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | `string` | `"Track"` | Track display name |
| `Type` | `TrackType` | `Animation` | Track type (see below) |
| `Muted` | `bool` | `false` | Muted tracks are skipped during playback |
| `Clips` | `List<TimelineClip>` | `[]` | Clips on this track |

### TimelineClip

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StartTime` | `float` | `0` | Start time in seconds |
| `Duration` | `float` | `1` | Duration in seconds |
| `BlendIn` | `float` | `0` | Crossfade-in duration |
| `BlendOut` | `float` | `0` | Crossfade-out duration |
| `Speed` | `float` | `1` | Playback speed multiplier |
| `AssetPath` | `string` | `""` | Animation/audio file path |
| `TargetName` | `string` | `""` | Target GameObject name |
| `EventName` | `string` | `""` | Event name for event tracks |
| `EventData` | `string` | `""` | String payload for events |

### Track Types

| Type | Runtime Behavior |
|------|-----------------|
| **Animation** | Finds the `Animator` on the target GameObject and calls `Play(assetPath)` for each active clip |
| **Camera** | Enables/disables target camera GameObjects based on clip time ranges (for camera cuts) |
| **Audio** | Plays audio files via `AudioBackend.Play()` when clips start; stops them when clips end or the timeline stops/loops |
| **Activation** | Enables target GameObjects during clip time ranges, disables them outside. Original states are restored on `Stop()` |
| **Event** | Publishes `TimelineEventFired` events via EventBus when the playhead crosses a clip's start time (fires once per playthrough) |

**Script example:**
```csharp
var player = GetComponent<TimelinePlayer>();
var timeline = new TimelineAsset { Name = "Intro Cutscene", Duration = 15f };

var camTrack = timeline.AddTrack("Camera Switches", TrackType.Camera);
camTrack.Clips.Add(new TimelineClip { StartTime = 0f, Duration = 5f, TargetName = "CinematicCam1" });
camTrack.Clips.Add(new TimelineClip { StartTime = 5f, Duration = 10f, TargetName = "CinematicCam2" });

var audioTrack = timeline.AddTrack("Music", TrackType.Audio);
audioTrack.Clips.Add(new TimelineClip { StartTime = 0f, Duration = 15f, AssetPath = "Assets/Audio/intro.wav" });

player.Timeline = timeline;
player.Play();
player.OnComplete += () => LogInfo("Cutscene finished!");
```

---

## Decal

Decal projection component for rendering textures onto surfaces.

| Property       | Type               | Default          | Description                              |
|----------------|--------------------|------------------|------------------------------------------|
| `TexturePath`  | `string`           | `""`             | Path to the decal texture image          |
| `Width`        | `float`            | `1.0`            | Decal width in world units               |
| `Height`       | `float`            | `1.0`            | Decal height in world units              |
| `Depth`        | `float`            | `0.5`            | Projection depth (z-fighting offset)     |
| `Color`        | `Vector4`          | `(1,1,1,1)`      | Decal tint color (RGBA)                  |
| `Opacity`      | `float`            | `1.0`            | Base opacity (0-1)                       |
| `Projection`   | `DecalProjection`  | `Forward`        | Projection axis: `Forward`, `Up`, `Down` |
| `AngleFade`    | `float`            | `60`             | Fade angle for steep surfaces (degrees)  |
| `Lifetime`     | `float`            | `0`              | Auto-destroy after N seconds (0 = infinite) |
| `FadeOutTime`  | `float`            | `1.0`            | Fade-out duration at end of lifetime     |

**Projection modes:**
- **Forward** — quad in XY plane facing -Z (for walls)
- **Up** — quad in XZ plane facing +Y (for floors/ground)
- **Down** — quad in XZ plane facing -Y (for ceilings)

**Script spawning:**
```csharp
Decal.Spawn(hitPoint, hitNormal, "Assets/Textures/bullethole.png", width: 0.5f, height: 0.5f, lifetime: 10f);
```

**Requires:** MeshFilter, MeshRenderer (auto-added via `[Require]`)

---

## NavMeshAgent

Navigation mesh agent component with A* pathfinding on baked navigation meshes.

| Property          | Type    | Default | Description                              |
|-------------------|---------|---------|------------------------------------------|
| `Speed`           | `float` | `3.5`   | Movement speed (units/sec)               |
| `AngularSpeed`    | `float` | `360`   | Rotation speed (degrees/sec)             |
| `Acceleration`    | `float` | `8`     | Acceleration rate                        |
| `StoppingDistance` | `float`| `0.5`   | Distance at which the agent stops        |
| `Height`          | `float` | `2`     | Agent height                             |
| `Radius`          | `float` | `0.5`   | Agent radius                             |
| `AvoidanceRadius` | `float` | `1`     | Inter-agent avoidance radius             |
| `AutoBraking`     | `bool`  | `true`  | Slow down when approaching destination   |
| `SnapToNavMesh`   | `bool`  | `true`  | Follow navmesh surface height            |
| `AutoRepath`      | `bool`  | `true`  | Re-compute path when stuck               |
| `RepathInterval`  | `float` | `1`     | Seconds between auto-repaths             |
| `AreaMask`        | `int`   | `-1`    | Walkable area mask (all areas)           |

**Read-only runtime state:**
- `Status` — `Idle`, `Moving`, `Reached`, or `PathNotFound`
- `HasPath` / `RemainingDistance` / `Velocity` / `CurrentSpeed`
- `Path` — current waypoint list (for debug drawing)

**Events:**
- `OnPathComplete` — fired when destination is reached or pathfinding fails

**Key methods:**
- `SetDestination(Vector3)` — navigate to a world position using A*
- `MoveTo(GameObject)` — navigate toward another object
- `Warp(Vector3)` — teleport to a position (snaps to NavMesh)
- `ClearPath()` / `Stop()` / `Resume()`

**NavMesh static API:**
```csharp
NavMesh.Bake();                                    // Bake from scene geometry
NavMesh.FindPath(start, end);                      // A* pathfinding
NavMesh.SamplePosition(pos, out hit);              // Closest point on NavMesh
NavMesh.SampleHeight(pos, out y);                  // Surface height at XZ
NavMesh.Raycast(origin, dir, maxDist, out hit);    // Raycast against NavMesh
```

---

## VegetationPainter

Vegetation painter component for GPU-instanced grass, rocks, and debris placement on terrain surfaces.

| Property              | Type             | Default              | Description                          |
|-----------------------|------------------|----------------------|--------------------------------------|
| `ActiveType`          | `VegetationType` | `Grass`              | Type: `Grass`, `Rock`, `Debris`, `Custom` |
| `BrushRadius`         | `float`          | `5`                  | Paint brush radius (world units)     |
| `Density`             | `float`          | `10`                 | Instances per unit area              |
| `MinScale`            | `float`          | `0.5`                | Minimum random scale                 |
| `MaxScale`            | `float`          | `1.5`                | Maximum random scale                 |
| `RandomRotation`      | `bool`           | `true`               | Apply random Y-axis rotation         |
| `GrassHeight`         | `float`          | `1.0`                | Grass blade height                   |
| `GrassWidth`          | `float`          | `0.4`                | Grass blade width                    |
| `GrassBaseColor`      | `Vector3`        | `(0.2, 0.5, 0.15)`  | Grass base color (RGB)               |
| `GrassTipColor`       | `Vector3`        | `(0.4, 0.7, 0.2)`   | Grass tip color (RGB)                |
| `WindStrength`        | `float`          | `0.5`                | Wind sway intensity                  |
| `FadeStartDistance`   | `float`          | `30`                 | LOD fade start distance              |
| `FadeEndDistance`     | `float`          | `50`                 | LOD fade end distance (cull beyond)  |
| `CustomMeshPath`      | `string`         | `""`                 | Custom 3D model or texture path      |
| `ModelExclusionRadius`| `float`          | `2`                  | Exclusion zone around existing models|
| `IsWaterPlant`        | `bool`           | `false`              | Only spawn in water areas            |

**Key methods:**
- `Paint(center, radius, terrain)` — scatter vegetation instances
- `Erase(center, radius)` — remove instances within radius
- `BuildOnTerrain()` — auto-populate entire terrain with chunked grass
- `ClearAll()` — remove all vegetation

**Features:**
- **Chunked rendering** — merged meshes per spatial chunk (one draw call per chunk)
- **Distance culling** — chunks beyond `FadeEndDistance` are automatically hidden
- **Model exclusion** — grass avoids spawning near placed 3D models
- **Custom meshes** — load 3D models or billboard textures as vegetation type
- **Water-aware** — optionally restrict placement to underwater areas

---

## Camera2D

2D camera helper that configures a Camera for orthographic 2D rendering with follow, zoom, bounds, pixel-perfect snapping, and camera shake.

| Property          | Type      | Default          | Description                          |
|-------------------|-----------|------------------|--------------------------------------|
| `PixelPerfect`    | `bool`    | `false`          | Snap to nearest pixel for crisp 2D   |
| `PixelsPerUnit`   | `float`   | `100`            | Pixels per world unit                |
| `ReferenceHeight` | `int`     | `1080`           | Reference screen height              |
| `Zoom`            | `float`   | `1`              | Zoom level (1 = default)             |
| `FollowTargetName`| `string`  | `""`             | Name of GameObject to follow         |
| `SmoothSpeed`     | `float`   | `5`              | Follow smoothing (0 = instant)       |
| `FollowOffset`    | `Vector3` | `(0, 0, -10)`   | Offset from follow target            |
| `UseBounds`       | `bool`    | `false`          | Enable camera bounds clamping        |
| `BoundsMinX/MaxX` | `float`   | `±100`           | Horizontal camera bounds             |
| `BoundsMinY/MaxY` | `float`   | `±100`           | Vertical camera bounds               |

**Methods:**
- `Shake(intensity, duration)` — trigger camera shake effect
- `ScreenToWorld(screenX, screenY, screenW, screenH)` — convert screen to world coordinates

**Requires:** Camera (auto-added via `[Require]`)

---

## Tilemap

2D tilemap component for grid-based level design with sparse storage, collision, and tileset UV mapping.

| Property         | Type      | Default   | Description                          |
|------------------|-----------|-----------|--------------------------------------|
| `CellSize`       | `float`   | `1`       | World-space size of each cell        |
| `Width`          | `int`     | `32`      | Grid width in cells                  |
| `Height`         | `int`     | `32`      | Grid height in cells                 |
| `SortingLayer`   | `string`  | `"Default"` | Sorting layer name                 |
| `SortingOrder`   | `int`     | `0`       | Order within the sorting layer       |
| `TilesetPath`    | `string`  | `""`      | Path to tileset texture atlas        |
| `TilesetColumns` | `int`     | `16`      | Columns in the tileset texture       |
| `TilesetRows`    | `int`     | `16`      | Rows in the tileset texture          |
| `TintColor`      | `Color`   | `White`   | Tint color for the entire tilemap    |

**Key methods:**
- `SetTile(x, y, tileId, color, collision)` — place a tile
- `GetTile(x, y)` — read a tile
- `ClearTile(x, y)` / `ClearAll()` — remove tiles
- `FillRect(x, y, w, h, tileId)` — fill a rectangular region
- `GridToWorld(x, y)` / `WorldToGrid(x, y)` — coordinate conversion
- `HasCollisionAt(x, y)` — check collision flag
- `CheckCollision(minX, minY, maxX, maxY)` — AABB collision query

**Features:**
- **Sparse storage** — only non-empty tiles consume memory
- **Per-tile collision** — flag tiles for 2D physics collision
- **Tileset UV mapping** — supports texture atlases with spacing and margins
- **Flip and rotation** — per-tile sprite flipping and 90-degree rotation

---

## IKConstraint

Inverse kinematics constraint component that overrides animated bone poses in `LateUpdate`.

| Property        | Type      | Default      | Description                          |
|-----------------|-----------|--------------|--------------------------------------|
| `Mode`          | `IKMode`  | `TwoBone`    | IK type: `TwoBone`, `LookAt`, `FABRIK` |
| `TargetName`    | `string`  | `""`         | Name of the target GameObject        |
| `PoleTargetName`| `string`  | `""`         | Pole target for bend direction (TwoBone) |
| `RootBoneName`  | `string`  | `""`         | Root bone (e.g., "UpperArm")         |
| `MidBoneName`   | `string`  | `""`         | Mid bone (e.g., "Forearm")           |
| `TipBoneName`   | `string`  | `""`         | Tip bone (e.g., "Hand")              |
| `Weight`        | `float`   | `1`          | Blend weight (0 = no IK, 1 = full)  |
| `MaxAngle`      | `float`   | `90`         | Maximum look-at angle (LookAt mode)  |
| `ChainLength`   | `int`     | `4`          | Number of joints (FABRIK mode)       |
| `Iterations`    | `int`     | `10`         | FABRIK solver iterations             |
| `Tolerance`     | `float`   | `0.01`       | Convergence tolerance                |

**IK Modes:**
- **TwoBone** — arm/leg IK with 3 joints (upper, mid, tip) and pole target for bend direction
- **LookAt** — rotate toward a target (head tracking, turrets) with angle clamping
- **FABRIK** — Forward And Backward Reaching IK for multi-joint chains (tails, spines, tentacles)

---

## RigidbodyPlayer

Physics-based player movement using Rigidbody dynamics (momentum, sliding, inertia). Drop-in alternative to `PlayerMovement` for a heavier, more physical feel.

| Property            | Type      | Default          | Description                          |
|---------------------|-----------|------------------|--------------------------------------|
| `MoveForce`         | `float`   | `50`             | Movement force                       |
| `MaxSpeed`          | `float`   | `7`              | Maximum horizontal speed             |
| `SprintMultiplier`  | `float`   | `1.75`           | Sprint speed multiplier              |
| `JumpImpulse`       | `float`   | `5`              | Jump impulse force                   |
| `AirControlFactor`  | `float`   | `0.3`            | Air control (0 = none, 1 = full)     |
| `GroundDrag`        | `float`   | `5`              | Ground friction                      |
| `AirDrag`           | `float`   | `0.5`            | Air resistance                       |
| `SwimForce`         | `float`   | `30`             | Swimming movement force              |
| `SwimMaxSpeed`      | `float`   | `4`              | Maximum swim speed                   |
| `SwimVerticalSpeed` | `float`   | `3`              | Vertical swim speed                  |
| `SwimDrag`          | `float`   | `4`              | Underwater drag                      |
| `LookSensitivity`   | `float`   | `90`             | Mouse look speed                     |
| `FirstPerson`       | `bool`    | `true`           | First-person camera mode             |
| `FirstPersonOffset` | `Vector3` | `(0, 1.7, 0)`    | First-person camera offset           |
| `ThirdPersonOffset` | `Vector3` | `(0, 1.7, -3.5)` | Third-person camera offset           |
| `CameraFollowLerp`  | `float`   | `12`             | Third-person camera smoothing        |
| `RotateBodyWithLook`| `bool`    | `true`           | Body follows look yaw                |
| `TurnBodyWhileMoving`| `bool`   | `false`          | Rotate body only while moving        |
| `JumpBufferSeconds` | `float`   | `0.12`           | Jump input buffer                    |

**Features:**
- **Swimming** — automatic underwater movement when the Rigidbody detects submersion
- **Momentum-based** — natural sliding, pushing, and inertia
- **Planet movement** — movement projected onto the local tangent plane
- **Planet jumping** — jump impulse applied along `Rigidbody.LocalUp`
- **Camera up alignment** — writes smoothed local up into `Camera.WorldUp`
- **Camera modes** — first-person and third-person with smooth follow
- **Jump buffering** — responsive jump input

**Requires:** Rigidbody, CapsuleCollider (auto-added via `[Require]`)

---

## NetworkIdentity

Network identity component that identifies a GameObject for multiplayer synchronization. Must be attached to any networked object.

| Property       | Type    | Default | Description                          |
|----------------|---------|---------|--------------------------------------|
| `NetworkId`    | `uint`  | `0`     | Unique network ID (assigned by server) |
| `IsLocalPlayer`| `bool`  | `false` | True if owned by the local player    |
| `OwnerPeerId`  | `int`   | `-1`    | Peer ID of the owner (-1 = server)   |

**Read-only:**
- `HasAuthority` — true if the local machine controls this object (server or owner)

**Methods:**
- `SerializeState()` — serialize transform and component state to bytes
- `DeserializeState(data)` — apply network state (with interpolation via NetworkTransform)

---

## NetworkTransform

Synchronizes position, rotation, and scale over the network with smooth interpolation.

| Property              | Type    | Default | Description                          |
|-----------------------|---------|---------|--------------------------------------|
| `InterpolationSpeed`  | `float` | `15`    | Interpolation speed (higher = snappier) |
| `PositionThreshold`   | `float` | `0.01`  | Minimum position change to sync      |
| `RotationThreshold`   | `float` | `0.5`   | Minimum rotation change to sync (degrees) |
| `SyncRate`            | `float` | `20`    | Updates per second                   |
| `SyncPosition`        | `bool`  | `true`  | Enable position syncing              |
| `SyncRotation`        | `bool`  | `true`  | Enable rotation syncing              |
| `SyncScale`           | `bool`  | `false` | Enable scale syncing                 |

**Requires:** NetworkIdentity (auto-added via `[Require]`)

---

## NetworkAnimator

Synchronizes animation state machine parameters and state transitions over the network.

| Property    | Type    | Default | Description                          |
|-------------|---------|---------|--------------------------------------|
| `SyncRate`  | `float` | `10`    | Animation sync rate (updates/sec)    |

Automatically detects changes in animation state names, float parameters, and bool parameters, then broadcasts updates to remote peers via RPC.

**Requires:** NetworkIdentity, Animator (auto-added via `[Require]`)

---

## ReverbZone

Audio reverb zone that applies reverb effects to audio sources when the AudioListener enters its volume.

| Property           | Type           | Default | Description                          |
|--------------------|----------------|---------|--------------------------------------|
| `Preset`           | `ReverbPreset` | `Room`  | Reverb preset (10 presets available) |
| `MinDistance`       | `float`        | `5`     | Inner radius (full reverb)           |
| `MaxDistance`       | `float`        | `20`    | Outer radius (reverb fades to zero)  |
| `DecayTime`        | `float`        | `1.5`   | Reverb decay time (custom preset)    |
| `Density`          | `float`        | `1`     | Echo density (custom preset)         |
| `Diffusion`        | `float`        | `1`     | Diffusion level (custom preset)      |

**Available presets:** `None`, `Room`, `Hall`, `Cathedral`, `Cave`, `Arena`, `Forest`, `Underwater`, `Bathroom`, `StoneRoom`, `Auditorium`

**Features:**
- **Distance-based blending** — reverb fades smoothly between inner and outer radius
- **Overlapping zones** — closest zone takes priority
- **Custom parameters** — when Preset is `None`, manual reverb parameters are used

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

// Runtime scene loading
SceneManager.LoadScene("Main Menu");

// Scene queries
var cam = SceneQuery.FindBehaviors<Camera>().FirstOrDefault();
var player = SceneQuery.FindByName("Player");
var weapon = SceneQuery.FindByPath("Player/RightHand/Weapon");
```

---

## Runtime UI System

The engine includes a full GPU-rendered runtime UI system for in-game interfaces (HUDs, menus, dialogs). UI elements are components attached to GameObjects, laid out with an anchor-based `RectTransform`, and rendered in batches by the `CanvasRenderer`.

### Architecture

```
Canvas (root)
  └─ RectTransform (layout)
       └─ UIElement subclasses (visuals + interaction)
            ├─ UIText        — Bitmap font text
            ├─ UIImage       — Sprite / texture
            ├─ UIButton      — Clickable button
            ├─ UIPanel       — Background panel
            ├─ UISlider      — Value slider
            ├─ UIToggle      — Checkbox toggle
            └─ UIInputField  — Text input box
```

The `UIEventSystem` processes pointer input each frame, raycasting against `RectTransform` rects in screen space and delivering hover/press/click/drag events to UI elements. `UIEventSystem.PointerOverUI` can be checked to prevent game input when the pointer is over a UI element.

---

### Canvas

Root component for runtime UI rendering. Attach to a GameObject to enable UI rendering for that hierarchy. All UI children must have a `RectTransform`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RenderMode` | `CanvasRenderMode` | `ScreenSpaceOverlay` | How the canvas is rendered |
| `SortOrder` | `int` | `0` | Drawing priority (higher = on top) |
| `PixelPerfect` | `bool` | `true` | Snap to pixel grid for crisp edges |
| `ScaleMode` | `CanvasScaleMode` | `ScaleWithScreenSize` | How the canvas scales with screen size |
| `ReferenceResolutionX` | `float` | `1920` | Design width for ScaleWithScreenSize |
| `ReferenceResolutionY` | `float` | `1080` | Design height for ScaleWithScreenSize |
| `MatchWidthOrHeight` | `float` | `0.5` | 0 = match width, 1 = match height |
| `WorldSizeX` | `float` | `5` | Width in world units (WorldSpace mode) |
| `WorldSizeY` | `float` | `3` | Height in world units (WorldSpace mode) |

**Render Modes:**
| Mode | Description |
|------|-------------|
| `ScreenSpaceOverlay` | Drawn after post-processing, always on top. Coordinates in pixels. |
| `ScreenSpaceCamera` | Rendered relative to a specific Camera, affected by post-processing. |
| `WorldSpace` | Lives in 3D world space on a GameObject. |

**Scale Modes:**
| Mode | Description |
|------|-------------|
| `ConstantPixelSize` | UI elements retain their pixel size regardless of screen size |
| `ScaleWithScreenSize` | UI scales with screen size based on reference resolution |
| `ConstantPhysicalSize` | UI elements retain their physical size (DPI-aware) |

---

### RectTransform

Defines a 2D rectangle for UI layout using anchor-based positioning relative to a parent `RectTransform` (or the Canvas root). Lives alongside `Transform` on the same GameObject.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `AnchorMinX/Y` | `float` | `0.5` | Bottom-left anchor (0–1 relative to parent) |
| `AnchorMaxX/Y` | `float` | `0.5` | Top-right anchor (0–1 relative to parent) |
| `PivotX/Y` | `float` | `0.5` | Local origin point (0–1) |
| `AnchoredPositionX/Y` | `float` | `0` | Offset from anchor centre (pixels) |
| `SizeDeltaX` | `float` | `160` | Width when anchors are together / delta when apart |
| `SizeDeltaY` | `float` | `40` | Height when anchors are together / delta when apart |
| `Rotation2D` | `float` | `0` | 2D rotation in degrees |
| `ScaleX/Y` | `float` | `1` | 2D scale multiplier |

**Key Methods:**
- `GetRect(parentRect)` — compute screen-space rect from parent rect
- `GetWorldRect(canvasRect)` — walk hierarchy to compute final screen rect
- `GetWorldCorners(canvasRect, corners)` — get 4 corners (supports 2D rotation)
- `ContainsScreenPoint(point, canvasRect)` — point-in-rect hit test

---

### UIElement (Base Class)

Abstract base class for all UI elements. Requires `RectTransform`. Provides common visual properties and pointer-event callbacks.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Raycastable` | `bool` | `true` | Whether this element receives pointer events |
| `Color` | `Color` | `White` | Base tint color |
| `Opacity` | `float` | `1` | Opacity (0 = transparent, 1 = opaque) |

**Pointer Events (virtual):**
| Method | When Called |
|--------|------------|
| `OnPointerEnter()` | Pointer enters this element's rect |
| `OnPointerExit()` | Pointer leaves this element's rect |
| `OnPointerDown()` | Pointer button pressed over this element |
| `OnPointerUp()` | Pointer button released over this element |
| `OnPointerClick()` | Click (press + release) on this element |
| `OnDrag(delta)` | Each frame while dragging over this element |

---

### UIText

Renders text using a bitmap font atlas (BMFont `.fnt` format). Supports font size scaling, alignment, word wrap, and SDF text rendering. If no font path is set, a default font atlas is auto-generated.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Text` | `string` | `"Text"` | The text string to display |
| `FontSize` | `float` | `24` | Font size in canvas pixels |
| `FontPath` | `string` | `""` | Path to BMFont `.fnt` file (empty = auto-generated default) |
| `Alignment` | `TextAnchor` | `Left` | Horizontal alignment (`Left`, `Center`, `Right`) |
| `WordWrap` | `bool` | `true` | Wrap text to fit the rect width |
| `LineSpacing` | `float` | `1.0` | Line spacing multiplier |

---

### UIImage

Renders a sprite or texture inside a `RectTransform`. Supports simple stretch, 9-slice, tiled, and filled image modes.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SpritePath` | `string` | `""` | Path to image file |
| `ImageType` | `ImageType` | `Simple` | Rendering mode |
| `FillAmount` | `float` | `1` | Fill (0–1) for `Filled` mode |
| `PreserveAspect` | `bool` | `false` | Maintain original aspect ratio |

**Image Types:**
| Type | Description |
|------|-------------|
| `Simple` | Stretch to fill the rect |
| `Sliced` | 9-slice rendering for resizable UI panels |
| `Tiled` | Tile the image to fill the rect |
| `Filled` | Horizontal fill controlled by `FillAmount` |

---

### UIButton

Interactive button that responds to pointer events. Drives a sibling `UIImage` color based on hover/press/disabled state with smooth color transitions.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Interactable` | `bool` | `true` | Whether the button can be clicked |
| `NormalColor` | `Color` | `#FFFFFF` | Color in normal state |
| `HighlightedColor` | `Color` | `#E0E0E0` | Color when hovered |
| `PressedColor` | `Color` | `#B0B0B0` | Color when pressed |
| `DisabledColor` | `Color` | `#808080` | Color when not interactable |
| `FadeDuration` | `float` | `0.1` | Color transition speed (0 = instant) |

**Events:**
- `OnClick` — `Action` fired when the button is clicked

**Usage:**
```csharp
var btn = GetComponent<UIButton>();
btn.OnClick += () => LogInfo("Button clicked!");
```

---

### UIPanel

A simple colored or textured rectangular background. Useful as a container/backdrop for other UI elements.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SpritePath` | `string` | `""` | Optional background image |

Inherits `Color` and `Opacity` from `UIElement`.

---

### UISlider

A draggable slider for selecting a value within a range. Renders a background track, fill bar, and handle knob.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MinValue` | `float` | `0` | Minimum slider value |
| `MaxValue` | `float` | `1` | Maximum slider value |
| `Value` | `float` | `0` | Current value |
| `WholeNumbers` | `bool` | `false` | Restrict to integers |
| `Direction` | `SliderDirection` | `LeftToRight` | Slider direction |
| `BackgroundColor` | `Color` | `#404040` | Track background color |
| `FillColor` | `Color` | `#40A0FF` | Fill bar color |
| `HandleColor` | `Color` | `White` | Handle knob color |
| `HandleSize` | `float` | `0.8` | Handle size as fraction of height |

**Directions:** `LeftToRight`, `RightToLeft`, `BottomToTop`, `TopToBottom`

**Events:**
- `OnValueChanged` — `Action<float>` fired when the value changes

**Usage:**
```csharp
var slider = GetComponent<UISlider>();
slider.OnValueChanged += (val) => LogInfo($"Volume: {val:F2}");
```

---

### UIToggle

A checkbox/toggle switch that alternates between on and off states. Renders a background box with a checkmark indicator when active.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `IsOn` | `bool` | `false` | Current toggle state |
| `Interactable` | `bool` | `true` | Whether the toggle can be interacted with |
| `BackgroundColor` | `Color` | `#505050` | Background when off |
| `ActiveColor` | `Color` | `#40A0FF` | Background when on |
| `CheckmarkColor` | `Color` | `White` | Checkmark indicator color |
| `CheckmarkInset` | `float` | `0.15` | Checkmark inset (0–0.5) |

**Events:**
- `OnValueChanged` — `Action<bool>` fired when the toggle state changes

---

### UIInputField

A text input box with cursor, selection, placeholder text, and keyboard input handling. Captures keyboard input when focused (clicked).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Text` | `string` | `""` | Current text content |
| `Placeholder` | `string` | `"Enter text..."` | Placeholder when empty |
| `CharacterLimit` | `int` | `0` | Max characters (0 = unlimited) |
| `ContentType` | `InputFieldContentType` | `Standard` | Input validation mode |
| `FontSize` | `float` | `20` | Font size in canvas pixels |
| `FontPath` | `string` | `""` | Path to BMFont `.fnt` file |
| `ReadOnly` | `bool` | `false` | Prevent editing |
| `BackgroundColor` | `Color` | `#303030` | Background color |
| `TextColor` | `Color` | `White` | Text color |
| `PlaceholderColor` | `Color` | `#808080` | Placeholder text color |
| `CursorColor` | `Color` | `White` | Cursor color |
| `SelectionColor` | `Color` | `#6040A0FF` | Selection highlight color |

**Content Types:** `Standard`, `IntegerNumber`, `DecimalNumber`, `Alphanumeric`, `Password`

**Events:**
- `OnValueChanged` — `Action<string>` fired when the text changes
- `OnEndEdit` — `Action<string>` fired when the user presses Enter

**Keyboard Support:** Arrow keys, Home, End, Backspace, Delete, Escape (unfocus), Enter (submit)

---

### Setting Up a UI Hierarchy

A typical in-game HUD setup:

```
HUD (Canvas — ScreenSpaceOverlay, SortOrder=0)
├── HealthBar (RectTransform, UIPanel — dark background)
│   └── HealthFill (RectTransform, UIImage — Filled, FillAmount bound to health)
├── ScoreText (RectTransform, UIText — "Score: 0", Alignment=Right)
├── PauseButton (RectTransform, UIImage + UIButton)
│   └── PauseIcon (RectTransform, UIImage — pause icon sprite)
└── SettingsPanel (RectTransform, UIPanel — hidden by default)
    ├── VolumeSlider (RectTransform, UISlider)
    ├── MuteToggle (RectTransform, UIToggle)
    └── PlayerName (RectTransform, UIInputField)
```

**Script example:**
```csharp
public class HealthBar : Behavior
{
    [Persist] public float MaxHealth { get; set; } = 100f;
    private float _currentHealth;
    private UIImage? _fillImage;

    public override void Start()
    {
        _currentHealth = MaxHealth;
        // Find the fill image on a child named "HealthFill"
        var fill = SceneQuery.FindByPath("HUD/HealthBar/HealthFill");
        _fillImage = fill?.Behaviors.OfType<UIImage>().FirstOrDefault();
    }

    public void TakeDamage(float amount)
    {
        _currentHealth = Math.Max(0, _currentHealth - amount);
        if (_fillImage != null)
            _fillImage.FillAmount = _currentHealth / MaxHealth;
    }
}
```
