#nullable enable
using System;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Audio source component — plays sound clips with 3D spatial audio support.
    /// Attach to any GameObject; spatial audio is handled natively by OpenAL
    /// using the source position and the AudioListener position.
    /// </summary>
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
        private SN.Vector3 _lastPosition;

        /// <summary>True if currently playing audio.</summary>
        public bool IsPlaying => _handle != null && _handle.IsPlaying;

        public override void Awake()
        {
            AudioManager.Register(this);
        }

        public override void Start()
        {
            _lastPosition = GetWorldPos();
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

            // Update volume and spatial properties each frame
            if (_handle == null || !_handle.IsPlaying) return;

            // Effective volume
            float vol = ComputeEffectiveVolume();
            _handle.Volume = Mute ? 0f : vol;

            // Keep loop in sync
            _handle.Loop = Loop;

            // Pitch
            _handle.Pitch = Math.Clamp(Pitch, 0.1f, 4f);

            // 3D spatial audio via OpenAL
            if (SpatialBlend > 0f)
            {
                var pos = GetWorldPos();

                // Set source position in world space (OpenAL handles attenuation + panning)
                _handle.SetPosition(pos.X, pos.Y, pos.Z);

                // Set distance model parameters
                _handle.SetDistanceModel(MinDistance, MaxDistance, 1f);

                // Doppler: set velocity based on position change
                if (DopplerLevel > 0f && Time.deltaTime > 0f)
                {
                    var velocity = (pos - _lastPosition) / Time.deltaTime * DopplerLevel;
                    _handle.SetVelocity(velocity.X, velocity.Y, velocity.Z);
                }
                else
                {
                    _handle.SetVelocity(0f, 0f, 0f);
                }

                _lastPosition = pos;
            }
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
                // Configure spatial audio
                if (SpatialBlend > 0f)
                {
                    var pos = GetWorldPos();
                    _handle.SetPosition(pos.X, pos.Y, pos.Z);
                    _handle.SetDistanceModel(MinDistance, MaxDistance, 1f);
                }

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
        /// Compute effective volume considering channel volumes.
        /// Distance attenuation is handled natively by OpenAL for 3D sources.
        /// </summary>
        float ComputeEffectiveVolume()
        {
            float channelVol = Channel == AudioChannel.Music
                ? AudioManager.MusicVolume
                : AudioManager.SFXVolume;

            float vol = Volume * AudioManager.MasterVolume * channelVol;
            return Math.Clamp(vol, 0f, 2f);
        }

        /// <summary>Get world position of this audio source.</summary>
        private SN.Vector3 GetWorldPos()
            => new(
                (float)Transform.Position.X,
                (float)Transform.Position.Y,
                (float)Transform.Position.Z);
    }

    /// <summary>Audio channel for volume control separation.</summary>
    public enum AudioChannel { Master, Music, SFX }
}
