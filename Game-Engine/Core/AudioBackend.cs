#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using Silk.NET.OpenAL;

namespace Game_Engine.Core
{
    /// <summary>
    /// Cross-platform audio backend using OpenAL (via Silk.NET).
    /// Works on Windows, macOS, and Linux.
    /// NAudio is kept only for audio file decoding (WAV/MP3/etc.).
    /// OpenAL handles all playback and 3D spatial audio natively.
    /// </summary>
    public static class AudioBackend
    {
        private static bool s_available;
        private static bool s_initialized;

        // OpenAL state
        private static AL? s_al;
        private static ALContext? s_alc;
        private static unsafe Device* s_device;
        private static unsafe Context* s_context;

        // Track all active handles so we can stop them all on game stop
        private static readonly List<WeakReference<AudioHandle>> s_activeHandles = new();

        /// <summary>The OpenAL API instance. Null if not initialized.</summary>
        internal static AL? AL => s_al;

        /// <summary>
        /// Initialize the OpenAL audio device and context.
        /// Safe to call multiple times — only initializes once.
        /// </summary>
        public static unsafe void EnsureInit()
        {
            if (s_initialized) return;
            s_initialized = true;

            try
            {
                s_alc = ALContext.GetApi();
                s_device = s_alc.OpenDevice(null); // default device
                if (s_device == null)
                {
                    Log.Warning("[AudioBackend] Failed to open OpenAL device — audio disabled.");
                    s_available = false;
                    return;
                }

                s_context = s_alc.CreateContext(s_device, null);
                if (s_context == null)
                {
                    Log.Warning("[AudioBackend] Failed to create OpenAL context — audio disabled.");
                    s_alc.CloseDevice(s_device);
                    s_device = null;
                    s_available = false;
                    return;
                }

                s_alc.MakeContextCurrent(s_context);
                s_al = AL.GetApi();

                // Default listener at origin
                s_al.SetListenerProperty(ListenerFloat.Gain, 1.0f);

                s_available = true;
                Log.Info("[AudioBackend] OpenAL initialized (cross-platform audio ready).");
            }
            catch (Exception ex)
            {
                Log.Warning($"[AudioBackend] OpenAL init failed: {ex.Message} — audio disabled.");
                s_available = false;
            }
        }

        /// <summary>
        /// Update the OpenAL listener position and orientation from the AudioListener component.
        /// Call this each frame from AudioManager.
        /// </summary>
        public static void UpdateListener(
            System.Numerics.Vector3 position,
            System.Numerics.Vector3 forward,
            System.Numerics.Vector3 up,
            float gain)
        {
            if (!s_available || s_al == null) return;

            s_al.SetListenerProperty(ListenerFloat.Gain, Math.Clamp(gain, 0f, 1f));

            // Position
            s_al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);

