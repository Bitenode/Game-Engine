using System;
using System.Collections.Generic;
using System.Linq;
using SN = System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Game_Engine.Core;
using CoreVec3 = Game_Engine.Core.Vector3;
using Avalonia.Platform;
using System.Reflection;
using System.Numerics;
using System.Diagnostics;
using Avalonia.Threading;

namespace Game_Engine.Views;


public class SceneView : Control
{
    #region Camera & selection
    float _yaw = -30f * MathF.PI / 180f;
    float _pitch = -20f * MathF.PI / 180f;
    float _distance = 8f;
    SN.Vector3 _target = SN.Vector3.Zero;

    // --- Free-fly state ---
    readonly HashSet<Key> _keysDown = new();
    DispatcherTimer _flyTimer;
    readonly Stopwatch _flyWatch = new();

    // tune to taste
    float _flyBaseSpeed = 5f;   // units/sec at distance≈1
    float _flyBoostMul = 4f;   // Shift multiplier
    float _flySlowMul = 0.25f; // Ctrl multiplier

    Point _last;
    bool _orbiting, _panning;

    bool _logNextRender;            // log once on next render

    GameObject? _selected;

    // Reuse uv-sphere meshes by (lon,lat) so we don't allocate every frame.
    readonly Dictionary<(int lon, int lat), Mesh> _uvSphereCache = new();

    #endregion

    #region Tooling (Move/Rotate/Scale)
    public enum ToolMode { Hand, Move, Rotate, Scale }

    public static readonly StyledProperty<ToolMode> ToolProperty =
        AvaloniaProperty.Register<SceneView, ToolMode>(nameof(Tool), ToolMode.Hand);

