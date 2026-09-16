#nullable enable
using System;

namespace Game_Engine.Core.Input;

/// <summary>
/// Windows pads: XInput first (Xbox / GameInput-compatible XInput devices), then SDL2
/// for DualSense/Steam Input when XInput sees nothing.
/// </summary>
public sealed class WindowsGamepadBackend : IInputBackend
{
    readonly XInputBackend _xinput = new();
    readonly SdlGamepadBackend _sdl = new();
    readonly GameInputBackend _gameInput = new();
    IInputBackend _active;

    public WindowsGamepadBackend() => _active = _xinput;

    public int ConnectedGamepadCount => _active.ConnectedGamepadCount;

    public GamepadState GetGamepadState(int index) => _active.GetGamepadState(index);

    public void SetGamepadVibration(int index, float leftMotor, float rightMotor)
        => _active.SetGamepadVibration(index, leftMotor, rightMotor);

    public void Poll()
    {
        _xinput.Poll();
        if (_xinput.ConnectedGamepadCount > 0)
        {
            _active = _xinput;
            _gameInput.NoteXInputActive(_xinput.ConnectedGamepadCount);
            return;
        }

        _sdl.Poll();
        if (_sdl.ConnectedGamepadCount > 0)
        {
            _active = _sdl;
            return;
        }

        _gameInput.Poll();
        _active = _gameInput.ConnectedGamepadCount > 0 ? _gameInput : _xinput;
    }
}
