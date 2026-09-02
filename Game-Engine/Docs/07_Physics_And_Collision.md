# Game Engine — Physics and Collision

## Overview

The engine provides a collision detection system with colliders, a character controller, rigidbody simulation, a collision world for runtime physics queries, and raycasting. Physics interactions are processed during `FixedUpdate` in the game loop. The system supports both flat-world physics and planet-relative gravity/grounding through `PlanetTerrain`.

---

## Architecture

```
Scene Graph (GameObjects)
    │
    ▼
Colliders (BoxCollider, CapsuleCollider, MeshCollider, PlanetCollider)
    │ registered in
    ▼
CollisionWorld (central registry)
    │ uses
    ├─► BVH (Bounding Volume Hierarchy for spatial acceleration)
    │ queried by
    ├─► CharacterController (per-frame ground/wall/ceiling detection)
    ├─► PlayerMovement (input → CharacterController.Simulate)
    ├─► RigidbodyPlayer (physics-based player movement)
    ├─► PlanetTerrain (planet surface sampling + active planet registry)
    ├─► Physics (static API for scripts)
    └─► SceneView (raycasting for object selection)
```

---

## Collider Types

All colliders inherit from the `Collider` base class and implement `GetWorldAABB()` for broad-phase collision detection.

### BoxCollider
An axis-aligned box shape for collision detection.

| Property | Type      | Default     | Description                        |
|----------|-----------|-------------|------------------------------------|
| `Center` | `Vector3` | `(0, 0, 0)` | Offset from the Transform origin  |
| `Size`   | `Vector3` | `(1, 1, 1)` | Box dimensions (width, height, depth) |

**Methods:**
- `GetLocalCorners(Vector3[])` — computes the 8 local-space corner positions
- `GetWorldAABB()` — transforms corners to world space and computes the axis-aligned bounding box

**Use cases:** Walls, floors, crates, doors, platforms, trigger zones.

### CapsuleCollider
A capsule shape (cylinder with hemispherical caps), primarily used for character controllers.

| Property    | Type      | Default     | Description                        |
|-------------|-----------|-------------|------------------------------------|
| `Center`    | `Vector3` | `(0, 1, 0)` | Offset from the Transform origin  |
| `Radius`    | `float`   | `0.4`       | Capsule radius (minimum: 0.0001)   |
| `Height`    | `float`   | `2.0`       | Total height including caps (clamped to >= 2 × Radius) |
| `Direction` | `Axis`    | `Y`         | Up axis: `X`, `Y`, or `Z`         |

**Methods:**
- `GetLocalCapsule(out Vector3 a, out Vector3 b, out float r)` — returns the two capsule segment endpoints and the clamped radius
- `GetWorldAABB()` — computes world-space AABB with non-uniform scale support

**Clamping:** The height is always clamped to be at least `2 × Radius`, ensuring the capsule doesn't degenerate.

**Use cases:** Player characters, NPCs, cylindrical objects, bipedal entities.

### MeshCollider
Uses the actual mesh geometry for precise triangle-based collision detection.

| Property               | Type           | Default | Description                              |
|------------------------|----------------|---------|------------------------------------------|
| `TargetPaths`          | `List<string>` | `[]`    | Scene paths to target MeshFilters        |
| `BindToTargetTransform`| `bool`         | `true`  | Use target's world Transform for collision |
| `Mesh`                 | `Mesh`         | `null`  | Manual override collision mesh           |

**Target path format:** `"path/to/GameObject#mf:ordinal"` where `ordinal` distinguishes multiple MeshFilters on the same object.

**Methods:**
- `AddTarget(MeshFilter)` / `RemoveTarget(MeshFilter)` / `ClearTargets()` — manage collision targets
- `EnumerateTargetMeshesWorld()` — yields `(Mesh, Matrix4x4)` pairs for all targets in world space
- `GetWorldAABB()` — computes the union AABB of all target meshes

**Fallback:** If no targets are specified, uses all `MeshFilter` components on the same GameObject.

**Use cases:** Complex static geometry (buildings, terrain, irregular shapes, level architecture).

