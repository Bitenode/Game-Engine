#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Game_Engine.Core.Input;

/// <summary>
/// Optional SDL2 gamecontroller backend (Linux/macOS). Loads SDL2 dynamically;
/// if the library is missing, pads stay disconnected.
/// </summary>
public sealed class SdlGamepadBackend : IInputBackend
{
    const int MaxPads = Gamepad.MaxPads;
    readonly GamepadState[] _pads = new GamepadState[MaxPads];
    readonly IntPtr[] _controllers = new IntPtr[MaxPads];
    bool _ready;
    bool _failed;

    public int ConnectedGamepadCount { get; private set; }

    public GamepadState GetGamepadState(int index)
    {
        if ((uint)index >= MaxPads) return default;
        return _pads[index];
    }

    public void SetGamepadVibration(int index, float leftMotor, float rightMotor)
    {
        if (!_ready || (uint)index >= MaxPads || _controllers[index] == IntPtr.Zero) return;
        ushort lo = (ushort)Math.Clamp((int)(leftMotor * 0xFFFF), 0, 0xFFFF);
        ushort hi = (ushort)Math.Clamp((int)(rightMotor * 0xFFFF), 0, 0xFFFF);
        try { SDL_GameControllerRumble(_controllers[index], lo, hi, 120); } catch { }
    }

    public void Poll()
    {
        if (_failed) return;
        if (!_ready) TryInit();
        if (!_ready) return;

        try { SDL_PumpEvents(); } catch { _failed = true; return; }

        ConnectedGamepadCount = 0;
        Array.Clear(_pads, 0, _pads.Length);
        for (int i = 0; i < MaxPads; i++)
        {
            if (_controllers[i] == IntPtr.Zero)
            {
                try
                {
                    if (SDL_IsGameController(i) == 1)
                        _controllers[i] = SDL_GameControllerOpen(i);
                }
                catch { continue; }
            }

            var c = _controllers[i];
            if (c == IntPtr.Zero) continue;
            if (SDL_GameControllerGetAttached(c) != 1)
            {
                _controllers[i] = IntPtr.Zero;
                continue;
            }

            _pads[i] = new GamepadState
            {
                Connected = true,
                Buttons = ReadButtons(c),
                LeftStickX = Axis(c, 0),
                LeftStickY = -Axis(c, 1),
                RightStickX = Axis(c, 2),
                RightStickY = -Axis(c, 3),
                LeftTrigger = Trigger(c, 4),
                RightTrigger = Trigger(c, 5)
            };
            ConnectedGamepadCount++;
        }
        Gamepad.OverwriteStates(_pads);
    }

    void TryInit()
    {
        try
        {
            if (SDL_Init(0x00002000) != 0) { _failed = true; return; }
            _ready = true;
        }
        catch (DllNotFoundException)
        {
            _failed = true;
        }
        catch (EntryPointNotFoundException)
        {
            _failed = true;
        }
    }

    static float Axis(IntPtr c, int axis)
    {
        short v = SDL_GameControllerGetAxis(c, axis);
        return Math.Clamp(v < 0 ? v / 32768f : v / 32767f, -1f, 1f);
    }

    static float Trigger(IntPtr c, int axis) => Math.Clamp(SDL_GameControllerGetAxis(c, axis) / 32767f, 0f, 1f);

    static ushort ReadButtons(IntPtr c)
    {
        ushort bits = 0;
        if (SDL_GameControllerGetButton(c, 11) != 0) bits |= 0x0001; // dpad up
        if (SDL_GameControllerGetButton(c, 12) != 0) bits |= 0x0002;
        if (SDL_GameControllerGetButton(c, 13) != 0) bits |= 0x0004;
        if (SDL_GameControllerGetButton(c, 14) != 0) bits |= 0x0008;
        if (SDL_GameControllerGetButton(c, 6) != 0) bits |= 0x0010; // start
        if (SDL_GameControllerGetButton(c, 4) != 0) bits |= 0x0020; // back
        if (SDL_GameControllerGetButton(c, 7) != 0) bits |= 0x0040; // L3
        if (SDL_GameControllerGetButton(c, 8) != 0) bits |= 0x0080;
        if (SDL_GameControllerGetButton(c, 9) != 0) bits |= 0x0100; // LB
        if (SDL_GameControllerGetButton(c, 10) != 0) bits |= 0x0200;
        if (SDL_GameControllerGetButton(c, 0) != 0) bits |= 0x1000; // A
        if (SDL_GameControllerGetButton(c, 1) != 0) bits |= 0x2000;
        if (SDL_GameControllerGetButton(c, 2) != 0) bits |= 0x4000;
        if (SDL_GameControllerGetButton(c, 3) != 0) bits |= 0x8000;
        return bits;
    }

    [DllImport("SDL2", EntryPoint = "SDL_Init")]
    static extern int SDL_Init(uint flags);
    [DllImport("SDL2", EntryPoint = "SDL_PumpEvents")]
    static extern void SDL_PumpEvents();
    [DllImport("SDL2", EntryPoint = "SDL_IsGameController")]
    static extern int SDL_IsGameController(int joystickIndex);
    [DllImport("SDL2", EntryPoint = "SDL_GameControllerOpen")]
    static extern IntPtr SDL_GameControllerOpen(int joystickIndex);
    [DllImport("SDL2", EntryPoint = "SDL_GameControllerGetAttached")]
    static extern int SDL_GameControllerGetAttached(IntPtr gamecontroller);
    [DllImport("SDL2", EntryPoint = "SDL_GameControllerGetAxis")]
    static extern short SDL_GameControllerGetAxis(IntPtr gamecontroller, int axis);
    [DllImport("SDL2", EntryPoint = "SDL_GameControllerGetButton")]
    static extern byte SDL_GameControllerGetButton(IntPtr gamecontroller, int button);
    [DllImport("SDL2", EntryPoint = "SDL_GameControllerRumble")]
    static extern int SDL_GameControllerRumble(IntPtr gamecontroller, ushort low, ushort high, uint durationMs);
}
