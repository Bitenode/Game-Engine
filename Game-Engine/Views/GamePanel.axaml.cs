using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Game_Engine.Core;
using Game_Engine.Core.Audio;
using Game_Engine.Core.Extensibility;
using Game_Engine;

namespace Game_Engine.Views;

public partial class GamePanel : UserControl
{
    public enum GameState { Stopped, Playing, Paused }

    private sealed record AspectPreset(string Label, double? Ratio)
    {
        public override string ToString() => Label;
    }

    private static readonly AspectPreset[] s_aspectPresets =
    {
        new("Free", null),
        new("16:9", 16.0 / 9.0),
        new("16:10", 16.0 / 10.0),
        new("4:3", 4.0 / 3.0),
    };

    public static readonly StyledProperty<GameState> StateProperty =
        AvaloniaProperty.Register<GamePanel, GameState>(nameof(State), GameState.Stopped);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _game ??= this.FindControl<GameView>("Game");
        ForwardState();
        if (MuteBtn != null)
            MuteBtn.IsChecked = AudioMixer.Master.Muted;
        ApplyAspectRatio();
    }

    private GameView? _game;

    public GameState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    static GamePanel()
    {
        StateProperty.Changed.AddClassHandler<GamePanel>((x, _) =>
        {
            x.LogState(x.State);
            x.UpdateButtons();
            x.ForwardState();
        });
    }

    public GamePanel()
    {
        InitializeComponent();

        if (AspectCombo != null)
        {
            AspectCombo.ItemsSource = s_aspectPresets;
            AspectCombo.SelectedIndex = 0;
            AspectCombo.SelectionChanged += (_, _) => ApplyAspectRatio();
        }

        if (ViewportHost != null)
            ViewportHost.SizeChanged += (_, _) => ApplyAspectRatio();

        UpdateButtons();
    }

    void ForwardState()
    {
        if (_game != null) _game.State = State;
    }

    private void UpdateButtons()
    {
        // Pause only while Playing. Stop and Step when Playing or Paused.
        if (PauseBtn != null) PauseBtn.IsEnabled = State == GameState.Playing;
        if (StopBtn != null) StopBtn.IsEnabled = State != GameState.Stopped;
        if (StepBtn != null) StepBtn.IsEnabled = State != GameState.Stopped;
    }

    private void ApplyAspectRatio()
    {
        var game = _game ?? this.FindControl<GameView>("Game");
        if (game == null || ViewportHost == null) return;

        var preset = AspectCombo?.SelectedItem as AspectPreset;
        if (preset?.Ratio is not double targetRatio)
        {
            game.HorizontalAlignment = HorizontalAlignment.Stretch;
            game.VerticalAlignment = VerticalAlignment.Stretch;
            game.Width = double.NaN;
            game.Height = double.NaN;
            return;
        }

        double hostW = ViewportHost.Bounds.Width;
        double hostH = ViewportHost.Bounds.Height;
        if (hostW <= 1 || hostH <= 1) return;

        double hostRatio = hostW / hostH;
        double w, h;
        if (hostRatio > targetRatio)
        {
            h = hostH;
            w = Math.Floor(h * targetRatio);
        }
        else
        {
            w = hostW;
            h = Math.Floor(w / targetRatio);
        }

        game.HorizontalAlignment = HorizontalAlignment.Center;
        game.VerticalAlignment = VerticalAlignment.Center;
        game.Width = Math.Max(1, w);
        game.Height = Math.Max(1, h);
    }

    private void LogState(GameState state)
    {
        switch (state)
        {
            case GameState.Playing: Core.Log.Success("Game: Play"); break;
            case GameState.Paused: Core.Log.Info("Game: Pause"); break;
            case GameState.Stopped: Core.Log.Warning("Game: Stop"); break;
        }
    }

    // Transitions:
    // - Play: always allowed (starts or resumes)
    // - Pause: only if currently Playing
    // - Stop: only if not already Stopped (and implicitly "unpauses")
    public void RequestPlay() => OnPlayClicked(null, new RoutedEventArgs());

    private async void OnPlayClicked(object? s, RoutedEventArgs e)
    {
        bool fromStop = State == GameState.Stopped;
        if (fromStop || ScriptCompiler.AreEditorScriptsStale())
        {
            Core.Log.Info(fromStop
                ? "Compiling project scripts for Play…"
                : "Scripts changed — compiling before Play…");
            var (ok, files, types, error) = await ScriptEditorWindow.TryCompileAllProjectScriptsAsync();
            if (!ok)
            {
                Core.Log.Error($"Script compile before Play failed: {error}");
                await ShowCompileFailedDialog(error);
                return;
            }
            if (files > 0)
            {
                ExtensionService.RefreshForCurrentProject();
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
                    && life.MainWindow is MainWindow mw)
                {
                    mw.RefreshProjectUI();
                    mw.RebuildExtensionMenus();
                }
                Core.Log.Success($"Project scripts compiled ({files} files, {types} behavior types).");
                ScriptTypeReload.NeedsFreshPlay = true;
            }
        }
        if (fromStop)
            ScriptTypeReload.NeedsFreshPlay = true;
        ScriptTypeReload.EnsureLatestLoaded();
        State = GameState.Playing;
    }

    static async Task ShowCompileFailedDialog(string? error)
    {
        var text = string.IsNullOrWhiteSpace(error)
            ? "Script compile failed."
            : error;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
            && life.MainWindow is MainWindow mw)
        {
            await mw.ShowCompileError(text);
        }
    }

    private void OnPauseClicked(object? s, RoutedEventArgs e)
    {
        if (State == GameState.Playing)
            State = GameState.Paused;
    }

    private void OnStopClicked(object? s, RoutedEventArgs e)
    {
        if (State != GameState.Stopped)
            State = GameState.Stopped; // this "unpauses" by leaving Paused state
    }

    private void OnStepClicked(object? s, RoutedEventArgs e)
    {
        if (State == GameState.Stopped) return;
        if (State == GameState.Playing)
            State = GameState.Paused;
        (_game ?? this.FindControl<GameView>("Game"))?.StepOneFrame();
    }

    private void OnMuteClicked(object? s, RoutedEventArgs e)
    {
        AudioMixer.Master.Muted = MuteBtn?.IsChecked == true;
    }
}