    public ToolMode Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    // Global grid / wire / light / 2D toggles
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowGrid), true);
    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public static readonly StyledProperty<bool> ShowWireProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowWire), false);
    public bool ShowWire
    {
        get => GetValue(ShowWireProperty);
        set => SetValue(ShowWireProperty, value);
    }

    public static readonly StyledProperty<bool> ShowLightProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(ShowLight), true);
    public bool ShowLight
    {
        get => GetValue(ShowLightProperty);
        set => SetValue(ShowLightProperty, value);
    }

    public static readonly StyledProperty<bool> Is2DProperty =
        AvaloniaProperty.Register<SceneView, bool>(nameof(Is2D), false);
    public bool Is2D
    {
        get => GetValue(Is2DProperty);
        set => SetValue(Is2DProperty, value);
    }

    public static readonly StyledProperty<bool> Supersample2xProperty =
    AvaloniaProperty.Register<SceneView, bool>(nameof(Supersample2x), false);

    public bool Supersample2x
    {
        get => GetValue(Supersample2xProperty);
        set => SetValue(Supersample2xProperty, value);
    }

    public static readonly StyledProperty<bool> GizmoLocalProperty =
    AvaloniaProperty.Register<SceneView, bool>(nameof(GizmoLocal), true);
    public bool GizmoLocal { get => GetValue(GizmoLocalProperty); set => SetValue(GizmoLocalProperty, value); }


    // Snap
    public bool SnapEnabled { get; set; } = false;
    public float SnapStep { get; set; } = 0.5f;

    static SceneView()
    {
        ToolProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowGridProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowWireProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        ShowLightProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        Is2DProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
        Supersample2xProperty.Changed.AddClassHandler<SceneView>((s, _) => s.InvalidateVisual());
    }
    #endregion

    #region Translate gizmo state
    const double GizmoScreenLen = 80.0; // pixels
    const double GizmoPickPixels = 10.0; // hit slop
    enum Axis { None, X, Y, Z }

    Axis _gizmoHot = Axis.None;

    // drag session
    Axis _dragAxis = Axis.None;
    bool _isDragging;

    SN.Vector3 _dragAxisW; // chosen axis in WORLD space
    SN.Vector3 _dragAnchorW; // world-space anchor at drag start
    SN.Vector3 _dragObjStartW; // object's origin world position at start
    CoreVec3 _dragObjStartLocal; // object's local position at start
    SN.Vector3 _dragPlaneN; // plane normal for screen-plane intersection
    CoreVec3 _dragStartRotation; // captured at BeginAxisDrag
    CoreVec3 _dragStartScale; // captured at BeginAxisDrag
    #endregion

    #region Constants & helpers
    const float NearEps = 0.001f;

    static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);

    static uint PackBGRA(Color c) => (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));

    static Color ShadeColor(Color c, float s)
    {
        s = Math.Clamp(s, 0f, 1f);
        byte r = (byte)Math.Clamp(c.R * s, 0, 255);
        byte g = (byte)Math.Clamp(c.G * s, 0, 255);
        byte b = (byte)Math.Clamp(c.B * s, 0, 255);
        return Color.FromArgb(c.A, r, g, b);
    }


    static SN.Matrix4x4 WorldFromTransform(Core.Transform t)
    {
        var s = SN.Matrix4x4.CreateScale((float)t.Scale.X, (float)t.Scale.Y, (float)t.Scale.Z);
        var r = SN.Matrix4x4.CreateFromYawPitchRoll(Deg2Rad(t.Rotation.Y), Deg2Rad(t.Rotation.X), Deg2Rad(t.Rotation.Z));
        var tr = SN.Matrix4x4.CreateTranslation((float)t.Position.X, (float)t.Position.Y, (float)t.Position.Z);
        return s * r * tr;
    }

    // Computes a world-space AABB for a GameObject subtree.
    // If it contains no mesh, we fallback to the object's world origin.
    (SN.Vector3 min, SN.Vector3 max) ComputeWorldAABB(GameObject root)
    {
        bool hasPoint = false;
        SN.Vector3 min = default, max = default;
        void Expand(in SN.Vector3 p)
        {
            if (!hasPoint) { min = max = p; hasPoint = true; }
            else
            {
                min = new SN.Vector3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
                max = new SN.Vector3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
            }
        }

        void Walk(GameObject go, SN.Matrix4x4 parentW)
        {
            var W = parentW * WorldFromTransform(go.Transform);

            // Renderers can have more than one MeshFilter on the same GO
            foreach (var mf in go.Behaviors.OfType<MeshFilter>())
            {
                var vtx = mf.Mesh?.Vertices;
                if (mf.Enabled && vtx is { Length: > 0 })
                {
                    for (int i = 0; i < vtx.Length; i++)
                        Expand(SN.Vector3.Transform(vtx[i], W));
                }
            }

            // If there was no mesh at all, at least include the origin
            if (!go.Behaviors.OfType<MeshFilter>().Any())
                Expand(SN.Vector3.Transform(SN.Vector3.Zero, W));

            foreach (var ch in go.Children)
                Walk(ch, W);
        }

        Walk(root, SN.Matrix4x4.Identity);
        return (min, max);
    }

    void FrameSelected(GameObject go)
    {
        var (min, max) = ComputeWorldAABB(go);
        var center = (min + max) * 0.5f;
        float radius = (max - center).Length(); // sphere that encloses the AABB corners
        _target = center;
        // Fit to vertical FOV
        float fov = 60f * MathF.PI / 180f; // keep in sync with GetViewProj
        float fit = radius / MathF.Tan(fov * 0.5f); // distance to fit vertically
        _distance = MathF.Max(1.5f, fit * 1.15f); // a little padding
        InvalidateVisual();
    }

    static Color MulColor(Color a, Color b)
    {
        byte r = (byte)((a.R * b.R) / 255);
        byte g = (byte)((a.G * b.G) / 255);
        byte b2 = (byte)((a.B * b.B) / 255);
        return Color.FromArgb(255, r, g, b2);
    }

    // --- Lighting & sky helpers -----------------------------------------------
    // forward (-Z) from a Transform (matches your yaw/pitch/roll order)
    static SN.Vector3 ForwardFrom(Core.Transform t)
    {
        var r = SN.Matrix4x4.CreateFromYawPitchRoll(Deg2Rad(t.Rotation.Y), Deg2Rad(t.Rotation.X), Deg2Rad(t.Rotation.Z));
        var f = SN.Vector3.TransformNormal(new SN.Vector3(0, 0, -1), r);
        return SN.Vector3.Normalize(f);
    }

    // enumerate enabled behaviors of type T across the whole scene
    static IEnumerable<T> FindBehaviors<T>() where T : Behavior
    {
        static IEnumerable<GameObject> Traverse(GameObject n)
        {
            yield return n;
            foreach (var c in n.Children)
                foreach (var s in Traverse(c)) yield return s;
        }
        foreach (var root in SceneService.Root)
            foreach (var go in Traverse(root))
                foreach (var b in go.Behaviors)
                    if (b.Enabled && b is T t) yield return t;
    }

    // BGRA row lerp (for sky gradient)
    static uint LerpBGRA(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int ab = (int)(a & 0xFF), ag = (int)((a >> 8) & 0xFF), ar = (int)((a >> 16) & 0xFF), aa = (int)((a >> 24) & 0xFF);
        int bb = (int)(b & 0xFF), bg = (int)((b >> 8) & 0xFF), br = (int)((b >> 16) & 0xFF), ba = (int)((b >> 24) & 0xFF);
        int rb = (int)(ab + (bb - ab) * t);
        int rg = (int)(ag + (bg - ag) * t);
        int rr = (int)(ar + (br - ar) * t);
        int ra = (int)(aa + (ba - aa) * t);
        return (uint)(rb | (rg << 8) | (rr << 16) | (ra << 24));
    }

    static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    static Color SampleLatlongBilinear(Game_Engine.Core.Texture2D tex, SN.Vector3 dirW)
    {
        var d = SN.Vector3.Normalize(dirW);
        // yaw: [-pi..pi], pitch: [-pi/2..pi/2]
        float yaw = MathF.Atan2(d.X, d.Z);                 // -Z forward ≈ u=0.5
        float pitch = MathF.Asin(Math.Clamp(d.Y, -1f, 1f));

        // to [0..1]
        float u = 0.5f + yaw / (2f * MathF.PI);
        if (u < 0f) u += 1f; else if (u >= 1f) u -= 1f;     // wrap X
        float v = 0.5f - pitch / MathF.PI;                  // clamp Y later

        int w = Math.Max(1, tex.Width);
        int h = Math.Max(1, tex.Height);

        float x = u * (w - 1);
        float y = Math.Clamp(v * (h - 1), 0f, h - 1);

        int x0 = (int)MathF.Floor(x);
        int x1 = (x0 + 1) % w;
        int y0 = (int)MathF.Floor(y);
        int y1 = Math.Min(y0 + 1, h - 1);

        float tx = x - x0, ty = y - y0;

        static void Read(byte[] d, int i, out float r, out float g, out float b, out float a)
        { r = d[i + 0] / 255f; g = d[i + 1] / 255f; b = d[i + 2] / 255f; a = d[i + 3] / 255f; }

        int i00 = (y0 * w + x0) * 4, i01 = (y0 * w + x1) * 4;
        int i10 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;

        Read(tex.Rgba, i00, out var r00, out var g00, out var b00, out var a00);
        Read(tex.Rgba, i01, out var r01, out var g01, out var b01, out var a01);
        Read(tex.Rgba, i10, out var r10, out var g10, out var b10, out var a10);
        Read(tex.Rgba, i11, out var r11, out var g11, out var b11, out var a11);

        float r0 = r00 * (1 - tx) + r01 * tx;
        float g0 = g00 * (1 - tx) + g01 * tx;
        float b0 = b00 * (1 - tx) + b01 * tx;
        float a0 = a00 * (1 - tx) + a01 * tx;

        float r1 = r10 * (1 - tx) + r11 * tx;
        float g1 = g10 * (1 - tx) + g11 * tx;
        float b1 = b10 * (1 - tx) + b11 * tx;
        float a1 = a10 * (1 - tx) + a11 * tx;

        float a = a0 * (1 - ty) + a1 * ty;
        float r = (r0 * (1 - ty) + r1 * ty);
        float g = (g0 * (1 - ty) + g1 * ty);
        float b = (b0 * (1 - ty) + b1 * ty);

        // if the atlas has alpha, composite on black (most skyboxes are opaque anyway)
        if (a > 0f) { r *= a; g *= a; b *= a; }

        return Color.FromArgb(255,
            (byte)Math.Clamp((int)(r * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(g * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(b * 255f + 0.5f), 0, 255));
    }

    static int FloorMod(int x, int m) => (x % m + m) % m;

    // Bilinear sample with REPEAT addressing (premultiplied-safe).
    static Avalonia.Media.Color SamplePMRepeat(Game_Engine.Core.Texture2D tex, float u, float v)
    {
        if (tex.Width <= 0 || tex.Height <= 0)
            return Avalonia.Media.Color.FromArgb(255, 255, 255, 255);

        // Wrap into [0,1)
        u = u - MathF.Floor(u);
        v = v - MathF.Floor(v);

        // Pixel space, -0.5 so that u==0 samples centered on texel 0 and can lerp to the last texel across the seam.
        float px = u * tex.Width - 0.5f;
        float py = v * tex.Height - 0.5f;

        int x0 = FloorMod((int)MathF.Floor(px), tex.Width);
        int y0 = FloorMod((int)MathF.Floor(py), tex.Height);
        int x1 = (x0 + 1) % tex.Width;
        int y1 = (y0 + 1) % tex.Height;

        float tx = px - MathF.Floor(px);
        float ty = py - MathF.Floor(py);

        static void Premul(byte[] d, int i, out float r, out float g, out float b, out float a)
        {
            a = d[i + 3] / 255f;
            float R = d[i + 0] / 255f, G = d[i + 1] / 255f, B = d[i + 2] / 255f;
            r = R * a; g = G * a; b = B * a;
        }

        int i00 = (y0 * tex.Width + x0) * 4;
        int i01 = (y0 * tex.Width + x1) * 4;
        int i10 = (y1 * tex.Width + x0) * 4;
        int i11 = (y1 * tex.Width + x1) * 4;

        Premul(tex.Rgba, i00, out var r00, out var g00, out var b00, out var a00);
        Premul(tex.Rgba, i01, out var r01, out var g01, out var b01, out var a01);
        Premul(tex.Rgba, i10, out var r10, out var g10, out var b10, out var a10);
        Premul(tex.Rgba, i11, out var r11, out var g11, out var b11, out var a11);

        float r0 = r00 * (1 - tx) + r01 * tx;
        float g0 = g00 * (1 - tx) + g01 * tx;
        float b0 = b00 * (1 - tx) + b01 * tx;
        float a0 = a00 * (1 - tx) + a01 * tx;

        float r1 = r10 * (1 - tx) + r11 * tx;
        float g1 = g10 * (1 - tx) + g11 * tx;
        float b1 = b10 * (1 - tx) + b11 * tx;
        float a1 = a10 * (1 - tx) + a11 * tx;

        float r = r0 * (1 - ty) + r1 * ty;
        float g = g0 * (1 - ty) + g1 * ty;
        float b = b0 * (1 - ty) + b1 * ty;
        float a = a0 * (1 - ty) + a1 * ty;

        if (a > 1e-6f) { r /= a; g /= a; b /= a; } else { r = g = b = 0f; }

        return Avalonia.Media.Color.FromArgb(
            (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(r * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(g * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(b * 255f + 0.5f), 0, 255));
    }



    void FillSkyWorldUp(
    uint[] color, float[] zbuf, int W, int H,
    in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
    Color topCol, Color botCol,
    SN.Vector3? sunDirWorld = null,
    Game_Engine.Core.Texture2D? skyTex = null,
    float skyTexBlend = 0f,
    float skyYawDegrees = 0f,          //  rotate sky horizontally
    float seamFeather = 0f,            //  0..~0.02 recommended
    bool keyOutNearBlack = false,      //  turn black JPG corners into alpha
    float keyLuma = 0.03f              //  luma threshold for keying (0..1)
)
    {
        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        static float Smooth01(float t) => t <= 0 ? 0 : (t >= 1 ? 1 : t * t * (3 - 2 * t));
        static Color LerpColor(Color a, Color b, float t)
        {
            t = Clamp01(t);
            byte r = (byte)(a.R + (b.R - a.R) * t);
            byte g = (byte)(a.G + (b.G - a.G) * t);
            byte b2 = (byte)(a.B + (b.B - a.B) * t);
            byte a2 = (byte)(a.A + (b.A - a.A) * t);
            return Color.FromArgb(a2, r, g, b2);
        }

        uint top = PackBGRA(topCol);
        uint bot = PackBGRA(botCol);

        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var invVP);

        var worldUp = SN.Vector3.UnitY;

        // optional sun highlight
        SN.Vector3 sun = SN.Vector3.Zero;
        bool useSun = false;
        if (sunDirWorld.HasValue)
        {
            sun = SN.Vector3.Normalize(sunDirWorld.Value);
            useSun = sun.LengthSquared() > 0.5f;
        }

        bool useTex = skyTex != null && skyTexBlend > 0.0001f;

        float yawRad = skyYawDegrees * (MathF.PI / 180f);
        var yawM = SN.Matrix4x4.CreateFromAxisAngle(worldUp, yawRad);

        // small pole clamp to avoid ringing at top/bottom rows
        float poleEps = (useTex && skyTex!.Height > 1) ? (0.5f / skyTex.Height) : 0f;
        // a sensible default feather if caller passes 0
        if (useTex && seamFeather <= 0f) seamFeather = MathF.Max(1f / (skyTex!.Width * 2f), 0.0015f);

        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            float ny = 1f - ((y + 0.5f) / H) * 2f;   // [-1..+1]

            for (int x = 0; x < W; x++)
            {
                float nx = ((x + 0.5f) / W) * 2f - 1f; // [-1..+1]

                // World-space view ray
                var n4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0f, 1f), invVP);
                var f4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1f, 1f), invVP);
                var n3 = new SN.Vector3(n4.X, n4.Y, n4.Z) / n4.W;
                var f3 = new SN.Vector3(f4.X, f4.Y, f4.Z) / f4.W;
                var dir = SN.Vector3.Normalize(f3 - n3);

                // rotate sky horizontally (moves the seam)
                if (yawRad != 0f)
                    dir = SN.Vector3.Transform(dir, yawM);

                // Gradient by "up" with optional sun highlight
                float t = Clamp01(0.5f + 0.5f * SN.Vector3.Dot(dir, worldUp));
                if (useSun)
                {
                    float sunGlow = MathF.Pow(MathF.Max(0f, SN.Vector3.Dot(dir, sun)), 64f);
                    t = Clamp01(t + sunGlow * 0.08f);
                }
                uint pix = LerpBGRA(bot, top, t);

                // Sky texture overlay (equirect/lat-long)
                if (useTex)
                {
                    // forward = -Z
                    float u = 0.5f + MathF.Atan2(dir.X, -dir.Z) / (2f * MathF.PI);
                    // wrap to [0,1)
                    u = u - MathF.Floor(u);
                    float v = 0.5f - MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) / MathF.PI;
                    v = Math.Clamp(v, poleEps, 1f - poleEps);

                    // sample with optional seam feathering
                    Color samp;
                    if (seamFeather > 0f)
                    {
                        float d = MathF.Min(u, 1f - u); // distance to either edge
                        if (d < seamFeather)
                        {
                            float k = Smooth01(d / seamFeather);          // 0 at edge → 1 away from edge
                            float uOther = (u < 0.5f) ? (u + 1f) : (u - 1f); // opposite side of seam
                            var a = SamplePMRepeat(skyTex!, u, v);
                            var b = SamplePMRepeat(skyTex!, uOther, v);
                            samp = LerpColor(b, a, k); // cross-fade across wrap
                        }
                        else
                        {
                            samp = SamplePMRepeat(skyTex!, u, v);
                        }
                    }
                    else
                    {
                        samp = SamplePMRepeat(skyTex!, u, v);
                    }

                    // optional “key out near black” for non-alpha JPGs
                    if (keyOutNearBlack)
                    {
                        float luma = (0.2126f * samp.R + 0.7152f * samp.G + 0.0722f * samp.B) / 255f;
                        if (luma <= keyLuma) samp = Color.FromArgb(0, samp.R, samp.G, samp.B);
                    }

                    // Use the sample's alpha to modulate blend weight
                    float w = skyTexBlend * (samp.A / 255f);
                    // ignore 'samp.A' for color channels (we already baked it into w)
                    var sampRGB = Color.FromRgb(samp.R, samp.G, samp.B);
                    pix = LerpBGRA(pix, PackBGRA(sampRGB), w);

                }

                color[row + x] = pix;
                zbuf[row + x] = 1.1f; // clear Z
            }
        }
    }





    // --- CPU shadow map (directional) -------------------------------------------
    struct ShadowMap
    {
        public SN.Matrix4x4 VP; // light view-projection
        public float[] Depth; // size = W*H, 0..1 depth
        public int W, H;
        public float Bias; // depth bias (in NDC space)
    }

    bool HandleFlyKeyDown(Key k)
    {
        // movement + modifiers
        if (k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E
              or Key.LeftShift or Key.RightShift
              or Key.LeftCtrl or Key.RightCtrl)
        {
            if (_keysDown.Add(k))
            {
                if (!_flyTimer.IsEnabled)
                {
                    _flyWatch.Restart();
                    _flyTimer.Start();
                }
            }
            return true;
        }
        return false;
    }

    bool HandleFlyKeyUp(Key k)
    {
        if (_keysDown.Remove(k))
        {
            // stop when nothing is held
            if (_keysDown.Count == 0 && _flyTimer.IsEnabled)
                _flyTimer.Stop();
            return true;
        }
        return false;
    }

    void StepFly()
    {
        // don’t fight gizmo drags
        if (_isDragging) return;
        double dt = _flyWatch.Elapsed.TotalSeconds;
        _flyWatch.Restart();
        if (dt <= 0) return;
        // camera basis from yaw/pitch (same as GetViewProj)
        var dir = new SN.Vector3(
            MathF.Cos(_pitch) * MathF.Cos(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Sin(_yaw));
        var up = SN.Vector3.UnitY;
        var right = SN.Vector3.Normalize(SN.Vector3.Cross(dir, up));
        SN.Vector3 move = SN.Vector3.Zero;
        if (_keysDown.Contains(Key.W)) move += dir;
        if (_keysDown.Contains(Key.S)) move -= dir;
        if (_keysDown.Contains(Key.A)) move -= right;
        if (_keysDown.Contains(Key.D)) move += right;
        if (_keysDown.Contains(Key.E)) move += up;
        if (_keysDown.Contains(Key.Q)) move -= up;
        if (move.LengthSquared() < 1e-8f) return;
        move = SN.Vector3.Normalize(move);
        // speed scales a bit with zoom distance (feels nice when far/near)
        float distScale = Math.Clamp(_distance * 0.35f, 0.5f, 20f);
        float mul = 1f;
        if (_keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift)) mul *= _flyBoostMul;
        if (_keysDown.Contains(Key.LeftCtrl) || _keysDown.Contains(Key.RightCtrl)) mul *= _flySlowMul;
        float speed = _flyBaseSpeed * distScale * (float)dt * mul;
        // move the camera’s look-target; the eye tracks it via GetViewProj
        _target += move * speed;
        InvalidateVisual();
    }



    #endregion

    #region Engine Texture helpers (shared with Inspector)
    static Game_Engine.Core.Texture2D? TryCreateEngineTextureFromPath(string path)
    {
        var t = typeof(Game_Engine.Core.Texture2D);

        // static FromFile(string)
        var m = t.GetMethod("FromFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (m is not null) return (Game_Engine.Core.Texture2D?)m.Invoke(null, new object?[] { path });

        // static Load(string)
        m = t.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (m is not null) return (Game_Engine.Core.Texture2D?)m.Invoke(null, new object?[] { path });

        // ctor(string)
        var ctor = t.GetConstructor(new[] { typeof(string) });
        if (ctor is not null) return (Game_Engine.Core.Texture2D?)ctor.Invoke(new object?[] { path });

        return null;
    }

    static Game_Engine.Core.Texture2D? TryCreateEngineTextureFromBytes(byte[] bytes)
    {
        var t = typeof(Game_Engine.Core.Texture2D);

        // static FromBytes(byte[]) or Load(byte[])
        var m = t.GetMethod("FromBytes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(byte[]) }, modifiers: null)
             ?? t.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(byte[]) }, modifiers: null);

        return m is null ? null : (Game_Engine.Core.Texture2D?)m.Invoke(null, new object?[] { bytes });
    }

    /// <summary>
    /// Accepts path/stream/byte[]/bitmap/engine objects and returns a real Texture2D if possible.
    /// </summary>
    static Game_Engine.Core.Texture2D? EnsureEngineTexture2D(object? texObj)
    {
        if (texObj is null) return null;
        if (texObj is Game_Engine.Core.Texture2D t2d) return t2d;

        var t = texObj.GetType();

        //Path-like properties
        foreach (var n in new[] { "Path", "FilePath", "SourcePath" })
        {
            var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p?.GetValue(texObj) is string s && !string.IsNullOrWhiteSpace(s) && System.IO.File.Exists(s))
            {
                var tex = TryCreateEngineTextureFromPath(s);
                if (tex != null) return tex;

                try
                {
                    var bytes = System.IO.File.ReadAllBytes(s);
                    tex = TryCreateEngineTextureFromBytes(bytes);
                    if (tex != null) return tex;
                }
                catch { }
            }
        }

        // OpenRead(): Stream
        if (t.GetMethod("OpenRead", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                is { } open &&
            open.Invoke(texObj, null) is System.IO.Stream stream)
        {
            try
            {
                using (stream)
                using (var ms = new System.IO.MemoryStream())
                {
                    stream.CopyTo(ms);
                    var tex = TryCreateEngineTextureFromBytes(ms.ToArray());
                    if (tex != null) return tex;
                }
            }
            catch { }
        }

        // GetBytes()/ToBytes(): byte[]
        foreach (var n in new[] { "GetBytes", "ToBytes" })
        {
            var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m?.Invoke(texObj, null) is byte[] bytes && bytes.Length > 0)
            {
                var tex = TryCreateEngineTextureFromBytes(bytes);
                if (tex != null) return tex;
            }
        }

        // Avalonia Bitmap → bytes
        if (texObj is Avalonia.Media.Imaging.Bitmap bmp)
        {
            using var ms = new System.IO.MemoryStream();
            try
            {
                bmp.Save(ms); // PNG
                var tex = TryCreateEngineTextureFromBytes(ms.ToArray());
                if (tex != null) return tex;
            }
            catch { }
        }

        return null;
    }
    #endregion


    #region Ctor & event hookup
    public SceneView()
    {
        Focusable = true;
        ClipToBounds = true;
        // current selection snapshot
        _selected = SelectionService.Current;
        // selection changes
        SelectionService.Changed += () =>
        {
            _selected = SelectionService.Current;
            _logNextRender = true; // log when selection changes
            InvalidateVisual();
        };
        // scene graph/material changes
        SceneService.Changed += () =>
        {
            _logNextRender = true; // log when material list changes, etc.
            InvalidateVisual();
        };
        // scene graph changes
        SceneService.Changed += () => InvalidateVisual();
        // input
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        _flyTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _flyTimer.Tick += (_, __) => StepFly();
    }
    #endregion

    #region Input: orbit/pan & gizmo drag
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F && _selected != null)
        {
            FrameSelected(_selected);
            e.Handled = true;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { Game_Engine.Core.UndoService.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            { Game_Engine.Core.UndoService.Redo(); e.Handled = true; }
        }
        if (HandleFlyKeyDown(e.Key))
        {
            e.Handled = true;
            return;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (HandleFlyKeyUp(e.Key))
            e.Handled = true;
    }

    void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        Focus();
        _last = e.GetPosition(this);
        var p = e.GetCurrentPoint(this).Properties;
        // Try translate gizmo first when in Move/Rotate/Scale
        if (Tool != ToolMode.Hand && _selected != null)
        {
            var (view, proj) = GetViewProj(Bounds.Size);
            if (BeginAxisDrag(_last, view, proj, Bounds.Size))
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }
        // Orbit / Pan fallback
        if (p.IsLeftButtonPressed || p.IsRightButtonPressed) _orbiting = true;
        if (p.IsMiddleButtonPressed) _panning = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    void OnPointerMoved(object? s, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        // Continue gizmo drag
        if (_isDragging && _dragAxis != Axis.None && _selected != null)
        {
            var (view, proj) = GetViewProj(Bounds.Size);
            bool axisOnly = e.KeyModifiers.HasFlag(KeyModifiers.Alt); // hold Alt = per-axis
            UpdateAxisDrag(pos, view, proj, Bounds.Size, axisOnly);
            e.Handled = true;
            return;
        }
        // Orbit/Pan
        var d = pos - _last;
        _last = pos;
        if (_orbiting)
        {
            _yaw += (float)d.X * 0.01f;
            _pitch -= (float)d.Y * 0.01f;
            _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);
            InvalidateVisual();
        }
        else if (_panning)
        {
            var (view, _) = GetViewProj(Bounds.Size);
            var right = SN.Vector3.Normalize(new SN.Vector3(view.M11, view.M21, view.M31));
            var up = SN.Vector3.Normalize(new SN.Vector3(view.M12, view.M22, view.M32));
            float sxy = 0.01f * _distance;
            _target += (-right * (float)d.X + up * (float)d.Y) * sxy;
            InvalidateVisual();
        }
    }

    void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        _orbiting = _panning = false;
        if (_isDragging)
        {
            _isDragging = false;
            if (_selected != null && _dragAxis != Axis.None)
            {
                var t = _selected.Transform;
                var cmd = new SetTransformPositionCmd(t, _dragObjStartLocal, t.Position);
                Game_Engine.Core.UndoService.Exec(cmd);
            }
            _dragAxis = Axis.None;
            SceneService.NotifyChanged(); // refresh inspector bindings
        }
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
        e.Handled = true;
    }

    void OnWheel(object? s, PointerWheelEventArgs e)
    {
        _distance *= (float)Math.Pow(1.1, -e.Delta.Y);
        _distance = Math.Clamp(_distance, 1.5f, 200f);
        InvalidateVisual();
    }
    #endregion

    #region Gizmo hit/drag
    bool BeginAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var axis = HitTestTranslateGizmo(mouse, view, proj, sz);
        if (axis == Axis.None) return false;
        BeginAxisDrag(mouse, axis, view, proj, sz);
        return true;
    }

    void BeginAxisDrag(Point mouse, Axis axis, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        _dragAxis = axis;
        _isDragging = true;
        // capture selected transforms at start
        var W = AccumulateWorld(_selected!);
        _dragStartScale = _selected!.Transform.Scale;
        _dragStartRotation = _selected!.Transform.Rotation;
        // object origin in world space
        _dragAnchorW = SN.Vector3.Transform(SN.Vector3.Zero, W);
        _dragObjStartW = _dragAnchorW;
        _dragObjStartLocal = _selected!.Transform.Position;
        // axis in world space (normalize to remove scaling)
        _dragAxisW = axis switch
        {
            Axis.X => new SN.Vector3(W.M11, W.M21, W.M31),
            Axis.Y => new SN.Vector3(W.M12, W.M22, W.M32),
            Axis.Z => new SN.Vector3(W.M13, W.M23, W.M33),
            _ => SN.Vector3.UnitX
        };
        if (_dragAxisW.LengthSquared() < 1e-8f)
            _dragAxisW = axis == Axis.X ? SN.Vector3.UnitX :
                         axis == Axis.Y ? SN.Vector3.UnitY : SN.Vector3.UnitZ;
        _dragAxisW = SN.Vector3.Normalize(_dragAxisW);
        // screen-aligned plane (stable)
        var camFwd = new SN.Vector3(view.M13, view.M23, view.M33);
        var tmp = SN.Vector3.Cross(camFwd, _dragAxisW);
        var n = SN.Vector3.Cross(_dragAxisW, tmp);
        if (n.LengthSquared() < 1e-8f) n = SN.Vector3.Cross(_dragAxisW, SN.Vector3.UnitY);
        if (n.LengthSquared() < 1e-8f) n = SN.Vector3.Cross(_dragAxisW, SN.Vector3.UnitX);
        _dragPlaneN = SN.Vector3.Normalize(n);
        // anchor point snapped onto the plane
        BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hit))
            _dragAnchorW = hit;
        InvalidateVisual();
    }

    void UpdateAxisDrag(Point mouse, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz, bool axisOnly = false)
    {
        if (!_isDragging || _selected is null || _dragAxis == Axis.None) return;
        BuildPickRay(mouse, view, proj, sz, out var ro, out var rd);
        if (!RayIntersectPlane(ro, rd, _dragPlaneN, _dragAnchorW, out var hitW)) return;
        float delta = SN.Vector3.Dot(hitW - _dragAnchorW, _dragAxisW);
        if (SnapEnabled && SnapStep > 1e-6f)
            delta = MathF.Round(delta / SnapStep) * SnapStep;
        switch (Tool)
        {
            case ToolMode.Move:
                {
                    var newWorld = _dragObjStartW + _dragAxisW * delta;
                    SetPositionWorld(_selected, newWorld);
                    SceneService.NotifyChanged();
                    SelectionService.Touch();
                    break;
                }
            case ToolMode.Rotate:
                {
                    float deg = delta * 90f;
                    var start = _dragStartRotation; // captured at BeginAxisDrag
                    var r = new CoreVec3(start.X, start.Y, start.Z);
                    if (_dragAxis == Axis.X) r.X = start.X + deg;
                    else if (_dragAxis == Axis.Y) r.Y = start.Y + deg;
                    else r.Z = start.Z + deg;
                    _selected.Transform.Rotation = r;
                    SceneService.NotifyChanged();
                    SelectionService.Touch();
                    break;
                }
            case ToolMode.Scale:
                {
                    // distance moved along the picked axis in world units
                    float axisDelta = SN.Vector3.Dot(hitW - _dragAnchorW, _dragAxisW);
                    // sensitivity (tweak to taste)
                    const float scaleK = 0.8f;
                    // clamp so we never hit zero/negative scale
                    float f = MathF.Max(0.001f, 1f + axisDelta * scaleK);
                    double F = f; // promote to double for CoreVec3
                    var s = _dragStartScale; // CoreVec3 captured at BeginAxisDrag
                    if (axisOnly)
                    {
                        // Alt held: scale only along the picked axis
                        switch (_dragAxis)
                        {
                            case Axis.X: s.X = Math.Max(0.001, s.X * F); break;
                            case Axis.Y: s.Y = Math.Max(0.001, s.Y * F); break;
                            case Axis.Z: s.Z = Math.Max(0.001, s.Z * F); break;
                        }
                    }
                    else
                    {
                        // Default: uniform scale on all sides
                        s.X = Math.Max(0.001, s.X * F);
                        s.Y = Math.Max(0.001, s.Y * F);
                        s.Z = Math.Max(0.001, s.Z * F);
                    }
                    _selected!.Transform.Scale = s;
                    SceneService.NotifyChanged();
                    SelectionService.Touch();
                    break;
                }
        }
        SceneService.NotifyChanged();
        SelectionService.Touch();
        InvalidateVisual();
    }

    static void SetPositionWorld(GameObject go, SN.Vector3 pWorld)
    {
        // accumulate parent world
        SN.Matrix4x4 parentW = SN.Matrix4x4.Identity;
        for (var p = go.Parent; p != null; p = p.Parent)
            parentW = WorldFromTransform(p.Transform) * parentW;
        SN.Matrix4x4.Invert(parentW, out var inv);
        var pLocal = SN.Vector3.Transform(pWorld, inv);
        // IMPORTANT: assign back to the Transform
        go.Transform.Position = new CoreVec3(pLocal.X, pLocal.Y, pLocal.Z);
    }
    #endregion

    #region Picking helpers
    void BuildPickRay(Point pt, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz,
                      out SN.Vector3 ro, out SN.Vector3 rd)
    {
        float x = (float)(pt.X / sz.Width * 2 - 1);
        float y = (float)(1 - pt.Y / sz.Height * 2);
        var np = new SN.Vector3(x, y, 0f);
        var fp = new SN.Vector3(x, y, 1f);
        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var inv);
        var n4 = SN.Vector4.Transform(new SN.Vector4(np, 1), inv);
        var f4 = SN.Vector4.Transform(new SN.Vector4(fp, 1), inv);
        var n3 = new SN.Vector3(n4.X, n4.Y, n4.Z) / n4.W;
        var f3 = new SN.Vector3(f4.X, f4.Y, f4.Z) / f4.W;
        ro = n3;
        rd = SN.Vector3.Normalize(f3 - n3);
    }

    static bool RayIntersectPlane(SN.Vector3 ro, SN.Vector3 rd, SN.Vector3 n, SN.Vector3 p0, out SN.Vector3 hit)
    {
        const float EPS = 1e-6f;
        float denom = SN.Vector3.Dot(rd, n);
        if (MathF.Abs(denom) < EPS) { hit = default; return false; }
        float t = SN.Vector3.Dot(p0 - ro, n) / denom;
        if (t < 0) { hit = default; return false; }
        hit = ro + rd * t;
        return true;
    }
    #endregion

    #region Projection helpers & grid/axes
    (SN.Matrix4x4 View, SN.Matrix4x4 Proj) GetViewProj(Size size)
    {
        var dir = new SN.Vector3(
            MathF.Cos(_pitch) * MathF.Cos(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Sin(_yaw));
        var eye = _target - dir * _distance;
        var view = SN.Matrix4x4.CreateLookAt(eye, _target, SN.Vector3.UnitY);
        float aspect = size.Width <= 0 || size.Height <= 0 ? 1f : (float)(size.Width / size.Height);
        SN.Matrix4x4 proj = Is2D
            ? SN.Matrix4x4.CreateOrthographic(12f, 12f / aspect, 0.1f, 1000f)
            : SN.Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, aspect, 0.1f, 1000f);
        return (view, proj);
    }

    float EstimateProjectedRadiusPx(in SN.Matrix4x4 world, float radiusLocal,
                                in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        // center in world
        var centerW = SN.Vector3.Transform(SN.Vector3.Zero, world);
        // use X basis length as scale proxy (good for uniform or near-uniform scale)
        var basisX = new SN.Vector3(world.M11, world.M12, world.M13);
        float sx = basisX.Length();
        float rWorld = radiusLocal * (sx <= 1e-6f ? 1f : sx);
        var edgeW = centerW + SN.Vector3.Normalize(basisX) * rWorld;
        if (!ProjectToScreen(centerW, view, proj, sz, out var sc, out _) ||
            !ProjectToScreen(edgeW, view, proj, sz, out var se, out _))
            return 32f; // fallback
        double dx = se.X - sc.X, dy = se.Y - sc.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    bool TryProjectViewPoint(SN.Vector3 pView, in SN.Matrix4x4 proj, Size size,
                             out Point screen, out SN.Vector4 clip, out int outCode)
    {
        clip = SN.Vector4.Transform(new SN.Vector4(pView, 1), proj);
        if (clip.W < NearEps) { screen = default; outCode = 0; return false; }
        var ndc = clip / clip.W;
        screen = new Point(
            (ndc.X * 0.5f + 0.5f) * size.Width,
            (1 - (ndc.Y * 0.5f + 0.5f)) * size.Height);
        outCode = (ndc.X < -1 ? 1 : 0) | (ndc.X > 1 ? 2 : 0) |
                  (ndc.Y < -1 ? 4 : 0) | (ndc.Y > 1 ? 8 : 0) |
                  (ndc.Z < 0 ? 16 : 0) | (ndc.Z > 1 ? 32 : 0);
        return true;
    }

    bool ProjectToScreen(SN.Vector3 world, SN.Matrix4x4 view, SN.Matrix4x4 proj,
                         Size sz, out Point screen, out SN.Vector3 viewPos)
    {
        viewPos = SN.Vector3.Transform(world, view);
        return TryProjectViewPoint(viewPos, proj, sz, out screen, out _, out _);
    }

    bool ProjectToScreenVP(SN.Vector3 pW, in SN.Matrix4x4 vp, Size sz, out Point p)
    {
        var clip = SN.Vector4.Transform(new SN.Vector4(pW, 1f), vp);
        if (clip.W <= 0f) { p = default; return false; }
        float invW = 1f / clip.W;
        double x = ((clip.X * invW) * 0.5 + 0.5) * sz.Width;
        double y = (1.0 - ((clip.Y * invW) * 0.5 + 0.5)) * sz.Height;
        p = new Point(x, y);
        return true;
    }

    static bool ClipToNear(ref SN.Vector4 a, ref SN.Vector4 b)
    {
        bool ab = a.W < NearEps, bb = b.W < NearEps;
        if (ab && bb) return false;
        if (ab || bb)
        {
            var d = b - a; float t = (NearEps - a.W) / d.W; var p = a + t * d;
            if (ab) a = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
            else b = new SN.Vector4(p.X, p.Y, p.Z, NearEps);
        }
        return true;
    }

    bool TryProjectSegment(SN.Vector3 A, SN.Vector3 B, SN.Matrix4x4 vp, Size size,
                           out Point p0, out Point p1)
    {
        var a = SN.Vector4.Transform(new SN.Vector4(A, 1), vp);
        var b = SN.Vector4.Transform(new SN.Vector4(B, 1), vp);
        if (!ClipToNear(ref a, ref b)) { p0 = default; p1 = default; return false; }
        var na = a / a.W; var nb = b / b.W;
        static int OutCode(SN.Vector4 n) =>
            (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
            (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
            (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);
        if ((OutCode(na) & OutCode(nb)) != 0) { p0 = default; p1 = default; return false; }
        p0 = new Point((na.X * 0.5f + 0.5f) * size.Width,
                       (1 - (na.Y * 0.5f + 0.5f)) * size.Height);
        p1 = new Point((nb.X * 0.5f + 0.5f) * size.Width,
                       (1 - (nb.Y * 0.5f + 0.5f)) * size.Height);
        return true;
    }

    void DrawLine3D(DrawingContext ctx, SN.Matrix4x4 vp, Size size,
                    SN.Vector3 a, SN.Vector3 b, Color c, double th = 1)
    {
        if (!TryProjectSegment(a, b, vp, size, out var s0, out var s1)) return;
        ctx.DrawLine(new Pen(new SolidColorBrush(c), th), s0, s1);
    }
    #endregion

    #region Gizmo drawing & hit test
    void DrawTranslateGizmo(DrawingContext ctx, SN.Matrix4x4 view, SN.Matrix4x4 proj, Size sz)
    {
        if (_selected is null) return;
        var W = AccumulateWorld(_selected);
        var anchor = SN.Vector3.Transform(SN.Vector3.Zero, W);
        if (!ProjectToScreen(anchor, view, proj, sz, out var pAnchor, out _)) return;
        // Determine world length -> ≈pixels
        if (!ProjectToScreen(anchor + SN.Vector3.UnitX, view, proj, sz, out var pX1, out _)) return;
        double oneWorldToPixels = Math.Max(1e-4, Dist(pX1, pAnchor));
        double worldLen = GizmoScreenLen / oneWorldToPixels;
        var endX = anchor + SN.Vector3.UnitX * (float)worldLen;
        var endY = anchor + SN.Vector3.UnitY * (float)worldLen;
        var endZ = anchor + SN.Vector3.UnitZ * (float)worldLen;
        if (!ProjectToScreen(endX, view, proj, sz, out var pX, out _)) return;
        if (!ProjectToScreen(endY, view, proj, sz, out var pY, out _)) return;
        if (!ProjectToScreen(endZ, view, proj, sz, out var pZ, out _)) return;
        void DrawAxis(Point a, Point b, Color c, bool hot)
        {
            var pen = new Pen(new SolidColorBrush(c), hot ? 5 : 3);
            ctx.DrawLine(pen, a, b);
            // arrow head
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            double nx = dx / len, ny = dy / len;
            double lx = -ny, ly = nx;
            var tip = b;
            var t1 = new Point(tip.X - nx * 10 + lx * 5, tip.Y - ny * 10 + ly * 5);
            var t2 = new Point(tip.X - nx * 10 - lx * 5, tip.Y - ny * 10 - ly * 5);
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(tip, true);
                s.LineTo(t1);
                s.LineTo(t2);
                s.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(c), null, g);
        }
        DrawAxis(pAnchor, pX, Colors.Red, _gizmoHot == Axis.X);
        DrawAxis(pAnchor, pY, Colors.Lime, _gizmoHot == Axis.Y);
        DrawAxis(pAnchor, pZ, Colors.DeepSkyBlue, _gizmoHot == Axis.Z);
    }

    Axis HitTestTranslateGizmo(Point mouse, SN.Matrix4x4 view, SN.Matrix4x4 proj, Size sz)
    {
        if (_selected is null) return Axis.None;
        var W = AccumulateWorld(_selected);
        var anchor = SN.Vector3.Transform(SN.Vector3.Zero, W);
        if (!ProjectToScreen(anchor, view, proj, sz, out var pAnchor, out _)) return Axis.None;
        if (!ProjectToScreen(anchor + SN.Vector3.UnitX, view, proj, sz, out var pX1, out _)) return Axis.None;
        double oneWorldToPixels = Math.Max(1e-4, Dist(pX1, pAnchor));
        double worldLen = GizmoScreenLen / oneWorldToPixels;
        bool TryAxis(SN.Vector3 axis, out double d)
        {
            d = double.MaxValue;
            var end = anchor + axis * (float)worldLen;
            if (!ProjectToScreen(end, view, proj, sz, out var pEnd, out _)) return false;
            d = DistToSegment(mouse, pAnchor, pEnd);
            return true;
        }
        Axis bestAxis = Axis.None;
        double best = GizmoPickPixels;
        if (TryAxis(SN.Vector3.UnitX, out var dx) && dx <= best) { best = dx; bestAxis = Axis.X; }
        if (TryAxis(SN.Vector3.UnitY, out var dy) && dy <= best) { best = dy; bestAxis = Axis.Y; }
        if (TryAxis(SN.Vector3.UnitZ, out var dz) && dz <= best) { best = dz; bestAxis = Axis.Z; }
        _gizmoHot = bestAxis;
        return bestAxis;
    }

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static double DistToSegment(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double apx = p.X - a.X, apy = p.Y - a.Y;
        double denom = abx * abx + aby * aby;
        double t = denom > 1e-9 ? Math.Clamp((apx * abx + apy * aby) / denom, 0.0, 1.0) : 0.0;
        double cx = a.X + abx * t, cy = a.Y + aby * t;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
    #endregion

    #region World accumulation
    SN.Matrix4x4 AccumulateWorld(GameObject go)
    {
        var stack = new Stack<GameObject>();
        for (var n = go; n != null; n = n.Parent) stack.Push(n);
        var w = SN.Matrix4x4.Identity;
        while (stack.Count > 0) w = w * WorldFromTransform(stack.Pop().Transform);
        return w;
    }

    (SN.Vector3 min, SN.Vector3 max) ComputeSceneAABB()
    {
        bool any = false;
        SN.Vector3 min = default, max = default;
        void Acc(in SN.Vector3 p)
        {
            if (!any) { min = max = p; any = true; }
            else
            {
                min = new SN.Vector3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
                max = new SN.Vector3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
            }
        }
        foreach (var root in SceneService.Root)
        {
            var (rmin, rmax) = ComputeWorldAABB(root);
            Acc(rmin); Acc(rmax);
        }
        if (!any) { min = new SN.Vector3(-1, -1, -1); max = new SN.Vector3(1, 1, 1); }
        return (min, max);
    }
    #endregion

    #region Render pipeline
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        int W = Math.Max(1, (int)size.Width);
        int H = Math.Max(1, (int)size.Height);
        int SS = Supersample2x ? 2 : 1;
        int RW = W * SS; // render width
        int RH = H * SS; // render height

        var color = new uint[RW * RH];
        var zbuf = new float[RW * RH];

        // Sky colors
        var sky = FindBehaviors<Game_Engine.Core.Skybox>().FirstOrDefault();
        var skyTop = sky?.Top ?? Color.Parse("#1f1f1f");
        var skyBot = sky?.Bottom ?? Color.Parse("#1f1f1f");

        // pull sky texture & blend (tolerant: path/stream/bytes/Bitmap)
        Game_Engine.Core.Texture2D? skyTex = null;
        float skyBlend = 0f;

        if (sky != null)
        {
            var st = sky.GetType();

            var pTex = st.GetProperty("Texture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var raw = pTex?.GetValue(sky);
            var coerced = raw as Game_Engine.Core.Texture2D ?? EnsureEngineTexture2D(raw);
            if (coerced != null)
            {
                skyTex = coerced;
                if (!ReferenceEquals(raw, coerced) && pTex?.CanWrite == true)
                    pTex.SetValue(sky, coerced); // persist the real Texture2D back onto the Skybox
            }

            var pBlend = st.GetProperty("TextureBlend", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pBlend != null)
            {
                var bv = pBlend.GetValue(sky);
                if (bv is float f) skyBlend = Math.Clamp(f, 0f, 1f);
                else if (bv is double d) skyBlend = (float)Math.Clamp(d, 0.0, 1.0);
            }
        }


        // View/Proj at render res
        var (view, proj) = GetViewProj(new Size(RW, RH));

        // sun dir (just for sky highlight)
        var dirLight = FindBehaviors<Game_Engine.Core.Light>().FirstOrDefault(l => l.Type == LightType.Directional);
        SN.Vector3? sunDir = null;
        if (dirLight?.gameObject is { } dgo)
        {
            var Wl = AccumulateWorld(dgo);
            var z = new SN.Vector3(Wl.M13, Wl.M23, Wl.M33);
            if (z.LengthSquared() < 1e-8f) z = SN.Vector3.UnitZ;
            sunDir = -SN.Vector3.Normalize(z);
        }

        
        // Defaults if the Skybox doesn’t set them
        float skyYaw = sky?.Yaw ?? 0f;          // degrees
        float seamFeather = sky?.SeamFeather ?? 0.01f;
        bool keyOut = sky?.KeyOutNearBlack ?? true;
        float keyLuma = sky?.KeyLuma ?? 0.08f;

        // Clear sky + z  
        FillSkyWorldUp(
            color, zbuf, RW, RH, view, proj,
            skyTop, skyBot,
            sunDir,
            skyTex, skyBlend,
            skyYaw, seamFeather, keyOut, keyLuma
        );


        // Lighting defaults (frame-level)
        var light = FindBehaviors<Game_Engine.Core.Light>().FirstOrDefault();
        SN.Vector3 L = SN.Vector3.Normalize(new SN.Vector3(0.35f, 0.9f, 0.45f));
        float Ambient = Math.Clamp(sky?.Ambient ?? 0f, 0f, 1f);
        float DiffuseK = ShowLight ? 1f : 0f;

        bool lightIsPoint = false;
        SN.Vector3 lightPosW = SN.Vector3.Zero;
        float lightRange = 10f;

        if (light is not null)
        {
            float lum = (light.Color.R * 0.2126f + light.Color.G * 0.7152f + light.Color.B * 0.0722f) / 255f;
            DiffuseK *= MathF.Max(0.01f, light.Intensity * lum);
            var lw = light.gameObject is null ? SN.Matrix4x4.Identity : AccumulateWorld(light.gameObject);
            lightPosW = SN.Vector3.Transform(SN.Vector3.Zero, lw);

            if (light.Type == Game_Engine.Core.LightType.Directional && light.gameObject is { } lt)
                L = -ForwardFrom(lt.Transform);
            else if (light.Type == Game_Engine.Core.LightType.Point)
            {
                lightIsPoint = true;
                lightRange = Math.Max(0.001f, light.Range);
            }
        }

        // Small directional shadow map
        ShadowMap? shadow = null;
        if (ShowLight && light is { Type: Game_Engine.Core.LightType.Directional } && !lightIsPoint)
        {
            var (smin, smax) = ComputeSceneAABB();
            var center = (smin + smax) * 0.5f;
            var diag = (smax - smin).Length();
            float ortho = Math.Max(8f, diag * 0.6f);
            float dist = Math.Max(8f, diag * 0.75f);

            var eye = center - L * dist;
            var lightView = SN.Matrix4x4.CreateLookAt(eye, center, SN.Vector3.UnitY);
            var lightProj = SN.Matrix4x4.CreateOrthographic(ortho, ortho, 0.1f, dist + diag + 8f);

            const int SW = 256, SH = 256;
            var sdepth = new float[SW * SH];
            for (int i = 0; i < sdepth.Length; i++) sdepth[i] = 1.1f;

            foreach (var root in SceneService.Root)
                DrawNodeDepth(root, lightView, lightProj, SN.Matrix4x4.Identity, sdepth, SW, SH);

            shadow = new ShadowMap { VP = lightView * lightProj, Depth = sdepth, W = SW, H = SH, Bias = 0.0025f };
        }

        // One-time debug dump after changes
        if (_logNextRender)
        {
            _logNextRender = false;
            DumpSelectedMaterialDebug();
            if (ShowWire)
                Debug.WriteLine("[SceneView] ShowWire is enabled — solid pass is skipped.");
        }

        // Depth-tested grid
        if (ShowGrid)
            OverlayInfiniteGrid(view, proj, color, zbuf, RW, RH, step: 1f, majorEvery: 5);

        // Solid opaque pass
        if (!ShowWire)
        {
            foreach (var root in SceneService.Root)
                DrawNodeSolidZ(root, view, proj, SN.Matrix4x4.Identity,
                               color, zbuf, RW, RH,
                               L, DiffuseK, Ambient,
                               lightIsPoint, lightPosW, lightRange,
                               shadow);
            // Transparent back-to-front pass
            foreach (var root in SceneService.Root)
                DrawNodeSolidZ_QueueTransparent(root, view, proj, SN.Matrix4x4.Identity,
                                                color, zbuf, RW, RH,
                                                L, DiffuseK, Ambient,
                                                lightIsPoint, lightPosW, lightRange,
                                                shadow);
        }

        // Blit
        var wb = new WriteableBitmap(new PixelSize(W, H), new Avalonia.Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var fb = wb.Lock())
            unsafe
            {
                byte* dst = (byte*)fb.Address;
                int stride = fb.RowBytes;

                if (SS == 2)
                {
                    var lo = new uint[W * H];
                    Downsample2x(color, RW, RH, lo, W, H);
                    fixed (uint* src = lo)
                    {
                        for (int y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + y * W, dst + y * stride, stride, W * 4);
                    }
                }
                else
                {
                    fixed (uint* src = color)
                    {
                        for (int y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + y * RW, dst + y * stride, stride, W * 4);
                    }
                }
            }
        ctx.DrawImage(wb, new Rect(0, 0, W, H));

        // Wire overlay
        var vp = view * proj;
        foreach (var root in SceneService.Root)
            DrawNodeWire(ctx, vp, size, root, SN.Matrix4x4.Identity, ShowWire);

        // Gizmo
        DrawTranslateGizmo(ctx, view, proj, size);
    }




    void DumpSelectedMaterialDebug()
    {
        try
        {
            var go = _selected;
            if (go == null)
            {
                Debug.WriteLine("[SceneView] No selection.");
                return;
            }

            // All enabled filters/renderers on this GO
            var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
            var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
            int pairs = Math.Min(filters.Count, renderers.Count);

            Debug.WriteLine($"[SceneView] '{go.Name}' meshPairs={pairs} (filters={filters.Count}, renderers={renderers.Count})");
            if (pairs == 0)
            {
                Debug.WriteLine("[SceneView] No enabled MeshFilter+MeshRenderer pairs.");
                return;
            }

            // --- helpers ----------------------------------------------------------
            static System.Numerics.Vector2[]? TryGetUVs(Mesh m)
            {
                var t = m.GetType();
                string[] candidates = { "UVs", "UV", "TexCoords", "TexCoord", "UV0", "UV1" };
                foreach (var n in candidates)
                {
                    var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.PropertyType == typeof(System.Numerics.Vector2[]))
                        return (System.Numerics.Vector2[]?)p.GetValue(m);

                    var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(System.Numerics.Vector2[]))
                        return (System.Numerics.Vector2[]?)f.GetValue(m);
                }
                return null;
            }

            static string GetTexUsage(object slot)
            {
                var prop = slot.GetType().GetProperty("Usage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var v = prop?.GetValue(slot);
                return v?.ToString() ?? "Albedo";
            }

            static int GetFaceMask(object slot)
            {
                var prop = slot.GetType().GetProperty("FaceMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null) return -1;
                var v = prop.GetValue(slot);
                if (v is int i) return i;
                if (v != null && v.GetType().IsEnum) return Convert.ToInt32(v);
                return -1;
            }

            // ----------------------------------------------------------------------

            for (int i = 0; i < pairs; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];

                var mesh = mf.Mesh;
                var verts = mesh?.Vertices?.Length ?? 0;
                var tris = (mesh?.TriIndices?.Length ?? 0) / 3;
                var uvs = mesh != null ? TryGetUVs(mesh) : null;

                Debug.WriteLine($"[SceneView]  Pair[{i}]  wire={mr.Wireframe}, castShadows={mr.CastShadows}, recvShadows={mr.ReceiveShadows}, color={mr.Color}");
                Debug.WriteLine($"[SceneView]    Mesh    : verts={verts}, tris={tris}, hasUVs={(uvs != null)}, uvLen={(uvs?.Length ?? 0)}");

                // Material (via reflection to work with non-public property)
                var matProp = mr.GetType().GetProperty("Material",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var mat = matProp?.GetValue(mr) as Game_Engine.Core.Material;

                if (mat == null)
                {
                    Debug.WriteLine($"[SceneView]    Material: <null>");
                    continue;
                }

                int texCount = mat.Textures?.Count ?? 0;
                Debug.WriteLine($"[SceneView]    Material: textures={texCount}");

                if (texCount > 0 && mat.Textures != null)
                {
                    for (int ti = 0; ti < mat.Textures.Count; ti++)
                    {
                        var slot = mat.Textures[ti];
                        var tex = slot.Texture;
                        string size = tex != null ? $"{tex.Width}x{tex.Height}" : "null";
                        string usage = GetTexUsage(slot);
                        int mask = GetFaceMask(slot);
                        string maskStr = mask == -1 ? "all" : $"0x{mask:X}";
                        string name = slot.Name ?? "(unnamed)";

                        Debug.WriteLine($"[SceneView]      [{ti}] name='{name}', usage={usage}, faceMask={maskStr}, tex={size}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SceneView] Debug dump error: " + ex);
        }
    }

    void DrawNodeSolidZ(GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                    in SN.Matrix4x4 parentWorld, uint[] color, float[] zbuf, int W, int H,
                    SN.Vector3 L, float DiffuseK, float Ambient,
                    bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
                    ShadowMap? shadow)
    {
        var world = parentWorld * WorldFromTransform(go.Transform);

        // Pair filters & renderers by index (importer puts them in lockstep)
        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = Math.Min(filters.Count, renderers.Count);

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            if (mr.Wireframe) continue;
            if (mf.Mesh is null) continue;

            // OPAQUE ONLY in this pass
            if (IsRendererTransparent(mr)) continue;

            
            var mesh = EnsureProceduralLod(mf, world, view, proj, new Size(W, H));

            // Pull material from this renderer (public or non-public)
            var matProp = mr.GetType().GetProperty("Material",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var mat = matProp?.GetValue(mr) as Game_Engine.Core.Material;

            RasterizeMeshSolidZ(mesh, world, view, proj, color, zbuf, W, H,
                mr.Color, mat, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadow, mr.ReceiveShadows, mr.DoubleSided, mr.InvertFrontFace,
                transparentPass: false);
        }

        foreach (var child in go.Children)
            DrawNodeSolidZ(child, view, proj, world, color, zbuf, W, H,
                           L, DiffuseK, Ambient, lightIsPoint, lightPosW, lightRange, shadow);
    }

    // decide if this renderer should be drawn in the transparent pass
    static bool IsRendererTransparent(MeshRenderer mr)
    {
        // Renderer tint alpha
        if (mr.Color.A < 255) return true;

        // Grab the material (public or non-public)
        var matProp = mr.GetType().GetProperty("Material",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var mat = matProp?.GetValue(mr);
        if (mat == null) return false;

        var mt = mat.GetType();

        // Material flags/knobs
        if (TryGetBool(mt, mat, "Transparent", out var isTrans) && isTrans)
            return true;

        if (TryGetDouble(mt, mat, "Opacity", out var opacity) && opacity < 0.999)
            return true;

        if (TryGetString(mt, mat, "Blend", out var blend) && BlendImpliesTransparency(blend))
            return true;

        if (TryGetString(mt, mat, "BlendMode", out var blendMode) && BlendImpliesTransparency(blendMode))
            return true;

        //Texture usages
        var texListProp = mt.GetProperty("Textures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (texListProp?.GetValue(mat) is System.Collections.IEnumerable slots)
        {
            foreach (var slot in slots)
            {
                // Usage (string or enum)
                string usage = GetUsage(slot);

                // Explicit transparency usages
                if (usage == "opacity" || usage == "transparent" ||
                    usage.Contains("alpha") || usage.Contains("transp"))
                    return true;

                // If albedo-like and actually has alpha, treat as transparent
                if (usage.Contains("albedo") || usage.Contains("basecolor") ||
                    usage.Contains("base") || usage.Contains("diff"))
                {
                    var texObj = GetTextureObject(slot); // tolerant fetch
                    if (texObj != null && TextureHasAnyAlpha(texObj))
                        return true;
                }
            }
        }

        return false;

        // ---------- local helpers -----------------------------------------------

        static bool TryGetBool(Type t, object o, string name, out bool v)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool))
            {
                v = (bool)p.GetValue(o)!;
                return true;
            }
            v = false; return false;
        }

        static bool TryGetDouble(Type t, object o, string name, out double v)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                var raw = p.GetValue(o);
                v = raw is float f ? f : raw is double d ? d : 1.0;
                return true;
            }
            v = 1.0; return false;
        }

        static bool TryGetString(Type t, object o, string name, out string s)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                s = p.GetValue(o)?.ToString() ?? "";
                return true;
            }
            s = ""; return false;
        }

        static bool BlendImpliesTransparency(string s)
        {
            s = (s ?? "").ToLowerInvariant();
            return s.Contains("alpha") || s.Contains("transp") ||
                   s.Contains("add") || s.Contains("screen");
        }

        static string GetUsage(object slot)
        {
            var up = slot.GetType().GetProperty("Usage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var u = up?.GetValue(slot);
            return (u?.ToString() ?? "albedo").ToLowerInvariant();
        }

        static object? GetTextureObject(object slot)
        {
            var p = slot.GetType().GetProperty("Texture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var raw = p?.GetValue(slot);
            //  non-engine objects to a real Texture2D if needed & write back
            var tex = raw as Game_Engine.Core.Texture2D ?? EnsureEngineTexture2D(raw);
            if (tex != null && tex != raw && p is { CanWrite: true }) p!.SetValue(slot, tex);
            return tex;
        }

        static bool TextureHasAnyAlpha(object texLike)
        {
            // Make sure we have a real engine texture
            var tex = texLike as Game_Engine.Core.Texture2D ?? EnsureEngineTexture2D(texLike);
            if (tex is null) return false;

            // Fast probe
            var rgba = tex.Rgba;
            if (rgba == null || rgba.Length < 4) return false;
            int pixels = rgba.Length / 4;
            if (pixels <= 0) return false;

            int step = Math.Max(1, pixels / 1024);
            for (int i = 0; i < pixels; i += step)
            {
                int a = rgba[i * 4 + 3];
                if (a < 250) return true; // allow minor noise
            }
            return false;
        }
    }







    void DrawNodeSolidZ_QueueTransparent(GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                                     in SN.Matrix4x4 parentWorld, uint[] color, float[] zbuf, int W, int H,
                                     SN.Vector3 L, float DiffuseK, float Ambient,
                                     bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
                                     ShadowMap? shadow)
    {
        // Copy 'in' params so the local function can capture them safely
        SN.Matrix4x4 v = view;
        SN.Matrix4x4 p = proj;

        var items = new List<(float ndcZ, SN.Matrix4x4 world, MeshFilter mf, MeshRenderer mr, Game_Engine.Core.Material? mat)>();

        void Gather(GameObject node, in SN.Matrix4x4 parentW)
        {
            var world = parentW * WorldFromTransform(node.Transform);

            var filters = node.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
            var renderers = node.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
            int n = Math.Min(filters.Count, renderers.Count);

            for (int i = 0; i < n; i++)
            {
                var mf = filters[i];
                var mr = renderers[i];
                if (mr.Wireframe || mf.Mesh is null) continue;
                if (!IsRendererTransparent(mr)) continue;

                
                var _ = EnsureProceduralLod(mf, world, v, p, new Size(W, H));

                // Depth key (use object origin), computed with local copies v/p
                var clip = SN.Vector4.Transform(new SN.Vector4(SN.Vector3.Transform(SN.Vector3.Zero, world), 1f), v * p);
                if (clip.W <= 0f) continue;
                float ndcZ = clip.Z / clip.W;

                var matProp = mr.GetType().GetProperty("Material",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var mat = matProp?.GetValue(mr) as Game_Engine.Core.Material;

                items.Add((ndcZ, world, mf, mr, mat));
            }

            foreach (var c in node.Children)
                Gather(c, world);
        }

        Gather(go, parentWorld);

        // Back-to-front
        items.Sort((a, b) => b.ndcZ.CompareTo(a.ndcZ));

        foreach (var it in items)
        {
            var mesh = it.mf.Mesh!;
            RasterizeMeshSolidZ(mesh, it.world, v, p,
                color, zbuf, W, H,
                it.mr.Color, it.mat, L, DiffuseK, Ambient,
                lightIsPoint, lightPosW, lightRange,
                shadow, it.mr.ReceiveShadows, it.mr.DoubleSided, it.mr.InvertFrontFace,
                transparentPass: true);
        }
    }




    Mesh EnsureProceduralLod(MeshFilter mf,
                         in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj, Size sz)
    {
        var mesh = mf.Mesh!;
        switch (mesh.Kind)
        {
            case MeshKind.Sphere:
                {
                    float rLocal = ApproxLocalRadius(mesh);
                    float rPx = EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    var (needLon, needLat) = Mesh.SuggestSphereTesselation(rPx);
                    if (needLon > mesh.TessA || needLat > mesh.TessB)
                    {
                        var upgraded = Mesh.CreateUvSphere(needLon, needLat, rLocal);
                        mf.Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }

            case MeshKind.Cylinder:
                {
                    var (rLocal, hLocal) = ApproxRadialAndHeight(mesh);
                    float rPx = EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    int needSides = Mesh.SuggestRadialTessellation(rPx);
                    if (needSides > mesh.TessA)
                    {
                        var upgraded = Mesh.CreateCylinder(needSides, rLocal, hLocal, caps: true);
                        mf.Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }

            case MeshKind.Cone:
                {
                    var (rLocal, hLocal) = ApproxRadialAndHeight(mesh);
                    float rPx = EstimateProjectedRadiusPx(world, rLocal, view, proj, sz);
                    int needSides = Mesh.SuggestRadialTessellation(rPx);
                    if (needSides > mesh.TessA)
                    {
                        var upgraded = Mesh.CreateCone(needSides, rLocal, hLocal, cap: true);
                        mf.Mesh = upgraded;
                        return upgraded;
                    }
                    break;
                }
        }
        return mesh;
    }


    void DrawNodeWire(DrawingContext ctx, in SN.Matrix4x4 vp, Size sz,
                  GameObject go, in SN.Matrix4x4 parentWorld, bool globalWire)
    {
        var world = parentWorld * WorldFromTransform(go.Transform);

        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = Math.Min(filters.Count, renderers.Count);

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            if (mf.Mesh != null && (globalWire || mr.Wireframe))
                DrawMeshWire(ctx, mf.Mesh, world, vp, sz, mr.Color, (float)mr.LineWidth);
        }

        foreach (var child in go.Children)
            DrawNodeWire(ctx, vp, sz, child, world, globalWire);
    }


    void DrawMeshWire(DrawingContext ctx, Mesh mesh, in SN.Matrix4x4 world,
                      in SN.Matrix4x4 vp, Size sz, Color color, float lineWidth)
    {
        if (mesh?.Vertices == null || mesh.TriIndices == null) return;
        var pen = new Pen(new SolidColorBrush(color), lineWidth <= 0 ? 1 : lineWidth);
        var v = mesh.Vertices;
        var tri = mesh.TriIndices;
        for (int i = 0; i < tri.Length; i += 3)
        {
            var p0w = SN.Vector3.Transform(v[tri[i]], world);
            var p1w = SN.Vector3.Transform(v[tri[i + 1]], world);
            var p2w = SN.Vector3.Transform(v[tri[i + 2]], world);
            if (!ProjectToScreenVP(p0w, vp, sz, out var s0)) continue;
            if (!ProjectToScreenVP(p1w, vp, sz, out var s1)) continue;
            if (!ProjectToScreenVP(p2w, vp, sz, out var s2)) continue;
            ctx.DrawLine(pen, s0, s1);
            ctx.DrawLine(pen, s1, s2);
            ctx.DrawLine(pen, s2, s0);
        }
    }
    #endregion

    #region Software rasterizer (solid pass)
    private struct ClipVertex
    {
        public SN.Vector4 ClipPos;
        public SN.Vector3 ViewPos;
        public SN.Vector3 WorldPos;
        public SN.Vector3 ViewNormal;
        public SN.Vector2 UV;
    }

    // Cache for reflection lookups so we don't pay repeatedly
    private static readonly Dictionary<Type, System.Numerics.Vector2[]?> _uvCache
        = new Dictionary<Type, System.Numerics.Vector2[]?>();

    private static System.Numerics.Vector2[]? GetMeshUVs(Mesh m)
    {
        var t = m.GetType();
        if (_uvCache.TryGetValue(t, out var cached))
            return cached;

        // Common property/field names for UVs
        string[] names = { "UVs", "UV", "TexCoords", "TexCoord", "UV0", "UV1" };

        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(System.Numerics.Vector2[]))
            {
                var v = (System.Numerics.Vector2[]?)p.GetValue(m);
                _uvCache[t] = v;
                return v;
            }
            var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(System.Numerics.Vector2[]))
            {
                var v = (System.Numerics.Vector2[]?)f.GetValue(m);
                _uvCache[t] = v;
                return v;
            }
        }

        _uvCache[t] = null;
        return null;
    }


    private static ClipVertex InterpVertex(in ClipVertex a, in ClipVertex b, float t)
    {
        return new ClipVertex
        {
            ClipPos = a.ClipPos + t * (b.ClipPos - a.ClipPos),
            ViewPos = a.ViewPos + t * (b.ViewPos - a.ViewPos),
            WorldPos = a.WorldPos + t * (b.WorldPos - a.WorldPos),
            ViewNormal = a.ViewNormal + t * (b.ViewNormal - a.ViewNormal),
            UV = a.UV + t * (b.UV - a.UV)
        };
    }

    private static List<ClipVertex> ClipAgainstPlane(List<ClipVertex> polygon, SN.Vector4 plane, float planeD)
    {
        var result = new List<ClipVertex>(polygon.Count + 1);
        int count = polygon.Count;

        for (int i = 0; i < count; i++)
        {
            ClipVertex curr = polygon[i];
            ClipVertex prev = polygon[(i + count - 1) % count];

            float currDist = SN.Vector4.Dot(curr.ClipPos, plane) + planeD;
            float prevDist = SN.Vector4.Dot(prev.ClipPos, plane) + planeD;

            bool currIn = currDist >= 0f;
            bool prevIn = prevDist >= 0f;

            if (prevIn != currIn)
            {
                float interpT = prevDist / (prevDist - currDist);
                result.Add(InterpVertex(prev, curr, interpT));
            }

            if (currIn)
            {
                result.Add(curr);
            }
        }

        return result;
    }

    private static List<ClipVertex> ClipTriangle(ClipVertex v0, ClipVertex v1, ClipVertex v2, float near)
    {
        var input = new List<ClipVertex> { v0, v1, v2 };
        return ClipAgainstPlane(input, new SN.Vector4(0f, 0f, 0f, 1f), -near);
    }

    private static float Edge(SN.Vector2 a, SN.Vector2 b, SN.Vector2 c)
    {
        return (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);
    }


    private static Color SampleTexture(Texture2D tex, float u, float v)
    {
        if (tex.Width <= 0 || tex.Height <= 0)
            return Color.FromArgb(255, 255, 255, 255);

        // Flip V (top-left origin) and clamp to texel centers
        v = 1f - v;

        float maxX = tex.Width - 1;
        float maxY = tex.Height - 1;

        // Keep samples inside [0.5/max, 1-0.5/max] to avoid wrapping into transparent borders
        float epsU = (tex.Width > 1) ? (0.5f / maxX) : 0f;
        float epsV = (tex.Height > 1) ? (0.5f / maxY) : 0f;

        u = Math.Clamp(u, epsU, 1f - epsU);
        v = Math.Clamp(v, epsV, 1f - epsV);

        float px = u * maxX;
        float py = v * maxY;

        int x0 = (int)MathF.Floor(px);
        int y0 = (int)MathF.Floor(py);
        int x1 = Math.Min(x0 + 1, tex.Width - 1);
        int y1 = Math.Min(y0 + 1, tex.Height - 1);

        float tx = px - x0;
        float ty = py - y0;

        // Read pixels
        int i00 = (y0 * tex.Width + x0) * 4;
        int i01 = (y0 * tex.Width + x1) * 4;
        int i10 = (y1 * tex.Width + x0) * 4;
        int i11 = (y1 * tex.Width + x1) * 4;

        // Convert to premultiplied floats [0..1]
        static void Premul(byte[] d, int i, out float r, out float g, out float b, out float a)
        {
            a = d[i + 3] / 255f;
            float R = d[i + 0] / 255f;
            float G = d[i + 1] / 255f;
            float B = d[i + 2] / 255f;
            r = R * a; g = G * a; b = B * a;
        }

        Premul(tex.Rgba, i00, out var r00, out var g00, out var b00, out var a00);
        Premul(tex.Rgba, i01, out var r01, out var g01, out var b01, out var a01);
        Premul(tex.Rgba, i10, out var r10, out var g10, out var b10, out var a10);
        Premul(tex.Rgba, i11, out var r11, out var g11, out var b11, out var a11);

        // Bilinear in premultiplied space
        float r0 = r00 * (1 - tx) + r01 * tx;
        float g0 = g00 * (1 - tx) + g01 * tx;
        float b0 = b00 * (1 - tx) + b01 * tx;
        float a0 = a00 * (1 - tx) + a01 * tx;

        float r1 = r10 * (1 - tx) + r11 * tx;
        float g1 = g10 * (1 - tx) + g11 * tx;
        float b1 = b10 * (1 - tx) + b11 * tx;
        float a1 = a10 * (1 - tx) + a11 * tx;

        float r = r0 * (1 - ty) + r1 * ty;
        float g = g0 * (1 - ty) + g1 * ty;
        float b = b0 * (1 - ty) + b1 * ty;
        float a = a0 * (1 - ty) + a1 * ty;

        // Unpremultiply (safe)
        if (a > 1e-6f) { r /= a; g /= a; b /= a; } else { r = g = b = 0f; }

        return Color.FromArgb(
            (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(r * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(g * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(b * 255f + 0.5f), 0, 255));
    }


    // Unpack BGRA uint -> Color
    static Color UnpackBGRA(uint p)
    {
        byte b = (byte)(p & 0xFF);
        byte g = (byte)((p >> 8) & 0xFF);
        byte r = (byte)((p >> 16) & 0xFF);
        byte a = (byte)((p >> 24) & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }

    // Classic "over" on top of an existing BGRA pixel (dst has A=255 in our buffer)
    static uint BlendOver(uint dstBGRA, Color src, float a /*0..1*/)
    {
        if (a <= 0f) return dstBGRA;
        if (a >= 1f) return PackBGRA(src);

        var dst = UnpackBGRA(dstBGRA);
        byte r = (byte)(src.R * a + dst.R * (1f - a));
        byte g = (byte)(src.G * a + dst.G * (1f - a));
        byte b = (byte)(src.B * a + dst.B * (1f - a));
        return PackBGRA(Color.FromRgb(r, g, b)); // keep A=255 in our buffer
    }





    void RasterizeMeshSolidZ(
    Mesh m,
    in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
    uint[] color, float[] zbuf, int W, int H,
    Color tint, Material? mat,
    SN.Vector3 L, float DiffuseK, float Ambient,
    bool lightIsPoint, SN.Vector3 lightPosW, float lightRange,
    ShadowMap? shadow, bool receiveShadows, bool doubleSided,
    bool invertFrontFace,
    bool transparentPass)
    {
        // --- tiny helpers ---------------------------------------------------------
        static Color AddColor(Color a, Color b) => Color.FromRgb(
            (byte)Math.Min(255, a.R + b.R),
            (byte)Math.Min(255, a.G + b.G),
            (byte)Math.Min(255, a.B + b.B));
        static float Luma(Color c) => (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;
        static Color AlphaOver(Color under, Color over)
        {
            float a = over.A / 255f;
            return Color.FromRgb(
                (byte)(over.R * a + under.R * (1f - a)),
                (byte)(over.G * a + under.G * (1f - a)),
                (byte)(over.B * a + under.B * (1f - a)));
        }
        static int MajorAxisMaskFromNormal(SN.Vector3 n)
        {
            if (n.LengthSquared() < 1e-12f) return -1;
            n = SN.Vector3.Normalize(n);
            var a = new SN.Vector3(MathF.Abs(n.X), MathF.Abs(n.Y), MathF.Abs(n.Z));
            if (a.X >= a.Y && a.X >= a.Z) return n.X >= 0 ? 1 : 2;  // +X / -X
            if (a.Y >= a.X && a.Y >= a.Z) return n.Y >= 0 ? 4 : 8;  // +Y / -Y
            return n.Z >= 0 ? 16 : 32;                               // +Z / -Z
        }


        // usage/face helpers (reflective)
        static string GetUsageName(MaterialTexture slot)
            => slot.GetType().GetProperty("Usage")?.GetValue(slot)?.ToString() ?? "Albedo";

        // infer mask from name when FaceMask isn’t set
        static int InferMaskFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string s = name.ToLowerInvariant();
            int m = 0;
            if (s.Contains("right") || s.Contains("+x") || s.Contains("px")) m |= 1;
            if (s.Contains("left") || s.Contains("-x") || s.Contains("nx")) m |= 2;
            if (s.Contains("top") || s.Contains("up") || s.Contains("+y") || s.Contains("py")) m |= 4;
            if (s.Contains("bottom") || s.Contains("down") || s.Contains("-y") || s.Contains("ny")) m |= 8;
            // Our renderer uses forward = -Z; call that "front"
            if (s.Contains("back") || s.Contains("+z") || s.Contains("pz")) m |= 16;
            if (s.Contains("front") || s.Contains("-z") || s.Contains("nz")) m |= 32;
            return m == 0 ? -1 : m;
        }
        static int GetFaceMask(object slot)
        {
            var prop = slot.GetType().GetProperty("FaceMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null) return -1;
            var v = prop.GetValue(slot);
            if (v is int i) return i;
            if (v != null && v.GetType().IsEnum) return Convert.ToInt32(v);
            return -1;
        }

        static int FaceMaskFromTriAndAabb(
            in SN.Vector3 pa, in SN.Vector3 pb, in SN.Vector3 pc,
            in SN.Vector3 bbMin, in SN.Vector3 bbMax)
        {
            //  Which axis is this triangle mostly facing?
            var n = SN.Vector3.Cross(pb - pa, pc - pa);
            var an = new SN.Vector3(MathF.Abs(n.X), MathF.Abs(n.Y), MathF.Abs(n.Z));

            //  Use the triangle centroid to decide sign (top vs bottom, etc)
            float cx = (pa.X + pb.X + pc.X) / 3f;
            float cy = (pa.Y + pb.Y + pc.Y) / 3f;
            float cz = (pa.Z + pb.Z + pc.Z) / 3f;
            float mx = (bbMin.X + bbMax.X) * 0.5f;
            float my = (bbMin.Y + bbMax.Y) * 0.5f;
            float mz = (bbMin.Z + bbMax.Z) * 0.5f;

            if (an.X >= an.Y && an.X >= an.Z) return (cx >= mx) ? 1 : 2;  // +X / -X
            if (an.Y >= an.X && an.Y >= an.Z) return (cy >= my) ? 4 : 8;  // +Y / -Y
            /*Z*/
            return (cz >= mz) ? 16 : 32; // +Z / -Z
        }



        // per-slot UV xform
        static float GetF(object o, string name, float def)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return def;
            var v = p.GetValue(o);
            return v is float f ? f : v is double d ? (float)d : def;
        }
        static bool GetB(object o, string name, bool def)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return def;
            var v = p.GetValue(o);
            return v is bool b ? b : def;
        }
        static void ApplyUVXform(object slot, ref float u, ref float v)
        {
            float su = GetF(slot, "ScaleU", 1f), sv = GetF(slot, "ScaleV", 1f);
            float ou = GetF(slot, "OffsetU", 0f), ov = GetF(slot, "OffsetV", 0f);
            float rot = GetF(slot, "RotateUV", 0f) * (MathF.PI / 180f);
            float uu = (u - 0.5f) * su, vv = (v - 0.5f) * sv;
            if (MathF.Abs(rot) > 1e-6f)
            {
                float cs = MathF.Cos(rot), sn = MathF.Sin(rot);
                (uu, vv) = (uu * cs - vv * sn, uu * sn + vv * cs);
            }
            u = uu + 0.5f + ou; v = vv + 0.5f + ov;
        }

        // texture sampler (premul & clamped)
        static Color SamplePMClamped(Texture2D tex, float u, float v)
        {
            if (tex.Width <= 0 || tex.Height <= 0) return Color.FromArgb(255, 255, 255, 255);
            v = 1f - v; // flip V
            float maxX = tex.Width - 1, maxY = tex.Height - 1;
            float epsU = tex.Width > 1 ? (0.5f / maxX) : 0f, epsV = tex.Height > 1 ? (0.5f / maxY) : 0f;
            u = Math.Clamp(u, epsU, 1f - epsU); v = Math.Clamp(v, epsV, 1f - epsV);
            float px = u * maxX, py = v * maxY;
            int x0 = (int)MathF.Floor(px), y0 = (int)MathF.Floor(py);
            int x1 = Math.Min(x0 + 1, tex.Width - 1), y1 = Math.Min(y0 + 1, tex.Height - 1);
            float tx = px - x0, ty = py - y0;
            static void Premul(byte[] d, int i, out float r, out float g, out float b, out float a)
            { a = d[i + 3] / 255f; float R = d[i + 0] / 255f, G = d[i + 1] / 255f, B = d[i + 2] / 255f; r = R * a; g = G * a; b = B * a; }
            int i00 = (y0 * tex.Width + x0) * 4, i01 = (y0 * tex.Width + x1) * 4, i10 = (y1 * tex.Width + x0) * 4, i11 = (y1 * tex.Width + x1) * 4;
            Premul(tex.Rgba, i00, out var r00, out var g00, out var b00, out var a00);
            Premul(tex.Rgba, i01, out var r01, out var g01, out var b01, out var a01);
            Premul(tex.Rgba, i10, out var r10, out var g10, out var b10, out var a10);
            Premul(tex.Rgba, i11, out var r11, out var g11, out var b11, out var a11);
            float r0 = r00 * (1 - tx) + r01 * tx, g0 = g00 * (1 - tx) + g01 * tx, b0 = b00 * (1 - tx) + b01 * tx, a0 = a00 * (1 - tx) + a01 * tx;
            float r1 = r10 * (1 - tx) + r11 * tx, g1 = g10 * (1 - tx) + g11 * tx, b1 = b10 * (1 - tx) + b11 * tx, a1 = a10 * (1 - tx) + a11 * tx;
            float r = r0 * (1 - ty) + r1 * ty, g = g0 * (1 - ty) + g1 * ty, b = b0 * (1 - ty) + b1 * ty, a = a0 * (1 - ty) + a1 * ty;
            if (a > 1e-6f) { r /= a; g /= a; b /= a; } else { r = g = b = 0f; }
            return Color.FromArgb(
                (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255),
                (byte)Math.Clamp((int)(r * 255f + 0.5f), 0, 255),
                (byte)Math.Clamp((int)(g * 255f + 0.5f), 0, 255),
                (byte)Math.Clamp((int)(b * 255f + 0.5f), 0, 255));
        }
        // -------------------------------------------------------------------------

        var Vtx = m.Vertices;
        var Idx = m.TriIndices;
        var Nor = m.Normals;

        // Get UVs via reflection-cache helper if available
        var UVMesh = GetMeshUVs(m);

        // Object-space AABB (for planar fallback UVs)
        SN.Vector3 bbMin = new(float.MaxValue), bbMax = new(float.MinValue);
        for (int v = 0; v < Vtx.Length; v++)
        {
            var p = Vtx[v];
            bbMin = new SN.Vector3(MathF.Min(bbMin.X, p.X), MathF.Min(bbMin.Y, p.Y), MathF.Min(bbMin.Z, p.Z));
            bbMax = new SN.Vector3(MathF.Max(bbMax.X, p.X), MathF.Max(bbMax.Y, p.Y), MathF.Max(bbMax.Z, p.Z));
        }
        var bbSize = bbMax - bbMin;
        bbSize = new SN.Vector3(bbSize.X == 0 ? 1f : bbSize.X,
                                bbSize.Y == 0 ? 1f : bbSize.Y,
                                bbSize.Z == 0 ? 1f : bbSize.Z);

        bool hasAnyTexture = mat?.Textures?.Any(t => t?.Texture != null) == true;

        SN.Matrix4x4 mv = world * view;
        SN.Matrix4x4 mvp = mv * proj;

        const float near = 0.1f;
        SN.Matrix4x4.Invert(mv, out var invMv);
        SN.Matrix4x4 normalMatrix = SN.Matrix4x4.Transpose(invMv);

        SN.Vector3 lightDirV = lightIsPoint ? SN.Vector3.Zero : SN.Vector3.Normalize(SN.Vector3.TransformNormal(L, view));
        SN.Vector3 lightPosV = lightIsPoint ? SN.Vector3.Transform(lightPosW, view) : SN.Vector3.Zero;

        const float INSIDE_EPS = 1e-3f;

        float matOpacity = 1f;
        if (mat != null)
        {
            var p = mat.GetType().GetProperty("Opacity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                var v = p.GetValue(mat);
                if (v is float f) matOpacity = Math.Clamp(f, 0f, 1f);
                else if (v is double d) matOpacity = (float)Math.Clamp(d, 0.0, 1.0);
            }
        }
        float tintA = tint.A / 255f;

        for (int i = 0; i < Idx.Length; i += 3)
        {
            int ia = Idx[i], ib = Idx[i + 1], ic = Idx[i + 2];

            // OBJECT-space positions for this tri
            var Pa = Vtx[ia]; var Pb = Vtx[ib]; var Pc = Vtx[ic];

            // Choose UVs (mesh or planar fallback)
            SN.Vector2 Ua, Ub, Uc;
            if (UVMesh != null && UVMesh.Length == Vtx.Length)
            {
                Ua = UVMesh[ia]; Ub = UVMesh[ib]; Uc = UVMesh[ic];
            }
            else if (hasAnyTexture)
            {
                var nObj = SN.Vector3.Normalize(SN.Vector3.Cross(Pb - Pa, Pc - Pa));
                var a = new SN.Vector3(MathF.Abs(nObj.X), MathF.Abs(nObj.Y), MathF.Abs(nObj.Z));
                if (a.X >= a.Y && a.X >= a.Z) // project to YZ
                {
                    Ua = new((Pa.Z - bbMin.Z) / bbSize.Z, (Pa.Y - bbMin.Y) / bbSize.Y);
                    Ub = new((Pb.Z - bbMin.Z) / bbSize.Z, (Pb.Y - bbMin.Y) / bbSize.Y);
                    Uc = new((Pc.Z - bbMin.Z) / bbSize.Z, (Pc.Y - bbMin.Y) / bbSize.Y);
                }
                else if (a.Y >= a.X && a.Y >= a.Z) // project to XZ
                {
                    Ua = new((Pa.X - bbMin.X) / bbSize.X, (Pa.Z - bbMin.Z) / bbSize.Z);
                    Ub = new((Pb.X - bbMin.X) / bbSize.X, (Pb.Z - bbMin.Z) / bbSize.Z);
                    Uc = new((Pc.X - bbMin.X) / bbSize.X, (Pc.Z - bbMin.Z) / bbSize.Z);
                }
                else // XY
                {
                    Ua = new((Pa.X - bbMin.X) / bbSize.X, (Pa.Y - bbMin.Y) / bbSize.Y);
                    Ub = new((Pb.X - bbMin.X) / bbSize.X, (Pb.Y - bbMin.Y) / bbSize.Y);
                    Uc = new((Pc.X - bbMin.X) / bbSize.X, (Pc.Y - bbMin.Y) / bbSize.Y);
                }
            }
            else
            {
                Ua = Ub = Uc = new SN.Vector2(0.5f, 0.5f);
            }

            // Transform to clip/view/world
            var A = SN.Vector4.Transform(new SN.Vector4(Pa, 1f), mvp);
            var B = SN.Vector4.Transform(new SN.Vector4(Pb, 1f), mvp);
            var C = SN.Vector4.Transform(new SN.Vector4(Pc, 1f), mvp);

            var Va = SN.Vector3.Transform(Pa, mv);
            var Vb = SN.Vector3.Transform(Pb, mv);
            var Vc = SN.Vector3.Transform(Pc, mv);

            var Wa = SN.Vector3.Transform(Pa, world);
            var Wb = SN.Vector3.Transform(Pb, world);
            var Wc = SN.Vector3.Transform(Pc, world);

            var Na = Nor != null ? SN.Vector3.TransformNormal(Nor[ia], normalMatrix) : SN.Vector3.UnitY;
            var Nb = Nor != null ? SN.Vector3.TransformNormal(Nor[ib], normalMatrix) : SN.Vector3.UnitY;
            var Nc = Nor != null ? SN.Vector3.TransformNormal(Nor[ic], normalMatrix) : SN.Vector3.UnitY;

            var cv0 = new ClipVertex { ClipPos = A, ViewPos = Va, WorldPos = Wa, ViewNormal = Na, UV = Ua };
            var cv1 = new ClipVertex { ClipPos = B, ViewPos = Vb, WorldPos = Wb, ViewNormal = Nb, UV = Ub };
            var cv2 = new ClipVertex { ClipPos = C, ViewPos = Vc, WorldPos = Wc, ViewNormal = Nc, UV = Uc };

            var clipped = ClipTriangle(cv0, cv1, cv2, near);
            if (clipped.Count < 3) continue;

            for (int kt = 0; kt < clipped.Count - 2; kt++)
            {
                cv0 = clipped[0]; cv1 = clipped[kt + 1]; cv2 = clipped[kt + 2];
                A = cv0.ClipPos; Va = cv0.ViewPos; Wa = cv0.WorldPos; Na = cv0.ViewNormal; Ua = cv0.UV;
                B = cv1.ClipPos; Vb = cv1.ViewPos; Wb = cv1.WorldPos; Nb = cv1.ViewNormal; Ub = cv1.UV;
                C = cv2.ClipPos; Vc = cv2.ViewPos; Wc = cv2.WorldPos; Nc = cv2.ViewNormal; Uc = cv2.UV;

                var nView = SN.Vector3.Cross(Vb - Va, Vc - Va);
                float facing = nView.Z * (world.GetDeterminant() >= 0 ? 1f : -1f);
                if (invertFrontFace) facing = -facing;
                bool backfacing = (facing >= 0f);
                if (!doubleSided && backfacing) continue;

                float aInvW = 1f / A.W; float Axs = (A.X * aInvW + 1f) * 0.5f * W; float Ays = (1f - A.Y * aInvW) * 0.5f * H; float aZw = A.Z * aInvW;
                float bInvW = 1f / B.W; float Bxs = (B.X * bInvW + 1f) * 0.5f * W; float Bys = (1f - B.Y * bInvW) * 0.5f * H; float bZw = B.Z * bInvW;
                float cInvW = 1f / C.W; float Cxs = (C.X * cInvW + 1f) * 0.5f * W; float Cys = (1f - C.Y * cInvW) * 0.5f * H; float cZw = C.Z * cInvW;

                var As = new SN.Vector2(Axs, Ays);
                var Bs = new SN.Vector2(Bxs, Bys);
                var Cs = new SN.Vector2(Cxs, Cys);

                float area = Edge(As, Bs, Cs);
                if (MathF.Abs(area) < 1e-6f) continue;
                float invArea = 1f / area;

                int minX = (int)MathF.Max(0, MathF.Min(Axs, MathF.Min(Bxs, Cxs)));
                int maxX = (int)MathF.Min(W - 1, MathF.Ceiling(MathF.Max(Axs, MathF.Max(Bxs, Cxs))));
                int minY = (int)MathF.Max(0, MathF.Min(Ays, MathF.Min(Bys, Cys)));
                int maxY = (int)MathF.Min(H - 1, MathF.Ceiling(MathF.Max(Ays, MathF.Max(Bys, Cys))));

                // face id in OBJECT space, not world 
                int triFaceMask = FaceMaskFromTriAndAabb(Pa, Pb, Pc, bbMin, bbMax);

                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        var p = new SN.Vector2(x + 0.5f, y + 0.5f);

                        float w0 = Edge(p, Bs, Cs);
                        float w1 = Edge(p, Cs, As);
                        float w2 = Edge(p, As, Bs);

                        if (area > 0f) { if (w0 < -INSIDE_EPS || w1 < -INSIDE_EPS || w2 < -INSIDE_EPS) continue; }
                        else { if (w0 > INSIDE_EPS || w1 > INSIDE_EPS || w2 > INSIDE_EPS) continue; }

                        w0 *= invArea; w1 *= invArea; w2 *= invArea;

                        float invW = w0 * aInvW + w1 * bInvW + w2 * cInvW;
                        if (invW <= 0) continue;

                        float z = w0 * aZw + w1 * bZw + w2 * cZw;
                        int idx = y * W + x;

                        float zTest = transparentPass ? (z - 1e-5f) : z;
                        if (zTest >= zbuf[idx]) continue;
                        if (!transparentPass) zbuf[idx] = z;

                        var viewPos = (w0 * Va * aInvW + w1 * Vb * bInvW + w2 * Vc * cInvW) / invW;
                        var worldPos = (w0 * Wa * aInvW + w1 * Wb * bInvW + w2 * Wc * cInvW) / invW;

                        var normal = SN.Vector3.Normalize((w0 * Na * aInvW + w1 * Nb * bInvW + w2 * Nc * cInvW) / invW);
                        if (backfacing) normal = -normal;

                        // Lighting
                        float ndl, atten = 1f;
                        if (lightIsPoint)
                        {
                            var toLight = lightPosV - viewPos;
                            float dist = toLight.Length();
                            ndl = Math.Max(0f, SN.Vector3.Dot(normal, toLight / (dist + 1e-6f)));
                            if (lightRange > 0f) atten = 1f / (1f + (dist / lightRange) * (dist / lightRange));
                        }
                        else ndl = Math.Max(0f, SN.Vector3.Dot(normal, lightDirV));
                        float intensity = Ambient + DiffuseK * ndl * atten;

                        // Shadow
                        if (shadow.HasValue && receiveShadows)
                        {
                            var sm = shadow.Value;
                            var clipShadow = SN.Vector4.Transform(new SN.Vector4(worldPos, 1f), sm.VP);
                            if (clipShadow.W > 0f)
                            {
                                var ndc = clipShadow / clipShadow.W;
                                float uS = ndc.X * 0.5f + 0.5f, vS = 1f - (ndc.Y * 0.5f + 0.5f), sz = ndc.Z;
                                int sx = Math.Clamp((int)(uS * sm.W), 0, sm.W - 1);
                                int sy = Math.Clamp((int)(vS * sm.H), 0, sm.H - 1);
                                int sx1 = Math.Min(sx + 1, sm.W - 1), sy1 = Math.Min(sy + 1, sm.H - 1);
                                float ndlBias = lightIsPoint ? 1f : MathF.Max(0f, SN.Vector3.Dot(normal, lightDirV));
                                float bias = MathF.Max(sm.Bias, 0.0005f + 0.002f * (1f - ndlBias));
                                float s0 = (sz > sm.Depth[sy * sm.W + sx] + bias) ? 1f : 0f;
                                float s1 = (sz > sm.Depth[sy * sm.W + sx1] + bias) ? 1f : 0f;
                                float s2 = (sz > sm.Depth[sy1 * sm.W + sx] + bias) ? 1f : 0f;
                                float s3 = (sz > sm.Depth[sy1 * sm.W + sx1] + bias) ? 1f : 0f;
                                float sh = (s0 + s1 + s2 + s3) * 0.25f;
                                intensity *= (1f - 0.5f * sh);
                            }
                        }

                        // UVs
                        float u = (w0 * Ua.X * aInvW + w1 * Ub.X * bInvW + w2 * Uc.X * cInvW) / invW;
                        float v = (w0 * Ua.Y * aInvW + w1 * Ub.Y * bInvW + w2 * Uc.Y * cInvW) / invW;

                        // Accumulators
                        Color albedo = Color.FromRgb(255, 255, 255);
                        Color detailMul = Color.FromRgb(255, 255, 255);
                        Color emissive = Color.FromRgb(0, 0, 0);
                        float aoMul = 1f, specMap = 0f, roughFromMap = -1f, metalFromMap = -1f;
                        float albedoAlpha = 0f, opacityMapMul = 1f;
                        bool hadAlbedoRGB = false, sawAlbedoSlot = false, hasOpacitySlot = false;

                        if (mat?.Textures != null && mat.Textures.Count > 0)
                        {
                            foreach (var slot in mat.Textures)
                            {
                                // tolerant fetch (works for path/stream/byte[]/bitmap/engine)
                                var slotType = slot.GetType();
                                var pTex = slotType.GetProperty("Texture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var rawObj = pTex?.GetValue(slot);

                                var tex = rawObj as Game_Engine.Core.Texture2D ?? EnsureEngineTexture2D(rawObj);
                                if (tex == null) continue;

                                // If we created a proper engine texture, persist it back on the slot
                                if (tex != rawObj && pTex is { CanWrite: true })
                                    pTex.SetValue(slot, tex);

                                int mask = GetFaceMask(slot);
                                if (mask != -1 && triFaceMask != -1 && (mask & triFaceMask) == 0) continue;

                                string usage = GetUsageName(slot).ToLowerInvariant();

                                float uu = u, vv = v; ApplyUVXform(slot, ref uu, ref vv);
                                bool noFlipV = GetB(slot, "NoFlipV", false);

                                if (usage.Contains("emiss"))
                                {
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    emissive = AddColor(emissive, s);
                                }
                                else if (usage.Contains("occl") || usage == "ao")
                                {
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    aoMul *= Math.Clamp(Luma(s), 0f, 1f);
                                }
                                else if (usage.Contains("detail"))
                                {
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    detailMul = MulColor(detailMul, s);
                                }
                                else if (usage.Contains("spec"))
                                {
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    specMap = Math.Clamp(Luma(s), 0f, 1f);
                                }
                                else if (usage.Contains("rough"))
                                {
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    roughFromMap = Math.Clamp(Luma(s), 0f, 1f);
                                }
                                else if (usage.Contains("metal"))
                                {
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    metalFromMap = Math.Clamp(Luma(s), 0f, 1f);
                                }
                                else if (usage.Contains("opacity") || usage.Contains("alpha") || usage.Contains("transp"))
                                {
                                    hasOpacitySlot = true;
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    float op = (s.A < 254) ? (s.A / 255f) : Math.Clamp(Luma(s), 0f, 1f);
                                    opacityMapMul *= op;
                                    if (!hadAlbedoRGB && !sawAlbedoSlot)
                                    {
                                        albedo = MulColor(albedo, Color.FromRgb(s.R, s.G, s.B));
                                        hadAlbedoRGB = true;
                                    }
                                }
                                else if (usage.Contains("normal"))
                                {
                                    // (no TBN yet)
                                }
                                else
                                {
                                    var s = SamplePMClamped(tex, uu, noFlipV ? (1f - vv) : vv);
                                    albedo = AlphaOver(albedo, s);
                                    hadAlbedoRGB = true; sawAlbedoSlot = true;
                                    float aA = s.A / 255f;
                                    albedoAlpha = albedoAlpha + (1f - albedoAlpha) * aA;
                                }
                            }
                        }

                        float metallic = metalFromMap >= 0 ? metalFromMap : Math.Clamp(mat?.Metallic ?? 0f, 0f, 1f);
                        float smooth = roughFromMap >= 0 ? (1f - roughFromMap) : Math.Clamp(mat?.Smoothness ?? 0.5f, 0f, 1f);
                        float specStr = Math.Clamp(specMap, 0f, 1f);
                        float shininess = 8f + smooth * smooth * 248f;

                        Color safeTint = (tint.R | tint.G | tint.B) == 0 ? Color.FromRgb(255, 255, 255) : tint;
                        Color lit = ShadeColor(safeTint, intensity * aoMul);
                        Color baseCol = MulColor(MulColor(albedo, detailMul), lit);

                        Color specAdd = Color.FromRgb(0, 0, 0);
                        if (DiffuseK > 0f)
                        {
                            var Vdir = SN.Vector3.Normalize(-viewPos);
                            SN.Vector3 halfVec = lightIsPoint
                                ? SN.Vector3.Normalize(SN.Vector3.Normalize(lightPosV - viewPos) + Vdir)
                                : SN.Vector3.Normalize(lightDirV + Vdir);
                            float ndh = MathF.Max(0f, SN.Vector3.Dot(normal, halfVec));
                            float spec = MathF.Pow(ndh, shininess) * specStr * (0.25f + 0.75f * metallic);
                            byte sr = (byte)Math.Clamp(spec * 255f, 0f, 255f);
                            specAdd = Color.FromRgb(sr, sr, sr);
                        }

                        Color pix = AddColor(AddColor(baseCol, specAdd), emissive);

                        if (transparentPass)
                        {
                            float baseAlpha = hasOpacitySlot ? 1f : (sawAlbedoSlot ? albedoAlpha : 1f);
                            float aEff = Math.Clamp(baseAlpha * opacityMapMul * matOpacity * tintA, 0f, 1f);
                            if (aEff <= 0.0001f) continue;
                            const float OPAQUEISH = 0.60f;
                            if (aEff >= OPAQUEISH) zbuf[idx] = z;
                            color[idx] = BlendOver(color[idx], pix, aEff);
                        }
                        else
                        {
                            color[idx] = PackBGRA(pix);
                        }
                    }
            }
        }
    }





    void DrawNodeDepth(GameObject go, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                   in SN.Matrix4x4 parentWorld, float[] depth, int W, int H)
    {
        var world = parentWorld * WorldFromTransform(go.Transform);

        var filters = go.Behaviors.OfType<MeshFilter>().Where(b => b.Enabled).ToList();
        var renderers = go.Behaviors.OfType<MeshRenderer>().Where(b => b.Enabled).ToList();
        int n = Math.Min(filters.Count, renderers.Count);

        for (int i = 0; i < n; i++)
        {
            var mf = filters[i];
            var mr = renderers[i];
            if (mf.Mesh != null && mr.CastShadows)
                RasterizeDepth(mf.Mesh, world, view, proj, depth, W, H, doubleSided: true);
        }

        foreach (var ch in go.Children)
            DrawNodeDepth(ch, view, proj, world, depth, W, H);
    }


    void RasterizeDepth(Mesh mesh,
                    in SN.Matrix4x4 world, in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                    float[] zbuf, int W, int H, bool doubleSided = false)
    {
        if (mesh.Vertices == null || mesh.TriIndices == null) return;
        var V = mesh.Vertices;
        var I = mesh.TriIndices;
        var WV = world * view;
        var WVP = WV * proj;
        float winding = world.GetDeterminant() >= 0 ? 1f : -1f;
        for (int i = 0; i < I.Length; i += 3)
        {
            int ia = I[i], ib = I[i + 1], ic = I[i + 2];
            var a = V[ia]; var b = V[ib]; var c = V[ic];
            var Ac = SN.Vector4.Transform(new SN.Vector4(a, 1), WVP);
            var Bc = SN.Vector4.Transform(new SN.Vector4(b, 1), WVP);
            var Cc = SN.Vector4.Transform(new SN.Vector4(c, 1), WVP);
            if (Ac.W <= 0 || Bc.W <= 0 || Cc.W <= 0) continue;
            var An = Ac / Ac.W; var Bn = Bc / Bc.W; var Cn = Cc / Cc.W;
            static int OutMask(SN.Vector4 n) =>
                (n.X < -1 ? 1 : 0) | (n.X > 1 ? 2 : 0) |
                (n.Y < -1 ? 4 : 0) | (n.Y > 1 ? 8 : 0) |
                (n.Z < 0 ? 16 : 0) | (n.Z > 1 ? 32 : 0);
            if ((OutMask(An) & OutMask(Bn) & OutMask(Cn)) != 0) continue;
            var av = SN.Vector3.Transform(a, WV);
            var bv = SN.Vector3.Transform(b, WV);
            var cv = SN.Vector3.Transform(c, WV);
            var nView = SN.Vector3.Cross(bv - av, cv - av);
            float viewNormalSign = winding * nView.Z;
            if (!doubleSided && viewNormalSign >= 0f) continue;
            var As = new SN.Vector2((An.X * 0.5f + 0.5f) * W, (1 - (An.Y * 0.5f + 0.5f)) * H);
            var Bs = new SN.Vector2((Bn.X * 0.5f + 0.5f) * W, (1 - (Bn.Y * 0.5f + 0.5f)) * H);
            var Cs = new SN.Vector2((Cn.X * 0.5f + 0.5f) * W, (1 - (Cn.Y * 0.5f + 0.5f)) * H);
            float aInvW = 1f / Ac.W, bInvW = 1f / Bc.W, cInvW = 1f / Cc.W;
            float aZw = An.Z * aInvW, bZw = Bn.Z * bInvW, cZw = Cn.Z * cInvW;
            int minX = (int)MathF.Floor(MathF.Min(As.X, MathF.Min(Bs.X, Cs.X)));
            int maxX = (int)MathF.Ceiling(MathF.Max(As.X, MathF.Max(Bs.X, Cs.X)));
            int minY = (int)MathF.Floor(MathF.Min(As.Y, MathF.Min(Bs.Y, Cs.Y)));
            int maxY = (int)MathF.Ceiling(MathF.Max(As.Y, MathF.Max(Bs.Y, Cs.Y)));
            if (maxX < 0 || maxY < 0 || minX >= W || minY >= H) continue;
            minX = Math.Clamp(minX, 0, W - 1); maxX = Math.Clamp(maxX, 0, W - 1);
            minY = Math.Clamp(minY, 0, H - 1); maxY = Math.Clamp(maxY, 0, H - 1);
            static float Edge(SN.Vector2 p, SN.Vector2 a2, SN.Vector2 b2)
                => (p.X - a2.X) * (b2.Y - a2.Y) - (p.Y - a2.Y) * (b2.X - a2.X);
            float area = Edge(Cs, As, Bs); if (area == 0) continue;
            float invArea = 1f / area;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new SN.Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(p, Bs, Cs);
                    float w1 = Edge(p, Cs, As);
                    float w2 = Edge(p, As, Bs);
                    if (area > 0f) { if (w0 < -NearEps || w1 < -NearEps || w2 < -NearEps) continue; }
                    else { if (w0 > NearEps || w1 > NearEps || w2 > NearEps) continue; }
                    w0 *= invArea; w1 *= invArea; w2 *= invArea;
                    float invW = w0 * aInvW + w1 * bInvW + w2 * cInvW;
                    if (invW <= 0) continue;
                    float z = (w0 * aZw + w1 * bZw + w2 * cZw) / invW;
                    int idx = y * W + x;
                    if (z < zbuf[idx]) zbuf[idx] = z;
                }
        }
    }
    #endregion

    static void Downsample2x(uint[] src, int srcW, int srcH, uint[] dst, int dstW, int dstH)
    {
        for (int y = 0; y < dstH; y++)
        {
            int sy = y * 2;
            int row0 = sy * srcW;
            int row1 = (sy + 1) * srcW;
            int di = y * dstW;

            for (int x = 0; x < dstW; x++)
            {
                int sx = x * 2;

                uint p00 = src[row0 + sx];
                uint p01 = src[row0 + sx + 1];
                uint p10 = src[row1 + sx];
                uint p11 = src[row1 + sx + 1];

                // Average BGRA (premul-safe if A is 255, which we use)
                int b = ((int)(p00 & 0xFF) + (int)(p01 & 0xFF) + (int)(p10 & 0xFF) + (int)(p11 & 0xFF)) >> 2;
                int g = (((int)(p00 >> 8) & 0xFF) + ((int)(p01 >> 8) & 0xFF) + ((int)(p10 >> 8) & 0xFF) + ((int)(p11 >> 8) & 0xFF)) >> 2;
                int r = (((int)(p00 >> 16) & 0xFF) + ((int)(p01 >> 16) & 0xFF) + ((int)(p10 >> 16) & 0xFF) + ((int)(p11 >> 16) & 0xFF)) >> 2;
                int a = (((int)(p00 >> 24) & 0xFF) + ((int)(p01 >> 24) & 0xFF) + ((int)(p10 >> 24) & 0xFF) + ((int)(p11 >> 24) & 0xFF)) >> 2;

                dst[di + x] = (uint)(b | (g << 8) | (r << 16) | (a << 24));
            }
        }
    }

    // Approx local bounding radius (for any mesh)
    static float ApproxLocalRadius(Mesh m)
    {
        float r2 = 0f;
        var v = m.Vertices;
        for (int i = 0; i < v.Length; i++)
        {
            float d2 = v[i].X * v[i].X + v[i].Y * v[i].Y + v[i].Z * v[i].Z;
            if (d2 > r2) r2 = d2;
        }
        return MathF.Sqrt(r2);
    }

    // Approx cylinder/cone parameters from geometry
    static (float radius, float height) ApproxRadialAndHeight(Mesh m)
    {
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity, r = 0f;
        foreach (var p in m.Vertices)
        {
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
            float rr = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
            if (rr > r) r = rr;
        }
        return (r, maxY - minY);
    }

    void OverlayInfiniteGrid(in SN.Matrix4x4 view, in SN.Matrix4x4 proj,
                         uint[] color, float[] zbuf, int W, int H,
                         float step = 1f, int majorEvery = 5)
    {
        // colors
        uint minor = PackBGRA(Color.FromRgb(0x30, 0x30, 0x30));
        uint major = PackBGRA(Color.FromRgb(0x48, 0x48, 0x48));
        uint axis = PackBGRA(Color.FromRgb(0x60, 0x60, 0x60));

        var vp = view * proj;
        SN.Matrix4x4.Invert(vp, out var invVP);

        // camera world position (for distance fade)
        SN.Matrix4x4.Invert(view, out var invView);
        var cam = SN.Vector3.Transform(SN.Vector3.Zero, invView);

        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            float ny = 1f - ((y + 0.5f) / H) * 2f;         // [-1..+1]

            for (int x = 0; x < W; x++)
            {
                float nx = ((x + 0.5f) / W) * 2f - 1f;     // [-1..+1]

                // ray in WORLD space
                var n4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0f, 1f), invVP);
                var f4 = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1f, 1f), invVP);
                var n3 = new SN.Vector3(n4.X, n4.Y, n4.Z) / n4.W;
                var f3 = new SN.Vector3(f4.X, f4.Y, f4.Z) / f4.W;
                var dir = SN.Vector3.Normalize(f3 - n3);

                // intersect y=0 ground plane in front of the near point
                const float EPS = 1e-6f;
                if (MathF.Abs(dir.Y) < EPS) continue;
                float t = -n3.Y / dir.Y;
                if (t <= 0f) continue;

                var p = n3 + dir * t; // world hit

                // project for proper z
                var clip = SN.Vector4.Transform(new SN.Vector4(p, 1f), vp);
                if (clip.W <= 0f) continue;
                float z = (clip.Z / clip.W);
                int idx = row + x;
                if (z >= zbuf[idx]) continue; // something nearer already there

                // grid shading (thin lines that fade with distance)
                float gx = p.X / step, gz = p.Z / step;
                float wx = MathF.Abs(gx - MathF.Round(gx));
                float wz = MathF.Abs(gz - MathF.Round(gz));
                float distToLine = MathF.Min(wx, wz);        // 0 at line, ~0.5 mid-cell

                // line width in "cell" units (slightly widens up close)
                float w = 0.015f + 0.0025f * MathF.Min(40f, t);
                float alpha = Math.Clamp((w - distToLine) / w, 0f, 1f);

                // distance fade so it doesn’t clutter the horizon
                float d = SN.Vector3.Distance(cam, p);
                float fade = 1f / (1f + 0.12f * d);
                alpha *= fade;

                if (alpha <= 0f) continue;

                // choose color: axis (x/z == 0), major every N, else minor
                int ix = (int)MathF.Round(gx);
                int iz = (int)MathF.Round(gz);
                bool onAxis = (ix == 0) || (iz == 0);
                bool onMajor = (ix % majorEvery == 0) || (iz % majorEvery == 0);
                uint col = onAxis ? axis : (onMajor ? major : minor);

                // blend over sky; write z so meshes in front occlude it
                color[idx] = LerpBGRA(color[idx], col, alpha);
                zbuf[idx] = z;
            }
        }
    }
}