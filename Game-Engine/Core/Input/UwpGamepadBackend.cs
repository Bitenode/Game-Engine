#if XBOX_UWP
#nullable enable
using System;
using Windows.Gaming.Input;
using WinPad = Windows.Gaming.Input.Gamepad;

namespace Game_Engine.Core.Input
{
    /// <summary>Xbox pads through Windows.Gaming.Input. No XInput or SDL.</summary>
    public sealed class UwpGamepadBackend : IInputBackend
    {
        readonly GamepadState[] _pads = new GamepadState[Gamepad.MaxPads];

        public int ConnectedGamepadCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pads.Length; i++)
                    if (_pads[i].Connected) n++;
                return n;
            }
        }

        public GamepadState GetGamepadState(int index)
        {
            if ((uint)index >= _pads.Length) return default;
            return _pads[index];
        }

        public void Poll()
        {
            var pads = WinPad.Gamepads;
            int n = Math.Min(Gamepad.MaxPads, pads.Count);
            for (int i = 0; i < Gamepad.MaxPads; i++)
            {
                if (i >= n)
                {
                    _pads[i] = default;
                    continue;
                }
                var reading = pads[i].GetCurrentReading();
                ushort bits = 0;
                void set(GamepadButtons flag, ushort bit)
                {
                    if ((reading.Buttons & flag) != 0) bits |= bit;
                }
                set(GamepadButtons.DPadUp, 0x0001);
                set(GamepadButtons.DPadDown, 0x0002);
                set(GamepadButtons.DPadLeft, 0x0004);
                set(GamepadButtons.DPadRight, 0x0008);
                set(GamepadButtons.Menu, 0x0010);
                set(GamepadButtons.View, 0x0020);
                set(GamepadButtons.LeftThumbstick, 0x0040);
                set(GamepadButtons.RightThumbstick, 0x0080);
                set(GamepadButtons.LeftShoulder, 0x0100);
                set(GamepadButtons.RightShoulder, 0x0200);
                set(GamepadButtons.A, 0x1000);
                set(GamepadButtons.B, 0x2000);
                set(GamepadButtons.X, 0x4000);
                set(GamepadButtons.Y, 0x8000);
                _pads[i] = new GamepadState
                {
                    Connected = true,
                    Buttons = bits,
                    LeftStickX = (float)reading.LeftThumbstickX,
                    LeftStickY = (float)reading.LeftThumbstickY,
                    RightStickX = (float)reading.RightThumbstickX,
                    RightStickY = (float)reading.RightThumbstickY,
                    LeftTrigger = (float)reading.LeftTrigger,
                    RightTrigger = (float)reading.RightTrigger
                };
            }
            Gamepad.OverwriteStates(_pads);
        }

        public void SetGamepadVibration(int index, float leftMotor, float rightMotor)
        {
            var pads = WinPad.Gamepads;
            if ((uint)index >= pads.Count) return;
            pads[index].Vibration = new GamepadVibration
            {
                LeftMotor = Math.Clamp(leftMotor, 0f, 1f),
                RightMotor = Math.Clamp(rightMotor, 0f, 1f)
            };
        }
    }
}
#endif
