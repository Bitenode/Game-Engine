using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Game_Engine.Core;
using Game_Engine.Core.Editor;

namespace Game_Engine.Views;

/// <summary>Add-on panel: lists the largest files under <c>Assets/</c> (see <see cref="AssetLargestFilesEditorExtension"/>).</summary>
public sealed class LargestFilesPanel : UserControl
{
    public ObservableCollection<string> Lines { get; } = new();

    public LargestFilesPanel()
    {
        var scan = new Button { Content = "Scan Assets folder", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(12, 12, 12, 8) };
        var hint = new TextBlock
        {
            Text = "CPU-bound scan uses EditorJobs; results apply on the UI thread.",
            Margin = new Avalonia.Thickness(12, 0, 12, 8),
            FontSize = 11,
            Opacity = 0.85
        };
        var list = new ListBox { ItemsSource = Lines, Margin = new Avalonia.Thickness(8, 0, 8, 8) };

        scan.Click += async (_, __) => await RunScanAsync();

        var root = new StackPanel { Children = { scan, hint, list } };
        Content = root;
    }

    async System.Threading.Tasks.Task RunScanAsync()
    {
        var proj = ProjectService.Current;
        if (proj is null)
        {
            Log.Warning("[LargestAssets] No project open.");
            return;
        }

        var assets = proj.AssetsPath;
        if (!Directory.Exists(assets))
        {
            Log.Warning("[LargestAssets] Assets folder missing: " + assets);
            return;
        }

        var rootPath = proj.RootPath;
        List<(long Length, string Relative)> rows;
        try
        {
            rows = await EditorJobs.RunCpuAsync(ct =>
            {
                var acc = new List<(long, string)>();
                foreach (var path in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var len = new FileInfo(path).Length;
                        var rel = Path.GetRelativePath(rootPath, path);
                        acc.Add((len, rel));
                    }
                    catch
                    {
                        // skip locked or unreadable files
                    }
                }

                return acc
                    .OrderByDescending(x => x.Item1)
                    .Take(100)
                    .ToList();
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"[LargestAssets] Scan failed: {ex.Message}");
            return;
        }

        await EditorJobs.InvokeOnUiAsync(() =>
        {
            Lines.Clear();
            foreach (var (len, rel) in rows)
                Lines.Add($"{FormatSize(len),-12}  {rel}");
            Log.Info($"[LargestAssets] Listed top {Lines.Count} file(s) by size.");
        });
    }

    static string FormatSize(long bytes)
    {
        const double k = 1024;
        if (bytes < k) return $"{bytes} B";
        double v = bytes;
        string[] suf = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (v >= k && i < suf.Length - 1)
        {
            v /= k;
            i++;
        }

        return $"{v:0.##} {suf[i]}";
    }
}
