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

        public static void Update(float dt)
        {
            if (dt < 0f) dt = 0f;
            Time += dt;
        }
    }
}
