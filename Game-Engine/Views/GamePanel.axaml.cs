using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Game_Engine.Views;

public partial class GamePanel : UserControl
{
    public enum GameState { Stopped, Playing, Paused }

    public static readonly StyledProperty<GameState> StateProperty =
        AvaloniaProperty.Register<GamePanel, GameState>(nameof(State), GameState.Stopped);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _game ??= this.FindControl<GameView>("Game");
        ForwardState();
    }

    private GameView _game;

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
        UpdateButtons();
    }

    void ForwardState()
    {
        if (_game != null) _game.State = State;
    }

    private void UpdateButtons()
    {
        // Pause only while Playing. Stop only when not Stopped.
        if (PauseBtn != null) PauseBtn.IsEnabled = State == GameState.Playing;
        if (StopBtn != null) StopBtn.IsEnabled = State != GameState.Stopped;
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
    private void OnPlayClicked(object? s, RoutedEventArgs e)
    {
        State = GameState.Playing;
    }

    private void OnPauseClicked(object? s, RoutedEventArgs e)
    {
        if (State == GameState.Playing)
            State = GameState.Paused;
    }

    private void OnStopClicked(object? s, RoutedEventArgs e)
    {
        if (State != GameState.Stopped)
            State = GameState.Stopped; // this �unpauses� by leaving Paused state
    }
}
