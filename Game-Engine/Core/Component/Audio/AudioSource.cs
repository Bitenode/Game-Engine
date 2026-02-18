#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Audio source component — plays sound clips with 3D spatial audio support.
    /// Attach to any GameObject; distance attenuation is calculated from the
    /// AudioListener's position each frame.
    /// Uses NAudio backend for real audio output.
    /// </summary>
    [ComponentCategory("Audio")]
    public sealed class AudioSource : Behavior
    {
        // ── Clip ──
        [Persist] public string ClipPath { get; set; } = "";

        // ── Playback ──
        [Persist] public float Volume { get; set; } = 1f;
        [Persist] public float Pitch { get; set; } = 1f;
        [Persist] public bool Loop { get; set; } = false;
        [Persist] public bool PlayOnAwake { get; set; } = true;
        [Persist] public bool Mute { get; set; } = false;

        // ── Spatial ──
        [Persist] public float SpatialBlend { get; set; } = 1f;    // 0 = 2D, 1 = full 3D
        [Persist] public float MinDistance { get; set; } = 1f;
        [Persist] public float MaxDistance { get; set; } = 50f;
        [Persist] public float DopplerLevel { get; set; } = 0f;

        // ── Channel ──
        [Persist] public AudioChannel Channel { get; set; } = AudioChannel.SFX;

        // ── Runtime state ──
        private AudioHandle? _handle;

        /// <summary>True if currently playing audio.</summary>
        public bool IsPlaying => _handle != null && _handle.IsPlaying;

        public override void Awake()
        {
            AudioManager.Register(this);
        }

        public override void Start()
        {
            // Only auto-play if the component is actually enabled
            if (Enabled && PlayOnAwake && !string.IsNullOrEmpty(ClipPath))
                Play();
        }

        public override void Update()
        {
            // Safety: if we're disabled but audio is still playing, stop it
            if (!Enabled)
            {
                if (_handle != null) Stop();
                return;
            }

            // Update volume and pan each frame for spatial audio
            if (_handle == null || !_handle.IsPlaying) return;

            // Effective volume
            float vol = ComputeEffectiveVolume();
            _handle.Volume = Mute ? 0f : vol;

            // Spatial panning
            float pan = ComputePan();
            _handle.Pan = pan;

            // Keep loop in sync
            _handle.Loop = Loop;
        }

        public override void OnEnable()
        {
            base.OnEnable();
        }

        public override void OnDisable()
        {
            // Stop audio when component is disabled
            Stop();
            base.OnDisable();
        }

        public override void OnDestroy()
        {
            Stop();
            AudioManager.Unregister(this);
        }

        /// <summary>Start playing the assigned clip.</summary>
        public void Play()
        {
            // Stop any existing playback first
            Stop();

            if (string.IsNullOrWhiteSpace(ClipPath)) return;

            float vol = Mute ? 0f : ComputeEffectiveVolume();
            _handle = AudioBackend.Play(ClipPath, vol, Pitch, Loop);

            if (_handle != null)
            {
                _handle.Pan = ComputePan();
                Log.Info($"[AudioSource] Playing: {ClipPath}");
            }
        }

        /// <summary>Stop playback and release resources.</summary>
        public void Stop()
        {
            if (_handle != null)
            {
                _handle.Stop();
                _handle = null;
            }
        }

        /// <summary>Pause playback without releasing resources.</summary>
        public void Pause()
        {
            _handle?.Pause();
        }

        /// <summary>Resume from paused position.</summary>
        public void UnPause()
        {
            _handle?.Resume();
        }

        /// <summary>
        /// Compute effective volume considering distance attenuation and channel volumes.
        /// </summary>
        float ComputeEffectiveVolume()
        {
            float channelVol = Channel == AudioChannel.Music
                ? AudioManager.MusicVolume
                : AudioManager.SFXVolume;

            float vol = Volume * AudioManager.MasterVolume * channelVol;

            if (SpatialBlend > 0f)
            {
                var listener = AudioManager.Listener;
                if (listener != null)
                {
                    var listenerPos = listener.GetWorldPosition();
                    var srcPos = new SN.Vector3(
                        (float)Transform.Position.X,
                        (float)Transform.Position.Y,
                        (float)Transform.Position.Z);
                    float dist = SN.Vector3.Distance(srcPos, listenerPos);

                    // Inverse distance clamped attenuation
                    float atten = 1f;
                    if (dist > MinDistance)
                    {
                        float t = Math.Clamp((dist - MinDistance) / Math.Max(MaxDistance - MinDistance, 0.001f), 0f, 1f);
                        atten = 1f - t;
                    }

                    vol *= (1f - SpatialBlend) + SpatialBlend * atten;
                }
            }

            return Math.Clamp(vol, 0f, 2f);
        }

        /// <summary>
        /// Compute stereo panning for 3D audio (-1 = left, 0 = center, +1 = right).
        /// </summary>
        float ComputePan()
        {
            if (SpatialBlend <= 0f) return 0f;

            var listener = AudioManager.Listener;
            if (listener == null) return 0f;

            var listenerPos = listener.GetWorldPosition();
            var listenerRight = listener.GetWorldRight();
            var srcPos = new SN.Vector3(
                (float)Transform.Position.X,
                (float)Transform.Position.Y,
                (float)Transform.Position.Z);
            var toSource = srcPos - listenerPos;
            float len = toSource.Length();
            if (len < 0.001f) return 0f;

            toSource /= len;
            float pan = SN.Vector3.Dot(toSource, listenerRight);
            return Math.Clamp(pan * SpatialBlend, -1f, 1f);
        }
    }

    /// <summary>Audio channel for volume control separation.</summary>
    public enum AudioChannel { Master, Music, SFX }
}