### PlanetCollider
Planet broad-phase collider shell for planetary worlds.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RadiusOverride` | `float` | `0` | Manual override for collider radius (0 = derive from `PlanetTerrain`) |

**Purpose:** Supplies a stable world-space AABB and visual shell bounds for planets.

**Important:** `PlanetCollider` is **broad-phase only**. Outer-crust walking uses `SampleCollisionRadius` on the visible leaf. Caves use `SpherecastGameplay` / `RaycastDensityGameplay` (and `ResolveDensityPenetration`). `SampleSurfaceRadius` is the outermost crust only (water, orbit, gizmos) — not interior contact. See [Planet System](13_Planet_System.md).

---

## CharacterController

A physics-based movement controller that handles gravity, ground detection, slope limiting, step climbing, coyote time, and continuous collision detection. It does **not** use rigid body dynamics — instead it uses a **sweep-and-slide** collision resolution approach.

### Properties

| Property               | Type    | Default | Description                             |
|------------------------|---------|---------|-----------------------------------------|
| `UseGravity`           | `bool`  | `true`  | Apply gravity to vertical velocity      |
| `Gravity`              | `float` | `9.81`  | Gravity acceleration (m/s²)             |
| `JumpHeight`           | `float` | `1.2`   | Jump height in meters                   |
| `StepUpMax`            | `float` | `0.5`   | Maximum step height to auto-climb       |
| `GroundSnapDistance`   | `float` | `0.7`   | Distance to snap character to ground    |
| `WallPush`             | `float` | `0`     | Lateral force to push away from walls   |
| `MaxSlopeAngleDeg`     | `float` | `55`    | Maximum walkable slope angle (degrees)  |
| `CoyoteTimeSeconds`   | `float` | `0.12`  | Grace period after leaving ground edge  |
| `FallbackCapsuleRadius`| `float` | `0.35`  | Capsule radius if no CapsuleCollider    |
| `FallbackCapsuleHeight`| `float` | `1.8`   | Capsule height if no CapsuleCollider    |
| `UnstickIgnoreHuge`    | `bool`  | `true`  | Ignore oversized colliders in unstick   |
| `UnstickMaxExtent`     | `float` | `5`     | Max collider extent for unstick checks  |
| `UnstickSkipIfInside`  | `bool`  | `true`  | Skip unstick if fully inside a collider |
| `PushForce`            | `float` | `3.0`   | Force applied to pushed Rigidbody objects |

### Read-Only Runtime State
| Property          | Type      | Description                        |
|-------------------|-----------|------------------------------------|
| `IsGrounded`      | `bool`    | Whether the character is touching ground |
| `GroundNormal`    | `Vector3` | Surface normal of the ground below |
| `VerticalVelocity`| `float`   | Current vertical velocity (+up)    |
| `CapsuleRadius`   | `float`   | Resolved capsule radius            |
| `CapsuleHalfCylinder` | `float` | Half the cylinder portion height |

### Trigger Events
| Event | Description |
|-------|-------------|
| `OnTriggerEnter(Collider)` | Fired when entering a trigger volume |
| `OnTriggerStay(Collider)` | Fired each frame while inside a trigger |
| `OnTriggerExit(Collider)` | Fired when leaving a trigger volume |

### Simulation Pipeline

The `Simulate(Vector3 desiredHorizontalDelta, bool jump)` method runs the full physics step:

```
1. Apply gravity → VerticalVelocity -= Gravity × dt (if UseGravity)
    │
2. Ground detection (5-sample ring probe)
    │ • 5 rays cast downward in a ring pattern (radius = CapsuleRadius × 0.6)
    │ • Ring radius minimum: 0.05f
    │ • Ray start offset: StepUpMax + 0.002 (minimum 0.2)
    │ • Ground tolerance: ±0.02 units
    │
3. Ceiling detection (upward raycast)
    │ • Prevents upward movement through ceilings
    │ • Resets VerticalVelocity to 0 on ceiling hit
    │
4. Jump evaluation
    │ • Checks IsGrounded OR within CoyoteTimeSeconds
    │ • Jump velocity = sqrt(2 × Gravity × JumpHeight)
    │
5. Continuous Collision Detection (CCD_AdvanceAndSlide)
    │ • Step length: CapsuleRadius / 4 (minimum 0.01)
    │ • Max iterations: 4
    │ • Skin thickness: radius × 0.2 (minimum 0.01)
    │ • Slides along surfaces on collision
    │
6. Horizontal AABB unstick
    │ • Resolves penetrations with world colliders
    │ • Skips huge colliders if UnstickIgnoreHuge is true
    │
