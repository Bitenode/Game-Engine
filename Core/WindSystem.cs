using System;
using SN = System.Numerics;

namespace Game_Engine.Core
{
    public static class WindSystem
    {
        // Accumulated time (seconds)
        public static float Time;

        // Unit direction of wind (world space)
        public static SN.Vector3 Direction = SN.Vector3.UnitX;

        // Overall strength (tune in Inspector/UI later)
        public static float Amplitude = 0.08f; // 0.00..~0.25 is typical

        // Wind variation over time (0 = steady, 1 = very gusty)
        public static float Gustiness = 0.4f;

        // Spatial turbulence frequency (higher = more varied across space)
        public static float TurbulenceFrequency = 1.0f;

        /// <summary>
        /// Returns the current effective wind strength including gust variation.
        /// </summary>
        public static float GetCurrentStrength()
        {
            float gust = 1f + Gustiness * MathF.Sin(Time * 0.7f) * MathF.Cos(Time * 0.31f);
            return Amplitude * Math.Max(0f, gust);
        }

        public static void Update(float dt)
        {
            if (dt < 0f) dt = 0f;
            Time += dt;
        }
    }
}
