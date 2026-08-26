#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game_Engine.Core;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>Emitter shape for particle spawning.</summary>
    public enum EmitterShape { Sphere, Cone, Box }

    /// <summary>Built-in particle presets.</summary>
    public enum ParticlePreset { Custom, Fire, Smoke, Sparks, Rain, Snow, Dust }

    /// <summary>
    /// GPU-friendly particle emitter component.
    /// Spawns billboard particles with configurable emission, lifetime, physics, and color.
    /// Particles are updated on CPU and rendered as camera-facing quads via instancing.
    /// </summary>
    [ComponentCategory("Effects")]
    [Require(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class ParticleEmitter : Behavior
    {
        // ── Emission ──
        [Persist] public float EmissionRate { get; set; } = 20f;
        [Persist] public int MaxParticles { get; set; } = 500;
        [Persist] public EmitterShape Shape { get; set; } = EmitterShape.Sphere;
        [Persist] public float ShapeRadius { get; set; } = 0.5f;
        [Persist] public float ConeAngle { get; set; } = 30f;       // degrees, for Cone shape
        [Persist] public SN.Vector3 BoxSize { get; set; } = SN.Vector3.One;

        // ── Particle properties ──
        [Persist] public float Lifetime { get; set; } = 2f;
        [Persist] public float StartSpeed { get; set; } = 2f;
        [Persist] public float SpeedVariation { get; set; } = 0.5f;
        [Persist] public float StartSize { get; set; } = 0.3f;
        [Persist] public float EndSize { get; set; } = 0.05f;
        [Persist] public float GravityMultiplier { get; set; } = 0f;
        [Persist] public float Drag { get; set; } = 0.02f;
        [Persist] public SN.Vector3 EmissionDirection { get; set; } = SN.Vector3.UnitY;
        [Persist] public bool AlignEmissionToGravity { get; set; } = false;
        [Persist] public bool UsePlanetGravity { get; set; } = false;
        [Persist] public bool StopOnPlanetSurfaceHit { get; set; } = false;
        /// <summary>When true, quads stretch along velocity (rain streaks) instead of circular camera billboards.</summary>
        [Persist] public bool StretchAlongVelocity { get; set; } = false;
        [Persist] public float StretchLength { get; set; } = 0.8f;

        // ── Color (start → end gradient) ──
        [Persist] public SN.Vector4 StartColor { get; set; } = new SN.Vector4(1f, 0.8f, 0.2f, 1f);
        [Persist] public SN.Vector4 EndColor { get; set; } = new SN.Vector4(0.3f, 0.1f, 0.0f, 0f);

        // ── State ──
        [Persist] public bool PlayOnAwake { get; set; } = true;
        [Persist] public bool Loop { get; set; } = true;
        [Persist] public ParticlePreset Preset { get; set; } = ParticlePreset.Custom;

        // ── Sub-emitter (spawn particles on death) ──
        [Persist] public bool SubEmitterEnabled { get; set; } = false;
        [Persist] public int SubEmitterCount { get; set; } = 3;
        [Persist] public float SubEmitterSpeed { get; set; } = 1f;
        [Persist] public float SubEmitterLifetime { get; set; } = 0.5f;

        // ── Runtime data ──
        internal struct Particle
        {
            public SN.Vector3 Position;
            public SN.Vector3 Velocity;
            public float Life;         // remaining
            public float MaxLife;
            public float Size;
            public float Rotation;
            public bool Active;
        }

        internal Particle[] Particles = Array.Empty<Particle>();
        internal int AliveCount;
        private float _emitAccum;
        private bool _playing;
        private readonly Random _rng = new();

        /// <summary>Whether particles are currently being emitted and simulated.</summary>
        public bool IsPlaying => _playing;
        public int ActiveParticleCount => AliveCount;

        public override void OnEnable()
        {
            base.OnEnable();
            // Clear the default cube mesh so it doesn't render beneath the particles
            var mf = gameObject?.Behaviors.OfType<MeshFilter>().FirstOrDefault();
            if (mf != null) mf.Mesh = null;
        }

        public override void Awake()
        {
            if (Preset != ParticlePreset.Custom)
                ApplyPreset(Preset);
            Particles = new Particle[MaxParticles];
            if (PlayOnAwake) _playing = true;
        }

        public override void Start()
        {
            if (PlayOnAwake) _playing = true;
        }

        public void Play() => _playing = true;
        public void Stop() { _playing = false; }
        public void Clear() { AliveCount = 0; for (int i = 0; i < Particles.Length; i++) Particles[i].Active = false; }

        public override void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Ensure array sized
            if (Particles.Length != MaxParticles)
            {
                var old = Particles;
                Particles = new Particle[MaxParticles];
                Array.Copy(old, Particles, Math.Min(old.Length, MaxParticles));
            }

            // Simulate existing particles
            var emitterPos = new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z);
            var gravityDir = ResolveGravityDirection(emitterPos);
            var nearestSurfaceCenter = SN.Vector3.Zero;
            var nearestSurfacePlanet = StopOnPlanetSurfaceHit ? ResolveNearestPlanet(emitterPos, out nearestSurfaceCenter) : null;
            int alive = 0;
            for (int i = 0; i < Particles.Length; i++)
            {
                ref var p = ref Particles[i];
                if (!p.Active) continue;

                p.Life -= dt;
                if (p.Life <= 0f)
                {
                    // Sub-emitter: spawn child particles at death position
                    if (SubEmitterEnabled)
                        SpawnSubParticles(p.Position);
                    p.Active = false;
                    continue;
                }

                // Physics
                p.Velocity += gravityDir * (9.81f * GravityMultiplier * dt);
                p.Velocity *= (1f - Drag * dt);
                p.Position += p.Velocity * dt;

                if (StopOnPlanetSurfaceHit && HasHitPlanetSurface(p.Position, nearestSurfacePlanet, nearestSurfaceCenter))
                {
                    p.Active = false;
                    continue;
                }
                alive++;
            }
            AliveCount = alive;

            // Emit new particles
            if (_playing)
            {
                _emitAccum += EmissionRate * dt;
                int toSpawn = (int)_emitAccum;
                _emitAccum -= toSpawn;

                for (int s = 0; s < toSpawn && alive < MaxParticles; s++)
                {
                    int idx = FindFreeSlot();
                    if (idx < 0) break;
                    SpawnParticle(ref Particles[idx]);
                    alive++;
                }
                AliveCount = alive;

                if (!Loop && _emitAccum <= 0f && alive == 0)
                    _playing = false;
            }
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < Particles.Length; i++)
                if (!Particles[i].Active) return i;
            return -1;
        }

        private void SpawnParticle(ref Particle p)
        {
            var worldPos = Transform.Position;
            SN.Vector3 pos = new SN.Vector3((float)worldPos.X, (float)worldPos.Y, (float)worldPos.Z);
            SN.Vector3 dir = SafeNormalize(EmissionDirection, SN.Vector3.UnitY);

            switch (Shape)
            {
                case EmitterShape.Sphere:
                    var radial = RandomOnSphere();
                    pos += radial * ShapeRadius * (float)_rng.NextDouble();
                    break;
                case EmitterShape.Cone:
                    float angle = ConeAngle * MathF.PI / 180f;
                    float theta = (float)_rng.NextDouble() * MathF.Tau;
                    float phi = (float)_rng.NextDouble() * angle;
                    dir = new SN.Vector3(
                        MathF.Sin(phi) * MathF.Cos(theta),
                        MathF.Cos(phi),
                        MathF.Sin(phi) * MathF.Sin(theta));
                    break;
                case EmitterShape.Box:
                    var localBox = new SN.Vector3(
                        (float)(_rng.NextDouble() - 0.5) * BoxSize.X,
                        (float)(_rng.NextDouble() - 0.5) * BoxSize.Y,
                        (float)(_rng.NextDouble() - 0.5) * BoxSize.Z);
                    if (UsePlanetGravity)
                    {
                        // BoxSize is (tangent, up, bitangent) so rain/snow sheets follow the globe.
                        var up = -ResolveGravityDirection(pos);
                        BuildTangentFrame(up, out var tangent, out var bitangent);
                        pos += tangent * localBox.X + up * localBox.Y + bitangent * localBox.Z;
                    }
                    else
                    {
                        pos += localBox;
                    }
                    break;
            }
            if (AlignEmissionToGravity)
                dir = ResolveGravityDirection(pos);

            float speed = StartSpeed + (float)(_rng.NextDouble() - 0.5) * 2f * SpeedVariation;

            p.Position = pos;
            p.Velocity = dir * speed;
            p.Life = Lifetime * (0.8f + 0.4f * (float)_rng.NextDouble());
            p.MaxLife = p.Life;
            p.Size = StartSize;
            p.Rotation = (float)_rng.NextDouble() * MathF.Tau;
            p.Active = true;
        }

        private void SpawnSubParticles(SN.Vector3 deathPos)
        {
            for (int i = 0; i < SubEmitterCount; i++)
            {
                int idx = FindFreeSlot();
                if (idx < 0) break;
                ref var p = ref Particles[idx];
                p.Position = deathPos;
                p.Velocity = RandomOnSphere() * SubEmitterSpeed;
                p.Life = SubEmitterLifetime * (0.8f + 0.4f * (float)_rng.NextDouble());
                p.MaxLife = p.Life;
                p.Size = StartSize * 0.5f;
                p.Rotation = (float)_rng.NextDouble() * MathF.Tau;
                p.Active = true;
            }
        }

        private SN.Vector3 RandomOnSphere()
        {
            float u = (float)_rng.NextDouble() * 2f - 1f;
            float theta = (float)_rng.NextDouble() * MathF.Tau;
            float r = MathF.Sqrt(1f - u * u);
            return new SN.Vector3(r * MathF.Cos(theta), u, r * MathF.Sin(theta));
        }

        /// <summary>Apply a built-in preset configuration.</summary>
        public void ApplyPreset(ParticlePreset preset)
        {
            StretchAlongVelocity = false;
            StretchLength = 0.8f;
            switch (preset)
            {
                case ParticlePreset.Fire:
                    EmissionRate = 40f; Lifetime = 1.5f; StartSpeed = 1.5f; SpeedVariation = 0.3f;
                    StartSize = 0.4f; EndSize = 0.05f; GravityMultiplier = -0.3f; Drag = 0.1f;
                    StartColor = new SN.Vector4(1f, 0.6f, 0.1f, 1f);
                    EndColor = new SN.Vector4(0.8f, 0.1f, 0.0f, 0f);
                    Shape = EmitterShape.Cone; ConeAngle = 15f; ShapeRadius = 0.2f;
                    SubEmitterEnabled = true; SubEmitterCount = 2; SubEmitterSpeed = 0.5f; SubEmitterLifetime = 0.3f;
                    break;
                case ParticlePreset.Smoke:
                    EmissionRate = 15f; Lifetime = 4f; StartSpeed = 0.8f; SpeedVariation = 0.2f;
                    StartSize = 0.3f; EndSize = 1.5f; GravityMultiplier = -0.1f; Drag = 0.3f;
                    StartColor = new SN.Vector4(0.5f, 0.5f, 0.5f, 0.7f);
                    EndColor = new SN.Vector4(0.3f, 0.3f, 0.3f, 0f);
                    Shape = EmitterShape.Sphere; ShapeRadius = 0.3f;
                    SubEmitterEnabled = false;
                    break;
                case ParticlePreset.Sparks:
                    EmissionRate = 60f; Lifetime = 0.8f; StartSpeed = 5f; SpeedVariation = 2f;
                    StartSize = 0.08f; EndSize = 0.02f; GravityMultiplier = 1f; Drag = 0.05f;
                    StartColor = new SN.Vector4(1f, 0.9f, 0.3f, 1f);
                    EndColor = new SN.Vector4(1f, 0.4f, 0.0f, 0f);
                    Shape = EmitterShape.Sphere; ShapeRadius = 0.1f;
                    SubEmitterEnabled = false;
                    break;
                case ParticlePreset.Rain:
                    EmissionRate = 520f; MaxParticles = 2500; Lifetime = 2.4f; StartSpeed = 22f; SpeedVariation = 4f;
                    StartSize = 0.055f; EndSize = 0.045f; GravityMultiplier = 1.15f; Drag = 0.0f;
                    StartColor = new SN.Vector4(0.78f, 0.84f, 0.92f, 0.95f);
                    EndColor = new SN.Vector4(0.70f, 0.78f, 0.88f, 0.55f);
                    Shape = EmitterShape.Box; BoxSize = new SN.Vector3(22f, 16f, 22f);
                    EmissionDirection = -SN.Vector3.UnitY;
                    AlignEmissionToGravity = true;
                    UsePlanetGravity = true;
                    StopOnPlanetSurfaceHit = true;
                    StretchAlongVelocity = true;
                    StretchLength = 1.15f;
                    SubEmitterEnabled = false;
                    break;
                case ParticlePreset.Snow:
                    EmissionRate = 160f; MaxParticles = 1400; Lifetime = 9f; StartSpeed = 1.4f; SpeedVariation = 0.6f;
                    StartSize = 0.16f; EndSize = 0.12f; GravityMultiplier = 0.12f; Drag = 0.35f;
                    StartColor = new SN.Vector4(0.97f, 0.98f, 1f, 0.95f);
                    EndColor = new SN.Vector4(0.92f, 0.95f, 1f, 0.55f);
                    Shape = EmitterShape.Box; BoxSize = new SN.Vector3(24f, 14f, 24f);
                    EmissionDirection = -SN.Vector3.UnitY;
                    AlignEmissionToGravity = true;
                    UsePlanetGravity = true;
                    StopOnPlanetSurfaceHit = true;
                    StretchAlongVelocity = false;
                    StretchLength = 0.8f;
                    SubEmitterEnabled = false;
                    break;
                case ParticlePreset.Dust:
                    EmissionRate = 10f; Lifetime = 3f; StartSpeed = 0.3f; SpeedVariation = 0.2f;
                    StartSize = 0.1f; EndSize = 0.3f; GravityMultiplier = -0.05f; Drag = 0.8f;
                    StartColor = new SN.Vector4(0.7f, 0.6f, 0.4f, 0.5f);
                    EndColor = new SN.Vector4(0.6f, 0.5f, 0.3f, 0f);
                    Shape = EmitterShape.Box; BoxSize = new SN.Vector3(5f, 0.5f, 5f);
                    SubEmitterEnabled = false;
                    break;
            }
            Preset = preset;
        }

        private bool HasHitPlanetSurface(SN.Vector3 worldPos, PlanetTerrain? nearest, SN.Vector3 nearestCenter)
        {
            if (nearest == null) return false;

            var toPos = worldPos - nearestCenter;
            float lenSq = toPos.LengthSquared();
            if (lenSq <= 1e-8f) return true;

            float dist = MathF.Sqrt(lenSq);
            var dir = toPos / dist;
            float surfaceRadius = nearest.SampleSurfaceRadius(dir);
            return dist <= surfaceRadius;
        }

        private PlanetTerrain? ResolveNearestPlanet(SN.Vector3 worldPos, out SN.Vector3 nearestCenter)
        {
            nearestCenter = SN.Vector3.Zero;
            if (PlanetTerrain.ActivePlanets.Count == 0) return null;

            PlanetTerrain? nearest = null;
            float nearestSq = float.MaxValue;

            for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
            {
                var p = PlanetTerrain.ActivePlanets[i];
                if (p?.gameObject == null) continue;

                var world = SceneGraphUtil.AccumulateWorld(p.gameObject);
                var center = new SN.Vector3(world.M41, world.M42, world.M43);
                float d2 = SN.Vector3.DistanceSquared(worldPos, center);
                if (d2 < nearestSq)
                {
                    nearestSq = d2;
                    nearest = p;
                    nearestCenter = center;
                }
            }

            return nearest;
        }

        private SN.Vector3 ResolveGravityDirection(SN.Vector3 atWorldPos)
        {
            if (!UsePlanetGravity || PlanetTerrain.ActivePlanets.Count == 0)
                return -SN.Vector3.UnitY;

            SN.Vector3 nearestCenter = SN.Vector3.Zero;
            float nearestSq = float.MaxValue;
            for (int i = 0; i < PlanetTerrain.ActivePlanets.Count; i++)
            {
                var p = PlanetTerrain.ActivePlanets[i];
                if (p?.gameObject == null) continue;
                var world = SceneGraphUtil.AccumulateWorld(p.gameObject);
                var center = new SN.Vector3(world.M41, world.M42, world.M43);
                float d2 = SN.Vector3.DistanceSquared(atWorldPos, center);
                if (d2 < nearestSq)
                {
                    nearestSq = d2;
                    nearestCenter = center;
                }
            }

            if (nearestSq >= float.MaxValue)
                return -SN.Vector3.UnitY;

            var down = nearestCenter - atWorldPos;
            return SafeNormalize(down, -SN.Vector3.UnitY);
        }

        private static SN.Vector3 SafeNormalize(SN.Vector3 v, SN.Vector3 fallback)
        {
            float lsq = v.LengthSquared();
            if (lsq <= 1e-10f) return fallback;
            return v / MathF.Sqrt(lsq);
        }

        static void BuildTangentFrame(SN.Vector3 up, out SN.Vector3 tangent, out SN.Vector3 bitangent)
        {
            up = SafeNormalize(up, SN.Vector3.UnitY);
            var hint = MathF.Abs(up.Y) > 0.9f ? SN.Vector3.UnitX : SN.Vector3.UnitY;
            tangent = SafeNormalize(SN.Vector3.Cross(hint, up), SN.Vector3.UnitX);
            bitangent = SafeNormalize(SN.Vector3.Cross(up, tangent), SN.Vector3.UnitZ);
        }

        public SN.Vector3 GetRenderFallDirection()
        {
            for (int i = 0; i < Particles.Length; i++)
            {
                ref var p = ref Particles[i];
                if (!p.Active) continue;
                if (p.Velocity.LengthSquared() > 1e-8f)
                    return SafeNormalize(p.Velocity, -SN.Vector3.UnitY);
            }
            return ResolveGravityDirection(new SN.Vector3((float)Transform.Position.X, (float)Transform.Position.Y, (float)Transform.Position.Z));
        }

        /// <summary>
        /// Get particle instance data for rendering (position, color, size).
        /// Called by the renderer each frame.
        /// </summary>
        public int FillRenderData(SN.Vector4[] positions, SN.Vector4[] colors, int maxCount, int skipActive = 0, SN.Vector4[]? stretch = null)
        {
            int skipped = 0;
            int count = 0;
            for (int i = 0; i < Particles.Length && count < maxCount; i++)
            {
                ref var p = ref Particles[i];
                if (!p.Active) continue;
                if (skipped < skipActive)
                {
                    skipped++;
                    continue;
                }

                float t = 1f - (p.Life / p.MaxLife); // 0 at spawn, 1 at death
                float size = StartSize + (EndSize - StartSize) * t;
                var color = SN.Vector4.Lerp(StartColor, EndColor, t);

                positions[count] = new SN.Vector4(p.Position, size);
                colors[count] = color;
                if (stretch != null)
                {
                    var vel = p.Velocity;
                    float len = StretchAlongVelocity ? Math.Max(0.12f, StretchLength) : size;
                    stretch[count] = new SN.Vector4(vel.X, vel.Y, vel.Z, len);
                }
                count++;
            }
            return count;
        }
    }
}
