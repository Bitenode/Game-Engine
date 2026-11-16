#nullable enable
namespace Game_Engine.Core.Xbox;

/// <summary>
/// Global access point for Xbox Live. Defaults to <see cref="NullXboxLiveService"/>.
/// GDK or future interop can replace <see cref="Current"/> at startup.
/// </summary>
public static class XboxLive
{
    private static IXboxLiveService s_current = new NullXboxLiveService();

    public static IXboxLiveService Current
    {
        get => s_current;
        set => s_current = value ?? new NullXboxLiveService();
    }

    /// <summary>Restore the no-op service (tests / player reset).</summary>
    public static void ResetToNull() => s_current = new NullXboxLiveService();
}
