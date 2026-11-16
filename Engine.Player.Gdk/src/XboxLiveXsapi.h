#pragma once

#include <string>

namespace EnginePlayerGdk::Live
{
    struct UserInfo
    {
        bool signedIn = false;
        std::string userId;
        std::string gamertag;
    };

    /// <summary>Initialize XSAPI against the title configured in MicrosoftGame.config / sandbox.</summary>
    bool Initialize(const char* scid);

    void Shutdown();

    /// <summary>Interactive sign-in. Populates current user on success.</summary>
    bool SignInInteractive();

    UserInfo CurrentUser();

    /// <summary>Unlock an achievement by partner id. Requires signed-in user.</summary>
    bool UnlockAchievement(const char* achievementId);
}
