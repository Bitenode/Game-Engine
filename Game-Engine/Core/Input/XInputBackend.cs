#nullable enable
namespace Game_Engine.Core.Input;

/// <summary>Windows XInput pads 0–3 plus rumble.</summary>
public sealed class XInputBackend : IInputBackend
{
    public int ConnectedGamepadCount => Gamepad.ConnectedCount;

    public void Poll() => Gamepad.PollHardware();

    public GamepadState GetGamepadState(int index) => Gamepad.GetState(index);

    public void SetGamepadVibration(int index, float leftMotor, float rightMotor)
    {
        if ((uint)index >= Gamepad.MaxPads) return;
        XInput.TrySetVibration(index, leftMotor, rightMotor);
    }
}
