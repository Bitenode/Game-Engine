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

    /// <summary>Cubemap face resolution (power of two).</summary>
    [Persist] public int Resolution { get; set; } = 128;

    /// <summary>Extra specular IBL weight in deferred pass.</summary>
    [Persist] public float Intensity { get; set; } = 0.35f;

    [Persist] public ProbeRefreshMode Mode { get; set; } = ProbeRefreshMode.Baked;

    /// <summary>When Baked, capture once when scene loads; Realtime refreshes periodically.</summary>
    [Persist] public int RealtimeRefreshFrames { get; set; } = 60;

    /// <summary>GPU cubemap for this probe (owned by the probe).</summary>
    public GPUTexture? GpuCubemap { get; private set; }

    public bool NeedsCapture { get; set; } = true;
    int _frameCounter;

    public override void OnEnable()
    {
        base.OnEnable();
        if (!ActiveProbes.Contains(this))
            ActiveProbes.Add(this);
    }

    public override void OnDisable()
    {
        ActiveProbes.Remove(this);
        GpuCubemap?.Dispose();
        GpuCubemap = null;
        base.OnDisable();
    }

    /// <summary>Allocate cubemap storage (solid color until a capture pass fills faces).</summary>
    public void EnsureGpuResources(GL gl)
    {
        if (GpuCubemap != null) return;
        int size = System.Math.Clamp(Resolution, 16, 512);
        GpuCubemap = new GPUTexture(gl);
        GpuCubemap.CreateCubemapRgba8(size);
        NeedsCapture = true;
    }

    public bool ShouldRefreshRealtime()
    {
        if (Mode != ProbeRefreshMode.Realtime) return false;
        _frameCounter++;
        if (_frameCounter < System.Math.Max(1, RealtimeRefreshFrames)) return false;
        _frameCounter = 0;
        return true;
    }

    /// <summary>Pick strongest probe near the camera (simple distance).</summary>
    public static ReflectionProbe? GetBestForPosition(SN.Vector3 worldCam)
    {
        ReflectionProbe? best = null;
        float bestScore = -1f;
        foreach (var p in ActiveProbes)
        {
            if (p is not { IsActiveAndEnabled: true, gameObject: not null }) continue;
            if (p.GpuCubemap == null) continue;
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
