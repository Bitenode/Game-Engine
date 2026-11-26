#nullable enable
using System;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core.Rendering.UI;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering;

/// <summary>
/// GPU programs, caches, and framebuffers owned by a viewport (SceneView, GameView, PlayerView, Xbox).
/// Built-in shaders are compiled and owned by <see cref="Shaders"/> (<see cref="ShaderLibrary"/>) so
/// variant caching, line-mapped diagnostics, binary caching, and live hot-reload work uniformly.
/// </summary>
public sealed class ViewRenderResources : IDisposable
{
    public ShaderLibrary? Shaders { get; private set; }
    public GlobalUniforms? Globals { get; private set; }

    public ShaderProgram? Standard, Depth, Sky, Terrain, Particle, Water;
    public ShaderProgram? PlanetTerrain, PlanetWater, PlanetAtmosphere, PlanetCloud, PostProcess, DepthOfField;
    public ShaderProgram? GBuffer, DeferredLight, Ssao, SsaoBlur, Ssr, VolFog, TaaResolve, DepthCopy;
    public ShaderProgram? Grid, Wireframe;

    public FullscreenQuad? FsQuad;
    public ResourceCache? Cache;
    public ShadowMapGPU? Shadow;
    public CascadedShadowMap? CascadedShadow;
    public CanvasRenderer? Canvas;
    public TiledLightTextureSystem? TiledLights;
    public GPUFramebuffer? SceneFbo, GBufferFbo, SsaoFbo, SsaoBlurFbo;
    public GPUFramebuffer? SsrFbo, VolFogFbo, TaaHistoryFbo, TaaTempFbo;
    public GPUFramebuffer? DofTempFbo, DofOutFbo;
    public uint ProbeCaptureFbo, ProbeCaptureDepthRbo;
    public int ProbeCaptureSize;
    public int SceneW, SceneH, GBufferW, GBufferH;
    public bool IsES { get; set; }

    /// <summary>When false, <see cref="Dispose"/> does not free GPU objects.</summary>
    public bool OwnsResources { get; set; } = true;

    private GL? _gl;
    private bool _includeEditorShaders;
    private bool _includeDeferred;
    private bool _includeVolFog;
    private ShaderVariantKey _terrainVariant = ShaderVariantKey.Empty;
    private long _seenReloadGen;

    public void Initialize(
        GL g,
        bool es,
        int shadowResolution = 768,
        bool includeEditorShaders = false,
        bool includeDeferred = true,
        bool includeVolFog = true,
        int csmResolution = 1024)
    {
        _gl = g;
        IsES = es;
        _includeEditorShaders = includeEditorShaders;
#if XBOX_UWP
        _includeDeferred = false;
        _includeVolFog = false;
        bool isXbox = true;
#else
        _includeDeferred = includeDeferred;
        _includeVolFog = includeVolFog || includeDeferred;
        bool isXbox = false;
#endif

        Shaders = ShaderLibrary.GetOrCreate(g, es, isXbox);
        Globals = GlobalUniforms.GetOrCreate(g, es);
        Globals.BindAll();

        int terrainUnits = g.GetInteger(GLEnum.MaxTextureImageUnits);
        _terrainVariant = ShaderSources.TerrainVariantForUnits(terrainUnits);

        CompileBuiltinShaders(logProgress: true);
        _seenReloadGen = Shaders.ReloadGeneration;

        FsQuad = new FullscreenQuad(g);
        Cache = new ResourceCache(g);
        GpuCompressionCaps.Initialize(g);
        if (_includeDeferred)
            TiledLights = new TiledLightTextureSystem(g);
        Shadow = new ShadowMapGPU(g, shadowResolution, shadowResolution);
#if !XBOX_UWP
        if (csmResolution > 0)
            CascadedShadow = new CascadedShadowMap(g, cascadeCount: 4, resolution: csmResolution);
#endif
        Canvas = new CanvasRenderer(g, Shaders);
    }

    /// <summary>
    /// If any watched <c>.glsl</c> file changed on disk, recompile only the affected shaders
    /// and update the cached program properties (preserving the previous valid shader on error).
    /// Zero overhead on normal frames (single volatile read).
    /// </summary>
    public void RefreshIfDirty()
    {
        // Re-assert UBO bindings and reset cached program handle at the start of each frame
        // (Avalonia's compositor or another viewport may have changed GL program/UBO state).
        if (_gl != null)
            ShaderProgram.NotifyProgramUnbound(_gl);
        Globals?.BindAll();

        if (Shaders == null)
            return;

        long gen = Shaders.ReloadGeneration;
        if (gen == _seenReloadGen)
            return;

        _seenReloadGen = gen;
        CompileBuiltinShaders(logProgress: false);
    }

    private void CompileBuiltinShaders(bool logProgress)
    {
        var lib = Shaders;
        if (lib == null) return;

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] standard");
        Standard = lib.GetBuiltin(ShaderSources.Paths.StandardVert, ShaderSources.Paths.StandardFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] depth");
        Depth = lib.GetBuiltin(ShaderSources.Paths.DepthOnlyVert, ShaderSources.Paths.DepthOnlyFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] sky");
        Sky = lib.GetBuiltin(ShaderSources.Paths.SkyVert, ShaderSources.Paths.SkyFrag);

