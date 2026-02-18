#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Component;
using Game_Engine.Core.Events;

namespace Game_Engine.Core.Timeline
{
    /// <summary>
    /// Raised when a timeline event track fires.
    /// </summary>
    public struct TimelineEventFired
    {
        public string EventName { get; set; }
        public string EventData { get; set; }
        public float Time { get; set; }
    }

    /// <summary>
    /// Component that plays a Timeline asset. Controls playback (play, pause, seek, speed).
    /// </summary>
    [ComponentCategory("Timeline")]
    public sealed class TimelinePlayer : Behavior
    {
        /// <summary>The timeline to play.</summary>
        public TimelineAsset? Timeline { get; set; }

        [Persist] public bool PlayOnAwake { get; set; } = false;
        [Persist] public float Speed { get; set; } = 1f;

        /// <summary>Current playback time in seconds.</summary>
        public float CurrentTime { get; private set; }

        /// <summary>Whether the timeline is currently playing.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>Whether the timeline has finished (non-looping).</summary>
        public bool IsFinished { get; private set; }

        /// <summary>Event raised when playback completes.</summary>
        public event Action? OnComplete;

        private readonly HashSet<int> _firedEvents = new();
        private readonly Dictionary<string, bool> _originalActivation = new();
        private readonly Dictionary<int, AudioHandle?> _activeAudio = new();

        public override void Start()
        {
            if (PlayOnAwake && Timeline != null)
                Play();
        }

        public override void Update()
        {
            if (!IsPlaying || Timeline == null) return;

            float prevTime = CurrentTime;
            CurrentTime += Time.deltaTime * Speed;

            foreach (var track in Timeline.Tracks)
            {
                if (track.Muted) continue;
                ProcessTrack(track, prevTime, CurrentTime);
            }

            if (CurrentTime >= Timeline.Duration)
            {
                if (Timeline.Loop)
                {
                    CurrentTime -= Timeline.Duration;
                    _firedEvents.Clear();
                    StopAllAudio();
                }
                else
                {
                    CurrentTime = Timeline.Duration;
                    IsPlaying = false;
                    IsFinished = true;
                    StopAllAudio();
                    OnComplete?.Invoke();
                }
            }
        }

        public void Play()
        {
            IsPlaying = true;
            IsFinished = false;
        }

        public void Pause() => IsPlaying = false;

        public void Stop()
        {
            IsPlaying = false;
            CurrentTime = 0f;
            IsFinished = false;
            _firedEvents.Clear();
            RestoreActivations();
            StopAllAudio();
        }

        public void Seek(float time)
        {
            CurrentTime = Math.Clamp(time, 0f, Timeline?.Duration ?? 0f);
            _firedEvents.Clear();
            StopAllAudio();
        }

        private void ProcessTrack(TimelineTrack track, float prevTime, float currentTime)
        {
            switch (track.Type)
            {
                case TrackType.Activation:
                    ProcessActivationTrack(track, currentTime);
                    break;
                case TrackType.Event:
                    ProcessEventTrack(track, prevTime, currentTime);
                    break;
                case TrackType.Camera:
                    ProcessCameraTrack(track, currentTime);
                    break;
                case TrackType.Animation:
                    ProcessAnimationTrack(track, currentTime);
                    break;
                case TrackType.Audio:
                    ProcessAudioTrack(track, prevTime, currentTime);
                    break;
            }
        }

        private void ProcessAnimationTrack(TimelineTrack track, float time)
        {
            foreach (var clip in track.Clips)
            {
                var target = FindGameObject(clip.TargetName);
                if (target == null) continue;

                Animator? animator = null;
                foreach (var b in target.Behaviors)
                    if (b is Animator a) { animator = a; break; }
                if (animator == null) continue;

                if (clip.Contains(time))
                {
                    if (!string.IsNullOrEmpty(clip.AssetPath))
                        animator.Play(clip.AssetPath, 0.1f);
                }
            }
        }

        private void ProcessAudioTrack(TimelineTrack track, float prevTime, float currentTime)
        {
            for (int i = 0; i < track.Clips.Count; i++)
            {
                var clip = track.Clips[i];
                int clipKey = HashCode.Combine(track.GetHashCode(), i);

                if (clip.Contains(currentTime))
                {
                    if (!_activeAudio.ContainsKey(clipKey) && prevTime < clip.StartTime)
                    {
                        float vol = Math.Clamp(clip.Speed, 0f, 2f);
                        var handle = AudioBackend.Play(clip.AssetPath, vol > 0 ? vol : 1f, 1f, false);
                        _activeAudio[clipKey] = handle;
                    }
                }
                else
                {
                    if (_activeAudio.TryGetValue(clipKey, out var handle))
                    {
                        handle?.Stop();
                        _activeAudio.Remove(clipKey);
                    }
                }
            }
        }

        private void StopAllAudio()
        {
            foreach (var kv in _activeAudio)
                kv.Value?.Stop();
            _activeAudio.Clear();
        }

        private void ProcessActivationTrack(TimelineTrack track, float time)
        {
            foreach (var clip in track.Clips)
            {
                var target = FindGameObject(clip.TargetName);
                if (target == null) continue;

                if (!_originalActivation.ContainsKey(clip.TargetName))
                    _originalActivation[clip.TargetName] = target.Enabled;

                target.Enabled = clip.Contains(time);
            }
        }

        private void ProcessEventTrack(TimelineTrack track, float prevTime, float currentTime)
        {
            for (int i = 0; i < track.Clips.Count; i++)
            {
                var clip = track.Clips[i];
                int clipHash = HashCode.Combine(track.GetHashCode(), i);

                if (!_firedEvents.Contains(clipHash) &&
                    prevTime < clip.StartTime && currentTime >= clip.StartTime)
                {
                    _firedEvents.Add(clipHash);
                    EventBus.Publish(new TimelineEventFired
                    {
                        EventName = clip.EventName,
                        EventData = clip.EventData,
                        Time = clip.StartTime
                    });
                }
            }
        }

        private void ProcessCameraTrack(TimelineTrack track, float time)
        {
            foreach (var clip in track.Clips)
            {
                var target = FindGameObject(clip.TargetName);
                if (target == null) continue;
                target.Enabled = clip.Contains(time);
            }
        }

        private void RestoreActivations()
        {
            foreach (var (name, wasEnabled) in _originalActivation)
            {
                var go = FindGameObject(name);
                if (go != null) go.Enabled = wasEnabled;
            }
            _originalActivation.Clear();
        }

        private static GameObject? FindGameObject(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var root in SceneService.Root)
            {
                var found = FindByName(root, name);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject? FindByName(GameObject go, string name)
        {
            if (go.Name == name) return go;
            foreach (var child in go.Children)
            {
                var found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
