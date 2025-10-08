using System;

namespace Game_Engine.Core
{
    /// Global frame timing for scripts.
    public static class Time
    {
        // Accumulated clocks
        public static float time { get; private set; }              // seconds since Play (Update clock)
        public static float fixedTime { get; private set; }         // seconds since Play (Fixed clock)

        // Per-step deltas
        public static float deltaTime { get; private set; }         // last Update dt
        public static float fixedDeltaTime { get; private set; }    // last FixedUpdate dt

        // Counters
        public static int frameCount { get; private set; }
        public static int fixedFrameCount { get; private set; }

        internal static void Reset()
        {
            time = fixedTime = 0f;
            deltaTime = fixedDeltaTime = 0f;
            frameCount = fixedFrameCount = 0;
        }

        internal static void BeginUpdate(double dt)
        {
            deltaTime = (float)Math.Max(0.0, dt);
            time += deltaTime;
            frameCount++;
        }

        internal static void BeginFixedUpdate(double dt)
        {
            fixedDeltaTime = (float)Math.Max(0.0, dt);
            fixedTime += fixedDeltaTime;
            fixedFrameCount++;
        }
    }
}
