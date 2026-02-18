#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    /// <summary>
    /// Manages saving / loading <see cref="AnimationClip"/> assets as .anim JSON files.
    /// Clips are cached by name so multiple Animators can share the same clip data.
    /// </summary>
    public static class AnimationClipAsset
    {
        // ── Cache: clip name → (clip, project-relative path) ──
        private static readonly Dictionary<string, (AnimationClip clip, string relPath)> _cache = new();
        private static readonly Dictionary<AnimationClip, string> _reverseLookup = new();

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ── DTO for JSON serialization ──

        private class ClipDTO
        {
            public string Name { get; set; } = "";
            public float Duration { get; set; } = 1f;
            public bool Loop { get; set; } = true;
            public List<TrackDTO> Tracks { get; set; } = new();
            public List<EventDTO> Events { get; set; } = new();
        }

        private class TrackDTO
        {
            public string Path { get; set; } = "";
            public List<KeyDTO> Keys { get; set; } = new();
        }

        private class KeyDTO
        {
            public float Time { get; set; }
            public float Value { get; set; }
            public string Interp { get; set; } = "Linear";
            public float InTangent { get; set; }
            public float OutTangent { get; set; }
        }

        private class EventDTO
        {
            public float Time { get; set; }
            public string MethodName { get; set; } = "";
            public string StringParam { get; set; } = "";
            public float FloatParam { get; set; }
            public int IntParam { get; set; }
        }

        // ── Save ──

        /// <summary>Save an AnimationClip to a .anim JSON file.</summary>
        public static void Save(AnimationClip clip, string projectRelativePath)
        {
            var absPath = ResolveAbsolute(projectRelativePath);
            if (absPath == null) return;

            var dto = new ClipDTO
            {
                Name = clip.Name,
                Duration = clip.Duration,
                Loop = clip.Loop
            };

            foreach (var (path, keys) in clip.Tracks)
            {
                var trackDto = new TrackDTO { Path = path };
                foreach (var k in keys)
                {
                    trackDto.Keys.Add(new KeyDTO
                    {
                        Time = k.Time,
                        Value = k.Value,
                        Interp = k.Interpolation.ToString(),
                        InTangent = k.InTangent,
                        OutTangent = k.OutTangent
                    });
                }
                dto.Tracks.Add(trackDto);
            }

            foreach (var evt in clip.Events)
            {
                dto.Events.Add(new EventDTO
                {
                    Time = evt.Time,
                    MethodName = evt.MethodName,
                    StringParam = evt.StringParam,
                    FloatParam = evt.FloatParam,
                    IntParam = evt.IntParam
                });
            }

            var dir = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(dto, _jsonOpts);
            File.WriteAllText(absPath, json);

            // Update cache
            _cache[clip.Name] = (clip, projectRelativePath);
            _reverseLookup[clip] = projectRelativePath;
        }

        // ── Load ──

        /// <summary>Load an AnimationClip from a .anim JSON file (project-relative path).</summary>
        public static AnimationClip? Load(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath)) return null;

            // Check cache
            foreach (var (_, entry) in _cache)
            {
                if (string.Equals(entry.relPath, projectRelativePath, StringComparison.OrdinalIgnoreCase))
                    return entry.clip;
            }

            var absPath = ResolveAbsolute(projectRelativePath);
            if (absPath == null || !File.Exists(absPath)) return null;

            try
            {
                var json = File.ReadAllText(absPath);
                var dto = JsonSerializer.Deserialize<ClipDTO>(json, _jsonOpts);
                if (dto == null) return null;

                var clip = new AnimationClip
                {
                    Name = dto.Name,
                    Duration = dto.Duration,
                    Loop = dto.Loop
                };

                foreach (var trackDto in dto.Tracks)
                {
                    var keys = new List<AnimKeyframe>();
                    foreach (var k in trackDto.Keys)
                    {
                        Enum.TryParse<InterpMode>(k.Interp, true, out var interp);
                        keys.Add(new AnimKeyframe(k.Time, k.Value, interp, k.InTangent, k.OutTangent));
                    }
                    clip.Tracks[trackDto.Path] = keys;
                }

                if (dto.Events != null)
                {
                    foreach (var evtDto in dto.Events)
                    {
                        clip.Events.Add(new AnimationEvent
                        {
                            Time = evtDto.Time,
                            MethodName = evtDto.MethodName,
                            StringParam = evtDto.StringParam,
                            FloatParam = evtDto.FloatParam,
                            IntParam = evtDto.IntParam
                        });
                    }
                }

                _cache[clip.Name] = (clip, projectRelativePath);
                _reverseLookup[clip] = projectRelativePath;
                return clip;
            }
            catch
            {
                return null;
            }
        }

        // ── Helpers ──

        /// <summary>Get the project-relative path for a cached clip, or null.</summary>
        public static string? GetPath(AnimationClip clip)
            => _reverseLookup.TryGetValue(clip, out var p) ? p : null;

        /// <summary>Register a clip in the cache (e.g. when created in the Animation Panel).</summary>
        public static void Register(AnimationClip clip, string projectRelativePath)
        {
            _cache[clip.Name] = (clip, projectRelativePath);
            _reverseLookup[clip] = projectRelativePath;
        }

        /// <summary>Get all cached clips.</summary>
        public static IEnumerable<AnimationClip> AllCached()
        {
            foreach (var (_, (clip, _)) in _cache)
                yield return clip;
        }

        /// <summary>Clear the entire cache.</summary>
        public static void ClearCache()
        {
            _cache.Clear();
            _reverseLookup.Clear();
        }

        /// <summary>Create a new empty clip and save it.</summary>
        public static AnimationClip CreateNew(string name, string projectRelativePath, float duration = 1f)
        {
            var clip = new AnimationClip { Name = name, Duration = duration };
            Save(clip, projectRelativePath);
            return clip;
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
}
