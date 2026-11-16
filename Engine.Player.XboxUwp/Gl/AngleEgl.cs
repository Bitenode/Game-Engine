using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml.Controls;

namespace Engine.Player.XboxUwp.Gl
{
    /// <summary>
    /// ANGLE (D3D11) ES 3 context bound to a UWP SwapChainPanel.
    /// libEGL.dll and libGLESv2.dll must sit next to the package executable.
    /// </summary>
    public sealed class AngleEgl : IDisposable
    {
        public const int EGL_NONE = 0x3038;
        public const int EGL_TRUE = 1;
        public const int EGL_FALSE = 0;
        public const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202;
        public const int EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203;
        public const int EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE = 0x3208;
        public const int EGL_PLATFORM_ANGLE_ENABLE_AUTOMATIC_TRIM_ANGLE = 0x320F;
        public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
        public const int EGL_RENDERABLE_TYPE = 0x3040;
        public const int EGL_OPENGL_ES3_BIT = 0x0040;
        public const int EGL_SURFACE_TYPE = 0x3033;
        public const int EGL_WINDOW_BIT = 0x0004;
        public const int EGL_RED_SIZE = 0x3024;
        public const int EGL_GREEN_SIZE = 0x3023;
        public const int EGL_BLUE_SIZE = 0x3022;
        public const int EGL_ALPHA_SIZE = 0x3021;
        public const int EGL_DEPTH_SIZE = 0x3025;
        public const int EGL_STENCIL_SIZE = 0x3026;
        public const int EGL_WIDTH = 0x3057;
        public const int EGL_HEIGHT = 0x3056;
        public const int EGL_GL_COLORSPACE_KHR = 0x309D;
        public const int EGL_GL_COLORSPACE_LINEAR_KHR = 0x308A;

        public IntPtr Display { get; private set; }
        public IntPtr Surface { get; private set; }
        public IntPtr Context { get; private set; }

        PropertySet _windowProps;
        float _lastPixelW = 1280, _lastPixelH = 720;

        /// <param name="initialDipWidth">Panel size from the UI thread (do not read XAML off the GL thread).</param>
        public static AngleEgl Create(
            SwapChainPanel panel,
            double initialDipWidth = 1280,
            double initialDipHeight = 720,
            float compositionScale = 1f)
        {
            if (panel == null) throw new ArgumentNullException(nameof(panel));

            var session = new AngleEgl();
            int[] displayAttribs =
            {
                EGL_PLATFORM_ANGLE_TYPE_ANGLE, EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
                EGL_PLATFORM_ANGLE_ENABLE_AUTOMATIC_TRIM_ANGLE, EGL_TRUE,
                EGL_NONE
            };

            session.Display = eglGetPlatformDisplayEXT(EGL_PLATFORM_ANGLE_ANGLE, IntPtr.Zero, displayAttribs);
            if (session.Display == IntPtr.Zero)
                throw new InvalidOperationException("eglGetPlatformDisplayEXT failed: 0x" + eglGetError().ToString("X"));

            if (eglInitialize(session.Display, out _, out _) == 0)
                throw new InvalidOperationException("eglInitialize failed: 0x" + eglGetError().ToString("X"));

            // Prefer opaque (alpha 0). Fall back to alpha 8 — some ANGLE builds reject alpha 0.
            IntPtr config = IntPtr.Zero;
            if (!TryChooseConfig(session.Display, alphaSize: 0, out config)
                && !TryChooseConfig(session.Display, alphaSize: 8, out config))
            {
                throw new InvalidOperationException("eglChooseConfig (ES3) failed: 0x" + eglGetError().ToString("X"));
            }

            int[] contextAttribs = { EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE };
            session.Context = eglCreateContext(session.Display, config, IntPtr.Zero, contextAttribs);
            if (session.Context == IntPtr.Zero)
                throw new InvalidOperationException("eglCreateContext ES3 failed: 0x" + eglGetError().ToString("X"));

            float scale = compositionScale > 0.01f ? compositionScale : 1f;
            float dipW = (float)Math.Max(64.0, initialDipWidth);
            float dipH = (float)Math.Max(64.0, initialDipHeight);
            float pixelW = Math.Max(64f, dipW * scale);
            float pixelH = Math.Max(64f, dipH * scale);
            session._lastPixelW = pixelW;
            session._lastPixelH = pixelH;

            // Only the panel. EGLRenderSurfaceSizeProperty and a linear colorspace
            // attrib both fast-fail (0xc0000409) inside eglCreateWindowSurface on this
            // ANGLE build and close the player before any fallback runs.
            var props = new PropertySet
            {
                { "EGLNativeWindowTypeProperty", panel }
            };
            session._windowProps = props;

            session.Surface = CreateSurface(session.Display, config, props);

            if (session.Surface == IntPtr.Zero)
                throw new InvalidOperationException("eglCreateWindowSurface failed: 0x" + eglGetError().ToString("X"));

            if (eglMakeCurrent(session.Display, session.Surface, session.Surface, session.Context) == 0)
                throw new InvalidOperationException("eglMakeCurrent failed: 0x" + eglGetError().ToString("X"));

            return session;
        }

