#nullable enable
using System;
using System.IO;
using Avalonia.Media;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// UI Panel element — a simple colored or textured rectangular background.
    /// Useful as a container/backdrop for other UI elements.
    /// </summary>
    [ComponentCategory("UI")]
    [Require(typeof(RectTransform))]
    public sealed class UIPanel : UIElement
    {
        /// <summary>Optional background image path (project-relative or absolute).</summary>
        [Persist] public string SpritePath { get; set; } = "";

        // ── Texture caching ──
        private Texture2D? _cachedTexture;
        private string _cachedPath = "";

        private Texture2D? GetTexture()
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

            if (_quadBuffer.Length < 1) _quadBuffer = new UIQuad[1];
            _quadBuffer[0] = new UIQuad
            {
                X0 = rect.X, Y0 = rect.Y,
                X1 = rect.X + rect.Width, Y1 = rect.Y + rect.Height,
                U0 = 0f, V0 = 0f, U1 = 1f, V1 = 1f,
                R = r, G = g, B = b, A = a,
                Texture = tex,
                TextureHandle = 0,
                IsSDF = false
            };
            return new UIDrawData { QuadCount = 1, Quads = _quadBuffer };
        }
    }
}
