#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace Game_Engine.Core
{
    /// <summary>
    /// Low-level audio backend using NAudio.
    /// Each sound gets its own WaveOutEvent for maximum compatibility.
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

                // Wrap for looping
                var loopStream = new LoopingReader(reader, loop);

                var output = new WaveOutEvent();
                output.Init(loopStream);
                output.Play();

                var handle = new AudioHandle(output, reader, loopStream);
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
        {
            int totalRead = 0;

            while (totalRead < count)
            {
                int read = _reader.Read(buffer, offset + totalRead, count - totalRead);
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
    /// Handle to a playing audio clip. Controls volume, pause, stop.
    /// </summary>
    public sealed class AudioHandle : IDisposable
    {
        private readonly WaveOutEvent _output;
        private readonly AudioFileReader _reader;
        private readonly LoopingReader _looper;
        private bool _disposed;

        public bool IsPlaying => !_disposed && _output.PlaybackState == PlaybackState.Playing;
        public TimeSpan Duration => _reader.TotalTime;

        internal AudioHandle(WaveOutEvent output, AudioFileReader reader, LoopingReader looper)
        {
            _output = output;
            _reader = reader;
            _looper = looper;

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

        /// <summary>Stereo pan: -1 left, 0 center, +1 right. (Approximate via volume for now.)</summary>
        public float Pan { get; set; }

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
}
