# Game Engine — Physics and Collision

## Overview

The engine provides a basic collision detection system with colliders, a character controller, and a collision world for runtime physics queries. Physics interactions are processed during `FixedUpdate` in the game loop.

---

## Collider Types

### BoxCollider
An axis-aligned box shape for collision detection.

| Property | Type      | Default   | Description              |
|----------|-----------|-----------|--------------------------|
| `Center` | `Vector3` | (0,0,0)  | Offset from the transform origin |
| `Size`   | `Vector3` | (1,1,1)  | Box dimensions (width, height, depth) |

**Use cases**: Walls, floors, crates, doors, platforms.

### CapsuleCollider
A capsule shape (cylinder with hemispherical caps) primarily used for character controllers.

| Property    | Type      | Default | Description               |
|-------------|-----------|---------|---------------------------|
| `Center`    | `Vector3` | (0,0,0)| Offset from transform     |
| `Radius`    | `float`   | 0.5    | Capsule radius            |
| `Height`    | `float`   | 2.0    | Total height (caps + body)|
| `Direction` | `int`     | 1      | Up axis (0=X, 1=Y, 2=Z)  |

**Use cases**: Player characters, NPCs, cylindrical objects.

### MeshCollider
Uses the actual mesh geometry for precise collision detection.

| Property     | Type     | Description                            |
|--------------|----------|----------------------------------------|
| `Mesh`       | `Mesh`   | The collision mesh (can differ from render mesh) |
| `TargetPath` | `string` | Path to resolve a MeshFilter for collision data  |

**Use cases**: Complex static geometry (buildings, terrain, irregular shapes).

MeshCollider computes a world-space AABB (axis-aligned bounding box) for broad-phase culling and supports binding to a specific target transform for objects that move.

---

## CharacterController

A physics-based movement controller that handles gravity, ground detection, slope limiting, stepping, and collision response. It does not use rigid body physics — instead it uses sweep-and-slide collision resolution.

| Property      | Type    | Default | Description                     |
|---------------|---------|---------|---------------------------------|
| `Height`      | `float` | 2.0     | Controller capsule height       |
| `Radius`      | `float` | 0.5     | Controller capsule radius       |
| `StepOffset`  | `float` | 0.3     | Max step height to auto-climb   |
| `SlopeLimit`  | `float` | 45      | Max walkable slope (degrees)    |
| `SkinWidth`   | `float` | 0.08    | Collision skin thickness        |
| `IsGrounded`  | `bool`  | —       | Read-only: touching ground?     |

### How It Works
1. Each `FixedUpdate`, gravity is applied to the vertical velocity
2. Movement input is combined with gravity into a displacement vector
3. The controller resolves collisions against the `CollisionWorld`
4. Ground detection sets `IsGrounded` based on downward collision checks
5. Slope limiting prevents walking up surfaces steeper than `SlopeLimit`
6. Step detection allows climbing small ledges below `StepOffset`

**Requires**: CapsuleCollider (auto-added via `[Require]`)

---

## CollisionWorld

`CollisionWorld` is the runtime collision manager. It collects all colliders in the scene and provides query methods.

### Collision Detection Flow
```
Scene Graph
    │
    ▼
Collect all Colliders
    │
    ├─► BoxColliders → AABB tests
    ├─► CapsuleColliders → Capsule-AABB tests
    └─► MeshColliders → Triangle intersection tests
```

### Broad Phase
All colliders compute world-space AABBs. Collision queries first check AABB overlap before performing detailed intersection tests.

### Narrow Phase
- **Box vs Box**: AABB overlap test
- **Capsule vs Box**: Closest point on capsule segment to box, then distance test
- **Capsule vs Mesh**: Ray/segment vs triangle tests for precise mesh collision
- **Ray vs Mesh**: Möller-Trumbore ray-triangle intersection

---

## Collider Gizmos

In the Scene View, collider shapes are visualized as green wireframes when gizmos are enabled:
- **BoxCollider**: Green wireframe cube
- **CapsuleCollider**: Green wireframe capsule (with hemisphere caps)
- **MeshCollider**: Green wireframe of the collision mesh

Toggle visibility with the Gizmo button in the Scene View toolbar.

---

## PlayerMovement Integration

The `PlayerMovement` component integrates input, camera control, and physics:

```
Input (WASD + Mouse)
    │
    ▼
PlayerMovement.Update()
    │
    ├─► Mouse look → Camera rotation (pitch + yaw)
    ├─► WASD → Movement direction (relative to camera)
    ├─► Space → Jump (if grounded)
    ├─► Shift → Sprint multiplier
    │
    ▼
CharacterController.Move(displacement)
    │
    ▼
CollisionWorld → Resolve collisions → Final position
```

### Jump Buffering
Jump input is buffered for a few frames so the player doesn't need to press jump at the exact frame they're grounded. This provides more responsive controls.

---

## Raycasting

### Scene View Picking
The editor uses raycasting to select objects:
1. Mouse position is unprojected from screen to world-space ray
2. Ray is tested against all mesh bounding spheres (broad phase)
3. On hit, ray is tested against individual triangles (Möller-Trumbore)
4. Closest hit determines the selected object

### Terrain Raycasting
Terrain tools use a specialized raycast:
1. Ray vs triangle mesh (standard Moller-Trumbore)
2. Fallback: ray vs horizontal plane at Y=0 (for holes/missing geometry)
3. Hit point determines brush center for sculpting/painting

### Terrain Heightmap Collision (O(1))
For runtime character movement, the engine provides an optimized terrain collision path that avoids brute-force ray-triangle tests:

1. The `CharacterController` maintains per-frame caches of terrain objects and other colliders
2. For each terrain, `SampleHeightWorld()` performs O(1) bilinear interpolation of the heightmap grid
3. This returns the exact terrain height and approximate surface normal at any XZ position
4. Non-terrain `MeshCollider` raycasting is only performed against non-terrain objects
5. Terrain `MeshCollider` objects are explicitly skipped in the ray-triangle test loop

**Performance impact**: On a 129x129 terrain (32K+ triangles), this reduces per-frame collision from ~32K triangle tests to a single array lookup per terrain, bringing frame times from 500ms+ down to <16ms.

---

## Limitations

- No rigid body dynamics (no bouncing, no physics-driven motion)
- No joints or constraints
- No trigger volumes (enter/exit events)
- Collision is primarily designed for character movement
- No spatial partitioning for non-terrain colliders (checked linearly)
- Terrain collision uses heightmap lookup (fast but doesn't account for terrain holes in collision)
