#nullable enable
using System;
using Game_Engine.Core.Component;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core;
using Silk.NET.OpenGL;
using SN = System.Numerics;
using AColor = Avalonia.Media.Color;

namespace Game_Engine.Core.Rendering;

/// <summary>Shared play/player world draw: CSM shadows, planet stack, deferred lighting, and post-processing.</summary>
public static class GameRenderPipeline
{
    /// <summary>Xbox sets this so the console stays on the forward path until deferred is measured.</summary>
    public static bool PreferForward { get; set; }

    /// <summary>
    /// Deferred rendering is enabled whenever <see cref="ProjectRenderingSettings.UseDeferredRendering"/> is true,
    /// <see cref="PreferForward"/> is false (desktop), and the deferred shaders are compiled — regardless of
    /// whether a <see cref="PostProcessVolume"/> is active.
    /// </summary>
    public static bool UseDeferred(ViewRenderResources? res = null)
        => !PreferForward
           && ProjectRenderingSettings.UseDeferredRendering
           && (res == null || (res.DeferredLight != null && res.GBuffer != null));

    /// <summary>Backward-compatible overload (PostProcessVolume no longer disables deferred rendering).</summary>
    public static bool UseDeferred(bool needsPostCapture)
        => !PreferForward && ProjectRenderingSettings.UseDeferredRendering;

    /// <summary>
    /// Extract near and far clip planes from a perspective projection matrix
    /// (supports <see cref="SN.Matrix4x4.CreatePerspectiveFieldOfView"/> and standard GL perspective).
    /// </summary>
    public static void ExtractNearFar(in SN.Matrix4x4 proj, out float near, out float far)
    {
        near = 0.1f;
        far = 1000f;
        if (MathF.Abs(proj.M34) < 1e-5f) return; // orthographic

        // System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView:
        //   M33 = far / (near - far), M43 = (near * far) / (near - far)
        //   => near = M43 / M33, far = M43 / (M33 + 1)
        float m33 = proj.M33;
        float m43 = proj.M43;
        if (MathF.Abs(m33) > 1e-6f && MathF.Abs(m33 + 1f) > 1e-6f)
        {
            float n = m43 / m33;
            float f = m43 / (m33 + 1f);
            if (n > 0.0001f && f > n)
            {
                near = n;
                far = f;
                return;
            }
        }
    }

    public static float TaaHalton(int index, int b)
    {
        float f = 1f, r = 0f;
        int i = index;
        while (i > 0)
        {
            f /= b;
            r += f * (i % b);
            i /= b;
        }
        return r;
    }

    /// <summary>
    /// Render directional 4-cascade CSM (when available on desktop) or fall back to single <see cref="ShadowMapGPU"/>
    /// (for point lights or Xbox UWP).
    /// </summary>
    public static void RenderShadowMaps(
        GL g,
        ViewRenderResources res,
        uint restoreFb,
        int viewportW,
        int viewportH,
        in SN.Matrix4x4 view,
        in SN.Matrix4x4 proj,
        SN.Vector3 camPos,
        SN.Vector3 sunShineDir,
        bool lightIsPoint,
        out GPUFramebuffer? shadowFbo,
        out SN.Matrix4x4 shadowVP,
        out CascadedShadowMap? activeCsm)
    {
        shadowFbo = null;
        shadowVP = SN.Matrix4x4.Identity;
        activeCsm = null;

        if (res.Depth == null || res.Cache == null) return;

        if (!lightIsPoint && res.CascadedShadow != null)
        {
            ExtractNearFar(in proj, out float nearP, out float farP);
            res.CascadedShadow.Update(sunShineDir, in view, in proj, nearP, farP);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            SceneRenderer.RenderCascadedShadowPass(g, res.Depth, res.Cache, res.CascadedShadow, restoreFb);
            g.Viewport(0, 0, (uint)viewportW, (uint)viewportH);
            activeCsm = res.CascadedShadow;
            shadowFbo = res.CascadedShadow.Cascades[0].FBO;
            shadowVP = res.CascadedShadow.LightVPs[0];
        }
        else if (res.Shadow != null)
        {
            SN.Matrix4x4.Invert(view, out var invV);
            var camFwd = new SN.Vector3(-invV.M31, -invV.M32, -invV.M33);
            var sceneCenter = camPos + camFwd * 12f;
            shadowVP = ShadowMapGPU.BuildDirectionalLightVP(sunShineDir, sceneCenter, 50f, res.Shadow.Width);
            res.Shadow.LightVP = shadowVP;
            res.Shadow.Begin(g);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            SceneRenderer.RenderShadowPass(g, res.Depth, res.Cache, shadowVP);
            res.Shadow.End(g, restoreFb);
            g.Viewport(0, 0, (uint)viewportW, (uint)viewportH);
            shadowFbo = res.Shadow.FBO;
        }
    }

