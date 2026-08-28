#nullable enable
using System;
using System.Linq;
using Game_Engine.Core;
using Game_Engine.Core.Planet;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Spawns a RigidbodyPlayer (Rigidbody + CapsuleCollider + camera) on PlanetTerrain.
    /// Add this to the planet or any scene object. Press Play — the player appears on the crust.
    /// </summary>
    [ComponentCategory("Gameplay")]
    public sealed class PlanetPlayerSpawner : Behavior
    {
        [Persist] public string PlayerObjectName { get; set; } = "Player";
        [Persist] public bool SpawnOnStart { get; set; } = true;
        [Persist] public bool ReuseExistingPlayer { get; set; } = true;
        [Persist] public bool CreateBodyMesh { get; set; } = true;
        [Persist] public bool CreateCameraChild { get; set; } = true;
        [Persist] public bool EnsurePlanetCollider { get; set; } = true;
        [Persist] public bool EnsureSunLight { get; set; } = true;

        [Persist] public bool UseLatitudeLongitude { get; set; } = true;
        [Persist] public float LatitudeDegrees { get; set; } = 18f;
        [Persist] public float LongitudeDegrees { get; set; } = 12f;
        [Persist] public Vector3 SpawnDirection { get; set; } = new Vector3(0, 1, 0);

        [Persist] public float ExtraHeight { get; set; } = 0.25f;
        [Persist] public float CapsuleHeight { get; set; } = 2f;
        [Persist] public float CapsuleRadius { get; set; } = 0.4f;
        [Persist] public bool FirstPerson { get; set; } = true;
        [Persist] public bool AttachPlanetTool { get; set; } = true;

        GameObject? _spawned;
        bool _awaitingSpawn;
        float _spawnWaitSec;
        const float SpawnRetryTimeoutSec = 12f;

        public GameObject? SpawnedPlayer => _spawned;

        public override void Start()
        {
            if (!SpawnOnStart)
                return;
            if (!TrySpawnNow(requireRenderableLeaf: true))
                _awaitingSpawn = true;
        }

        public override void Update()
        {
            if (_spawned != null || !_awaitingSpawn)
                return;
            _spawnWaitSec += Math.Max(0f, Time.deltaTime);
            bool requireLeaf = _spawnWaitSec < SpawnRetryTimeoutSec;
            if (TrySpawnNow(requireLeaf))
                _awaitingSpawn = false;
        }

        public bool SpawnPlayer() => TrySpawnNow(requireRenderableLeaf: false);

        bool TrySpawnNow(bool requireRenderableLeaf)
        {
            var planet = ResolvePlanet();
            if (planet == null || planet.Config == null)
                return false;

            if (requireRenderableLeaf && planet.ActiveChunkCount <= 0)
                return false;

            if (EnsurePlanetCollider)
                EnsureCollider(planet);
            if (EnsureSunLight)
                EnsureLight();

            if (!TryFindSurface(planet, out var worldPos, out var up, allowFallback: !requireRenderableLeaf))
                return false;

            float halfH = MathF.Max(CapsuleHeight, CapsuleRadius * 2f) * 0.5f;
            worldPos += up * (halfH + MathF.Max(0f, ExtraHeight));

            var player = ResolvePlayerObject();
            EnsurePlayerSetup(player, planet);
            PlacePlayer(player, worldPos, up, planet);
            _spawned = player;
            SceneService.NotifyChanged();
            return true;
        }

        PlanetTerrain? ResolvePlanet()
        {
            var onSelf = gameObject?.Behaviors.OfType<PlanetTerrain>().FirstOrDefault();
            if (onSelf != null && onSelf.Enabled)
                return onSelf;

            var probe = new SN.Vector3(
                (float)Transform.Position.X,
                (float)Transform.Position.Y,
                (float)Transform.Position.Z);
            var nearest = Rigidbody.FindNearestPlanet(probe, out _, out _);
            if (nearest != null)
                return nearest;

            for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
            {
                var p = PlanetTerrain.ActivePlanets[i];
                if (p != null && p.IsActiveAndEnabled)
                    return p;
            }

            return SceneQuery.FindBehaviors<PlanetTerrain>().FirstOrDefault();
        }

        static void EnsureCollider(PlanetTerrain planet)
        {
            var go = planet.gameObject;
            if (go == null) return;
            if (go.Behaviors.OfType<PlanetCollider>().Any())
                return;
            go.AddBehavior<PlanetCollider>();
        }

        static void EnsureLight()
        {
            if (SceneQuery.FindBehaviors<Light>().Any())
                return;
            var sun = new GameObject("Sun");
            var light = sun.AddBehavior<Light>();
            light.Type = LightType.Directional;
            light.Intensity = 1.15f;
            sun.Transform.Rotation = new Vector3(125, 35, 0);
            SceneService.Add(sun);
        }

        bool TryFindSurface(PlanetTerrain planet, out SN.Vector3 worldPos, out SN.Vector3 up, bool allowFallback)
        {
            worldPos = SN.Vector3.Zero;
            up = SN.Vector3.UnitY;
            var center = planet.GetWorldCenter();
            float sea = 0f;
            if (planet.Config != null)
                sea = planet.Config.SeaLevel * planet.GetWorldRadiusScale();

            if (TrySampleDir(planet, SpawnSphereDir(), sea, preferLand: true, out worldPos, out up))
                return true;

            // Default lat/lon is often ocean. Walk the globe for crust above sea level.
            float bestR = -1f;
            SN.Vector3 bestPos = default;
            SN.Vector3 bestUp = SN.Vector3.UnitY;
            for (int i = 0; i < 36; i++)
            {
                float lat = 8f + (i % 6) * 12f;
                float lon = i * 47f;
                float latR = lat * (MathF.PI / 180f);
                float lonR = lon * (MathF.PI / 180f);
                float cl = MathF.Cos(latR);
                var dir = SN.Vector3.Normalize(new SN.Vector3(cl * MathF.Sin(lonR), MathF.Sin(latR), cl * MathF.Cos(lonR)));
                if (!TrySampleDir(planet, dir, sea, preferLand: false, out var p, out var n))
                    continue;
                float r = (p - center).Length();
                if (r <= sea + 4f)
                    continue;
                if (r > bestR)
                {
                    bestR = r;
                    bestPos = p;
                    bestUp = n;
                }
            }

            if (bestR > 0f)
            {
                worldPos = bestPos;
                up = bestUp;
                return true;
            }

            var fallbackDir = SpawnSphereDir();
            if (TrySampleDir(planet, fallbackDir, sea, preferLand: false, out worldPos, out up))
            {
                float radial = (worldPos - center).Length();
                if (radial < sea + 2f)
                    worldPos = center + up * (sea + 8f);
                return true;
            }

            if (!allowFallback)
                return false;

            // Timeout only: density ray from far out. Never SampleHeightfieldRadius.
            float maxR = MathF.Max(10f, planet.Radius) * 4f;
            var origin = center + fallbackDir * maxR;
            if (planet.Raycast(origin, -fallbackDir, maxR * 2f, out PlanetDensityHit hit))
            {
                worldPos = hit.Point;
                up = hit.Normal.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(hit.Normal) : fallbackDir;
                if (SN.Vector3.Dot(up, fallbackDir) < 0.2f)
                    up = fallbackDir;
                return true;
            }

            worldPos = center + fallbackDir * MathF.Max(sea + 8f, planet.Radius);
            up = fallbackDir;
            return true;
        }

        static bool TrySampleDir(PlanetTerrain planet, SN.Vector3 dir, float worldSea, bool preferLand, out SN.Vector3 worldPos, out SN.Vector3 up)
        {
            worldPos = SN.Vector3.Zero;
            up = dir;
            var center = planet.GetWorldCenter();

            if (planet.TrySampleLocalIsosurface(dir, out var localPt, out var localN))
            {
                worldPos = planet.LocalToWorld(localPt);
                var nWorld = planet.LocalToWorld(localPt + localN) - worldPos;
                up = nWorld.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(nWorld) : dir;
            }
            else
            {
                float maxR = MathF.Max(10f, planet.Radius) * 4f;
                var origin = center + dir * maxR;
                if (planet.Raycast(origin, -dir, maxR * 2f, out PlanetDensityHit hit))
                {
                    worldPos = hit.Point;
                    up = hit.Normal.LengthSquared() > 1e-8f ? SN.Vector3.Normalize(hit.Normal) : dir;
                    if (SN.Vector3.Dot(up, dir) < 0f)
                        up = dir;
                }
                else
                {
                    return false;
                }
            }

            if (SN.Vector3.Dot(up, dir) < 0.2f)
                up = dir;

            float r = (worldPos - center).Length();
            if (preferLand && r < worldSea + 4f)
                return false;
            if (r < worldSea + 1f)
            {
                worldPos = center + dir * (worldSea + 6f);
                up = dir;
            }
            return true;
        }

        SN.Vector3 SpawnSphereDir()
        {
            if (UseLatitudeLongitude)
            {
                float lat = LatitudeDegrees * (MathF.PI / 180f);
                float lon = LongitudeDegrees * (MathF.PI / 180f);
                float cl = MathF.Cos(lat);
                var d = new SN.Vector3(cl * MathF.Sin(lon), MathF.Sin(lat), cl * MathF.Cos(lon));
                float len = d.Length();
                return len > 1e-8f ? d / len : SN.Vector3.UnitY;
            }

            var raw = new SN.Vector3(
                (float)SpawnDirection.X,
                (float)SpawnDirection.Y,
                (float)SpawnDirection.Z);
            float l = raw.Length();
            return l > 1e-8f ? raw / l : SN.Vector3.UnitY;
        }

        GameObject ResolvePlayerObject()
        {
            if (ReuseExistingPlayer)
            {
                if (_spawned?.Behaviors.OfType<RigidbodyPlayer>().Any() == true)
                    return _spawned;

                var byName = SceneQuery.FindByName(PlayerObjectName);
                if (byName != null)
                    return byName;

                var existing = SceneQuery.FindBehaviors<RigidbodyPlayer>().FirstOrDefault();
                if (existing?.gameObject != null)
                    return existing.gameObject;
            }

            return BuildPlayer();
        }

        /// <summary>
        /// Scene Player objects often have a capsule but no motor. Add RigidbodyPlayer
        /// before Awake/Start so WASD actually drives the body.
        /// </summary>
        public static void EnsurePlayModeControllers()
        {
            foreach (var rb in Rigidbody.All.ToList())
            {
                var go = rb.gameObject;
                if (go == null || !go.Enabled) continue;
                if (go.Behaviors.OfType<RigidbodyPlayer>().Any()) continue;

                bool hasCapsule = go.Behaviors.OfType<CapsuleCollider>().Any();
                if (!hasCapsule) continue;

                bool namedPlayer = string.Equals(go.Name, "Player", StringComparison.OrdinalIgnoreCase);
                bool hasCamera = go.Behaviors.OfType<Camera>().Any()
                    || go.Children.Any(c => c.Behaviors.OfType<Camera>().Any());
                if (!namedPlayer && !hasCamera) continue;

                FlattenCapsuleCenter(go);
                var motor = go.AddBehavior<RigidbodyPlayer>();
                motor.__Awake();
                motor.__Start();

                float far = 8000f;
                var nearest = Rigidbody.FindNearestPlanet(
                    new SN.Vector3((float)go.Transform.Position.X, (float)go.Transform.Position.Y, (float)go.Transform.Position.Z),
                    out _, out _);
                if (nearest != null)
                    far = MathF.Max(far, nearest.Radius * 8f);

                foreach (var cam in go.Behaviors.OfType<Camera>()
                             .Concat(go.Children.SelectMany(c => c.Behaviors.OfType<Camera>())))
                {
                    cam.IsMain = true;
                    cam.Far = MathF.Max(cam.Far, far);
                    cam.Near = MathF.Min(cam.Near, 0.08f);
                }
            }

            EnsurePlanetToolsOnPlayers();
        }

        /// <summary>Attach <see cref="PlanetTool"/> to every playable body.</summary>
        public static void EnsurePlanetToolsOnPlayers()
        {
            var seen = new HashSet<GameObject>();
            foreach (var motor in SceneQuery.FindBehaviors<RigidbodyPlayer>().ToList())
            {
                var go = motor.gameObject;
                if (go == null || !go.Enabled || !seen.Add(go)) continue;
                var planet = Rigidbody.FindNearestPlanet(
                    new SN.Vector3((float)go.Transform.Position.X, (float)go.Transform.Position.Y, (float)go.Transform.Position.Z),
                    out _, out _);
                EnsurePlanetTool(go, planet);
            }

            var named = SceneQuery.FindByName("Player");
            if (named != null && named.Enabled && seen.Add(named))
            {
                var planet = Rigidbody.FindNearestPlanet(
                    new SN.Vector3((float)named.Transform.Position.X, (float)named.Transform.Position.Y, (float)named.Transform.Position.Z),
                    out _, out _);
                EnsurePlanetTool(named, planet);
            }
        }

        static void EnsurePlanetTool(GameObject player, PlanetTerrain? planet)
        {
            var tool = player.Behaviors.OfType<PlanetTool>().FirstOrDefault();
            if (tool == null)
            {
                tool = player.AddBehavior<PlanetTool>();
                if (SceneService.PlayMode)
                {
                    tool.__Awake();
                    tool.__Start();
                }
            }
            if (planet != null)
                tool.BindPlanet(planet);
        }

        static void FlattenCapsuleCenter(GameObject player)
        {
            var capsule = player.Behaviors.OfType<CapsuleCollider>().FirstOrDefault();
            if (capsule == null) return;
            var c = capsule.Center;
            if (Math.Abs(c.X) + Math.Abs(c.Y) + Math.Abs(c.Z) < 1e-4) return;

            var W = SceneGraphUtil.AccumulateWorld(player);
            var worldOff = SN.Vector3.TransformNormal(
                new SN.Vector3((float)c.X, (float)c.Y, (float)c.Z), W);
            var p = player.Transform.Position;
            player.Transform.Position = new Vector3(p.X + worldOff.X, p.Y + worldOff.Y, p.Z + worldOff.Z);
            capsule.Center = new Vector3(0, 0, 0);
        }

        void EnsurePlayerSetup(GameObject player, PlanetTerrain planet)
        {
            FlattenCapsuleCenter(player);
            if (player.Behaviors.OfType<RigidbodyPlayer>().FirstOrDefault() == null)
                player.AddBehavior<RigidbodyPlayer>();

            var rb = player.Behaviors.OfType<Rigidbody>().FirstOrDefault()
                     ?? player.AddBehavior<Rigidbody>();
            rb.UseGravity = true;
            rb.FreezeRotation = true;

            var capsule = player.Behaviors.OfType<CapsuleCollider>().FirstOrDefault()
                          ?? player.AddBehavior<CapsuleCollider>();
            capsule.Height = CapsuleHeight;
            capsule.Radius = CapsuleRadius;
            capsule.Center = new Vector3(0, 0, 0);
            capsule.Direction = CapsuleCollider.Axis.Y;

            float far = MathF.Max(8000f, planet.Radius * 8f);
            foreach (var cam in player.Behaviors.OfType<Camera>()
                         .Concat(player.Children.SelectMany(c => c.Behaviors.OfType<Camera>())))
            {
                cam.IsMain = true;
                cam.Near = MathF.Min(cam.Near, 0.08f);
                cam.Far = MathF.Max(cam.Far, far);
            }

            foreach (var other in SceneQuery.FindBehaviors<Camera>().ToList())
            {
                if (player.Behaviors.OfType<Camera>().Contains(other)) continue;
                if (player.Children.Any(c => c.Behaviors.OfType<Camera>().Contains(other))) continue;
                other.IsMain = false;
            }

            if (AttachPlanetTool)
                EnsurePlanetTool(player, planet);
        }

        GameObject BuildPlayer()
        {
            var player = new GameObject(string.IsNullOrWhiteSpace(PlayerObjectName) ? "Player" : PlayerObjectName);

            var motor = player.AddBehavior<RigidbodyPlayer>();
            motor.FirstPerson = FirstPerson;
            motor.FirstPersonOffset = new Vector3(0, Math.Max(1.2, CapsuleHeight * 0.85), 0);
            motor.LookSensitivity = 90f;

            var capsule = player.Behaviors.OfType<CapsuleCollider>().FirstOrDefault()
                          ?? player.AddBehavior<CapsuleCollider>();
            capsule.Height = CapsuleHeight;
            capsule.Radius = CapsuleRadius;
            // Rigidbody treats transform as capsule center. Do not also offset Center by half-height.
            capsule.Center = new Vector3(0, 0, 0);
            capsule.Direction = CapsuleCollider.Axis.Y;

            var rb = player.Behaviors.OfType<Rigidbody>().FirstOrDefault()
                     ?? player.AddBehavior<Rigidbody>();
            rb.UseGravity = true;
            rb.FreezeRotation = true;
            rb.Mass = 1f;
            rb.Drag = 0f;

            if (CreateBodyMesh)
            {
                var body = new GameObject("Body");
                player.AddChild(body);
                body.Transform.Position = new Vector3(0, CapsuleHeight * 0.5, 0);
                var mf = body.AddBehavior<MeshFilter>();
                mf.Mesh = Mesh.CreateCylinder(16, CapsuleRadius, CapsuleHeight, true);
                body.AddBehavior<MeshRenderer>();
            }

            if (CreateCameraChild)
            {
                var camGo = new GameObject("PlayerCamera");
                player.AddChild(camGo);
                var cam = camGo.AddBehavior<Camera>();
                cam.IsMain = true;
                cam.Near = 0.08f;
                cam.Far = MathF.Max(8000f, CapsuleHeight * 4000f);
                cam.FieldOfView = 70f;
                camGo.Transform.Position = new Vector3(0, Math.Max(1.5, CapsuleHeight * 0.85), 0);

                foreach (var other in SceneQuery.FindBehaviors<Camera>().ToList())
                {
                    if (ReferenceEquals(other, cam)) continue;
                    other.IsMain = false;
                }
            }

            SceneService.Add(player);
            return player;
        }

        void PlacePlayer(GameObject player, SN.Vector3 worldPos, SN.Vector3 up, PlanetTerrain planet)
        {
            player.Transform.Position = new Vector3(worldPos.X, worldPos.Y, worldPos.Z);

            var rb = player.Behaviors.OfType<Rigidbody>().FirstOrDefault();
            if (rb != null)
            {
                rb.Velocity = SN.Vector3.Zero;
                rb.AngularVelocity = SN.Vector3.Zero;
                rb.FreezeRotation = true;
            }

            var seed = MathF.Abs(up.Y) < 0.95f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
            var tangent = SN.Vector3.Cross(seed, up);
            if (tangent.LengthSquared() > 1e-8f)
                tangent = SN.Vector3.Normalize(tangent);
            TransformUtil.AlignLocalUp(player.Transform, up, tangent);

            float far = MathF.Max(4000f, planet.Radius * 8f);
            foreach (var cam in player.Behaviors.OfType<Camera>()
                         .Concat(player.Children.SelectMany(c => c.Behaviors.OfType<Camera>())))
            {
                cam.Far = far;
                cam.IsMain = true;
            }
        }
    }
}
