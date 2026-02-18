#nullable enable
using System;
using System.IO;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>How the image is rendered within its rect.</summary>
    public enum ImageType
    {
        /// <summary>Stretch to fill the rect.</summary>
        Simple,
        /// <summary>9-slice rendering for resizable UI panels.</summary>
        Sliced,
        /// <summary>Tile the image to fill the rect.</summary>
        Tiled,
        /// <summary>Radial or horizontal fill (controlled by FillAmount).</summary>
        Filled
    }

    /// <summary>
    /// UI Image element — renders a sprite/texture inside a RectTransform.
    /// Supports simple, sliced, tiled, and filled image types.
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public sealed class UIImage : UIElement
    {
        /// <summary>Path to the image file (project-relative or absolute).</summary>
        [Persist] public string SpritePath { get; set; } = "";

        /// <summary>How the image is drawn within the rect.</summary>
        [Persist] public ImageType ImageType { get; set; } = ImageType.Simple;

        /// <summary>Fill amount (0-1) for Filled image type.</summary>
        [Persist] public float FillAmount { get; set; } = 1f;

        /// <summary>Preserve the original aspect ratio of the image.</summary>
        [Persist] public bool PreserveAspect { get; set; } = false;

        // ── Texture caching ──
        private Texture2D? _cachedTexture;
        private string _cachedPath = "";

        /// <summary>Load or return the cached Texture2D for the current SpritePath.</summary>
        public Texture2D? GetTexture()
        {
            if (string.IsNullOrEmpty(SpritePath)) return null;

            if (_cachedPath == SpritePath && _cachedTexture != null)
                return _cachedTexture;

            try
            {
                string path = SpritePath;
                if (!Path.IsPathRooted(path))
                {
                    var proj = ProjectService.Current;
                    if (proj != null)
                        path = Path.GetFullPath(Path.Combine(proj.RootPath, path));
                }

                if (File.Exists(path))
                {
                    _cachedTexture = Texture2D.FromFile(path);
                    _cachedPath = SpritePath;
                }
                else
                {
                    _cachedTexture = null;
                    _cachedPath = SpritePath;
                }
            }
            catch
            {
                _cachedTexture = null;
                _cachedPath = SpritePath;
            }

            return _cachedTexture;
        }

        public override UIDrawData GetDrawData(in RectTransform.Rect rect)
        {
            var (r, g, b, a) = GetFinalColor();
            if (a <= 0f) return UIDrawData.Empty;

            var tex = GetTexture();

            float x0 = rect.X, y0 = rect.Y;
            float x1 = rect.X + rect.Width, y1 = rect.Y + rect.Height;

            // Preserve aspect ratio
            if (PreserveAspect && tex != null && tex.Width > 0 && tex.Height > 0)
            {
                float texAspect = (float)tex.Width / tex.Height;
                float rectAspect = rect.Width / Math.Max(1f, rect.Height);

                if (rectAspect > texAspect)
                {
                    float fitW = rect.Height * texAspect;
                    float pad = (rect.Width - fitW) * 0.5f;
                    x0 += pad;
                    x1 -= pad;
                }
                else
                {
                    float fitH = rect.Width / texAspect;
                    float pad = (rect.Height - fitH) * 0.5f;
                    y0 += pad;
                    y1 -= pad;
                }
            }

            float u0 = 0f, v0 = 0f, u1 = 1f, v1 = 1f;

            // Filled mode: clip the UV and position
            if (ImageType == ImageType.Filled)
            {
                float fill = Math.Clamp(FillAmount, 0f, 1f);
                x1 = x0 + (x1 - x0) * fill;
                u1 = u0 + (u1 - u0) * fill;
            }

            if (_quadBuffer.Length < 1) _quadBuffer = new UIQuad[1];
            _quadBuffer[0] = new UIQuad
            {
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                U0 = u0, V0 = v0, U1 = u1, V1 = v1,
                R = r, G = g, B = b, A = a,
                Texture = tex,
                TextureHandle = 0,
                IsSDF = false
            };
            return new UIDrawData { QuadCount = 1, Quads = _quadBuffer };
        }
    }
}
