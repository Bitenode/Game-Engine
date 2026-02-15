#nullable enable
using System;
using System.Collections.Generic;
using SN = System.Numerics;

namespace Game_Engine.Core.Physics
{
    /// <summary>
    /// Bounding Volume Hierarchy for O(log n) spatial queries.
    /// Supports AABB queries, raycasts, and sphere overlap tests.
    /// Rebuilt each physics frame from the active collider set.
    /// </summary>
    public sealed class BVH
    {
        /// <summary>A node in the BVH tree. Leaf nodes hold a collider reference.</summary>
        private struct Node
        {
            public SN.Vector3 Min, Max;
            public int Left, Right;      // child indices (-1 for leaf)
            public int ColliderIndex;     // index into the collider list (-1 for internal)
        }

        private Node[] _nodes = Array.Empty<Node>();
        private int _nodeCount;
        private int _root = -1;
        private Component.Collider[] _colliders = Array.Empty<Component.Collider>();
        private int _colliderCount;

        /// <summary>Number of colliders in the BVH.</summary>
        public int Count => _colliderCount;

        /// <summary>
        /// Build the BVH from a list of active colliders.
        /// Call once per frame after colliders are gathered.
        /// </summary>
        public void Build(IReadOnlyList<Component.Collider> colliders)
        {
            _colliderCount = colliders.Count;
            if (_colliderCount == 0)
            {
                _root = -1;
                return;
            }

            // Ensure arrays are big enough
            if (_colliders.Length < _colliderCount)
                _colliders = new Component.Collider[_colliderCount * 2];

            // Max nodes = 2 * n - 1 for a full binary tree
            int maxNodes = Math.Max(2 * _colliderCount, 4);
            if (_nodes.Length < maxNodes)
                _nodes = new Node[maxNodes];

            _nodeCount = 0;

            // Copy colliders and compute AABBs
            var indices = new int[_colliderCount];
            for (int i = 0; i < _colliderCount; i++)
            {
                _colliders[i] = colliders[i];
                indices[i] = i;
            }

            _root = BuildRecursive(indices, 0, _colliderCount);
        }

        private int BuildRecursive(int[] indices, int start, int end)
        {
            int count = end - start;
            if (count <= 0) return -1;

            int nodeIdx = _nodeCount++;

            if (count == 1)
            {
                // Leaf node
                var aabb = _colliders[indices[start]].GetWorldAABB();
                _nodes[nodeIdx] = new Node
                {
                    Min = aabb.Min,
                    Max = aabb.Max,
                    Left = -1,
                    Right = -1,
                    ColliderIndex = indices[start]
                };
                return nodeIdx;
            }

            // Compute combined AABB
            var firstAABB = _colliders[indices[start]].GetWorldAABB();
            SN.Vector3 min = firstAABB.Min, max = firstAABB.Max;
            SN.Vector3 centroidMin = (firstAABB.Min + firstAABB.Max) * 0.5f;
            SN.Vector3 centroidMax = centroidMin;

            for (int i = start + 1; i < end; i++)
            {
                var aabb = _colliders[indices[i]].GetWorldAABB();
                min = SN.Vector3.Min(min, aabb.Min);
                max = SN.Vector3.Max(max, aabb.Max);
                var centroid = (aabb.Min + aabb.Max) * 0.5f;
                centroidMin = SN.Vector3.Min(centroidMin, centroid);
                centroidMax = SN.Vector3.Max(centroidMax, centroid);
            }

            // Pick split axis (longest centroid extent)
            var extent = centroidMax - centroidMin;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0
                     : extent.Y >= extent.Z ? 1 : 2;

            // Sort along split axis by centroid
            float splitValue = GetComponent(centroidMin, axis) + GetComponent(extent, axis) * 0.5f;

            // Partition
            int mid = Partition(indices, start, end, axis, splitValue);
            if (mid == start || mid == end)
                mid = start + count / 2; // fallback to median split

            // Reserve this node index and build children
            int left = BuildRecursive(indices, start, mid);
            int right = BuildRecursive(indices, mid, end);

            // Combine child bounds
            SN.Vector3 combinedMin = min, combinedMax = max;
            if (left >= 0 && right >= 0)
            {
                combinedMin = SN.Vector3.Min(_nodes[left].Min, _nodes[right].Min);
                combinedMax = SN.Vector3.Max(_nodes[left].Max, _nodes[right].Max);
            }

            _nodes[nodeIdx] = new Node
            {
                Min = combinedMin,
                Max = combinedMax,
                Left = left,
                Right = right,
                ColliderIndex = -1
            };

            return nodeIdx;
        }

