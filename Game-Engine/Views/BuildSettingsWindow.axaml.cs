using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Editor;
using Game_Engine.Core.Rendering;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Game_Engine.Views
{
    public partial class BuildSettingsWindow : Window
    {
        // ─── Scene build list ───

        /// <summary>Absolute paths of scenes in build order. Index 0 = startup scene.</summary>
        private readonly ObservableCollection<string> _buildScenes = new();

        public BuildSettingsWindow()
        {
            InitializeComponent();

            var proj = ProjectService.Current;
            if (proj != null)
            {
                ProductNameBox.Text = proj.Name;
                RefreshOutputPath();

                // Try to load saved settings for the currently selected platform
                if (!TryLoadBuildSettings())
                {
                    // No saved settings — auto-discover scenes
                    DiscoverProjectScenes(proj);
                }
            }

            // Scene list data
            SceneList.ItemsSource = _buildScenes;
            RefreshSceneListDisplay();
            _buildScenes.CollectionChanged += (_, __) => RefreshSceneListDisplay();

            // Scene buttons
            BtnAddScene.Click    += OnAddScene;
            BtnRemoveScene.Click += OnRemoveScene;
            BtnSceneUp.Click     += OnSceneUp;
            BtnSceneDown.Click   += OnSceneDown;

            // Update output path / architecture constraints when platform changes
            PlatformBox.SelectionChanged += (_, __) =>
            {
                ApplyPlatformConstraints();
                RefreshOutputPath();
                RefreshOpenInVisualStudioButton();
            };
            ToolTip.SetTip(PlatformBox,
                "Desktop: publish Engine.Player to the output folder below. " +
                "Android: zips game Data as Data.zip in APK assets (first-launch extract). " +
                "Xbox: UWP Dev Mode host (launches on console — not FullTrust Avalonia). " +
                "Xbox GDK: native PC host with XAudio2 / GameInput / XSAPI. See Docs/12_Build_Settings.md.");

            ProjectRenderingSettings.Load(ProjectService.Current);
            DeferredRenderingCheck.IsChecked = ProjectRenderingSettings.UseDeferredRendering;
            DeferredRenderingCheck.IsCheckedChanged += (_, __) =>
            {
                if (DeferredRenderingCheck.IsChecked is bool b)
                    ProjectRenderingSettings.Save(ProjectService.Current, b);
            };

            BtnBuild.Click       += OnBuildClicked;
            BtnBuildAndRun.Click += OnBuildAndRunClicked;
            BtnOpenInVS.Click    += OnOpenInVisualStudioClicked;
            BtnRunLocal.Click    += OnRunLocalClicked;
            BtnCancel.Click      += (_, __) => Close();

            ApplyPlatformConstraints();
            RefreshOpenInVisualStudioButton();
        }

        // ─── Helpers ───

        private string SelectedPlatform =>
            (PlatformBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Windows";

        private string SelectedArchitecture =>
            ArchArm64.IsChecked == true ? "ARM64" : "x64";

        private string SelectedConfiguration =>
            CfgDebug.IsChecked == true ? "Debug" : "Release";

        private string SelectedCompression =>
            (CompressionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";

        private void RefreshOutputPath()
        {
            var proj = ProjectService.Current;
            if (proj == null) return;
            OutputFolderBox.Text = Path.Combine(proj.BuildsPath, SelectedPlatform);
        }

        private static bool IsXboxFamilyPlatform(string platform) =>
            string.Equals(platform, "Xbox", StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, "Xbox GDK", StringComparison.OrdinalIgnoreCase);

        /// <summary>Xbox / Xbox GDK packaging is x64-only; lock the architecture radios accordingly.</summary>
        private void ApplyPlatformConstraints()
        {
            var xboxFamily = IsXboxFamilyPlatform(SelectedPlatform);
            if (xboxFamily)
            {
                ArchX64.IsChecked = true;
                ArchArm64.IsChecked = false;
            }
            ArchArm64.IsEnabled = !xboxFamily;
            ArchX64.IsEnabled = true;

            var gdk = string.Equals(SelectedPlatform, "Xbox GDK", StringComparison.OrdinalIgnoreCase);
            var xboxUwp = string.Equals(SelectedPlatform, "Xbox", StringComparison.OrdinalIgnoreCase);
            XboxGameConfigSection.IsVisible = xboxFamily;
            XboxUwpDeployFields.IsVisible = xboxUwp;
            XboxGdkLiveFields.IsVisible = gdk;
        }

        private void RefreshOpenInVisualStudioButton()
        {
            var proj = ProjectService.Current;
            var isMsixXbox = string.Equals(SelectedPlatform, "Xbox", StringComparison.OrdinalIgnoreCase);
            var isGdk = string.Equals(SelectedPlatform, "Xbox GDK", StringComparison.OrdinalIgnoreCase);

            string? sln = null;
            if (proj != null && isMsixXbox)
                sln = XboxVisualStudioPackager.FindExistingSolution(Path.Combine(proj.BuildsPath, "Xbox"));
            else if (proj != null && isGdk)
                sln = XboxGdkPackager.FindExistingSolution(Path.Combine(proj.BuildsPath, "XboxGdk"));

            BtnOpenInVS.IsVisible = isMsixXbox || isGdk;
            BtnOpenInVS.IsEnabled = (isMsixXbox || isGdk) && !string.IsNullOrEmpty(sln) && File.Exists(sln);

            // UWP Xbox builds deploy via Visual Studio Remote Machine — no Avalonia layout Run Local.
            BtnRunLocal.IsVisible = false;
            BtnRunLocal.IsEnabled = false;
        }

        private void OnOpenInVisualStudioClicked(object? sender, RoutedEventArgs e)
        {
            var proj = ProjectService.Current;
            if (proj == null)
            {
                StatusText.Text = "No project open.";
                return;
            }

            string? sln = null;
            if (string.Equals(SelectedPlatform, "Xbox GDK", StringComparison.OrdinalIgnoreCase))
                sln = XboxGdkPackager.FindExistingSolution(Path.Combine(proj.BuildsPath, "XboxGdk"));
            else
                sln = XboxVisualStudioPackager.FindExistingSolution(Path.Combine(proj.BuildsPath, "Xbox"));

            if (string.IsNullOrEmpty(sln))
            {
                StatusText.Text = "No generated Visual Studio solution yet — build for this platform first.";
                return;
            }

            try
            {
                XboxVisualStudioPackager.OpenInVisualStudio(sln);
                StatusText.Text = $"Opened {Path.GetFileName(sln)} in Visual Studio.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open Visual Studio: {ex.Message}";
                Log.Warning($"[Build] Open in Visual Studio failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Quick PC test: run the already-published layout player with external Data.
        /// Does not rebuild Assets or create an MSIX.
        /// </summary>
        private void OnRunLocalClicked(object? sender, RoutedEventArgs e)
        {
            var proj = ProjectService.Current;
            if (proj == null)
            {
                StatusText.Text = "No project open.";
                return;
            }

            var xboxOut = Path.Combine(proj.BuildsPath, "Xbox");
            var exe = Path.Combine(xboxOut, "layout", "Engine.Player.exe");
            var buildJson = Path.Combine(xboxOut, "layout", "Data", "build.json");
            if (!File.Exists(exe) || !File.Exists(buildJson))
            {
                StatusText.Text = "No Xbox layout yet — Build for Xbox once first.";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"\"{buildJson}\"",
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true
                });
                StatusText.Text = "Launched local unpackaged player (first asset extract can take a while; later launches reuse cache).";
                Log.Info($"[Build] Run Local: {exe} {buildJson}");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Run Local failed: {ex.Message}";
                Log.Warning($"[Build] Run Local failed: {ex.Message}");
            }
        }

        // ─── Saved settings persistence ───

        /// <summary>Path to the project-level build settings file.</summary>
        private static string? GetSettingsPath()
        {
            var proj = ProjectService.Current;
            if (proj == null) return null;
            return Path.Combine(proj.RootPath, "ProjectSettings", "build.json");
        }

        /// <summary>
        /// Load saved build settings from ProjectSettings/build.json and populate all
        /// UI fields. Returns true if settings were found and loaded.
        /// </summary>
        private bool TryLoadBuildSettings()
        {
            var proj = ProjectService.Current;
            if (proj == null) return false;

            var settingsPath = GetSettingsPath();
            if (settingsPath == null || !File.Exists(settingsPath)) return false;

            try
            {
                var json = File.ReadAllText(settingsPath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // ── Player settings ──
                if (root.TryGetProperty("product", out var product))
                    ProductNameBox.Text = product.GetString() ?? "";
                if (root.TryGetProperty("company", out var company))
                    CompanyBox.Text = company.GetString() ?? "";
                if (root.TryGetProperty("version", out var version))
                    VersionBox.Text = version.GetString() ?? "1.0.0";

                // ── Platform ──
                if (root.TryGetProperty("platform", out var platform))
                {
                    var plat = platform.GetString() ?? "Windows";
                    // Legacy label
                    if (string.Equals(plat, "Windows (MSIX)", StringComparison.OrdinalIgnoreCase))
                        plat = "Xbox";
                    SelectComboBoxItem(PlatformBox, plat);
                }

                // ── Architecture ──
                if (root.TryGetProperty("architecture", out var arch))
                {
                    var archStr = arch.GetString() ?? "x64";
                    if (archStr.Equals("ARM64", StringComparison.OrdinalIgnoreCase))
                    { ArchArm64.IsChecked = true; ArchX64.IsChecked = false; }
                    else
                    { ArchX64.IsChecked = true; ArchArm64.IsChecked = false; }
                }

                // ── Configuration ──
                if (root.TryGetProperty("configuration", out var cfg))
                {
                    var cfgStr = cfg.GetString() ?? "Release";
                    if (cfgStr.Equals("Debug", StringComparison.OrdinalIgnoreCase))
                    { CfgDebug.IsChecked = true; CfgRelease.IsChecked = false; }
                    else
                    { CfgRelease.IsChecked = true; CfgDebug.IsChecked = false; }
                }

                // ── Resolution ──
                if (root.TryGetProperty("resolution", out var res))
                {
                    if (res.TryGetProperty("width", out var w)) ResWidth.Value = w.GetInt32();
                    if (res.TryGetProperty("height", out var h)) ResHeight.Value = h.GetInt32();
                    if (res.TryGetProperty("fullscreen", out var fs)) FullscreenCheck.IsChecked = fs.GetBoolean();
                }

                // ── Compression ──
                if (root.TryGetProperty("compression", out var comp))
                    SelectComboBoxItem(CompressionBox, comp.GetString() ?? "None");

                // ── Xbox Game Config / Live ──
                if (root.TryGetProperty("gdkScid", out var scid))
                    GdkScidBox.Text = scid.GetString() ?? "";
                if (root.TryGetProperty("gdkTitleId", out var titleId))
                    GdkTitleIdBox.Text = titleId.GetString() ?? "";
                if (root.TryGetProperty("gdkSandbox", out var sandbox))
                    GdkSandboxBox.Text = sandbox.GetString() ?? "XDKS.1";
                if (root.TryGetProperty("gcIdentityName", out var idName))
                    GcIdentityNameBox.Text = idName.GetString() ?? "";
                if (root.TryGetProperty("gcDisplayName", out var dispName))
                    GcDisplayNameBox.Text = dispName.GetString() ?? "";
                if (root.TryGetProperty("gcPublisherDisplay", out var pubDisp))
                    GcPublisherDisplayBox.Text = pubDisp.GetString() ?? "";
                if (root.TryGetProperty("gcDescription", out var desc))
                    GcDescriptionBox.Text = desc.GetString() ?? "";
                if (root.TryGetProperty("gcStoreId", out var storeId))
                    GcStoreIdBox.Text = storeId.GetString() ?? "";
                if (root.TryGetProperty("gcMsaAppId", out var msaAppId))
                    GcMsaAppIdBox.Text = msaAppId.GetString() ?? "";
                if (root.TryGetProperty("gcLogo150", out var logo150))
                    GcLogo150Box.Text = logo150.GetString() ?? "";
                if (root.TryGetProperty("gcLogo44", out var logo44))
                    GcLogo44Box.Text = logo44.GetString() ?? "";
                if (root.TryGetProperty("gcDeviceFamily", out var deviceFamily))
                    SelectComboBoxItem(GcDeviceFamilyBox, deviceFamily.GetString() ?? "PC");
                if (root.TryGetProperty("gcRemoteMachine", out var remoteMachine))
                    GcRemoteMachineBox.Text = remoteMachine.GetString() ?? "";

                // ── Scenes ──
                if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
                {
                    _buildScenes.Clear();
                    foreach (var sceneEl in scenes.EnumerateArray())
                    {
                        var sceneName = sceneEl.GetString();
                        if (string.IsNullOrEmpty(sceneName)) continue;
                        var absPath = ResolveSceneName(proj, sceneName);
                        if (absPath != null && !_buildScenes.Contains(absPath))
                            _buildScenes.Add(absPath);
                    }
                }

                // ── Built timestamp info ──
                if (root.TryGetProperty("builtUtc", out var builtUtc))
                {
                    try
                    {
                        var dt = builtUtc.GetDateTime().ToLocalTime();
                        StatusText.Text = $"Last built: {dt:yyyy-MM-dd HH:mm}";
                    }
                    catch { }
                }

                // Refresh output path now that platform may have changed
                RefreshOutputPath();

                Log.Info($"[Build] Loaded saved build settings from {settingsPath}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[Build] Could not load build settings: {ex.Message}");
                return false;
            }
        }

        /// <summary>Save current UI values to ProjectSettings/build.json for next session.</summary>
        private void SaveBuildSettings(BuildInfo info, List<string> sceneOrder)
        {
            var settingsPath = GetSettingsPath();
            if (settingsPath == null) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

                var obj = JsonSerializer.Serialize(new
                {
                    product       = info.ProductName,
                    company       = info.Company,
                    version       = info.Version,
                    platform      = info.Platform,
                    architecture  = info.Architecture,
                    configuration = info.Configuration,
                    scenes        = sceneOrder,
                    resolution    = new { width = info.Width, height = info.Height, fullscreen = info.Fullscreen },
                    compression   = info.Compression,
                    gdkScid            = info.GdkScid,
                    gdkTitleId         = info.GdkTitleId,
                    gdkSandbox         = info.GdkSandbox,
                    gcIdentityName     = info.GcIdentityName,
                    gcDisplayName      = info.GcDisplayName,
                    gcPublisherDisplay = info.GcPublisherDisplay,
                    gcDescription      = info.GcDescription,
                    gcStoreId          = info.GcStoreId,
                    gcMsaAppId         = info.GcMsaAppId,
                    gcLogo150          = info.GcLogo150,
                    gcLogo44           = info.GcLogo44,
                    gcDeviceFamily     = info.GcDeviceFamily,
                    gcRemoteMachine    = info.GcRemoteMachine,
                    builtUtc           = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(settingsPath, obj);
            }
            catch { /* non-fatal */ }
        }

        /// <summary>
        /// Resolve a relative scene filename (e.g. "Main.scene") back to its absolute path
        /// by searching the project's Scenes folder.
        /// </summary>
        private static string? ResolveSceneName(Project proj, string sceneName)
        {
            if (!Directory.Exists(proj.ScenesPath)) return null;

            // Direct match in Scenes root
            var direct = Path.Combine(proj.ScenesPath, sceneName);
            if (File.Exists(direct)) return direct;

            // Search recursively for the filename
            try
            {
                var match = Directory.EnumerateFiles(proj.ScenesPath, sceneName, SearchOption.AllDirectories)
                                     .FirstOrDefault();
                if (match != null) return Path.GetFullPath(match);
            }
            catch { }

            return null;
        }

        /// <summary>Select a ComboBox item by its string content.</summary>
        private static void SelectComboBoxItem(ComboBox box, string content)
        {
            for (int i = 0; i < box.ItemCount; i++)
            {
                if (box.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
        }

        // ─── Scene list management ───

        /// <summary>Auto-discover .scene files in project Scenes folder on first open.</summary>
        private void DiscoverProjectScenes(Project proj)
        {
            if (!Directory.Exists(proj.ScenesPath)) return;

            var files = Directory.GetFiles(proj.ScenesPath, "*.scene", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (!_buildScenes.Contains(f))
                    _buildScenes.Add(f);
            }
        }

        /// <summary>Get available scenes from the project that aren't in the build list yet.</summary>
        private List<string> GetAvailableScenes()
        {
            var proj = ProjectService.Current;
            if (proj == null || !Directory.Exists(proj.ScenesPath))
                return new List<string>();

            return Directory.GetFiles(proj.ScenesPath, "*.scene", SearchOption.AllDirectories)
                            .Where(f => !_buildScenes.Contains(f))
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }

        /// <summary>Make the path project-relative for display.</summary>
        private string DisplayName(string absolutePath)
        {
            var proj = ProjectService.Current;
            if (proj != null && absolutePath.StartsWith(proj.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                var rel = absolutePath.Substring(proj.RootPath.Length)
                                      .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel;
            }
            return Path.GetFileName(absolutePath);
        }

        /// <summary>Rebuild the ListBox items with index numbers and startup badge.</summary>
        private void RefreshSceneListDisplay()
        {
            // Preserve selection
            var selIdx = SceneList.SelectedIndex;

            var items = new List<object>();
            for (int i = 0; i < _buildScenes.Count; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

                // Index number
                row.Children.Add(new TextBlock
                {
                    Text = i.ToString(),
                    Width = 20,
                    Foreground = new SolidColorBrush(Color.Parse("#555B63")),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right
                });

                // Scene name
                row.Children.Add(new TextBlock
                {
                    Text = DisplayName(_buildScenes[i]),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#C8CDD4")),
                    VerticalAlignment = VerticalAlignment.Center
                });

                // Startup badge on index 0
                if (i == 0)
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = "STARTUP",
                        FontSize = 10,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#33C759")),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0)
                    });
                }

                items.Add(row);
            }

            SceneList.ItemsSource = items;

            // Restore selection
            if (selIdx >= 0 && selIdx < items.Count)
                SceneList.SelectedIndex = selIdx;
        }

        private async void OnAddScene(object? sender, RoutedEventArgs e)
        {
            var available = GetAvailableScenes();

            if (available.Count == 0)
            {
                // No scenes left to add — try file picker
                var dlg = new OpenFileDialog
                {
                    AllowMultiple = true,
                    Title = "Add Scenes to Build",
                    Filters = { new FileDialogFilter { Name = "Scene Files", Extensions = { "scene" } } }
                };

                var proj = ProjectService.Current;
                if (proj != null) dlg.Directory = proj.ScenesPath;

                var files = await dlg.ShowAsync(this);
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        if (!_buildScenes.Contains(f))
                            _buildScenes.Add(f);
                    }
                }
                return;
            }

            // Show a picker with available project scenes
            var picker = new Window
            {
                Title = "Add Scene to Build",
                Width = 420,
                Height = 340,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var root = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
            root.Children.Add(new TextBlock
            {
                Text = "Select scenes to add:",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var list = new ListBox
            {
                SelectionMode = SelectionMode.Multiple,
                MinHeight = 180,
                MaxHeight = 220
            };

            var displayNames = available.Select(f => DisplayName(f)).ToList();
            list.ItemsSource = displayNames;

            root.Children.Add(list);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var addBtn = new Button { Content = "Add Selected", Padding = new Thickness(14, 6) };
            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(14, 6) };

            addBtn.Click += (_, __) =>
            {
                var indices = list.Selection.SelectedIndexes;
                foreach (var idx in indices)
                {
                    if (idx >= 0 && idx < available.Count && !_buildScenes.Contains(available[idx]))
                        _buildScenes.Add(available[idx]);
                }
                picker.Close();
            };
            cancelBtn.Click += (_, __) => picker.Close();

            btnRow.Children.Add(addBtn);
            btnRow.Children.Add(cancelBtn);
            root.Children.Add(btnRow);

            picker.Content = root;
            await picker.ShowDialog(this);
        }

        private void OnRemoveScene(object? sender, RoutedEventArgs e)
        {
            var idx = SceneList.SelectedIndex;
            if (idx < 0 || idx >= _buildScenes.Count) return;
            _buildScenes.RemoveAt(idx);

            // Select nearest item
            if (_buildScenes.Count > 0)
                SceneList.SelectedIndex = Math.Min(idx, _buildScenes.Count - 1);
        }

        private void OnSceneUp(object? sender, RoutedEventArgs e)
        {
            var idx = SceneList.SelectedIndex;
            if (idx <= 0 || idx >= _buildScenes.Count) return;
            _buildScenes.Move(idx, idx - 1);
            SceneList.SelectedIndex = idx - 1;
        }

        private void OnSceneDown(object? sender, RoutedEventArgs e)
        {
            var idx = SceneList.SelectedIndex;
            if (idx < 0 || idx >= _buildScenes.Count - 1) return;
            _buildScenes.Move(idx, idx + 1);
            SceneList.SelectedIndex = idx + 1;
        }

        // ─── Build logic ───

        private async void OnBuildClicked(object? sender, RoutedEventArgs e)
        {
            await RunBuild(launch: false);
        }

        private async void OnBuildAndRunClicked(object? sender, RoutedEventArgs e)
        {
            await RunBuild(launch: true);
        }

        private async Task RunBuild(bool launch)
        {
            var proj = ProjectService.Current;
            if (proj == null)
            {
                StatusText.Text = "No project open.";
                return;
            }

            if (_buildScenes.Count == 0)
            {
                StatusText.Text = "No scenes in build. Add at least one scene.";
                return;
            }

            // Lock UI
            BtnBuild.IsEnabled = false;
            BtnBuildAndRun.IsEnabled = false;
            BuildProgressPanel.IsVisible = true;
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Value = 0;
            BuildProgressPercent.Text = "0%";

            var sceneCount = _buildScenes.Count;
            var buildHeadline =
                $"Building {SelectedConfiguration} for {SelectedPlatform} ({SelectedArchitecture}) — {sceneCount} scene{(sceneCount != 1 ? "s" : "")}";
            ReportBuildProgress(0, buildHeadline + "...");
            Log.Info($"[Build] Starting {SelectedConfiguration} build for {SelectedPlatform} ({SelectedArchitecture}) with {sceneCount} scene(s)...");

            try
            {
                var outDir = Path.Combine(proj.BuildsPath, SelectedPlatform);
                var scenePaths = _buildScenes.ToList();

                // Capture all UI values on the UI thread before entering Task.Run
                var buildInfo = new BuildInfo
                {
                    ProductName   = ProductNameBox.Text ?? "",
                    Company       = CompanyBox.Text ?? "",
                    Version       = VersionBox.Text ?? "1.0.0",
                    Platform      = SelectedPlatform,
                    Architecture  = SelectedArchitecture,
                    Configuration = SelectedConfiguration,
                    Compression   = SelectedCompression,
                    Width         = (int)(ResWidth.Value ?? 1920),
                    Height        = (int)(ResHeight.Value ?? 1080),
                    Fullscreen    = FullscreenCheck.IsChecked == true,
                    GdkScid            = GdkScidBox.Text ?? "",
                    GdkTitleId         = GdkTitleIdBox.Text ?? "",
                    GdkSandbox         = GdkSandboxBox.Text ?? "XDKS.1",
                    GcIdentityName     = GcIdentityNameBox.Text ?? "",
                    GcDisplayName      = GcDisplayNameBox.Text ?? "",
                    GcPublisherDisplay = GcPublisherDisplayBox.Text ?? "",
                    GcDescription      = GcDescriptionBox.Text ?? "",
                    GcStoreId          = GcStoreIdBox.Text ?? "",
                    GcMsaAppId         = GcMsaAppIdBox.Text ?? "",
                    GcLogo150          = GcLogo150Box.Text ?? "",
                    GcLogo44           = GcLogo44Box.Text ?? "",
                    GcDeviceFamily     = (GcDeviceFamilyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PC",
                    GcRemoteMachine    = GcRemoteMachineBox.Text ?? ""
                };

                var progress = new Progress<(int Percent, string Message)>(p =>
                    ReportBuildProgress(p.Percent, p.Message));

                string? exePath = null;
                await Task.Run(() => exePath = PerformBuild(proj, outDir, scenePaths, buildInfo, progress));

                ReportBuildProgress(100, "Finishing...");

                if (_copyWarnings > 0)
                {
                    StatusText.Text = $"Build succeeded with {_copyWarnings} warning(s) — {DescribeBuildOutput(outDir, exePath)}";
                    Log.Warning($"[Build] Build succeeded with {_copyWarnings} file(s) skipped — output: {outDir}");
                }
                else
                {
                    StatusText.Text = $"Build succeeded — {DescribeBuildOutput(outDir, exePath)}";
                    Log.Info($"[Build] Build succeeded — output: {outDir}");
                }

                BuildProgressPercent.Text = "100%";
                RefreshOpenInVisualStudioButton();

                if (launch && string.Equals(buildInfo.Platform, "Xbox", StringComparison.OrdinalIgnoreCase))
                {
                    var sln = XboxVisualStudioPackager.FindExistingSolution(outDir);
                    if (!string.IsNullOrEmpty(sln) && File.Exists(sln))
                    {
                        StatusText.Text += " — Opening UWP solution for Xbox Remote Machine deploy...";
                        try { XboxVisualStudioPackager.OpenInVisualStudio(sln); }
                        catch (Exception openEx)
                        {
                            StatusText.Text += $" (open failed: {openEx.Message})";
                        }
                    }
                    else
                    {
                        StatusText.Text += " — Open Builds/Xbox/VisualStudio/*.sln in VS and Deploy to Remote Machine.";
                    }
                }
                else if (launch && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    StatusText.Text += " — Launching...";
                    Log.Info($"[Build] Launching {exePath}");
                    try
                    {
                        if (string.Equals(buildInfo.Platform, "Xbox GDK", StringComparison.OrdinalIgnoreCase)
                                 && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var args = new List<string>();
                            if (!string.IsNullOrWhiteSpace(buildInfo.GdkScid))
                                args.Add($"--scid={buildInfo.GdkScid.Trim()}");
                            args.Add("--test-tone=1");
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = exePath,
                                Arguments = string.Join(" ", args),
                                WorkingDirectory = Path.GetDirectoryName(exePath),
                                UseShellExecute = true
                            });
                        }
                        else if (exePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "adb",
                                Arguments = $"install -r \"{exePath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            });
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = exePath,
                                WorkingDirectory = Path.GetDirectoryName(exePath),
                                UseShellExecute = true
                            });
                        }
                    }
                    catch (Exception launchEx)
                    {
                        StatusText.Text += $" (launch failed: {launchEx.Message})";
                        Log.Warning($"[Build] Launch failed: {launchEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                BuildProgress.Value = 0;
                BuildProgressPercent.Text = "0%";
                StatusText.Text = $"Build failed: {ex.Message}";
                Log.Warning($"[Build] Build failed: {ex.Message}");
            }

            // Unlock UI
            BtnBuild.IsEnabled = true;
            BtnBuildAndRun.IsEnabled = true;
        }

        /// <summary>Update the determinate progress bar and status line (UI thread).</summary>
        private void ReportBuildProgress(int percent, string message)
        {
            percent = Math.Clamp(percent, 0, 100);
            void apply()
            {
                BuildProgressPanel.IsVisible = true;
                BuildProgress.IsIndeterminate = false;
                BuildProgress.Value = percent;
                BuildProgressPercent.Text = $"{percent}%";
                StatusText.Text = percent >= 100
                    ? message
                    : $"[{percent}%] {message}";
            }

            if (Dispatcher.UIThread.CheckAccess())
                apply();
            else
                Dispatcher.UIThread.Post(apply);
        }

        private static void Report(
            IProgress<(int Percent, string Message)>? progress,
            int percent,
            string message)
        {
            progress?.Report((percent, message));
        }

        /// <summary>All UI values captured on the UI thread before Task.Run.</summary>
        private struct BuildInfo
        {
            public string ProductName, Company, Version;
            public string Platform, Architecture, Configuration, Compression;
            public int Width, Height;
            public bool Fullscreen;
            public string GdkScid, GdkTitleId, GdkSandbox;
            public string GcIdentityName, GcDisplayName, GcPublisherDisplay, GcDescription;
            public string GcStoreId, GcMsaAppId, GcLogo150, GcLogo44, GcDeviceFamily, GcRemoteMachine;
        }

        /// <summary>Returns true if <paramref name="path"/> is a sideloadable Windows app package (.msix, .msixbundle, or .appx).</summary>
        private static bool IsWindowsAppPackagePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".appx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Map Build Settings platform + architecture choices to a .NET Runtime Identifier.
        /// </summary>
        private static string GetRuntimeIdentifier(string platform, string architecture)
        {
            var arch = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
            return platform switch
            {
                "macOS"            => $"osx-{arch}",
                "Linux"            => $"linux-{arch}",
                "Android"          => architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "android-arm64" : "android-x64",
                "Xbox"             => $"win-{arch}",
                "Xbox GDK"         => $"win-{arch}",
                _                  => $"win-{arch}"   // Windows (unpackaged)
            };
        }

        private static string GetTargetFramework(string platform) =>
            platform switch
            {
                "Xbox"           => "net9.0-windows10.0.19041.0",
                "Xbox GDK"       => "net9.0-windows10.0.19041.0",
                "Android"        => "net9.0-android35.0",
                _                => "net9.0"
            };

        /// <summary>
        /// Locate the Engine.Player.csproj relative to this editor project.
        /// </summary>
        private static string FindPlayerCsproj()
        {
            // The editor exe sits inside Game-Engine/bin/... — walk up to workspace root
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(dir, "Engine.Player", "Engine.Player.csproj");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null || parent == dir) break;
                dir = parent;
            }

            // Fallback: check relative to the project file itself
            var editorCsproj = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Game_Engine.csproj");
            if (File.Exists(editorCsproj))
            {
                var editorDir = Path.GetDirectoryName(Path.GetFullPath(editorCsproj))!;
                var wsRoot = Path.GetDirectoryName(editorDir)!;
                var candidate2 = Path.Combine(wsRoot, "Engine.Player", "Engine.Player.csproj");
                if (File.Exists(candidate2)) return candidate2;
            }

            throw new FileNotFoundException(
                "Could not find Engine.Player/Engine.Player.csproj. " +
                "Make sure the Engine.Player project exists as a sibling to Game-Engine/.");
        }

        /// <summary>
        /// Locate Engine.Player.Android.csproj (Android-only host) next to Engine.Player.
        /// </summary>
        private static string FindPlayerAndroidCsproj()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(dir, "Engine.Player.Android", "Engine.Player.Android.csproj");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null || parent == dir) break;
                dir = parent;
            }

            var editorCsproj = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Game_Engine.csproj");
            if (File.Exists(editorCsproj))
            {
                var editorDir = Path.GetDirectoryName(Path.GetFullPath(editorCsproj))!;
                var wsRoot = Path.GetDirectoryName(editorDir)!;
                var candidate2 = Path.Combine(wsRoot, "Engine.Player.Android", "Engine.Player.Android.csproj");
                if (File.Exists(candidate2)) return candidate2;
            }

            throw new FileNotFoundException(
                "Could not find Engine.Player.Android/Engine.Player.Android.csproj. " +
                "Make sure the Android player project exists next to Engine.Player/.");
        }

        /// <summary>
        /// Full build pipeline: compile scripts -> dotnet publish -> package assets + Data.
        /// Returns a path to the primary output for launch (unpackaged exe, Windows app package, or APK) when present.
        /// </summary>
        private string? PerformBuild(
            Project proj,
            string outDir,
            List<string> scenePaths,
            BuildInfo info,
            IProgress<(int Percent, string Message)>? progress = null)
        {
            if (IsXboxFamilyPlatform(info.Platform) && !OperatingSystem.IsWindows())
                throw new Exception("Xbox packaging must be run on Windows.");

            if (string.Equals(info.Platform, "Android", StringComparison.OrdinalIgnoreCase))
                return PerformBuildAndroid(proj, outDir, scenePaths, info, progress);

            if (string.Equals(info.Platform, "Xbox GDK", StringComparison.OrdinalIgnoreCase))
                return PerformBuildXboxGdk(proj, outDir, scenePaths, info, progress);

            var isXbox = string.Equals(info.Platform, "Xbox", StringComparison.OrdinalIgnoreCase);
            if (isXbox)
                return PerformBuildXboxUwp(proj, outDir, scenePaths, info, progress);

            _copyWarnings = 0;
            Directory.CreateDirectory(outDir);

            var publishDir = outDir;
            Directory.CreateDirectory(publishDir);

            var rid = GetRuntimeIdentifier(info.Platform, info.Architecture);
            var tfm = GetTargetFramework(info.Platform);
            var dataDir = Path.Combine(publishDir, "Data");
            Directory.CreateDirectory(dataDir);

            // ── Step 1: Compile user scripts ──
            Report(progress, 5, "Compiling user scripts...");
            Log.Info("[Build] Step 1/5: Compiling user scripts...");
            CompilePlayerScripts(dataDir, info.Configuration == "Release");

            // ── Step 2: dotnet publish Engine.Player (often the longest step) ──
            Report(progress, 15, $"Publishing Engine.Player ({tfm}, {rid})...");
            Log.Info($"[Build] Step 2/5: Publishing Engine.Player ({tfm}, {rid}, {info.Configuration})...");
            var playerCsproj = FindPlayerCsproj();
            RunDotnetPublish(playerCsproj,
                $"publish \"{playerCsproj}\" -c {info.Configuration} -f {tfm} -r {rid} --self-contained --force -o \"{publishDir}\"");

            // ── Step 3: Package only assets referenced by scenes in this build ──
            Report(progress, 65, "Collecting assets used by build scenes...");
            Log.Info("[Build] Step 3/5: Packaging scene-referenced assets into Assets.dll...");
            if (Directory.Exists(proj.AssetsPath))
            {
                var referenced = BuildAssetCollector.CollectForScenes(proj, scenePaths);
                var dllPath = Path.Combine(dataDir, "Assets.dll");
                if (File.Exists(dllPath)) File.Delete(dllPath);
                if (referenced.Count == 0)
                {
                    Log.Info("[Build] No external asset files referenced by build scenes — skipping Assets.dll.");
                }
                else
                {
                    CreateAssetPak(proj.AssetsPath, dllPath, referenced, info.Compression, progress, 65, 82);
                    Log.Info($"[Build] Assets.dll created ({new FileInfo(dllPath).Length / 1024.0 / 1024.0:F1} MB) from {referenced.Count} scene-referenced file(s).");
                }
            }

            Report(progress, 80, "Copying scenes and input bindings...");
            Log.Info("[Build] Step 4/5: Copying scenes...");
            CopyBuildScenes(dataDir, scenePaths);
            CopyInputBindings(proj, dataDir);

            Report(progress, 90, "Writing build manifest...");
            Log.Info("[Build] Step 5/5: Writing build manifest...");
            var sceneOrder = WriteDataManifest(dataDir, scenePaths, info);
            SaveBuildSettings(info, sceneOrder);

            string exeName = rid.StartsWith("win") ? "Engine.Player.exe" : "Engine.Player";
            var exePath = Path.Combine(publishDir, exeName);
            return File.Exists(exePath) ? exePath : null;
        }

        /// <summary>
        /// Stage Engine.Player.XboxUwp (real UWP) with game Data for Xbox Dev Mode Remote Machine deploy.
        /// Does not publish Avalonia FullTrust Engine.Player — Xbox rejects that app model.
        /// </summary>
        private string? PerformBuildXboxUwp(
            Project proj,
            string outDir,
            List<string> scenePaths,
            BuildInfo info,
            IProgress<(int Percent, string Message)>? progress = null)
        {
            _copyWarnings = 0;
            Directory.CreateDirectory(outDir);

            var dataDir = Path.Combine(outDir, "Data");
            Directory.CreateDirectory(dataDir);

            Report(progress, 5, "Compiling user scripts (Xbox UWP)...");
            CompilePlayerScripts(dataDir, info.Configuration == "Release");

            // Assets named by the scenes, by the project's scripts and by the data files those load,
            // plus the scenes themselves with absolute paths rewritten for the package.
            Report(progress, 20, "Staging assets and scenes (Xbox UWP)...");
            var staged = XboxContentStager.Stage(proj, scenePaths, dataDir,
                message => Report(progress, 35, message));
            Log.Info($"[Build] Xbox content: {staged.ProjectFiles} project file(s), {staged.ExternalFiles} external, "
                     + $"{staged.TexturesBaked} texture(s) compressed, {staged.StaleFilesRemoved} stale file(s) removed.");
            _copyWarnings += staged.Unresolved.Count + staged.Undecodable.Count;
            try
            {
                Game_Engine.Core.Component.UI.DefaultFontGenerator.Generate(
                    Path.Combine(dataDir, "Assets", "Standard Assets", "Fonts"));
            }
            catch (Exception ex)
            {
                Log.Warning("[Build] Default font bake failed: " + ex.Message);
            }

            Report(progress, 60, "Writing manifest...");
            CopyInputBindings(proj, dataDir);
            var sceneOrder = WriteDataManifest(dataDir, scenePaths, info);
            SaveBuildSettings(info, sceneOrder);

            Report(progress, 75, "Staging Engine.Player.XboxUwp and building package...");
            var result = XboxVisualStudioPackager.GenerateAndBuild(new XboxVisualStudioPackager.Options(
                XboxOutDir: outDir,
                DataDir: dataDir,
                ProjectRootPath: proj.RootPath,
                ProductName: info.ProductName,
                Company: info.Company,
                Version: info.Version,
                IdentityName: info.GcIdentityName,
                DisplayName: info.GcDisplayName,
                PublisherDisplayName: info.GcPublisherDisplay,
                Description: info.GcDescription,
                RemoteMachine: info.GcRemoteMachine,
                Configuration: info.Configuration,
                Progress: progress));

            Report(progress, 98, "Xbox UWP package ready for Remote Machine deploy...");
            return result.SolutionPath;
        }

        /// <summary>
        /// Overwrite a staged file even when an earlier copy was marked read-only.
        /// Asset packs often ship that way, and <see cref="File.Copy(string, string, bool)"/>
        /// then throws "Access to the path is denied" on the next build.
        /// </summary>
        static void CopyReplacing(string src, string dest)
        {
            MakeWritable(dest);
            File.Copy(src, dest, overwrite: true);
            MakeWritable(dest);
        }

        static void MakeWritable(string path)
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }

        private static string DescribeBuildOutput(string outDir, string? primaryOutput)
        {
            if (!string.IsNullOrEmpty(primaryOutput) && primaryOutput.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(primaryOutput) + " (Xbox UWP — Deploy via VS Remote Machine)";
            if (!string.IsNullOrEmpty(primaryOutput) && IsWindowsAppPackagePath(primaryOutput))
                return Path.GetFileName(primaryOutput);
            if (!string.IsNullOrEmpty(primaryOutput) && primaryOutput.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(primaryOutput);
            return outDir;
        }

        /// <summary>
        /// Stage Engine.Player.Gdk, write Partner Center identity, MSBuild Gaming.Desktop.x64.
        /// Fails clearly when the Microsoft GDK is not installed.
        /// </summary>
        private string? PerformBuildXboxGdk(
            Project proj,
            string outDir,
            List<string> scenePaths,
            BuildInfo info,
            IProgress<(int Percent, string Message)>? progress = null)
        {
            _copyWarnings = 0;
            Directory.CreateDirectory(outDir);

            if (!XboxGdkPackager.IsGdkInstalled())
            {
                throw new Exception(
                    "Microsoft GDK was not found. Install the GDK (sets the GameDK environment variable) " +
                    "and Visual Studio Gaming.* platforms. See Engine.Player.Gdk/README.md. " +
                    "Xbox Live (XSAPI) is not packed into Assets.dll.");
            }

            // Still produce Data/ for future content streaming next to the GDK host.
            var dataDir = Path.Combine(outDir, "Data");
            Directory.CreateDirectory(dataDir);

            Report(progress, 5, "Compiling user scripts (Xbox GDK)...");
            CompilePlayerScripts(dataDir, info.Configuration == "Release");

            Report(progress, 15, "Collecting scene-referenced assets (Xbox GDK)...");
            if (Directory.Exists(proj.AssetsPath))
            {
                var referenced = BuildAssetCollector.CollectForScenes(proj, scenePaths);
                var dllPath = Path.Combine(dataDir, "Assets.dll");
                if (File.Exists(dllPath)) File.Delete(dllPath);
                if (referenced.Count > 0)
                    CreateAssetPak(proj.AssetsPath, dllPath, referenced, info.Compression, progress, 15, 35);
            }

            CopyBuildScenes(dataDir, scenePaths);
            CopyInputBindings(proj, dataDir);
            var sceneOrder = WriteDataManifest(dataDir, scenePaths, info);
            SaveBuildSettings(info, sceneOrder);

            Report(progress, 40, "Building Engine.Player.Gdk (XAudio2 / GameInput / XSAPI)...");
            var result = XboxGdkPackager.GenerateAndBuild(new XboxGdkPackager.Options(
                XboxGdkOutDir: outDir,
                ProjectRootPath: proj.RootPath,
                ProductName: info.ProductName,
                Company: info.Company,
                Version: info.Version,
                Scid: info.GdkScid,
                TitleId: info.GdkTitleId,
                Sandbox: info.GdkSandbox,
                Configuration: info.Configuration,
                IdentityName: info.GcIdentityName,
                DisplayName: info.GcDisplayName,
                PublisherDisplayName: info.GcPublisherDisplay,
                Description: info.GcDescription,
                StoreId: info.GcStoreId,
                MsaAppId: info.GcMsaAppId,
                Logo150: info.GcLogo150,
                Logo44: info.GcLogo44,
                TargetDeviceFamily: info.GcDeviceFamily,
                Progress: progress));

            return result.OutputExePath;
        }

        private string? PerformBuildAndroid(
            Project proj,
            string outDir,
            List<string> scenePaths,
            BuildInfo info,
            IProgress<(int Percent, string Message)>? progress = null)
        {
            _copyWarnings = 0;
            Directory.CreateDirectory(outDir);

            var tempRoot = Path.Combine(Path.GetTempPath(), "EnginePlayerAndroid_" + Guid.NewGuid().ToString("N"));
            var dataDir = Path.Combine(tempRoot, "Data");
            Directory.CreateDirectory(dataDir);

            try
            {
                Report(progress, 5, "Compiling user scripts (Android)...");
                Log.Info("[Build] Step 1/5: Compiling user scripts (Android)...");
                CompilePlayerScripts(dataDir, info.Configuration == "Release");

                Report(progress, 20, "Collecting assets used by build scenes (Android)...");
                Log.Info("[Build] Step 2/5: Packaging scene-referenced assets and scenes (Android)...");
                if (Directory.Exists(proj.AssetsPath))
                {
                    var referenced = BuildAssetCollector.CollectForScenes(proj, scenePaths);
                    var dllPath = Path.Combine(dataDir, "Assets.dll");
                    if (File.Exists(dllPath)) File.Delete(dllPath);
                    if (referenced.Count == 0)
                    {
                        Log.Info("[Build] No external asset files referenced by build scenes — skipping Assets.dll.");
                    }
                    else
                    {
                        CreateAssetPak(proj.AssetsPath, dllPath, referenced, info.Compression, progress, 20, 40);
                        Log.Info($"[Build] Assets.dll created ({new FileInfo(dllPath).Length / 1024.0 / 1024.0:F1} MB) from {referenced.Count} scene-referenced file(s).");
                    }
                }

                CopyBuildScenes(dataDir, scenePaths);
                CopyInputBindings(proj, dataDir);

                Report(progress, 35, "Writing build manifest and Data.zip...");
                var sceneOrder = WriteDataManifest(dataDir, scenePaths, info);
                SaveBuildSettings(info, sceneOrder);

                var zipPath = Path.Combine(tempRoot, "player_data.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(dataDir, zipPath);

                var rid = GetRuntimeIdentifier(info.Platform, info.Architecture);
                var androidCsproj = FindPlayerAndroidCsproj();
                Report(progress, 45, $"Publishing Engine.Player.Android ({rid})...");
                Log.Info($"[Build] Step 3/5: Publishing Engine.Player.Android (net9.0-android35.0, {rid})...");
                RunDotnetPublish(androidCsproj,
                    $"publish \"{androidCsproj}\" -c {info.Configuration} -f net9.0-android35.0 -r {rid} --self-contained " +
                    $"-p:EnginePlayerZipPath=\"{zipPath}\" -o \"{outDir}\"");

                Report(progress, 95, "Locating APK...");
                var apk = FindFirstFile(outDir, "*.apk");
                return apk;
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); }
                catch { /* best-effort */ }
            }
        }

        private void CompilePlayerScripts(string dataDir, bool optimized)
        {
            var scriptRoots = ScriptCompiler.GetProjectScriptRoots().ToList();
            if (scriptRoots.Count == 0)
            {
                Log.Info("[Build] No script roots — skipping script compilation.");
                return;
            }

            var dllOut = Path.Combine(dataDir, "GameScripts.dll");
            var compileResult = ScriptCompiler.CompileToDll(
                scriptRoots, dllOut,
                assemblyName: "GameScripts",
                optimized: optimized,
                playerBuild: true);

            if (!compileResult.Success)
                throw new Exception($"Script compilation failed:\n{compileResult.ErrorText}");

            if (compileResult.FileCount > 0)
                Log.Info($"[Build] Compiled {compileResult.FileCount} script(s) → GameScripts.dll");
            else
                Log.Info("[Build] No user scripts found — skipping script compilation.");
        }

        private void CopyBuildScenes(string dataDir, List<string> scenePaths)
        {
            var scenesOutDir = Path.Combine(dataDir, "Scenes");
            Directory.CreateDirectory(scenesOutDir);
            for (int i = 0; i < scenePaths.Count; i++)
            {
                var src = scenePaths[i];
                if (!File.Exists(src)) continue;
                var dest = Path.Combine(scenesOutDir, Path.GetFileName(src));
                try
                {
                    MakeWritable(dest);
                    using var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                    srcStream.CopyTo(dstStream);
                }
                catch (Exception ex)
                {
                    _copyWarnings++;
                    Log.Warning($"[Build] Could not copy scene '{Path.GetFileName(src)}': {ex.Message}");
                }
            }
        }

        static void CopyInputBindings(Project proj, string dataDir)
        {
            var src = Path.Combine(proj.RootPath, "ProjectSettings", "input.bindings.json");
            if (!File.Exists(src)) return;
            var dstDir = Path.Combine(dataDir, "ProjectSettings");
            Directory.CreateDirectory(dstDir);
            CopyReplacing(src, Path.Combine(dstDir, "input.bindings.json"));
            Log.Info("[Build] Copied input.bindings.json");
        }

        private List<string> WriteDataManifest(string dataDir, List<string> scenePaths, BuildInfo info)
        {
            var sceneOrder = scenePaths.Select(p => Path.GetFileName(p)).ToList();
            var manifest = JsonSerializer.Serialize(new
            {
                product       = info.ProductName,
                company       = info.Company,
                version       = info.Version,
                platform      = info.Platform,
                architecture  = info.Architecture,
                configuration = info.Configuration,
                scenes        = sceneOrder,
                startupScene  = sceneOrder.Count > 0 ? sceneOrder[0] : null,
                resolution = new
                {
                    width      = info.Width,
                    height     = info.Height,
                    fullscreen = info.Fullscreen
                },
                compression = info.Compression,
                builtUtc    = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(Path.Combine(dataDir, "build.json"), manifest);
            return sceneOrder;
        }

        private static void RunDotnetPublish(string playerCsproj, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                var combined = (stdout + "\n" + stderr).Trim();
                var errorLines = combined.Split('\n')
                    .Where(l => l.Contains("error ", StringComparison.OrdinalIgnoreCase))
                    .Take(10);
                var errorSummary = string.Join("\n", errorLines);
                if (string.IsNullOrWhiteSpace(errorSummary))
                    errorSummary = combined.Length > 800 ? combined[..800] + "..." : combined;

                throw new Exception($"dotnet publish failed (exit code {proc.ExitCode}):\n{errorSummary}");
            }

            Log.Info("[Build] dotnet publish completed successfully.");
        }

        private static string? FindFirstFile(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static int _copyWarnings;

        /// <summary>
        /// Create a ZIP archive (Assets.dll) from the given asset files only
        /// (typically scene-referenced). Paths are stored relative to the Assets root.
        /// </summary>
        private void CreateAssetPak(
            string assetsDir,
            string pakPath,
            IReadOnlyList<string> filesToPack,
            string compression,
            IProgress<(int Percent, string Message)>? progress,
            int progressMin,
            int progressMax)
        {
            var basePath = Path.GetFullPath(assetsDir);
            var files = filesToPack
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            long totalBytes = 0;
            foreach (var file in files)
            {
                try { totalBytes += new FileInfo(file).Length; }
                catch { /* skip size probe failures */ }
            }

            var defaultLevel = ResolveZipCompression(compression);
            Log.Info($"[Build] Packaging {files.Count} scene-referenced asset file(s) ({FormatBytes(totalBytes)}) with {defaultLevel}...");

            using var archive = ZipFile.Open(pakPath, ZipArchiveMode.Create);
            long doneBytes = 0;
            var lastReportUtc = DateTime.MinValue;
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var entryName = Path.GetRelativePath(basePath, file).Replace('\\', '/');
                long fileLen = 0;
                try { fileLen = new FileInfo(file).Length; } catch { }

                try
                {
                    using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var level = ShouldStoreUncompressed(entryName) ? CompressionLevel.NoCompression : defaultLevel;
                    var entry = archive.CreateEntry(entryName, level);
                    using var entryStream = entry.Open();
                    srcStream.CopyTo(entryStream);
                }
                catch (Exception ex)
                {
                    _copyWarnings++;
                    Log.Warning($"[Build] Could not pack '{entryName}': {ex.Message}");
                }

                doneBytes += fileLen;
                var now = DateTime.UtcNow;
                var isLast = i == files.Count - 1;
                if (isLast || (now - lastReportUtc).TotalMilliseconds >= 250)
                {
                    lastReportUtc = now;
                    var span = Math.Max(1, progressMax - progressMin);
                    var pct = progressMin;
                    if (totalBytes > 0)
                        pct = progressMin + (int)(span * (double)doneBytes / totalBytes);
                    else if (files.Count > 0)
                        pct = progressMin + (int)(span * (double)(i + 1) / files.Count);
                    pct = Math.Min(progressMax, pct);
                    Report(progress, pct,
                        $"Packaging assets ({i + 1}/{files.Count}, {FormatBytes(doneBytes)} / {FormatBytes(totalBytes)})...");
                }
            }
        }

        private static CompressionLevel ResolveZipCompression(string? compression) =>
            string.Equals(compression, "None", StringComparison.OrdinalIgnoreCase)
                ? CompressionLevel.NoCompression
                : CompressionLevel.Fastest; // LZ4/LZMA UI options map to fast deflate for now

        private static bool ShouldStoreUncompressed(string entryName)
        {
            var ext = Path.GetExtension(entryName);
            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".pak", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".fbx", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".glb", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".basis", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ktx", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ktx2", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F0} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                var dest = Path.Combine(dst, Path.GetFileName(file));
                try
                {
                    // Read with FileShare.ReadWrite so we can copy files the renderer has open
                    using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                    srcStream.CopyTo(dstStream);
                }
                catch (Exception ex)
                {
                    _copyWarnings++;
                    Log.Warning($"[Build] Could not copy '{Path.GetFileName(file)}': {ex.Message}");
                }
            }
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}
