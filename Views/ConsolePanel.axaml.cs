using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Game_Engine.Core;

namespace Game_Engine.Views;

public partial class ConsolePanel : UserControl
{
    public ObservableCollection<LogItem> Logs { get; } = new();

    public ConsolePanel()
    {
        InitializeComponent();
        DataContext = this;

        // hook UI events here to avoid XAML event parser edge cases
        Input.KeyUp += Input_OnKeyUp;
        RunButton.Click += Run_Click;

        // feed global logger to the UI list
        Log.Logged += OnLogged;

        // autoscroll on append
        Logs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && List.ItemCount > 0)
                List.ScrollIntoView(List.ItemCount - 1);
        };

        Log.Info("Console ready. Type 'help' for commands.");
    }

    private void OnLogged(object? sender, LogItem e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(() => Logs.Add(e));
        else
            Logs.Add(e);
    }

    private void Run_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RunCommand();

    private void Input_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunCommand();
            e.Handled = true;
        }
    }

    private void RunCommand()
    {
        var text = Input.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        ProcessCommand(text);
        Input.Text = string.Empty;
    }

    private void ProcessCommand(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                Log.Info("Commands:");
                Log.Info("  clear                      - clear the console");
                Log.Info("  log info <msg>            - info line");
                Log.Info("  log warn <msg>            - warning line");
                Log.Info("  log error <msg>           - error line");
                Log.Info("  log success <msg>         - success line");
                Log.Info("  log debug <msg>           - debug line");
                break;

            case "clear":
                Logs.Clear();
                break;

            case "log":
                if (parts.Length < 3)
                {
                    Log.Warning("Usage: log <info|warn|error|success|debug> <message>");
                    break;
                }
                var lvl = parts[1].ToLowerInvariant();
                var msg = parts[2];
                switch (lvl)
                {
                    case "info": Log.Info(msg); break;
                    case "warn":
                    case "warning": Log.Warning(msg); break;
                    case "error": Log.Error(msg); break;
                    case "success": Log.Success(msg); break;
                    case "debug": Log.Debug(msg); break;
                    default: Log.Warning($"Unknown level '{lvl}'."); break;
                }
                break;

            default:
                Log.Warning($"Unknown command '{parts[0]}'. Type 'help'.");
                break;
        }
    }
}
