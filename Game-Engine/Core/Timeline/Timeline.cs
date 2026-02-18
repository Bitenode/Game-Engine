#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Timeline
{
    /// <summary>Type of timeline track.</summary>
    public enum TrackType
    {
        Animation,
        Camera,
        Audio,
        Activation,
        Event
    }

    /// <summary>
    /// A clip on a timeline track with start time, duration, and blend settings.
    /// </summary>
    public class TimelineClip
    {
        /// <summary>Start time in seconds.</summary>
        [Persist] public float StartTime { get; set; }
        /// <summary>Duration in seconds.</summary>
        [Persist] public float Duration { get; set; } = 1f;
        /// <summary>Blend-in duration for crossfade.</summary>
        [Persist] public float BlendIn { get; set; } = 0f;
        /// <summary>Blend-out duration for crossfade.</summary>
        [Persist] public float BlendOut { get; set; } = 0f;
        /// <summary>Playback speed multiplier.</summary>
        [Persist] public float Speed { get; set; } = 1f;

        // ── Track-specific data ──
        /// <summary>Asset path for animation/audio clips.</summary>
        [Persist] public string AssetPath { get; set; } = "";
        /// <summary>Target GameObject name for activation/camera tracks.</summary>
        [Persist] public string TargetName { get; set; } = "";
        /// <summary>Event name to publish for event tracks.</summary>
        [Persist] public string EventName { get; set; } = "";
        /// <summary>String data for events.</summary>
        [Persist] public string EventData { get; set; } = "";

        /// <summary>The end time of this clip.</summary>
        public float EndTime => StartTime + Duration;

        /// <summary>Check if a given time falls within this clip.</summary>
        public bool Contains(float time) => time >= StartTime && time < EndTime;

        /// <summary>Get the local time within this clip (0 to Duration).</summary>
        public float LocalTime(float globalTime) => (globalTime - StartTime) * Speed;

        /// <summary>Get the blend weight at the given time (0-1).</summary>
        public float GetWeight(float globalTime)
        {
            float local = globalTime - StartTime;
            float w = 1f;
            if (BlendIn > 0f && local < BlendIn)
                w = local / BlendIn;
            float remaining = Duration - local;
            if (BlendOut > 0f && remaining < BlendOut)
                w = Math.Min(w, remaining / BlendOut);
            return Math.Clamp(w, 0f, 1f);
        }
    }

    /// <summary>
    /// A track in a timeline. Contains ordered clips of a specific type.
    /// </summary>
    public class TimelineTrack
    {
        [Persist] public string Name { get; set; } = "Track";
        [Persist] public TrackType Type { get; set; } = TrackType.Animation;
        [Persist] public bool Muted { get; set; } = false;
        [Persist] public List<TimelineClip> Clips { get; set; } = new();

        /// <summary>Get the active clip at the given time, or null.</summary>
        public TimelineClip? GetActiveClip(float time)
        {
            for (int i = 0; i < Clips.Count; i++)
                if (Clips[i].Contains(time)) return Clips[i];
            return null;
        }
    }

    /// <summary>
    /// Timeline asset: an ordered list of tracks, each containing clips on a shared time ruler.
    /// </summary>
    public class TimelineAsset
    {
        [Persist] public string Name { get; set; } = "New Timeline";
        [Persist] public float Duration { get; set; } = 10f;
        [Persist] public bool Loop { get; set; } = false;
        [Persist] public List<TimelineTrack> Tracks { get; set; } = new();

        /// <summary>Add a new track.</summary>
        public TimelineTrack AddTrack(string name, TrackType type)
        {
            var track = new TimelineTrack { Name = name, Type = type };
            Tracks.Add(track);
            return track;
        }

        /// <summary>Remove a track by reference.</summary>
        public bool RemoveTrack(TimelineTrack track) => Tracks.Remove(track);

        /// <summary>Recalculate duration from the latest clip end time.</summary>
        public void RecalculateDuration()
        {
            float max = 0f;
            foreach (var track in Tracks)
                foreach (var clip in track.Clips)
                    if (clip.EndTime > max) max = clip.EndTime;
            Duration = max;
        }
    }
}
