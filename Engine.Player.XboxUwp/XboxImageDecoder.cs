using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Game_Engine.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Engine.Player.XboxUwp
{
    /// <summary>PNG/JPEG/BMP/GIF via Windows Imaging. TGA stays on the engine's own decoder.</summary>
    public static class XboxImageDecoder
    {
        public static void Register()
        {
            TextureImageDecoder.DecodeFile = DecodeFile;
            TextureImageDecoder.DecodeBytes = DecodeBytes;
        }

        static Texture2D DecodeFile(string path)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".tga" or ".targa")
                throw new InvalidOperationException("TGA is decoded by the engine, not WIC: " + path);
            return DecodeBytes(System.IO.File.ReadAllBytes(path), path);
        }

        static Texture2D DecodeBytes(byte[] encoded) => DecodeBytes(encoded, null);

        static Texture2D DecodeBytes(byte[] encoded, string? path)
        {
            // Completions must not marshal back onto the GL thread or the Wait deadlocks.
            var previous = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                return DecodeBytesCore(encoded, path);
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        static Texture2D DecodeBytesCore(byte[] encoded, string? path)
        {
            var mem = new InMemoryRandomAccessStream();
            var writer = new DataWriter(mem);
            writer.WriteBytes(encoded);
            Wait(writer.StoreAsync());
            writer.DetachStream();
            mem.Seek(0);
            var decoder = Wait(BitmapDecoder.CreateAsync(mem));
            // Planet biomes include multi-thousand-px TIFFs. Keep console decode small so the
            // final texture wave + fauna cannot OOM after the player can already walk around.
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            // Biome sheets are huge. Font atlases must stay full size or menu type turns into a blur.
            bool fontAtlas = path != null && path.Contains($"{System.IO.Path.DirectorySeparatorChar}Fonts{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            uint maxEdge = fontAtlas ? 4096u : 512u;
            uint tw = w, th = h;
            if (w > maxEdge || h > maxEdge)
            {
                if (w >= h)
                {
                    tw = maxEdge;
                    th = Math.Max(1, h * maxEdge / w);
                }
                else
                {
                    th = maxEdge;
                    tw = Math.Max(1, w * maxEdge / h);
                }
            }
            var transform = new BitmapTransform
            {
                ScaledWidth = tw,
                ScaledHeight = th,
                InterpolationMode = BitmapInterpolationMode.Linear
            };
            var data = Wait(decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage));
            var pixels = data.DetachPixelData();
            return new Texture2D((int)tw, (int)th, pixels, path);
        }

        static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
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
                    if (status == Windows.Foundation.AsyncStatus.Completed) result = o.GetResults();
                    else error = new InvalidOperationException("WIC op " + status);
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            };
            done.Wait();
            if (error != null) throw error;
            return result;
        }
    }
}
