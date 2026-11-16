#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SN = System.Numerics;

namespace Game_Engine.Core;

// ============================================================
//  BoneKeyframe — a single keyframe for one bone
// ============================================================

public struct BoneKeyframe
{
    public float Time;
    public SN.Vector3 Position;
    public SN.Quaternion Rotation;
    public SN.Vector3 Scale;

    public BoneKeyframe(float time, SN.Vector3 pos, SN.Quaternion rot, SN.Vector3 scale)
    {
        Time = time;
        Position = pos;
        Rotation = rot;
        Scale = scale;
    }
}

// ============================================================
//  BoneTrack — keyframes for a single bone
// ============================================================

public sealed class BoneTrack
{
    public string BoneName { get; set; } = "";
    public int BoneIndex { get; set; }
    public List<BoneKeyframe> Keyframes { get; set; } = new();

    /// <summary>Sample the bone transform at the given time using Lerp/Slerp interpolation.</summary>
    public BonePose Sample(float time)
    {
        if (Keyframes.Count == 0) return BonePose.Identity;
        if (Keyframes.Count == 1)
        {
            var k = Keyframes[0];
            return new BonePose { Position = k.Position, Rotation = k.Rotation, Scale = k.Scale, IsSet = true };
        }

        // Clamp to range
        if (time <= Keyframes[0].Time)
        {
            var k = Keyframes[0];
            return new BonePose { Position = k.Position, Rotation = k.Rotation, Scale = k.Scale, IsSet = true };
        }
        if (time >= Keyframes[^1].Time)
        {
            var k = Keyframes[^1];
            return new BonePose { Position = k.Position, Rotation = k.Rotation, Scale = k.Scale, IsSet = true };
        }

        // Find surrounding keyframes
        int i1 = 0;
        for (int i = 0; i < Keyframes.Count - 1; i++)
        {
            if (Keyframes[i + 1].Time >= time) { i1 = i; break; }
        }
        int i2 = i1 + 1;

        var k0 = Keyframes[i1];
        var k1 = Keyframes[i2];
        float seg = k1.Time - k0.Time;
        float t = seg > 0f ? (time - k0.Time) / seg : 0f;

        return new BonePose
        {
            Position = SN.Vector3.Lerp(k0.Position, k1.Position, t),
            Rotation = SN.Quaternion.Slerp(k0.Rotation, k1.Rotation, t),
            Scale = SN.Vector3.Lerp(k0.Scale, k1.Scale, t),
            IsSet = true
        };
    }
}

// ============================================================
//  BoneAnimationClip — a complete bone-based animation
// ============================================================

public sealed class BoneAnimationClip
{
    public string Name { get; set; } = "";
    public float Duration { get; set; } = 1f;
    public bool Loop { get; set; } = true;
    public List<BoneTrack> Tracks { get; set; } = new();

    private Dictionary<int, BoneTrack>? _indexedTracks;

    /// <summary>Get the track for a given bone index, or null.</summary>
    public BoneTrack? GetTrack(int boneIndex)
    {
        if (_indexedTracks == null)
        {
            _indexedTracks = new Dictionary<int, BoneTrack>(Tracks.Count);
            foreach (var t in Tracks)
                _indexedTracks[t.BoneIndex] = t;
        }
        return _indexedTracks.TryGetValue(boneIndex, out var track) ? track : null;
    }

