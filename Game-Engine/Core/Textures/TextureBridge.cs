#nullable enable
using System;
using System.Reflection;

namespace Game_Engine.Core;

public static class TextureBridge
{
    private static Texture2D? TryCreateEngineTextureFromPath(string path)
    {
        var t = typeof(Texture2D);

        var m = t.GetMethod("FromFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (m is not null) return (Texture2D?)m.Invoke(null, new object?[] { path });

        m = t.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (m is not null) return (Texture2D?)m.Invoke(null, new object?[] { path });

        var ctor = t.GetConstructor(new[] { typeof(string) });
        if (ctor is not null) return (Texture2D?)ctor.Invoke(new object?[] { path });

        return null;
    }

    private static Texture2D? TryCreateEngineTextureFromBytes(byte[] bytes)
    {
        var t = typeof(Texture2D);
        var m = t.GetMethod("FromBytes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(byte[]) }, modifiers: null)
             ?? t.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            binder: null, types: new[] { typeof(byte[]) }, modifiers: null);
        return m is null ? null : (Texture2D?)m.Invoke(null, new object?[] { bytes });
    }

    /// Accepts path/stream/byte[]/bitmap/engine objects and returns a real Texture2D if possible.
    public static Texture2D? EnsureEngineTexture2D(object? texObj)
    {
        if (texObj is null) return null;
        if (texObj is Texture2D t2d) return t2d;

        var t = texObj.GetType();

        foreach (var n in new[] { "Path", "FilePath", "SourcePath" })
        {
            var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p?.GetValue(texObj) is string s && !string.IsNullOrWhiteSpace(s) && System.IO.File.Exists(s))
            {
                var tex = TryCreateEngineTextureFromPath(s);
                if (tex != null) return tex;

                try
                {
                    var bytes = System.IO.File.ReadAllBytes(s);
                    tex = TryCreateEngineTextureFromBytes(bytes);
                    if (tex != null) return tex;
                }
                catch { }
            }
        }

        if (t.GetMethod("OpenRead", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                is { } open &&
            open.Invoke(texObj, null) is System.IO.Stream stream)
        {
            try
            {
                using (stream)
                using (var ms = new System.IO.MemoryStream())
                {
                    stream.CopyTo(ms);
                    var tex = TryCreateEngineTextureFromBytes(ms.ToArray());
                    if (tex != null) return tex;
                }
            }
            catch { }
        }

        foreach (var n in new[] { "GetBytes", "ToBytes" })
        {
            var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m?.Invoke(texObj, null) is byte[] bytes && bytes.Length > 0)
            {
                var tex = TryCreateEngineTextureFromBytes(bytes);
                if (tex != null) return tex;
            }
        }

        if (texObj is Avalonia.Media.Imaging.Bitmap bmp)
        {
            using var ms = new System.IO.MemoryStream();
            try
            {
                bmp.Save(ms); // PNG
                var tex = TryCreateEngineTextureFromBytes(ms.ToArray());
                if (tex != null) return tex;
            }
            catch { }
        }

        return null;
    }
}
