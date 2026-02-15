#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Game_Engine.Core
{
    /// <summary>
    /// Engine profiler — tracks per-system timing, draw call counts, and memory usage.
    /// Use Profiler.Begin/End sections around code blocks to measure performance.
    /// Data is exposed for the editor profiler panel to visualize.
    /// </summary>
    public static class Profiler
    {
        /// <summary>A single profiler sample with timing information.</summary>
        public struct ProfileSample
        {
            public string Name;
            public double ElapsedMs;
            public int Depth;
        }

        /// <summary>Frame statistics snapshot.</summary>
        public struct FrameStats
        {
            public double TotalFrameMs;
            public double RenderMs;
            public double PhysicsMs;
            public double ScriptsMs;
            public double AudioMs;
            public double AnimationMs;
            public int DrawCalls;
            public int TriangleCount;
            public int BatchCount;
            public int ActiveGameObjects;
            public int ActiveColliders;
            public long TextureMemoryBytes;
            public long MeshMemoryBytes;
            public List<ProfileSample> Samples;
        }

        // ── Configuration ──
        /// <summary>Enable or disable profiling. When disabled, Begin/End are no-ops.</summary>
        public static bool Enabled { get; set; } = false;

        /// <summary>Number of frames to keep in the history ring buffer.</summary>
        public const int HistorySize = 300; // ~5 seconds at 60fps

        // ── Ring buffer of frame stats ──
        private static readonly FrameStats[] _history = new FrameStats[HistorySize];
        private static int _historyIndex;
        private static int _historyCount;

        // ── Current frame state ──
        private static readonly Stopwatch _frameStopwatch = new();
        private static readonly Stack<(string name, long startTicks, int depth)> _stack = new();
        private static readonly List<ProfileSample> _currentSamples = new(32);
        private static int _currentDepth;

        // ── Counters (set externally by engine systems) ──
        private static int _drawCalls;
        private static int _triangles;
        private static int _batches;

        /// <summary>Latest completed frame stats.</summary>
        public static FrameStats Latest => _historyCount > 0
            ? _history[(_historyIndex - 1 + HistorySize) % HistorySize]
            : default;

        /// <summary>Get frame stats from N frames ago (0 = latest).</summary>
        public static FrameStats GetFrame(int framesAgo)
        {
            if (framesAgo < 0 || framesAgo >= _historyCount)
                return default;
            int idx = (_historyIndex - 1 - framesAgo + HistorySize * 2) % HistorySize;
            return _history[idx];
        }

        /// <summary>Number of frames currently in the history buffer.</summary>
        public static int FrameCount => _historyCount;

        // ── Frame lifecycle ──

        /// <summary>Call at the start of each frame.</summary>
        public static void BeginFrame()
        {
            if (!Enabled) return;
            _frameStopwatch.Restart();
            _currentSamples.Clear();
            _currentDepth = 0;
            _stack.Clear();
            _drawCalls = 0;
            _triangles = 0;
            _batches = 0;
        }

        /// <summary>Call at the end of each frame to finalize stats.</summary>
        public static void EndFrame()
        {
            if (!Enabled) return;
            _frameStopwatch.Stop();

            // Collect per-section timings
            double renderMs = 0, physicsMs = 0, scriptsMs = 0, audioMs = 0, animMs = 0;
            foreach (var s in _currentSamples)
            {
                if (s.Depth == 0) // Only top-level sections
                {
                    switch (s.Name)
                    {
                        case "Render": renderMs = s.ElapsedMs; break;
                        case "Physics": physicsMs = s.ElapsedMs; break;
                        case "Scripts": scriptsMs = s.ElapsedMs; break;
                        case "Audio": audioMs = s.ElapsedMs; break;
                        case "Animation": animMs = s.ElapsedMs; break;
                    }
                }
            }

            var stats = new FrameStats
            {
                TotalFrameMs = _frameStopwatch.Elapsed.TotalMilliseconds,
                RenderMs = renderMs,
                PhysicsMs = physicsMs,
                ScriptsMs = scriptsMs,
                AudioMs = audioMs,
                AnimationMs = animMs,
                DrawCalls = _drawCalls,
                TriangleCount = _triangles,
                BatchCount = _batches,
                ActiveGameObjects = SceneService.Root?.Count > 0 ? CountGameObjects() : 0,
                ActiveColliders = Physics.CollisionWorld.All.Count,
                TextureMemoryBytes = 0, // TODO: track from GPUTexture
                MeshMemoryBytes = 0,    // TODO: track from GPUMesh
                Samples = new List<ProfileSample>(_currentSamples)
            };

            _history[_historyIndex] = stats;
            _historyIndex = (_historyIndex + 1) % HistorySize;
            if (_historyCount < HistorySize) _historyCount++;
        }

        // ── Section timing ──

        /// <summary>Begin a named profiler section. Must be paired with End().</summary>
        public static void Begin(string name)
        {
            if (!Enabled) return;
            _stack.Push((name, Stopwatch.GetTimestamp(), _currentDepth));
            _currentDepth++;
        }

        /// <summary>End the current profiler section.</summary>
        public static void End()
        {
            if (!Enabled || _stack.Count == 0) return;
            var (name, startTicks, depth) = _stack.Pop();
            _currentDepth = depth;

            double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            _currentSamples.Add(new ProfileSample
            {
                Name = name,
                ElapsedMs = elapsedMs,
                Depth = depth
            });
        }

        // ── Counter API (called by engine systems) ──

        /// <summary>Increment the draw call counter for this frame.</summary>
        public static void CountDrawCall() { if (Enabled) _drawCalls++; }

        /// <summary>Add to the triangle counter for this frame.</summary>
        public static void CountTriangles(int count) { if (Enabled) _triangles += count; }

        /// <summary>Increment the batch counter for this frame.</summary>
        public static void CountBatch() { if (Enabled) _batches++; }

        // ── Helpers ──

        private static int CountGameObjects()
        {
            int count = 0;
            static void Count(GameObject go, ref int c)
            {
                c++;
                for (int i = 0; i < go.Children.Count; i++)
                    Count(go.Children[i], ref c);
            }
            foreach (var root in SceneService.Root)
                Count(root, ref count);
            return count;
        }

        /// <summary>Get the average frame time over the last N frames.</summary>
        public static double AverageFrameMs(int frameCount = 60)
        {
            if (_historyCount == 0) return 0;
            int count = Math.Min(frameCount, _historyCount);
            double total = 0;
            for (int i = 0; i < count; i++)
                total += GetFrame(i).TotalFrameMs;
            return total / count;
        }

        /// <summary>Get the estimated FPS based on average frame time.</summary>
        public static double FPS
        {
            get
            {
                double avg = AverageFrameMs(60);
                return avg > 0 ? 1000.0 / avg : 0;
            }
        }
    }
}
