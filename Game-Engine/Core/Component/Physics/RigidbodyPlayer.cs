#nullable enable
using System;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Physics;
using Game_Engine.Core.Planet;
using GEInput = Game_Engine.Core.Input.Input;
using GEPhysics = Game_Engine.Core.Physics.Physics;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Physics-based player movement using Rigidbody instead of CharacterController.
    /// Gives a momentum-based feel: sliding on slopes, natural pushing of other objects,
    /// and inertia when stopping/turning. Drop-in alternative to PlayerMovement.
    ///
    /// Attach to a GameObject with a Rigidbody and CapsuleCollider.
    /// The [Require] attribute auto-adds these if missing.
    /// </summary>
    [ComponentCategory("Physics")]
    [Require(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class RigidbodyPlayer : Behavior
    {
        // ── Movement ──
        [Persist] public float MoveForce { get; set; } = 50f;
        [Persist] public float MaxSpeed { get; set; } = 7f;
        [Persist] public float SprintMultiplier { get; set; } = 1.75f;
        [Persist] public float JumpImpulse { get; set; } = 5f;
        [Persist] public float AirControlFactor { get; set; } = 0.3f;
        [Persist] public float GroundDrag { get; set; } = 5f;
        [Persist] public float AirDrag { get; set; } = 0.5f;

        // ── Swimming ──
        [Persist] public float SwimForce { get; set; } = 30f;
        [Persist] public float SwimMaxSpeed { get; set; } = 4.5f;
        [Persist] public float SwimVerticalSpeed { get; set; } = 8f;
        [Persist] public float SwimDrag { get; set; } = 3.2f;
        [Persist] public float PlanetSurfaceSwimSpeed { get; set; } = 6.5f;

        // ── Look / camera ──
        [Persist] public float LookSensitivity { get; set; } = 90f;
        [Persist] public bool FirstPerson { get; set; } = true;
        [Persist] public Vector3 FirstPersonOffset { get; set; } = new Vector3(0, 1.7, 0);
        [Persist] public Vector3 ThirdPersonOffset { get; set; } = new Vector3(0, 1.7, -3.5);
        [Persist] public float CameraFollowLerp { get; set; } = 12f;
        [Persist] public float MaxLookPitch { get; set; } = 89f;
        [Persist] public bool AvoidCameraGroundClip { get; set; } = true;
        [Persist] public float CameraCollisionPadding { get; set; } = 0.2f;
        [Persist] public float CameraCollisionStartOffset { get; set; } = 0.05f;
        [Persist] public float CameraUpSmoothing { get; set; } = 10f;

        // ── Body facing ──
        [Persist] public bool RotateBodyWithLook { get; set; } = true;
        [Persist] public bool TurnBodyWhileMoving { get; set; } = false;

        // ── Jump buffering ──
        [Persist] public float JumpBufferSeconds { get; set; } = 0.12f;

        // ── Planet density grounding (matches CharacterController) ──
        [Persist] public float StepUpMax { get; set; } = 0.5f;
        [Persist] public float GroundSnapDistance { get; set; } = 0.7f;

        /// <summary>
        /// Chunks in every direction used for player collision. Surface stand
        /// uses the heightfield (one sample). Cave probes never march farther
        /// than this neighborhood — the whole planet collider is not scanned.
        /// </summary>
        [Persist] public int NearbyChunkRadius { get; set; } = 2;

        /// <summary>
        /// Optional world-meter cap on that neighborhood. 0 = from chunk size only.
        /// </summary>
        [Persist] public float NearbyCollisionRadius { get; set; } = 32f;

        public bool IsPlanetSwimming { get; private set; }
        /// <summary>True when the player is actively diving below the surface (underwater post FX).</summary>
        public bool IsPlanetSubmerged { get; private set; }
        public float PlanetSubmergeDepth { get; private set; }

        /// <summary>
        /// Latched outer-crust walking: StandRadiusGrid only, zero density marches.
        /// Enter when radial &gt;= crustR - 6; leave when radial &lt; crustR - 10 or CameraBelowCrust.
        /// </summary>
        public bool SurfaceMode => _surfaceMode;

        bool _planetInWater;
        bool _planetDiving;
        bool _planetWasDiving;
        bool _planetSubmergedLatch;
        bool _diveHeld;
        bool _jumpHeld;
        Rigidbody? _rb;
        CapsuleCollider? _capsule;
        Camera? _cam;
        Transform? _camTr;
        PlanetTerrain? _planet;
        SN.Vector3 _planetCenter;
        float _neighborhoodM;
        int _neighborhoodFrame = -1;

        bool _surfaceMode = true;
        SN.Vector3 _planetCachePos;
        int _activePlanetCount = -1;

        float _yawDeg;
        float _pitchDeg;
        SN.Vector2 _wishLocal;
        bool _sprintHeld;
        float _jumpBuf;
        SN.Vector3 _cameraUp = SN.Vector3.UnitY;
        SN.Vector3 _lastMoveForward = new(0f, 0f, -1f);
        bool _airborne;
        float _verticalVel;
        float _swimRadialVel;
        SN.Vector3 _swimPlanarVel;
        SN.Vector3 _lastAlignUp;
        float _lastAlignYaw = float.NaN;

        float _collisionCacheTime = float.NegativeInfinity;
        SN.Vector3 _collisionCacheDir = new(float.NaN);
        float _collisionCacheR;

        public override void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            ResolveCamera();

            _yawDeg = (float)Transform.Rotation.Y;
            _pitchDeg = 0f;

            if (GEInput.MouseSensitivity < 0.15f)
                GEInput.MouseSensitivity = 0.25f;

            if (_rb != null)
            {
                _rb.FreezeRotation = true;
                _rb.Drag = 0f;
            }

            var spawnPos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var spawnPlanet = Rigidbody.FindNearestPlanet(spawnPos, out var spawnCenter, out _);
            if (spawnPlanet != null)
            {
                var radial = spawnPos - spawnCenter;
                _cameraUp = radial.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(radial) : SN.Vector3.UnitY;
                ApplyPlanetCamera(_cameraUp, true, 0f, spawnPlanet);
            }
            else
                _cameraUp = SN.Vector3.UnitY;
        }

        public override void OnDisable()
        {
            if (_cam != null)
                _cam.UseLookOverride = false;
            base.OnDisable();
        }

        public override void OnEnable() => ResolveCamera();

        void ResolveCamera()
        {
            _cam = null; _camTr = null;

            if (gameObject != null)
            {
                foreach (var c in gameObject.Children)
                {
                    var cc = c.Behaviors?.OfType<Camera>().FirstOrDefault();
                    if (cc != null && cc.Enabled) { _cam = cc; _camTr = cc.Transform; break; }
                }
            }
            if (_cam == null) { _cam = GetComponent<Camera>(); _camTr = _cam?.Transform; }
            if (_cam == null)
            {
                var cams = SceneQuery.FindBehaviors<Camera>();
                _cam = cams.FirstOrDefault(c => c.IsMain) ?? cams.FirstOrDefault();
                _camTr = _cam?.Transform;
            }
        }

        public override void Update()
        {
            float dt = Math.Max(0.0001f, Time.deltaTime);

            // ── Look ──
            float lookX = GEInput.GetAxis("Mouse X");
            float lookY = GEInput.GetAxis("Mouse Y");
            _yawDeg = Normalize180(_yawDeg - lookX * LookSensitivity * dt);
            float maxPitch = Clamp(MaxLookPitch, 10f, 89f);
            _pitchDeg = Clamp(_pitchDeg - lookY * LookSensitivity * dt, -maxPitch, maxPitch);

            // ── Move intent ──
            GEInput.PollHardwareHeldKeys();
            int zFwd = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.W) ? 1 : 0)
                     - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.S) ? 1 : 0);
            int xRight = (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.D) ? 1 : 0)
                       - (GEInput.GetKey(Game_Engine.Core.Input.KeyCode.A) ? 1 : 0);
            if (zFwd == 0 && xRight == 0)
            {
                float axisV = GEInput.GetAxis("Vertical");
                float axisH = GEInput.GetAxis("Horizontal");
                if (MathF.Abs(axisV) > 0.15f) zFwd = axisV > 0f ? 1 : -1;
                if (MathF.Abs(axisH) > 0.15f) xRight = axisH > 0f ? 1 : -1;
            }

            var local = new SN.Vector2(xRight, -zFwd);
            float m2 = local.X * local.X + local.Y * local.Y;
            if (m2 > 1e-6f)
            {
                float inv = 1f / MathF.Sqrt(m2);
                local.X *= inv; local.Y *= inv;
            }
            _wishLocal = local;

            _sprintHeld = GEInput.GetAction("Sprint")
                || GEInput.GetKey(Game_Engine.Core.Input.KeyCode.LeftShift);

            _jumpHeld = GEInput.GetAction("Jump")
                || GEInput.GetKey(Game_Engine.Core.Input.KeyCode.Space);
            _diveHeld = GEInput.GetKey(Game_Engine.Core.Input.KeyCode.LeftCtrl)
                || GEInput.GetKey(Game_Engine.Core.Input.KeyCode.RightCtrl)
                || GEInput.GetAction("Crouch");

            if (GEInput.GetActionDown("Jump") || GEInput.GetKeyDown(Game_Engine.Core.Input.KeyCode.Space))
                _jumpBuf = JumpBufferSeconds;
            else if (_jumpHeld)
                _jumpBuf = Math.Max(_jumpBuf, 0.04f);

            var posNow = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var planet = BindPlanet(posNow);
            bool onPlanet = planet != null;
            var planetUp = SN.Vector3.UnitY;
            if (onPlanet)
            {
                var radial = posNow - _planetCenter;
                planetUp = radial.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(radial) : SN.Vector3.UnitY;
            }

            // Planet locomotion runs in FixedUpdate (60 Hz). Update only samples input + camera.
            if (!onPlanet)
            {
                IsPlanetSwimming = false;
                IsPlanetSubmerged = false;
                PlanetSubmergeDepth = 0f;
                _planetInWater = false;
                _planetDiving = false;
                _planetSubmergedLatch = false;
                _swimPlanarVel = SN.Vector3.Zero;
                _surfaceMode = false;
            }

            if (RotateBodyWithLook || (TurnBodyWhileMoving && m2 > 1e-6f) || onPlanet)
            {
                if (onPlanet && IsPlanetSwimming)
                {
                    // Pose applied inside SwimOnPlanet (horizontal swim, not upright stand).
                }
                else if (onPlanet)
                {
                    bool yawMoved = float.IsNaN(_lastAlignYaw) || MathF.Abs(_yawDeg - _lastAlignYaw) > 0.2f;
                    bool upMoved = _lastAlignUp.LengthSquared() < 1e-8f || SN.Vector3.Dot(_lastAlignUp, planetUp) < 0.9995f;
                    if (yawMoved || upMoved)
                    {
                        float yawRad = Deg2Rad(_yawDeg);
                        var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
                        TransformUtil.AlignLocalUp(Transform, planetUp, yawForward);
                        _lastAlignYaw = _yawDeg;
                        _lastAlignUp = planetUp;
                    }
                }
                else
                {
                    var rE = Transform.Rotation; rE.Y = _yawDeg; Transform.Rotation = rE;
                }
            }

            var cameraPos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            ApplyPlanetCamera(planetUp, onPlanet, dt, planet, cameraPos);
        }

        void ApplyPlanetSwimPose(SN.Vector3 radialUp, SN.Vector3 swimAxis, SN.Vector3 poseHint)
        {
            if (swimAxis.LengthSquared() < 1e-8f)
            {
                float yawRad = Deg2Rad(_yawDeg);
                var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
                BuildTangentBasis(radialUp, yawForward, out swimAxis, out _);
            }

            TransformUtil.AlignLocalUp(Transform, SN.Vector3.Normalize(swimAxis), poseHint);
            _lastAlignYaw = _yawDeg;
            _lastAlignUp = swimAxis;
        }

        PlanetTerrain? BindPlanet(SN.Vector3 pos)
        {
            _planet = Rigidbody.FindNearestPlanetCached(
                pos, ref _planet, ref _planetCenter, ref _planetCachePos, ref _activePlanetCount,
                out _planetCenter);
            return _planet;
        }

        void UpdateSurfaceMode(PlanetTerrain planet, SN.Vector3 pos, float crustR)
        {
            float dist = (pos - _planetCenter).Length();
            bool belowCrust = planet.Config != null && planet.Config.CameraBelowCrust;
            Rigidbody.RefreshPlanetSurfaceMode(ref _surfaceMode, dist, crustR, belowCrust);
        }

        float GetNeighborhoodRadius(PlanetTerrain planet, SN.Vector3 sphereDir)
        {
            int frame = Time.frameCount;
            if (frame == _neighborhoodFrame && _neighborhoodM > 0f)
                return _neighborhoodM;

            float chunkM = 24f;
            var leaf = planet.ChunkManager?.FindLeafAtDirection(sphereDir);
            if (leaf != null && planet.Config != null)
                chunkM = MathF.Max(8f, leaf.WorldSize(planet.Config.Radius) * planet.GetWorldRadiusScale());

            int chunks = Math.Clamp(NearbyChunkRadius, 1, 12);
            float fromChunks = chunks * chunkM;
            _neighborhoodM = NearbyCollisionRadius > 1f
                ? MathF.Min(fromChunks, NearbyCollisionRadius)
                : fromChunks;
            _neighborhoodM = Math.Clamp(_neighborhoodM, 8f, 256f);
            _neighborhoodFrame = frame;
            return _neighborhoodM;
        }

        void ApplyPlanetCamera(SN.Vector3 planetUp, bool onPlanet, float dt, PlanetTerrain? planet, SN.Vector3? postMovePos = null)
        {
            if (_cam == null)
                ResolveCamera();
            if (_cam == null) return;

            if (onPlanet)
            {
                var desiredUp = planetUp;
                if (postMovePos.HasValue && _planet != null)
                {
                    var radial = postMovePos.Value - _planetCenter;
                    if (radial.LengthSquared() > 1e-8f)
                        desiredUp = SN.Vector3.Normalize(radial);
                }
                if (IsPlanetSwimming)
                {
                    float upLerp = 1f - MathF.Exp(-MathF.Max(0f, CameraUpSmoothing) * dt);
                    _cameraUp = SafeNormalize(SN.Vector3.Lerp(_cameraUp, desiredUp, upLerp), desiredUp);
                }
                else
                    _cameraUp = desiredUp;
            }
            else if (dt > 0f)
            {
                var desired = _rb != null ? SafeNormalize(_rb.LocalUp, SN.Vector3.UnitY) : SN.Vector3.UnitY;
                float upLerp = 1f - MathF.Exp(-MathF.Max(0f, CameraUpSmoothing) * dt);
                _cameraUp = SafeNormalize(SN.Vector3.Lerp(_cameraUp, desired, upLerp), desired);
            }

            _cam.WorldUp = _cameraUp;
            if (_camTr != null)
            {
                if (FirstPerson) DriveCameraFirstPerson(_cameraUp, planet);
                else DriveCameraThirdPerson(Math.Max(dt, 0.0001f), _cameraUp);
            }
        }

        void WalkOnPlanetSurface(float dt, SN.Vector3 planetUp, PlanetTerrain planet, SN.Vector3 center)
        {
            _rb ??= GetComponent<Rigidbody>();
            _capsule ??= GetComponent<CapsuleCollider>();

            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

            var up = pos - center;
            if (up.LengthSquared() < 1e-8f) up = planetUp;
            else up = SN.Vector3.Normalize(up);

            float yawRad = Deg2Rad(_yawDeg);
            var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            BuildTangentBasis(up, yawForward, out var fwd, out var right);

            var wish = right * _wishLocal.X + fwd * (-_wishLocal.Y);
            wish -= up * SN.Vector3.Dot(wish, up);
            float speed = MathF.Max(1f, MaxSpeed) * (_sprintHeld ? MathF.Max(1f, SprintMultiplier) : 1f);

            float radius = 0.4f;
            float capsuleH = 1f;
            if (_capsule != null)
            {
                radius = MathF.Max(0.05f, _capsule.Radius);
                capsuleH = MathF.Max(radius, _capsule.Height * 0.5f);
            }

            const float stepUp = Rigidbody.PlanetWalkStepUp;
            float neighborhood = GetNeighborhoodRadius(planet, up);
            float probeDist = MathF.Min(capsuleH + stepUp + Rigidbody.PlanetWalkGroundSnap, neighborhood);

            if (wish.LengthSquared() > 1e-8f)
            {
                wish = SN.Vector3.Normalize(wish);
                float altBefore = (pos - center).Length();
                pos += wish * speed * dt;
                var toNew = pos - center;
                float newDist = toNew.Length();
                if (newDist > 1e-6f)
                    pos = center + (toNew / newDist) * altBefore;
                up = SN.Vector3.Normalize(pos - center);
            }

            float crustR = SampleCollisionRadiusCached(planet, up);
            UpdateSurfaceMode(planet, pos, crustR);
            var hit = default(PlanetDensityHit);
            bool densityHit = false;
            bool onContact = false;
            var standNormal = up;

            if (_surfaceMode && !_airborne)
            {
                float stand = crustR + capsuleH;
                pos = center + up * stand;
                onContact = true;
                hit.Point = center + up * crustR;
                hit.Normal = up;
            }
            else if (!_surfaceMode)
            {
                planet.ResolveDensityPenetration(ref pos, radius, 4);
                RefreshRadialUp(pos, center, ref up);
                densityHit = Rigidbody.TryDensityGroundProbe(
                    planet, pos, up, radius, probeDist, out hit);

                bool standable = densityHit && !hit.StartedInside;
                if (standable)
                {
                    var n = hit.Normal.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(hit.Normal) : up;
                    float minSlope = MathF.Cos(55f * (MathF.PI / 180f));
                    if (SN.Vector3.Dot(n, up) < minSlope * 0.5f)
                        standable = false;
                    else
                        standNormal = n;
                }

                float feetDiff = float.PositiveInfinity;
                if (standable)
                    feetDiff = SN.Vector3.Dot((pos - up * capsuleH) - hit.Point, up);
                onContact = standable && feetDiff >= -0.02f && feetDiff <= stepUp + 0.02f;
            }

            if (!_airborne && _jumpBuf > 0f && onContact)
            {
                _airborne = true;
                _verticalVel = JumpImpulse;
                _jumpBuf = 0f;
                onContact = false;
            }

            if (_airborne)
            {
                _verticalVel -= 9.81f * dt;
                pos += up * (_verticalVel * dt);
                RefreshRadialUp(pos, center, ref up);

                crustR = SampleCollisionRadiusCached(planet, up);
                UpdateSurfaceMode(planet, pos, crustR);
                float stand = crustR + capsuleH;
                float dist = (pos - center).Length();

                if (_surfaceMode && _verticalVel <= 0f && dist <= stand + 0.02f)
                {
                    pos = center + up * stand;
                    _airborne = false;
                    _verticalVel = 0f;
                    onContact = true;
                    standNormal = up;
                }
                else if (!_surfaceMode)
                {
                    bool wasInside = planet.ResolveDensityPenetration(ref pos, radius, 4);
                    RefreshRadialUp(pos, center, ref up);

                    var landStart = pos + up * 0.02f;
                    float landProbe = MathF.Min(capsuleH + 0.05f, neighborhood);
                    bool landHit = false;
                    PlanetDensityHit land = default;
                    if (!wasInside)
                    {
                        if (planet.SpherecastGameplay(landStart, -up, radius * 0.25f, landProbe, out land))
                            landHit = true;
                        else if (planet.RaycastDensityGameplay(landStart, -up, landProbe, out land))
                            landHit = true;
                    }
                    if (landHit && !land.StartedInside && land.Distance < capsuleH)
                    {
                        pos = land.Point + up * capsuleH;
                        if (_verticalVel < 0f) _verticalVel = 0f;
                        _airborne = false;
                        onContact = true;
                        standNormal = land.Normal.LengthSquared() > 1e-8f
                            ? SN.Vector3.Normalize(land.Normal)
                            : up;
                    }
                }
            }
            else if (onContact)
            {
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else if (!_surfaceMode && densityHit && hit.StartedInside)
            {
                planet.ResolveDensityPenetration(ref pos, radius, 4);
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else if (!_surfaceMode && densityHit)
            {
                _airborne = true;
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else if (_surfaceMode)
            {
                pos = center + up * (crustR + capsuleH);
                onContact = true;
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
            else
            {
                _airborne = true;
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }

            var tan = wish.LengthSquared() > 1e-8f ? wish * speed : SN.Vector3.Zero;
            if (_rb != null)
            {
                _rb.Velocity = tan + up * _verticalVel;
                _rb.ApplyPlayerPlanetStand(!_airborne && onContact, standNormal, up);
            }

            Transform.Position = new Vector3(pos.X, pos.Y, pos.Z);
        }

        static bool TryQueryPlanetWater(
            PlanetTerrain planet,
            SN.Vector3 worldPos,
            SN.Vector3 center,
            out SN.Vector3 up,
            out float bodyDist,
            out float waterSurfaceR,
            out float crustR,
            out PlanetWaterSurfaceSample waterSample)
        {
            up = SN.Vector3.UnitY;
            bodyDist = waterSurfaceR = crustR = 0f;
            waterSample = PlanetWaterSurfaceSample.Empty;

            var toBody = worldPos - center;
            bodyDist = toBody.Length();
            if (bodyDist < 1e-6f)
                return false;

            up = toBody / bodyDist;
            return planet.TryGetWaterColumn(up, bodyDist, out waterSurfaceR, out crustR, out waterSample);
        }

        static UnderwaterState BuildPlanetWaterFeel(PlanetTerrain planet, float depth)
        {
            var b = planet.OceanBiome;
            return new UnderwaterState
            {
                Depth = MathF.Max(0.05f, depth),
                Tint = new SN.Vector3(b.UnderwaterTintR, b.UnderwaterTintG, b.UnderwaterTintB),
                FogDensity = b.UnderwaterFogDensity,
                CausticStrength = b.UnderwaterCausticStrength,
                Distortion = b.UnderwaterDistortion,
                Buoyancy = b.UnderwaterBuoyancy,
                Drag = b.UnderwaterDrag
            };
        }

        void SwimOnPlanet(
            float dt,
            PlanetTerrain planet,
            SN.Vector3 center,
            SN.Vector3 up,
            float dist,
            float waterSurfaceR,
            float crustR,
            PlanetWaterSurfaceSample waterSample,
            bool diving)
        {
            _ = waterSample;
            _rb ??= GetComponent<Rigidbody>();
            _capsule ??= GetComponent<CapsuleCollider>();
            IsPlanetSwimming = true;

            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);

            float capsuleH = 1f;
            float radius = 0.4f;
            if (_capsule != null)
            {
                radius = MathF.Max(0.05f, _capsule.Radius);
                capsuleH = MathF.Max(radius, _capsule.Height * 0.5f);
            }

            float depth = waterSurfaceR - dist;
            var uw = BuildPlanetWaterFeel(planet, MathF.Max(0.05f, depth));

            // Chest on the water line, head above — default rest pose.
            float surfaceFloatR = waterSurfaceR - MathF.Max(0.12f, capsuleH * 0.22f);

            float yawRad = Deg2Rad(_yawDeg);
            var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            BuildTangentBasis(up, yawForward, out var fwd, out var right);

            float pitchRad = Deg2Rad(_pitchDeg);
            float cp = MathF.Cos(pitchRad), sp = MathF.Sin(pitchRad);
            var lookFwd = fwd * cp + up * sp;
            if (lookFwd.LengthSquared() > 1e-8f)
                lookFwd = SN.Vector3.Normalize(lookFwd);

            // Space always wins over Ctrl so you can surface while diving.
            bool wantsDive = diving && !_jumpHeld;
            bool wantsSurface = _jumpHeld || _jumpBuf > 0f;

            var wish = SN.Vector3.Zero;
            if (_wishLocal.LengthSquared() > 1e-6f)
            {
                wish = lookFwd * (-_wishLocal.Y) + right * _wishLocal.X;
                if (!wantsDive)
                    wish -= up * SN.Vector3.Dot(wish, up);
                if (wish.LengthSquared() > 1e-8f)
                    wish = SN.Vector3.Normalize(wish);
            }

            float swimSpeed = (wantsDive ? SwimMaxSpeed : PlanetSurfaceSwimSpeed) * (_sprintHeld ? SprintMultiplier : 1f);
            if (wish.LengthSquared() > 1e-8f)
            {
                float accel = SwimForce * dt;
                _swimPlanarVel += wish * accel;
                float planarSpeed = _swimPlanarVel.Length();
                if (planarSpeed > swimSpeed)
                    _swimPlanarVel = _swimPlanarVel * (swimSpeed / planarSpeed);
            }
            else
            {
                _swimPlanarVel *= MathF.Max(0f, 1f - SwimDrag * 0.65f * dt);
            }

            if (_swimPlanarVel.LengthSquared() > 1e-8f)
            {
                var move = _swimPlanarVel * dt;
                pos += move;
                float alt = (pos - center).Length();
                if (alt > 1e-6f)
                {
                    var movedDir = SN.Vector3.Normalize(pos - center);
                    pos = center + movedDir * alt;
                }
            }

            RefreshRadialUp(pos, center, ref up);
            dist = (pos - center).Length();
            depth = waterSurfaceR - dist;

            float buoyancy = MathF.Max(1f, uw.Buoyancy);
            float targetR = dist;
            float springK = 0f;
            if (wantsSurface)
            {
                targetR = surfaceFloatR;
                springK = 18f * buoyancy;
                _swimRadialVel += SwimVerticalSpeed * 1.35f * dt;
            }
            else if (wantsDive)
            {
                targetR = dist;
                springK = 0f;
                _swimRadialVel -= SwimVerticalSpeed * 1.35f * dt;
            }
            else if (depth <= 1.15f)
            {
                // Already at the surface: stay lying on the water.
                // Releasing Ctrl while deep does not pull you up — only Space does.
                targetR = surfaceFloatR;
                springK = 14f * buoyancy;
            }
            else
            {
                targetR = dist;
                springK = 0f;
                _swimRadialVel *= MathF.Max(0f, 1f - SwimDrag * 1.8f * dt);
            }

            _planetWasDiving = wantsDive;

            float radialError = targetR - dist;
            if (springK > 0f)
                _swimRadialVel += radialError * springK * dt;
            _swimRadialVel *= MathF.Max(0f, 1f - SwimDrag * (wantsDive ? 0.28f : 0.55f) * dt);
            if (!wantsDive && !wantsSurface)
                _swimRadialVel = Math.Clamp(_swimRadialVel, -6f, 8f);

            pos += up * (_swimRadialVel * dt);
            RefreshRadialUp(pos, center, ref up);
            dist = (pos - center).Length();
            depth = waterSurfaceR - dist;

            float bedR = crustR + radius;
            float minR = bedR;
            float maxR = wantsDive ? waterSurfaceR + 0.35f : waterSurfaceR + 1.35f;
            float clampedDist = Math.Clamp(dist, minR, maxR);
            if (MathF.Abs(clampedDist - dist) > 1e-4f)
            {
                pos = center + up * clampedDist;
                dist = clampedDist;
                depth = waterSurfaceR - dist;
                if (clampedDist <= minR + 0.05f)
                    _swimRadialVel = MathF.Max(0f, _swimRadialVel);
                if (!wantsDive && clampedDist >= maxR - 0.05f)
                    _swimRadialVel = MathF.Min(0f, _swimRadialVel * 0.25f);
            }

            // Head vs the water table. Surface swim keeps the chest on the
            // waterline and the eyes above — do not count Ctrl or body depth.
            float eyeR = dist + MathF.Max((float)FirstPersonOffset.Y * 0.92f, capsuleH * 0.75f);
            if (_cam is { UseLookOverride: true } && (_cam.LookEye - center).LengthSquared() > 1f)
                eyeR = (_cam.LookEye - center).Length();
            float eyeDepth = waterSurfaceR - eyeR;
            if (eyeDepth >= 0.30f)
                _planetSubmergedLatch = true;
            else if (eyeDepth <= 0.10f)
                _planetSubmergedLatch = false;

            IsPlanetSubmerged = _planetSubmergedLatch;
            PlanetSubmergeDepth = MathF.Max(0f, eyeDepth);

            _airborne = false;
            _verticalVel = _swimRadialVel;
            if (wantsSurface) _jumpBuf = 0f;
            else _jumpBuf = Math.Max(0f, _jumpBuf - dt);

            bool underwaterBody = wantsDive || depth > 1.35f;
            var poseFwd = _swimPlanarVel.LengthSquared() > 0.25f ? _swimPlanarVel : wish;
            if (poseFwd.LengthSquared() < 1e-8f)
                poseFwd = fwd;
            poseFwd -= up * SN.Vector3.Dot(poseFwd, up);
            if (underwaterBody && wish.LengthSquared() > 1e-8f)
                poseFwd = wish;
            if (poseFwd.LengthSquared() > 1e-8f)
                ApplyPlanetSwimPose(up, poseFwd, underwaterBody ? up : -up);
            else
                ApplyPlanetSwimPose(up, fwd, underwaterBody ? up : -up);

            if (_rb != null)
            {
                var vel = _swimPlanarVel + up * _swimRadialVel;
                _rb.Velocity = vel;
                _rb.ApplyPlayerPlanetStand(false, up, up);
            }

            Transform.Position = new Vector3(pos.X, pos.Y, pos.Z);
        }

        float SampleCollisionRadiusCached(PlanetTerrain planet, SN.Vector3 dir)
        {
            float now = MathF.Max(Time.time, Time.fixedTime);
            if (now - _collisionCacheTime < 1f / 60f && SN.Vector3.Dot(_collisionCacheDir, dir) > 0.9995f)
                return _collisionCacheR;
            _collisionCacheTime = now;
            _collisionCacheDir = dir;
            _collisionCacheR = planet.SampleCollisionRadius(dir);
            return _collisionCacheR;
        }

        static void RefreshRadialUp(SN.Vector3 pos, SN.Vector3 center, ref SN.Vector3 up)
        {
            var radial = pos - center;
            if (radial.LengthSquared() > 1e-8f)
                up = SN.Vector3.Normalize(radial);
        }

        public override void FixedUpdate()
        {
            _rb ??= GetComponent<Rigidbody>();
            if (_rb == null) return;

            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) dt = 0.02f;
            if (dt > 0.25f) dt = 0.25f;

            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var planet = BindPlanet(pos);
            if (planet != null)
            {
                var radial = pos - _planetCenter;
                var planetUp = radial.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(radial) : SN.Vector3.UnitY;

                if (TryQueryPlanetWater(planet, pos, _planetCenter,
                        out var waterUp, out float bodyDist, out float waterSurfaceR, out float crustR, out var waterSample))
                {
                    bool wantsDive = _diveHeld && !_jumpHeld;

                    bool wasInWater = _planetInWater;
                    float wadeDepth = waterSurfaceR - bodyDist;
                    bool inWater = true;
                    if (wasInWater && (wadeDepth < -1.6f || crustR > waterSurfaceR + 1.4f))
                        inWater = false;

                    if (inWater)
                    {
                        if (!wasInWater)
                        {
                            _swimRadialVel = 0f;
                            _swimPlanarVel = SN.Vector3.Zero;
                            _verticalVel = 0f;
                            _airborne = false;
                            _jumpBuf = 0f;
                        }
                        _planetInWater = true;
                        _planetDiving = wantsDive;
                        SwimOnPlanet(dt, planet, _planetCenter, waterUp, bodyDist, waterSurfaceR, crustR, waterSample, _planetDiving);
                    }
                    else
                    {
                        _planetDiving = false;
                        IsPlanetSwimming = false;
                        IsPlanetSubmerged = false;
                        PlanetSubmergeDepth = 0f;
                        _planetSubmergedLatch = false;
                        _swimPlanarVel = SN.Vector3.Zero;
                        _swimRadialVel *= MathF.Max(0f, 1f - dt * 5f);
                        WalkOnPlanetSurface(dt, planetUp, planet, _planetCenter);
                    }
                }
                else
                {
                    _planetInWater = false;
                    _planetDiving = false;
                    IsPlanetSwimming = false;
                    IsPlanetSubmerged = false;
                    PlanetSubmergeDepth = 0f;
                    _planetSubmergedLatch = false;
                    _swimPlanarVel = SN.Vector3.Zero;
                    _swimRadialVel *= MathF.Max(0f, 1f - dt * 4f);
                    WalkOnPlanetSurface(dt, planetUp, planet, _planetCenter);
                }
                return;
            }

            bool grounded = _rb.IsGrounded;
            bool underwater = _rb.IsUnderwater && !grounded;

            if (underwater && !_airborne)
            {
                FixedUpdateSwimming(dt);
                return;
            }

            // ── Flat-world physics ──
            float r = Deg2Rad(_yawDeg);
            float c = MathF.Cos(r), s = MathF.Sin(r);
            var wishWorld = new SN.Vector3(
                _wishLocal.X * c + _wishLocal.Y * s,
                0f,
                -_wishLocal.X * s + _wishLocal.Y * c);

            float speed = MoveForce * (_sprintHeld ? SprintMultiplier : 1f);
            float control = grounded ? 1f : AirControlFactor;
            var horizVel = new SN.Vector3(_rb.Velocity.X, 0, _rb.Velocity.Z);
            float currentSpeed = SN.Vector3.Dot(horizVel, wishWorld);
            float maxSpd = MaxSpeed * (_sprintHeld ? SprintMultiplier : 1f);

            if (wishWorld.LengthSquared() > 1e-6f)
            {
                float addSpeed = maxSpd - currentSpeed;
                if (addSpeed > 0f)
                {
                    float accel = MathF.Min(speed * control * dt, addSpeed);
                    _rb.AddForce(wishWorld * accel / MathF.Max(dt, 0.001f));
                }
            }

            float dragFactor = MathF.Max(0f, 1f - (grounded ? GroundDrag : AirDrag) * dt);
            var vel = _rb.Velocity;
            _rb.Velocity = new SN.Vector3(vel.X * dragFactor, vel.Y, vel.Z * dragFactor);

            if (_jumpBuf > 0f && grounded)
            {
                var vel2 = _rb.Velocity;
                _rb.Velocity = new SN.Vector3(vel2.X, MathF.Max(vel2.Y, 0f), vel2.Z);
                _rb.AddImpulse(SN.Vector3.UnitY * JumpImpulse);
                _jumpBuf = 0f;
            }
            else
            {
                _jumpBuf = Math.Max(0f, _jumpBuf - dt);
            }
        }

        /// <summary>
        /// Swimming physics: 3D movement following camera direction.
        /// </summary>
        void FixedUpdateSwimming(float dt)
        {
            if (_rb == null) return;

            float yawRad = Deg2Rad(_yawDeg);
            float pitchRad = Deg2Rad(_pitchDeg);

            float cosPitch = MathF.Cos(pitchRad);
            float sinPitch = MathF.Sin(pitchRad);
            float cosYaw = MathF.Cos(yawRad);
            float sinYaw = MathF.Sin(yawRad);

            var forward = new SN.Vector3(
                sinYaw * cosPitch,
                -sinPitch,
                cosYaw * cosPitch);
            if (forward.LengthSquared() > 1e-6f)
                forward = SN.Vector3.Normalize(forward);

            var right = new SN.Vector3(cosYaw, 0f, -sinYaw);

            var wishDir = SN.Vector3.Zero;
            if (_wishLocal.LengthSquared() > 1e-6f)
            {
                wishDir = right * _wishLocal.X + forward * _wishLocal.Y;
                if (wishDir.LengthSquared() > 1e-6f)
                    wishDir = SN.Vector3.Normalize(wishDir);
            }

            bool jumpHeld = GEInput.GetAction("Jump");
            if (jumpHeld)
                wishDir += SN.Vector3.UnitY * SwimVerticalSpeed * 0.5f;

            float swimSpeed = SwimForce * (_sprintHeld ? SprintMultiplier : 1f);
            float maxSwimSpd = SwimMaxSpeed * (_sprintHeld ? SprintMultiplier : 1f);

            var vel = _rb.Velocity;
            if (wishDir.LengthSquared() > 1e-6f)
            {
                wishDir = SN.Vector3.Normalize(wishDir);
                float currentSpeed = SN.Vector3.Dot(vel, wishDir);
                float addSpeed = maxSwimSpd - currentSpeed;
                if (addSpeed > 0f)
                    vel += wishDir * MathF.Min(swimSpeed * dt, addSpeed);
            }

            float dragFactor = MathF.Max(0f, 1f - SwimDrag * dt);
            vel *= dragFactor;

            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            pos += vel * dt;
            _rb.Velocity = vel;
            Transform.Position = new Vector3(pos.X, pos.Y, pos.Z);

            _jumpBuf = 0f;
        }

        // ── Camera helpers ──

        void DriveCameraFirstPerson(SN.Vector3 localUp, PlanetTerrain? planet)
        {
            var pos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            float yawRad = Deg2Rad(_yawDeg);
            var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            BuildTangentBasis(localUp, yawForward, out var fwd, out var right);

            var off = new SN.Vector3((float)FirstPersonOffset.X, (float)FirstPersonOffset.Y, (float)FirstPersonOffset.Z);
            float pitchRad = Deg2Rad(_pitchDeg);
            float cp = MathF.Cos(pitchRad), sp = MathF.Sin(pitchRad);
            var lookFwd = fwd * cp + localUp * sp;
            if (lookFwd.LengthSquared() > 1e-8f)
                lookFwd = SN.Vector3.Normalize(lookFwd);
            var eye = pos + right * off.X + localUp * off.Y + fwd * off.Z;
            if (AvoidCameraGroundClip)
                eye = ResolveFirstPersonEye(eye, pos, localUp, fwd, lookFwd, planet);

            bool nested = _cam.gameObject?.Parent == gameObject;
            if (nested)
            {
                var d = eye - pos;
                _camTr!.Position = new Vector3(
                    SN.Vector3.Dot(d, right),
                    SN.Vector3.Dot(d, localUp),
                    SN.Vector3.Dot(d, fwd));
                var cr = _camTr.Rotation;
                cr.X = _pitchDeg;
                cr.Y = 0;
                cr.Z = 0;
                _camTr.Rotation = cr;
            }
            else
            {
                _camTr!.Position = new Vector3(eye.X, eye.Y, eye.Z);
                var cr = _camTr.Rotation;
                cr.X = _pitchDeg;
                cr.Y = _yawDeg;
                cr.Z = 0;
                _camTr.Rotation = cr;
            }

            _cam.WorldUp = localUp;
            _cam.UseLookOverride = true;
            _cam.LookEye = eye;
            _cam.LookForward = lookFwd;
            _cam.LookUp = localUp;
        }

        void DriveCameraThirdPerson(float dt, SN.Vector3 localUp)
        {
            float yawRad = Deg2Rad(_yawDeg);
            var yawForward = new SN.Vector3(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            BuildTangentBasis(localUp, yawForward, out var fwd, out var right);

            var off = new SN.Vector3((float)ThirdPersonOffset.X, (float)ThirdPersonOffset.Y, (float)ThirdPersonOffset.Z);
            var desired = right * off.X + localUp * off.Y + (-fwd) * MathF.Abs(off.Z);

            var target = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var lookAt = target + localUp * (float)FirstPersonOffset.Y;
            var desiredPos = target + desired;
            if (IsPlanetSwimming && !_planetDiving && _planet != null)
            {
                var sample = _planet.SampleWaterSurface(localUp);
                float waterR = sample.Mask >= 0.04f ? sample.Radius * _planet.GetWorldRadiusScale() : 0f;
                float bodyR = SN.Vector3.Dot(target - _planetCenter, localUp);
                if (waterR > 1f && bodyR > waterR - 1.25f)
                    desiredPos = LiftEyeAboveWater(desiredPos, localUp, _planet);
            }
            if (AvoidCameraGroundClip)
                desiredPos = ResolveThirdPersonCameraObstruction(lookAt, desiredPos);

            if (CameraFollowLerp <= 0f)
            {
                _camTr!.Position = new Vector3(desiredPos.X, desiredPos.Y, desiredPos.Z);
            }
            else
            {
                var cur = new SN.Vector3((float)_camTr!.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
                var t = 1f - (float)Math.Exp(-CameraFollowLerp * dt);
                var blended = cur + (desiredPos - cur) * t;
                _camTr.Position = new Vector3(blended.X, blended.Y, blended.Z);
            }

            var dir = lookAt - new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
            if (dir.LengthSquared() > 1e-6f)
                dir = SN.Vector3.Normalize(dir);
            else
                dir = fwd;

            _cam.WorldUp = localUp;
            _cam.UseLookOverride = true;
            _cam.LookEye = new SN.Vector3((float)_camTr.Position.X, (float)_camTr.Position.Y, (float)_camTr.Position.Z);
            _cam.LookForward = dir;
            _cam.LookUp = localUp;
        }

        // ── Helpers ──
        static float Deg2Rad(float d) => (float)(Math.PI / 180.0) * d;
        static float Normalize180(float a)
        {
            while (a > 180f) a -= 360f;
            while (a < -180f) a += 360f;
            return a;
        }
        static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
        {
            float lenSq = v.LengthSquared();
            if (lenSq <= 1e-10f) return fallback;
            return v / MathF.Sqrt(lenSq);
        }

        void BuildTangentBasis(SN.Vector3 localUp, SN.Vector3 preferredForward, out SN.Vector3 forward, out SN.Vector3 right)
        {
            localUp = SafeNormalize(localUp, SN.Vector3.UnitY);
            forward = preferredForward - localUp * SN.Vector3.Dot(preferredForward, localUp);
            if (forward.LengthSquared() <= 1e-8f)
                forward = _lastMoveForward - localUp * SN.Vector3.Dot(_lastMoveForward, localUp);
            if (forward.LengthSquared() <= 1e-8f)
            {
                var seed = MathF.Abs(localUp.Y) < 0.95f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
                forward = SN.Vector3.Cross(seed, localUp);
            }
            forward = SafeNormalize(forward, SN.Vector3.UnitZ);

            right = SN.Vector3.Cross(forward, localUp);
            if (right.LengthSquared() <= 1e-8f)
                right = SN.Vector3.Cross(MathF.Abs(localUp.Y) < 0.95f ? SN.Vector3.UnitY : SN.Vector3.UnitX, localUp);
            right = SafeNormalize(right, SN.Vector3.UnitX);

            forward = SafeNormalize(SN.Vector3.Cross(localUp, right), forward);
            _lastMoveForward = forward;
        }

        SN.Vector3 LiftEyeAboveWater(SN.Vector3 eye, SN.Vector3 up, PlanetTerrain planet)
        {
            up = SafeNormalize(up, SN.Vector3.UnitY);
            var sample = planet.SampleWaterSurface(up);
            if (sample.Mask < 0.04f || sample.Kind == PlanetWaterKind.Lava)
                return eye;

            float waterR = sample.Radius * planet.GetWorldRadiusScale();
            if (waterR < 1f)
                return eye;

            // Near-surface waves are ~0.08; keep the near plane above the mesh.
            float minEyeR = waterR + 0.35f;
            float radial = SN.Vector3.Dot(eye - _planetCenter, up);
            if (radial >= minEyeR)
                return eye;

            var tangent = eye - _planetCenter - up * radial;
            return _planetCenter + up * minEyeR + tangent;
        }

        /// <summary>
        /// Camera only. Body stay on the heightfield stand. The visible transvoxel
        /// crust sits above that field on slopes, so the authored eye lands in dirt
        /// and the near plane punches through — which also wrecks water / TAA.
        /// </summary>
        SN.Vector3 ResolveFirstPersonEye(
            SN.Vector3 eye,
            SN.Vector3 bodyPos,
            SN.Vector3 up,
            SN.Vector3 fwd,
            SN.Vector3 lookFwd,
            PlanetTerrain? planet)
        {
            if (planet == null)
                return eye;

            // Surface swim: never snap to the crust stand. That stand is the
            // seabed / slope, so the eye lands under the water table.
            if (IsPlanetSwimming)
            {
                if (!_planetDiving)
                {
                    var sample = planet.SampleWaterSurface(up);
                    float waterR = sample.Mask >= 0.04f ? sample.Radius * planet.GetWorldRadiusScale() : 0f;
                    float bodyR = SN.Vector3.Dot(bodyPos - _planetCenter, up);
                    if (waterR > 1f && bodyR > waterR - 1.25f)
                        eye = LiftEyeAboveWater(eye, up, planet);
                }
                return eye;
            }

            // Surface mode: lift from stand grid + radial offset — no density loops.
            if (_surfaceMode)
            {
                float crustR = SampleCollisionRadiusCached(planet, up);
                float eyeHeight = MathF.Max(0.5f, (float)FirstPersonOffset.Y);
                float standEyeR = crustR + eyeHeight;
                float radial = SN.Vector3.Dot(eye - _planetCenter, up);
                if (radial < standEyeR)
                    eye = _planetCenter + up * standEyeR;
                else if (radial > standEyeR + 0.35f && !_airborne)
                    eye = _planetCenter + up * standEyeR + (eye - _planetCenter - up * radial);
                return eye;
            }

            const float clearance = 0.16f;
            const float maxLift = 1.6f;
            float lifted = 0f;
            bool disturbed = false;

            if (_rb != null && _rb.IsGrounded)
            {
                var gn = SafeNormalize(_rb.GroundNormal, up);
                float steep = Math.Clamp((0.92f - SN.Vector3.Dot(gn, up)) / 0.92f, 0f, 1f);
                if (steep > 0f)
                {
                    float extra = steep * 0.45f;
                    eye += up * extra;
                    lifted += extra;
                }
            }

            float probe = _cam != null
                ? MathF.Max(0.22f, _cam.Near + MathF.Max(0.12f, CameraCollisionPadding))
                : 0.28f;
            var probePt = eye + lookFwd * probe;
            if (!planet.TrySampleWorldDensity(eye, out float dEye) || dEye >= clearance)
            {
                if (!planet.TrySampleWorldDensity(probePt, out float dAir) || dAir >= clearance)
                    return eye;
            }

            var e = eye;
            if (planet.ResolveDensityPenetration(ref e, clearance, 4))
            {
                eye = e;
                disturbed = true;
            }

            if (planet.TrySampleWorldDensity(eye, out dEye) && dEye < clearance)
            {
                for (int i = 0; i < 10 && lifted < maxLift; i++)
                {
                    eye += up * 0.12f;
                    lifted += 0.12f;
                    disturbed = true;
                    if (!planet.TrySampleWorldDensity(eye, out dEye) || dEye >= clearance)
                        break;
                }
            }

            probePt = eye + lookFwd * probe;
            if (planet.TrySampleWorldDensity(probePt, out float dProbe) && dProbe < clearance)
            {
                for (int i = 0; i < 10 && lifted < maxLift; i++)
                {
                    eye += up * 0.1f;
                    lifted += 0.1f;
                    disturbed = true;
                    probePt = eye + lookFwd * probe;
                    if (!planet.TrySampleWorldDensity(probePt, out dProbe) || dProbe >= clearance)
                        break;
                }

                if (dProbe < clearance)
                {
                    eye -= fwd * 0.22f;
                    disturbed = true;
                    planet.ResolveDensityPenetration(ref eye, clearance, 4);
                }
            }

            if (disturbed && _cam != null)
                _cam.InvalidateTemporalHistory = true;

            return eye;
        }

        SN.Vector3 ResolveThirdPersonCameraObstruction(SN.Vector3 anchor, SN.Vector3 desiredPos)
        {
            var toCam = desiredPos - anchor;
            float dist = toCam.Length();
            if (dist <= 1e-4f) return desiredPos;

            var dir = toCam / dist;
            float startOffset = MathF.Max(0f, CameraCollisionStartOffset);
            var rayOrigin = anchor + dir * startOffset;
            float maxDist = MathF.Max(0f, dist - startOffset);
            if (maxDist <= 1e-4f) return desiredPos;

            var hits = GEPhysics.RaycastAll(rayOrigin, dir, maxDist);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < hits.Count; i++)
            {
                var c = hits[i].Collider;
                var hitGo = c?.gameObject;
                if (hitGo == null) continue;
                if (IsSelfOrDescendant(hitGo)) continue;
                if (hits[i].Distance < nearest) nearest = hits[i].Distance;
            }

            if (float.IsPositiveInfinity(nearest)) return desiredPos;

            float pad = MathF.Max(0.02f, CameraCollisionPadding);
            float safeDist = MathF.Max(0.05f, nearest - pad);
            return rayOrigin + dir * safeDist;
        }

        bool IsSelfOrDescendant(GameObject go)
        {
            if (gameObject == null) return false;
            for (GameObject? cur = go; cur != null; cur = cur.Parent)
            {
                if (ReferenceEquals(cur, gameObject))
                    return true;
            }
            return false;
        }
    }
}
