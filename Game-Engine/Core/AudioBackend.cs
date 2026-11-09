#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
#if WINDOWS
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
#endif

namespace Game_Engine.Core
{
#if !WINDOWS
    /// <summary>OpenAL + PCM WAV backend for Linux/macOS/Android player builds.</summary>
    public static class AudioBackend
    {
        private static readonly List<WeakReference<AudioHandle>> s_activeHandles = new();
        static bool s_available = true;

        public static string? ResolveAudioPath(string filePath) => SharedAudioPaths.Resolve(filePath);

        public static AudioHandle? Play(string filePath, float volume, float pitch, bool loop)
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

        public static void PlayOneShot(string filePath, float volume = 1f) => Play(filePath, volume, 1f, false);

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
        float _volume = 1f, _pan;

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
    /// Low-level audio backend using NAudio.
    /// Each sound gets its own WaveOut for maximum compatibility.
    /// Simple and reliable — avoids mixer format-matching issues.
    /// </summary>
    public static class AudioBackend
    {
        private static bool s_available = true;

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
        /// Play an audio file. Returns a handle for volume/pause/stop control.
        /// Each call creates its own output device — simple and reliable.
        /// </summary>
        public static AudioHandle? Play(string filePath, float volume, float pitch, bool loop)
        {
            if (!s_available || string.IsNullOrWhiteSpace(filePath)) return null;

            string? absPath = ResolveAudioPath(filePath);
            if (absPath == null)
            {
                Log.Warning($"[AudioBackend] File not found: {filePath}");
                return null;
            }

            try
            {
                var reader = new AudioFileReader(absPath);
                reader.Volume = Math.Clamp(volume, 0f, 1f);

                // Wrap for looping, then stereo pan and a short zone reverb.
                var loopStream = new LoopingReader(reader, loop);
                ISampleProvider samples = loopStream.ToSampleProvider();
                if (samples.WaveFormat.Channels == 1)
                    samples = new MonoToStereoSampleProvider(samples);
                else if (samples.WaveFormat.Channels != 2)
                    samples = new MultiplexingSampleProvider(new[] { samples }, 2);
                var panner = new StereoPanProvider(samples);
                var reverb = new ZoneReverbProvider(panner);

                var output = new WaveOut();
                output.Init(reverb);
                output.Play();

                var handle = new AudioHandle(output, reader, loopStream, panner);
                lock (s_activeHandles) s_activeHandles.Add(new WeakReference<AudioHandle>(handle));
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
        public static void PlayOneShot(string filePath, float volume = 1f)
        {
            var handle = Play(filePath, volume, 1f, loop: false);
            // Handle auto-disposes when playback ends via PlaybackStopped event
        }

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

        /// <summary>Shut down — stops all audio and prevents future playback.</summary>
        public static void Shutdown()
        {
            StopAll();
            s_available = false;
        }

        public static void EnsureInit() { s_available = true; }

        /// <summary>Last listener transform passed from <see cref="AudioManager.UpdateListenerTransform"/> (NAudio playback uses <see cref="Component.AudioListener"/> via <see cref="AudioSource"/> for panning and attenuation).</summary>
        public static System.Numerics.Vector3 LastListenerPosition { get; private set; }
        public static System.Numerics.Vector3 LastListenerForward { get; private set; }
        public static System.Numerics.Vector3 LastListenerUp { get; private set; }

        /// <summary>Update the 3D audio listener position and orientation (cached for diagnostics and future spatial backends).</summary>
        public static void SetListenerPosition(System.Numerics.Vector3 pos, System.Numerics.Vector3 forward, System.Numerics.Vector3 up)
        {
            LastListenerPosition = pos;
            var fl = forward.LengthSquared() > 1e-8f ? System.Numerics.Vector3.Normalize(forward) : new System.Numerics.Vector3(0, 0, -1);
            var ul = up.LengthSquared() > 1e-8f ? System.Numerics.Vector3.Normalize(up) : System.Numerics.Vector3.UnitY;
            LastListenerForward = fl;
            LastListenerUp = ul;
        }
    }

    /// <summary>
    /// WaveStream wrapper that loops an AudioFileReader.
    /// </summary>
    internal sealed class LoopingReader : WaveStream
    {
        private readonly AudioFileReader _reader;
        public bool Loop { get; set; }

        public override WaveFormat WaveFormat => _reader.WaveFormat;
        public override long Length => _reader.Length;
        public override long Position
        {
            get => _reader.Position;
            set => _reader.Position = value;
        }

        public LoopingReader(AudioFileReader reader, bool loop)
        {
            _reader = reader;
            Loop = loop;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int totalRead = 0;

            while (totalRead < buffer.Length)
            {
                int read = _reader.Read(buffer[totalRead..]);
                if (read == 0)
                {
                    if (Loop)
                    {
                        _reader.Position = 0; // loop back
                    }
                    else
                    {
                        break; // done, EOF
                    }
                }
                totalRead += read;
            }

            return totalRead;
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
            for (int i = 0; i < read; i++)
            {
                float dry = buffer[i];
                float delayed = _delay[_write];
                float fed = dry + delayed * 0.45f;
                _delay[_write] = fed;
                _write++;
                if (_write >= _delay.Length) _write = 0;
                buffer[i] = dry * (1f - wet) + fed * wet;
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
    /// Handle to a playing audio clip. Controls volume, pause, stop.
    /// </summary>
    public sealed class AudioHandle : IDisposable
    {
        private readonly WaveOut _output;
        private readonly AudioFileReader _reader;
        private readonly LoopingReader _looper;
        private readonly StereoPanProvider _panner;
        private bool _disposed;
        private float _pan;

        public bool IsPlaying => !_disposed && _output.PlaybackState == PlaybackState.Playing;
        public TimeSpan Duration => _reader.TotalTime;

        internal AudioHandle(WaveOut output, AudioFileReader reader, LoopingReader looper, StereoPanProvider panner)
        {
            _output = output;
            _reader = reader;
            _looper = looper;
            _panner = panner;

            // Auto-cleanup when playback finishes naturally
            _output.PlaybackStopped += (s, e) =>
            {
                if (!_looper.Loop) Dispose();
            };
        }

        public float Volume
        {
            get => _reader.Volume;
            set => _reader.Volume = Math.Clamp(value, 0f, 1f);
        }

        public bool Loop
        {
            get => _looper.Loop;
            set => _looper.Loop = value;
        }

        /// <summary>Stereo pan: -1 left, 0 center, +1 right.</summary>
        public float Pan
        {
            get => _pan;
            set
            {
                _pan = Math.Clamp(value, -1f, 1f);
                _panner.Pan = _pan;
            }
        }

        public void Pause()
        {
            if (!_disposed) _output.Pause();
        }

        public void Resume()
        {
            if (!_disposed) _output.Play();
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
        }
    }
#endif
}
