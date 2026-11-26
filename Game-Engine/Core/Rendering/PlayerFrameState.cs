#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using Game_Engine.Core.Component;
using Game_Engine.Core.Rendering.GPU;

namespace Game_Engine.Core.Rendering
{
    /// <summary>Mutable play-frame state shared by the desktop viewport and the Xbox loop.</summary>
    public sealed class PlayerFrameState
    {
        public ViewRenderResources? Res;
        public GPUFramebuffer? SceneFbo;
        public int SceneW, SceneH;
        public bool Playing;
        public Skybox? Sky;
        public Light? Light;
        public List<Camera> Cameras = new(4);
        public double RenderScale = 1;
        public double WidthDip = 1;
        public double HeightDip = 1;
        public readonly Stopwatch FrameWatch = new();
        public readonly Stopwatch FpsTick = new();
        public readonly Stopwatch FpsWindow = new();
        public double MsFrameLast, MsFrameEma, FpsDisplay;
        public int FpsFrames;
        public bool RenderInFlight;
        public System.Numerics.Matrix4x4 PrevViewProj = System.Numerics.Matrix4x4.Identity;
        public bool TaaResetHistory = true;
        public int TaaFrameCounter;

        public void DisposeGpu()
        {
            SceneFbo?.Dispose();
            SceneFbo = null;
            SceneW = SceneH = 0;
            Res?.Dispose();
            Res = null;
        }
    }
}
