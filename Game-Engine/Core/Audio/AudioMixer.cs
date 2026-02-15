#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Audio
{
    /// <summary>
    /// Audio mixer group — hierarchical volume/effects grouping for audio sources.
    /// Supports parent-child relationships (e.g., Master -> Music -> BossMusic).
    /// Each group has its own volume, mute state, and effects chain.
    /// </summary>
    public sealed class AudioMixerGroup
    {
        public string Name { get; set; } = "";
        public float Volume { get; set; } = 1f;
        public bool Muted { get; set; } = false;
        public AudioMixerGroup? Parent { get; set; }
        public List<AudioMixerGroup> Children { get; } = new();
        public List<AudioEffect> Effects { get; } = new();

        /// <summary>Effective volume considering parent chain.</summary>
        public float EffectiveVolume
        {
            get
            {
                if (Muted) return 0f;
                float vol = Volume;
                var p = Parent;
                while (p != null)
                {
                    if (p.Muted) return 0f;
                    vol *= p.Volume;
                    p = p.Parent;
                }
                return vol;
            }
        }

        /// <summary>Add a child group.</summary>
        public AudioMixerGroup AddChild(string name, float volume = 1f)
        {
            var child = new AudioMixerGroup { Name = name, Volume = volume, Parent = this };
            Children.Add(child);
            return child;
        }

        /// <summary>Add an effect to this group's effects chain.</summary>
        public void AddEffect(AudioEffect effect) => Effects.Add(effect);

        /// <summary>Remove an effect from this group.</summary>
        public bool RemoveEffect(AudioEffect effect) => Effects.Remove(effect);
    }

    /// <summary>Type of audio effect.</summary>
    public enum AudioEffectType
    {
        Reverb,
        Echo,
        LowPassFilter,
        HighPassFilter,
        Chorus,
        Distortion,
        Compressor
    }

    /// <summary>
    /// Audio effect that can be applied to a mixer group.
    /// Stores parameters as a dictionary for flexibility.
    /// </summary>
    public sealed class AudioEffect
    {
        public AudioEffectType Type { get; set; }
        public bool Enabled { get; set; } = true;
        public float WetMix { get; set; } = 1f;   // 0 = dry only, 1 = fully wet
        public Dictionary<string, float> Parameters { get; } = new();

        /// <summary>Create a reverb effect with common parameters.</summary>
        public static AudioEffect CreateReverb(float decayTime = 1.5f, float density = 1f, float diffusion = 1f)
        {
            var fx = new AudioEffect { Type = AudioEffectType.Reverb };
            fx.Parameters["DecayTime"] = decayTime;
            fx.Parameters["Density"] = density;
            fx.Parameters["Diffusion"] = diffusion;
            return fx;
        }

        /// <summary>Create an echo effect.</summary>
        public static AudioEffect CreateEcho(float delay = 0.3f, float feedback = 0.5f, float wetMix = 0.5f)
        {
            var fx = new AudioEffect { Type = AudioEffectType.Echo, WetMix = wetMix };
            fx.Parameters["Delay"] = delay;
            fx.Parameters["Feedback"] = feedback;
            return fx;
        }

        /// <summary>Create a low-pass filter effect.</summary>
        public static AudioEffect CreateLowPass(float cutoffHz = 5000f, float resonance = 1f)
        {
            var fx = new AudioEffect { Type = AudioEffectType.LowPassFilter };
            fx.Parameters["CutoffHz"] = cutoffHz;
            fx.Parameters["Resonance"] = resonance;
            return fx;
        }

        /// <summary>Create a high-pass filter effect.</summary>
        public static AudioEffect CreateHighPass(float cutoffHz = 200f, float resonance = 1f)
        {
            var fx = new AudioEffect { Type = AudioEffectType.HighPassFilter };
            fx.Parameters["CutoffHz"] = cutoffHz;
            fx.Parameters["Resonance"] = resonance;
            return fx;
        }

        /// <summary>Create a compressor effect for ducking.</summary>
        public static AudioEffect CreateCompressor(float threshold = -20f, float ratio = 4f, float attackMs = 10f, float releaseMs = 100f)
        {
            var fx = new AudioEffect { Type = AudioEffectType.Compressor };
            fx.Parameters["Threshold"] = threshold;
            fx.Parameters["Ratio"] = ratio;
            fx.Parameters["AttackMs"] = attackMs;
            fx.Parameters["ReleaseMs"] = releaseMs;
            return fx;
        }
    }

    /// <summary>
    /// Central audio mixer — manages the hierarchy of mixer groups
    /// and provides ducking/snapshot functionality.
    /// </summary>
    public static class AudioMixer
    {
        private static AudioMixerGroup? _master;

        /// <summary>The master mixer group (root of the hierarchy).</summary>
        public static AudioMixerGroup Master => _master ??= CreateDefaultMixer();

        /// <summary>Find a mixer group by name (depth-first search).</summary>
        public static AudioMixerGroup? FindGroup(string name)
        {
            return FindRecursive(Master, name);
        }

        private static AudioMixerGroup? FindRecursive(AudioMixerGroup group, string name)
        {
            if (group.Name == name) return group;
            foreach (var child in group.Children)
            {
                var found = FindRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Create the default mixer hierarchy.</summary>
        private static AudioMixerGroup CreateDefaultMixer()
        {
            var master = new AudioMixerGroup { Name = "Master", Volume = 1f };
            master.AddChild("Music", 0.8f);
            var sfx = master.AddChild("SFX", 1f);
            sfx.AddChild("Footsteps", 0.7f);
            sfx.AddChild("Weapons", 1f);
            sfx.AddChild("Ambient", 0.6f);
            master.AddChild("UI", 0.9f);
            master.AddChild("Voice", 1f);
            return master;
        }

        /// <summary>
        /// Audio snapshot — stores a set of mixer group volumes for quick transitions.
        /// Useful for pausing (mute gameplay, keep UI) or underwater effects.
        /// </summary>
        public sealed class Snapshot
        {
            public string Name { get; set; } = "";
            public Dictionary<string, float> Volumes { get; } = new();

            /// <summary>Capture the current mixer state as a snapshot.</summary>
            public static Snapshot Capture(string name)
            {
                var snap = new Snapshot { Name = name };
                CaptureRecursive(Master, snap);
                return snap;
            }

            private static void CaptureRecursive(AudioMixerGroup group, Snapshot snap)
            {
                snap.Volumes[group.Name] = group.Volume;
                foreach (var child in group.Children)
                    CaptureRecursive(child, snap);
            }

            /// <summary>Apply this snapshot immediately.</summary>
            public void Apply()
            {
                foreach (var (name, volume) in Volumes)
                {
                    var group = FindGroup(name);
                    if (group != null) group.Volume = volume;
                }
            }

            /// <summary>Lerp toward this snapshot over time (call each frame).</summary>
            public void TransitionTo(float t)
            {
                foreach (var (name, targetVol) in Volumes)
                {
                    var group = FindGroup(name);
                    if (group != null)
                        group.Volume = group.Volume + (targetVol - group.Volume) * Math.Clamp(t, 0f, 1f);
                }
            }
        }
    }

    /// <summary>
    /// Audio occlusion system — uses raycasts to determine if sound is blocked
    /// by geometry, and applies low-pass filtering + volume reduction.
    /// </summary>
    public static class AudioOcclusion
    {
        /// <summary>
        /// Compute the occlusion factor between a listener and a source.
        /// Returns 0 (fully occluded) to 1 (fully audible).
        /// Uses the physics raycast system to check for blocking geometry.
        /// </summary>
        public static float ComputeOcclusion(
            System.Numerics.Vector3 listenerPos,
            System.Numerics.Vector3 sourcePos)
        {
            var dir = sourcePos - listenerPos;
            float dist = dir.Length();
            if (dist < 0.01f) return 1f; // Same position = fully audible

            dir /= dist;

            // Cast a ray from listener to source
            if (Physics.CollisionWorld.Raycast(listenerPos, dir, dist, out var hit))
            {
                // Something is between the listener and source
                // The more objects, the more occluded. Simple single-ray approach:
                float hitDist = hit.Distance;
                if (hitDist < dist - 0.1f)
                {
                    // Occluded — reduce based on how close the blocker is to the listener
                    float occlusionStrength = 1f - (hitDist / dist);
                    return Math.Clamp(1f - occlusionStrength * 0.8f, 0.1f, 1f);
                }
            }

            return 1f; // No obstruction
        }

        /// <summary>
        /// Multi-ray occlusion — casts several rays for more accurate results.
        /// Returns average occlusion factor (0..1).
        /// </summary>
        public static float ComputeOcclusionMultiRay(
            System.Numerics.Vector3 listenerPos,
            System.Numerics.Vector3 sourcePos,
            int rayCount = 5)
        {
            float total = 0f;
            var center = (listenerPos + sourcePos) * 0.5f;

            // Center ray
            total += ComputeOcclusion(listenerPos, sourcePos);

            // Offset rays for better coverage
            if (rayCount > 1)
            {
                var right = System.Numerics.Vector3.Cross(
                    System.Numerics.Vector3.Normalize(sourcePos - listenerPos),
                    System.Numerics.Vector3.UnitY);
                if (right.LengthSquared() < 0.01f)
                    right = System.Numerics.Vector3.UnitX;
                right = System.Numerics.Vector3.Normalize(right);
                var up = System.Numerics.Vector3.UnitY;

                float offset = 0.3f;
                total += ComputeOcclusion(listenerPos, sourcePos + right * offset);
                total += ComputeOcclusion(listenerPos, sourcePos - right * offset);
                total += ComputeOcclusion(listenerPos, sourcePos + up * offset);
                total += ComputeOcclusion(listenerPos, sourcePos - up * offset);

                return total / 5f;
            }

            return total;
        }
    }
}
