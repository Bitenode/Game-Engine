#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Component;
using SN = System.Numerics;

namespace Game_Engine.Core
{
    /// <summary>
    /// Central audio manager — tracks all AudioSources and the active AudioListener.
    /// Manages volume channels, spatial audio processing, and playback state.
    /// Uses NAudio backend for real audio output.
    /// </summary>
    public static class AudioManager
    {
        // ── Volume channels ──
        public static float MasterVolume { get; set; } = 1f;
        public static float MusicVolume { get; set; } = 0.8f;
        public static float SFXVolume { get; set; } = 1f;

        // ── Registry ──
        private static readonly List<AudioSource> _sources = new(64);
        private static AudioListener? _listener;

        /// <summary>Active audio listener (the "ears").</summary>
        public static AudioListener? Listener => _listener;

        /// <summary>All registered audio sources.</summary>
        public static IReadOnlyList<AudioSource> Sources => _sources;

        internal static void Register(AudioSource src)
        {
            if (src != null && !_sources.Contains(src))
                _sources.Add(src);
        }

        internal static void Unregister(AudioSource src)
        {
            if (src != null) _sources.Remove(src);
        }

        internal static void SetListener(AudioListener listener)
        {
            _listener = listener;
            // Initialize audio backend when a listener is first set
            AudioBackend.EnsureInit();
        }

        internal static void ClearListener(AudioListener listener)
        {
            if (ReferenceEquals(_listener, listener))
                _listener = null;
        }

        /// <summary>Play a one-shot sound at a world position (fire-and-forget).</summary>
        public static void PlayOneShot(string clipPath, SN.Vector3 position, float volume = 1f)
        {
            AudioBackend.PlayOneShot(clipPath, volume);
        }

        /// <summary>Stop all playing audio sources.</summary>
        public static void StopAll()
        {
            for (int i = 0; i < _sources.Count; i++)
                _sources[i].Stop();
        }

        /// <summary>Pause all playing audio sources.</summary>
        public static void PauseAll()
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                if (_sources[i].IsPlaying)
                    _sources[i].Pause();
            }
        }

        /// <summary>Resume all paused audio sources.</summary>
        public static void ResumeAll()
        {
            for (int i = 0; i < _sources.Count; i++)
                _sources[i].UnPause();
        }

        /// <summary>Shut down the audio system. Call on application exit.</summary>
        public static void Shutdown()
        {
            StopAll();
            AudioBackend.Shutdown();
        }
    }
}
