#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
#if WINDOWS && !XBOX_UWP
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
#endif

namespace Game_Engine.Core.Audio
{
#if WINDOWS && !XBOX_UWP
    /// <summary>
    /// Decoded stereo IEEE-float PCM buffer at the shared mixer sample rate (44.1 kHz, 2 channels).
    /// Immutable after creation so any number of concurrent voices can read from it lock-free.
    /// </summary>
    public sealed class CachedAudioClip
    {
        public string FilePath { get; }
        public long LastWriteUtcTicks { get; }
        public float[] Samples { get; }
        public WaveFormat WaveFormat { get; }
        public TimeSpan Duration { get; }
        public long ByteSize => (long)Samples.Length * sizeof(float);

        internal long LastAccessTick;

        public CachedAudioClip(string filePath, long lastWriteUtcTicks, float[] samples, WaveFormat waveFormat)
        {
            FilePath = filePath;
            LastWriteUtcTicks = lastWriteUtcTicks;
            Samples = samples;
            WaveFormat = waveFormat;
            int frames = samples.Length / Math.Max(1, waveFormat.Channels);
            Duration = TimeSpan.FromSeconds((double)frames / Math.Max(1, waveFormat.SampleRate));
            LastAccessTick = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Thread-safe decoded PCM audio cache keyed by normalized file path + last-write timestamp.
    /// Resamples all loaded clips into the common shared mixer format (44100 Hz, 2ch, IEEE float)
    /// and enforces a configurable LRU memory cap.
    /// </summary>
    public static class AudioClipCache
    {
        public const int MixSampleRate = 44100;
        public const int MixChannels = 2;

        public static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels);

        /// <summary>Maximum total cached PCM bytes before LRU eviction (default 256 MB).</summary>
        public static long MaxCacheBytes { get; set; } = 256L * 1024 * 1024;

        private static readonly object s_lock = new();
        private static readonly Dictionary<string, CachedAudioClip> s_cache = new(StringComparer.OrdinalIgnoreCase);
        private static long s_totalBytes;

        static AudioClipCache()
        {
            try
            {
                ProjectService.ProjectClosed += ClearCache;
            }
            catch
            {
                // Ignore if ProjectService is unavailable in standalone host startup.
            }
        }

        public static int CachedClipCount
        {
            get { lock (s_lock) return s_cache.Count; }
        }

        public static long TotalCachedBytes
        {
            get { lock (s_lock) return s_totalBytes; }
        }

        /// <summary>
        /// Fetch a decoded clip from the cache, or decode &amp; resample it from disk on a cache miss
        /// or if the file's last-write timestamp changed.
        /// </summary>
        public static CachedAudioClip? GetOrLoad(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return null;

            string fullPath;
            long lastWriteTicks;
            try
            {
                fullPath = Path.GetFullPath(absolutePath);
                if (!File.Exists(fullPath)) return null;
                lastWriteTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
            }
            catch
            {
                return null;
            }

            lock (s_lock)
            {
                if (s_cache.TryGetValue(fullPath, out var existing) && existing.LastWriteUtcTicks == lastWriteTicks)
                {
                    existing.LastAccessTick = Environment.TickCount64;
                    return existing;
                }
            }

            // Decode outside the lock so disk I/O never stalls other audio operations.
            float[] decoded;
            try
            {
                decoded = DecodeToMixFormat(fullPath);
            }
            catch (Exception ex)
            {
                Log.Warning($"[AudioClipCache] Failed to decode {Path.GetFileName(fullPath)}: {ex.Message}");
                return null;
            }

            var clip = new CachedAudioClip(fullPath, lastWriteTicks, decoded, MixFormat);

            lock (s_lock)
            {
                if (s_cache.TryGetValue(fullPath, out var prev))
                {
                    s_totalBytes -= prev.ByteSize;
                }
                s_cache[fullPath] = clip;
                s_totalBytes += clip.ByteSize;
                EvictIfOverBudgetLocked(fullPath);
            }

            return clip;
        }

        /// <summary>Remove a single clip from the cache (e.g. after reimport).</summary>
        public static void Invalidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return; }

