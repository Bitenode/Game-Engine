#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Game_Engine.Core.Xbox;

/// <summary>
/// Engine-facing Xbox Live surface. Game scripts call only this API.
/// The Avalonia MSIX player uses <see cref="NullXboxLiveService"/>.
/// The GDK native host implements XSAPI behind this contract later.
/// Do not ship xbox-live-api (XSAPI) inside Assets.dll — it is a GDK dependency.
/// </summary>
public interface IXboxLiveService
{
    /// <summary>True when a user is signed in for Xbox Live features.</summary>
    bool IsSignedIn { get; }

    /// <summary>Xbox user id / XUID string when signed in; otherwise null.</summary>
    string? UserId { get; }

    /// <summary>Gamertag when available; otherwise null.</summary>
    string? Gamertag { get; }

    /// <summary>Short line for the menu. Null when there is nothing to report.</summary>
    string? Status { get; }

    /// <summary>Interactive or silent sign-in. Returns true on success.</summary>
    Task<bool> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Sign out locally. No-op when not signed in.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlock (or progress) an achievement by partner-defined id.
    /// Returns false when Live is unavailable or the call fails.
    /// </summary>
    Task<bool> UnlockAchievementAsync(string achievementId, CancellationToken cancellationToken = default);
}