        if (_includeEditorShaders)
        {
            if (logProgress) Game_Engine.Core.Log.Info("[Shader] grid");
            Grid = lib.GetBuiltin(ShaderSources.Paths.GridVert, ShaderSources.Paths.GridFrag);
            if (logProgress) Game_Engine.Core.Log.Info("[Shader] wireframe");
            Wireframe = lib.GetBuiltin(ShaderSources.Paths.WireframeVert, ShaderSources.Paths.WireframeFrag);
        }

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] terrain");
        Terrain = lib.GetBuiltin(ShaderSources.Paths.TerrainVert, ShaderSources.Paths.TerrainFrag, _terrainVariant);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] particle");
        Particle = lib.GetBuiltin(ShaderSources.Paths.ParticleVert, ShaderSources.Paths.ParticleFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] water");
        Water = lib.GetBuiltin(ShaderSources.Paths.WaterVert, ShaderSources.Paths.WaterFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] planet");
        PlanetTerrain = lib.GetBuiltin(ShaderSources.Paths.PlanetTerrainVert, ShaderSources.Paths.PlanetTerrainFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] planet-water");
        PlanetWater = lib.GetBuiltin(ShaderSources.Paths.PlanetWaterVert, ShaderSources.Paths.PlanetWaterFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] planet-atmosphere");
        PlanetAtmosphere = lib.GetBuiltin(ShaderSources.Paths.PlanetAtmosphereVert, ShaderSources.Paths.PlanetAtmosphereFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] planet-cloud");
        PlanetCloud = lib.GetBuiltin(ShaderSources.Paths.PlanetCloudsVert, ShaderSources.Paths.PlanetCloudsFrag);

        if (logProgress) Game_Engine.Core.Log.Info("[Shader] post");
        PostProcess = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.PostProcessFrag);

#if !XBOX_UWP
        if (logProgress) Game_Engine.Core.Log.Info("[Shader] dof");
        DepthOfField = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.DepthOfFieldFrag);
#endif

        if (_includeVolFog && !_includeDeferred)
        {
            if (logProgress) Game_Engine.Core.Log.Info("[Shader] volfog");
            VolFog = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.VolumetricFogFrag);
        }

        if (_includeDeferred)
        {
            if (logProgress) Game_Engine.Core.Log.Info("[Shader] gbuffer");
            GBuffer = lib.GetBuiltin(ShaderSources.Paths.GBufferVert, ShaderSources.Paths.GBufferFrag);

            if (logProgress) Game_Engine.Core.Log.Info("[Shader] deferred-light");
            DeferredLight = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.DeferredLightingFrag);

            if (logProgress) Game_Engine.Core.Log.Info("[Shader] ssao");
            Ssao = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.SSAOFrag);
            SsaoBlur = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.SSAOBlurFrag);

            if (logProgress) Game_Engine.Core.Log.Info("[Shader] ssr");
            Ssr = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.SSRFrag);

            if (logProgress) Game_Engine.Core.Log.Info("[Shader] volfog");
            VolFog = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.VolumetricFogFrag);

            if (logProgress) Game_Engine.Core.Log.Info("[Shader] taa");
            TaaResolve = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.TaaResolveFrag);
            DepthCopy = lib.GetBuiltin(ShaderSources.Paths.FullscreenVert, ShaderSources.Paths.DepthCopyFrag);
        }
        else if (logProgress)
        {
#if XBOX_UWP
            // The Xbox player stays on the forward path. Linking deferred, SSAO, SSR,
            // and the volumetric-fog march here sits inside ANGLE's HLSL compiler and
            // the first frame never presents.
            Game_Engine.Core.Log.Info("[Shader] deferred set skipped");
#endif
        }
    }

    public void Dispose()
    {
        if (!OwnsResources) return;

        SceneFbo?.Dispose(); SceneFbo = null; SceneW = 0; SceneH = 0;
        GBufferFbo?.Dispose(); GBufferFbo = null; GBufferW = 0; GBufferH = 0;
        SsaoFbo?.Dispose(); SsaoFbo = null;
        SsaoBlurFbo?.Dispose(); SsaoBlurFbo = null;
        SsrFbo?.Dispose(); SsrFbo = null;
        VolFogFbo?.Dispose(); VolFogFbo = null;
        TaaHistoryFbo?.Dispose(); TaaHistoryFbo = null;
        TaaTempFbo?.Dispose(); TaaTempFbo = null;
        DofTempFbo?.Dispose(); DofTempFbo = null;
        DofOutFbo?.Dispose(); DofOutFbo = null;

        if (_gl != null)
        {
            if (ProbeCaptureDepthRbo != 0) { _gl.DeleteRenderbuffer(ProbeCaptureDepthRbo); ProbeCaptureDepthRbo = 0; }
            if (ProbeCaptureFbo != 0) { _gl.DeleteFramebuffer(ProbeCaptureFbo); ProbeCaptureFbo = 0; }
            ProbeCaptureSize = 0;
        }

        Canvas?.Dispose(); Canvas = null;
        TiledLights?.Dispose(); TiledLights = null;
        CascadedShadow?.Dispose(); CascadedShadow = null;
        Shadow?.Dispose(); Shadow = null;
        Cache?.Dispose(); Cache = null;
        FsQuad?.Dispose(); FsQuad = null;

        // GlobalUniforms owns the per-context UBOs (FrameGlobals, LightingGlobals, BoneGlobals).
        Globals?.Dispose();
        Globals = null;

        // ShaderLibrary owns and deletes every ShaderProgram compiled for this GL context.
        Shaders?.Dispose();
        Shaders = null;

        Standard = Depth = Sky = Terrain = Particle = Water = null;
        PlanetTerrain = PlanetWater = PlanetAtmosphere = PlanetCloud = PostProcess = DepthOfField = null;
        GBuffer = DeferredLight = Ssao = SsaoBlur = Ssr = VolFog = TaaResolve = DepthCopy = null;
        Grid = Wireframe = null;
    }
}
