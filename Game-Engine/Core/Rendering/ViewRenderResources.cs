#nullable enable
using System;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core.Rendering.UI;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering;

/// <summary>GPU programs and caches shared by the editor Game view and the standalone player.</summary>
public sealed class ViewRenderResources : IDisposable
{
    public ShaderProgram? Standard, Depth, Sky, Terrain, Particle, Water;
    public ShaderProgram? PlanetTerrain, PlanetWater, PlanetAtmosphere, PlanetCloud, PostProcess;
    public ShaderProgram? GBuffer, DeferredLight, Ssao, SsaoBlur, Ssr, VolFog, TaaResolve, DepthCopy;
    public FullscreenQuad? FsQuad;
    public ResourceCache? Cache;
    public ShadowMapGPU? Shadow;
    public CanvasRenderer? Canvas;
    public TiledLightTextureSystem? TiledLights;
    public GPUFramebuffer? SceneFbo, GBufferFbo, SsaoFbo, SsaoBlurFbo;
    public GPUFramebuffer? SsrFbo, VolFogFbo, TaaHistoryFbo, TaaTempFbo;
    public int SceneW, SceneH, GBufferW, GBufferH;
    public bool IsES { get; set; }
    /// <summary>When false, <see cref="Dispose"/> does not free GPU objects (editor GameView owns them).</summary>
    public bool OwnsResources { get; set; } = true;

    public void Initialize(GL g, bool es, int shadowResolution = 768)
    {
        IsES = es;
        Game_Engine.Core.Log.Info("[Shader] standard");
        Standard = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.StandardVert, es), ShaderSources.Adapt(ShaderSources.StandardFrag, es));
        Game_Engine.Core.Log.Info("[Shader] depth");
        Depth = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.DepthOnlyVert, es), ShaderSources.Adapt(ShaderSources.DepthOnlyFrag, es));
        Game_Engine.Core.Log.Info("[Shader] sky");
        Sky = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.SkyVert, es), ShaderSources.Adapt(ShaderSources.SkyFrag, es));
        int terrainUnits = g.GetInteger(GLEnum.MaxTextureImageUnits);
        Game_Engine.Core.Log.Info("[Shader] terrain");
        Terrain = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.TerrainVert, es), ShaderSources.Adapt(ShaderSources.TerrainFragForUnits(terrainUnits), es));
        Game_Engine.Core.Log.Info("[Shader] particle");
        Particle = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.ParticleVert, es), ShaderSources.Adapt(ShaderSources.ParticleFrag, es));
        Game_Engine.Core.Log.Info("[Shader] water");
        Water = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.WaterVert, es), ShaderSources.Adapt(ShaderSources.WaterFrag, es));
        Game_Engine.Core.Log.Info("[Shader] planet");
#if XBOX_UWP
        PlanetTerrain = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.PlanetTerrainVert, es), ShaderSources.Adapt(ShaderSources.PlanetTerrainFragXbox, es));
#else
        PlanetTerrain = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.PlanetTerrainVert, es), ShaderSources.Adapt(ShaderSources.PlanetTerrainFrag, es));
#endif
        Game_Engine.Core.Log.Info("[Shader] planet-water");
        PlanetWater = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.PlanetWaterVert, es), ShaderSources.Adapt(ShaderSources.PlanetWaterFrag, es));
        Game_Engine.Core.Log.Info("[Shader] planet-atmosphere");
        PlanetAtmosphere = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.PlanetAtmosphereVert, es), ShaderSources.Adapt(ShaderSources.PlanetAtmosphereFrag, es));
        Game_Engine.Core.Log.Info("[Shader] planet-cloud");
        PlanetCloud = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.PlanetCloudsVert, es), ShaderSources.Adapt(ShaderSources.PlanetCloudsFrag, es));
        Game_Engine.Core.Log.Info("[Shader] post");
        PostProcess = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.PostProcessVert, es), ShaderSources.Adapt(ShaderSources.PostProcessFrag, es));
#if XBOX_UWP
        // The Xbox player stays on the forward path. Linking deferred, SSAO, SSR,
        // and the volumetric-fog march here sits inside ANGLE's HLSL compiler and
        // the first frame never presents. The last [Shader] line in player.log is
        // the program that was linking when that happens.
        Game_Engine.Core.Log.Info("[Shader] deferred set skipped");
#else
        Game_Engine.Core.Log.Info("[Shader] gbuffer");
        GBuffer = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.GBufferVert, es), ShaderSources.Adapt(ShaderSources.GBufferFrag, es));
        Game_Engine.Core.Log.Info("[Shader] deferred-light");
        DeferredLight = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.DeferredLightingVert, es), ShaderSources.Adapt(ShaderSources.DeferredLightingFrag, es));
        Game_Engine.Core.Log.Info("[Shader] ssao");
        Ssao = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.SSAOVert, es), ShaderSources.Adapt(ShaderSources.SSAOFrag, es));
        SsaoBlur = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.SSAOVert, es), ShaderSources.Adapt(ShaderSources.SSAOBlurFrag, es));
        Game_Engine.Core.Log.Info("[Shader] ssr");
        Ssr = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.SSRVert, es), ShaderSources.Adapt(ShaderSources.SSRFrag, es));
        Game_Engine.Core.Log.Info("[Shader] volfog");
        VolFog = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.VolumetricFogVert, es), ShaderSources.Adapt(ShaderSources.VolumetricFogFrag, es));
        Game_Engine.Core.Log.Info("[Shader] taa");
        TaaResolve = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.PostProcessVert, es), ShaderSources.Adapt(ShaderSources.TaaResolveFrag, es));
        DepthCopy = new ShaderProgram(g, ShaderSources.Adapt(ShaderSources.BlitVert, es), ShaderSources.Adapt(ShaderSources.DepthCopyFrag, es));
#endif
        FsQuad = new FullscreenQuad(g);
        Cache = new ResourceCache(g);
        GpuCompressionCaps.Initialize(g);
        TiledLights = new TiledLightTextureSystem(g);
        Shadow = new ShadowMapGPU(g, shadowResolution, shadowResolution);
        Canvas = new CanvasRenderer(g, es);
    }

    public void Dispose()
    {
        if (!OwnsResources) return;
        SceneFbo?.Dispose(); GBufferFbo?.Dispose(); SsaoFbo?.Dispose(); SsaoBlurFbo?.Dispose();
        SsrFbo?.Dispose(); VolFogFbo?.Dispose(); TaaHistoryFbo?.Dispose(); TaaTempFbo?.Dispose();
        Canvas?.Dispose(); TiledLights?.Dispose(); Shadow?.Dispose(); Cache?.Dispose(); FsQuad?.Dispose();
        Standard?.Dispose(); Depth?.Dispose(); Sky?.Dispose(); Terrain?.Dispose(); Particle?.Dispose();
        Water?.Dispose(); PlanetTerrain?.Dispose(); PlanetWater?.Dispose(); PlanetAtmosphere?.Dispose();
        PlanetCloud?.Dispose(); PostProcess?.Dispose(); GBuffer?.Dispose(); DeferredLight?.Dispose();
        Ssao?.Dispose(); SsaoBlur?.Dispose(); Ssr?.Dispose(); VolFog?.Dispose(); TaaResolve?.Dispose();
        DepthCopy?.Dispose();
    }
}
