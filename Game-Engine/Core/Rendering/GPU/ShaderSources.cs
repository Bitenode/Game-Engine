#nullable enable
using System.Collections.Generic;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Slim backward-compatible facade over the modular GLSL files in <c>Core/Rendering/Shaders/</c>
/// and <see cref="ShaderPreprocessor"/>. New engine code should prefer <see cref="ShaderLibrary"/>
/// so shaders benefit from variant caching, line-mapped errors, and live hot-reload.
/// </summary>
public static class ShaderSources
{
    // ── Built-in Logical Shader Paths ─────────────────────────────────────────
    public static class Paths
    {
        public const string FullscreenVert       = "Common/Fullscreen.vert.glsl";

        public const string StandardVert         = "Core/Standard.vert.glsl";
        public const string StandardFrag         = "Core/Standard.frag.glsl";
        public const string GBufferVert          = "Core/Standard.vert.glsl";
        public const string GBufferFrag          = "Core/GBuffer.frag.glsl";
        public const string DeferredLightingFrag = "Core/DeferredLighting.frag.glsl";
        public const string DepthOnlyVert        = "Core/DepthOnly.vert.glsl";
        public const string DepthOnlyFrag        = "Core/DepthOnly.frag.glsl";

        public const string TerrainVert          = "Terrain/Terrain.vert.glsl";
        public const string TerrainFrag          = "Terrain/Terrain.frag.glsl";

        public const string PlanetTerrainVert    = "Planet/PlanetTerrain.vert.glsl";
        public const string PlanetTerrainFrag    = "Planet/PlanetTerrain.frag.glsl";
        public const string PlanetWaterVert      = "Planet/PlanetWater.vert.glsl";
        public const string PlanetWaterFrag      = "Planet/PlanetWater.frag.glsl";
        public const string PlanetAtmosphereVert = "Planet/PlanetAtmosphere.vert.glsl";
        public const string PlanetAtmosphereFrag = "Planet/PlanetAtmosphere.frag.glsl";
        public const string PlanetCloudsVert     = "Planet/PlanetClouds.vert.glsl";
        public const string PlanetCloudsFrag     = "Planet/PlanetClouds.frag.glsl";
        public const string PlanetGpuGrassVert   = "Planet/PlanetGpuGrass.vert.glsl";
        public const string PlanetGpuGrassFrag   = "Planet/PlanetGpuGrass.frag.glsl";
        public const string PlanetForestCanopyVert = "Planet/PlanetForestCanopy.vert.glsl";
        public const string PlanetForestCanopyFrag = "Planet/PlanetForestCanopy.frag.glsl";

        public const string SkyVert              = "Effects/Sky.vert.glsl";
        public const string SkyFrag              = "Effects/Sky.frag.glsl";
        public const string WaterVert            = "Effects/Water.vert.glsl";
        public const string WaterFrag            = "Effects/Water.frag.glsl";
        public const string ParticleVert         = "Effects/Particle.vert.glsl";
        public const string ParticleFrag         = "Effects/Particle.frag.glsl";

        public const string PostProcessFrag      = "PostProcess/PostProcess.frag.glsl";
        public const string SSAOFrag             = "PostProcess/SSAO.frag.glsl";
        public const string SSAOBlurFrag         = "PostProcess/SSAOBlur.frag.glsl";
        public const string SSRFrag              = "PostProcess/SSR.frag.glsl";
        public const string VolumetricFogFrag    = "PostProcess/VolumetricFog.frag.glsl";
        public const string TaaResolveFrag       = "PostProcess/TaaResolve.frag.glsl";
        public const string DepthCopyFrag        = "PostProcess/DepthCopy.frag.glsl";
        public const string BlitFrag             = "PostProcess/Blit.frag.glsl";
        public const string DepthOfFieldFrag     = "PostProcess/DepthOfField.frag.glsl";

