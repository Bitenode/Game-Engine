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
        public enum ScriptPhase : byte
        {
            Update,
            LateUpdate,
            FixedUpdate
        }

        /// <summary>One behavior type's cost for the latest published script tick.</summary>
        public struct ScriptCost
        {
            public string TypeName;
            public string ObjectName;
            public string Phase;
            public double Ms;
            public int Count;
        }

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
            public double PlanetLodMs;
            public double PlanetRenderMs;
            public int DrawCalls;
            public int TriangleCount;
            public int BatchCount;
            public int ActiveGameObjects;
            public int ActiveColliders;
            public int PlanetCount;
            public int PlanetChunkCount;
            public int PlanetActiveJobs;
            public int PlanetPendingJobs;
            public long TextureMemoryBytes;
            public long MeshMemoryBytes;
            public List<ProfileSample> Samples;
        }

        // ── Configuration ──
        /// <summary>Enable or disable profiling. When disabled, Begin/End are no-ops.</summary>
        public static bool Enabled { get; set; } = false;

        /// <summary>
        /// Time each behavior during Update / LateUpdate / FixedUpdate.
        /// Cheap (two timestamps per component); on by default so script spikes are attributable.
        /// </summary>
        public static bool SampleScripts { get; set; } = true;

        public const int TopScriptCount = 8;
        public const double ScriptSpikeMs = 8.0;

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
        private static int _planetCount;
        private static int _planetChunkCount;
        private static int _planetActiveJobs;
        private static int _planetPendingJobs;
        private static double _planetLodMs;
        private static double _planetRenderMs;

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
            _planetCount = 0;
            _planetChunkCount = 0;
            _planetActiveJobs = 0;
            _planetPendingJobs = 0;
            _planetLodMs = 0;
            _planetRenderMs = 0;
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
                PlanetLodMs = _planetLodMs,
                PlanetRenderMs = _planetRenderMs,
                DrawCalls = _drawCalls,
                TriangleCount = _triangles,
                BatchCount = _batches,
                ActiveGameObjects = SceneService.Root?.Count > 0 ? CountGameObjects() : 0,
                ActiveColliders = Physics.CollisionWorld.All.Count,
                PlanetCount = _planetCount,
                PlanetChunkCount = _planetChunkCount,
                PlanetActiveJobs = _planetActiveJobs,
                PlanetPendingJobs = _planetPendingJobs,
                TextureMemoryBytes = 0,
                MeshMemoryBytes = 0,
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

        sealed class ScriptAccum
        {
            public double TotalMs;
            public int Count;
            public double HeaviestMs;
            public string HeaviestObject = "";
            public ScriptPhase HeaviestPhase;
        }

        static readonly Dictionary<string, ScriptAccum> _scriptByType = new(64);
        static readonly Dictionary<string, long> _scriptSpikeLog = new(32);
        static readonly ScriptCost[] _latestTop = new ScriptCost[TopScriptCount];
        static readonly ScriptCost[] _spikeTop = new ScriptCost[TopScriptCount];
        static int _latestTopCount;
        static int _spikeTopCount;
        static double _latestScriptsMs;
        static double _spikeScriptsMs;
        static long _spikeTimestamp;

        public static int LatestTopScriptCount => _latestTopCount;
        public static double LatestScriptsMs => _latestScriptsMs;
        public static int SpikeTopScriptCount => _spikeTopCount;
        public static double SpikeScriptsMs => _spikeScriptsMs;

        public static ScriptCost GetLatestTopScript(int index)
            => (uint)index < (uint)_latestTopCount ? _latestTop[index] : default;

        public static ScriptCost GetSpikeTopScript(int index)
            => (uint)index < (uint)_spikeTopCount ? _spikeTop[index] : default;

        public static double SpikeAgeSeconds
        {
            get
            {
                if (_spikeTimestamp == 0) return -1;
                return (Stopwatch.GetTimestamp() - _spikeTimestamp) * 1.0 / Stopwatch.Frequency;
            }
        }

        public static string FormatScriptCost(in ScriptCost s)
        {
            string obj = string.IsNullOrEmpty(s.ObjectName) ? "" : $" @{s.ObjectName}";
            string count = s.Count > 1 ? $"  ×{s.Count}" : "";
            string phase = string.IsNullOrEmpty(s.Phase) ? "" : $"  {s.Phase}";
            return $"{s.TypeName}  {s.Ms:F2} ms{count}{phase}{obj}";
        }

        /// <summary>Run a behavior tick and, when sampling, add its time to the per-type totals.</summary>
        public static void InvokeAndRecord(Behavior b, ScriptPhase phase)
        {
            if (!SampleScripts)
            {
                InvokePhase(b, phase);
                return;
            }

            long t0 = Stopwatch.GetTimestamp();
            InvokePhase(b, phase);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            RecordScript(b, phase, ms);
        }

        static void InvokePhase(Behavior b, ScriptPhase phase)
        {
            switch (phase)
            {
                case ScriptPhase.LateUpdate: b.__LateUpdate(); break;
                case ScriptPhase.FixedUpdate: b.__FixedUpdate(); break;
                default: b.__Update(); break;
            }
        }

        static void RecordScript(Behavior b, ScriptPhase phase, double ms)
        {
            string typeName = b.GetType().Name;
            if (!_scriptByType.TryGetValue(typeName, out var acc))
            {
                acc = new ScriptAccum();
                _scriptByType[typeName] = acc;
            }

            acc.TotalMs += ms;
            acc.Count++;
            if (ms >= acc.HeaviestMs)
            {
                acc.HeaviestMs = ms;
                acc.HeaviestPhase = phase;
                acc.HeaviestObject = b.gameObject?.Name ?? "";
            }

            if (ms < ScriptSpikeMs)
                return;

            long now = Stopwatch.GetTimestamp();
            if (_scriptSpikeLog.TryGetValue(typeName, out long last)
                && (now - last) * 1000.0 / Stopwatch.Frequency < 2000.0)
                return;

            _scriptSpikeLog[typeName] = now;
            string owner = b.gameObject?.Name ?? "?";
            Log.Warning($"[Scripts] {typeName}.{phase} {ms:F1} ms on '{owner}'");
        }

        /// <summary>
        /// Sort this tick's behavior costs, remember a spike snapshot, then reset accumulators.
        /// Call once after Update/LateUpdate (FixedUpdate since the last publish is included).
        /// </summary>
        public static void PublishScriptCosts()
        {
            _latestScriptsMs = 0;
            _latestTopCount = 0;
            if (_scriptByType.Count == 0)
                return;

            foreach (var acc in _scriptByType.Values)
                _latestScriptsMs += acc.TotalMs;

            int filled = 0;
            foreach (var kv in _scriptByType)
            {
                var acc = kv.Value;
                if (acc.Count <= 0 || acc.TotalMs <= 0)
                    continue;

                var cost = new ScriptCost
                {
                    TypeName = kv.Key,
                    ObjectName = acc.HeaviestObject,
                    Phase = acc.HeaviestPhase.ToString(),
                    Ms = acc.TotalMs,
                    Count = acc.Count
                };

                if (filled < TopScriptCount)
                {
                    _latestTop[filled++] = cost;
                    continue;
                }

                int weakest = 0;
                for (int i = 1; i < TopScriptCount; i++)
                {
                    if (_latestTop[i].Ms < _latestTop[weakest].Ms)
                        weakest = i;
                }
                if (cost.Ms > _latestTop[weakest].Ms)
                    _latestTop[weakest] = cost;
            }

            _latestTopCount = filled;
            for (int i = 0; i < _latestTopCount - 1; i++)
            {
                int best = i;
                for (int j = i + 1; j < _latestTopCount; j++)
                {
                    if (_latestTop[j].Ms > _latestTop[best].Ms)
                        best = j;
                }
                if (best != i)
                    (_latestTop[i], _latestTop[best]) = (_latestTop[best], _latestTop[i]);
            }

            bool spikeAgedOut = _spikeTimestamp == 0 || SpikeAgeSeconds > 8.0;
            if (_latestScriptsMs >= ScriptSpikeMs
                && (spikeAgedOut || _latestScriptsMs >= _spikeScriptsMs))
            {
                _spikeScriptsMs = _latestScriptsMs;
                _spikeTopCount = _latestTopCount;
                _spikeTimestamp = Stopwatch.GetTimestamp();
                for (int i = 0; i < _latestTopCount; i++)
                    _spikeTop[i] = _latestTop[i];
            }

            foreach (var acc in _scriptByType.Values)
            {
                acc.TotalMs = 0;
                acc.Count = 0;
                acc.HeaviestMs = 0;
                acc.HeaviestObject = "";
                acc.HeaviestPhase = ScriptPhase.Update;
            }
        }

        /// <summary>Set planet system counters/timings for this frame.</summary>
        public static void SetPlanetStats(
            int planetCount,
            int chunkCount,
            int activeJobs,
            int pendingJobs,
            double lodMs,
            double renderMs)
        {
            if (!Enabled) return;
            _planetCount = planetCount;
            _planetChunkCount = chunkCount;
            _planetActiveJobs = activeJobs;
            _planetPendingJobs = pendingJobs;
            _planetLodMs = lodMs;
            _planetRenderMs = renderMs;
        }

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
