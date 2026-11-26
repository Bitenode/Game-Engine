#nullable enable
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>Box-style reflection probe: prefiltered cubemap sampled in deferred lighting.</summary>
[ComponentCategory("Rendering")]
public sealed class ReflectionProbe : Behavior
{
    /// <summary>Per-OpenGL-context cubemap and capture state (SceneView, GameView, and Player each have their own GL context).</summary>
    public sealed class ProbeGpuState
    {
        public GPUTexture? Cubemap { get; set; }
        public bool NeedsCapture { get; set; } = true;
        public bool HasCaptured { get; set; }
        public int FrameCounter { get; set; }
    }

    public static readonly List<ReflectionProbe> ActiveProbes = new(4);
    public static IReadOnlyList<ReflectionProbe> All => ActiveProbes;

    private readonly ConditionalWeakTable<GL, ProbeGpuState> _gpuStates = new();

    /// <summary>Cubemap face resolution (power of two).</summary>
    [Persist] public int Resolution { get; set; } = 128;

    /// <summary>Extra specular IBL weight in deferred &amp; forward passes.</summary>
    [Persist] public float Intensity { get; set; } = 0.6f;

    [Persist] public ProbeRefreshMode Mode { get; set; } = ProbeRefreshMode.Baked;

    /// <summary>When Baked, capture once when scene loads; Realtime refreshes periodically.</summary>
    [Persist] public int RealtimeRefreshFrames { get; set; } = 60;

    /// <summary>First allocated GPU cubemap across active GL contexts (for Inspector display).</summary>
    [HideInInspector]
    public GPUTexture? GpuCubemap
    {
        get
        {
            foreach (var kv in _gpuStates)
            {
                if (kv.Value.Cubemap != null) return kv.Value.Cubemap;
            }
            return null;
        }
    }

    /// <summary>Whether any GL context still needs capture; setting true invalidates all contexts.</summary>
    public bool NeedsCapture
    {
        get
        {
            bool any = false;
            foreach (var kv in _gpuStates)
            {
                any = true;
                if (kv.Value.NeedsCapture) return true;
            }
            return !any;
        }
        set
        {
            if (value) InvalidateAllContexts();
            else
            {
                foreach (var kv in _gpuStates)
                    kv.Value.NeedsCapture = false;
            }
        }
    }

    public override void OnEnable()
    {
        base.OnEnable();
        if (!ActiveProbes.Contains(this))
            ActiveProbes.Add(this);
        InvalidateAllContexts();
    }

    public override void OnDisable()
    {
        ActiveProbes.Remove(this);
        foreach (var kv in _gpuStates)
        {
            kv.Value.Cubemap?.Dispose();
            kv.Value.Cubemap = null;
            kv.Value.HasCaptured = false;
            kv.Value.NeedsCapture = true;
            kv.Value.FrameCounter = 0;
        }
        _gpuStates.Clear();
        base.OnDisable();
    }

    /// <summary>Mark all GL contexts as needing recapture on their next frame.</summary>
    public void InvalidateAllContexts()
    {
        foreach (var kv in _gpuStates)
            kv.Value.NeedsCapture = true;
    }

    /// <summary>Get or create the per-GL-context probe state.</summary>
    public ProbeGpuState GetState(GL gl) => _gpuStates.GetOrCreateValue(gl);

    /// <summary>Get the cubemap texture owned by <paramref name="gl"/>, or null if not allocated for that context.</summary>
    public GPUTexture? GetCubemap(GL gl) => _gpuStates.TryGetValue(gl, out var state) ? state.Cubemap : null;

    /// <summary>Whether <paramref name="gl"/> needs to capture this probe.</summary>
    public bool NeedsCaptureFor(GL gl) => !_gpuStates.TryGetValue(gl, out var state) || state.NeedsCapture;

    /// <summary>Whether <paramref name="gl"/> has a captured cubemap for this probe.</summary>
    public bool HasCapturedFor(GL gl) => _gpuStates.TryGetValue(gl, out var state) && state.HasCaptured;

    /// <summary>Mark this probe as captured for <paramref name="gl"/>.</summary>
    public void MarkCaptured(GL gl)
    {
        var state = _gpuStates.GetOrCreateValue(gl);
        state.NeedsCapture = false;
        state.HasCaptured = true;
    }

    /// <summary>Allocate cubemap storage for <paramref name="gl"/> (filled by <c>SceneRenderer.CaptureReflectionProbes</c>).</summary>
    public ProbeGpuState EnsureGpuResources(GL gl)
    {
        var state = _gpuStates.GetOrCreateValue(gl);
        int size = System.Math.Clamp(Resolution, 16, 512);
        if (state.Cubemap != null && state.Cubemap.Width == size) return state;
        state.Cubemap?.Dispose();
        state.Cubemap = new GPUTexture(gl);
        state.Cubemap.CreateCubemapRgba8(size);
        state.NeedsCapture = true;
        state.HasCaptured = false;
        return state;
    }

    public bool ShouldRefreshRealtime(GL gl)
    {
        if (Mode != ProbeRefreshMode.Realtime) return false;
        var state = _gpuStates.GetOrCreateValue(gl);
        if (!state.HasCaptured) return true;
        state.FrameCounter++;
        if (state.FrameCounter < System.Math.Max(1, RealtimeRefreshFrames)) return false;
        state.FrameCounter = 0;
        return true;
    }

    /// <summary>Pick strongest captured probe near the camera for <paramref name="gl"/> (world-space distance falloff).</summary>
    public static ReflectionProbe? GetBestForPosition(SN.Vector3 worldCam, GL gl)
    {
        ReflectionProbe? best = null;
        float bestScore = -1f;
        foreach (var p in ActiveProbes)
        {
            if (p is not { IsActiveAndEnabled: true, gameObject: not null }) continue;
            if (!p._gpuStates.TryGetValue(gl, out var state) || state.Cubemap == null || !state.HasCaptured) continue;
            var worldMat = SceneGraphUtil.AccumulateWorld(p.gameObject);
            var c = new SN.Vector3(worldMat.M41, worldMat.M42, worldMat.M43);
            float d2 = SN.Vector3.DistanceSquared(worldCam, c);
            float score = p.Intensity / (1f + d2 * 0.002f);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }
        return best;
    }
}

public enum ProbeRefreshMode
{
    Baked,
    Realtime
}
