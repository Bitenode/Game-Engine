#nullable enable
#if !WINDOWS
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Game_Engine.Core;

/// <summary>Minimal OpenAL playback for PCM WAV files (non-Windows player).</summary>
static class OpenAlAudio
{
    static IntPtr s_device, s_context;
    static bool s_ready, s_failed;

    public static void Ensure()
    {
        if (s_ready || s_failed) return;
        try
        {
            s_device = alcOpenDevice(null);
            if (s_device == IntPtr.Zero) { s_failed = true; return; }
            s_context = alcCreateContext(s_device, IntPtr.Zero);
            if (s_context == IntPtr.Zero) { s_failed = true; return; }
            alcMakeContextCurrent(s_context);
            s_ready = true;
        }
        catch
        {
            s_failed = true;
        }
    }

    public static void Shutdown()
    {
        if (!s_ready) return;
        try
        {
            alcMakeContextCurrent(IntPtr.Zero);
            if (s_context != IntPtr.Zero) alcDestroyContext(s_context);
            if (s_device != IntPtr.Zero) alcCloseDevice(s_device);
        }
        catch { }
        s_ready = false;
        s_context = IntPtr.Zero;
        s_device = IntPtr.Zero;
    }

    public static bool TryPlay(string path, float volume, float pitch, bool loop, out AudioHandle? handle)
    {
        handle = null;
        Ensure();
        if (!s_ready || !PcmWav.TryLoad(path, out var pcm, out int rate, out int ch))
            return false;

        alGenBuffers(1, out uint buf);
        alGenSources(1, out uint src);
        int format = ch == 1 ? 0x1101 : 0x1103; // AL_FORMAT_MONO16 / STEREO16
        var gch = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            alBufferData(buf, format, gch.AddrOfPinnedObject(), pcm.Length, rate);
        }
        finally { gch.Free(); }

        alSourcei(src, 0x1009, (int)buf); // AL_BUFFER
        alSourcef(src, 0x100A, Math.Clamp(volume, 0f, 1f)); // AL_GAIN
        alSourcef(src, 0x1003, Math.Clamp(pitch, 0.1f, 3f)); // AL_PITCH
        alSourcei(src, 0x1007, loop ? 1 : 0); // AL_LOOPING
        alSourcePlay(src);

        double seconds = pcm.Length / 2.0 / Math.Max(1, ch) / Math.Max(1, rate);
        handle = new AudioHandle(src, TimeSpan.FromSeconds(seconds));
        return true;
    }

    public static bool IsPlaying(uint src)
    {
        alGetSourcei(src, 0x1010, out int state); // AL_SOURCE_STATE
        return state == 0x1012; // AL_PLAYING
    }

    public static void SetGain(uint src, float g) => alSourcef(src, 0x100A, Math.Clamp(g, 0f, 1f));
    public static void SetPan(uint src, float pan)
    {
        float x = Math.Clamp(pan, -1f, 1f);
        alSource3f(src, 0x1004, x, 0f, -1f); // AL_POSITION
    }
    public static void Pause(uint src) => alSourcePause(src);
    public static void Resume(uint src) => alSourcePlay(src);
    public static void Stop(uint src)
    {
        alSourceStop(src);
        alDeleteSources(1, ref src);
    }

    public static void SetListener(System.Numerics.Vector3 pos, System.Numerics.Vector3 fwd, System.Numerics.Vector3 up)
    {
        if (!s_ready) return;
        alListener3f(0x1004, pos.X, pos.Y, pos.Z);
        float[] ori = { fwd.X, fwd.Y, fwd.Z, up.X, up.Y, up.Z };
        alListenerfv(0x100F, ori);
    }

    const string Lib = "openal";

    [DllImport(Lib)] static extern IntPtr alcOpenDevice(string? name);
    [DllImport(Lib)] static extern IntPtr alcCreateContext(IntPtr device, IntPtr attr);
    [DllImport(Lib)] static extern bool alcMakeContextCurrent(IntPtr ctx);
    [DllImport(Lib)] static extern void alcDestroyContext(IntPtr ctx);
    [DllImport(Lib)] static extern bool alcCloseDevice(IntPtr device);
    [DllImport(Lib)] static extern void alGenBuffers(int n, out uint buffers);
    [DllImport(Lib)] static extern void alGenSources(int n, out uint sources);
    [DllImport(Lib)] static extern void alDeleteSources(int n, ref uint sources);
    [DllImport(Lib)] static extern void alBufferData(uint buffer, int format, IntPtr data, int size, int freq);
    [DllImport(Lib)] static extern void alSourcei(uint source, int param, int value);
    [DllImport(Lib)] static extern void alSourcef(uint source, int param, float value);
    [DllImport(Lib)] static extern void alSource3f(uint source, int param, float x, float y, float z);
    [DllImport(Lib)] static extern void alSourcePlay(uint source);
    [DllImport(Lib)] static extern void alSourcePause(uint source);
    [DllImport(Lib)] static extern void alSourceStop(uint source);
    [DllImport(Lib)] static extern void alGetSourcei(uint source, int param, out int value);
    [DllImport(Lib)] static extern void alListener3f(int param, float x, float y, float z);
    [DllImport(Lib)] static extern void alListenerfv(int param, float[] values);
}

static class PcmWav
{
    public static bool TryLoad(string path, out byte[] pcm, out int sampleRate, out int channels)
    {
        pcm = Array.Empty<byte>();
        sampleRate = 44100;
        channels = 2;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (new string(br.ReadChars(4)) != "RIFF") return false;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") return false;

            short bits = 16;
            while (fs.Position + 8 <= fs.Length)
            {
                var id = new string(br.ReadChars(4));
                int size = br.ReadInt32();
                long next = fs.Position + size;
                if (id == "fmt ")
                {
                    short fmt = br.ReadInt16();
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32();
                    br.ReadInt16();
                    bits = br.ReadInt16();
                    if (fmt != 1 || bits != 16) return false;
                }
                else if (id == "data")
                {
                    pcm = br.ReadBytes(size);
                    return pcm.Length > 0;
                }
                fs.Position = next + (size & 1);
            }
        }
        catch { }
        return false;
    }
}

static class SharedAudioPaths
{
    public static string? Resolve(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (Path.IsPathRooted(filePath) && File.Exists(filePath)) return filePath;
        var root = ProjectService.Current?.RootPath;
        if (!string.IsNullOrEmpty(root))
        {
            var c = Path.GetFullPath(Path.Combine(root, filePath));
            if (File.Exists(c)) return c;
            c = Path.GetFullPath(Path.Combine(root, "Assets", filePath));
            if (File.Exists(c)) return c;
            var assets = Path.Combine(root, "Assets");
            if (Directory.Exists(assets))
            {
                try
                {
                    var found = Directory.GetFiles(assets, Path.GetFileName(filePath), SearchOption.AllDirectories);
                    if (found.Length > 0) return found[0];
                }
                catch { }
            }
        }
        return File.Exists(filePath) ? Path.GetFullPath(filePath) : null;
    }
}
#endif
