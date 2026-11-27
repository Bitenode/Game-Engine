#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Game_Engine.Core.Audio;
#if WINDOWS && !XBOX_UWP
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
#endif

namespace Game_Engine.Core
{
#if XBOX_UWP
#elif !WINDOWS
    /// <summary>OpenAL + PCM WAV backend for Linux/macOS/Android player builds.</summary>
    public static class AudioBackend
    {
        private static readonly List<WeakReference<AudioHandle>> s_activeHandles = new();
        static bool s_available = true;

        public static string? ResolveAudioPath(string filePath) => SharedAudioPaths.Resolve(filePath);

        public static AudioHandle? Play(string filePath, float volume, float pitch, bool loop, string? mixerGroup = null)
        {
            if (!s_available) return null;
            var abs = ResolveAudioPath(filePath);
            if (abs == null) return null;
            if (!OpenAlAudio.TryPlay(abs, volume, pitch, loop, out var handle) || handle == null)
            {
                Log.Warning($"[AudioBackend] OpenAL could not play {Path.GetFileName(abs)}");
                return null;
            }
            lock (s_activeHandles) s_activeHandles.Add(new WeakReference<AudioHandle>(handle));
            return handle;
        }

        public static void PlayOneShot(string filePath, float volume = 1f, string? mixerGroup = null)
            => Play(filePath, volume, 1f, false, mixerGroup);

        public static void ClearCache() => AudioClipCache.ClearCache();

        public static void StopAll()
        {
            lock (s_activeHandles)
            {
                foreach (var w in s_activeHandles)
                    if (w.TryGetTarget(out var h)) { try { h.Stop(); } catch { } }
                s_activeHandles.Clear();
            }
        }

        public static void Shutdown() { StopAll(); s_available = false; OpenAlAudio.Shutdown(); }
        public static void EnsureInit() { s_available = true; OpenAlAudio.Ensure(); }
        public static System.Numerics.Vector3 LastListenerPosition { get; private set; }
        public static void SetListenerPosition(System.Numerics.Vector3 pos, System.Numerics.Vector3 forward, System.Numerics.Vector3 up)
        {
            LastListenerPosition = pos;
            OpenAlAudio.SetListener(pos, forward, up);
        }
    }

    public sealed class AudioHandle : IDisposable
    {
        readonly uint _source;
        bool _disposed;
        float _volume = 1f, _pan, _pitch = 1f;

        internal AudioHandle(uint source, TimeSpan duration)
        {
            _source = source;
            Duration = duration;
        }

        public bool IsPlaying => !_disposed && OpenAlAudio.IsPlaying(_source);
        public TimeSpan Duration { get; }
        public float Volume
        {
            get => _volume;
            set { _volume = value; if (!_disposed) OpenAlAudio.SetGain(_source, value); }
        }
        public float Pitch
        {
            get => _pitch;
            set => _pitch = Math.Clamp(value <= 0f ? 1f : value, 0.1f, 4f);
        }
        public bool Loop { get; set; }
        public float Pan
        {
            get => _pan;
            set { _pan = value; if (!_disposed) OpenAlAudio.SetPan(_source, value); }
        }
        public void Pause() { if (!_disposed) OpenAlAudio.Pause(_source); }
        public void Resume() { if (!_disposed) OpenAlAudio.Resume(_source); }
        public void Stop() => Dispose();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            OpenAlAudio.Stop(_source);
        }
    }