        public const string GridVert             = "Editor/Grid.vert.glsl";
        public const string GridFrag             = "Editor/Grid.frag.glsl";
        public const string WireframeVert        = "Editor/Wireframe.vert.glsl";
        public const string WireframeFrag        = "Editor/Wireframe.frag.glsl";

        public const string UIVert               = "UI/UI.vert.glsl";
        public const string UIFrag               = "UI/UI.frag.glsl";
        public const string UITextFrag           = "UI/UIText.frag.glsl";
    }

    /// <summary>
    /// Adapt a GLSL source string for the current GL context.
    /// Delegates to <see cref="ShaderPreprocessor.AdaptSource"/>.
    /// </summary>
    public static string Adapt(string glsl, bool isES)
        => ShaderPreprocessor.AdaptSource(glsl, isES);

    /// <summary>
    /// How many splat normal and height samplers fit after the terrain shader's
    /// required maps (2 splats, 8 albedos, 1 shadow, normals 0–4 = 16).
    /// A 16-unit context keeps that set. Later units add normals 5–7, then height maps.
    /// </summary>
    public readonly struct TerrainSamplerBudget
    {
        public int NormalLayers { get; init; }
        public int HeightLayers { get; init; }
    }

    public static TerrainSamplerBudget TerrainBudget(int maxTextureUnits)
    {
        if (maxTextureUnits < 16) maxTextureUnits = 16;
        int extra = maxTextureUnits - 16;
        int extraNormals = extra > 3 ? 3 : extra;
        int height = extra - extraNormals;
        if (height > 8) height = 8;
        if (height < 0) height = 0;
        return new TerrainSamplerBudget
        {
            NormalLayers = 5 + extraNormals,
            HeightLayers = height
        };
    }

    /// <summary>Build the preprocessor variant key for <c>Terrain/Terrain.frag.glsl</c>.</summary>
    public static ShaderVariantKey TerrainVariantForUnits(int maxTextureUnits)
    {
        var budget = TerrainBudget(maxTextureUnits);
        return ShaderVariantKey.Of(
            ("TERRAIN_NORMAL_LAYERS", budget.NormalLayers.ToString()),
            ("TERRAIN_HEIGHT_LAYERS", budget.HeightLayers.ToString()));
    }

    /// <summary>
    /// Terrain fragment source limited to <paramref name="maxTextureUnits"/> samplers.
    /// </summary>
    public static string TerrainFragForUnits(int maxTextureUnits)
    {
        var budget = TerrainBudget(maxTextureUnits);
        var opts = new ShaderCompileOptions
        {
            IsES = false,
            Defines = new Dictionary<string, string?>
            {
                ["TERRAIN_NORMAL_LAYERS"] = budget.NormalLayers.ToString(),
                ["TERRAIN_HEIGHT_LAYERS"] = budget.HeightLayers.ToString()
            }
        };
        return ShaderPreprocessor.PreprocessBuiltin(Paths.TerrainFrag, opts).Source;
    }

    private static string LoadDesktop(string logicalPath)
        => ShaderPreprocessor.PreprocessBuiltin(logicalPath, ShaderCompileOptions.Desktop).Source;

