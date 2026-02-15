#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Game_Engine.Core.Component
{
    /// <summary>Sprite flip modes.</summary>
    [Flags]
    public enum SpriteFlip
    {
        None = 0,
        FlipX = 1,
        FlipY = 2,
        FlipBoth = FlipX | FlipY
    }

    /// <summary>
    /// Sprite renderer component — renders a 2D sprite (texture region) on a quad.
    /// Supports sprite atlases, tinting, flipping, sorting layers, and sprite animation.
    /// Uses the existing rendering pipeline with an orthographic camera for 2D mode.
    /// </summary>
    public sealed class SpriteRenderer : Behavior
    {
        // ── Sprite ──
        /// <summary>Path to the sprite texture file.</summary>
        [Persist] public string SpritePath { get; set; } = "";

        /// <summary>Tint color applied to the sprite.</summary>
        [Persist] public Color Color { get; set; } = Colors.White;

        /// <summary>Flip the sprite horizontally/vertically.</summary>
        [Persist] public SpriteFlip Flip { get; set; } = SpriteFlip.None;

        // ── Atlas / spritesheet ──
        /// <summary>UV region within the texture (for sprite atlases). 0-1 range.</summary>
        [Persist] public float UVX { get; set; } = 0f;
        [Persist] public float UVY { get; set; } = 0f;
        [Persist] public float UVWidth { get; set; } = 1f;
        [Persist] public float UVHeight { get; set; } = 1f;

        /// <summary>Pixels per unit for sizing. Higher = smaller sprite.</summary>
        [Persist] public float PixelsPerUnit { get; set; } = 100f;

        // ── Sorting ──
        /// <summary>Sorting layer name for draw order control.</summary>
        [Persist] public string SortingLayer { get; set; } = "Default";

        /// <summary>Order within the sorting layer (higher = rendered later / on top).</summary>
        [Persist] public int SortingOrder { get; set; } = 0;

        // ── Rendering ──
        /// <summary>Opacity (0 = transparent, 1 = fully opaque).</summary>
        [Persist] public float Opacity { get; set; } = 1f;

        /// <summary>Render as a billboard (always faces the camera).</summary>
        [Persist] public bool Billboard { get; set; } = false;

        // ── Runtime ──
        private static readonly List<SpriteRenderer> _all = new(256);
        public static IReadOnlyList<SpriteRenderer> All => _all;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_all.Contains(this)) _all.Add(this);
        }

        public override void OnDisable()
        {
            _all.Remove(this);
            base.OnDisable();
        }

        /// <summary>Set the sprite UV region from a spritesheet grid.</summary>
        /// <param name="col">Column index (0-based).</param>
        /// <param name="row">Row index (0-based).</param>
        /// <param name="cols">Total columns in the sheet.</param>
        /// <param name="rows">Total rows in the sheet.</param>
        public void SetSpriteFromGrid(int col, int row, int cols, int rows)
        {
            UVWidth = 1f / cols;
            UVHeight = 1f / rows;
            UVX = col * UVWidth;
            UVY = row * UVHeight;
        }

        /// <summary>Get the world-space size of the sprite based on PixelsPerUnit and UV region.</summary>
        public (float width, float height) GetWorldSize()
        {
            // Default to 1x1 if no texture info available
            float w = UVWidth / PixelsPerUnit * 100f;
            float h = UVHeight / PixelsPerUnit * 100f;
            return (w * (float)Transform.Scale.X, h * (float)Transform.Scale.Y);
        }
    }

    /// <summary>
    /// Sprite animation component — plays frame-by-frame animation from a spritesheet.
    /// Drives a SpriteRenderer's UV region over time.
    /// </summary>
    [Require(typeof(SpriteRenderer))]
    public sealed class SpriteAnimator : Behavior
    {
        /// <summary>A single sprite animation clip.</summary>
        public sealed class SpriteClip
        {
            public string Name { get; set; } = "";
            public int StartFrame { get; set; }
            public int FrameCount { get; set; }
            public float FrameRate { get; set; } = 12f;
            public bool Loop { get; set; } = true;
        }

        // ── Configuration ──
        /// <summary>Number of columns in the spritesheet grid.</summary>
        [Persist] public int Columns { get; set; } = 1;

        /// <summary>Number of rows in the spritesheet grid.</summary>
        [Persist] public int Rows { get; set; } = 1;

        /// <summary>Frames per second for the default animation.</summary>
        [Persist] public float FrameRate { get; set; } = 12f;

        /// <summary>Total number of frames in the animation.</summary>
        [Persist] public int TotalFrames { get; set; } = 1;

        /// <summary>Should the animation loop?</summary>
        [Persist] public bool Loop { get; set; } = true;

        /// <summary>Play animation on start?</summary>
        [Persist] public bool PlayOnAwake { get; set; } = true;

        // ── Named clips ──
        private readonly Dictionary<string, SpriteClip> _clips = new();

        // ── Runtime ──
        private float _timer;
        private int _currentFrame;
        private bool _playing;
        private SpriteClip? _currentClip;

        /// <summary>Current frame index.</summary>
        public int CurrentFrame => _currentFrame;

        /// <summary>Is the animation currently playing?</summary>
        public bool IsPlaying => _playing;

        /// <summary>Add a named animation clip.</summary>
        public void AddClip(string name, int startFrame, int frameCount, float fps = 12f, bool loop = true)
        {
            _clips[name] = new SpriteClip
            {
                Name = name,
                StartFrame = startFrame,
                FrameCount = frameCount,
                FrameRate = fps,
                Loop = loop
            };
        }

        /// <summary>Play a named clip.</summary>
        public void Play(string clipName)
        {
            if (_clips.TryGetValue(clipName, out var clip))
            {
                _currentClip = clip;
                _currentFrame = clip.StartFrame;
                _timer = 0f;
                _playing = true;
            }
        }

        /// <summary>Play the default animation (all frames).</summary>
        public void Play()
        {
            _currentClip = null;
            _currentFrame = 0;
            _timer = 0f;
            _playing = true;
        }

        /// <summary>Stop the animation.</summary>
        public void Stop()
        {
            _playing = false;
        }

        /// <summary>Pause the animation.</summary>
        public void Pause() => _playing = false;

        /// <summary>Resume the animation.</summary>
        public void Resume() => _playing = true;

        public override void Start()
        {
            if (PlayOnAwake)
                Play();
        }

        public override void Update()
        {
            if (!_playing) return;

            float fps = _currentClip?.FrameRate ?? FrameRate;
            bool loop = _currentClip?.Loop ?? Loop;
            int startFrame = _currentClip?.StartFrame ?? 0;
            int frameCount = _currentClip?.FrameCount ?? TotalFrames;

            if (fps <= 0 || frameCount <= 0) return;

            _timer += Time.deltaTime;
            float frameDuration = 1f / fps;

            if (_timer >= frameDuration)
            {
                _timer -= frameDuration;
                int localFrame = _currentFrame - startFrame;
                localFrame++;

                if (localFrame >= frameCount)
                {
                    if (loop)
                        localFrame = 0;
                    else
                    {
                        localFrame = frameCount - 1;
                        _playing = false;
                    }
                }

                _currentFrame = startFrame + localFrame;
            }

            // Update the SpriteRenderer UV region
            var sr = GetComponent<SpriteRenderer>();
            if (sr != null && Columns > 0 && Rows > 0)
            {
                int col = _currentFrame % Columns;
                int row = _currentFrame / Columns;
                sr.SetSpriteFromGrid(col, row, Columns, Rows);
            }
        }
    }
}
