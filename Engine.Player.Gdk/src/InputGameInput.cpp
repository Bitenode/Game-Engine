#include "InputGameInput.h"

#include <GameInput.h>
#include <wrl/client.h>

#pragma comment(lib, "GameInput.lib")

using Microsoft::WRL::ComPtr;

namespace EnginePlayerGdk::Input
{
    static ComPtr<IGameInput> s_gameInput;
    static bool s_ready = false;

    bool Initialize()
    {
        if (s_ready) return true;
        const HRESULT hr = GameInputCreate(s_gameInput.GetAddressOf());
        if (FAILED(hr) || !s_gameInput)
            return false;
        s_ready = true;
        return true;
    }

    void Shutdown()
    {
        s_gameInput.Reset();
        s_ready = false;
    }

    static void MapButtons(const GameInputGamepadState& pad, PadState& out)
    {
        auto set = [&](GameInputGamepadButtons bit, PadButton name)
        {
            if ((pad.buttons & bit) != 0)
                out.buttons |= static_cast<uint32_t>(name);
        };

        set(GameInputGamepadA, PadButton::A);
        set(GameInputGamepadB, PadButton::B);
        set(GameInputGamepadX, PadButton::X);
        set(GameInputGamepadY, PadButton::Y);
        set(GameInputGamepadLeftShoulder, PadButton::LeftShoulder);
        set(GameInputGamepadRightShoulder, PadButton::RightShoulder);
        set(GameInputGamepadView, PadButton::View);
        set(GameInputGamepadMenu, PadButton::Menu);
        set(GameInputGamepadLeftThumbstick, PadButton::LeftThumb);
        set(GameInputGamepadRightThumbstick, PadButton::RightThumb);
        set(GameInputGamepadDPadUp, PadButton::DPadUp);
        set(GameInputGamepadDPadDown, PadButton::DPadDown);
        set(GameInputGamepadDPadLeft, PadButton::DPadLeft);
        set(GameInputGamepadDPadRight, PadButton::DPadRight);

        out.leftStickX = pad.leftThumbstickX;
        out.leftStickY = pad.leftThumbstickY;
        out.rightStickX = pad.rightThumbstickX;
        out.rightStickY = pad.rightThumbstickY;
        out.leftTrigger = pad.leftTrigger;
        out.rightTrigger = pad.rightTrigger;
    }

    bool Poll(PadState& outState)
    {
        outState = {};
        if (!Initialize() || !s_gameInput)
            return false;

        ComPtr<IGameInputReading> reading;
        const HRESULT hr = s_gameInput->GetCurrentReading(
            GameInputKindGamepad,
            nullptr,
            reading.GetAddressOf());
        if (FAILED(hr) || !reading)
            return false;

        GameInputGamepadState pad{};
        if (!reading->GetGamepadState(&pad))
            return false;

        MapButtons(pad, outState);
        return true;
    }
}
