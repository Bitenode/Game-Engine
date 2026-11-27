#if XBOX_UWP
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Storage;

namespace Game_Engine.Core
{
    /// <summary>WAV and MP3 through AudioGraph. Ogg is logged and skipped.</summary>
    public static class AudioBackend
    {
        static AudioGraph? _graph;
        static AudioDeviceOutputNode? _output;
        static bool _failed;
        static readonly List<WeakReference<AudioHandle>> Handles = new();

        public static string? ResolveAudioPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            if (Path.IsPathRooted(filePath) && File.Exists(filePath)) return filePath;
            var root = ProjectService.Current?.RootPath;
            if (!string.IsNullOrEmpty(root))
            {
                var c = Path.GetFullPath(Path.Combine(root, filePath));
                if (File.Exists(c)) return c;
                c = Path.GetFullPath(Path.Combine(root, "Assets", filePath));
                if (File.Exists(c)) return c;
            }
            if (File.Exists(filePath)) return Path.GetFullPath(filePath);
            // Clip stored as an absolute path on the authoring machine.
            return AssetPathRemap.FindExisting(filePath);
        }
        public static System.Numerics.Vector3 LastListenerPosition { get; private set; }

        public static void EnsureInit() { }

        public static void SetListenerPosition(System.Numerics.Vector3 pos, System.Numerics.Vector3 forward, System.Numerics.Vector3 up)
            => LastListenerPosition = pos;

        public static AudioHandle? Play(string filePath, float volume, float pitch, bool loop, string? mixerGroup = null)
        {
            var abs = ResolveAudioPath(filePath);
            if (abs == null) return null;
            var ext = Path.GetExtension(abs).ToLowerInvariant();
            if (ext is ".ogg" or ".flac")
            {
                Log.Warning("[AudioBackend] Xbox AudioGraph does not decode " + ext + " (" + Path.GetFileName(abs) + ").");
                return null;
            }
            EnsureGraph();
            if (_graph == null) return null;
            try
            {
                var file = Wait(StorageFile.GetFileFromPathAsync(abs));
                var created = Wait(_graph.CreateFileInputNodeAsync(file));
                if (created.Status != AudioFileNodeCreationStatus.Success || created.FileInputNode == null)
                {
                    Log.Warning("[AudioBackend] Could not open " + Path.GetFileName(abs));
                    return null;
                }
                var node = created.FileInputNode;
                node.LoopCount = loop ? null : 0;
                node.PlaybackSpeedFactor = Math.Max(0.25, pitch <= 0 ? 1 : pitch);
                node.OutgoingGain = volume;
                node.AddOutgoingConnection(_output);
                node.Start();
                var handle = new AudioHandle(node);
                Handles.Add(new WeakReference<AudioHandle>(handle));
                return handle;
            }
            catch (Exception ex)
            {
                Log.Warning("[AudioBackend] " + ex.Message);
                return null;
            }
        }

        public static void PlayOneShot(string filePath, float volume = 1f, string? mixerGroup = null)
            => Play(filePath, volume, 1f, false, mixerGroup);

        public static void ClearCache() { }

        public static void StopAll()
        {
            foreach (var w in Handles)
                if (w.TryGetTarget(out var h)) { try { h.Stop(); } catch { } }
            Handles.Clear();
        }

        public static void Shutdown()
        {
            StopAll();
            _graph?.Stop();
            _graph = null;
        }

        static void EnsureGraph()
        {
            if (_graph != null || _failed) return;
            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.GameEffects);
                var created = Wait(AudioGraph.CreateAsync(settings));
                if (created.Status != AudioGraphCreationStatus.Success || created.Graph == null)
                {
                    _failed = true;
                    return;
                }
                _graph = created.Graph;
                var output = Wait(_graph.CreateDeviceOutputNodeAsync());
                if (output.Status != AudioDeviceNodeCreationStatus.Success || output.DeviceOutputNode == null)
                {
                    _failed = true;
                    _graph = null;
                    return;
                }
                _output = output.DeviceOutputNode;
                _graph.Start();
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Warning("[AudioBackend] AudioGraph init failed: " + ex.Message);
            }
        }

        static T Wait<T>(IAsyncOperation<T> op)
        {
            if (op.Status == AsyncStatus.Completed) return op.GetResults();
            using var done = new ManualResetEventSlim(false);
            T result = default!;
            Exception? error = null;
            op.Completed = (o, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed) result = o.GetResults();
                    else error = new InvalidOperationException(status.ToString());
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            };
            done.Wait();
            if (error != null) throw error;
            return result;
        }
    }

    public sealed class AudioHandle : IDisposable
    {
        readonly AudioFileInputNode _node;
        bool _disposed;
        float _volume = 1f, _pan;

        internal AudioHandle(AudioFileInputNode node) { _node = node; Duration = node.Duration; }

        public bool IsPlaying => !_disposed;
        public TimeSpan Duration { get; }
        public float Volume
        {
            get => _volume;
            set { _volume = value; if (!_disposed) _node.OutgoingGain = value; }
        }
        public float Pitch
        {
            get => (float)_node.PlaybackSpeedFactor;
            set { if (!_disposed) _node.PlaybackSpeedFactor = Math.Max(0.25, value <= 0 ? 1 : value); }
        }
        public bool Loop
        {
            get => _node.LoopCount == null;
            set { if (!_disposed) _node.LoopCount = value ? null : 0; }
        }
        public float Pan { get => _pan; set => _pan = value; }
        public void Pause() { if (!_disposed) _node.Stop(); }
        public void Resume() { if (!_disposed) _node.Start(); }
        public void Stop() => Dispose();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _node.Stop(); } catch { }
        }
    }
}
#endif