    public static void UpdateLod(SN.Vector3 camPos)
    {
        FrameProfile.Enter(FrameProfile.Part.SceneLod);
        TerrainStreamer.SyncAll(camPos);
        foreach (var root in SceneService.Root) WalkTerrain(root, camPos);
        foreach (var root in SceneService.Root) WalkTree(root, camPos);
        foreach (var root in SceneService.Root) WalkMeshLod(root, camPos);
        FrameProfile.Enter(FrameProfile.Part.PlanetLod);
        foreach (var planet in PlanetTerrain.ActivePlanets)
            planet?.RefreshLodAroundCamera(camPos);
        FrameProfile.Enter(FrameProfile.Part.PlantStreaming);
        PlanetVegetationSystem.TickAllStreaming(camPos, Time.deltaTime);
        FrameProfile.Enter(FrameProfile.Part.Other);
    }

    static void WalkTerrain(GameObject go, SN.Vector3 cam)
    {
        if (!go.Enabled) return;
        foreach (var b in go.Behaviors)
            if (b is Terrain t && t.Enabled) { t.UpdateLOD(cam); break; }
        foreach (var c in go.Children) WalkTerrain(c, cam);
    }

    static void WalkTree(GameObject go, SN.Vector3 cam)
    {
        if (!go.Enabled) return;
        foreach (var b in go.Behaviors)
            if (b is TreeLOD tl && tl.Enabled) { tl.UpdateLOD(cam); break; }
        foreach (var c in go.Children) WalkTree(c, cam);
    }

    static void WalkMeshLod(GameObject go, SN.Vector3 cam)
    {
        if (!go.Enabled) return;
        foreach (var b in go.Behaviors)
            if (b is MeshLodGroup m && m.Enabled) m.UpdateLOD(cam);
        foreach (var c in go.Children) WalkMeshLod(c, cam);
    }