            // Orientation: (forward, up) as 6 floats
            unsafe
            {
                float* ori = stackalloc float[6];
                ori[0] = forward.X; ori[1] = forward.Y; ori[2] = forward.Z;
                ori[3] = up.X; ori[4] = up.Y; ori[5] = up.Z;
                s_al.SetListenerProperty(ListenerFloatArray.Orientation, ori);
            }
        }

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
        /// Decode an audio file to raw PCM data using NAudio.
        /// Returns the PCM bytes, sample rate, channels, and bits per sample.
        /// </summary>
        internal static (byte[] pcm, int sampleRate, int channels, int bitsPerSample)? DecodeAudio(string absPath)
        {
            try
            {
                using var reader = new AudioFileReader(absPath);

                // Convert to 16-bit PCM for OpenAL compatibility
                var format = new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels);
                using var resampler = new MediaFoundationResampler(reader, format);
                resampler.ResamplerQuality = 60;

                using var ms = new MemoryStream();
                var buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    ms.Write(buffer, 0, bytesRead);

                return (ms.ToArray(), format.SampleRate, format.Channels, format.BitsPerSample);
            }
            catch
            {
                // MediaFoundationResampler may not be available on all platforms.
                // Fall back to reading float samples and converting manually.
                try
                {
                    return DecodeFallback(absPath);
                }
                catch (Exception ex2)
                {
                    Log.Warning($"[AudioBackend] Decode failed for {Path.GetFileName(absPath)}: {ex2.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Fallback decoder: read float samples from NAudio and convert to 16-bit PCM manually.
        /// Works on all platforms (no MediaFoundation dependency).
        /// </summary>
        private static (byte[] pcm, int sampleRate, int channels, int bitsPerSample)? DecodeFallback(string absPath)
        {
            using var reader = new AudioFileReader(absPath);
            int sampleRate = reader.WaveFormat.SampleRate;
            int channels = reader.WaveFormat.Channels;

            var floatBuffer = new float[4096];
            using var ms = new MemoryStream();
            int samplesRead;
            while ((samplesRead = reader.Read(floatBuffer, 0, floatBuffer.Length)) > 0)
            {
                for (int i = 0; i < samplesRead; i++)
                {
                    float sample = Math.Clamp(floatBuffer[i], -1f, 1f);
                    short s16 = (short)(sample * 32767f);
                    ms.WriteByte((byte)(s16 & 0xFF));
                    ms.WriteByte((byte)((s16 >> 8) & 0xFF));
                }
            }

            return (ms.ToArray(), sampleRate, channels, 16);
        }

        /// <summary>
        /// Get the OpenAL buffer format for the given channel count and bit depth.
        /// </summary>
        internal static BufferFormat GetALFormat(int channels, int bitsPerSample)
        {
            if (channels == 1)
                return bitsPerSample == 16 ? BufferFormat.Mono16 : BufferFormat.Mono8;
            return bitsPerSample == 16 ? BufferFormat.Stereo16 : BufferFormat.Stereo8;
        }

        /// <summary>
        /// Play an audio file. Returns a handle for volume/pause/stop control.
        /// Uses OpenAL for playback with native 3D spatial audio support.
        /// </summary>
        public static AudioHandle? Play(string filePath, float volume, float pitch, bool loop)
        {
            if (!s_available || s_al == null || string.IsNullOrWhiteSpace(filePath)) return null;

            string? absPath = ResolveAudioPath(filePath);
            if (absPath == null)
            {
                Log.Warning($"[AudioBackend] File not found: {filePath}");
                return null;
            }

            try
            {
                var decoded = DecodeAudio(absPath);
                if (decoded == null) return null;

                var (pcm, sampleRate, channels, bitsPerSample) = decoded.Value;
                var format = GetALFormat(channels, bitsPerSample);

                // Create OpenAL buffer
                uint buffer = s_al.GenBuffer();
                unsafe
                {
                    fixed (byte* pData = pcm)
                    {
                        s_al.BufferData(buffer, format, pData, pcm.Length, sampleRate);
                    }
                }

                // Create OpenAL source
                uint source = s_al.GenSource();
                s_al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
                s_al.SetSourceProperty(source, SourceFloat.Gain, Math.Clamp(volume, 0f, 2f));
                s_al.SetSourceProperty(source, SourceFloat.Pitch, Math.Clamp(pitch, 0.1f, 4f));
                s_al.SetSourceProperty(source, SourceBoolean.Looping, loop);

                // Default: non-spatial (relative to listener) until AudioSource configures it
                s_al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
                s_al.SetSourceProperty(source, SourceVector3.Position, 0f, 0f, 0f);

                s_al.SourcePlay(source);

                var handle = new AudioHandle(s_al, source, buffer, loop);
                lock (s_activeHandles) s_activeHandles.Add(new WeakReference<AudioHandle>(handle));
                Log.Info($"[AudioBackend] Now playing: {Path.GetFileName(absPath)}");
                return handle;
            }
            catch (Exception ex)
            {
                Log.Warning($"[AudioBackend] Failed to play {Path.GetFileName(absPath)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Play a one-shot sound (fire-and-forget).</summary>
        public static void PlayOneShot(string filePath, float volume = 1f)
        {
            var handle = Play(filePath, volume, 1f, loop: false);
            // Handle auto-disposes when playback ends via polling or GC
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

        /// <summary>Shut down — stops all audio and releases OpenAL resources.</summary>
        public static unsafe void Shutdown()
        {
            StopAll();
            s_available = false;

            if (s_al != null)
            {
                s_al.Dispose();
                s_al = null;
            }

            if (s_alc != null)
            {
                if (s_context != null)
                {
                    s_alc.MakeContextCurrent(null);
                    s_alc.DestroyContext(s_context);
                    s_context = null;
                }
                if (s_device != null)
                {
                    s_alc.CloseDevice(s_device);
                    s_device = null;
                }
                s_alc.Dispose();
                s_alc = null;
            }

            s_initialized = false;
            Log.Info("[AudioBackend] OpenAL shut down.");
        }
    }

    /// <summary>
    /// Handle to a playing audio clip via OpenAL. Controls volume, pitch, pan, position, stop.
    /// </summary>
    public sealed class AudioHandle : IDisposable
    {
        private readonly AL _al;
        private readonly uint _source;
        private readonly uint _buffer;
        private bool _disposed;
        private bool _loop;

        public bool IsPlaying
        {
            get
            {
                if (_disposed) return false;
                _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                return state == (int)SourceState.Playing;
            }
        }

        internal AudioHandle(AL al, uint source, uint buffer, bool loop)
        {
            _al = al;
            _source = source;
            _buffer = buffer;
            _loop = loop;
        }

        /// <summary>OpenAL source ID for advanced control.</summary>
        internal uint SourceId => _source;

        public float Volume
        {
            get { _al.GetSourceProperty(_source, SourceFloat.Gain, out float v); return v; }
            set { if (!_disposed) _al.SetSourceProperty(_source, SourceFloat.Gain, Math.Clamp(value, 0f, 2f)); }
        }

        public float Pitch
        {
            get { _al.GetSourceProperty(_source, SourceFloat.Pitch, out float v); return v; }
            set { if (!_disposed) _al.SetSourceProperty(_source, SourceFloat.Pitch, Math.Clamp(value, 0.1f, 4f)); }
        }

        public bool Loop
        {
            get => _loop;
            set
            {
                _loop = value;
                if (!_disposed) _al.SetSourceProperty(_source, SourceBoolean.Looping, value);
            }
        }

        /// <summary>Set the 3D position of this audio source in world space.</summary>
        public void SetPosition(float x, float y, float z)
        {
            if (_disposed) return;
            _al.SetSourceProperty(_source, SourceBoolean.SourceRelative, false);
            _al.SetSourceProperty(_source, SourceVector3.Position, x, y, z);
        }

        /// <summary>Set the velocity for Doppler effect.</summary>
        public void SetVelocity(float x, float y, float z)
        {
            if (_disposed) return;
            _al.SetSourceProperty(_source, SourceVector3.Velocity, x, y, z);
        }

        /// <summary>Configure distance attenuation model parameters.</summary>
        public void SetDistanceModel(float refDistance, float maxDistance, float rolloff)
        {
            if (_disposed) return;
            _al.SetSourceProperty(_source, SourceFloat.ReferenceDistance, refDistance);
            _al.SetSourceProperty(_source, SourceFloat.MaxDistance, maxDistance);
            _al.SetSourceProperty(_source, SourceFloat.RolloffFactor, rolloff);
        }

        /// <summary>Stereo pan: -1 left, 0 center, +1 right. (For 2D sounds only.)</summary>
        public float Pan { get; set; }

        public void Pause()
        {
            if (!_disposed) _al.SourcePause(_source);
        }

        public void Resume()
        {
            if (!_disposed) _al.SourcePlay(_source);
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _al.SourceStop(_source); } catch { }
            try { _al.DeleteSource(_source); } catch { }
            try { _al.DeleteBuffer(_buffer); } catch { }
        }
    }
}