        private int Partition(int[] indices, int start, int end, int axis, float splitValue)
        {
            int left = start;
            for (int i = start; i < end; i++)
            {
                var aabb = _colliders[indices[i]].GetWorldAABB();
                float centroid = GetComponent((aabb.Min + aabb.Max) * 0.5f, axis);
                if (centroid < splitValue)
                {
                    (indices[left], indices[i]) = (indices[i], indices[left]);
                    left++;
                }
            }
            return left;
        }

        private static float GetComponent(SN.Vector3 v, int axis) => axis switch
        {
            0 => v.X, 1 => v.Y, _ => v.Z
        };

        // ── Queries ──

        /// <summary>Query all colliders whose AABB overlaps the given AABB.</summary>
        public void QueryAABB(SN.Vector3 queryMin, SN.Vector3 queryMax, List<Component.Collider> results)
        {
            if (_root < 0) return;
            QueryAABBRecursive(_root, queryMin, queryMax, results);
        }

        private void QueryAABBRecursive(int nodeIdx, SN.Vector3 qMin, SN.Vector3 qMax, List<Component.Collider> results)
        {
            if (nodeIdx < 0) return;
            ref var node = ref _nodes[nodeIdx];

            // Skip if this node doesn't overlap the query
            if (!AABBOverlap(node.Min, node.Max, qMin, qMax)) return;

            if (node.ColliderIndex >= 0)
            {
                // Leaf — add the collider
                results.Add(_colliders[node.ColliderIndex]);
                return;
            }

            QueryAABBRecursive(node.Left, qMin, qMax, results);
            QueryAABBRecursive(node.Right, qMin, qMax, results);
        }

        /// <summary>Raycast through the BVH. Returns the closest hit.</summary>
        public bool Raycast(SN.Vector3 origin, SN.Vector3 direction, float maxDist,
                            out SN.Vector3 hitPoint, out SN.Vector3 hitNormal, out float hitDist,
                            out Component.Collider? hitCollider, bool ignoreTriggers = true)
        {
            hitPoint = SN.Vector3.Zero;
            hitNormal = SN.Vector3.Zero;
            hitDist = maxDist;
            hitCollider = null;

            if (_root < 0) return false;
            return RaycastRecursive(_root, origin, direction, ref hitDist, ref hitPoint, ref hitNormal, ref hitCollider, ignoreTriggers);
        }

        private bool RaycastRecursive(int nodeIdx, SN.Vector3 origin, SN.Vector3 dir,
                                       ref float bestDist, ref SN.Vector3 bestPoint,
                                       ref SN.Vector3 bestNormal, ref Component.Collider? bestCollider,
                                       bool ignoreTriggers)
        {
            if (nodeIdx < 0) return false;
            ref var node = ref _nodes[nodeIdx];

            // Test ray against node AABB
            if (!RayAABBTest(origin, dir, node.Min, node.Max, bestDist))
                return false;

            if (node.ColliderIndex >= 0)
            {
                // Leaf — test against the collider's AABB
                var c = _colliders[node.ColliderIndex];
                if (!c.IsActiveAndEnabled) return false;
                if (ignoreTriggers && c.IsTrigger) return false;

                var aabb = c.GetWorldAABB();
                if (RayAABBHit(origin, dir, aabb.Min, aabb.Max, out float t, out SN.Vector3 normal) && t >= 0f && t < bestDist)
                {
                    bestDist = t;
                    bestPoint = origin + dir * t;
                    bestNormal = normal;
                    bestCollider = c;
                    return true;
                }
                return false;
            }

            bool hitLeft = RaycastRecursive(node.Left, origin, dir, ref bestDist, ref bestPoint, ref bestNormal, ref bestCollider, ignoreTriggers);
            bool hitRight = RaycastRecursive(node.Right, origin, dir, ref bestDist, ref bestPoint, ref bestNormal, ref bestCollider, ignoreTriggers);
            return hitLeft || hitRight;
        }

        /// <summary>Collect all hits along a ray.</summary>
        public void RaycastAll(SN.Vector3 origin, SN.Vector3 direction, float maxDist,
                                List<(Component.Collider collider, float distance, SN.Vector3 point, SN.Vector3 normal)> results)
        {
            if (_root < 0) return;
            RaycastAllRecursive(_root, origin, direction, maxDist, results);
        }

