#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Game_Engine.Core.Xbox;

/// <summary>
/// Default Xbox Live implementation for editor and Avalonia player builds.
/// Logs and returns not-signed-in; does not call XSAPI.
/// </summary>
public sealed class NullXboxLiveService : IXboxLiveService
{
    public bool IsSignedIn => false;
    public string? UserId => null;
    public string? Gamertag => null;
    public string? Status => null;

    public Task<bool> SignInAsync(CancellationToken cancellationToken = default)
    {
        Log.Info("[XboxLive] Sign-in is unavailable in this build (NullXboxLiveService). Use the GDK host for XSAPI.");
        return Task.FromResult(false);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> UnlockAchievementAsync(string achievementId, CancellationToken cancellationToken = default)
    {
        Log.Info($"[XboxLive] UnlockAchievement('{achievementId}') skipped — NullXboxLiveService.");
        return Task.FromResult(false);
    }
}