7. Wall detection (forward raycast)
    │ • Applies WallPush force if configured
    │ • Slope normal threshold: 0.45 (ignores floors/ceilings)
    │
8. Rigidbody push
    │ • Applies PushForce to contacted Rigidbody components
    │
9. Trigger detection
    │ • Detects Enter/Stay/Exit transitions for trigger colliders
    │
10. Final position update → Transform.Position
```

### Terrain Integration
The CharacterController uses the terrain's O(1) `SampleHeightWorld()` for ground detection on heightmap terrain surfaces. On `PlanetTerrain` it uses **surface mode** + `SampleCollisionRadius` on the outer crust, and gameplay density probes underground. Per-frame caches of terrain objects and other colliders are maintained. Terrain `MeshCollider` objects are explicitly skipped in ray-triangle tests — only the heightmap lookup is used.

**MeshCollider detail:** On each `Terrain`, the **`MeshFilter`** keeps a **full-resolution** mesh for editor picking. The **`MeshCollider`** may use a **subsampled** mesh when **`CollisionLodStep` > 1**, reducing physics triangle count without affecting `SampleHeightWorld`.

**TerrainStreamer:** For streamed tiles, **`CollisionRingRadius`** can disable **`MeshCollider`** on outer tiles while leaving rendering enabled; grounding still uses height sampling where applicable.

**Requires:** CapsuleCollider (auto-added via `[Require]`)

---

## Triggers and `TriggerVolume`

### `Collider.IsTrigger`

Any `BoxCollider`, `CapsuleCollider`, or `MeshCollider` can set **`IsTrigger`**. Trigger colliders are listed in `PhysicsCache.TriggerColliders`, are skipped by `CollisionWorld` contact resolution, and do not generate blocking collision response. Overlaps are detected by **`CharacterController`** and **`Rigidbody`** using AABB tests against trigger volumes.

### `Behavior` callbacks (Unity-style)

When a body overlaps a trigger, the engine invokes **`OnTriggerEnter`**, **`OnTriggerStay`**, and **`OnTriggerExit`** on **every enabled `Behavior`** on **both** GameObjects: the listener’s collider side receives `other` = the trigger collider, and the trigger’s GameObject receives `other` = the listener collider. Existing **`Rigidbody` / `CharacterController` C# events** (`OnTriggerEnter` etc.) still fire as well.

Implement gameplay by overriding these virtual methods on your scripts, or add a **`TriggerVolume`** component for inspector-driven presets.

### `GameObject.Tag` and `GameObject.Layer`

**Tag** is a string (default `Untagged`). **Layer** is an integer **0–31**. `TriggerVolume` can filter with **`LayerMask`** (bit mask) and optional **`TagFilter`**.

### `TriggerVolume` component

Add **`TriggerVolume`** (Physics category) on the same GameObject as a **trigger** collider. Configure:

| Field | Role |
|--------|------|
| **Preset** | `Custom`, `DamageZone`, `Checkpoint`, `SceneLoad`, `Activation` |
| **LayerMask** | If not zero, only overlapping objects whose **Layer** bit is set are handled |
| **TagFilter** | If non-empty, overlapping object’s **Tag** must match exactly |
| **OneShot** | After the first qualifying **Enter**, the `TriggerVolume` behavior disables itself |
| **CooldownSeconds** | Minimum time between **Enter** handling |
| **DamagePerSecond** | `DamageZone` only: applied on **Stay** to `IDamageable` on the **other** object |
| **PlayerTag** | `Checkpoint` only: tags that may set the checkpoint (default `Player`) |
| **SceneName** | `SceneLoad` preset: argument to `SceneManager.LoadScene` |
| **TargetPathOrName** | `Activation` preset: hierarchy path `Root/Child` or first matching **Name** in the scene |
| **EnableTargetOnEnter** / **DisableTargetOnExit** | Toggle target `GameObject.Enabled` |

**Checkpoint** stores the instigator position in **`CheckpointService`** (`LastCheckpointPosition`, `HasCheckpoint`) for your respawn logic.

### Inspector reactions (no C#)

Under **On enter** / **On exit**, add rows: **Kind** (`LoadScene`, `SetObjectEnabled`, `PublishChannel`), **Primary** string, and **Bool** (for `SetObjectEnabled`). `PublishChannel` raises **`TriggerVolumeSignal`** on **`EventBus`**; subscribe with `EventBus.Subscribe<TriggerVolumeSignal>(handler)` and read **`Channel`**.

### Scene view colors

Trigger wireframes use **blue** (neutral / custom / scene load / activation), **red** (`DamageZone`), or **green** (`Checkpoint`) when a **`TriggerVolume`** is present on the same GameObject. Solid colliders stay **DeepSkyBlue** as before.

### Limitations

Only objects simulated with **`CharacterController`** or **`Rigidbody`** generate trigger overlap checks today. Mesh trigger tests use **AABB** approximation. For sample damage handling, see **`Health`** in Standard Assets (`IDamageable`).

---

## PlayerMovement

First-person / third-person player controller that integrates input, camera control, and physics via the CharacterController.

### Input Flow
```
Input (WASD + Mouse)
    │
    ▼