        private void RaycastAllRecursive(int nodeIdx, SN.Vector3 origin, SN.Vector3 dir, float maxDist,
                                          List<(Component.Collider, float, SN.Vector3, SN.Vector3)> results)
        {
            if (nodeIdx < 0) return;
            ref var node = ref _nodes[nodeIdx];

            if (!RayAABBTest(origin, dir, node.Min, node.Max, maxDist)) return;

            if (node.ColliderIndex >= 0)
            {
                var c = _colliders[node.ColliderIndex];
                if (!c.IsActiveAndEnabled) return;

                var aabb = c.GetWorldAABB();
                if (RayAABBHit(origin, dir, aabb.Min, aabb.Max, out float t, out SN.Vector3 normal) && t >= 0f && t <= maxDist)
                    results.Add((c, t, origin + dir * t, normal));
                return;
            }

            RaycastAllRecursive(node.Left, origin, dir, maxDist, results);
            RaycastAllRecursive(node.Right, origin, dir, maxDist, results);
        }

        /// <summary>Sphere overlap query — returns all colliders within the sphere.</summary>
        public void OverlapSphere(SN.Vector3 center, float radius, List<Component.Collider> results)
        {
            if (_root < 0) return;
            var sphereMin = center - new SN.Vector3(radius);
            var sphereMax = center + new SN.Vector3(radius);
            OverlapSphereRecursive(_root, center, radius, sphereMin, sphereMax, results);
        }

        private void OverlapSphereRecursive(int nodeIdx, SN.Vector3 center, float radius,
                                             SN.Vector3 sMin, SN.Vector3 sMax,
                                             List<Component.Collider> results)
        {
            if (nodeIdx < 0) return;
            ref var node = ref _nodes[nodeIdx];

            if (!AABBOverlap(node.Min, node.Max, sMin, sMax)) return;

            if (node.ColliderIndex >= 0)
            {
                var c = _colliders[node.ColliderIndex];
                if (c.IsActiveAndEnabled)
                    results.Add(c);
                return;
            }

            OverlapSphereRecursive(node.Left, center, radius, sMin, sMax, results);
            OverlapSphereRecursive(node.Right, center, radius, sMin, sMax, results);
        }

        // ── Helpers ──

        private static bool AABBOverlap(SN.Vector3 aMin, SN.Vector3 aMax, SN.Vector3 bMin, SN.Vector3 bMax)
            => (aMin.X <= bMax.X && aMax.X >= bMin.X) &&
               (aMin.Y <= bMax.Y && aMax.Y >= bMin.Y) &&
               (aMin.Z <= bMax.Z && aMax.Z >= bMin.Z);

        /// <summary>Fast ray-AABB test (does it intersect within maxDist?)</summary>
        private static bool RayAABBTest(SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 min, SN.Vector3 max, float maxDist)
        {
            float tmin = 0f, tmax = maxDist;

            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? origin.X : i == 1 ? origin.Y : origin.Z;
                float d = i == 0 ? dir.X : i == 1 ? dir.Y : dir.Z;
                float bmin = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
                float bmax = i == 0 ? max.X : i == 1 ? max.Y : max.Z;

                if (MathF.Abs(d) < 1e-8f)
                {
                    if (o < bmin || o > bmax) return false;
                }
                else
                {
                    float t1 = (bmin - o) / d;
                    float t2 = (bmax - o) / d;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tmin = MathF.Max(tmin, t1);
                    tmax = MathF.Min(tmax, t2);
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }

        /// <summary>Ray-AABB intersection returning the hit distance and face normal.</summary>
        private static bool RayAABBHit(SN.Vector3 origin, SN.Vector3 dir, SN.Vector3 min, SN.Vector3 max,
                                        out float tHit, out SN.Vector3 normal)
        {
            tHit = 0;
            normal = SN.Vector3.Zero;
            float tmin = float.NegativeInfinity;
            float tmax = float.PositiveInfinity;
            int hitAxis = 0;
            bool hitMin = false;

            for (int axis = 0; axis < 3; axis++)
            {
                float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
                float bmin = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                float bmax = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

                if (MathF.Abs(d) < 1e-8f)
                {
                    if (o < bmin || o > bmax) return false;
                }
                else
                {
                    float t1 = (bmin - o) / d;
                    float t2 = (bmax - o) / d;
                    bool swapped = false;
                    if (t1 > t2) { (t1, t2) = (t2, t1); swapped = true; }
                    if (t1 > tmin) { tmin = t1; hitAxis = axis; hitMin = !swapped; }
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }

            if (tmax < 0) return false;
            tHit = tmin >= 0 ? tmin : tmax;

            normal = hitAxis switch
            {
                0 => hitMin ? -SN.Vector3.UnitX : SN.Vector3.UnitX,
                1 => hitMin ? -SN.Vector3.UnitY : SN.Vector3.UnitY,
                _ => hitMin ? -SN.Vector3.UnitZ : SN.Vector3.UnitZ,
            };
            return true;
        }
    }
}
