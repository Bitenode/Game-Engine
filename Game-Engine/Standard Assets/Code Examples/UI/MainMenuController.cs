#nullable enable
using System.Linq;
using System.Numerics;
using Game_Engine.Core.Input;
using Game_Engine.Core.Networking;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// Controls main-menu navigation — switches between the main menu and settings panel,
    /// loads a scene when Play is clicked, and connects as a multiplayer client when Join is clicked.
    /// Uses the Input API directly for click detection and RectTransform hit testing.
    /// Attach to the root Canvas GameObject.
    /// </summary>
    public sealed class MainMenuController : Behavior
    {
        [Persist] public string MainMenuGroupName { get; set; } = "Main Menu Group";
        [Persist] public string SettingsPanelName { get; set; } = "Settings Panel";
        [Persist] public string SettingsButtonName { get; set; } = "Settings Button";
        [Persist] public string BackButtonName { get; set; } = "Back Button";
        [Persist] public string PlayButtonName { get; set; } = "Play Button";
        [Persist] public string JoinButtonName { get; set; } = "Join Button";
        [Persist] public string QuitButtonName { get; set; } = "Quit Button";

        /// <summary>
        /// Play: load this scene immediately. Join: load after <see cref="NetworkManager.OnPlayerConnected"/> (handshake complete).
        /// Example: <c>Game</c> → <c>Scenes/Game.scene</c>.
        /// </summary>
        [Persist] public string PlaySceneName { get; set; } = "";

        /// <summary>Server hostname or IP for <see cref="NetworkManager.StartClient"/>.</summary>
        [Persist] public string JoinHost { get; set; } = "127.0.0.1";

        /// <summary>Server port for Join.</summary>
        [Persist] public int JoinPort { get; set; } = 7777;

        private GameObject? _mainMenuGroup;
        private GameObject? _settingsPanel;
        private RectTransform? _settingsBtnRT;
        private RectTransform? _backBtnRT;
        private RectTransform? _playBtnRT;
        private RectTransform? _joinBtnRT;
        private RectTransform? _quitBtnRT;
        private Canvas? _canvas;
        private bool _wasMouseDown;
        private bool _showingSettings;
        private string? _joinPendingScene;

        public override void Start()
        {
            var root = gameObject;
            if (root == null) return;

            _canvas = GetComponent<Canvas>();
            _mainMenuGroup = FindChild(root, MainMenuGroupName);
            _settingsPanel = FindChild(root, SettingsPanelName);

            var settingsBtnGO = FindChild(root, SettingsButtonName);
            var backBtnGO = FindChild(root, BackButtonName);
            var playBtnGO = FindChild(root, PlayButtonName);
            var joinBtnGO = FindChild(root, JoinButtonName);
            var quitBtnGO = FindChild(root, QuitButtonName);

            _settingsBtnRT = settingsBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _backBtnRT = backBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _playBtnRT = playBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _joinBtnRT = joinBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _quitBtnRT = quitBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();

            _showingSettings = false;
            SetGroupVisible(_mainMenuGroup, true);
            SetGroupVisible(_settingsPanel, false);
        }

        public override void OnDestroy()
        {
            NetworkManager.OnPlayerConnected -= OnJoinHandshakeComplete;
            base.OnDestroy();
        }

        /// <summary>After the client receives CONNECT_ACK, load the game scene so NetworkIdentity objects exist while the session is active.</summary>
        void OnJoinHandshakeComplete(NetworkPeer _)
        {
            NetworkManager.OnPlayerConnected -= OnJoinHandshakeComplete;
            if (string.IsNullOrWhiteSpace(_joinPendingScene))
                return;
            string name = _joinPendingScene.Trim();
            _joinPendingScene = null;
            SceneManager.LoadScene(name);
            Log.Info($"[MainMenuController] Join: loading '{name}' after connect.");
        }

        public override void Update()
        {
            if (_canvas == null) return;

            bool mouseIsDown = Input.Input.GetMouse(MouseButton.Left);
            bool clicked = mouseIsDown && !_wasMouseDown;
            _wasMouseDown = mouseIsDown;

            if (!clicked) return;

            var vp = Input.Input.ViewportSize;
            if (vp.X <= 0 || vp.Y <= 0) return;

            var canvasRect = _canvas.GetCanvasRect(vp.X, vp.Y);
            float scale = _canvas.GetScaleFactor(vp.X, vp.Y);

            var mousePos = Input.Input.MousePosition;
            var canvasPoint = new Vector2(mousePos.X / scale, (vp.Y - mousePos.Y) / scale);

            if (!_showingSettings)
            {
                if (_playBtnRT != null && _playBtnRT.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    if (!string.IsNullOrWhiteSpace(PlaySceneName))
                        SceneManager.LoadScene(PlaySceneName);
                    else
                        Log.Warning("[MainMenuController] Play button clicked but no PlaySceneName is set.");
                    return;
                }

                if (_joinBtnRT != null && _joinBtnRT.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    if (NetworkManager.IsActive)
                    {
                        Log.Warning("[MainMenuController] Join ignored — networking already active.");
                        return;
                    }

                    string host = string.IsNullOrWhiteSpace(JoinHost) ? "127.0.0.1" : JoinHost.Trim();
                    NetworkManager.OnPlayerConnected -= OnJoinHandshakeComplete;
                    _joinPendingScene = null;

                    if (!string.IsNullOrWhiteSpace(PlaySceneName))
                    {
                        _joinPendingScene = PlaySceneName.Trim();
                        NetworkManager.OnPlayerConnected += OnJoinHandshakeComplete;
                    }
                    else
                        Log.Warning("[MainMenuController] Join: set PlaySceneName to the gameplay scene (e.g. Game) so the client loads after connect.");

                    NetworkManager.StartClient(host, JoinPort);
                    return;
                }

                if (_settingsBtnRT != null && _settingsBtnRT.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    _showingSettings = true;
                    SetGroupVisible(_mainMenuGroup, false);
                    SetGroupVisible(_settingsPanel, true);
                    return;
                }

                if (_quitBtnRT != null && _quitBtnRT.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    Log.Info("[MainMenuController] Quit button clicked.");
#if !DEBUG
                    System.Environment.Exit(0);
#endif
                    return;
                }
            }
            else
            {
                if (_backBtnRT != null && _backBtnRT.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    _showingSettings = false;
                    SetGroupVisible(_mainMenuGroup, true);
                    SetGroupVisible(_settingsPanel, false);
                }
            }
        }

        private static void SetGroupVisible(GameObject? go, bool visible)
        {
            if (go == null) return;
            foreach (var b in go.Behaviors)
            {
                if (b is UIElement e)
                    e.Enabled = visible;
            }
            foreach (var child in go.Children)
                SetGroupVisible(child, visible);
        }

        private static GameObject? FindChild(GameObject? parent, string name)
        {
            if (parent == null) return null;
            foreach (var child in parent.Children)
            {
                if (child.Name == name) return child;
                var found = FindChild(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