    // ── Backward-Compatible Static Properties ─────────────────────────────────
    public static string StandardVert          => LoadDesktop(Paths.StandardVert);
    public static string StandardFrag          => LoadDesktop(Paths.StandardFrag);
    public static string GBufferVert           => LoadDesktop(Paths.GBufferVert);
    public static string GBufferFrag           => LoadDesktop(Paths.GBufferFrag);
    public static string DeferredLightingVert  => LoadDesktop(Paths.FullscreenVert);
    public static string DeferredLightingFrag  => LoadDesktop(Paths.DeferredLightingFrag);
    public static string SSAOVert              => LoadDesktop(Paths.FullscreenVert);
    public static string SSAOFrag              => LoadDesktop(Paths.SSAOFrag);
    public static string SSAOBlurFrag          => LoadDesktop(Paths.SSAOBlurFrag);
    public static string SSRVert               => LoadDesktop(Paths.FullscreenVert);
    public static string SSRFrag               => LoadDesktop(Paths.SSRFrag);
    public static string DepthOnlyVert         => LoadDesktop(Paths.DepthOnlyVert);
    public static string DepthOnlyFrag         => LoadDesktop(Paths.DepthOnlyFrag);
    public static string SkyVert               => LoadDesktop(Paths.SkyVert);
    public static string SkyFrag               => LoadDesktop(Paths.SkyFrag);
    public static string GridVert              => LoadDesktop(Paths.GridVert);
    public static string GridFrag              => LoadDesktop(Paths.GridFrag);
    public static string WireframeVert         => LoadDesktop(Paths.WireframeVert);
    public static string WireframeFrag         => LoadDesktop(Paths.WireframeFrag);
    public static string TerrainVert           => LoadDesktop(Paths.TerrainVert);
    public static string TerrainFrag           => LoadDesktop(Paths.TerrainFrag);
    public static string BlitVert              => LoadDesktop(Paths.FullscreenVert);
    public static string BlitFrag              => LoadDesktop(Paths.BlitFrag);
    public static string DepthCopyFrag         => LoadDesktop(Paths.DepthCopyFrag);
    public static string ParticleVert          => LoadDesktop(Paths.ParticleVert);
    public static string ParticleFrag          => LoadDesktop(Paths.ParticleFrag);
    public static string TaaResolveFrag        => LoadDesktop(Paths.TaaResolveFrag);
    public static string PostProcessVert       => LoadDesktop(Paths.FullscreenVert);
    public static string PostProcessFrag       => LoadDesktop(Paths.PostProcessFrag);
    public static string WaterVert             => LoadDesktop(Paths.WaterVert);
    public static string WaterFrag             => LoadDesktop(Paths.WaterFrag);
    public static string VolumetricFogVert     => LoadDesktop(Paths.FullscreenVert);
    public static string VolumetricFogFrag     => LoadDesktop(Paths.VolumetricFogFrag);
    public static string DepthOfFieldVert      => LoadDesktop(Paths.FullscreenVert);
    public static string DepthOfFieldFrag      => LoadDesktop(Paths.DepthOfFieldFrag);
    public static string UIVert                => LoadDesktop(Paths.UIVert);
    public static string UIFrag                => LoadDesktop(Paths.UIFrag);
    public static string UITextFrag            => LoadDesktop(Paths.UITextFrag);
    public static string PlanetTerrainVert     => LoadDesktop(Paths.PlanetTerrainVert);
    public static string PlanetTerrainFrag     => LoadDesktop(Paths.PlanetTerrainFrag);
    public static string PlanetTerrainFragXbox => ShaderPreprocessor.PreprocessBuiltin(
        Paths.PlanetTerrainFrag, new ShaderCompileOptions { IsES = false, IsXbox = true }).Source;
    public static string PlanetWaterVert       => LoadDesktop(Paths.PlanetWaterVert);
    public static string PlanetWaterFrag       => LoadDesktop(Paths.PlanetWaterFrag);
    public static string PlanetAtmosphereVert  => LoadDesktop(Paths.PlanetAtmosphereVert);
    public static string PlanetAtmosphereFrag  => LoadDesktop(Paths.PlanetAtmosphereFrag);
    public static string PlanetCloudsVert      => LoadDesktop(Paths.PlanetCloudsVert);
    public static string PlanetCloudsFrag      => LoadDesktop(Paths.PlanetCloudsFrag);
    public static string PlanetGpuGrassVert    => LoadDesktop(Paths.PlanetGpuGrassVert);
    public static string PlanetGpuGrassFrag    => LoadDesktop(Paths.PlanetGpuGrassFrag);
    public static string PlanetForestCanopyVert => LoadDesktop(Paths.PlanetForestCanopyVert);
    public static string PlanetForestCanopyFrag => LoadDesktop(Paths.PlanetForestCanopyFrag);
}
