#nullable enable
namespace Game_Engine.Core.Input;

/// <summary>Device-layer poller. Keyboard/mouse still feed through Avalonia; this covers OS pads and rumble.</summary>
public interface IInputBackend
{
    void Poll();
    GamepadState GetGamepadState(int index);
    int ConnectedGamepadCount { get; }
    void SetGamepadVibration(int index, float leftMotor, float rightMotor);
}

public static class InputBackends
{
    public static IInputBackend Current { get; private set; } = CreateDefault();

    public static IInputBackend CreateDefault()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsGamepadBackend();
        return new SdlGamepadBackend();
    }

    public static void Set(IInputBackend backend) => Current = backend ?? CreateDefault();
}
