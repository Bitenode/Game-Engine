#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Rendering;

/// <summary>
/// Where the game thread's time goes, for a host that has no profiler panel (the console
/// player writes it to its log). A frame is cut into consecutive parts: <see cref="Enter"/>
/// charges the time since the previous call to the part that was running and starts the
/// next one. The host closes each frame with <see cref="EndFrame"/> and every few seconds
/// writes <see cref="TakeReport"/>. One timestamp read per part; nothing is allocated
/// between reports.
/// </summary>
public static class FrameProfile
{
    public enum Part
    {
        Other,
        Scripts,
        Physics,
        SceneUpkeep,
        Sky,
        Shadows,
        SceneLod,
        PlanetLod,
        PlantStreaming,
        Scene,
        PlanetGround,
        PlanetPlants,
        SceneAfterPlanet,
        Water,
        Atmosphere,
        Clouds,
        PlanetWater,
        WorldUi,
        Post,
        Particles,
        Ui,
        Finish,
        Present,
        Count
    }

    static readonly string[] s_names =
    {
        "other", "scripts", "physics", "scene upkeep", "sky", "shadows", "scene LOD", "planet LOD",
        "plant streaming", "scene", "planet ground", "planet plants", "scene after planet", "water",
        "atmosphere", "clouds", "planet water", "world UI", "post", "particles", "UI", "finish", "present",
    };

    static readonly long[] s_frame = new long[(int)Part.Count];
    static readonly long[] s_total = new long[(int)Part.Count];
    static readonly long[] s_slowest = new long[(int)Part.Count];
    static long s_slowestTicks;
    static Part s_current;
    static long s_mark;
    static int s_frames;

    static long s_draws, s_meshUploads, s_meshUploadTicks, s_textureUploads, s_textureUploadTicks;
    static long s_modelImports;

    // What the counters read when this window started.
    static long s_windowAt, s_windowAllocated, s_windowImports;
    static int s_windowGc0, s_windowGc1, s_windowGc2;
    static TimeSpan s_windowPause, s_windowCpu;

    /// <summary>Charge the time since the last call to the part that was running, then start <paramref name="next"/>.</summary>
    public static void Enter(Part next)
    {
        long now = Stopwatch.GetTimestamp();
        if (s_mark != 0)
            s_frame[(int)s_current] += now - s_mark;
        s_mark = now;
        s_current = next;
    }

    /// <summary>Close the frame: add it to the window and remember it if it is the slowest so far.</summary>
    public static void EndFrame()
    {
        Enter(Part.Other);
        long sum = 0;
        for (int i = 0; i < s_frame.Length; i++)
            sum += s_frame[i];
        bool slowest = sum > s_slowestTicks;
        if (slowest)
            s_slowestTicks = sum;
        for (int i = 0; i < s_frame.Length; i++)
        {
            s_total[i] += s_frame[i];
            if (slowest)
                s_slowest[i] = s_frame[i];
            s_frame[i] = 0;
        }
        s_frames++;
    }

    public static void CountDraw() => s_draws++;

    public static void CountMeshUpload(long ticks)
    {
        s_meshUploads++;
        s_meshUploadTicks += ticks;
    }

    public static void CountTextureUpload(long ticks)
    {
        s_textureUploads++;
        s_textureUploadTicks += ticks;
    }

    /// <summary>A model file was imported. Called from any thread.</summary>
    public static void CountModelImport() => Interlocked.Increment(ref s_modelImports);

    /// <summary>Forget what was gathered and start measuring from now.</summary>
    public static void StartWindow()
    {
        Array.Clear(s_frame);
        Array.Clear(s_total);
        Array.Clear(s_slowest);
        s_slowestTicks = 0;
        s_frames = 0;
        s_draws = s_meshUploads = s_meshUploadTicks = s_textureUploads = s_textureUploadTicks = 0;
        s_mark = Stopwatch.GetTimestamp();
        s_windowAt = s_mark;
        s_windowAllocated = GC.GetTotalAllocatedBytes(false);
        s_windowImports = Interlocked.Read(ref s_modelImports);
        s_windowGc0 = GC.CollectionCount(0);
        s_windowGc1 = GC.CollectionCount(1);
        s_windowGc2 = GC.CollectionCount(2);
        s_windowPause = GC.GetTotalPauseDuration();
        s_windowCpu = ProcessCpu();
    }

