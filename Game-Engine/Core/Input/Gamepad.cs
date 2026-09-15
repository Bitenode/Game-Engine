#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Game_Engine.Core.Input
{
    /// <summary>Xbox-layout digital controls (including analog triggers past a threshold).</summary>
    public enum GamepadButton
    {
        None = 0,
        A,
        B,
        X,
        Y,
        LeftShoulder,
        RightShoulder,
        LeftStick,
        RightStick,
        Start,
        Back,
        DPadUp,
        DPadDown,
        DPadLeft,
        DPadRight,
        LeftTrigger,
        RightTrigger
    }

    /// <summary>Analog stick and trigger axes. Stick range is −1…1; triggers are 0…1.</summary>
    public enum GamepadAxis
    {
        None = 0,
        LeftStickX,
        LeftStickY,
        RightStickX,
        RightStickY,
        LeftTrigger,
        RightTrigger
    }

    /// <summary>Live snapshot of one XInput-compatible pad.</summary>
    public struct GamepadState
    {
        public bool Connected;
        public ushort Buttons;
        public float LeftStickX;
        public float LeftStickY;
        public float RightStickX;
        public float RightStickY;
        public float LeftTrigger;
        public float RightTrigger;
    }

    /// <summary>
    /// Windows XInput poller (Xbox 360/One/Series pads and compatible).
    /// Non-Windows platforms report no devices until a later backend is added.
    /// </summary>
    public static class Gamepad
    {
        public const int MaxPads = 4;

        public static int ConnectedCount { get; private set; }

        static readonly GamepadState[] s_pads = new GamepadState[MaxPads];
        static bool s_leftTriggerHeld, s_rightTriggerHeld;
        static bool s_xinputUnavailable;

        public static GamepadState GetState(int index)
        {
            if ((uint)index >= MaxPads) return default;
            return s_pads[index];
        }

        /// <summary>Merged analog from the first connected pad (0 if none).</summary>
        public static float GetAxisRaw(GamepadAxis axis)
        {
            for (int i = 0; i < MaxPads; i++)
            {
                if (!s_pads[i].Connected) continue;
                return AxisValue(in s_pads[i], axis);
            }
            return 0f;
        }

        public static float AxisValue(in GamepadState s, GamepadAxis axis) => axis switch
        {
            GamepadAxis.LeftStickX => s.LeftStickX,
            GamepadAxis.LeftStickY => s.LeftStickY,
            GamepadAxis.RightStickX => s.RightStickX,
            GamepadAxis.RightStickY => s.RightStickY,
            GamepadAxis.LeftTrigger => s.LeftTrigger,
            GamepadAxis.RightTrigger => s.RightTrigger,
            _ => 0f
        };

        /// <summary>Refresh <see cref="s_pads"/>. Returns how many pads are connected.</summary>
        public static int PollHardware()
        {
            ConnectedCount = 0;
            if (s_xinputUnavailable || !OperatingSystem.IsWindows())
            {
                Array.Clear(s_pads, 0, s_pads.Length);
                return 0;
            }

            for (int i = 0; i < MaxPads; i++)
            {
                if (!XInput.TryGetState(i, out var native))
                {
                    if (i == 0 && XInput.LibraryMissing)
                    {
                        s_xinputUnavailable = true;
                        Array.Clear(s_pads, 0, s_pads.Length);
                        return 0;
                    }
                    s_pads[i] = default;
                    continue;
                }

                s_pads[i] = new GamepadState
                {
                    Connected = true,
                    Buttons = native.Gamepad.wButtons,
                    LeftStickX = Stick(native.Gamepad.sThumbLX),
                    LeftStickY = Stick(native.Gamepad.sThumbLY),
                    RightStickX = Stick(native.Gamepad.sThumbRX),
                    RightStickY = Stick(native.Gamepad.sThumbRY),
                    LeftTrigger = native.Gamepad.bLeftTrigger / 255f,
                    RightTrigger = native.Gamepad.bRightTrigger / 255f
                };
                ConnectedCount++;
            }

            return ConnectedCount;
        }

        /// <summary>OR of face/bumper/dpad bits across all connected pads.</summary>
        public static ushort MergedButtons()
        {
            ushort bits = 0;
            for (int i = 0; i < MaxPads; i++)
                if (s_pads[i].Connected) bits |= s_pads[i].Buttons;
            return bits;
        }

        public static bool DigitalTriggerHeld(bool left, float downThreshold = 0.55f, float upThreshold = 0.40f)
        {
            float v = 0f;
            for (int i = 0; i < MaxPads; i++)
            {
                if (!s_pads[i].Connected) continue;
                float t = left ? s_pads[i].LeftTrigger : s_pads[i].RightTrigger;
                if (t > v) v = t;
            }

            ref bool held = ref left ? ref s_leftTriggerHeld : ref s_rightTriggerHeld;
            if (held)
            {
                if (v < upThreshold) held = false;
            }
            else if (v > downThreshold)
                held = true;
            return held;
        }

        static float Stick(short raw)
        {
            float v = raw < 0 ? raw / 32768f : raw / 32767f;
            return Math.Clamp(v, -1f, 1f);
        }

        public static bool ButtonBit(ushort bits, GamepadButton button) => button switch
        {
            GamepadButton.DPadUp => (bits & 0x0001) != 0,
            GamepadButton.DPadDown => (bits & 0x0002) != 0,
            GamepadButton.DPadLeft => (bits & 0x0004) != 0,
            GamepadButton.DPadRight => (bits & 0x0008) != 0,
            GamepadButton.Start => (bits & 0x0010) != 0,
            GamepadButton.Back => (bits & 0x0020) != 0,
            GamepadButton.LeftStick => (bits & 0x0040) != 0,
            GamepadButton.RightStick => (bits & 0x0080) != 0,
            GamepadButton.LeftShoulder => (bits & 0x0100) != 0,
            GamepadButton.RightShoulder => (bits & 0x0200) != 0,
            GamepadButton.A => (bits & 0x1000) != 0,
            GamepadButton.B => (bits & 0x2000) != 0,
            GamepadButton.X => (bits & 0x4000) != 0,
            GamepadButton.Y => (bits & 0x8000) != 0,
            _ => false
        };
    }

    static class XInput
    {
        public static bool LibraryMissing { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GamepadNative
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StateNative
        {
            public uint dwPacketNumber;
            public GamepadNative Gamepad;
        }

        const uint ErrorDeviceNotConnected = 1167;

        static readonly string[] s_dlls = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };
        static int s_dllIndex;

        public static bool TryGetState(int userIndex, out StateNative state)
        {
            state = default;
            if (LibraryMissing) return false;

            for (int attempt = 0; attempt < s_dlls.Length; attempt++)
            {
                try
                {
                    uint err = s_dllIndex switch
                    {
                        1 => Native13.XInputGetState(userIndex, out state),
                        2 => Native91.XInputGetState(userIndex, out state),
                        _ => Native14.XInputGetState(userIndex, out state)
                    };
                    if (err == 0) return true;
                    if (err == ErrorDeviceNotConnected) { state = default; return false; }
                    return false;
                }
                catch (DllNotFoundException)
                {
                    s_dllIndex++;
                    if (s_dllIndex >= s_dlls.Length)
                    {
                        LibraryMissing = true;
                        return false;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    s_dllIndex++;
                    if (s_dllIndex >= s_dlls.Length)
                    {
                        LibraryMissing = true;
                        return false;
                    }
                }
            }

            LibraryMissing = true;
            return false;
        }

        static class Native14
        {
            [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
            public static extern uint XInputGetState(int dwUserIndex, out StateNative pState);
        }

        static class Native13
        {
            [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
            public static extern uint XInputGetState(int dwUserIndex, out StateNative pState);
        }

        static class Native91
        {
            [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
            public static extern uint XInputGetState(int dwUserIndex, out StateNative pState);
        }
    }
}