PlayerMovement.Update()
    │
    ├─► Mouse look → Camera rotation (pitch ± 89°, yaw)
    ├─► WASD → Movement direction (relative to camera facing)
    ├─► Space → Jump (buffered for JumpBufferSeconds = 0.12s)
    ├─► Shift → Sprint (MoveSpeed × SprintMultiplier)
    │
    ▼
PlayerMovement.FixedUpdate()
    │
    ▼
CharacterController.Simulate(displacement, jump)
    │
    ▼
CollisionWorld → Resolve collisions → Final position
```

### Camera Modes
| Mode | Property | Behavior |
|------|----------|----------|
| **First Person** | `FirstPerson = true` | Camera placed at `FirstPersonOffset` (default: 0, 1.7, 0) relative to character |
| **Third Person** | `FirstPerson = false` | Camera at `ThirdPersonOffset` (default: 0, 1.7, -3.5) with smooth lerp following |

### Jump Buffering
Jump input is buffered for `JumpBufferSeconds` (default: 0.12s). If the player presses Jump while slightly in the air, the jump will execute as soon as `IsGrounded` becomes true. Combined with coyote time on the CharacterController, this provides responsive controls.

**Requires:** CharacterController, CapsuleCollider (auto-added via `[Require]`)

---

## CollisionWorld

`CollisionWorld` is the runtime collision manager. It maintains a registry of all colliders in the scene and provides spatial query methods.

### Collider Registry
All colliders are registered in `_colliders` list. The CollisionWorld collects all active colliders from the scene graph each frame.

### Query Methods

| Method | Return | Description |
|--------|--------|-------------|
| `QueryAABB(AABB)` | `List<Collider>` | All colliders overlapping the given AABB |
| `AnyOverlap(AABB)` | `bool` | Quick check if any collider overlaps the AABB |
| `Raycast(origin, dir, maxDist)` | `RayHit?` | Closest hit along the ray |
| `RaycastAll(origin, dir, maxDist)` | `List<RayHit>` | All hits along the ray |
| `OverlapSphere(center, radius)` | `List<Collider>` | All colliders within the sphere (expanded AABB test) |

### Collision Detection Flow
```
Query (e.g., Raycast)
    │
    ▼
Broad Phase: AABB overlap test
    │ • All colliders compute world-space AABBs
    │ • Simple min/max comparison on all 3 axes
    │ • Quickly eliminates distant colliders
    │
    ▼
Narrow Phase: Detailed intersection
    │
    ├─► Box vs Box: AABB overlap test
    ├─► Capsule vs Box: Closest point on capsule segment to box, distance test
    ├─► Capsule vs Mesh: Ray/segment vs triangle tests
    └─► Ray vs Mesh: Möller-Trumbore ray-triangle intersection
```

### Ray-AABB Intersection (Slab Method)
The `RayAABB()` method uses the **slab method** for ray-box intersection:
1. For each of the 3 axes (X, Y, Z), compute `tmin` and `tmax` entry/exit distances
2. The ray intersects the AABB if `max(tmin_x, tmin_y, tmin_z) < min(tmax_x, tmax_y, tmax_z)`
3. Returns the hit point and the face normal of the nearest intersection

---

## Scene layers and masks

Each `GameObject` has **`Layer`** (0–31), editable in the Inspector next to **Tag**. For physics:

- **`Physics.Raycast`**, **`RaycastAll`**, **`OverlapSphere`**, and **`CollisionWorld.QueryAABB`** accept an optional **`layerMask`** (`int`). Set bit `1 << layer` to include that layer; **`-1`** means all layers (default). Use `PhysicsLayerMask.Includes(mask, layer)` if you build masks in code.
- **`Rigidbody.CollisionLayerMask`** and **`CharacterController.CollisionLayerMask`** (default **`-1`**) restrict which layers that body collides against when resolving overlaps, mesh tests, and triggers.

---

## Physics Static API

The `Physics` class provides a Unity-style static convenience wrapper around `CollisionWorld`.

```csharp
// Gravity
Vector3 gravity = Physics.Gravity;  // Default: (0, -9.81, 0)

