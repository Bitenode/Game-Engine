#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using SN = System.Numerics;

namespace Game_Engine.Core.VirtualCamera
{
    /// <summary>
    /// A Catmull-Rom spline path for dolly camera movement.
    /// Define waypoints and evaluate positions along the path.
    /// </summary>
    public sealed class DollyPath : Behavior
    {
        /// <summary>World-space waypoints defining the path.</summary>
        [Persist] public List<Vector3> Waypoints { get; set; } = new()
        {
            new Vector3(0, 2, 0),
            new Vector3(5, 2, 5),
            new Vector3(10, 2, 0),
            new Vector3(5, 2, -5)
        };

        /// <summary>Whether the path loops back to the start.</summary>
        [Persist] public bool IsLoop { get; set; } = false;

        /// <summary>
        /// Evaluate a position on the path.
        /// t ranges from 0 (start) to 1 (end).
        /// </summary>
        public SN.Vector3 Evaluate(float t)
        {
            if (Waypoints.Count == 0) return SN.Vector3.Zero;
            if (Waypoints.Count == 1) return ToSN(Waypoints[0]);

            t = Math.Clamp(t, 0f, 1f);
            int segmentCount = IsLoop ? Waypoints.Count : Waypoints.Count - 1;
            float scaled = t * segmentCount;
            int segment = Math.Min((int)scaled, segmentCount - 1);
            float localT = scaled - segment;

            int count = Waypoints.Count;
            int p0 = IsLoop ? (segment - 1 + count) % count : Math.Max(segment - 1, 0);
            int p1 = IsLoop ? segment % count : segment;
            int p2 = IsLoop ? (segment + 1) % count : Math.Min(segment + 1, count - 1);
            int p3 = IsLoop ? (segment + 2) % count : Math.Min(segment + 2, count - 1);

            return CatmullRom(ToSN(Waypoints[p0]), ToSN(Waypoints[p1]),
                              ToSN(Waypoints[p2]), ToSN(Waypoints[p3]), localT);
        }

        /// <summary>Get the tangent direction at position t.</summary>
        public SN.Vector3 EvaluateTangent(float t)
        {
            float dt = 0.001f;
            var a = Evaluate(Math.Max(t - dt, 0f));
            var b = Evaluate(Math.Min(t + dt, 1f));
            var tangent = b - a;
            return tangent.LengthSquared() > 0.0001f ? SN.Vector3.Normalize(tangent) : SN.Vector3.UnitZ;
        }

        /// <summary>Get the total approximate length of the path.</summary>
        public float ApproximateLength(int sampleCount = 50)
        {
            float length = 0f;
            SN.Vector3 prev = Evaluate(0f);
            for (int i = 1; i <= sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                SN.Vector3 curr = Evaluate(t);
                length += (curr - prev).Length();
                prev = curr;
            }
            return length;
        }

        private static SN.Vector3 CatmullRom(SN.Vector3 p0, SN.Vector3 p1, SN.Vector3 p2, SN.Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private static SN.Vector3 ToSN(Vector3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

        // ── Registry ──
        private static readonly List<DollyPath> _allPaths = new(4);
        public static IReadOnlyList<DollyPath> All => _allPaths;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_allPaths.Contains(this)) _allPaths.Add(this);
        }

        public override void OnDisable()
        {
            _allPaths.Remove(this);
            base.OnDisable();
        }

        public static DollyPath? FindByName(string name)
        {
            foreach (var p in _allPaths)
                if (p.gameObject?.Name == name) return p;
            return null;
        }
    }
}