    /// <summary>Get the track for a given bone name, or null.</summary>
    public BoneTrack? GetTrack(string boneName)
        => Tracks.FirstOrDefault(t => string.Equals(t.BoneName, boneName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sample all bone poses at the given time. Array indexed by bone index.</summary>
    public BonePose[] SampleAllBones(int boneCount, float time)
    {
        var poses = new BonePose[boneCount];
        SampleAllBonesInto(poses, boneCount, time);
        return poses;
    }

    /// <summary>Same as <see cref="SampleAllBones"/> into a caller-owned array, for per-frame playback.</summary>
    public void SampleAllBonesInto(BonePose[] poses, int boneCount, float time)
    {
        int n = Math.Min(boneCount, poses.Length);
        for (int i = 0; i < n; i++)
            poses[i] = BonePose.Identity;

        foreach (var track in Tracks)
        {
            if (track.BoneIndex >= 0 && track.BoneIndex < n)
                poses[track.BoneIndex] = track.Sample(time);
        }
    }

    /// <summary>Invalidate the indexed track cache (call after modifying Tracks list).</summary>
    public void InvalidateCache() => _indexedTracks = null;
}

// ============================================================
//  BoneAnimationClipAsset — .boneanim JSON save/load + caching
// ============================================================

public static class BoneAnimationClipAsset
{
    private static readonly Dictionary<string, (BoneAnimationClip clip, string relPath)> _cache = new();
    private static readonly Dictionary<BoneAnimationClip, string> _reverseLookup = new();

    // Clips by file. The table above keeps one clip per clip name (the animation panel
    // lists it), so two rigs that each ship an "Idle" pushed each other out and every
    // animator built after that re-read and re-parsed its clips from disk.
    private static readonly Dictionary<string, BoneAnimationClip> _byPath = new(StringComparer.OrdinalIgnoreCase);

    // Rig imports run on worker threads while the game thread builds animators.
    private static readonly object _sync = new();

    private static string PathKey(string relPath) => relPath.Replace('\\', '/');

    private static void Remember(BoneAnimationClip clip, string projectRelativePath)
    {
        lock (_sync)
        {
            _cache[clip.Name] = (clip, projectRelativePath);
            _reverseLookup[clip] = projectRelativePath;
            _byPath[PathKey(projectRelativePath)] = clip;
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── DTOs ──

    private class ClipDTO
    {
        public string Name { get; set; } = "";
        public float Duration { get; set; }
        public bool Loop { get; set; }
        public List<TrackDTO> Tracks { get; set; } = new();
    }

    private class TrackDTO
    {
        public string BoneName { get; set; } = "";
        public int BoneIndex { get; set; }
        public List<BoneKeyDTO> Keys { get; set; } = new();
    }

    private class BoneKeyDTO
    {
        public float T { get; set; } // time
        public float Px { get; set; }
        public float Py { get; set; }
        public float Pz { get; set; }
        public float Rx { get; set; }
        public float Ry { get; set; }
        public float Rz { get; set; }
        public float Rw { get; set; } = 1f;
        public float Sx { get; set; } = 1f;
        public float Sy { get; set; } = 1f;
        public float Sz { get; set; } = 1f;
    }

    // ── Save ──

    public static void Save(BoneAnimationClip clip, string projectRelativePath)
    {
        var absPath = ResolveAbsolute(projectRelativePath);
        if (absPath == null) return;

#if PLAYER
        // A built player never writes assets (the Xbox package is read-only). The clip that was
        // packed with the build stays the one on disk; a clip with no file is kept in memory.
        if (!File.Exists(absPath))
            Remember(clip, projectRelativePath);
        return;
#else
        var dto = new ClipDTO { Name = clip.Name, Duration = clip.Duration, Loop = clip.Loop };

        foreach (var track in clip.Tracks)
        {
            var td = new TrackDTO { BoneName = track.BoneName, BoneIndex = track.BoneIndex };
            foreach (var k in track.Keyframes)
            {
                td.Keys.Add(new BoneKeyDTO
                {
                    T = k.Time,
                    Px = k.Position.X, Py = k.Position.Y, Pz = k.Position.Z,
                    Rx = k.Rotation.X, Ry = k.Rotation.Y, Rz = k.Rotation.Z, Rw = k.Rotation.W,
                    Sx = k.Scale.X, Sy = k.Scale.Y, Sz = k.Scale.Z
                });
            }
            dto.Tracks.Add(td);
        }

        var dir = Path.GetDirectoryName(absPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(absPath, JsonSerializer.Serialize(dto, _jsonOpts));

        Remember(clip, projectRelativePath);
#endif
    }

    // ── Load ──

    public static BoneAnimationClip? Load(string projectRelativePath)
    {
        if (string.IsNullOrWhiteSpace(projectRelativePath)) return null;

        lock (_sync)
        {
            if (_byPath.TryGetValue(PathKey(projectRelativePath), out var cached))
                return cached;
        }

        var absPath = ResolveAbsolute(projectRelativePath);
        if (absPath == null || !File.Exists(absPath)) return null;

        try
        {
            var json = File.ReadAllText(absPath);
            var dto = JsonSerializer.Deserialize<ClipDTO>(json, _jsonOpts);
            if (dto == null) return null;

            var clip = new BoneAnimationClip { Name = dto.Name, Duration = dto.Duration, Loop = dto.Loop };

            foreach (var td in dto.Tracks)
            {
                var track = new BoneTrack { BoneName = td.BoneName, BoneIndex = td.BoneIndex };
                foreach (var k in td.Keys)
                {
                    track.Keyframes.Add(new BoneKeyframe(
                        k.T,
                        new SN.Vector3(k.Px, k.Py, k.Pz),
                        new SN.Quaternion(k.Rx, k.Ry, k.Rz, k.Rw),
                        new SN.Vector3(k.Sx, k.Sy, k.Sz)
                    ));
                }
                clip.Tracks.Add(track);
            }

            lock (_sync)
            {
                // Two threads can parse the same file. Keep the first so every animator shares one clip.
                if (_byPath.TryGetValue(PathKey(projectRelativePath), out var raced))
                    return raced;
            }
            Remember(clip, projectRelativePath);
            return clip;
        }
        catch { return null; }
    }

    // ── Helpers ──

    public static string? GetPath(BoneAnimationClip clip)
    {
        lock (_sync)
            return _reverseLookup.TryGetValue(clip, out var p) ? p : null;
    }

    public static void Register(BoneAnimationClip clip, string projectRelativePath)
        => Remember(clip, projectRelativePath);

    public static IEnumerable<BoneAnimationClip> AllCached()
    {
        lock (_sync)
        {
            var clips = new List<BoneAnimationClip>(_cache.Count);
            foreach (var (_, (clip, _)) in _cache)
                clips.Add(clip);
            return clips;
        }
    }

    public static void ClearCache()
    {
        lock (_sync)
        {
            _cache.Clear();
            _reverseLookup.Clear();
            _byPath.Clear();
        }
    }

    private static string? ResolveAbsolute(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        if (Path.IsPathRooted(relPath)) return relPath;
        var proj = ProjectService.Current;
        if (proj == null) return null;
        return Path.Combine(proj.RootPath, relPath);
    }
}