            lock (s_lock)
            {
                if (s_cache.Remove(fullPath, out var removed))
                    s_totalBytes = Math.Max(0, s_totalBytes - removed.ByteSize);
            }
        }

        /// <summary>Clear all cached decoded PCM buffers.</summary>
        public static void ClearCache()
        {
            lock (s_lock)
            {
                s_cache.Clear();
                s_totalBytes = 0;
            }
        }

        private static void EvictIfOverBudgetLocked(string protectedKey)
        {
            long budget = Math.Max(16L * 1024 * 1024, MaxCacheBytes);
            while (s_totalBytes > budget && s_cache.Count > 1)
            {
                string? oldestKey = null;
                long oldestTick = long.MaxValue;

                foreach (var kv in s_cache)
                {
                    if (string.Equals(kv.Key, protectedKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (kv.Value.LastAccessTick < oldestTick)
                    {
                        oldestTick = kv.Value.LastAccessTick;
                        oldestKey = kv.Key;
                    }
                }

                if (oldestKey == null) break;
                if (s_cache.Remove(oldestKey, out var evicted))
                    s_totalBytes = Math.Max(0, s_totalBytes - evicted.ByteSize);
            }
        }

        private static float[] DecodeToMixFormat(string fullPath)
        {
            using var reader = new AudioFileReader(fullPath);
            ISampleProvider provider = reader;

            if (provider.WaveFormat.Channels == 1)
                provider = new MonoToStereoSampleProvider(provider);
            else if (provider.WaveFormat.Channels != MixChannels)
                provider = new MultiplexingSampleProvider(new[] { provider }, MixChannels);

            if (provider.WaveFormat.SampleRate != MixSampleRate)
                provider = new WdlResamplingSampleProvider(provider, MixSampleRate);

            // Pre-size estimate from reader duration to minimize list growth.
            int estimatedSamples = (int)Math.Clamp(
                reader.TotalTime.TotalSeconds * MixSampleRate * MixChannels + 4096,
                4096,
                64 * 1024 * 1024);

            var allSamples = new List<float>(estimatedSamples);
            var chunk = new float[16384];
            int read;
            while ((read = provider.Read(chunk)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    float s = chunk[i];
                    if (!float.IsFinite(s)) s = 0f;
                    allSamples.Add(s);
                }
            }

            // Ensure stereo frame alignment (even sample count).
            if ((allSamples.Count & 1) != 0)
                allSamples.Add(0f);

            return allSamples.ToArray();
        }
    }

    /// <summary>
    /// Lightweight zero-allocation playback voice reading from a <see cref="CachedAudioClip"/>.
    /// Supports Volume, Stereo Pan, Pitch (linear resampling), Loop, Pause/Resume, Stop, and MixerGroup routing.
    /// </summary>
    internal sealed class CachedSoundSampleProvider : ISampleProvider
    {
        private readonly CachedAudioClip _clip;
        private readonly int _frameCount;
        private double _framePos;

        private float _volume;
        private float _pan;
        private float _pitch;
        private volatile bool _loop;
        private volatile bool _paused;
        private volatile bool _stopped;
        private volatile bool _finished;

        public CachedSoundSampleProvider(
            CachedAudioClip clip,
            float volume,
            float pitch,
            bool loop,
            string? mixerGroupName)
        {
            _clip = clip;
            _frameCount = clip.Samples.Length / 2;
            _volume = Math.Clamp(volume, 0f, 2f);
            _pitch = Math.Clamp(pitch <= 0f ? 1f : pitch, 0.1f, 4f);
            _loop = loop;
            MixerGroupName = mixerGroupName;
        }

        public WaveFormat WaveFormat => _clip.WaveFormat;
        public TimeSpan Duration => _clip.Duration;
        public string? MixerGroupName { get; }

        public bool IsPlaying => !_stopped && !_finished && !_paused;
        public bool IsFinished => _stopped || _finished;

        public float Volume
        {
            get => Volatile.Read(ref _volume);
            set => Volatile.Write(ref _volume, Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 2f));
        }

        public float Pan
        {
            get => Volatile.Read(ref _pan);
            set => Volatile.Write(ref _pan, Math.Clamp(float.IsFinite(value) ? value : 0f, -1f, 1f));
        }

        public float Pitch
        {
            get => Volatile.Read(ref _pitch);
            set => Volatile.Write(ref _pitch, Math.Clamp(float.IsFinite(value) && value > 0f ? value : 1f, 0.1f, 4f));
        }

        public bool Loop
        {
            get => _loop;
            set => _loop = value;
        }

        public void Pause() => _paused = true;
        public void Resume() => _paused = false;
        public void Stop() => _stopped = true;

        public int Read(float[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            if (_stopped || _finished || _frameCount <= 0)
                return 0;

            if (_paused)
            {
                buffer.Clear();
                return buffer.Length;
            }

            float vol = Volatile.Read(ref _volume);
            float pan = Volatile.Read(ref _pan);
            float pitch = Volatile.Read(ref _pitch);
            bool loop = _loop;

            float lGain = vol * Math.Clamp(1f - Math.Max(pan, 0f), 0f, 1f);
            float rGain = vol * Math.Clamp(1f + Math.Min(pan, 0f), 0f, 1f);

            float[] src = _clip.Samples;
            int framesRequested = buffer.Length >> 1;
            int outIndex = 0;
            int framesWritten = 0;

            if (MathF.Abs(pitch - 1f) < 1e-4f)
            {
                // Fast 1:1 copy path (no interpolation needed).
                int framePos = (int)_framePos;
                while (framesWritten < framesRequested)
                {
                    if (framePos >= _frameCount)
                    {
                        if (loop)
                        {
                            framePos = 0;
                        }
                        else
                        {
                            _finished = true;
                            break;
                        }
                    }

                    int available = _frameCount - framePos;
                    int toCopy = Math.Min(framesRequested - framesWritten, available);
                    int srcIdx = framePos << 1;

                    for (int f = 0; f < toCopy; f++)
                    {
                        buffer[outIndex++] = src[srcIdx++] * lGain;
                        buffer[outIndex++] = src[srcIdx++] * rGain;
                    }

                    framePos += toCopy;
                    framesWritten += toCopy;
                }
                _framePos = framePos;
            }
            else
            {
                // Linear interpolation resampling for variable pitch.
                double pos = _framePos;
                while (framesWritten < framesRequested)
                {
                    if (pos >= _frameCount)
                    {
                        if (loop)
                        {
                            pos %= _frameCount;
                        }
                        else
                        {
                            _finished = true;
                            break;
                        }
                    }

                    int idx0 = (int)pos;
                    float frac = (float)(pos - idx0);
                    int idx1 = idx0 + 1;
                    if (idx1 >= _frameCount)
                        idx1 = loop ? 0 : _frameCount - 1;

                    int s0 = idx0 << 1;
                    int s1 = idx1 << 1;

                    float l = src[s0] + (src[s1] - src[s0]) * frac;
                    float r = src[s0 + 1] + (src[s1 + 1] - src[s0 + 1]) * frac;

                    buffer[outIndex++] = l * lGain;
                    buffer[outIndex++] = r * rGain;

                    pos += pitch;
                    framesWritten++;
                }
                _framePos = pos;
            }

            return framesWritten << 1;
        }
    }
#else
    /// <summary>Cross-platform stub for AudioClipCache on non-NAudio targets.</summary>
    public static class AudioClipCache
    {
        public static long MaxCacheBytes { get; set; } = 256L * 1024 * 1024;
        public static int CachedClipCount => 0;
        public static long TotalCachedBytes => 0;
        public static void Invalidate(string path) { }
        public static void ClearCache() { }
    }
#endif
}
