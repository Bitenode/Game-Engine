#nullable enable
using System;
using Game_Engine.Core.Component;
using Game_Engine.Core.Rendering.GPU;
using Game_Engine.Core;
using Silk.NET.OpenGL;
using SN = System.Numerics;
using AColor = Avalonia.Media.Color;

namespace Game_Engine.Core.Rendering;

/// <summary>Shared play/player world draw: planet stack + deferred lighting.</summary>
public static class GameRenderPipeline
{
    public static bool UseDeferred(bool needsPostCapture)
        => ProjectRenderingSettings.UseDeferredRendering && !needsPostCapture;

    public static void UpdateLod(SN.Vector3 camPos)
    {
        TerrainStreamer.SyncAll(camPos);
        foreach (var root in SceneService.Root) WalkTerrain(root, camPos);
        foreach (var root in SceneService.Root) WalkTree(root, camPos);
        foreach (var root in SceneService.Root) WalkMeshLod(root, camPos);
        foreach (var planet in PlanetTerrain.ActivePlanets)
            planet?.RefreshLodAroundCamera(camPos);
        PlanetVegetationSystem.TickAllStreaming(camPos, Time.deltaTime);
    }

    static void WalkTerrain(GameObject go, SN.Vector3 cam)
    {
        foreach (var b in go.Behaviors)
            if (b is Terrain t && t.Enabled) { t.UpdateLOD(cam); break; }
        foreach (var c in go.Children) WalkTerrain(c, cam);
    }

    static void WalkTree(GameObject go, SN.Vector3 cam)
    {
        foreach (var b in go.Behaviors)
            if (b is TreeLOD tl && tl.Enabled) { tl.UpdateLOD(cam); break; }
        foreach (var c in go.Children) WalkTree(c, cam);
    }

    static void WalkMeshLod(GameObject go, SN.Vector3 cam)
    {
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
        SN.Vector3 lightColor, Light? light, Skybox? sky, SN.Vector3 fallbackSun)
    {
        if (res.Cache == null) return;
        if (res.PlanetTerrain != null && res.Standard != null)
        {
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null) continue;
                var tp = planet.gameObject?.Transform?.Position;
                var pc = tp != null ? new SN.Vector3((float)tp.X, (float)tp.Y, (float)tp.Z) : SN.Vector3.Zero;
                var atmo = SceneRenderer.ResolvePlanetAtmosphere(planet, light, fallbackSun, ambient);
                SceneRenderer.RenderPlanetTerrain(g, res.PlanetTerrain, res.Cache, view, proj, planet, atmo,
                    lightDir, diffuseK, camPos, pc, shadowFbo, shadowVP);
            }
            SceneRenderer.RenderPlanetVegetationAfterTerrain(g, res.Standard, res.Cache, view, proj, camPos,
                lightDir, diffuseK, ambient, lightIsPoint, lightPosW, lightRange,
                shadowFbo, shadowVP, sunSD, isES: res.IsES, lightColor: lightColor);
        }

        if (res.Water != null)
        {
            var skyC = sky != null
                ? new SN.Vector3(sky.Top.R / 255f, sky.Top.G / 255f, sky.Top.B / 255f)
                : new SN.Vector3(0.5f, 0.6f, 0.8f);
            SceneRenderer.RenderWater(g, res.Water, res.Cache, view, proj, lightDir, ambient, diffuseK, camPos, skyC);
        }

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
        PostProcessVolume? postVolume, SN.Vector3 fallbackSun)
    {
        if (res.GBuffer == null || res.Cache == null || res.FsQuad == null || res.DeferredLight == null || res.Sky == null)
            return null;

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
        bool useSSAO = postVolume?.SSAOEnabled == true;
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
            float ssaoRadius = postVolume != null ? Math.Clamp(postVolume.SSAORadius, 0.05f, 3f) : 0.5f;
            float ssaoBias = postVolume != null ? Math.Clamp(postVolume.SSAOBias, 0.0001f, 0.2f) : 0.025f;
            int ssaoSamples = postVolume != null ? Math.Clamp(postVolume.SSAOSamples, 4, 32) : 24;
            float depthSig = postVolume != null ? Math.Clamp(postVolume.SSAODepthSigma, 1f, 500f) : 80f;
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
        Sky.RenderGPU(g, res.Sky, res.FsQuad, res.Cache, view, proj, skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw);

        g.BindVertexArray(res.FsQuad.VAO);
        float ssaoIntensity = postVolume != null ? postVolume.SSAOIntensity : 1f;
        foreach (var rp in ReflectionProbe.ActiveProbes)
            rp.EnsureGpuResources(g);
        var probePick = ReflectionProbe.GetBestForPosition(camPos);
        SceneRenderer.RenderDeferredLighting(g, res.DeferredLight, res.FsQuad, res.GBufferFbo, ssaoResult, shadowFbo,
            view, proj, camPos, shadowVP, sunSD, ambient, 0.008f, ssaoIntensity, res.TiledLights, W, H,
            probePick?.GpuCubemap, probePick?.Intensity ?? 0f);
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
                shadowFbo, shadowVP, sunSD, terrainShader: res.Terrain, isES: res.IsES, lightColor: lightColor);
        }

        RenderPlanetStack(g, res, view, proj, camPos, SN.Vector3.Normalize(-L), diffuseK, ambient,
            lightIsPoint, lightPosW, lightRange, shadowFbo, shadowVP, sunSD, lightColor, light, sky, fallbackSun);

        return res.SceneFbo.ColorTexture;
    }

    public static void RenderForwardWorld(
        GL g, ViewRenderResources res,
        SN.Matrix4x4 view, SN.Matrix4x4 proj,
        SN.Vector3 camPos, SN.Vector3 L, float diffuseK, float ambient,
        bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
        GPUFramebuffer? shadowFbo, SN.Matrix4x4 shadowVP, SN.Vector3 sunSD,
        SN.Vector3 lightColor, Light? light, Skybox? sky, SN.Vector3 fallbackSun)
    {
        if (res.Standard == null || res.Depth == null || res.Cache == null) return;
        SceneRenderer.RenderGPU(g, res.Standard, res.Depth, res.Cache,
            view, proj, SN.Vector3.Normalize(-L), diffuseK, ambient,
            lightIsPoint, lightPosW, lightRange,
            shadowFbo, shadowVP, camPos, sunSD,
            terrainShader: res.Terrain, isES: res.IsES, lightColor: lightColor);
        RenderPlanetStack(g, res, view, proj, camPos, SN.Vector3.Normalize(-L), diffuseK, ambient,
            lightIsPoint, lightPosW, lightRange, shadowFbo, shadowVP, sunSD, lightColor, light, sky, fallbackSun);
    }
}
