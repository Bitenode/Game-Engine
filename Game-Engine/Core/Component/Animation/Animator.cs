#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SN = System.Numerics;

namespace Game_Engine.Core.Component
{
    // ── Interpolation ──

    /// <summary>How keyframes are interpolated between.</summary>
    public enum InterpMode : byte
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
        CubicBezier,
        Step
    }

    // ── Keyframe ──

    /// <summary>Keyframe for animation: time + value + interpolation.</summary>
    public struct AnimKeyframe
    {
        public float Time;
        public float Value;
        public InterpMode Interpolation;
        /// <summary>Incoming tangent (slope) for CubicBezier.</summary>
        public float InTangent;
        /// <summary>Outgoing tangent (slope) for CubicBezier.</summary>
        public float OutTangent;

        public AnimKeyframe(float time, float value)
        {
            Time = time; Value = value;
            Interpolation = InterpMode.Linear;
            InTangent = 0f; OutTangent = 0f;
        }

        public AnimKeyframe(float time, float value, InterpMode interp, float inTan = 0f, float outTan = 0f)
        {
            Time = time; Value = value;
            Interpolation = interp;
            InTangent = inTan; OutTangent = outTan;
        }
    }

    // ── Animation Clip ──

    /// <summary>
    /// Animation clip asset — a collection of property tracks with keyframes.
    /// Each track targets a property path (e.g., "Transform.Position.X").
    /// </summary>
    public sealed class AnimationClip
    {
        public string Name { get; set; } = "New Clip";
        public float Duration { get; set; } = 1f;
        public bool Loop { get; set; } = true;

        /// <summary>Property path -> keyframes (sorted by time).</summary>
        public Dictionary<string, List<AnimKeyframe>> Tracks { get; set; } = new();

        /// <summary>Events that fire at specific times during playback.</summary>
        public List<AnimationEvent> Events { get; set; } = new();

        /// <summary>Add an event at the specified time.</summary>
        public void AddEvent(float time, string methodName, string stringParam = "", float floatParam = 0f, int intParam = 0)
        {
            Events.Add(new AnimationEvent
            {
                Time = time,
                MethodName = methodName,
                StringParam = stringParam,
                FloatParam = floatParam,
                IntParam = intParam
            });
            Events.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        /// <summary>Remove all events at the specified time.</summary>
        public bool RemoveEvent(float time)
        {
            int removed = Events.RemoveAll(e => MathF.Abs(e.Time - time) < 0.001f);
            return removed > 0;
        }

        // ── Keyframe management ──

        /// <summary>Add or replace a keyframe on a property track.</summary>
        public void SetKey(string propertyPath, float time, float value,
                           InterpMode interp = InterpMode.Linear,
                           float inTan = 0f, float outTan = 0f)
        {
            if (!Tracks.TryGetValue(propertyPath, out var keys))
            {
                keys = new List<AnimKeyframe>();
                Tracks[propertyPath] = keys;
            }

            var kf = new AnimKeyframe(time, value, interp, inTan, outTan);

            for (int i = 0; i < keys.Count; i++)
            {
                if (MathF.Abs(keys[i].Time - time) < 0.001f)
                {
                    keys[i] = kf;
                    return;
                }
            }

            keys.Add(kf);
            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        /// <summary>Remove a keyframe at the given time on a track.</summary>
        public bool RemoveKey(string propertyPath, float time)
        {
            if (!Tracks.TryGetValue(propertyPath, out var keys)) return false;
            int idx = keys.FindIndex(k => MathF.Abs(k.Time - time) < 0.001f);
            if (idx < 0) return false;
            keys.RemoveAt(idx);
            if (keys.Count == 0) Tracks.Remove(propertyPath);
            return true;
        }

        /// <summary>Get a read-only copy of keyframes for a track.</summary>
        public IReadOnlyList<AnimKeyframe>? GetKeyframes(string propertyPath)
            => Tracks.TryGetValue(propertyPath, out var keys) ? keys : null;

        /// <summary>Remove an entire property track.</summary>
        public bool RemoveTrack(string propertyPath) => Tracks.Remove(propertyPath);

        /// <summary>Get all property paths that have tracks.</summary>
        public IEnumerable<string> TrackPaths => Tracks.Keys;

        // ── Sampling ──

        /// <summary>Sample a track at the given time respecting interpolation modes.</summary>
        public float Sample(string propertyPath, float time)
        {
            if (!Tracks.TryGetValue(propertyPath, out var keys) || keys.Count == 0)
                return 0f;

            if (keys.Count == 1) return keys[0].Value;

            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                var k0 = keys[i];
                var k1 = keys[i + 1];
                if (time >= k0.Time && time <= k1.Time)
                {
                    float segDur = k1.Time - k0.Time;
                    if (segDur < 1e-6f) return k0.Value;
                    float t = (time - k0.Time) / segDur;

                    return k0.Interpolation switch
                    {
                        InterpMode.Step => k0.Value,
                        InterpMode.EaseIn => Lerp(k0.Value, k1.Value, t * t),
                        InterpMode.EaseOut => Lerp(k0.Value, k1.Value, 1f - (1f - t) * (1f - t)),
                        InterpMode.EaseInOut => Lerp(k0.Value, k1.Value, SmoothStep(t)),
                        InterpMode.CubicBezier => CubicHermite(k0.Value, k0.OutTangent * segDur,
                                                                k1.Value, k1.InTangent * segDur, t),
                        _ => Lerp(k0.Value, k1.Value, t), // Linear
                    };
                }
            }

            return keys[^1].Value;
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float SmoothStep(float t) => t * t * (3f - 2f * t);
        static float CubicHermite(float p0, float m0, float p1, float m1, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * p0
                 + (t3 - 2f * t2 + t) * m0
                 + (-2f * t3 + 3f * t2) * p1
                 + (t3 - t2) * m1;
        }
    }

    // ── State Machine Types ──

    /// <summary>Animation state machine transition.</summary>
    public sealed class AnimTransition
    {
        public string FromState { get; set; } = "";
        public string ToState { get; set; } = "";
        public string Condition { get; set; } = "";
        public float ConditionValue { get; set; } = 1f;
        public bool BoolCondition { get; set; } = true;
        public float TransitionDuration { get; set; } = 0.2f;
        public bool HasExitTime { get; set; } = false;
        public float ExitTime { get; set; } = 0.9f;
    }

    /// <summary>Animation state in the state machine.</summary>
    public sealed class AnimState
    {
        public string Name { get; set; } = "";
        public AnimationClip? Clip { get; set; }
        /// <summary>Optional bone animation clip (for skeletal animation).</summary>
        public BoneAnimationClip? BoneClip { get; set; }
        public float Speed { get; set; } = 1f;
        /// <summary>Editor position for state machine graph.</summary>
        public SN.Vector2 EditorPosition { get; set; }
    }

    /// <summary>Animation event that fires a callback at a specific time in a clip.</summary>
    public sealed class AnimationEvent
    {
        /// <summary>Time in seconds when this event fires.</summary>
        public float Time { get; set; }
        /// <summary>Name of the method to invoke on sibling Behaviors.</summary>
        public string MethodName { get; set; } = "";
        /// <summary>Optional string parameter.</summary>
        public string StringParam { get; set; } = "";
        /// <summary>Optional float parameter.</summary>
        public float FloatParam { get; set; }
        /// <summary>Optional int parameter.</summary>
        public int IntParam { get; set; }
    }

    // ── Animator Component ──

    /// <summary>
    /// Animator component — drives animation playback using a state machine.
    /// Supports animation clips with keyframed properties, state transitions,
    /// blending, and parameter-based control.
    /// </summary>
    [ComponentCategory("Animation")]
    public sealed class Animator : Behavior
    {
        // ── Configuration ──
        [Persist] public bool PlayOnAwake { get; set; } = true;
        [Persist] public float Speed { get; set; } = 1f;

        // ── Persisted state machine data ──
        // These are exposed so the Animation Panel and serialization can access them.
        [Persist] public List<AnimStateDTO> StateList { get; set; } = new();
        [Persist] public List<AnimTransitionDTO> TransitionList { get; set; } = new();
        [Persist] public string DefaultStateName { get; set; } = "";

        // Runtime state machine (built from persisted data or API)
        private readonly Dictionary<string, AnimState> _states = new();
        private readonly List<AnimTransition> _transitions = new();
        private readonly Dictionary<string, float> _floatParams = new();
        private readonly Dictionary<string, bool> _boolParams = new();
        private readonly Dictionary<string, int> _intParams = new();

        private AnimState? _currentState;
        private AnimState? _nextState;
        private float _stateTime;
        private float _prevStateTime;
        private float _blendFactor;
        private float _blendDuration;
        private bool _isBlending;

        /// <summary>Current animation state name.</summary>
        public string? CurrentStateName => _currentState?.Name;
        public float StateTime { get => _stateTime; set => _stateTime = value; }
        public float NormalizedTime => _currentState?.Clip != null && _currentState.Clip.Duration > 0
            ? _stateTime / _currentState.Clip.Duration : 0f;

        /// <summary>Read-only access to the runtime states.</summary>
        public IReadOnlyDictionary<string, AnimState> States { get { EnsureBuilt(); return _states; } }
        /// <summary>Read-only access to the runtime transitions.</summary>
        public IReadOnlyList<AnimTransition> Transitions { get { EnsureBuilt(); return _transitions; } }
        /// <summary>The currently active clip (from the current state).</summary>
        public AnimationClip? CurrentClip
        {
            get
            {
                EnsureBuilt();
                return _currentState?.Clip;
            }
        }

        /// <summary>Current bone poses (sampled each frame for skeletal animation). Null if no bone clip.</summary>
        public BonePose[]? CurrentBonePose { get; set; }

        /// <summary>Rebuild runtime state machine from DTOs if not yet built (needed in editor).</summary>
        public void EnsureBuilt()
        {
            if (_states.Count == 0 && StateList.Count > 0)
                RebuildFromDTO();
        }

        // ── State Machine API ──

        /// <summary>Add a state with an animation clip.</summary>
        public void AddState(string name, AnimationClip clip, float speed = 1f)
        {
            _states[name] = new AnimState { Name = name, Clip = clip, Speed = speed };
            if (_currentState == null) _currentState = _states[name];
        }

        /// <summary>Add a state with a bone animation clip (skeletal animation).</summary>
        public void AddState(string name, BoneAnimationClip boneClip, float speed = 1f)
        {
            _states[name] = new AnimState { Name = name, BoneClip = boneClip, Speed = speed };
            if (_currentState == null) _currentState = _states[name];
        }

        /// <summary>Add a state with both property and bone animation clips.</summary>
        public void AddState(string name, AnimationClip? clip, BoneAnimationClip? boneClip, float speed = 1f)
        {
            _states[name] = new AnimState { Name = name, Clip = clip, BoneClip = boneClip, Speed = speed };
            if (_currentState == null) _currentState = _states[name];
        }

        /// <summary>Remove a state by name.</summary>
        public bool RemoveState(string name)
        {
            if (!_states.Remove(name)) return false;
            _transitions.RemoveAll(t => t.FromState == name || t.ToState == name);
            if (_currentState?.Name == name) _currentState = _states.Values.FirstOrDefault();
            return true;
        }

        /// <summary>Add a transition between states.</summary>
        public void AddTransition(AnimTransition transition) => _transitions.Add(transition);

        /// <summary>Remove a specific transition.</summary>
        public bool RemoveTransition(AnimTransition transition) => _transitions.Remove(transition);

        // ── Parameter API ──
        public void SetFloat(string name, float value) => _floatParams[name] = value;
        public float GetFloat(string name) => _floatParams.TryGetValue(name, out var v) ? v : 0f;
        public void SetBool(string name, bool value) => _boolParams[name] = value;
        public bool GetBool(string name) => _boolParams.TryGetValue(name, out var v) && v;
        public void SetInt(string name, int value) => _intParams[name] = value;
        public int GetInt(string name) => _intParams.TryGetValue(name, out var v) ? v : 0;
        public void SetTrigger(string name) => _boolParams[name] = true;
        public void ResetTrigger(string name) => _boolParams[name] = false;

        public IReadOnlyDictionary<string, float> FloatParams => _floatParams;
        public IReadOnlyDictionary<string, bool> BoolParams => _boolParams;
        public IReadOnlyDictionary<string, int> IntParams => _intParams;

        /// <summary>Force-play a specific state immediately.</summary>
        public void Play(string stateName, float transitionDuration = 0f)
        {
            if (!_states.TryGetValue(stateName, out var state)) return;

            if (transitionDuration > 0f && _currentState != null)
            {
                _nextState = state;
                _blendDuration = transitionDuration;
                _blendFactor = 0f;
                _isBlending = true;
            }
            else
            {
                _currentState = state;
                _stateTime = 0f;
                _isBlending = false;
            }
        }

        /// <summary>Sample the current clip at a specific time without advancing playback.
        /// Used by the Animation Panel for preview scrubbing.</summary>
        public void SampleAt(float time)
        {
            if (_currentState?.Clip == null) return;
            ApplyTracks(_currentState.Clip, time);
        }

        /// <summary>Public wrapper for ApplyPropertyValue so the Animation Panel can
        /// drive properties during preview playback.</summary>
        public void ApplyPropertyValue_External(string path, float value) => ApplyPropertyValue(path, value);

        public override void Start()
        {
            // Rebuild runtime state machine from persisted data if available
            if (StateList.Count > 0 && _states.Count == 0)
                RebuildFromDTO();

            if (PlayOnAwake && _currentState != null)
                _stateTime = 0f;
        }

        public override void Update()
        {
            bool hasPropertyClip = _currentState?.Clip != null;
            bool hasBoneClip = _currentState?.BoneClip != null;
            if (!hasPropertyClip && !hasBoneClip) return;

            float dt = Time.deltaTime * Speed * (_currentState!.Speed);
            _stateTime += dt;

            // Determine duration from whichever clip is active
            float duration = 0f;
            bool loop = false;
            if (hasPropertyClip) { duration = _currentState.Clip!.Duration; loop = _currentState.Clip.Loop; }
            else if (hasBoneClip) { duration = _currentState.BoneClip!.Duration; loop = _currentState.BoneClip.Loop; }

            if (loop && duration > 0 && _stateTime >= duration)
                _stateTime -= duration;

            // Blending
            if (_isBlending && _nextState != null)
            {
                _blendFactor += Time.deltaTime / _blendDuration;
                if (_blendFactor >= 1f)
                {
                    _currentState = _nextState;
                    _stateTime = 0f;
                    _nextState = null;
                    _isBlending = false;
                    _blendFactor = 0f;
                }
            }

            if (!_isBlending) EvaluateTransitions();

            // Apply property animation tracks
            if (hasPropertyClip)
                ApplyTracks(_currentState.Clip!, _stateTime);

            // Fire animation events that were crossed this frame
            if (hasPropertyClip)
                FireEvents(_currentState!.Clip!, _prevStateTime, _stateTime);
            else if (hasBoneClip)
                FireBoneClipEvents(_prevStateTime, _stateTime);

            _prevStateTime = _stateTime;

            // Sample bone animation
            if (hasBoneClip)
                SampleBoneAnimation();
            else
                CurrentBonePose = null;
        }

        /// <summary>Sample bone animation from the current (and optionally next) state.</summary>
        private void SampleBoneAnimation()
        {
            var boneClip = _currentState?.BoneClip;
            if (boneClip == null) return;

            // Get bone count from the clip tracks (max bone index + 1)
            int boneCount = 0;
            foreach (var track in boneClip.Tracks)
                if (track.BoneIndex >= boneCount) boneCount = track.BoneIndex + 1;
            if (boneCount == 0) return;

            // Sample current state
            var poses = boneClip.SampleAllBones(boneCount, _stateTime);

            // Blend with next state if transitioning
            if (_isBlending && _nextState?.BoneClip != null)
            {
                var nextPoses = _nextState.BoneClip.SampleAllBones(boneCount, _stateTime * _nextState.Speed);
                for (int i = 0; i < boneCount && i < nextPoses.Length; i++)
                    poses[i] = BonePose.Lerp(poses[i], nextPoses[i], _blendFactor);
            }

            CurrentBonePose = poses;
        }

        /// <summary>Fire animation events that were crossed between prevTime and currentTime.</summary>
        private void FireEvents(AnimationClip clip, float prevTime, float currentTime)
        {
            if (clip.Events.Count == 0 || gameObject == null) return;

            foreach (var evt in clip.Events)
            {
                bool crossed = (prevTime < evt.Time && currentTime >= evt.Time)
                    || (currentTime < prevTime && (evt.Time >= prevTime || evt.Time <= currentTime));

                if (crossed)
                    InvokeEvent(evt);
            }
        }

        /// <summary>Fire events for bone animation clips (they share the same clip events).</summary>
        private void FireBoneClipEvents(float prevTime, float currentTime)
        {
            var clip = _currentState?.Clip;
            if (clip == null || clip.Events.Count == 0 || gameObject == null) return;
            FireEvents(clip, prevTime, currentTime);
        }

        /// <summary>Invoke an animation event on all sibling Behaviors via reflection.</summary>
        private void InvokeEvent(AnimationEvent evt)
        {
            if (string.IsNullOrEmpty(evt.MethodName) || gameObject == null) return;

            foreach (var behavior in gameObject.Behaviors)
            {
                if (behavior == this || !behavior.Enabled) continue;

                var type = behavior.GetType();
                var method = type.GetMethod(evt.MethodName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (method == null) continue;

                try
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                        method.Invoke(behavior, null);
                    else if (parameters.Length == 1)
                    {
                        var paramType = parameters[0].ParameterType;
                        if (paramType == typeof(string))
                            method.Invoke(behavior, new object[] { evt.StringParam });
                        else if (paramType == typeof(float))
                            method.Invoke(behavior, new object[] { evt.FloatParam });
                        else if (paramType == typeof(int))
                            method.Invoke(behavior, new object[] { evt.IntParam });
                        else if (paramType == typeof(AnimationEvent))
                            method.Invoke(behavior, new object[] { evt });
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"AnimEvent '{evt.MethodName}' on {type.Name}: {ex.Message}");
                }
            }
        }

        private void EvaluateTransitions()
        {
            if (_currentState == null) return;

            foreach (var t in _transitions)
            {
                if (t.FromState != _currentState.Name) continue;

                bool shouldTransition = false;

                if (t.HasExitTime && _currentState.Clip != null && _currentState.Clip.Duration > 0)
                {
                    float norm = _stateTime / _currentState.Clip.Duration;
                    if (norm >= t.ExitTime) shouldTransition = true;
                }

                if (!string.IsNullOrEmpty(t.Condition))
                {
                    if (_boolParams.TryGetValue(t.Condition, out bool bv))
                        shouldTransition = bv == t.BoolCondition;
                    else if (_floatParams.TryGetValue(t.Condition, out float fv))
                        shouldTransition = fv >= t.ConditionValue;
                    else if (_intParams.TryGetValue(t.Condition, out int iv))
                        shouldTransition = iv >= (int)t.ConditionValue;
                }

                if (shouldTransition && _states.TryGetValue(t.ToState, out var nextState))
                {
                    if (_boolParams.ContainsKey(t.Condition))
                        _boolParams[t.Condition] = false;

                    if (t.TransitionDuration > 0f)
                    {
                        _nextState = nextState;
                        _blendDuration = t.TransitionDuration;
                        _blendFactor = 0f;
                        _isBlending = true;
                    }
                    else
                    {
                        _currentState = nextState;
                        _stateTime = 0f;
                    }
                    break;
                }
            }
        }

        private void ApplyTracks(AnimationClip clip, float time)
        {
            foreach (var (path, _) in clip.Tracks)
            {
                float value = clip.Sample(path, time);

                if (_isBlending && _nextState?.Clip != null)
                {
                    float nextValue = _nextState.Clip.Sample(path, _stateTime * _nextState.Speed);
                    value = value * (1f - _blendFactor) + nextValue * _blendFactor;
                }

                ApplyPropertyValue(path, value);
            }
        }

        /// <summary>Apply a sampled value to the target property path.</summary>
        private void ApplyPropertyValue(string path, float value)
        {
            var parts = path.Split('.');
            if (parts.Length < 2) return;

            // ── Transform properties ──
            if (parts[0] == "Transform" && parts.Length == 3)
            {
                var t = Transform;
                switch (parts[1])
                {
                    case "Position":
                        var p = t.Position;
                        t.Position = parts[2] switch
                        {
                            "X" => new Vector3(value, p.Y, p.Z),
                            "Y" => new Vector3(p.X, value, p.Z),
                            "Z" => new Vector3(p.X, p.Y, value),
                            _ => p
                        };
                        break;
                    case "Rotation":
                        var r = t.Rotation;
                        t.Rotation = parts[2] switch
                        {
                            "X" => new Vector3(value, r.Y, r.Z),
                            "Y" => new Vector3(r.X, value, r.Z),
                            "Z" => new Vector3(r.X, r.Y, value),
                            _ => r
                        };
                        break;
                    case "Scale":
                        var s = t.Scale;
                        t.Scale = parts[2] switch
                        {
                            "X" => new Vector3(value, s.Y, s.Z),
                            "Y" => new Vector3(s.X, value, s.Z),
                            "Z" => new Vector3(s.X, s.Y, value),
                            _ => s
                        };
                        break;
                }
                return;
            }

            // ── MeshRenderer.Color ──
            if (parts[0] == "MeshRenderer" && parts.Length >= 2)
            {
                var mr = GetComponent<MeshRenderer>();
                if (mr == null) return;

                if (parts.Length == 3 && parts[1] == "Color")
                {
                    var c = mr.Color;
                    byte bv = (byte)Math.Clamp((int)(value * 255f), 0, 255);
                    mr.Color = parts[2] switch
                    {
                        "R" => Avalonia.Media.Color.FromArgb(c.A, bv, c.G, c.B),
                        "G" => Avalonia.Media.Color.FromArgb(c.A, c.R, bv, c.B),
                        "B" => Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, bv),
                        "A" => Avalonia.Media.Color.FromArgb(bv, c.R, c.G, c.B),
                        _ => c
                    };
                }
                return;
            }

            // ── Light properties ──
            if (parts[0] == "Light" && parts.Length == 2)
            {
                var light = GetComponent<Light>();
                if (light == null) return;
                switch (parts[1])
                {
                    case "Intensity": light.Intensity = value; break;
                    case "Range": light.Range = value; break;
                }
                return;
            }

            // ── Camera properties ──
            if (parts[0] == "Camera" && parts.Length == 2)
            {
                var cam = GetComponent<Camera>();
                if (cam == null) return;
                switch (parts[1])
                {
                    case "FieldOfView": cam.FieldOfView = value; break;
                }
                return;
            }

            // ── Generic reflection fallback: "ComponentType.PropertyName" ──
            if (parts.Length == 2 && gameObject != null)
            {
                foreach (var b in gameObject.Behaviors)
                {
                    if (b.GetType().Name == parts[0])
                    {
                        var prop = b.GetType().GetProperty(parts[1],
                            BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null && prop.CanWrite)
                        {
                            try
                            {
                                if (prop.PropertyType == typeof(float))
                                    prop.SetValue(b, value);
                                else if (prop.PropertyType == typeof(double))
                                    prop.SetValue(b, (double)value);
                                else if (prop.PropertyType == typeof(int))
                                    prop.SetValue(b, (int)value);
                            }
                            catch { /* ignore reflection errors */ }
                        }
                        return;
                    }
                }
            }
        }

        /// <summary>Read a property value from the target. Used by record mode.</summary>
        public float ReadPropertyValue(string path)
        {
            var parts = path.Split('.');
            if (parts.Length < 2) return 0f;

            if (parts[0] == "Transform" && parts.Length == 3)
            {
                var t = Transform;
                return parts[1] switch
                {
                    "Position" => parts[2] switch { "X" => (float)t.Position.X, "Y" => (float)t.Position.Y, "Z" => (float)t.Position.Z, _ => 0f },
                    "Rotation" => parts[2] switch { "X" => (float)t.Rotation.X, "Y" => (float)t.Rotation.Y, "Z" => (float)t.Rotation.Z, _ => 0f },
                    "Scale" => parts[2] switch { "X" => (float)t.Scale.X, "Y" => (float)t.Scale.Y, "Z" => (float)t.Scale.Z, _ => 0f },
                    _ => 0f
                };
            }

            if (parts[0] == "MeshRenderer" && parts.Length == 3 && parts[1] == "Color")
            {
                var mr = GetComponent<MeshRenderer>();
                if (mr == null) return 0f;
                var c = mr.Color;
                return parts[2] switch
                {
                    "R" => c.R / 255f, "G" => c.G / 255f, "B" => c.B / 255f, "A" => c.A / 255f, _ => 0f
                };
            }

            if (parts[0] == "Light" && parts.Length == 2)
            {
                var light = GetComponent<Light>();
                if (light == null) return 0f;
                return parts[1] switch { "Intensity" => light.Intensity, "Range" => light.Range, _ => 0f };
            }

            if (parts[0] == "Camera" && parts.Length == 2)
            {
                var cam = GetComponent<Camera>();
                if (cam == null) return 0f;
                return parts[1] switch { "FieldOfView" => cam.FieldOfView, _ => 0f };
            }

            // Generic reflection
            if (parts.Length == 2 && gameObject != null)
            {
                foreach (var b in gameObject.Behaviors)
                {
                    if (b.GetType().Name == parts[0])
                    {
                        var prop = b.GetType().GetProperty(parts[1], BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null && prop.CanRead)
                        {
                            try
                            {
                                var val = prop.GetValue(b);
                                if (val is float f) return f;
                                if (val is double d) return (float)d;
                                if (val is int iv) return iv;
                            }
                            catch { }
                        }
                        return 0f;
                    }
                }
            }

            return 0f;
        }

        /// <summary>Get a list of all animatable property paths on this GameObject.</summary>
        public static List<string> GetAnimatableProperties(GameObject? go)
        {
            var paths = new List<string>
            {
                "Transform.Position.X", "Transform.Position.Y", "Transform.Position.Z",
                "Transform.Rotation.X", "Transform.Rotation.Y", "Transform.Rotation.Z",
                "Transform.Scale.X", "Transform.Scale.Y", "Transform.Scale.Z"
            };

            if (go == null) return paths;

            foreach (var b in go.Behaviors)
            {
                if (b is MeshRenderer)
                {
                    paths.Add("MeshRenderer.Color.R");
                    paths.Add("MeshRenderer.Color.G");
                    paths.Add("MeshRenderer.Color.B");
                    paths.Add("MeshRenderer.Color.A");
                }
                else if (b is Light)
                {
                    paths.Add("Light.Intensity");
                    paths.Add("Light.Range");
                }
                else if (b is Camera)
                {
                    paths.Add("Camera.FieldOfView");
                }
                else if (b is not Animator)
                {
                    // Add float/double/int [Persist] properties
                    var type = b.GetType();
                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!prop.CanRead || !prop.CanWrite) continue;
                        if (prop.GetCustomAttribute<PersistAttribute>() == null) continue;
                        if (prop.PropertyType == typeof(float) || prop.PropertyType == typeof(double) || prop.PropertyType == typeof(int))
                            paths.Add($"{type.Name}.{prop.Name}");
                    }
                }
            }

            return paths;
        }

        // ── Serialization DTO helpers ──

        /// <summary>Persist-friendly DTO for an animation state.</summary>
        public class AnimStateDTO
        {
            public string Name { get; set; } = "";
            public string ClipPath { get; set; } = "";
            /// <summary>Path to bone animation clip (.boneanim file). Empty if none.</summary>
            public string BoneClipPath { get; set; } = "";
            public float Speed { get; set; } = 1f;
            public float EditorX { get; set; }
            public float EditorY { get; set; }
        }

        /// <summary>Persist-friendly DTO for a transition.</summary>
        public class AnimTransitionDTO
        {
            public string FromState { get; set; } = "";
            public string ToState { get; set; } = "";
            public string Condition { get; set; } = "";
            public float ConditionValue { get; set; } = 1f;
            public bool BoolCondition { get; set; } = true;
            public float TransitionDuration { get; set; } = 0.2f;
            public bool HasExitTime { get; set; }
            public float ExitTime { get; set; } = 0.9f;
        }

        /// <summary>Rebuild runtime state machine from persisted DTO lists.</summary>
        private void RebuildFromDTO()
        {
            _states.Clear();
            _transitions.Clear();

            foreach (var dto in StateList)
            {
                var clip = AnimationClipAsset.Load(dto.ClipPath);
                BoneAnimationClip? boneClip = null;
                if (!string.IsNullOrWhiteSpace(dto.BoneClipPath))
                    boneClip = BoneAnimationClipAsset.Load(dto.BoneClipPath);

                var state = new AnimState
                {
                    Name = dto.Name,
                    Clip = clip,
                    BoneClip = boneClip,
                    Speed = dto.Speed,
                    EditorPosition = new SN.Vector2(dto.EditorX, dto.EditorY)
                };
                _states[dto.Name] = state;
            }

            foreach (var dto in TransitionList)
            {
                _transitions.Add(new AnimTransition
                {
                    FromState = dto.FromState,
                    ToState = dto.ToState,
                    Condition = dto.Condition,
                    ConditionValue = dto.ConditionValue,
                    BoolCondition = dto.BoolCondition,
                    TransitionDuration = dto.TransitionDuration,
                    HasExitTime = dto.HasExitTime,
                    ExitTime = dto.ExitTime
                });
            }

            if (!string.IsNullOrEmpty(DefaultStateName) && _states.TryGetValue(DefaultStateName, out var def))
                _currentState = def;
            else
                _currentState = _states.Values.FirstOrDefault();
        }

        /// <summary>Sync runtime states/transitions back to DTO lists for persistence.</summary>
        public void SyncToDTO()
        {
            StateList.Clear();
            foreach (var (name, state) in _states)
            {
                StateList.Add(new AnimStateDTO
                {
                    Name = name,
                    ClipPath = state.Clip != null ? AnimationClipAsset.GetPath(state.Clip) ?? "" : "",
                    BoneClipPath = state.BoneClip != null ? BoneAnimationClipAsset.GetPath(state.BoneClip) ?? "" : "",
                    Speed = state.Speed,
                    EditorX = state.EditorPosition.X,
                    EditorY = state.EditorPosition.Y
                });
            }

            TransitionList.Clear();
            foreach (var t in _transitions)
            {
                TransitionList.Add(new AnimTransitionDTO
                {
                    FromState = t.FromState,
                    ToState = t.ToState,
                    Condition = t.Condition,
                    ConditionValue = t.ConditionValue,
                    BoolCondition = t.BoolCondition,
                    TransitionDuration = t.TransitionDuration,
                    HasExitTime = t.HasExitTime,
                    ExitTime = t.ExitTime
                });
            }

            DefaultStateName = _currentState?.Name ?? "";
        }
    }
}
