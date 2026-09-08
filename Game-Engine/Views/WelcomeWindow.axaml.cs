using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Game_Engine.Views;

public partial class WelcomeWindow : Window
{
    MainWindow _host = null!;

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    public WelcomeWindow(MainWindow host) : this()
    {
        _host = host;

        ChkIncludeStandardAssets.IsChecked = EditorSettings.IncludeStandardAssetsWhenCreatingProject;
        ChkShowWelcomeOnStartup.IsChecked = EditorSettings.ShowWelcomeDialogOnStartup;

        ChkIncludeStandardAssets.IsCheckedChanged += (_, __) =>
        {
            EditorSettings.IncludeStandardAssetsWhenCreatingProject = ChkIncludeStandardAssets.IsChecked == true;
            EditorSettings.Save();
        };
        ChkShowWelcomeOnStartup.IsCheckedChanged += (_, __) =>
        {
            EditorSettings.ShowWelcomeDialogOnStartup = ChkShowWelcomeOnStartup.IsChecked == true;
            EditorSettings.Save();
        };

        BtnCreate.Click += async (_, __) => await OnCreateAsync();
        BtnOpen.Click += async (_, __) => await OnOpenAsync();
        BtnCancel.Click += (_, __) => Close();
        BtnOpenRecent.Click += async (_, __) => await OnOpenSelectedRecentAsync();
        RecentsList.SelectionChanged += (_, __) =>
            BtnOpenRecent.IsEnabled = RecentsList.SelectedItem is RecentProjectRow;
        RecentsList.DoubleTapped += async (_, __) => await OnOpenSelectedRecentAsync();

        RefreshRecentsList();
    }

    void RefreshRecentsList()
    {
        var rows = new List<RecentProjectRow>();
        foreach (var p in RecentProjectsStore.GetPinned())
            rows.Add(new RecentProjectRow(p, true));
        foreach (var p in RecentProjectsStore.GetRecents())
        {
            if (rows.Any(r => PathsEqual(r.ManifestPath, p))) continue;
            rows.Add(new RecentProjectRow(p, false));
        }

        RecentsList.ItemsSource = rows;
    }

    static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    async Task OnCreateAsync()
    {
        if (!await _host.EnsureSafeToLoseUnsavedSceneAsync()) return;

        var parent = await PickParentFolderAsync();
        if (string.IsNullOrWhiteSpace(parent)) return;

        var nameDlg = new ProjectNameDialog { Title = "New Project" };
        var name = await nameDlg.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name)) return;

        bool includeAssets = ChkIncludeStandardAssets.IsChecked is not false;
        string parentPath = parent;
        string projectName = name;
        var host = _host;
        Close();

        // Close hub first, then open/create on the next UI tick so the modal isn't "Not Responding".
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                ProjectService.CreateNew(parentPath, projectName, openAfterCreate: true);
                if (includeAssets && ProjectService.Current is { } proj)
                {
                    if (!StandardAssetsInstaller.TryCopyToProject(proj.RootPath, out var err) && err is not null)
                        await host.ShowError($"Project was created, but standard assets were not copied:\n{err}");
                }

                host.ApplyProjectOpenedAfterCreateOrOpen();
            }
            catch (Exception ex)
            {
                await host.ShowError($"Failed to create project:\n{ex.Message}");
            }
        }, DispatcherPriority.Background);
    }

    async Task<string?> PickParentFolderAsync()
    {
        var parentDlg = new OpenFolderDialog { Title = "Choose parent folder for new project" };
        return await parentDlg.ShowAsync(this);
    }

    async Task OnOpenAsync()
    {
        if (!await _host.EnsureSafeToLoseUnsavedSceneAsync()) return;

        var dlg = new OpenFileDialog
        {
            AllowMultiple = false,
            Title = "Open project.json",
            Filters = { new FileDialogFilter { Name = "Project", Extensions = { "json" } } }
        };
        var files = await dlg.ShowAsync(this);
        if (files is not { Length: > 0 }) return;
        var path = files[0];
        if (string.IsNullOrWhiteSpace(path)) return;

        var host = _host;
        Close();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ProjectService.Open(path);
                host.ApplyProjectOpenedAfterCreateOrOpen();
            }
            catch (Exception ex)
            {
                _ = host.ShowError($"Failed to open project:\n{ex.Message}");
            }
        }, DispatcherPriority.Background);
    }

    async Task OnOpenSelectedRecentAsync()
    {
        if (RecentsList.SelectedItem is not RecentProjectRow row) return;
        if (!await _host.EnsureSafeToLoseUnsavedSceneAsync()) return;

        var path = row.ManifestPath;
        var host = _host;
        Close();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ProjectService.Open(path);
                host.ApplyProjectOpenedAfterCreateOrOpen();
            }
            catch (Exception ex)
            {
                _ = host.ShowError($"Failed to open recent project:\n{ex.Message}");
            }
        }, DispatcherPriority.Background);
    }

    sealed record RecentProjectRow(string ManifestPath, bool Pinned)
    {
        public override string ToString()
        {
            var star = Pinned ? "★ " : "";
            try
            {
                var dir = Path.GetDirectoryName(ManifestPath);
                var folder = dir is not null ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) : ManifestPath;
                return $"{star}{folder} — {ManifestPath}";
            }
            catch
            {
                return $"{star}{ManifestPath}";
            }
        }
    }
}
