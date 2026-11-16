#include "XboxLiveXsapi.h"

#include <cstdio>
#include <memory>

// XSAPI + XGameRuntime headers ship with the Microsoft GDK.
#include <xsapi-c/services_c.h>
#include <XGameRuntime.h>

namespace EnginePlayerGdk::Live
{
    static bool s_runtimeReady = false;
    static bool s_xsapiReady = false;
    static std::string s_scid;
    static UserInfo s_user;
    static XblContextHandle s_xblContext = nullptr;
    static XUserHandle s_userHandle = nullptr;

    bool Initialize(const char* scid)
    {
        if (!scid || !*scid)
            return false;

        s_scid = scid;

        HRESULT hr = XGameRuntimeInitialize();
        if (FAILED(hr))
        {
            std::printf("[XboxLive] XGameRuntimeInitialize failed: 0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }
        s_runtimeReady = true;

        XblInitArgs args{};
        args.scid = s_scid.c_str();
        hr = XblInitialize(&args);
        if (FAILED(hr))
        {
            std::printf("[XboxLive] XblInitialize failed: 0x%08lX (check SCID / sandbox)\n",
                static_cast<unsigned long>(hr));
            return false;
        }

        s_xsapiReady = true;
        std::printf("[XboxLive] XSAPI initialized (SCID=%s)\n", s_scid.c_str());
        return true;
    }

    void Shutdown()
    {
        if (s_xblContext)
        {
            XblContextCloseHandle(s_xblContext);
            s_xblContext = nullptr;
        }
        if (s_userHandle)
        {
            XUserCloseHandle(s_userHandle);
            s_userHandle = nullptr;
        }
        s_user = {};
        if (s_xsapiReady)
        {
            XAsyncBlock cleanupAsync{};
            HRESULT hr = XblCleanupAsync(&cleanupAsync);
            if (SUCCEEDED(hr))
                XAsyncGetStatus(&cleanupAsync, true);
            s_xsapiReady = false;
        }
        if (s_runtimeReady)
        {
            XGameRuntimeUninitialize();
            s_runtimeReady = false;
        }
    }

    bool SignInInteractive()
    {
        if (!s_xsapiReady)
            return false;

        XAsyncBlock async{};
        HRESULT hr = XUserAddAsync(XUserAddOptions::None, &async);
        if (FAILED(hr))
        {
            std::printf("[XboxLive] XUserAddAsync failed: 0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }

        hr = XAsyncGetStatus(&async, true);
        if (FAILED(hr))
        {
            std::printf("[XboxLive] Sign-in async failed: 0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }

        if (s_userHandle)
        {
            XUserCloseHandle(s_userHandle);
            s_userHandle = nullptr;
        }

        hr = XUserAddResult(&async, &s_userHandle);
        if (FAILED(hr) || !s_userHandle)
        {
            std::printf("[XboxLive] XUserAddResult failed: 0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }

        if (s_xblContext)
        {
            XblContextCloseHandle(s_xblContext);
            s_xblContext = nullptr;
        }

        hr = XblContextCreateHandle(s_userHandle, &s_xblContext);
        if (FAILED(hr) || !s_xblContext)
        {
            std::printf("[XboxLive] XblContextCreateHandle failed: 0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }

        char gamertag[256] = {};
        hr = XUserGetGamertag(s_userHandle, XUserGamertagComponent::Classic, sizeof(gamertag), gamertag, nullptr);
        uint64_t xuid = 0;
        XUserGetId(s_userHandle, &xuid);

        s_user.signedIn = true;
        s_user.gamertag = SUCCEEDED(hr) ? gamertag : "";
        s_user.userId = std::to_string(xuid);
        std::printf("[XboxLive] Signed in as %s (xuid=%s)\n",
            s_user.gamertag.c_str(), s_user.userId.c_str());
        return true;
    }

    UserInfo CurrentUser()
    {
        return s_user;
    }

    bool UnlockAchievement(const char* achievementId)
    {
        if (!achievementId || !*achievementId)
            return false;
        if (!s_user.signedIn || !s_xblContext)
        {
            std::printf("[XboxLive] UnlockAchievement requires sign-in.\n");
            return false;
        }

        // Title-managed achievements use XblAchievementsUpdateAchievementAsync.
        // Exact progress semantics depend on Partner Center achievement config.
        XAsyncBlock async{};
        uint64_t xuid = 0;
        XUserGetId(s_userHandle, &xuid);

        HRESULT hr = XblAchievementsUpdateAchievementAsync(
            s_xblContext,
            xuid,
            achievementId,
            100,
            &async);
        if (FAILED(hr))
        {
            std::printf("[XboxLive] UpdateAchievementAsync failed: 0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }

        hr = XAsyncGetStatus(&async, true);
        if (FAILED(hr))
        {
            std::printf("[XboxLive] Achievement unlock failed: 0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }

        std::printf("[XboxLive] Achievement unlocked: %s\n", achievementId);
        return true;
    }
}
