using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Game_Engine.Core;

namespace Game_Engine.Views;

public partial class ScriptEditorWindow : Window
{
    private string _path;

    public ScriptEditorWindow(string path)
    {
        _path = path;
        InitializeComponent();

        Title = $"Script Editor — {Path.GetFileName(_path)}";

        // Wire UI
        BtnSave.Click += OnSave;
        BtnSaveAs.Click += OnSaveAs;
        BtnReload.Click += OnReload;
        BtnClose.Click += (_, __) => Close();

        // Load file text
        TryLoad();
    }

    private void TryLoad()
    {
        try { Editor.Text = File.Exists(_path) ? File.ReadAllText(_path) : ""; }
        catch { Editor.Text = ""; }
    }

    private void OnSave(object? s, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(_path, Editor.Text ?? "");
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save:\n{ex.Message}");
        }
    }

    private async void OnSaveAs(object? s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Script As…",
            InitialFileName = Path.GetFileName(_path),
            Filters = { new FileDialogFilter { Name = "C# File", Extensions = { "cs" } } }
        };
        var dst = await dlg.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(dst)) return;

        try
        {
            File.WriteAllText(dst, Editor.Text ?? "");
            _path = dst;
            Title = $"Script Editor — {Path.GetFileName(_path)}";
            ProjectService.TouchModified();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save:\n{ex.Message}");
        }
    }

    private void OnReload(object? s, RoutedEventArgs e) => TryLoad();

    private async void ShowError(string message)
    {
        var win = new Window
        {
            Title = "Error",
            Width = 420,
            Height = 180,
            Content = new TextBlock
            {
                Text = message,
                Margin = new Thickness(16),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        await win.ShowDialog(this);
    }

    /// <summary>Convenience opener used by ProjectPanel.</summary>
    public static async void Open(Window? owner, string path)
    {
        var w = new ScriptEditorWindow(path) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (owner is null) w.Show();
        else await w.ShowDialog(owner);
    }
}
