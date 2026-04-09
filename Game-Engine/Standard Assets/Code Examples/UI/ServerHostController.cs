#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Game_Engine.Core.Input;
using Game_Engine.Core.Networking;
using Game_Engine.Core.SaveSystem;

namespace Game_Engine.Core.Component.UI
{
    /// <summary>
    /// Host UI for a multiplayer game server — start/stop listening, optional game scene + save slot,
    /// and a rolling log console (mirrors <see cref="Log"/> while active).
    /// </summary>
    public sealed class ServerHostController : Behavior
    {
        /// <summary>Applied after <see cref="SceneManager"/> finishes loading a game scene (when a save slot was requested).</summary>
        static int? _pendingSaveSlotAfterSceneLoad;

        [Persist] public int Port { get; set; } = 7777;
        [Persist] public bool AutoStartOnPlay { get; set; }

        [Persist] public string StartButtonName { get; set; } = "Start Server Button";
        [Persist] public string StopButtonName { get; set; } = "Stop Server Button";
        [Persist] public string StatusTextName { get; set; } = "Status Text";
        [Persist] public string SceneNameInputName { get; set; } = "Scene Name Input";
        [Persist] public string SaveSlotInputName { get; set; } = "Save Slot Input";
        [Persist] public string ConsoleTextName { get; set; } = "Console Text";

        [Persist] public int MaxConsoleLines { get; set; } = 40;

        static ServerHostController()
        {
            SceneManager.SceneLoaded += OnGlobalSceneLoaded;
        }

        private Canvas? _canvas;
        private RectTransform? _startRt;
        private RectTransform? _stopRt;
        private UIText? _statusText;
        private UIText? _consoleText;
        private UIInputField? _sceneInput;
        private UIInputField? _saveSlotInput;
        private bool _wasMouseDown;

        private readonly List<string> _consoleLines = new();
        private EventHandler<LogItem>? _logHandler;

        public override void Start()
        {
            var root = gameObject;
            if (root == null) return;

            _canvas = GetComponent<Canvas>();

            var startGo = FindChild(root, StartButtonName);
            var stopGo = FindChild(root, StopButtonName);
            var statusGo = FindChild(root, StatusTextName);
            var consoleGo = FindChild(root, ConsoleTextName);
            var sceneGo = FindChild(root, SceneNameInputName);
            var slotGo = FindChild(root, SaveSlotInputName);

            _startRt = startGo?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _stopRt = stopGo?.Behaviors.OfType<RectTransform>().FirstOrDefault();
            _statusText = statusGo?.Behaviors.OfType<UIText>().FirstOrDefault();
            _consoleText = consoleGo?.Behaviors.OfType<UIText>().FirstOrDefault();
            _sceneInput = sceneGo?.Behaviors.OfType<UIInputField>().FirstOrDefault();
            _saveSlotInput = slotGo?.Behaviors.OfType<UIInputField>().FirstOrDefault();

            _logHandler = (_, item) => AppendConsole(item.ToString());
            Log.Logged += _logHandler;

            NetworkManager.OnPlayerConnected += OnPlayerConnected;
            NetworkManager.OnPlayerDisconnected += OnPlayerDisconnected;

            AppendConsole("[Server] Console ready. Set game scene name and/or save slot, then Start Server.");

            if (AutoStartOnPlay && !NetworkManager.IsActive)
                TryStartServer();

            RefreshStatus();
        }

        public override void Update()
        {
            if (_canvas == null) return;

            bool mouseIsDown = Input.Input.GetMouse(MouseButton.Left);
            bool clicked = mouseIsDown && !_wasMouseDown;
            _wasMouseDown = mouseIsDown;

            RefreshStatus();

            if (!clicked) return;

            var vp = Input.Input.ViewportSize;
            if (vp.X <= 0 || vp.Y <= 0) return;

            var canvasRect = _canvas.GetCanvasRect(vp.X, vp.Y);
            float scale = _canvas.GetScaleFactor(vp.X, vp.Y);
            var mousePos = Input.Input.MousePosition;
            var canvasPoint = new Vector2(mousePos.X / scale, (vp.Y - mousePos.Y) / scale);

            if (_startRt != null && _startRt.ContainsScreenPoint(canvasPoint, in canvasRect))
            {
                if (!NetworkManager.IsActive)
                    TryStartServer();
                RefreshStatus();
                return;
            }

            if (_stopRt != null && _stopRt.ContainsScreenPoint(canvasPoint, in canvasRect))
            {
                TryStopServer();
                RefreshStatus();
            }
        }

        public override void OnDestroy()
        {
            NetworkManager.OnPlayerConnected -= OnPlayerConnected;
            NetworkManager.OnPlayerDisconnected -= OnPlayerDisconnected;

            if (_logHandler != null)
                Log.Logged -= _logHandler;

            if (NetworkManager.IsServer)
                NetworkManager.Stop();
        }

        private void TryStartServer()
        {
            NetworkManager.StartServer(Port);

            var scene = _sceneInput?.Text?.Trim() ?? "";
            var slotStr = _saveSlotInput?.Text?.Trim() ?? "";
            int? slot = int.TryParse(slotStr, out var si) ? si : null;

            if (!string.IsNullOrEmpty(scene))
            {
                _pendingSaveSlotAfterSceneLoad = slot;
                SceneManager.LoadScene(scene);
                Log.Info(slot.HasValue
                    ? $"[ServerHost] Game scene '{scene}' queued; will apply save slot {slot.Value} after load."
                    : $"[ServerHost] Game scene '{scene}' queued (no save slot).");
            }
            else if (slot.HasValue)
            {
                if (SaveManager.Load(slot.Value))
                    Log.Success($"[ServerHost] Applied save slot {slot.Value} in the current scene.");
                else
                    Log.Warning($"[ServerHost] Could not load save slot {slot.Value}.");
            }
        }

        private static void OnGlobalSceneLoaded(string sceneName)
        {
            if (_pendingSaveSlotAfterSceneLoad is not int slot)
                return;

            _pendingSaveSlotAfterSceneLoad = null;

            if (SaveManager.Load(slot))
                Log.Success($"[ServerHost] Applied save slot {slot} after loading scene '{sceneName}'.");
            else
                Log.Warning($"[ServerHost] Could not load save slot {slot} after scene '{sceneName}'.");
        }

        private void TryStopServer()
        {
            _pendingSaveSlotAfterSceneLoad = null;
            if (NetworkManager.IsActive)
                NetworkManager.Stop();
        }

        private void OnPlayerConnected(NetworkPeer peer)
        {
            Log.Info($"[Server] Peer {peer.PeerId} connected.");
        }

        private void OnPlayerDisconnected(NetworkPeer peer, string reason)
        {
            Log.Info($"[Server] Peer {peer.PeerId} disconnected ({reason}).");
        }

        private void AppendConsole(string line)
        {
            if (_consoleText == null) return;
            _consoleLines.Add(line);
            int max = Math.Max(8, MaxConsoleLines);
            while (_consoleLines.Count > max)
                _consoleLines.RemoveAt(0);
            _consoleText.Text = string.Join("\n", _consoleLines);
        }

        private void RefreshStatus()
        {
            if (_statusText == null) return;

            if (!NetworkManager.IsActive)
            {
                _statusText.Text = "Stopped - click Start to listen.";
                return;
            }

            int peers = NetworkManager.Transport?.Peers.Count ?? 0;
            int listenPort = NetworkManager.Transport?.LocalPort ?? Port;
            _statusText.Text = NetworkManager.IsServer
                ? $"Listening on UDP {listenPort} - {peers} peer(s) connected."
                : "Networking active (not server).";
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
