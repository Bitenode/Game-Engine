using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Security.Authentication.Web.Core;
using Windows.Security.Credentials;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Core;
using Game_Engine.Core.Xbox;

namespace Engine.Player.XboxUwp;

/// <summary>
/// Xbox Live for the UWP player. Signs in the console or PC profile and asks Xbox Live for a token.
/// Game scripts keep calling <see cref="XboxLive.Current"/>; this type stays in the host, not Assets.dll.
/// </summary>
public sealed class UwpXboxLiveService : IXboxLiveService
{
    const string XboxProvider = "https://xsts.auth.xboxlive.com";
    const string XboxScope = "xboxlive.signin";

    readonly object _gate = new object();
    readonly SemaphoreSlim _signIn = new SemaphoreSlim(1, 1);
    string _msaAppId = "";
    bool _signedIn;
    string _userId;
    string _gamertag;
    string _status;

    public bool IsSignedIn { get { lock (_gate) return _signedIn; } }
    public string UserId { get { lock (_gate) return _userId; } }
    public string Gamertag { get { lock (_gate) return _gamertag; } }
    public string Status { get { lock (_gate) return _status; } }

    public static void Install()
    {
        try
        {
            var service = new UwpXboxLiveService();
            service._msaAppId = ReadMsaAppId();
            XboxLive.Current = service;
            service.BeginSilent();
            XboxPlayerLog.Write("Info", "[XboxLive] UWP sign-in started.");
        }
        catch (Exception ex)
        {
            XboxPlayerLog.Write("Error", "[XboxLive] Install failed: " + ex.Message);
        }
    }

    public void BeginSilent()
    {
        var _ = SignInCore(false);
    }