        static bool TryChooseConfig(IntPtr display, int alphaSize, out IntPtr config)
        {
            config = IntPtr.Zero;
            int[] configAttribs =
            {
                EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
                EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
                EGL_RED_SIZE, 8,
                EGL_GREEN_SIZE, 8,
                EGL_BLUE_SIZE, 8,
                EGL_ALPHA_SIZE, alphaSize,
                EGL_DEPTH_SIZE, 24,
                EGL_STENCIL_SIZE, 8,
                EGL_NONE
            };
            if (eglChooseConfig(display, configAttribs, out config, 1, out var count) == 0 || count < 1)
            {
                config = IntPtr.Zero;
                return false;
            }
            return true;
        }

        static IntPtr CreateSurface(IntPtr display, IntPtr config, PropertySet props)
        {
            var window = Marshal.GetIUnknownForObject(props);
            try
            {
                return eglCreateWindowSurface(display, config, window, IntPtr.Zero);
            }
            finally
            {
                Marshal.Release(window);
            }
        }

        /// <summary>GL thread. The backbuffer size is whatever ANGLE created for the panel.</summary>
        public void UpdatePanelSize(float dipWidth, float dipHeight, float compositionScale)
        {
            // Writing EGLRenderSurfaceSizeProperty here made the GL viewport larger than
            // the visible swap chain, so a centered menu sat on the right and the title clipped.
            float scale = compositionScale > 0.01f ? compositionScale : 1f;
            if (dipWidth > 1) _lastPixelW = Math.Max(64f, dipWidth * scale);
            if (dipHeight > 1) _lastPixelH = Math.Max(64f, dipHeight * scale);
        }

        public void MakeCurrent()
        {
            if (eglMakeCurrent(Display, Surface, Surface, Context) == 0)
                throw new InvalidOperationException("eglMakeCurrent failed: 0x" + eglGetError().ToString("X"));
        }

        public void Swap() => eglSwapBuffers(Display, Surface);

        public void QuerySize(out int width, out int height)
        {
            width = height = 0;
            if (Display == IntPtr.Zero || Surface == IntPtr.Zero) return;
            eglQuerySurface(Display, Surface, EGL_WIDTH, out width);
            eglQuerySurface(Display, Surface, EGL_HEIGHT, out height);
            // Do not inflate past the real backbuffer. A larger viewport than the
            // surface shifts a centered menu toward the right and clips the top.
            if (width < 1) width = (int)Math.Round(_lastPixelW);
            if (height < 1) height = (int)Math.Round(_lastPixelH);
        }

        public void Dispose()
        {
            if (Display != IntPtr.Zero)
                eglMakeCurrent(Display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Surface != IntPtr.Zero) eglDestroySurface(Display, Surface);
            if (Context != IntPtr.Zero) eglDestroyContext(Display, Context);
            if (Display != IntPtr.Zero) eglTerminate(Display);
            Display = Surface = Context = IntPtr.Zero;
            _windowProps = null;
        }

        public static IntPtr GetProc(string name) => eglGetProcAddress(name);

        public static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            if (op.Status == Windows.Foundation.AsyncStatus.Completed)
                return op.GetResults();
            using var done = new ManualResetEventSlim(false);
            T result = default;
            Exception error = null;
            op.Completed = (o, status) =>
            {
                try
                {
                    if (status == Windows.Foundation.AsyncStatus.Completed)
                        result = o.GetResults();
                    else
                        error = new InvalidOperationException("Async EGL helper failed: " + status);
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            };
            done.Wait();
            if (error != null) throw error;
            return result;
        }

        [DllImport("libEGL.dll", CharSet = CharSet.Ansi)]
        static extern IntPtr eglGetProcAddress(string name);

        [DllImport("libEGL.dll")]
        static extern IntPtr eglGetPlatformDisplayEXT(int platform, IntPtr nativeDisplay, int[] attribs);

        [DllImport("libEGL.dll")]
        static extern int eglInitialize(IntPtr display, out int major, out int minor);

        [DllImport("libEGL.dll")]
        static extern int eglChooseConfig(IntPtr display, int[] attribs, out IntPtr config, int configSize, out int numConfig);

        [DllImport("libEGL.dll")]
        static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attribs);

        [DllImport("libEGL.dll")]
        static extern IntPtr eglCreateWindowSurface(IntPtr display, IntPtr config, IntPtr nativeWindow, IntPtr attribs);

        [DllImport("libEGL.dll")]
        static extern int eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

        [DllImport("libEGL.dll")]
        static extern int eglSwapBuffers(IntPtr display, IntPtr surface);

        [DllImport("libEGL.dll")]
        static extern int eglQuerySurface(IntPtr display, IntPtr surface, int attribute, out int value);

        [DllImport("libEGL.dll")]
        static extern int eglDestroySurface(IntPtr display, IntPtr surface);

        [DllImport("libEGL.dll")]
        static extern int eglDestroyContext(IntPtr display, IntPtr context);

        [DllImport("libEGL.dll")]
        static extern int eglTerminate(IntPtr display);

        [DllImport("libEGL.dll")]
        public static extern int eglGetError();
    }
}
