#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Game_Engine.Core.Input;

/// <summary>
/// Optional Microsoft GameInput runtime. When GameInput.dll is present the backend
/// initializes the session; pad state still comes from XInput on Windows because the
/// COM reading surface is not required for Xbox-layout pads.
/// </summary>
public sealed class GameInputBackend : IInputBackend
{
    readonly XInputBackend _xinput = new();
    static bool s_tried;
    static bool s_available;

    public int ConnectedGamepadCount => _xinput.ConnectedGamepadCount;

    public GamepadState GetGamepadState(int index) => _xinput.GetGamepadState(index);

    public void SetGamepadVibration(int index, float leftMotor, float rightMotor)
        => _xinput.SetGamepadVibration(index, leftMotor, rightMotor);

    public void Poll()
    {
        EnsureRuntime();
        _xinput.Poll();
    }

    internal void NoteXInputActive(int count)
    {
        EnsureRuntime();
        _ = count;
    }

    static void EnsureRuntime()
    {
        if (s_tried || !OperatingSystem.IsWindows()) return;
        s_tried = true;
        try
        {
            int hr = GameInputCreate(out var gameInput);
            if (hr >= 0 && gameInput != IntPtr.Zero)
            {
                s_available = true;
                Marshal.Release(gameInput);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch { }
    }

    public static bool RuntimeAvailable => s_available;

    [DllImport("GameInput.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int GameInputCreate(out IntPtr gameInput);
}
