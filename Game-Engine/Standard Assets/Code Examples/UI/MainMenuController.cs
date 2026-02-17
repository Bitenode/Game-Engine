#nullable enable
using System.Linq;
using System.Numerics;
using Game_Engine.Core.Input;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// Controls main-menu navigation — switches between the main menu and settings panel,
    /// and loads a scene when the Play button is clicked.
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
        [Persist] public string QuitButtonName { get; set; } = "Quit Button";

        /// <summary>
        /// The name of the scene to load when the Play button is clicked
        /// (e.g. "Game" will load Scenes/Game.scene).
        /// </summary>
        [Persist] public string PlaySceneName { get; set; } = "";

        private GameObject? _mainMenuGroup;
        private GameObject? _settingsPanel;
        private RectTransform? _settingsBtnRT;
        private RectTransform? _backBtnRT;
        private RectTransform? _playBtnRT;
        private RectTransform? _quitBtnRT;
        private Canvas? _canvas;
        private bool _wasMouseDown;
        private bool _showingSettings;

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
            var quitBtnGO = FindChild(root, QuitButtonName);

            _settingsBtnRT = settingsBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _backBtnRT = backBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _playBtnRT = playBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _quitBtnRT = quitBtnGO?.Behaviors.OfType<RectTransform>().FirstOrDefault();

            _showingSettings = false;
            SetGroupVisible(_mainMenuGroup, true);
            SetGroupVisible(_settingsPanel, false);
        }

        public override void Update()
        {
            if (_canvas == null) return;

            // Derive click edge from the held state (always reliable)
            bool mouseIsDown = Input.Input.GetMouse(MouseButton.Left);
            bool clicked = mouseIsDown && !_wasMouseDown;
            _wasMouseDown = mouseIsDown;

            if (!clicked) return;

            // Get viewport size and mouse position (both in DIP space)
            var vp = Input.Input.ViewportSize;
            if (vp.X <= 0 || vp.Y <= 0) return;

            var canvasRect = _canvas.GetCanvasRect(vp.X, vp.Y);
            float scale = _canvas.GetScaleFactor(vp.X, vp.Y);

            // Convert mouse from top-left DIP to bottom-left canvas coordinates
            var mousePos = Input.Input.MousePosition;
            var canvasPoint = new Vector2(mousePos.X / scale, (vp.Y - mousePos.Y) / scale);

            if (!_showingSettings)
            {
                // Play button — load the target scene
                if (_playBtnRT != null && _playBtnRT.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    if (!string.IsNullOrWhiteSpace(PlaySceneName))
                    {
                        SceneManager.LoadScene(PlaySceneName);
                    }
                    else
                    {
                        Log.Warning("[MainMenuController] Play button clicked but no PlaySceneName is set.");
                    }
                    return;
                }

                // Settings button — open settings panel
                if (_settingsBtnRT != null && _settingsBtnRT.ContainsScreenPoint(canvasPoint, in canvasRect))
                {
                    _showingSettings = true;
                    SetGroupVisible(_mainMenuGroup, false);
                    SetGroupVisible(_settingsPanel, true);
                    return;
                }

                // Quit button — stop play mode (in editor) or exit application
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

        /// <summary>
        /// Recursively enable or disable all UIElement behaviors in a GameObject subtree.
        /// </summary>
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

        /// <summary>Recursively search for a child GameObject by name.</summary>
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