// Raycasting (optional layerMask; -1 = all layers)
int mask = (1 << 0) | (1 << 8); // e.g. Default + Custom8
if (Physics.Raycast(origin, direction, out var hit, maxDistance: 100f, layerMask: mask))
{
    Vector3 point = hit.Point;
    Vector3 normal = hit.Normal;
    float distance = hit.Distance;
    Collider collider = hit.Collider;
}

// All hits
var hits = Physics.RaycastAll(origin, direction, maxDistance, layerMask: -1);

// Overlap queries
var colliders = Physics.OverlapSphere(center, radius, layerMask: -1);
```

### PhysicsCache
Per-frame caching system for physics queries. Avoids redundant collision tests when multiple systems query the same data within a single frame.

---

## Collider Gizmos

In the Scene View, collider wireframes use the **Wireframe** shader in the gizmo GL pass (toggle with the Scene View **Gizmo** control).

| Kind | Color / style |
|------|----------------|
| **Solid colliders** (`IsTrigger` = false) | DeepSkyBlue-style wireframe (box, capsule, mesh, etc.) |
| **Triggers** (generic, no `TriggerVolume`) | Semi-transparent **blue** wire |
| **`TriggerVolume` + DamageZone** | Semi-transparent **red** |
| **`TriggerVolume` + Checkpoint** | Semi-transparent **green** |
| **Other `TriggerVolume` presets** | Same neutral **blue** as generic triggers |
| **MeshCollider** | Full wireframe when triangle count is modest; faint **AABB** overlay when too dense |

Selection outline uses a separate highlight pass.

---

## Raycasting

### Scene View Object Selection
The editor uses raycasting to select objects when clicking in the Scene View:
1. Mouse position is unprojected from screen coordinates to a world-space ray
2. The ray is tested against all mesh **bounding spheres** (broad phase — fast rejection)
3. On bounding sphere hit, the ray is tested against individual **triangles** (Möller-Trumbore algorithm)
4. The closest hit determines the selected object
5. `SelectionService.Set(hitObject)` updates the Inspector, Hierarchy, and gizmo state

### Terrain Raycasting
Terrain tools use a specialized raycast for brush placement:
1. Ray vs triangle mesh (standard Möller-Trumbore against the terrain's full-resolution mesh)
2. **Fallback:** Ray vs horizontal plane at Y=0 (for areas with holes or missing geometry)
3. The hit point determines the brush center for sculpting/painting operations

### Terrain Heightmap Collision (O(1))
For runtime character movement, the engine provides an optimized terrain collision path:

1. The `CharacterController` maintains per-frame caches of terrain objects and other colliders
2. For each terrain, `SampleHeightWorld()` performs O(1) bilinear interpolation of the heightmap grid
3. Returns the exact terrain height and approximate surface normal at any XZ position
4. Non-terrain `MeshCollider` raycasting is performed only against non-terrain objects
5. Terrain `MeshCollider` objects are **explicitly skipped** in the ray-triangle test loop

**Performance impact:**

| Terrain Size | Triangles | Brute-Force | Heightmap O(1) |
|--------------|-----------|-------------|----------------|
| 129 × 129 | ~32K | ~500ms/frame | <1ms/frame |
| 257 × 257 | ~131K | ~2000ms/frame | <1ms/frame |

---

## Physics Joints

The engine supports five joint constraint types defined in `PhysicsJoint`:

| Joint Type | Description |
|------------|-------------|
| **Fixed** | Rigidly connects two objects (no relative movement) |
| **Hinge** | Rotation around a single axis (doors, wheels) |
| **Spring** | Elastic connection with configurable stiffness and damping |
| **Slider** | Linear movement along a single axis (pistons, rails) |
| **BallSocket** | Rotation in all directions around a point (ragdoll shoulders) |

---

## Bounding Volume Hierarchy (BVH)

The `BVH` class provides spatial acceleration for collision queries. Instead of testing every collider linearly, the BVH organizes colliders into a tree structure for O(log n) query performance.

---

## RigidbodyPlayer

An alternative to `PlayerMovement` that uses Rigidbody physics for a momentum-based feel. Features include:
- **Force-based movement** with configurable ground/air drag
- **Swimming physics** — automatic 3D movement when in a water column (land caves stay dry). On planets, **`SwimOnPlanet()`** runs from `TryGetWaterColumn`: surface float on the waterline, tangent WASD, **Space** up, **Ctrl** dive (look-down + W is not dive). Releasing Ctrl hovers at depth
- **Jump impulse** — physics-driven jumping with buffered input
- **Natural push interactions** — momentum transfer between objects
- **Planet movement** — tangent-plane walk, jump along `LocalUp`, camera `WorldUp` smoothing
- **Cave-aware grounding** — short `SpherecastGameplay` / `RaycastDensityGameplay` probe along `-LocalUp` after penetration resolve (32 steps / 4 refine). Outer crust uses **surface mode** + `SampleCollisionRadius` on the visible leaf; heightfield radius only as last-resort fallback when the probe misses

Pair with **`PlanetPlayerSpawner`** for quick play-mode setup (retries spawn up to **12 s** while waiting for renderable leaves), or add manually with `Rigidbody` + `CapsuleCollider`.

See the [Components Reference](03_Components_Reference.md) for full property details.

---

## Planet Physics Integration

The planet pipeline integrates directly with runtime rigidbody and character physics:

1. `Rigidbody` / `CharacterController` find the nearest active `PlanetTerrain` (`FindNearestPlanetCached` — rebind after ~48 m or planet-count change)
2. `LocalUp` is computed from planet center to body position (radial gravity)
3. Gravity is applied along `-LocalUp` (fallback is world `-Y` when no planet is active)
4. **Surface mode** (`RefreshPlanetSurfaceMode`): walk the visible-leaf stand radius when radial ≥ crust − **6 m**; leave when radial < crust − **10 m** or `CameraBelowCrust`. Collision radius comes from `FindRenderableAtDirection` (the chunk you see), not a finer prefetch leaf or a coarser neighbor peak
5. Interior / cave grounding uses `SpherecastGameplay` / `RaycastDensityGameplay` (32 steps / 4 refine) against the **same density field as meshing**. Editor brushes keep the 96/10 pick. `ResolveDensityPenetration` pushes the body out of solid. `RigidbodyPlayer` uses a **short** downward probe (capsule height + step-up + ground snap), not a full radial ray to the core
6. On contact, the into-surface velocity component is removed and tangent motion is preserved
7. **Underwater:** `UnderwaterQuery` uses the **local water table** (`SampleWaterSurface`), not a single global sea radius. Planet **movement** starts when the body is in a basin (`TryGetWaterColumn`; lava is not swim water). The **underwater post pass** requires the camera/head ≥ **0.28–0.30 m** under that table and `IsPlanetSubmerged`. Surface float keeps the camera above the mesh (no crust-stand eye snap). Cave air below the crust stays dry. `Rigidbody` preserves underwater state only while actually submerged. See [Planet System — Planet water](13_Planet_System.md#planet-water).

`SampleSurfaceRadius` is **not** used for this contact; it remains the outer-crust radius for water/orbit/atmosphere/vegetation estimates. `PlanetPlayerSpawner` also stands on `SampleCollisionRadius`, not an isosurface that can land in a pit.

`RigidbodyPlayer` uses `Rigidbody.LocalUp` for movement and jumping:
- Movement direction is projected onto the local tangent plane
- Jump impulse is applied along local up
- Camera controllers set `Camera.WorldUp` from smoothed local up to keep horizon alignment stable while traversing curved surfaces

See [Planet System](13_Planet_System.md) for interior fill, stacked voxel shells, cave scales, chunk streaming, and `.planetvox` edits.

---

## Limitations

The physics system is designed primarily for character movement and collision queries. Current limitations include:

| Limitation | Description |
|------------|-------------|
| **Simplified rigid body dynamics** | Basic velocity and force support, no full constraint solver |
| **No continuous collision for non-characters** | CCD is only implemented in CharacterController |
| **Terrain holes in collision** | Heightmap lookup doesn't account for holes (height is interpolated even over holes) |
| **Joint simulation** | Joint constraints are defined but use simplified solving |
