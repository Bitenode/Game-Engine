#nullable enable
using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Game_Engine.Core.Component;
using Game_Engine.Core.Rendering.GPU;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering
{
    /// <summary>One play-mode GL frame. Desktop <c>PlayerView</c> and the Xbox host both call this.</summary>
    public static class PlayerFrameRenderer
    {
        static readonly Color FallbackSkyTop = Color.FromRgb(0x1f, 0x1f, 0x1f);
        static readonly Color FallbackSkyBot = Color.FromRgb(0x0a, 0x0a, 0x0a);

        public static void Render(GL g, PlayerFrameState s, int fb)
        {
            s.Res?.RefreshIfDirty();
            if (s.Res?.Standard == null || s.Res.Sky == null || s.Res.FsQuad == null || s.Res.Cache == null)
                return;

            FrameProfile.Enter(FrameProfile.Part.Sky);
            while (g.GetError() != GLEnum.NoError) { }

            s.FrameWatch.Restart();
            double dt = s.FpsTick.IsRunning ? s.FpsTick.Elapsed.TotalSeconds : 0.0;
            s.FpsTick.Restart();
            UpdateFps(s, dt);
            WindSystem.Update((float)Math.Min(dt, 0.1));

            double Wdip = Math.Max(1.0, s.WidthDip);
            double Hdip = Math.Max(1.0, s.HeightDip);
            int W = Math.Max(1, (int)(Wdip * s.RenderScale));
            int H = Math.Max(1, (int)(Hdip * s.RenderScale));

            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Less);
            g.Disable(EnableCap.Blend);
            g.ColorMask(true, true, true, true);
            g.DepthMask(true);

            if (!s.Playing)
            {
                g.ClearColor(0f, 0f, 0f, 1f);
                g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                Cleanup(g, s, fb);
                return;
            }

            var sky = s.Sky;
            var skyTop = sky?.Top ?? FallbackSkyTop;
            var skyBot = sky?.Bottom ?? FallbackSkyBot;
            Texture2D? skyTex = sky?.Texture;
            float skyMix = sky != null ? Math.Clamp(sky.TextureBlend, 0f, 1f) : 0f;
            float skyYaw = sky?.Yaw ?? 0f;
            SN.Vector3? planetSkyUp = null;

            SN.Vector3? sunDir = null;
            if (sky != null)
            {
                float elevRad = Math.Clamp(sky.SunElevation, 1f, 89f) * MathF.PI / 180f;
                float yawRad = sky.Yaw * MathF.PI / 180f;
                var baseSun = new SN.Vector3(0f, MathF.Sin(elevRad), MathF.Cos(elevRad));
                var rotY = SN.Matrix4x4.CreateFromAxisAngle(SN.Vector3.UnitY, yawRad);
                sunDir = SN.Vector3.Normalize(SN.Vector3.Transform(baseSun, rotY));
            }

            float ambient = Math.Clamp(sky?.Ambient ?? 0f, 0f, 1f);
            var light = s.Light;
            SN.Vector3 L = SN.Vector3.UnitY;
            float diffuseK = 0f;
            bool lightIsPoint = false;
            SN.Vector3 lightPosW = SN.Vector3.Zero;
            float lightRange = 0f;
            SN.Vector3 lightColorNorm = new SN.Vector3(1f, 1f, 1f);
            if (light?.gameObject != null)
            {
                float lum = (light.Color.R * 0.2126f + light.Color.G * 0.7152f + light.Color.B * 0.0722f) / 255f;
                diffuseK = Math.Max(light.Intensity * Math.Max(lum, 0.001f), 0.001f);
                lightColorNorm = new SN.Vector3(light.Color.R / 255f, light.Color.G / 255f, light.Color.B / 255f);
                var m = SceneGraphUtil.AccumulateWorld(light.gameObject);
                if (light.Type == LightType.Directional)
                {
                    var fwd = SN.Vector3.Normalize(new SN.Vector3(m.M13, m.M23, m.M33));
                    L = -fwd;
                }
                else
                {
                    lightIsPoint = true;
                    lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, m);
                    lightRange = Math.Max(0.001f, light.Range);
                }
            }

            Camera? cam = s.Cameras.Count > 0 ? s.Cameras[0] : null;
            SN.Matrix4x4 view, proj;
            if (cam != null)
            {
                view = cam.GetViewMatrix();
                proj = cam.GetProjectionMatrix(new Size(Wdip, Hdip));
            }
            else
            {
                view = SN.Matrix4x4.CreateLookAt(new SN.Vector3(0, 5, 10), SN.Vector3.Zero, SN.Vector3.UnitY);
                proj = SN.Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, (float)(Wdip / Math.Max(1, Hdip)), 0.1f, 1000f);
            }

            SN.Matrix4x4.Invert(view, out var invView);
            var camPos = new SN.Vector3(invView.M41, invView.M42, invView.M43);

            // Same rule as the editor's Game view: inside a planet's atmosphere shell the space
            // panorama is replaced by that atmosphere's zenith/horizon colors. Dropping only the
            // panorama left the scene skybox's own near-black gradient as the sky.
            foreach (var planet in PlanetTerrain.ActivePlanets)
            {
                if (planet?.Config == null || planet.gameObject == null || !planet.IsActiveAndEnabled) continue;
                var atmo = planet.Atmosphere;
                if (atmo == null || !atmo.Enabled) continue;

                var pp = planet.gameObject.Transform.Position;
                var center = new SN.Vector3((float)pp.X, (float)pp.Y, (float)pp.Z);
                float groundR = atmo.GroundRadiusOverride > 0.01f ? atmo.GroundRadiusOverride : planet.Config.Radius;
                float atmoR = groundR + Math.Max(1f, atmo.AtmosphereHeight);
                if ((camPos - center).LengthSquared() <= atmoR * atmoR)
                {
                    skyTex = null;
                    skyMix = 0f;
                    var top = atmo.ZenithTint;
                    var bot = atmo.HorizonTint;
                    skyTop = Color.FromRgb((byte)Math.Clamp((int)(top.X * 255f), 0, 255), (byte)Math.Clamp((int)(top.Y * 255f), 0, 255), (byte)Math.Clamp((int)(top.Z * 255f), 0, 255));
                    skyBot = Color.FromRgb((byte)Math.Clamp((int)(bot.X * 255f), 0, 255), (byte)Math.Clamp((int)(bot.Y * 255f), 0, 255), (byte)Math.Clamp((int)(bot.Z * 255f), 0, 255));
                    var up = camPos - center;
                    if (up.LengthSquared() > 1f)
                        planetSkyUp = up;
                    break;
                }
            }
            SceneRenderer.TryApplyPlanetAtmosphereAmbient(camPos, light, ref ambient);

            g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
            g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            var res = s.Res!;
            Sky.RenderGPU(g, res.Sky!, res.FsQuad!, res.Cache!, view, proj, skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw, planetSkyUp);

            FrameProfile.Enter(FrameProfile.Part.Shadows);
            var sunSD = -(sunDir ?? SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f)));
            var fallbackPlanetSunDir = sunSD.LengthSquared() > 1e-8f
                ? SN.Vector3.Normalize(sunSD)
                : SN.Vector3.Normalize(new SN.Vector3(-0.35f, 0.60f, 0.45f));

            GameRenderPipeline.RenderShadowMaps(g, res, (uint)fb, W, H,
                in view, in proj, camPos, fallbackPlanetSunDir, lightIsPoint,
                out GPUFramebuffer? shadowFBO, out SN.Matrix4x4 shadowVP, out CascadedShadowMap? activeCsm);

            // Capture up to 1 pending ReflectionProbe cubemap per frame before main passes
            SceneRenderer.CaptureReflectionProbes(g, res,
                SN.Vector3.Normalize(-L), diffuseK, ambient,
                lightIsPoint, lightPosW, lightRange,
                shadowFBO, in shadowVP, sunSD, lightColorNorm, light,
                skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw, fallbackPlanetSunDir, planetSkyUp);

            FrameProfile.Enter(FrameProfile.Part.Other);
            var underwater = UnderwaterQuery.GetState(camPos);
            var postSettings = PostProcessVolume.SampleBlended(camPos);
            bool usePostFX = (postSettings != null || underwater != null) && res.PostProcess != null;
            bool useDeferred = GameRenderPipeline.UseDeferred(res);
            bool onPlanet = PlanetTerrain.ActivePlanets.Count > 0;
            bool useTaa = postSettings?.TAAEnabled == true && useDeferred && !onPlanet;

            if (cam != null && cam.InvalidateTemporalHistory)
            {
                s.TaaResetHistory = true;
                cam.InvalidateTemporalHistory = false;
            }
            if (useTaa)
            {
                s.TaaFrameCounter++;
                int fi = (s.TaaFrameCounter % 16) + 1;
                float jx = (GameRenderPipeline.TaaHalton(fi, 2) - 0.5f) * 2f / Math.Max(1, W);
                float jy = (GameRenderPipeline.TaaHalton(fi, 3) - 0.5f) * 2f / Math.Max(1, H);
                proj.M13 += jx;
                proj.M23 += jy;
            }
            SN.Matrix4x4.Invert(view * proj, out var invVpCurr);
            SN.Matrix4x4 prevVp = s.TaaResetHistory ? (view * proj) : s.PrevViewProj;

            GameRenderPipeline.UpdateLod(camPos);

            FrameProfile.Enter(FrameProfile.Part.Scene);
            GPUTexture? finalSceneTex = null;
            GPUTexture? sceneDepthTex = null;
            if (useDeferred)
            {
                finalSceneTex = GameRenderPipeline.RenderDeferred(g, res, W, H,
                    view, proj, camPos, L, diffuseK, ambient,
                    lightIsPoint, lightPosW, lightRange,
                    shadowFBO, shadowVP, sunSD, lightColorNorm, light, sky,
                    skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw,
                    postSettings, fallbackPlanetSunDir, planetSkyUp, activeCsm);
                s.SceneFbo = res.SceneFbo;
                s.SceneW = res.SceneW;
                s.SceneH = res.SceneH;
                sceneDepthTex = res.SceneFbo?.DepthTexture ?? res.GBufferFbo?.DepthTexture;

                FrameProfile.Enter(FrameProfile.Part.WorldUi);
                if (res.Canvas != null && res.Cache != null)
                {
                    var viewProj = view * proj;
                    foreach (var wc in Component.UI.Canvas.All)
                    {
                        if (wc.IsActiveAndEnabled && wc.RenderMode == Component.UI.CanvasRenderMode.WorldSpace)
                            res.Canvas.RenderWorldCanvas(wc, in viewProj, res.Cache);
                    }
                }

                GameRenderPipeline.RunPostDeferredEffects(g, res, W, H,
                    in view, in proj, camPos, sunSD, lightColorNorm,
                    shadowFBO, in shadowVP, postSettings,
                    useTaa, in invVpCurr, in prevVp, ref s.TaaResetHistory, ref finalSceneTex);
                s.PrevViewProj = view * proj;
            }
            else
            {
                s.TaaResetHistory = true;
                if (usePostFX)
                {
                    if (s.SceneFbo == null) s.SceneFbo = new GPUFramebuffer(g);
                    if (s.SceneW != W || s.SceneH != H)
                    {
                        s.SceneFbo.SetupColorDepth(W, H);
                        s.SceneW = W;
                        s.SceneH = H;
                    }
                    s.SceneFbo.Bind();
                    g.ClearColor(0.12f, 0.12f, 0.15f, 1f);
                    g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    Sky.RenderGPU(g, res.Sky!, res.FsQuad!, res.Cache!, view, proj, skyTop, skyBot, sunDir, skyTex, skyMix, skyYaw, planetSkyUp);
                }

                GameRenderPipeline.RenderForwardWorld(g, res,
                    view, proj, camPos, L, diffuseK, ambient,
                    lightIsPoint, lightPosW, lightRange,
                    shadowFBO, shadowVP, sunSD, lightColorNorm, light, sky, fallbackPlanetSunDir, activeCsm);

                FrameProfile.Enter(FrameProfile.Part.WorldUi);
                if (res.Canvas != null && res.Cache != null)
                {
                    var viewProj = view * proj;
                    foreach (var wc in Component.UI.Canvas.All)
                    {
                        if (wc.IsActiveAndEnabled && wc.RenderMode == Component.UI.CanvasRenderMode.WorldSpace)
                            res.Canvas.RenderWorldCanvas(wc, in viewProj, res.Cache);
                    }
                }

                finalSceneTex = usePostFX ? s.SceneFbo?.ColorTexture : null;
                sceneDepthTex = usePostFX ? s.SceneFbo?.DepthTexture : null;
                s.PrevViewProj = view * proj;
            }

            FrameProfile.Enter(FrameProfile.Part.Post);
            if (finalSceneTex != null && res.PostProcess != null)
            {
                GameRenderPipeline.RunPostProcessAndPresent(g, res, (uint)fb, W, H,
                    in view, in proj, camPos, finalSceneTex, sceneDepthTex,
                    usePostFX ? postSettings : null, usePostFX ? underwater : null,
                    usePostFX ? (float)Time.time : 0f);
            }

            FrameProfile.Enter(FrameProfile.Part.Particles);
            if (res.Particle != null && res.Cache != null)
            {
                g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
                g.Viewport(0, 0, (uint)W, (uint)H);
                SceneRenderer.RenderParticles(g, res.Particle, res.Cache, view, proj, W, H, overlayPass: true);
            }

            FrameProfile.Enter(FrameProfile.Part.Ui);
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.Viewport(0, 0, (uint)W, (uint)H);
            if (res.Canvas != null && res.Cache != null)
                res.Canvas.RenderOverlays(W, H, res.Cache);

            FrameProfile.Enter(FrameProfile.Part.Finish);
            g.Flush();
            Cleanup(g, s, fb);
        }

        static void Cleanup(GL g, PlayerFrameState s, int fb)
        {
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            g.UseProgram(0);
            ShaderProgram.NotifyProgramUnbound(g);
            g.BindVertexArray(0);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            g.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
            g.Disable(EnableCap.DepthTest);
            g.Disable(EnableCap.CullFace);
            g.Disable(EnableCap.Blend);
            g.ActiveTexture(TextureUnit.Texture0);
            g.BindTexture(TextureTarget.Texture2D, 0);
            s.FrameWatch.Stop();
            s.MsFrameLast = s.FrameWatch.Elapsed.TotalMilliseconds;
            s.MsFrameEma = s.MsFrameEma <= 0 ? s.MsFrameLast : (1 - 0.18) * s.MsFrameEma + 0.18 * s.MsFrameLast;
            s.RenderInFlight = false;
        }

        static void UpdateFps(PlayerFrameState s, double dt)
        {
            s.FpsFrames++;
            if (!s.FpsWindow.IsRunning) s.FpsWindow.Restart();
            if (s.FpsWindow.ElapsedMilliseconds >= 500)
            {
                s.FpsDisplay = s.FpsFrames / s.FpsWindow.Elapsed.TotalSeconds;
                s.FpsFrames = 0;
                s.FpsWindow.Restart();
            }
        }

        public static void RebuildSceneCaches(PlayerFrameState s)
        {
            s.Sky = SceneQuery.FindBehaviors<Skybox>().FirstOrDefault();
            s.Light = SceneQuery.FindBehaviors<Light>().FirstOrDefault(l => l.Enabled);
            s.Cameras.Clear();
            foreach (var c in SceneQuery.FindBehaviors<Camera>()) s.Cameras.Add(c);
        }
    }
}
