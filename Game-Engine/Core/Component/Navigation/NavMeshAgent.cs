#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    // ────────────────────────────────────────────────────────────────
    //  NavMeshAgentStatus — easy-to-check status enum
    // ────────────────────────────────────────────────────────────────

    public enum NavMeshAgentStatus
    {
        Idle,           // No destination set
        Moving,         // Actively following a path
        Reached,        // Arrived at the destination
        PathNotFound    // Destination was set but no path exists
    }

    // ────────────────────────────────────────────────────────────────
    //  NavMeshAgent — attach to any GameObject to navigate
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// NavMesh agent component — automatically navigates toward a destination
    /// using A* pathfinding on the baked navigation mesh.
    /// <para>
    /// <b>Quick-start:</b><br/>
    /// 1. Call <c>NavMesh.Bake()</c> once after your scene is loaded.<br/>
    /// 2. Add <c>NavMeshAgent</c> to a GameObject.<br/>
    /// 3. Call <c>agent.SetDestination(worldPos)</c> or <c>agent.MoveTo(targetGO)</c>.<br/>
    /// 4. (Optional) subscribe to <c>agent.OnPathComplete</c>.
    /// </para>
    /// </summary>
    [ComponentCategory("Navigation")]
    public sealed class NavMeshAgent : Behavior
    {
        // ═══════════════════════════════════════════════════════════
        //  Inspector-visible properties
        // ═══════════════════════════════════════════════════════════

        [Persist] public float Speed            { get; set; } = 3.5f;
        [Persist] public float AngularSpeed     { get; set; } = 360f;     // deg/s
        [Persist] public float Acceleration     { get; set; } = 8f;
        [Persist] public float StoppingDistance  { get; set; } = 0.5f;
        [Persist] public float Height           { get; set; } = 2f;
        [Persist] public float Radius           { get; set; } = 0.5f;
        [Persist] public float AvoidanceRadius  { get; set; } = 1f;
        [Persist] public bool  AutoBraking      { get; set; } = true;
        [Persist] public bool  SnapToNavMesh    { get; set; } = true;     // follow navmesh height
        [Persist] public bool  AutoRepath        { get; set; } = true;     // repath when straying
        [Persist] public float RepathInterval   { get; set; } = 1f;       // seconds between auto-repaths
        [Persist] public int   AreaMask         { get; set; } = -1;       // all areas

        // ═══════════════════════════════════════════════════════════
        //  Runtime state (read-only for scripts)
        // ═══════════════════════════════════════════════════════════

        /// <summary>Current destination in world space.</summary>
        public SN.Vector3 Destination { get; private set; }

        /// <summary>Whether the agent currently has a valid path.</summary>
        public bool HasPath { get; private set; }

        /// <summary>Whether a path computation is pending (reserved for async).</summary>
        public bool PathPending { get; private set; }

        /// <summary>Total remaining distance along the path to the destination.</summary>
        public float RemainingDistance { get; private set; }

        /// <summary>Current movement velocity in world space.</summary>
        public SN.Vector3 Velocity { get; private set; }

        /// <summary>Current speed magnitude.</summary>
        public float CurrentSpeed => _currentSpeed;

        /// <summary>High-level status of the agent.</summary>
        public NavMeshAgentStatus Status { get; private set; } = NavMeshAgentStatus.Idle;

        /// <summary>Stop/resume the agent without clearing its path.</summary>
        public bool IsStopped { get; set; }

        /// <summary>The current smoothed path waypoints (read-only, useful for debug drawing).</summary>
        public IReadOnlyList<SN.Vector3>? Path => _path;

        /// <summary>Current waypoint index along the path.</summary>
        public int PathIndex => _pathIndex;

        // ═══════════════════════════════════════════════════════════
        //  Events
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Fired when the agent reaches the destination (within StoppingDistance),
        /// or when pathfinding fails. Check <see cref="Status"/> to know which.
        /// </summary>
        public event Action<NavMeshAgent>? OnPathComplete;

        // ═══════════════════════════════════════════════════════════
        //  Private state
        // ═══════════════════════════════════════════════════════════

        private List<SN.Vector3>? _path;
        private int   _pathIndex;
        private float _currentSpeed;
        private float _repathTimer;
        private float _stuckTimer;
        private SN.Vector3 _lastPos;
        private bool  _destinationReachedFired;

        // ═══════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════

        /// <summary>Navigate to a world-space destination.</summary>
        public bool SetDestination(SN.Vector3 destination)
        {
            Destination = destination;
            _destinationReachedFired = false;
            _repathTimer = 0f;
            _stuckTimer = 0f;

            _path = NavMesh.FindPath(GetWorldPos(), destination);
            HasPath = _path != null && _path.Count > 1;
            _pathIndex = HasPath ? 1 : 0;   // index 0 is the start point
            PathPending = false;

            if (HasPath)
            {
                Status = NavMeshAgentStatus.Moving;
                RemainingDistance = ComputeRemainingDistance();
            }
            else
            {
                Status = NavMeshAgentStatus.PathNotFound;
                OnPathComplete?.Invoke(this);
            }
            return HasPath;
        }

        /// <summary>Navigate to a world-space destination (engine Vector3 overload).</summary>
        public bool SetDestination(Vector3 destination)
            => SetDestination(new SN.Vector3((float)destination.X, (float)destination.Y, (float)destination.Z));

        /// <summary>Navigate toward another GameObject's position.</summary>
        public bool MoveTo(GameObject target)
        {
            if (target == null) return false;
            var p = target.Transform.Position;
            return SetDestination(new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z));
        }

        /// <summary>Warp the agent to a position (snaps to NavMesh if possible).</summary>
        public void Warp(SN.Vector3 position)
        {
            if (NavMesh.SamplePosition(position, out var snapped))
                position = snapped;
            Transform.Position = new Vector3(position.X, position.Y, position.Z);
            ClearPath();
        }

        /// <summary>Warp overload for engine Vector3.</summary>
        public void Warp(Vector3 position)
            => Warp(new SN.Vector3((float)position.X, (float)position.Y, (float)position.Z));

        /// <summary>Stop movement and clear the path.</summary>
        public void ClearPath()
        {
            _path = null;
            HasPath = false;
            _pathIndex = 0;
            Velocity = SN.Vector3.Zero;
            _currentSpeed = 0f;
            Status = NavMeshAgentStatus.Idle;
        }

        /// <summary>Pause the agent (keeps its path).</summary>
        public void Stop()  => IsStopped = true;

        /// <summary>Resume a paused agent.</summary>
        public void Resume() => IsStopped = false;

        // ═══════════════════════════════════════════════════════════
        //  Update loop
        // ═══════════════════════════════════════════════════════════

        public override void Update()
        {
            if (IsStopped || !HasPath || _path == null || _path.Count < 2)
            {
                if (HasPath && IsStopped) Velocity = SN.Vector3.Zero;
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            var pos = GetWorldPos();

            // ── Advance through waypoints ──
            while (_pathIndex < _path.Count - 1)
            {
                float d = SN.Vector3.Distance(pos, _path[_pathIndex]);
                if (d > StoppingDistance * 0.5f) break;
                _pathIndex++;
            }

            var target = _path[_pathIndex];
            var toTarget = target - pos;
            float distToWaypoint = toTarget.Length();
            RemainingDistance = ComputeRemainingDistance(pos);

            // ── Reached destination? ──
            if (_pathIndex >= _path.Count - 1 && RemainingDistance <= StoppingDistance)
            {
                Velocity = SN.Vector3.Zero;
                _currentSpeed = 0f;
                HasPath = false;
                Status = NavMeshAgentStatus.Reached;
                if (!_destinationReachedFired)
                {
                    _destinationReachedFired = true;
                    OnPathComplete?.Invoke(this);
                }
                return;
            }

            // ── Compute desired direction ──
            if (distToWaypoint > 0.001f)
            {
                var dir = SN.Vector3.Normalize(toTarget);

                // ── Speed with auto-braking ──
                float targetSpeed = Speed;
                if (AutoBraking)
                {
                    float brakeDist = Speed * Speed / (2f * Math.Max(Acceleration, 0.01f));
                    if (RemainingDistance < brakeDist)
                        targetSpeed = Speed * Math.Max(RemainingDistance / brakeDist, 0.1f);
                }

                // ── Smooth acceleration / deceleration ──
                if (_currentSpeed < targetSpeed)
                    _currentSpeed = Math.Min(_currentSpeed + Acceleration * dt, targetSpeed);
                else
                    _currentSpeed = Math.Max(_currentSpeed - Acceleration * dt, targetSpeed);

                Velocity = dir * _currentSpeed;

                // ── Move ──
                var newPos = pos + Velocity * dt;

                // ── Obstacle avoidance ──
                newPos = ApplyAvoidance(newPos, dir, dt);

                // ── Snap to NavMesh height ──
                if (SnapToNavMesh && NavMesh.SampleHeight(newPos, out float navY))
                    newPos.Y = navY;

                Transform.Position = new Vector3(newPos.X, newPos.Y, newPos.Z);

                // ── Rotate to face movement direction ──
                if (AngularSpeed > 0f && (dir.X * dir.X + dir.Z * dir.Z) > 0.0001f)
                {
                    float targetYaw = MathF.Atan2(dir.X, dir.Z) * (180f / MathF.PI);
                    float currentYaw = (float)Transform.Rotation.Y;
                    float diff = NormalizeAngle(targetYaw - currentYaw);

                    float maxTurn = AngularSpeed * dt;
                    float turn = Math.Clamp(diff, -maxTurn, maxTurn);
                    Transform.Rotation = new Vector3(
                        Transform.Rotation.X,
                        currentYaw + turn,
                        Transform.Rotation.Z);
                }
            }

            // ── Auto-repath when stuck ──
            if (AutoRepath)
            {
                _repathTimer += dt;

                // Check if stuck (hasn't moved much)
                float moved = SN.Vector3.Distance(pos, _lastPos);
                if (moved < _currentSpeed * dt * 0.1f && _currentSpeed > 0.01f)
                    _stuckTimer += dt;
                else
                    _stuckTimer = 0f;

                if ((_repathTimer >= RepathInterval && moved < 0.01f) || _stuckTimer > 1f)
                {
                    _repathTimer = 0f;
                    _stuckTimer = 0f;
                    SetDestination(Destination);   // repath
                }
            }
            _lastPos = pos;
        }

        // ═══════════════════════════════════════════════════════════
        //  Avoidance
        // ═══════════════════════════════════════════════════════════

        private SN.Vector3 ApplyAvoidance(SN.Vector3 pos, SN.Vector3 dir, float dt)
        {
            foreach (var other in NavMesh.Agents)
            {
                if (ReferenceEquals(other, this) || !other.IsActiveAndEnabled) continue;

                var otherPos = other.GetWorldPos();
                var toOther = otherPos - pos;
                float dist = toOther.Length();
                float minDist = AvoidanceRadius + other.AvoidanceRadius;

                if (dist < minDist && dist > 0.001f)
                {
                    var pushDir = SN.Vector3.Normalize(pos - otherPos);
                    float pushForce = (minDist - dist) / minDist;
                    pos += pushDir * pushForce * Speed * dt;
                }
            }
            return pos;
        }

        // ═══════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════

        private SN.Vector3 GetWorldPos()
            => new((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

        /// <summary>Total remaining distance from current position through all remaining waypoints.</summary>
        private float ComputeRemainingDistance(SN.Vector3? from = null)
        {
            if (_path == null || _pathIndex >= _path.Count) return 0f;

            var pos = from ?? GetWorldPos();
            float total = SN.Vector3.Distance(pos, _path[_pathIndex]);
            for (int i = _pathIndex; i < _path.Count - 1; i++)
                total += SN.Vector3.Distance(_path[i], _path[i + 1]);
            return total;
        }

        private static float NormalizeAngle(float angle)
        {
            while (angle > 180f) angle -= 360f;
            while (angle < -180f) angle += 360f;
            return angle;
        }

        // ═══════════════════════════════════════════════════════════
        //  Registry
        // ═══════════════════════════════════════════════════════════

        public override void OnEnable()
        {
            base.OnEnable();
            NavMesh.RegisterAgent(this);
        }

        public override void OnDisable()
        {
            NavMesh.UnregisterAgent(this);
            base.OnDisable();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  NavMesh — static navigation mesh system
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Static NavMesh system — bake walkable geometry and query paths.
    /// <para>
    /// <b>Usage:</b><br/>
    /// <c>NavMesh.Bake();</c> — Bake from all scene terrain and mesh colliders.<br/>
    /// <c>NavMesh.FindPath(a, b);</c> — A* pathfinding with optional smoothing.<br/>
    /// <c>NavMesh.SamplePosition(p, out hit);</c> — Find closest point on NavMesh.<br/>
    /// <c>NavMesh.SampleHeight(p, out y);</c> — Get NavMesh surface height at XZ.<br/>
    /// <c>NavMesh.Raycast(o, d, max, out hit);</c> — Raycast against the mesh.
    /// </para>
    /// </summary>
    public static class NavMesh
    {
        // ── Data ──
        private static SN.Vector3[]? _vertices;
        private static int[]? _triangles;
        private static bool _baked;

        // ── Agent registry ──
        private static readonly List<NavMeshAgent> _agents = new(16);
        public static IReadOnlyList<NavMeshAgent> Agents => _agents;

        internal static void RegisterAgent(NavMeshAgent a)
        {
            if (!_agents.Contains(a)) _agents.Add(a);
        }

        internal static void UnregisterAgent(NavMeshAgent a) => _agents.Remove(a);

        /// <summary>Whether a navigation mesh has been baked.</summary>
        public static bool IsBaked => _baked;

        /// <summary>Baked vertices (for debug visualization).</summary>
        public static SN.Vector3[]? Vertices => _vertices;

        /// <summary>Baked triangle indices (for debug visualization).</summary>
        public static int[]? Triangles => _triangles;

        // ─────────────────────────────────────────────────────────
        //  Bake
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Bake the navigation mesh from scene geometry (terrain + static mesh colliders).
        /// Call once after the scene is loaded or geometry changes.
        /// </summary>
        public static void Bake(float agentHeight = 2f, float agentRadius = 0.5f, float maxSlope = 45f)
        {
            var verts = new List<SN.Vector3>();
            var tris = new List<int>();

            foreach (var root in SceneService.Root)
                CollectGeometry(root, SN.Matrix4x4.Identity, verts, tris, maxSlope);

            if (verts.Count == 0)
            {
                Log.Warning("[NavMesh] No geometry found for baking.");
                _baked = false;
                return;
            }

            _vertices = verts.ToArray();
            _triangles = tris.ToArray();
            _baked = true;

            Log.Success($"[NavMesh] Baked: {verts.Count} vertices, {tris.Count / 3} triangles");
        }

        /// <summary>Clear the baked NavMesh data.</summary>
        public static void Clear()
        {
            _vertices = null;
            _triangles = null;
            _baked = false;
        }

        // ─────────────────────────────────────────────────────────
        //  FindPath  (A* + optional string-pulling)
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Find a smoothed path from <paramref name="start"/> to <paramref name="end"/>
        /// using A* on triangle adjacency, then simple string-pulling to remove zig-zag.
        /// </summary>
        public static List<SN.Vector3>? FindPath(SN.Vector3 start, SN.Vector3 end)
        {
            if (!_baked || _vertices == null || _triangles == null)
                return null;

            int startTri = FindClosestTriangle(start);
            int endTri   = FindClosestTriangle(end);
            if (startTri < 0 || endTri < 0) return null;

            // Same triangle — direct path
            if (startTri == endTri)
                return new List<SN.Vector3> { start, end };

            // Build adjacency + A*
            int triCount = _triangles.Length / 3;
            var adj = BuildAdjacency(triCount);
            var triPath = AStarSearch(startTri, endTri, adj);
            if (triPath == null) return null;

            // Convert to waypoints (triangle centroids)
            var waypoints = new List<SN.Vector3>(triPath.Count + 2) { start };
            foreach (int triIdx in triPath)
                waypoints.Add(TriCentroid(triIdx));
            waypoints.Add(end);

            // String-pulling to smooth the path
            SmoothPath(waypoints);

            return waypoints;
        }

        // ─────────────────────────────────────────────────────────
        //  SamplePosition — closest point on NavMesh
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Find the closest point on the NavMesh to <paramref name="position"/>.
        /// Returns true if a point was found within <paramref name="maxDistance"/>.
        /// </summary>
        public static bool SamplePosition(SN.Vector3 position, out SN.Vector3 navPos, float maxDistance = 10f)
        {
            navPos = position;
            if (!_baked || _vertices == null || _triangles == null) return false;

            float bestDist2 = maxDistance * maxDistance;
            bool found = false;

            for (int i = 0; i < _triangles.Length; i += 3)
            {
                var v0 = _vertices[_triangles[i]];
                var v1 = _vertices[_triangles[i + 1]];
                var v2 = _vertices[_triangles[i + 2]];

                var closest = ClosestPointOnTriangle(position, v0, v1, v2);
                float d2 = SN.Vector3.DistanceSquared(position, closest);
                if (d2 < bestDist2)
                {
                    bestDist2 = d2;
                    navPos = closest;
                    found = true;
                }
            }
            return found;
        }

        // ─────────────────────────────────────────────────────────
        //  SampleHeight — get NavMesh Y at an XZ position
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Get the NavMesh surface height at the given XZ position.
        /// Uses barycentric interpolation for accurate height.
        /// </summary>
        public static bool SampleHeight(SN.Vector3 position, out float height)
        {
            height = position.Y;
            if (!_baked || _vertices == null || _triangles == null) return false;

            float bestXZDist2 = float.MaxValue;
            bool found = false;

            for (int i = 0; i < _triangles.Length; i += 3)
            {
                var v0 = _vertices[_triangles[i]];
                var v1 = _vertices[_triangles[i + 1]];
                var v2 = _vertices[_triangles[i + 2]];

                // Check if point XZ is inside the triangle XZ projection
                if (PointInTriangleXZ(position, v0, v1, v2, out float u, out float v))
                {
                    float w = 1f - u - v;
                    float h = w * v0.Y + u * v1.Y + v * v2.Y;

                    // Among all triangles containing this XZ, pick the closest Y
                    float yDiff = MathF.Abs(h - position.Y);
                    if (yDiff < MathF.Sqrt(bestXZDist2))
                    {
                        bestXZDist2 = yDiff * yDiff;
                        height = h;
                        found = true;
                    }
                }
            }

            // Fallback: if XZ wasn't inside any triangle, find nearest triangle centroid
            if (!found)
            {
                float bestD = float.MaxValue;
                for (int i = 0; i < _triangles.Length; i += 3)
                {
                    var c = (_vertices[_triangles[i]] + _vertices[_triangles[i + 1]] + _vertices[_triangles[i + 2]]) / 3f;
                    float d = (position.X - c.X) * (position.X - c.X) + (position.Z - c.Z) * (position.Z - c.Z);
                    if (d < bestD && d < 25f) // within 5 units
                    {
                        bestD = d;
                        height = c.Y;
                        found = true;
                    }
                }
            }
            return found;
        }

        // ─────────────────────────────────────────────────────────
        //  Raycast
        // ─────────────────────────────────────────────────────────

        /// <summary>Raycast against the NavMesh surface.</summary>
        public static bool Raycast(SN.Vector3 origin, SN.Vector3 direction, float maxDist, out SN.Vector3 hitPoint)
        {
            hitPoint = origin;
            if (!_baked || _vertices == null || _triangles == null) return false;

            float bestT = float.MaxValue;
            bool hit = false;

            for (int i = 0; i < _triangles.Length; i += 3)
            {
                var v0 = _vertices[_triangles[i]];
                var v1 = _vertices[_triangles[i + 1]];
                var v2 = _vertices[_triangles[i + 2]];

                if (RayTriangle(origin, direction, v0, v1, v2, out float t) && t > 0 && t < bestT && t <= maxDist)
                {
                    bestT = t;
                    hitPoint = origin + direction * t;
                    hit = true;
                }
            }
            return hit;
        }

        // ─────────────────────────────────────────────────────────
        //  Geometry collection (bake internals)
        // ─────────────────────────────────────────────────────────

        private static void CollectGeometry(GameObject go, SN.Matrix4x4 parentWorld,
            List<SN.Vector3> verts, List<int> tris, float maxSlope)
        {
            var world = TransformUtil.WorldFromTransform(go.Transform) * parentWorld;

            foreach (var b in go.Behaviors)
            {
                if (b is Terrain terrain && terrain.Enabled && terrain.Heights != null)
                {
                    CollectTerrainGeometry(terrain, world, verts, tris, maxSlope);
                }
                else if (b is MeshFilter mf && mf.Enabled && mf.Mesh != null)
                {
                    CollectMeshGeometry(mf.Mesh, world, verts, tris, maxSlope);
                }
            }

            foreach (var child in go.Children)
                CollectGeometry(child, world, verts, tris, maxSlope);
        }

        private static void CollectMeshGeometry(Mesh mesh, SN.Matrix4x4 world,
            List<SN.Vector3> verts, List<int> tris, float maxSlope)
        {
            if (mesh.Vertices == null || mesh.TriIndices == null) return;

            int baseIdx = verts.Count;
            for (int i = 0; i < mesh.Vertices.Length; i++)
                verts.Add(SN.Vector3.Transform(mesh.Vertices[i], world));

            for (int i = 0; i < mesh.TriIndices.Length; i += 3)
            {
                var v0 = verts[baseIdx + mesh.TriIndices[i]];
                var v1 = verts[baseIdx + mesh.TriIndices[i + 1]];
                var v2 = verts[baseIdx + mesh.TriIndices[i + 2]];

                var normal = SN.Vector3.Cross(v1 - v0, v2 - v0);
                if (normal.LengthSquared() > 0.0001f)
                {
                    normal = SN.Vector3.Normalize(normal);
                    float slopeAngle = MathF.Acos(Math.Clamp(
                        SN.Vector3.Dot(normal, SN.Vector3.UnitY), -1f, 1f)) * (180f / MathF.PI);
                    if (slopeAngle <= maxSlope)
                    {
                        tris.Add(baseIdx + mesh.TriIndices[i]);
                        tris.Add(baseIdx + mesh.TriIndices[i + 1]);
                        tris.Add(baseIdx + mesh.TriIndices[i + 2]);
                    }
                }
            }
        }

        private static void CollectTerrainGeometry(Terrain terrain, SN.Matrix4x4 world,
            List<SN.Vector3> verts, List<int> tris, float maxSlope)
        {
            int rx = terrain.ResX, rz = terrain.ResZ;
            float sx = terrain.SizeX, sz = terrain.SizeZ, hs = terrain.HeightScale;
            // Terrain mesh is centered: X in [-SizeX/2, +SizeX/2], Z in [-SizeZ/2, +SizeZ/2]
            float hx = sx * 0.5f, hz = sz * 0.5f;

            int baseIdx = verts.Count;
            for (int z = 0; z < rz; z++)
            {
                float tz = (rz == 1) ? 0f : (float)z / (rz - 1);
                for (int x = 0; x < rx; x++)
                {
                    float tx = (rx == 1) ? 0f : (float)x / (rx - 1);
                    float py = terrain.Heights![z * rx + x] * hs;
                    var localPos = new SN.Vector3(-hx + tx * sx, py, -hz + tz * sz);
                    verts.Add(SN.Vector3.Transform(localPos, world));
                }
            }

            for (int z = 0; z < rz - 1; z++)
            {
                for (int x = 0; x < rx - 1; x++)
                {
                    int a = baseIdx + z * rx + x;
                    int b = a + 1;
                    int c = a + rx;
                    int d = c + 1;

                    // Check slope for each triangle
                    AddTriIfWalkable(verts, tris, a, c, b, maxSlope);
                    AddTriIfWalkable(verts, tris, b, c, d, maxSlope);
                }
            }
        }

        private static void AddTriIfWalkable(List<SN.Vector3> verts, List<int> tris,
            int i0, int i1, int i2, float maxSlope)
        {
            var normal = SN.Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
            if (normal.LengthSquared() < 0.0001f) return;
            normal = SN.Vector3.Normalize(normal);
            float angle = MathF.Acos(Math.Clamp(
                SN.Vector3.Dot(normal, SN.Vector3.UnitY), -1f, 1f)) * (180f / MathF.PI);
            if (angle <= maxSlope)
            {
                tris.Add(i0);
                tris.Add(i1);
                tris.Add(i2);
            }
        }

        // ─────────────────────────────────────────────────────────
        //  A* search
        // ─────────────────────────────────────────────────────────

        private static int FindClosestTriangle(SN.Vector3 point)
        {
            if (_vertices == null || _triangles == null) return -1;

            float bestDist = float.MaxValue;
            int bestIdx = -1;

            for (int i = 0; i < _triangles.Length; i += 3)
            {
                var centroid = (_vertices[_triangles[i]] + _vertices[_triangles[i + 1]] + _vertices[_triangles[i + 2]]) / 3f;
                float dist = SN.Vector3.DistanceSquared(point, centroid);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i / 3;
                }
            }
            return bestIdx;
        }

        private static Dictionary<int, List<int>> BuildAdjacency(int triCount)
        {
            var adj = new Dictionary<int, List<int>>(triCount);
            var edgeToTri = new Dictionary<long, List<int>>(triCount * 3);

            for (int t = 0; t < triCount; t++)
            {
                adj[t] = new List<int>(3);
                int i = t * 3;
                AddEdge(edgeToTri, _triangles![i], _triangles[i + 1], t);
                AddEdge(edgeToTri, _triangles[i + 1], _triangles[i + 2], t);
                AddEdge(edgeToTri, _triangles[i + 2], _triangles[i], t);
            }

            foreach (var triList in edgeToTri.Values)
            {
                for (int a = 0; a < triList.Count; a++)
                    for (int b = a + 1; b < triList.Count; b++)
                    {
                        var ta = triList[a]; var tb = triList[b];
                        if (!adj[ta].Contains(tb)) adj[ta].Add(tb);
                        if (!adj[tb].Contains(ta)) adj[tb].Add(ta);
                    }
            }
            return adj;
        }

        private static void AddEdge(Dictionary<long, List<int>> map, int a, int b, int tri)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                map[key] = list;
            }
            list.Add(tri);
        }

        private static List<int>? AStarSearch(int start, int end, Dictionary<int, List<int>> adj)
        {
            if (start == end) return new List<int> { start };

            var openSet = new SortedSet<(float f, int node)>(
                Comparer<(float, int)>.Create((a, b) =>
                    a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2)));

            var gScore = new Dictionary<int, float> { [start] = 0 };
            var cameFrom = new Dictionary<int, int>();

            openSet.Add((TriDistance(start, end), start));

            while (openSet.Count > 0)
            {
                var current = openSet.Min;
                openSet.Remove(current);
                int node = current.node;

                if (node == end)
                {
                    var path = new List<int>();
                    int c = end;
                    while (cameFrom.ContainsKey(c)) { path.Add(c); c = cameFrom[c]; }
                    path.Add(start);
                    path.Reverse();
                    return path;
                }

                if (!adj.TryGetValue(node, out var neighbors)) continue;

                foreach (int neighbor in neighbors)
                {
                    float tentG = gScore[node] + TriDistance(node, neighbor);
                    if (!gScore.TryGetValue(neighbor, out float prevG) || tentG < prevG)
                    {
                        cameFrom[neighbor] = node;
                        gScore[neighbor] = tentG;
                        openSet.Add((tentG + TriDistance(neighbor, end), neighbor));
                    }
                }
            }
            return null;
        }

        private static float TriDistance(int triA, int triB)
        {
            if (_vertices == null || _triangles == null) return float.MaxValue;
            return SN.Vector3.Distance(TriCentroid(triA), TriCentroid(triB));
        }

        private static SN.Vector3 TriCentroid(int triIdx)
        {
            int i = triIdx * 3;
            return (_vertices![_triangles![i]] + _vertices[_triangles[i + 1]] + _vertices[_triangles[i + 2]]) / 3f;
        }

        // ─────────────────────────────────────────────────────────
        //  String-pulling (simple path smoothing)
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Remove redundant waypoints by checking line-of-sight on the NavMesh.
        /// Modifies the list in-place.
        /// </summary>
        private static void SmoothPath(List<SN.Vector3> waypoints)
        {
            if (waypoints.Count <= 2) return;

            int i = 0;
            while (i < waypoints.Count - 2)
            {
                // Can we skip waypoints[i+1] by going straight from i to i+2?
                if (HasLineOfSight(waypoints[i], waypoints[i + 2]))
                    waypoints.RemoveAt(i + 1);
                else
                    i++;
            }
        }

        /// <summary>
        /// Check if there's a clear path on the NavMesh between two points
        /// by sampling along the line and ensuring each sample is on the mesh.
        /// </summary>
        private static bool HasLineOfSight(SN.Vector3 a, SN.Vector3 b)
        {
            if (_vertices == null || _triangles == null) return false;

            float dist = SN.Vector3.Distance(a, b);
            int steps = Math.Max(2, (int)(dist * 2f)); // 2 samples per unit
            float maxOffMeshDist = 2f; // tolerance

            for (int s = 1; s < steps; s++)
            {
                float t = s / (float)steps;
                var p = SN.Vector3.Lerp(a, b, t);

                // Check if this point is close to the NavMesh
                bool onMesh = false;
                for (int i = 0; i < _triangles.Length; i += 3)
                {
                    var v0 = _vertices[_triangles[i]];
                    var v1 = _vertices[_triangles[i + 1]];
                    var v2 = _vertices[_triangles[i + 2]];

                    var closest = ClosestPointOnTriangle(p, v0, v1, v2);
                    if (SN.Vector3.DistanceSquared(p, closest) < maxOffMeshDist * maxOffMeshDist)
                    {
                        onMesh = true;
                        break;
                    }
                }
                if (!onMesh) return false;
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────
        //  Geometry helpers
        // ─────────────────────────────────────────────────────────

        /// <summary>Closest point on a triangle to a given point.</summary>
        private static SN.Vector3 ClosestPointOnTriangle(SN.Vector3 p, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = SN.Vector3.Dot(ab, ap);
            float d2 = SN.Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a; // vertex A region

            var bp = p - b;
            float d3 = SN.Vector3.Dot(ab, bp);
            float d4 = SN.Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b; // vertex B region

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return a + ab * v; // edge AB
            }

            var cp = p - c;
            float d5 = SN.Vector3.Dot(ab, cp);
            float d6 = SN.Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c; // vertex C region

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return a + ac * w; // edge AC
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + (c - b) * w; // edge BC
            }

            // Inside triangle
            float denom = 1f / (va + vb + vc);
            float v2 = vb * denom;
            float w2 = vc * denom;
            return a + ab * v2 + ac * w2;
        }

        /// <summary>
        /// Check if a point's XZ projection is inside a triangle's XZ projection.
        /// Returns barycentric coordinates (u, v) so height = (1-u-v)*v0.Y + u*v1.Y + v*v2.Y.
        /// </summary>
        private static bool PointInTriangleXZ(SN.Vector3 p, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c,
            out float u, out float v)
        {
            u = v = 0f;
            float ax = b.X - a.X, az = b.Z - a.Z;
            float bx = c.X - a.X, bz = c.Z - a.Z;
            float cx = p.X - a.X, cz = p.Z - a.Z;

            float d00 = ax * ax + az * az;
            float d01 = ax * bx + az * bz;
            float d11 = bx * bx + bz * bz;
            float d20 = cx * ax + cz * az;
            float d21 = cx * bx + cz * bz;

            float denom = d00 * d11 - d01 * d01;
            if (MathF.Abs(denom) < 1e-10f) return false;

            float invDenom = 1f / denom;
            u = (d11 * d20 - d01 * d21) * invDenom;
            v = (d00 * d21 - d01 * d20) * invDenom;

            return u >= -1e-6f && v >= -1e-6f && (u + v) <= 1f + 1e-6f;
        }

        /// <summary>Möller–Trumbore ray-triangle intersection.</summary>
        private static bool RayTriangle(SN.Vector3 orig, SN.Vector3 dir,
            SN.Vector3 v0, SN.Vector3 v1, SN.Vector3 v2, out float t)
        {
            t = 0;
            var e1 = v1 - v0;
            var e2 = v2 - v0;
            var p = SN.Vector3.Cross(dir, e2);
            float det = SN.Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-8f) return false;

            float invDet = 1f / det;
            var tvec = orig - v0;
            float u = SN.Vector3.Dot(tvec, p) * invDet;
            if (u < 0f || u > 1f) return false;

            var q = SN.Vector3.Cross(tvec, e1);
            float v = SN.Vector3.Dot(dir, q) * invDet;
            if (v < 0f || u + v > 1f) return false;

            t = SN.Vector3.Dot(e2, q) * invDet;
            return t > 0f;
        }
    }
}
