#nullable enable
using System;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Input;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

/// <summary>
/// Runtime planet sculpting tool (camera / look-ray, same as Scene View):
/// - Hold left mouse: dig
/// - Hold right mouse: build
/// - '[' / ']': radius down/up
/// - '-' / '=': strength down/up
/// Attach to any GameObject (commonly player or camera rig).
/// </summary>
public sealed class PlanetTool : Behavior
{
    [Persist] public float BrushRadius { get; set; } = 10f;
    [Persist] public float BrushStrength { get; set; } = 8f;
    [Persist] public float BrushFalloff { get; set; } = 0.6f;
    [Persist] public float MaxApplyRatePerSecond { get; set; } = 18f;
    [Persist] public float MaxRayDistance { get; set; } = 20000f;
    [Persist] public bool LogAdjustments { get; set; } = true;

    float _applyCooldown;
    PlanetTerrain? _targetPlanet;
    bool _wasPainting;
    SN.Vector3 _lastPaintHit;
    bool _hasLastPaintHit;

    public override void Start()
    {
        RefreshPlanetTarget();
    }

    public override void Update()
    {
        if (_targetPlanet == null || !_targetPlanet.IsActiveAndEnabled)
            RefreshPlanetTarget();
        if (_targetPlanet == null || gameObject == null)
            return;

        HandleBrushTweaks();

        bool dig = Input.GetMouse(MouseButton.Left);
        bool build = Input.GetMouse(MouseButton.Right);
        if (!dig && !build)
        {
            if (_wasPainting)
            {
                _targetPlanet.SaveVoxelEdits();
                _wasPainting = false;
                _hasLastPaintHit = false;
            }
            return;
        }

        _applyCooldown -= Math.Max(0f, (float)Time.deltaTime);
        if (_applyCooldown > 0f)
            return;

        if (!TryGetLookRay(out var origin, out var dir))
            return;

        float maxDist = MaxRayDistance;
        if (maxDist <= 1f)
            maxDist = Math.Max(2000f, _targetPlanet.Radius * 8f);

        if (!_targetPlanet.Raycast(origin, dir, maxDist, out PlanetDensityHit hit))
            return;

        float minStep = Math.Max(0.05f, BrushRadius * 0.22f);
        if (_hasLastPaintHit && SN.Vector3.DistanceSquared(hit.Point, _lastPaintHit) < minStep * minStep)
            return;
        _lastPaintHit = hit.Point;
        _hasLastPaintHit = true;

        if (dig)
            _targetPlanet.DigSphere(hit.Point, BrushRadius, BrushStrength, BrushFalloff);
        if (build)
            _targetPlanet.BuildSphere(hit.Point, BrushRadius, BrushStrength, BrushFalloff);

        _wasPainting = true;
        float rate = Math.Clamp(MaxApplyRatePerSecond, 1f, 12f);
        _applyCooldown = 1f / rate;
    }

    bool TryGetLookRay(out SN.Vector3 origin, out SN.Vector3 direction)
    {
        origin = default;
        direction = default;

        Camera? cam = gameObject?.Behaviors.OfType<Camera>().FirstOrDefault(c => c.Enabled);
        cam ??= SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled && c.IsMain);
        cam ??= SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);

        GameObject? src = cam?.gameObject ?? gameObject;
        if (src?.Transform == null)
            return false;

        var tr = src.Transform;
        origin = new SN.Vector3((float)tr.Position.X, (float)tr.Position.Y, (float)tr.Position.Z);

        static float Deg2Rad(double d) => (float)(Math.PI / 180.0 * d);
        var r = SN.Matrix4x4.CreateFromYawPitchRoll(
            Deg2Rad(tr.Rotation.Y), Deg2Rad(tr.Rotation.X), Deg2Rad(tr.Rotation.Z));
        direction = SN.Vector3.TransformNormal(new SN.Vector3(0, 0, -1), r);
        if (direction.LengthSquared() <= 1e-10f)
            direction = new SN.Vector3(0, 0, -1);
        else
            direction = SN.Vector3.Normalize(direction);
        return true;
    }

    void HandleBrushTweaks()
    {
        if (Input.GetKeyDown(KeyCode.OemOpenBrackets))
        {
            BrushRadius = Math.Max(0.5f, BrushRadius - 1f);
            if (LogAdjustments) LogInfo($"PlanetTool radius: {BrushRadius:F1}");
        }
        if (Input.GetKeyDown(KeyCode.OemCloseBrackets))
        {
            BrushRadius += 1f;
            if (LogAdjustments) LogInfo($"PlanetTool radius: {BrushRadius:F1}");
        }

        if (Input.GetKeyDown(KeyCode.OemMinus))
        {
            BrushStrength = Math.Max(0.1f, BrushStrength - 1f);
            if (LogAdjustments) LogInfo($"PlanetTool strength: {BrushStrength:F1}");
        }
        if (Input.GetKeyDown(KeyCode.OemPlus))
        {
            BrushStrength += 1f;
            if (LogAdjustments) LogInfo($"PlanetTool strength: {BrushStrength:F1}");
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            RefreshPlanetTarget();
            if (LogAdjustments)
                LogInfo(_targetPlanet != null ? "PlanetTool target refreshed." : "PlanetTool: no active planet found.");
        }
    }

    void RefreshPlanetTarget()
    {
        var p = gameObject?.Transform.Position;
        var worldPos = p == null
            ? SN.Vector3.Zero
            : new SN.Vector3((float)p.X, (float)p.Y, (float)p.Z);
        _targetPlanet = PlanetManipulationApi.FindNearestPlanet(worldPos);
    }
}
