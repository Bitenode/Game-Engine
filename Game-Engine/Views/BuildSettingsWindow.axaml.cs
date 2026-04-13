using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core;
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

            // Update output path when platform changes
            PlatformBox.SelectionChanged += (_, __) => RefreshOutputPath();

            ProjectRenderingSettings.Load(ProjectService.Current);
            DeferredRenderingCheck.IsChecked = ProjectRenderingSettings.UseDeferredRendering;
            DeferredRenderingCheck.IsCheckedChanged += (_, __) =>
            {
                if (DeferredRenderingCheck.IsChecked is bool b)
                    ProjectRenderingSettings.Save(ProjectService.Current, b);
            };

            BtnBuild.Click       += OnBuildClicked;
            BtnBuildAndRun.Click += OnBuildAndRunClicked;
            BtnCancel.Click      += (_, __) => Close();
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
                    builtUtc      = DateTime.UtcNow
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
            BuildProgress.IsVisible = true;
            BuildProgress.IsIndeterminate = true;

            var sceneCount = _buildScenes.Count;
            StatusText.Text = $"Building {SelectedConfiguration} for {SelectedPlatform} ({SelectedArchitecture}) — {sceneCount} scene{(sceneCount != 1 ? "s" : "")}...";
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
                    Fullscreen    = FullscreenCheck.IsChecked == true
                };

                string? exePath = null;
                await Task.Run(() => exePath = PerformBuild(proj, outDir, scenePaths, buildInfo));

                BuildProgress.IsIndeterminate = false;
                BuildProgress.Value = 100;

                if (_copyWarnings > 0)
                {
                    StatusText.Text = $"Build succeeded with {_copyWarnings} warning(s) — {outDir}";
                    Log.Warning($"[Build] Build succeeded with {_copyWarnings} file(s) skipped — output: {outDir}");
                }
                else
                {
                    StatusText.Text = $"Build succeeded — {outDir}";
                    Log.Info($"[Build] Build succeeded — output: {outDir}");
                }

                if (launch && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    StatusText.Text += " — Launching...";
                    Log.Info($"[Build] Launching {exePath}");
                    try
                    {
                        if (IsWindowsAppPackagePath(exePath))
                        {
                            var escaped = exePath.Replace("'", "''");
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "powershell.exe",
                                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{escaped}'\"",
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
                BuildProgress.IsIndeterminate = false;
                BuildProgress.Value = 0;
                StatusText.Text = $"Build failed: {ex.Message}";
                Log.Warning($"[Build] Build failed: {ex.Message}");
            }

            // Unlock UI
            BtnBuild.IsEnabled = true;
            BtnBuildAndRun.IsEnabled = true;
        }

        /// <summary>All UI values captured on the UI thread before Task.Run.</summary>
        private struct BuildInfo
        {
            public string ProductName, Company, Version;
            public string Platform, Architecture, Configuration, Compression;
            public int Width, Height;
            public bool Fullscreen;
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
                _                  => $"win-{arch}"   // Windows (unpackaged)
            };
        }

        private static string GetTargetFramework(string platform) =>
            platform switch
            {
                "Xbox"           => "net9.0-windows10.0.19041.0",
                "Android"        => "net9.0-android",
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
        private string? PerformBuild(Project proj, string outDir, List<string> scenePaths, BuildInfo info)
        {
            if (string.Equals(info.Platform, "Xbox", StringComparison.OrdinalIgnoreCase)
                && !OperatingSystem.IsWindows())
                throw new Exception("Xbox packaging must be run on Windows.");

            if (string.Equals(info.Platform, "Android", StringComparison.OrdinalIgnoreCase))
                return PerformBuildAndroid(proj, outDir, scenePaths, info);

            _copyWarnings = 0;
            Directory.CreateDirectory(outDir);

            var rid = GetRuntimeIdentifier(info.Platform, info.Architecture);
            var tfm = GetTargetFramework(info.Platform);
            var dataDir = Path.Combine(outDir, "Data");
            Directory.CreateDirectory(dataDir);

            // ── Step 1: Compile user scripts ──
            Log.Info("[Build] Step 1/5: Compiling user scripts...");
            CompilePlayerScripts(dataDir, info.Configuration == "Release");

            // ── Step 2: dotnet publish Engine.Player ──
            Log.Info($"[Build] Step 2/5: Publishing Engine.Player ({tfm}, {rid}, {info.Configuration})...");
            var playerCsproj = FindPlayerCsproj();
            RunDotnetPublish(playerCsproj,
                $"publish \"{playerCsproj}\" -c {info.Configuration} -f {tfm} -r {rid} --self-contained -o \"{outDir}\"");

            // ── Step 3: Package assets into Data/Assets.dll ──
            Log.Info("[Build] Step 3/5: Packaging assets into Assets.dll...");
            if (Directory.Exists(proj.AssetsPath))
            {
                var dllPath = Path.Combine(dataDir, "Assets.dll");
                if (File.Exists(dllPath)) File.Delete(dllPath);
                CreateAssetPak(proj.AssetsPath, dllPath);
                Log.Info($"[Build] Assets.dll created ({new FileInfo(dllPath).Length / 1024} KB)");
            }

            Log.Info("[Build] Step 4/5: Copying scenes...");
            CopyBuildScenes(dataDir, scenePaths);

            Log.Info("[Build] Step 5/5: Writing build manifest...");
            var sceneOrder = WriteDataManifest(dataDir, scenePaths, info);
            SaveBuildSettings(info, sceneOrder);

            if (string.Equals(info.Platform, "Xbox", StringComparison.OrdinalIgnoreCase))
            {
                var xboxPackagePath = FindFirstFile(outDir, "*.msix")
                    ?? FindFirstFile(outDir, "*.msixbundle")
                    ?? FindFirstFile(outDir, "*.appx");
                if (xboxPackagePath != null)
                    return xboxPackagePath;
                Log.Info("[Build] No Windows app package in output — using unpackaged Engine.Player.exe. For retail Xbox, ship with Xbox GDK; see Package.appxmanifest for Desktop + Xbox device families.");
            }

            string exeName = rid.StartsWith("win") ? "Engine.Player.exe" : "Engine.Player";
            var exePath = Path.Combine(outDir, exeName);
            return File.Exists(exePath) ? exePath : null;
        }

        private string? PerformBuildAndroid(Project proj, string outDir, List<string> scenePaths, BuildInfo info)
        {
            _copyWarnings = 0;
            Directory.CreateDirectory(outDir);

            var tempRoot = Path.Combine(Path.GetTempPath(), "EnginePlayerAndroid_" + Guid.NewGuid().ToString("N"));
            var dataDir = Path.Combine(tempRoot, "Data");
            Directory.CreateDirectory(dataDir);

            try
            {
                Log.Info("[Build] Step 1/5: Compiling user scripts (Android)...");
                CompilePlayerScripts(dataDir, info.Configuration == "Release");

                Log.Info("[Build] Step 2/5: Packaging assets and scenes (Android)...");
                if (Directory.Exists(proj.AssetsPath))
                {
                    var dllPath = Path.Combine(dataDir, "Assets.dll");
                    if (File.Exists(dllPath)) File.Delete(dllPath);
                    CreateAssetPak(proj.AssetsPath, dllPath);
                    Log.Info($"[Build] Assets.dll created ({new FileInfo(dllPath).Length / 1024} KB)");
                }

                CopyBuildScenes(dataDir, scenePaths);

                var sceneOrder = WriteDataManifest(dataDir, scenePaths, info);
                SaveBuildSettings(info, sceneOrder);

                var zipPath = Path.Combine(tempRoot, "player_data.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(dataDir, zipPath);

                var rid = GetRuntimeIdentifier(info.Platform, info.Architecture);
                var androidCsproj = FindPlayerAndroidCsproj();
                Log.Info($"[Build] Step 3/5: Publishing Engine.Player.Android (net9.0-android, {rid})...");
                RunDotnetPublish(androidCsproj,
                    $"publish \"{androidCsproj}\" -c {info.Configuration} -f net9.0-android -r {rid} --self-contained " +
                    $"-p:EnginePlayerZipPath=\"{zipPath}\" -o \"{outDir}\"");

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
                optimized: optimized);

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
        /// Create a ZIP archive (Assets.dll) from the project's Assets folder.
        /// Files are stored with paths relative to the Assets folder root
        /// (e.g., "fbx/textures/Box_D.jpg"). The player extracts them to a
        /// temp directory at runtime so all file-based loading works normally.
        /// </summary>
        private void CreateAssetPak(string assetsDir, string pakPath)
        {
            using var archive = ZipFile.Open(pakPath, ZipArchiveMode.Create);
            var basePath = Path.GetFullPath(assetsDir);

            foreach (var file in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories))
            {
                // Relative entry name: "fbx/textures/Box_D.jpg"
                var entryName = Path.GetRelativePath(basePath, file).Replace('\\', '/');
                try
                {
                    using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    srcStream.CopyTo(entryStream);
                }
                catch (Exception ex)
                {
                    _copyWarnings++;
                    Log.Warning($"[Build] Could not pack '{entryName}': {ex.Message}");
                }
            }
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