    public Task<bool> SignInAsync(CancellationToken cancellationToken = default)
        => SignInCore(true);

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _signedIn = false;
            _userId = null;
            _gamertag = null;
            _status = "Signed out on this device. The console account is unchanged.";
        }
        return Task.CompletedTask;
    }

    public Task<bool> UnlockAchievementAsync(string achievementId, CancellationToken cancellationToken = default)
    {
        XboxPlayerLog.Write("Info", "[XboxLive] UnlockAchievement '" + achievementId + "' is not wired in the UWP host.");
        return Task.FromResult(false);
    }

    async Task<bool> SignInCore(bool interactive)
    {
        await _signIn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!interactive && IsSignedIn)
                return true;
            SetStatus("Reaching Xbox Live…");
            return await RunOnUi(() => SignInOnUi(interactive)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetSignedOut("Couldn't reach Xbox Live. " + ex.Message);
            XboxPlayerLog.Write("Error", "[XboxLive] " + ex);
            return false;
        }
        finally
        {
            _signIn.Release();
        }
    }

    async Task<bool> SignInOnUi(bool interactive)
    {
        User user = await FindSignedInUser().ConfigureAwait(true);
        if (user == null && interactive)
            user = await PickUser().ConfigureAwait(true);
        if (user == null)
        {
            SetSignedOut(OnXbox()
                ? "No Xbox profile is signed in."
                : "Sign in with the Xbox profile on this PC.");
            return false;
        }

        string name = await ReadGamertag(user).ConfigureAwait(true);
        string id = user.NonRoamableId;
        TokenResult token = await TryXboxToken(interactive).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(token.Name))
            name = token.Name;
        if (!string.IsNullOrWhiteSpace(token.Id))
            id = token.Id;

        if (token.Ok || (OnXbox() && !string.IsNullOrWhiteSpace(name)))
        {
            string status = token.Ok
                ? "Signed in to Xbox Live."
                : "Xbox profile found. Live did not issue a token" + FormatError(token.Error);
            SetSignedIn(name, id, status);
            XboxPlayerLog.Write("Info", "[XboxLive] " + status + " tag=" + name);
            return true;
        }

        SetSignedOut(string.IsNullOrWhiteSpace(token.Error)
            ? "Couldn't reach Xbox Live."
            : "Couldn't reach Xbox Live. " + token.Error);
        XboxPlayerLog.Write("Info", "[XboxLive] " + Status);
        return false;
    }

    static async Task<User> FindSignedInUser()
    {
        var users = await User.FindAllAsync();
        User fallback = null;
        foreach (var user in users)
        {
            if (user == null) continue;
            if (user.AuthenticationStatus == UserAuthenticationStatus.LocallyAuthenticated)
                return user;
            fallback ??= user;
        }
        return fallback;
    }

    static async Task<User> PickUser()
    {
        try
        {
            var picker = new UserPicker();
            picker.AllowGuestAccounts = false;
            return await picker.PickSingleUserAsync();
        }
        catch (Exception ex)
        {
            XboxPlayerLog.Write("Info", "[XboxLive] User picker: " + ex.Message);
            return null;
        }
    }

    static async Task<string> ReadGamertag(User user)
    {
        string account = await Prop(user, KnownUserProperties.AccountName).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(account)) return account;
        string display = await Prop(user, KnownUserProperties.DisplayName).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(display)) return display;
        string first = await Prop(user, KnownUserProperties.FirstName).ConfigureAwait(true);
        return string.IsNullOrWhiteSpace(first) ? "Xbox player" : first;
    }

    static async Task<string> Prop(User user, string key)
    {
        try
        {
            object value = await user.GetPropertyAsync(key);
            return value as string;
        }
        catch
        {
            return null;
        }
    }

    async Task<TokenResult> TryXboxToken(bool interactive)
    {
        try
        {
            WebAccountProvider provider = await WebAuthenticationCoreManager
                .FindAccountProviderAsync(XboxProvider, "user.auth")
                .AsTask()
                .ConfigureAwait(true);
            if (provider == null)
            {
                provider = await WebAuthenticationCoreManager
                    .FindAccountProviderAsync(XboxProvider)
                    .AsTask()
                    .ConfigureAwait(true);
            }
            if (provider == null)
                return new TokenResult { Error = "Xbox Live sign-in is not available to this package." };

            string client = _msaAppId;
            var request = string.IsNullOrWhiteSpace(client)
                ? new WebTokenRequest(provider, XboxScope)
                : new WebTokenRequest(provider, XboxScope, client);

            WebTokenRequestResult result = await RequestToken(request, silent: true).ConfigureAwait(true);
            if (interactive && result != null && result.ResponseStatus == WebTokenRequestStatus.UserInteractionRequired)
                result = await RequestToken(request, silent: false).ConfigureAwait(true);

            if (result == null)
                return new TokenResult { Error = "No token response." };
            if (result.ResponseStatus != WebTokenRequestStatus.Success || result.ResponseData == null || result.ResponseData.Count == 0)
            {
                string why = result.ResponseStatus.ToString();
                if (result.ResponseError != null && !string.IsNullOrWhiteSpace(result.ResponseError.ErrorMessage))
                    why = result.ResponseError.ErrorMessage;
                return new TokenResult { Error = why };
            }

            var data = result.ResponseData[0];
            var account = data?.WebAccount;
            return new TokenResult
            {
                Ok = true,
                Name = account?.UserName,
                Id = account?.Id
            };
        }
        catch (Exception ex)
        {
            return new TokenResult { Error = ex.Message };
        }
    }

    static Task<WebTokenRequestResult> RequestToken(WebTokenRequest request, bool silent)
    {
        return silent
            ? WebAuthenticationCoreManager.GetTokenSilentlyAsync(request).AsTask()
            : WebAuthenticationCoreManager.RequestTokenAsync(request).AsTask();
    }

    static bool OnXbox()
    {
        try
        {
            return string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Xbox", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static string ReadMsaAppId()
    {
        try
        {
            string path = Path.Combine(Package.Current.InstalledLocation.Path, "Data", "build.json");
            if (!File.Exists(path)) return "";
            string text = File.ReadAllText(path);
            const string key = "\"gcMsaAppId\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int colon = text.IndexOf(':', i);
            int q1 = text.IndexOf('"', colon + 1);
            int q2 = q1 < 0 ? -1 : text.IndexOf('"', q1 + 1);
            if (q1 < 0 || q2 < 0) return "";
            return text.Substring(q1 + 1, q2 - q1 - 1).Trim();
        }
        catch
        {
            return "";
        }
    }

    static string FormatError(string error)
        => string.IsNullOrWhiteSpace(error) ? "." : ": " + error;

    void SetStatus(string status)
    {
        lock (_gate) _status = status;
    }

    void SetSignedIn(string name, string id, string status)
    {
        lock (_gate)
        {
            _signedIn = true;
            _gamertag = string.IsNullOrWhiteSpace(name) ? "Xbox player" : name;
            _userId = id;
            _status = status;
        }
    }

    void SetSignedOut(string status)
    {
        lock (_gate)
        {
            _signedIn = false;
            _gamertag = null;
            _userId = null;
            _status = status;
        }
    }

    static Task<T> RunOnUi<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>();
        CoreDispatcher dispatcher;
        try
        {
            dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
            return tcs.Task;
        }

        var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            work().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    tcs.TrySetException(t.Exception.InnerExceptions);
                else if (t.IsCanceled)
                    tcs.TrySetCanceled();
                else
                    tcs.TrySetResult(t.Result);
            }, TaskScheduler.Default);
        });
        return tcs.Task;
    }

    struct TokenResult
    {
        public bool Ok;
        public string Name;
        public string Id;
        public string Error;
    }
}
