using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Game_Engine.Core;

namespace Game_Engine.Views;

public partial class ConsolePanel : UserControl
{
    public ObservableCollection<LogItem> AllLogs { get; } = new();
    public ObservableCollection<LogItem> VisibleLogs { get; } = new();

    private static readonly object InstancesLock = new();
    private static readonly System.Collections.Generic.List<ConsolePanel> Instances = new();

    public ConsolePanel()
    {
        InitializeComponent();
        DataContext = this;

        lock (InstancesLock)
            Instances.Add(this);
        DetachedFromVisualTree += (_, __) =>
        {
            lock (InstancesLock)
                Instances.Remove(this);
        };

        Input.KeyUp += Input_OnKeyUp;
        RunButton.Click += Run_Click;
        BtnClear.Click += (_, __) => ClearConsoleContent();
        AddHandler(KeyDownEvent, OnConsolePanelKeyDown, RoutingStrategies.Tunnel);
        BtnCopy.Click += (_, __) => CopySelectedLine();
        FilterText.TextChanged += (_, __) => RebuildVisibleLogs();
        ChkInfo.IsCheckedChanged += (_, __) => RebuildVisibleLogs();
        ChkWarn.IsCheckedChanged += (_, __) => RebuildVisibleLogs();
        ChkError.IsCheckedChanged += (_, __) => RebuildVisibleLogs();
        ChkSuccess.IsCheckedChanged += (_, __) => RebuildVisibleLogs();
        ChkDebug.IsCheckedChanged += (_, __) => RebuildVisibleLogs();

        List.DoubleTapped += OnListDoubleTapped;

        Log.Logged += OnLogged;

        AllLogs.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            LogItem? added = null;
            if (e.NewItems?.Count > 0)
                added = e.NewItems[0] as LogItem;
            RebuildVisibleLogs();
            if (ChkAutoScroll.IsChecked == true && added != null && VisibleLogs.Contains(added) && VisibleLogs.Count > 0)
                List.ScrollIntoView(VisibleLogs[^1]);
        };

        RebuildVisibleLogs();
        Log.Info("Console ready. Type 'help' for commands. Double-click a line with a .cs path to open the editor.");
    }

    /// <summary>Clear all stored lines (e.g. when entering play mode).</summary>
    public static void ClearAllPanels()
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (InstancesLock)
            {
                foreach (var p in Instances.ToArray())
                    p.ClearAllLocal();
            }
        });
    }

    void ClearAllLocal()
    {
        AllLogs.Clear();
        VisibleLogs.Clear();
    }

    void ClearConsoleContent()
    {
        AllLogs.Clear();
        RebuildVisibleLogs();
    }

    private void OnConsolePanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key != Key.L) return;
        ClearConsoleContent();
        e.Handled = true;
    }

    private void OnLogged(object? sender, LogItem e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(() => AllLogs.Add(e));
        else
            AllLogs.Add(e);
    }

    bool SeverityVisible(LogSeverity s) => s switch
    {
        LogSeverity.Info => ChkInfo.IsChecked == true,
        LogSeverity.Warning => ChkWarn.IsChecked == true,
        LogSeverity.Error => ChkError.IsChecked == true,
        LogSeverity.Success => ChkSuccess.IsChecked == true,
        LogSeverity.Debug => ChkDebug.IsChecked == true,
        _ => true
    };

    void RebuildVisibleLogs()
    {
        var q = (FilterText.Text ?? "").Trim();
        VisibleLogs.Clear();
        foreach (var item in AllLogs)
        {
            if (!SeverityVisible(item.Severity)) continue;
            if (q.Length > 0 && (item.Message?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                continue;
            VisibleLogs.Add(item);
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (List.SelectedItem is not LogItem li) return;
        var win = this.GetVisualRoot() as Window;
        if (ConsoleLogNavigation.TryOpenFromMessage(li.Message ?? "", win))
            e.Handled = true;
    }

    private void Run_Click(object? sender, RoutedEventArgs e) => RunCommand();

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
                AllLogs.Clear();
                RebuildVisibleLogs();
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

    private async void CopySelectedLine()
    {
        if (List.SelectedItem is not LogItem li) return;
        var txt = $"[{li.Timestamp:HH:mm:ss}] [{li.Severity}] {li.Message}";
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
            await cb.SetTextAsync(txt);
    }
}