    /// <summary>The frames since the window started, as log lines. Starts a new window.</summary>
    public static string[] TakeReport()
    {
        var lines = new List<string>(5);
        double msPerTick = 1000.0 / Stopwatch.Frequency;
        int frames = Math.Max(1, s_frames);
        double seconds = Math.Max(1e-3, (Stopwatch.GetTimestamp() - s_windowAt) / (double)Stopwatch.Frequency);

        lines.Add("by part, ms per frame: " + Largest(s_total, msPerTick / frames, 14, 0.05));
        if (s_slowestTicks > 0)
            lines.Add($"slowest frame {s_slowestTicks * msPerTick:F0} ms: " + Largest(s_slowest, msPerTick, 6, 0.5));

        lines.Add($"{s_draws / frames} draw calls per frame; sent to the GPU: {s_meshUploads} meshes in {s_meshUploadTicks * msPerTick:F0} ms, "
            + $"{s_textureUploads} textures in {s_textureUploadTicks * msPerTick:F0} ms");

        double allocatedMb = (GC.GetTotalAllocatedBytes(false) - s_windowAllocated) / 1048576.0;
        var machine = new StringBuilder(200);
        machine.Append($"allocated {allocatedMb / seconds:F0} MB/s, managed heap {GC.GetTotalMemory(false) / 1048576.0:F0} MB, ")
            .Append($"collections gen0 {GC.CollectionCount(0) - s_windowGc0} gen1 {GC.CollectionCount(1) - s_windowGc1} gen2 {GC.CollectionCount(2) - s_windowGc2} ")
            .Append($"paused {(GC.GetTotalPauseDuration() - s_windowPause).TotalMilliseconds:F0} ms");
        var cpu = ProcessCpu();
        if (cpu > TimeSpan.Zero && s_windowCpu > TimeSpan.Zero)
            machine.Append($"; CPU {(cpu - s_windowCpu).TotalSeconds / seconds:F1} of {Environment.ProcessorCount} cores busy");
        machine.Append($"; thread pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued");
        lines.Add(machine.ToString());

        int planets = 0, drawn = 0, building = 0, waiting = 0, baked = 0;
        foreach (var planet in PlanetTerrain.ActivePlanets)
        {
            var chunks = planet?.ChunkManager;
            if (planet == null || chunks == null) continue;
            planets++;
            drawn += chunks.GetRenderableLeaves().Count;
            building += chunks.ActiveJobs;
            waiting += chunks.PendingCompletedJobs;
            if (planet.HasBakedStandSurface) baked++;
        }
        long imports = Interlocked.Read(ref s_modelImports) - s_windowImports;
        lines.Add(planets > 0
            ? $"planet: {drawn} chunks drawn, {building} building, {waiting} built and waiting, height map {(baked == planets ? "baked" : "still baking")}; model files imported {imports}"
            : $"model files imported {imports}");

        StartWindow();
        return lines.ToArray();
    }

    static string Largest(long[] ticks, double scale, int most, double floorMs)
    {
        Span<int> order = stackalloc int[ticks.Length];
        int n = 0;
        for (int i = 0; i < ticks.Length; i++)
        {
            if (ticks[i] * scale >= floorMs)
                order[n++] = i;
        }
        // Largest first. A handful of entries: insertion sort.
        for (int i = 1; i < n; i++)
        {
            int at = order[i], j = i - 1;
            while (j >= 0 && ticks[order[j]] < ticks[at])
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = at;
        }
        if (n == 0)
            return "nothing above " + floorMs.ToString("0.##") + " ms";
        var sb = new StringBuilder(160);
        for (int i = 0; i < n && i < most; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(s_names[order[i]]).Append(' ').Append((ticks[order[i]] * scale).ToString("F1"));
        }
        return sb.ToString();
    }

    static TimeSpan ProcessCpu()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch { return TimeSpan.Zero; }
    }
}