#else
    /// <summary>
    /// Low-level audio backend using NAudio with a decoded PCM clip cache (<see cref="AudioClipCache"/>),
    /// a single shared <see cref="WaveOutEvent"/> + hierarchical <see cref="MixingSampleProvider"/> bus graph,
    /// and real-time <see cref="AudioMixerGroupProvider"/> DSP effects per mixer group.
    /// </summary>
    public static class AudioBackend
    {
        private static bool s_available = true;

        // Shared output device & hierarchical mixer graph (protected by s_mixerLock)
        private static readonly object s_mixerLock = new();
        private static WaveOutEvent? s_waveOut;
        private static MixerGroupBus? s_masterBus;
        private static readonly Dictionary<AudioMixerGroup, MixerGroupBus> s_groupBuses = new();

        // Track all active handles so we can stop them all on game stop
        private static readonly List<WeakReference<AudioHandle>> s_activeHandles = new();

        /// <summary>
        /// Resolve an audio file path. Tries multiple locations:
        /// 1. Absolute path  2. Relative to project root  3. Relative to Assets
        /// 4. Filename search in Assets  5. Relative to working directory
        /// </summary>
        public static string? ResolveAudioPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            // 1. Absolute path
            if (Path.IsPathRooted(filePath) && File.Exists(filePath))
                return filePath;

            var root = ProjectService.Current?.RootPath;

            if (!string.IsNullOrEmpty(root))
            {
                // 2. Relative to project root
                var candidate = Path.GetFullPath(Path.Combine(root, filePath));
                if (File.Exists(candidate)) return candidate;

                // 3. Relative to Assets folder
                candidate = Path.GetFullPath(Path.Combine(root, "Assets", filePath));
                if (File.Exists(candidate)) return candidate;

                // 4. Search Assets by filename
                var fileName = Path.GetFileName(filePath);
                var assetsDir = Path.Combine(root, "Assets");
                if (Directory.Exists(assetsDir))
                {
                    try
                    {
                        var found = Directory.GetFiles(assetsDir, fileName, SearchOption.AllDirectories);
                        if (found.Length > 0) return found[0];
                    }
                    catch { }
                }
            }

            // 5. Working directory fallback
            if (File.Exists(filePath))
                return Path.GetFullPath(filePath);

            return null;
        }

        /// <summary>
        /// Play an audio file through the shared mixer output and decoded PCM cache.
        /// Routes the voice through the requested <see cref="AudioMixer"/> group (and its parent chain up to Master).
        /// </summary>
        public static AudioHandle? Play(string filePath, float volume, float pitch, bool loop, string? mixerGroup = null)
        {
            if (!s_available || string.IsNullOrWhiteSpace(filePath)) return null;

            string? absPath = ResolveAudioPath(filePath);
            if (absPath == null)
            {
                Log.Warning($"[AudioBackend] File not found: {filePath}");
                return null;
            }

            var clip = AudioClipCache.GetOrLoad(absPath);
            if (clip == null) return null;

            try
            {
                var voice = new CachedSoundSampleProvider(clip, volume, pitch, loop, mixerGroup);

                lock (s_mixerLock)
                {
                    if (!EnsureOutputDeviceLocked())
                        return null;

                    var targetGroup = string.IsNullOrWhiteSpace(mixerGroup)
                        ? AudioMixer.Master
                        : (AudioMixer.FindGroup(mixerGroup) ?? AudioMixer.Master);

                    var bus = GetOrCreateBusLocked(targetGroup);
                    bus.InputMixer.AddMixerInput(voice);

                    if (s_waveOut != null && s_waveOut.PlaybackState != PlaybackState.Playing)
                        s_waveOut.Play();
                }

                var handle = new AudioHandle(voice);
                lock (s_activeHandles)
                {
                    if (s_activeHandles.Count > 64)
                    {
                        for (int i = s_activeHandles.Count - 1; i >= 0; i--)
                        {
                            if (!s_activeHandles[i].TryGetTarget(out var h) || !h.IsPlaying)
                                s_activeHandles.RemoveAt(i);
                        }
                    }
                    s_activeHandles.Add(new WeakReference<AudioHandle>(handle));
                }

                Log.Info($"[AudioBackend] Now playing: {Path.GetFileName(absPath)}");
                return handle;
            }
            catch (Exception ex)
            {
                Log.Warning($"[AudioBackend] Failed to play {Path.GetFileName(absPath)}: {ex.Message}");
                s_available = false; // disable future attempts if device fails
                return null;
            }
        }

        /// <summary>Play a one-shot sound (fire-and-forget).</summary>
        public static void PlayOneShot(string filePath, float volume = 1f, string? mixerGroup = null)
        {
            Play(filePath, volume, 1f, loop: false, mixerGroup: mixerGroup);
        }

        /// <summary>Clear the decoded PCM audio cache.</summary>
        public static void ClearCache() => AudioClipCache.ClearCache();

        /// <summary>Stop all currently playing audio handles.</summary>
        public static void StopAll()
        {
            lock (s_activeHandles)
            {
                foreach (var weakRef in s_activeHandles)
                {
                    if (weakRef.TryGetTarget(out var handle))
                    {
                        try { handle.Stop(); } catch { }
                    }
                }
                s_activeHandles.Clear();
            }
        }

        /// <summary>Shut down — stops all audio, disposes the shared output device, and clears the PCM cache.</summary>
        public static void Shutdown()
        {
            StopAll();
            lock (s_mixerLock)
            {
                if (s_waveOut != null)
                {
                    try { s_waveOut.Stop(); } catch { }
                    try { s_waveOut.Dispose(); } catch { }
                    s_waveOut = null;
                }
                s_masterBus = null;
                s_groupBuses.Clear();
            }
            AudioClipCache.ClearCache();
            s_available = false;
        }

        public static void EnsureInit() { s_available = true; }

        /// <summary>Last listener transform passed from <see cref="AudioManager.UpdateListenerTransform"/> (NAudio playback uses <see cref="Component.AudioListener"/> via <see cref="AudioSource"/> for panning and attenuation).</summary>
        public static System.Numerics.Vector3 LastListenerPosition { get; private set; }
        public static System.Numerics.Vector3 LastListenerForward { get; private set; }
        public static System.Numerics.Vector3 LastListenerUp { get; private set; }

        /// <summary>Update the 3D audio listener position and orientation (cached for diagnostics and spatial reverb zones).</summary>
        public static void SetListenerPosition(System.Numerics.Vector3 pos, System.Numerics.Vector3 forward, System.Numerics.Vector3 up)
        {
            LastListenerPosition = pos;
            var fl = forward.LengthSquared() > 1e-8f ? System.Numerics.Vector3.Normalize(forward) : new System.Numerics.Vector3(0, 0, -1);
            var ul = up.LengthSquared() > 1e-8f ? System.Numerics.Vector3.Normalize(up) : System.Numerics.Vector3.UnitY;
            LastListenerForward = fl;
            LastListenerUp = ul;
        }

        private static bool EnsureOutputDeviceLocked()
        {
            if (s_waveOut != null && s_masterBus != null)
                return true;

            try
            {
                var master = AudioMixer.Master;
                var masterBus = new MixerGroupBus(master, AudioClipCache.MixFormat);
                s_masterBus = masterBus;
                s_groupBuses.Clear();
                s_groupBuses[master] = masterBus;

                var masterOutput = new ZoneReverbProvider(masterBus.EffectProvider);
                var waveOut = new WaveOutEvent();
                waveOut.Init(masterOutput);
                waveOut.Play();

                s_waveOut = waveOut;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[AudioBackend] Failed to initialize shared audio output: {ex.Message}");
                s_available = false;
                s_waveOut = null;
                s_masterBus = null;
                s_groupBuses.Clear();
                return false;
            }
        }

        private static MixerGroupBus GetOrCreateBusLocked(AudioMixerGroup group)
        {
            if (s_groupBuses.TryGetValue(group, out var existing))
                return existing;

            var bus = new MixerGroupBus(group, AudioClipCache.MixFormat);
            s_groupBuses[group] = bus;

            var parent = group.Parent ?? (ReferenceEquals(group, AudioMixer.Master) ? null : AudioMixer.Master);
            if (parent != null)
            {
                var parentBus = GetOrCreateBusLocked(parent);
                parentBus.InputMixer.AddMixerInput(bus.EffectProvider);
            }

            return bus;
        }

        private sealed class MixerGroupBus
        {
            public readonly MixingSampleProvider InputMixer;
            public readonly AudioMixerGroupProvider EffectProvider;

            public MixerGroupBus(AudioMixerGroup group, WaveFormat format)
            {
                InputMixer = new MixingSampleProvider(format) { ReadFully = true };
                EffectProvider = new AudioMixerGroupProvider(InputMixer, group);
            }
        }
    }

    /// <summary>
    /// Stereo balance. -1 is left, 0 is center, +1 is right.
    /// NAudio's panner accepts only a mono source, so this scales an already-stereo stream.
    /// </summary>
    public sealed class StereoPanProvider : ISampleProvider
    {
        readonly ISampleProvider _source;
        float _pan;

        public StereoPanProvider(ISampleProvider source)
        {
            if (source.WaveFormat.Channels != 2)
                throw new ArgumentException("Stereo pan expects two channels.", nameof(source));
            _source = source;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public float Pan
        {
            get => _pan;
            set => _pan = Math.Clamp(value, -1f, 1f);
        }

        public int Read(float[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            int read = _source.Read(buffer);
            float lGain = Math.Clamp(1f - Math.Max(_pan, 0f), 0f, 1f);
            float rGain = Math.Clamp(1f + Math.Min(_pan, 0f), 0f, 1f);
            for (int i = 0; i + 1 < read; i += 2)
            {
                buffer[i] *= lGain;
                buffer[i + 1] *= rGain;
            }
            return read;
        }
    }

    /// <summary>
    /// Short feedback delay. Wet mix follows <see cref="Component.ReverbZone"/> at the listener.
    /// No zone, or a listener outside every zone, stays dry.
    /// </summary>
    sealed class ZoneReverbProvider : ISampleProvider
    {
        readonly ISampleProvider _source;
        readonly float[] _delay;
        int _write;

        public ZoneReverbProvider(ISampleProvider source)
        {
            _source = source;
            int samples = Math.Max(1, (int)(source.WaveFormat.SampleRate * 0.08f)) * Math.Max(1, source.WaveFormat.Channels);
            _delay = new float[samples];
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            int read = _source.Read(buffer);
            float wet = CurrentWet();
            if (wet <= 1e-5f)
            {
                // Still feed delay gently or bypass when completely dry for zero coloration.
                for (int i = 0; i < read; i++)
                {
                    float fed = buffer[i] + _delay[_write] * 0.45f;
                    if (!float.IsFinite(fed) || MathF.Abs(fed) < 1e-20f) fed = 0f;
                    _delay[_write] = fed;
                    _write++;
                    if (_write >= _delay.Length) _write = 0;
                }
                return read;
            }

            float dryMix = 1f - wet;
            for (int i = 0; i < read; i++)
            {
                float dry = buffer[i];
                float delayed = _delay[_write];
                float fed = dry + delayed * 0.45f;
                if (!float.IsFinite(fed) || MathF.Abs(fed) < 1e-20f) fed = 0f;
                _delay[_write] = fed;
                _write++;
                if (_write >= _delay.Length) _write = 0;
                buffer[i] = dry * dryMix + fed * wet;
            }
            return read;
        }

        static float CurrentWet()
        {
            var (zone, weight) = Component.ReverbZone.GetActiveZone(AudioBackend.LastListenerPosition);
            if (zone == null || weight <= 0f) return 0f;
            float decay = zone.Preset == Component.ReverbPreset.None
                ? zone.DecayTime
                : Component.ReverbZone.GetPresetParams(zone.Preset).decayTime;
            return Math.Clamp(weight * Math.Clamp(decay / 5f, 0f, 1f), 0f, 0.85f);
        }
    }

    /// <summary>
    /// Handle to a playing audio voice in the shared mixer. Controls volume, pitch, pan, loop, pause, and stop.
    /// </summary>
    public sealed class AudioHandle : IDisposable
    {
        private readonly CachedSoundSampleProvider _voice;
        private bool _disposed;

        public bool IsPlaying => !_disposed && _voice.IsPlaying;
        public TimeSpan Duration => _voice.Duration;
        public string? MixerGroup => _voice.MixerGroupName;

        internal AudioHandle(CachedSoundSampleProvider voice)
        {
            _voice = voice;
        }

        public float Volume
        {
            get => _voice.Volume;
            set => _voice.Volume = value;
        }

        public float Pitch
        {
            get => _voice.Pitch;
            set => _voice.Pitch = value;
        }

        public bool Loop
        {
            get => _voice.Loop;
            set => _voice.Loop = value;
        }

        /// <summary>Stereo pan: -1 left, 0 center, +1 right.</summary>
        public float Pan
        {
            get => _voice.Pan;
            set => _voice.Pan = value;
        }

        public void Pause()
        {
            if (!_disposed) _voice.Pause();
        }

        public void Resume()
        {
            if (!_disposed) _voice.Resume();
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _voice.Stop();
        }
    }
#endif
}
