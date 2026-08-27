#nullable enable
using System;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Input;
using Game_Engine.Core.Planet;
using GEInput = Game_Engine.Core.Input.Input;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Play-mode planet sculpting (camera look-ray):
    /// - Hold LMB / Fire1: dig
    /// - Hold RMB: build
    /// - '[' / ']': radius; '-' / '=': strength
    /// Added automatically by <see cref="PlanetPlayerSpawner"/>.
    /// </summary>
    [ComponentCategory("Gameplay")]
    public sealed class PlanetTool : Behavior
    {
        [Persist] public float BrushRadius { get; set; } = 0.6f;
        [Persist] public float BrushStrength { get; set; } = 0.5f;
        [Persist] public float BrushFalloff { get; set; } = 0.65f;
        [Persist] public float MaxApplyRatePerSecond { get; set; } = 6f;
        [Persist] public float MaxRayDistance { get; set; } = 20000f;
        [Persist] public bool LogAdjustments { get; set; } = true;

        float _applyCooldown;
        PlanetTerrain? _targetPlanet;
        Camera? _camera;
        bool _wasPainting;
        SN.Vector3 _lastPaintHit;
        bool _hasLastPaintHit;

        public void BindPlanet(PlanetTerrain planet) => _targetPlanet = planet;

        public override void Start()
        {
            ResolveCamera();
            RefreshPlanetTarget();
            // Older defaults (10 m / 8 strength) feel like craters on foot-scale play.
            if (BrushRadius > 1.25f) BrushRadius = 0.6f;
            if (BrushStrength > 1.25f) BrushStrength = 0.5f;
            if (LogAdjustments)
                LogInfo(_targetPlanet != null
                    ? "PlanetTool ready — Game view: hold LMB dig / RMB build (F/G keys work too). Scene view: Hand tool + click planet."
                    : "PlanetTool: no planet found.");
        }

        /// <summary>Scene-view click path during Play (uses cursor hit, not look-ray).</summary>
        public static void ApplyStrokeAt(SN.Vector3 worldPoint, bool dig, bool build)
        {
            foreach (var tool in SceneQuery.FindBehaviors<PlanetTool>())
            {
                if (tool == null || !tool.IsActiveAndEnabled) continue;
                tool.TryPaintAt(worldPoint, dig, build);
                return;
            }
        }

        /// <summary>Game-view pointer path during Play (camera look-ray).</summary>
        public static void ApplyLookStroke(bool dig, bool build)
        {
            foreach (var tool in SceneQuery.FindBehaviors<PlanetTool>())
            {
                if (tool == null || !tool.IsActiveAndEnabled) continue;
                tool.TryLookPaint(dig, build);
                return;
            }
        }

        public override void Update()
        {
            if (!SceneService.PlayMode)
                return;

            HandleBrushTweaks();

            if (_targetPlanet == null || !_targetPlanet.IsActiveAndEnabled)
                RefreshPlanetTarget();
            if (_targetPlanet == null || gameObject == null)
                return;

            bool shift = GEInput.GetKey(KeyCode.LeftShift);
            bool dig = GEInput.GetMouse(MouseButton.Left) || GEInput.GetAction("Fire1")
                       || GEInput.GetKey(KeyCode.F);
            bool build = GEInput.GetMouse(MouseButton.Right)
                         || (GEInput.GetMouse(MouseButton.Left) && shift)
                         || GEInput.GetKey(KeyCode.G);
            if (shift && GEInput.GetKey(KeyCode.F))
                dig = false;
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

            TryLookPaint(dig, build);
        }

        void TryLookPaint(bool dig, bool build)
        {
            if (_targetPlanet == null || gameObject == null)
                return;

            _applyCooldown -= Math.Max(0f, (float)Time.deltaTime);
            if (_applyCooldown > 0f)
                return;

            if (!TryGetLookRay(out var origin, out var dir))
                return;

            float maxDist = MaxRayDistance;
            if (maxDist <= 1f)
                maxDist = Math.Max(2000f, _targetPlanet.Radius * 8f);

            if (!_targetPlanet.RaycastPaintSurface(origin, dir, maxDist, out PlanetDensityHit hit))
                return;

            float minStep = Math.Max(0.05f, BrushRadius * 0.22f);
            if (_hasLastPaintHit && SN.Vector3.DistanceSquared(hit.Point, _lastPaintHit) < minStep * minStep)
                return;
            _lastPaintHit = hit.Point;
            _hasLastPaintHit = true;

            if (dig)
                _targetPlanet.DigSphere(hit.Point, EffectiveRadius(), EffectiveStrength(), BrushFalloff);
            if (build)
                _targetPlanet.BuildSphere(hit.Point, EffectiveRadius(), EffectiveStrength(), BrushFalloff);

            _targetPlanet.NotifyEdited(origin);

            _wasPainting = true;
            float rate = Math.Clamp(MaxApplyRatePerSecond, 1f, 24f);
            _applyCooldown = 1f / rate;
        }

        void TryPaintAt(SN.Vector3 worldPoint, bool dig, bool build)
        {
            if (_targetPlanet == null || !_targetPlanet.IsActiveAndEnabled)
                RefreshPlanetTarget();
            if (_targetPlanet == null) return;

            _applyCooldown -= Math.Max(0f, (float)Time.deltaTime);
            if (_applyCooldown > 0f) return;

            SN.Vector3 origin = worldPoint;
            if (TryGetLookRay(out var eye, out _))
                origin = eye;

            float minStep = Math.Max(0.05f, BrushRadius * 0.22f);
            if (_hasLastPaintHit && SN.Vector3.DistanceSquared(worldPoint, _lastPaintHit) < minStep * minStep)
                return;
            _lastPaintHit = worldPoint;
            _hasLastPaintHit = true;

            if (dig) _targetPlanet.DigSphere(worldPoint, EffectiveRadius(), EffectiveStrength(), BrushFalloff);
            if (build) _targetPlanet.BuildSphere(worldPoint, EffectiveRadius(), EffectiveStrength(), BrushFalloff);
            _targetPlanet.NotifyEdited(origin);
            _wasPainting = true;
            _applyCooldown = 1f / Math.Clamp(MaxApplyRatePerSecond, 1f, 24f);
        }

        float EffectiveRadius() => Math.Clamp(BrushRadius, 0.2f, 2.5f);

        float EffectiveStrength() => Math.Clamp(BrushStrength, 0.15f, 1.5f);

        void ResolveCamera()
        {
            _camera = null;
            if (gameObject != null)
            {
                foreach (var child in gameObject.Children)
                {
                    var cam = child.Behaviors?.OfType<Camera>().FirstOrDefault(c => c.Enabled);
                    if (cam != null) { _camera = cam; return; }
                }
                _camera = gameObject.Behaviors.OfType<Camera>().FirstOrDefault(c => c.Enabled);
            }
            _camera ??= CameraService.MainOrFirst();
            _camera ??= SceneQuery.FindBehaviors<Camera>().FirstOrDefault(c => c.Enabled);
        }

        bool TryGetLookRay(out SN.Vector3 origin, out SN.Vector3 direction)
        {
            origin = default;
            direction = default;

            if (_camera == null || !_camera.Enabled)
                ResolveCamera();
            if (_camera != null && _camera.TryGetWorldLookRay(out origin, out direction))
                return true;

            if (gameObject?.Transform == null)
                return false;

            var world = SceneGraphUtil.AccumulateWorld(gameObject);
            origin = new SN.Vector3(world.M41, world.M42, world.M43);
            direction = SN.Vector3.TransformNormal(new SN.Vector3(0f, 0f, -1f), world);
            if (direction.LengthSquared() <= 1e-10f)
                direction = new SN.Vector3(0, 0, -1);
            else
                direction = SN.Vector3.Normalize(direction);
            return true;
        }

        void HandleBrushTweaks()
        {
            if (GEInput.GetKeyDown(KeyCode.OemOpenBrackets))
            {
                BrushRadius = Math.Max(0.2f, BrushRadius - 0.1f);
                if (LogAdjustments) LogInfo($"PlanetTool radius: {BrushRadius:F2} m");
            }
            if (GEInput.GetKeyDown(KeyCode.OemCloseBrackets))
            {
                BrushRadius = Math.Min(2.5f, BrushRadius + 0.1f);
                if (LogAdjustments) LogInfo($"PlanetTool radius: {BrushRadius:F2} m");
            }

            if (GEInput.GetKeyDown(KeyCode.OemMinus))
            {
                BrushStrength = Math.Max(0.15f, BrushStrength - 0.1f);
                if (LogAdjustments) LogInfo($"PlanetTool strength: {BrushStrength:F2}");
            }
            if (GEInput.GetKeyDown(KeyCode.OemPlus))
            {
                BrushStrength = Math.Min(1.5f, BrushStrength + 0.1f);
                if (LogAdjustments) LogInfo($"PlanetTool strength: {BrushStrength:F2}");
            }

            if (GEInput.GetKeyDown(KeyCode.R))
            {
                ResolveCamera();
                RefreshPlanetTarget();
                if (LogAdjustments)
                    LogInfo(_targetPlanet != null ? "PlanetTool target refreshed." : "PlanetTool: no active planet found.");
            }
        }

        void RefreshPlanetTarget()
        {
            SN.Vector3 worldPos = SN.Vector3.Zero;
            if (gameObject?.Transform != null)
            {
                var world = SceneGraphUtil.AccumulateWorld(gameObject);
                worldPos = new SN.Vector3(world.M41, world.M42, world.M43);
            }
            _targetPlanet = PlanetManipulationApi.FindNearestPlanet(worldPos);
        }
    }
}
