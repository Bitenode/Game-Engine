#if !PLAYER
using System;
using System.Collections.Generic;
using System.IO;
using Game_Engine.Core;

namespace Game_Engine.Core.Editor;

/// <summary>Copies shipped Standard Assets from the editor install directory into a project.</summary>
public static class StandardAssetsInstaller
{
    /// <summary>Project-relative folder for shipped templates (under <c>Assets</c>).</summary>
    public const string ProjectStandardAssetsRelative = "Standard Assets";

    /// <summary>Full path to Standard Assets inside a project (Assets/Standard Assets).</summary>
    public static string ProjectStandardAssetsDirectory(Project proj) =>
        Path.Combine(proj.AssetsPath, ProjectStandardAssetsRelative);

    /// <summary>
    /// Copies <c>Standard Assets</c> from next to the editor (see <see cref="ResolveStandardAssetsSourceDirectory"/>)
    /// into <paramref name="projectRoot"/>/Assets/Standard Assets.
    /// </summary>
    /// <returns>false if the source tree is missing; <paramref name="error"/> describes the failure.</returns>
    public static bool TryCopyToProject(string projectRoot, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            error = "Project root is empty.";
            return false;
        }

        var src = ResolveStandardAssetsSourceDirectory();
        if (string.IsNullOrEmpty(src))
        {
            error =
                "Standard Assets folder was not found next to the editor. " +
                "Rebuild the editor so the post-build step copies Standard Assets to the output folder, " +
                "or run Game_Engine.exe from its build directory (bin/Debug or bin/Release).";
            return false;
        }

        projectRoot = Path.GetFullPath(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var assetsDir = Path.Combine(projectRoot, "Assets");
        var dest = Path.Combine(assetsDir, ProjectStandardAssetsRelative);
        try
        {
            Directory.CreateDirectory(assetsDir);
            Directory.CreateDirectory(dest);
            CopyDirectoryRecursive(src, dest);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        // CreateNew/Open already fired ProjectOpened/Changed before copy; refresh panels (Project tree, etc.).
        ProjectService.NotifyProjectFilesystemChanged();
        return true;
    }

    /// <summary>
    /// First directory under a candidate base that exists and contains at least one file (any depth).
    /// </summary>
    public static string? ResolveStandardAssetsSourceDirectory()
    {
        foreach (var baseDir in GetCandidateEditorDirectories())
        {
            var p = Path.Combine(baseDir, "Standard Assets");
            if (!Directory.Exists(p)) continue;
            try
            {
                if (Directory.GetFiles(p, "*", SearchOption.AllDirectories).Length > 0)
                    return p;
            }
            catch
            {
                /* ignore unreadable tree */
            }
        }

        return null;
    }

    static IEnumerable<string> GetCandidateEditorDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> YieldUnique(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) yield break;
            var full = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!seen.Add(full)) yield break;
            yield return full;
        }

        foreach (var d in YieldUnique(AppContext.BaseDirectory)) yield return d;

        var asmPath = typeof(StandardAssetsInstaller).Assembly.Location;
        foreach (var d in YieldUnique(Path.GetDirectoryName(asmPath))) yield return d;
    }

    static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(destDir, name);
            Directory.CreateDirectory(destDir);
            File.Copy(file, target, overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) continue;
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name));
        }
    }
}
#endif
