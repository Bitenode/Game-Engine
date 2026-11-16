#pragma once

#include <cstdint>

namespace EnginePlayerGdk::Input
{
    /// <summary>Matches the engine's Xbox-layout digital names (see Game_Engine.Core.Input.Gamepad).</summary>
    enum class PadButton : uint32_t
    {
        A = 1u << 0,
        B = 1u << 1,
        X = 1u << 2,
        Y = 1u << 3,
        LeftShoulder = 1u << 4,
        RightShoulder = 1u << 5,
        View = 1u << 6,
        Menu = 1u << 7,
        LeftThumb = 1u << 8,
        RightThumb = 1u << 9,
        DPadUp = 1u << 10,
        DPadDown = 1u << 11,
        DPadLeft = 1u << 12,
        DPadRight = 1u << 13,
    };

    struct PadState
    {
        uint32_t buttons = 0;
        float leftStickX = 0.f;
        float leftStickY = 0.f;
        float rightStickX = 0.f;
        float rightStickY = 0.f;
        float leftTrigger = 0.f;
        float rightTrigger = 0.f;
    };

    bool Initialize();
    void Shutdown();
    bool Poll(PadState& outState);
}
