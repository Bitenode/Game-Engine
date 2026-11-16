#include "AudioXAudio2.h"
#include "InputGameInput.h"
#include "XboxLiveXsapi.h"

#include <cstdio>
#include <cstring>
#include <string>

#include <Windows.h>

static std::string ReadArgValue(int argc, char** argv, const char* key)
{
    const std::string prefix = std::string(key) + "=";
    for (int i = 1; i < argc; ++i)
    {
        if (std::strncmp(argv[i], prefix.c_str(), prefix.size()) == 0)
            return argv[i] + prefix.size();
        if (std::strcmp(argv[i], key) == 0 && i + 1 < argc)
            return argv[i + 1];
    }
    return {};
}

int main(int argc, char** argv)
{
    std::printf("Engine.Player.Gdk — GDK bootstrap (XAudio2 / GameInput / XSAPI)\n");

    const std::string scid = ReadArgValue(argc, argv, "--scid");
    const std::string achievement = ReadArgValue(argc, argv, "--unlock-achievement");
    const bool skipLive = ReadArgValue(argc, argv, "--skip-live") == "1"
        || ReadArgValue(argc, argv, "--skip-live") == "true";
    const bool playTone = ReadArgValue(argc, argv, "--test-tone") != "0";

    if (!EnginePlayerGdk::Audio::Initialize())
        std::printf("[Main] XAudio2 init failed (continuing).\n");
    else if (playTone)
        EnginePlayerGdk::Audio::PlayTestTone(0.2f, 440.f);

    if (!EnginePlayerGdk::Input::Initialize())
        std::printf("[Main] GameInput init failed (continuing).\n");

    if (!skipLive)
    {
        if (scid.empty())
        {
            std::printf("[Main] No --scid=... provided. Xbox Live left uninitialized.\n");
            std::printf("       Pass SCID from Partner Center or Build Settings (Xbox GDK).\n");
        }
        else if (!EnginePlayerGdk::Live::Initialize(scid.c_str()))
        {
            std::printf("[Main] Xbox Live init failed.\n");
        }
        else
        {
            EnginePlayerGdk::Live::SignInInteractive();
            if (!achievement.empty())
                EnginePlayerGdk::Live::UnlockAchievement(achievement.c_str());
        }
    }

    // Short poll loop so pads can be verified from a console / local GDK run.
    for (int frame = 0; frame < 120; ++frame)
    {
        EnginePlayerGdk::Input::PadState pad{};
        if (EnginePlayerGdk::Input::Poll(pad) && pad.buttons != 0)
        {
            std::printf("[Input] buttons=0x%08X sticks=(%.2f,%.2f) triggers=(%.2f,%.2f)\n",
                pad.buttons, pad.leftStickX, pad.leftStickY, pad.leftTrigger, pad.rightTrigger);
        }
        Sleep(16);
    }

    EnginePlayerGdk::Live::Shutdown();
    EnginePlayerGdk::Input::Shutdown();
    EnginePlayerGdk::Audio::Shutdown();
    std::printf("[Main] Exit.\n");
    return 0;
}
