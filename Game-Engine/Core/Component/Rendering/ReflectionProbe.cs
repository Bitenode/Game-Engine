#nullable enable
using System.Collections.Generic;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>Box-style reflection probe: prefiltered cubemap sampled in deferred lighting.</summary>
[ComponentCategory("Rendering")]
public sealed class ReflectionProbe : Behavior
{
    public static readonly List<ReflectionProbe> ActiveProbes = new(4);
    public static IReadOnlyList<ReflectionProbe> All => ActiveProbes;

    /// <summary>Cubemap face resolution (power of two).</summary>
    [Persist] public int Resolution { get; set; } = 128;

    /// <summary>Extra specular IBL weight in deferred &amp; forward passes.</summary>
    [Persist] public float Intensity { get; set; } = 0.6f;

    [Persist] public ProbeRefreshMode Mode { get; set; } = ProbeRefreshMode.Baked;

    /// <summary>When Baked, capture once when scene loads; Realtime refreshes periodically.</summary>
    [Persist] public int RealtimeRefreshFrames { get; set; } = 60;

    /// <summary>GPU cubemap for this probe (owned by the probe).</summary>
    [HideInInspector]
    public GPUTexture? GpuCubemap { get; private set; }

    public bool NeedsCapture { get; set; } = true;
    public bool HasCaptured { get; set; }
    int _frameCounter;

    public override void OnEnable()
    {
        base.OnEnable();
        if (!ActiveProbes.Contains(this))
            ActiveProbes.Add(this);
        NeedsCapture = true;
    }

    public override void OnDisable()
    {
        ActiveProbes.Remove(this);
        GpuCubemap?.Dispose();
        GpuCubemap = null;
        HasCaptured = false;
        base.OnDisable();
    }

    /// <summary>Allocate cubemap storage (filled by <c>SceneRenderer.CaptureReflectionProbes</c>).</summary>
    public void EnsureGpuResources(GL gl)
    {
        int size = System.Math.Clamp(Resolution, 16, 512);
        if (GpuCubemap != null && GpuCubemap.Width == size) return;
        GpuCubemap?.Dispose();
        GpuCubemap = new GPUTexture(gl);
        GpuCubemap.CreateCubemapRgba8(size);
        NeedsCapture = true;
        HasCaptured = false;
    }

    public bool ShouldRefreshRealtime()
    {
        if (Mode != ProbeRefreshMode.Realtime) return false;
        if (!HasCaptured) return true;
        _frameCounter++;
        if (_frameCounter < System.Math.Max(1, RealtimeRefreshFrames)) return false;
        _frameCounter = 0;
        return true;
    }

    /// <summary>Pick strongest captured probe near the camera (simple distance falloff).</summary>
    public static ReflectionProbe? GetBestForPosition(SN.Vector3 worldCam)
    {
        ReflectionProbe? best = null;
        float bestScore = -1f;
        foreach (var p in ActiveProbes)
        {
            if (p is not { IsActiveAndEnabled: true, gameObject: not null }) continue;
            if (p.GpuCubemap == null || !p.HasCaptured) continue;
            var pos = p.gameObject.Transform.Position;
            var c = new SN.Vector3((float)pos.X, (float)pos.Y, (float)pos.Z);
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
