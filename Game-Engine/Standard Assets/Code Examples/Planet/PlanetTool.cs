#nullable enable
using System;
using Game_Engine.Core;
using Game_Engine.Core.Component;
using Game_Engine.Core.Input;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

/// <summary>
/// Basic runtime planet sculpting tool:
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
    [Persist] public float SurfaceOffset { get; set; } = 0.75f;
    [Persist] public bool LogAdjustments { get; set; } = true;

    float _applyCooldown;
    PlanetTerrain? _targetPlanet;

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

        _applyCooldown -= Math.Max(0f, (float)Time.deltaTime);
        if (_applyCooldown > 0f)
            return;

        bool dig = Input.GetMouse(MouseButton.Left);
        bool build = Input.GetMouse(MouseButton.Right);
        if (!dig && !build)
            return;

        var centrePos = _targetPlanet.gameObject?.Transform.Position;
        var toolPos = gameObject.Transform.Position;
        if (centrePos == null)
            return;

        var center = new SN.Vector3((float)centrePos.X, (float)centrePos.Y, (float)centrePos.Z);
        var origin = new SN.Vector3((float)toolPos.X, (float)toolPos.Y, (float)toolPos.Z);
        var toTool = origin - center;
        if (toTool.LengthSquared() < 1e-5f)
            return;

        var dir = SN.Vector3.Normalize(toTool);
        float surfaceRadius = _targetPlanet.SampleSurfaceRadius(dir);
        var surfacePoint = center + dir * (surfaceRadius + SurfaceOffset);

        if (dig)
            _targetPlanet.DigSphere(surfacePoint, BrushRadius, BrushStrength, BrushFalloff);
        if (build)
            _targetPlanet.BuildSphere(surfacePoint, BrushRadius, BrushStrength, BrushFalloff);

        float rate = Math.Max(1f, MaxApplyRatePerSecond);
        _applyCooldown = 1f / rate;
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