    public static void RenderPlanetStack(
        GL g, ViewRenderResources res,
        SN.Matrix4x4 view, SN.Matrix4x4 proj,
        SN.Vector3 camPos, SN.Vector3 lightDir, float diffuseK, float ambient,
        bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
        GPUFramebuffer? shadowFbo, SN.Matrix4x4 shadowVP, SN.Vector3 sunSD,
        SN.Vector3 lightColor, Light? light, Skybox? sky, SN.Vector3 fallbackSun,
        CascadedShadowMap? cascadedShadow = null)
    {
        if (res.Cache == null) return;
        if (res.PlanetTerrain != null && res.Standard != null)
        {
            FrameProfile.Enter(FrameProfile.Part.PlanetGround);
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null) continue;
                var tp = planet.gameObject?.Transform?.Position;
                var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackSun, ambient);
                SceneRenderer.RenderPlanetTerrain(g, res.PlanetTerrain, res.Cache, view, proj, planet, atmo,
                    lightDir, diffuseK, camPos, pc, shadowFbo, shadowVP);
            }
            FrameProfile.Enter(FrameProfile.Part.PlanetPlants);
            SceneRenderer.RenderPlanetVegetationAfterTerrain(g, res.Standard, res.Cache, view, proj, camPos,
                lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                shadowFbo, shadowVP, sunSD, isES: res.IsES, lightColor: lightColor, cascadedShadow: cascadedShadow);
            // Forward re-draw of opaques exists only so meshes are not buried by the planet pass.
            // Without active planets it would just overwrite deferred shading with forward shading.
            if (PlanetTerrain.ActivePlanets.Count > 0)
            {
                FrameProfile.Enter(FrameProfile.Part.SceneAfterPlanet);
                SceneRenderer.RenderForwardSceneOpaques(g, res.Standard, res.Cache, view, proj, camPos,
                    lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                    shadowFbo, shadowVP, sunSD, isES: res.IsES, lightColor: lightColor, cascadedShadow: cascadedShadow);
            }
        }

        FrameProfile.Enter(FrameProfile.Part.Water);
        if (res.Water != null)
        {
            var skyC = sky != null
                ? new SN.Vector3(sky.Top.R / 255f, sky.Top.G / 255f, sky.Top.B / 255f)
                : new SN.Vector3(0.5f, 0.6f, 0.8f);
            SceneRenderer.RenderWater(g, res.Water, res.Cache, view, proj, lightDir, ambient, diffuseK, camPos, skyC);
        }

        FrameProfile.Enter(FrameProfile.Part.Atmosphere);
        if (res.PlanetAtmosphere != null)
        {
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null) continue;
                var tp = planet.gameObject?.Transform?.Position;
                var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackSun, ambient);
                SceneRenderer.RenderPlanetAtmosphere(g, res.PlanetAtmosphere, res.Cache, view, proj, planet, atmo, camPos, pc);
            }
        }

        FrameProfile.Enter(FrameProfile.Part.Clouds);
        if (res.PlanetCloud != null)
        {
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null) continue;
                var tp = planet.gameObject?.Transform?.Position;
                var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackSun, ambient);
                SceneRenderer.RenderPlanetClouds(g, res.PlanetCloud, res.Cache, view, proj, planet, atmo, camPos, pc, Time.time);
            }
        }

        FrameProfile.Enter(FrameProfile.Part.PlanetWater);
        if (res.PlanetWater != null)
        {
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null) continue;
                var tp = planet.gameObject?.Transform?.Position;
                var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackSun, ambient);
                SceneRenderer.RenderPlanetWater(g, res.PlanetWater, res.Cache, view, proj, planet, atmo,
                    lightDir, diffuseK, camPos, pc, planet.Config.SeaLevel);
            }
        }
    }

    public static GPUTexture? RenderDeferred(
        GL g, ViewRenderResources res, int W, int H,
        SN.Matrix4x4 view, SN.Matrix4x4 proj,
        SN.Vector3 camPos, SN.Vector3 L, float diffuseK, float ambient,
        bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
        GPUFramebuffer? shadowFbo, SN.Matrix4x4 shadowVP, SN.Vector3 sunSD,
        SN.Vector3 lightColor, Light? light, Skybox? sky,
        AColor skyTop, AColor skyBot, SN.Vector3? sunDir, Texture2D? skyTex, float skyMix, float skyYaw,
        PostProcessVolume? postVolume, SN.Vector3 fallbackSun, SN.Vector3? planetSkyUp = null,
        CascadedShadowMap? cascadedShadow = null)
        => RenderDeferred(g, res, W, H, view, proj, camPos, L, diffuseK, ambient,
            lightIsPoint, lightPosW, lightRange, shadowFbo, shadowVP, sunSD,
            lightColor, light, sky, skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw,
            postVolume != null ? PostProcessSettings.FromVolume(postVolume) : null,
            fallbackSun, planetSkyUp, cascadedShadow);

    public static GPUTexture? RenderDeferred(
        GL g, ViewRenderResources res, int W, int H,
        SN.Matrix4x4 view, SN.Matrix4x4 proj,
        SN.Vector3 camPos, SN.Vector3 L, float diffuseK, float ambient,
        bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
        GPUFramebuffer? shadowFbo, SN.Matrix4x4 shadowVP, SN.Vector3 sunSD,
        SN.Vector3 lightColor, Light? light, Skybox? sky,
        AColor skyTop, AColor skyBot, SN.Vector3? sunDir, Texture2D? skyTex, float skyMix, float skyYaw,
        PostProcessSettings? postSettings, SN.Vector3 fallbackSun, SN.Vector3? planetSkyUp = null,
        CascadedShadowMap? cascadedShadow = null)
    {
        if (res.GBuffer == null || res.Cache == null || res.FsQuad == null || res.DeferredLight == null || res.Sky == null)
            return null;

        FrameProfile.Enter(FrameProfile.Part.Scene);
        if (res.GBufferFbo == null) res.GBufferFbo = new GPUFramebuffer(g);
        if (res.GBufferW != W || res.GBufferH != H)
        {
            res.GBufferFbo.SetupGBuffer(W, H);
            res.GBufferW = W; res.GBufferH = H;
        }

        res.GBufferFbo.Bind();
        g.ClearColor(0f, 0f, 0f, 0f);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        SceneRenderer.RenderGBufferPass(g, res.GBuffer, res.Cache, view, proj, camPos, shadowFbo, shadowVP, sunSD, res.IsES);

        GPUTexture? ssaoResult = null;
        bool useSSAO = postSettings?.SSAOEnabled == true;
        if (useSSAO && res.Ssao != null && res.SsaoBlur != null)
        {
            int ssaoW = Math.Max(1, W / 2);
            int ssaoH = Math.Max(1, H / 2);
            if (res.SsaoFbo == null) res.SsaoFbo = new GPUFramebuffer(g);
            if (res.SsaoFbo.Width != ssaoW || res.SsaoFbo.Height != ssaoH)
                res.SsaoFbo.SetupColorDepth(ssaoW, ssaoH);
            if (res.SsaoBlurFbo == null) res.SsaoBlurFbo = new GPUFramebuffer(g);
            if (res.SsaoBlurFbo.Width != ssaoW || res.SsaoBlurFbo.Height != ssaoH)
                res.SsaoBlurFbo.SetupColorDepth(ssaoW, ssaoH);

            res.SsaoFbo.Bind();
            g.ClearColor(1f, 1f, 1f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit);
            float ssaoRadius = postSettings != null ? Math.Clamp(postSettings.SSAORadius, 0.05f, 3f) : 0.5f;
            float ssaoBias = postSettings != null ? Math.Clamp(postSettings.SSAOBias, 0.0001f, 0.2f) : 0.025f;
            int ssaoSamples = postSettings != null ? Math.Clamp(postSettings.SSAOSamples, 4, 32) : 24;
            float depthSig = postSettings != null ? Math.Clamp(postSettings.SSAODepthSigma, 1f, 500f) : 80f;
            SceneRenderer.RenderSSAO(g, res.Ssao, res.FsQuad, res.GBufferFbo, view, proj, W, H, ssaoRadius, ssaoBias, ssaoSamples);
            res.SsaoBlurFbo.Bind();
            g.ClearColor(1f, 1f, 1f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit);
            SceneRenderer.RenderSSAOBlur(g, res.SsaoBlur, res.FsQuad, res.SsaoFbo.ColorTexture!, res.GBufferFbo, ssaoW, ssaoH, depthSig);
            ssaoResult = res.SsaoBlurFbo.ColorTexture;
        }

        if (res.SceneFbo == null) res.SceneFbo = new GPUFramebuffer(g);
        if (res.SceneW != W || res.SceneH != H)
        {
            res.SceneFbo.SetupColorDepth(W, H);
            res.SceneW = W; res.SceneH = H;
        }

        res.SceneFbo.Bind();
        g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        Sky.RenderGPU(g, res.Sky, res.FsQuad, res.Cache, view, proj, skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw, planetSkyUp);

        g.BindVertexArray(res.FsQuad.VAO);
        float ssaoIntensity = postSettings != null ? postSettings.SSAOIntensity : 1f;
        var probePick = ReflectionProbe.GetBestForPosition(camPos, g);
        SceneRenderer.RenderDeferredLighting(g, res.DeferredLight, res.FsQuad, res.GBufferFbo, ssaoResult, shadowFbo,
            view, proj, camPos, shadowVP, sunSD, ambient, 0.008f, ssaoIntensity, res.TiledLights, W, H,
            probePick?.GetCubemap(g), probePick?.Intensity ?? 0f, cascadedShadow);
        g.BindVertexArray(0);

        res.SceneFbo.Bind();
        if (res.DepthCopy != null && res.GBufferFbo.DepthTexture != null)
            SceneRenderer.RenderDepthTextureToFramebufferDepth(g, res.DepthCopy, res.FsQuad, res.GBufferFbo.DepthTexture);
        else
        {
            g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, res.GBufferFbo.Handle);
            g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, res.SceneFbo.Handle);
            const ClearBufferMask StencilBufferBit = (ClearBufferMask)0x400;
            g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                ClearBufferMask.DepthBufferBit | StencilBufferBit, BlitFramebufferFilter.Nearest);
            if (g.GetError() != GLEnum.NoError)
            {
                while (g.GetError() != GLEnum.NoError) { }
                g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                    ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
            }
            res.SceneFbo.Bind();
        }

        if (res.Standard != null)
        {
            SceneRenderer.RenderForwardOverlays(g, res.Standard, res.Cache, view, proj, camPos,
                SN.Vector3.Normalize(-L), diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                shadowFbo, shadowVP, sunSD, terrainShader: res.Terrain, isES: res.IsES, lightColor: lightColor,
                cascadedShadow: cascadedShadow);
        }

        RenderPlanetStack(g, res, view, proj, camPos, SN.Vector3.Normalize(-L), diffuseK, ambient,
            lightIsPoint, lightPosW, lightRange, shadowFbo, shadowVP, sunSD, lightColor, light, sky, fallbackSun,
            cascadedShadow: cascadedShadow);

        return res.SceneFbo.ColorTexture;
    }

    public static void RenderForwardWorld(
        GL g, ViewRenderResources res,
        SN.Matrix4x4 view, SN.Matrix4x4 proj,
        SN.Vector3 camPos, SN.Vector3 L, float diffuseK, float ambient,
        bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
        GPUFramebuffer? shadowFbo, SN.Matrix4x4 shadowVP, SN.Vector3 sunSD,
        SN.Vector3 lightColor, Light? light, Skybox? sky, SN.Vector3 fallbackSun,
        CascadedShadowMap? cascadedShadow = null)
    {
        if (res.Standard == null || res.Depth == null || res.Cache == null) return;
        FrameProfile.Enter(FrameProfile.Part.Scene);
        SceneRenderer.RenderGPU(g, res.Standard, res.Depth, res.Cache,
            view, proj, SN.Vector3.Normalize(-L), diffuseK, ambient,
            lightIsPoint, lightPosW, lightRange,
            shadowFbo, shadowVP, camPos, sunSD,
            terrainShader: res.Terrain, isES: res.IsES, lightColor: lightColor,
            cascadedShadow: cascadedShadow);
        RenderPlanetStack(g, res, view, proj, camPos, SN.Vector3.Normalize(-L), diffuseK, ambient,
            lightIsPoint, lightPosW, lightRange, shadowFbo, shadowVP, sunSD, lightColor, light, sky, fallbackSun,
            cascadedShadow: cascadedShadow);
    }

    /// <summary>
    /// Shared post-deferred screen-space effects (SSR -> Volumetric Fog -> TAA) used by both
    /// Editor <c>GameView</c> and standalone <c>PlayerFrameRenderer</c>.
    /// </summary>
    public static void RunPostDeferredEffects(
        GL g, ViewRenderResources res, int W, int H,
        in SN.Matrix4x4 view, in SN.Matrix4x4 proj, SN.Vector3 camPos,
        SN.Vector3 sunSD, SN.Vector3 lightColor,
        GPUFramebuffer? shadowFbo, in SN.Matrix4x4 shadowVP,
        PostProcessSettings? postSettings,
        bool useTaa, in SN.Matrix4x4 invVpCurr, in SN.Matrix4x4 prevVp,
        ref bool taaResetHistory,
        ref GPUTexture? finalSceneTex)
    {
        if (res.FsQuad == null) return;
        finalSceneTex ??= res.SceneFbo?.ColorTexture;
        var depthTex = res.SceneFbo?.DepthTexture ?? res.GBufferFbo?.DepthTexture;

        // 1. SSR — screen-space reflections
        if (postSettings?.SSREnabled == true && res.Ssr != null && res.SceneFbo?.ColorTexture != null && res.GBufferFbo != null)
        {
            if (res.SsrFbo == null) res.SsrFbo = new GPUFramebuffer(g);
            if (res.SsrFbo.Width != W || res.SsrFbo.Height != H)
                res.SsrFbo.SetupColorDepth(W, H);

            res.SsrFbo.Bind();
            g.ClearColor(0f, 0f, 0f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit);

            g.BindVertexArray(res.FsQuad.VAO);
            SceneRenderer.RenderSSR(g, res.Ssr, res.FsQuad, res.SceneFbo.ColorTexture, res.GBufferFbo,
                view, proj, camPos, W, H,
                postSettings.SSRMaxRaySteps,
                postSettings.SSRRoughnessCutoff,
                postSettings.SSRMaxRayDistance);
            g.BindVertexArray(0);

            finalSceneTex = res.SsrFbo.ColorTexture;
        }

        // 2. Volumetric Fog — ray-marched fullscreen pass
        if (res.VolFog != null && postSettings?.VolumetricFogEnabled == true
            && finalSceneTex != null && depthTex != null)
        {
            if (res.VolFogFbo == null) res.VolFogFbo = new GPUFramebuffer(g);
            if (res.VolFogFbo.Width != W || res.VolFogFbo.Height != H)
                res.VolFogFbo.SetupColorDepth(W, H);

            res.VolFogFbo.Bind();
            g.ClearColor(0f, 0f, 0f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit);

            g.BindVertexArray(res.FsQuad.VAO);
            SceneRenderer.RenderVolumetricFog(g, res.VolFog, res.FsQuad,
                finalSceneTex, depthTex,
                view, proj, camPos,
                sunSD, lightColor,
                shadowFbo, shadowVP, postSettings,
                (float)Time.time);
            g.BindVertexArray(0);

            finalSceneTex = res.VolFogFbo.ColorTexture;
        }

        // 3. TAA — temporal resolve
        if (useTaa && res.TaaResolve != null && finalSceneTex != null && depthTex != null)
        {
            if (res.TaaHistoryFbo == null) res.TaaHistoryFbo = new GPUFramebuffer(g);
            if (res.TaaHistoryFbo.Width != W || res.TaaHistoryFbo.Height != H)
                res.TaaHistoryFbo.SetupColorDepth(W, H);
            if (res.TaaTempFbo == null) res.TaaTempFbo = new GPUFramebuffer(g);
            if (res.TaaTempFbo.Width != W || res.TaaTempFbo.Height != H)
                res.TaaTempFbo.SetupColorDepth(W, H);

            res.TaaTempFbo.Bind();
            g.Viewport(0, 0, (uint)W, (uint)H);
            g.Clear(ClearBufferMask.ColorBufferBit);
            g.BindVertexArray(res.FsQuad.VAO);
            SceneRenderer.RenderTemporalAA(g, res.TaaResolve, res.FsQuad, finalSceneTex,
                res.TaaHistoryFbo.ColorTexture, depthTex, invVpCurr, prevVp, W, H,
                postSettings?.TAAFrameBlend ?? 0.12f,
                postSettings?.TAASharpen ?? 0.35f,
                taaResetHistory);
            g.BindVertexArray(0);

            g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, res.TaaTempFbo.Handle);
            g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, res.TaaHistoryFbo.Handle);
            g.BlitFramebuffer(0, 0, W, H, 0, 0, W, H,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);

            finalSceneTex = res.TaaTempFbo.ColorTexture;
            taaResetHistory = false;
        }
        else if (!useTaa)
        {
            taaResetHistory = true;
        }
    }

    /// <summary>
    /// Shared Depth-of-Field + PostProcess (Depth/Height Fog, Bloom, Color Grading, Vignette, FXAA, Underwater)
    /// presentation pass into <paramref name="targetFb"/>.
    /// </summary>
    public static void RunPostProcessAndPresent(
        GL g, ViewRenderResources res, uint targetFb, int W, int H,
        in SN.Matrix4x4 view, in SN.Matrix4x4 proj, SN.Vector3 camPos,
        GPUTexture? finalSceneTex, GPUTexture? sceneDepthTex,
        PostProcessSettings? postSettings, UnderwaterState? underwater, float time)
    {
        if (finalSceneTex == null || res.PostProcess == null || res.FsQuad == null)
            return;

        // Ensure FrameGlobals UBO carries accurate near/far and invView/invProj for Depth Fog & DoF
        ExtractNearFar(in proj, out float nearP, out float farP);
        GlobalUniforms.ForContext(g).UpdateFrame(in view, in proj, camPos, time, W, H, nearP, farP);

        // Optional 2-pass separable Depth of Field before final PostProcess
        if (postSettings?.DepthOfFieldEnabled == true && res.DepthOfField != null && sceneDepthTex != null)
        {
            if (res.DofTempFbo == null) res.DofTempFbo = new GPUFramebuffer(g);
            if (res.DofTempFbo.Width != W || res.DofTempFbo.Height != H)
                res.DofTempFbo.SetupColorDepth(W, H);
            if (res.DofOutFbo == null) res.DofOutFbo = new GPUFramebuffer(g);
            if (res.DofOutFbo.Width != W || res.DofOutFbo.Height != H)
                res.DofOutFbo.SetupColorDepth(W, H);

            g.BindVertexArray(res.FsQuad.VAO);
            finalSceneTex = SceneRenderer.RenderDepthOfField(g, res.DepthOfField, res.FsQuad,
                finalSceneTex, sceneDepthTex, res.DofTempFbo, res.DofOutFbo, W, H,
                postSettings, nearP, farP);
            g.BindVertexArray(0);
        }

        // Final PostProcess / Blit -> target framebuffer
        g.BindFramebuffer(FramebufferTarget.Framebuffer, targetFb);
        g.Viewport(0, 0, (uint)W, (uint)H);
        g.Disable(EnableCap.DepthTest);
        g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
        g.Clear(ClearBufferMask.ColorBufferBit);
        g.BindVertexArray(res.FsQuad.VAO);
        SceneRenderer.ApplyPostProcessing(g, res.PostProcess, finalSceneTex, W, H,
            postSettings, underwater, time, sceneDepthTex);
        g.BindVertexArray(0);
        g.Enable(EnableCap.DepthTest);
    }
}
