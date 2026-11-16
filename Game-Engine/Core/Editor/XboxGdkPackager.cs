#if !PLAYER
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Detects the Microsoft GDK, stages Engine.Player.Gdk into Builds/XboxGdk/,
/// writes SCID/title identity into MicrosoftGame.config, and invokes MSBuild.
/// </summary>
public static class XboxGdkPackager
{
    public sealed record Options(
        string XboxGdkOutDir,
        string ProjectRootPath,
        string ProductName,
        string Company,
        string Version,
        string Scid,
        string TitleId,
        string Sandbox,
        string Configuration,
        string IdentityName = "",
        string DisplayName = "",
        string PublisherDisplayName = "",
        string Description = "",
        string StoreId = "",
        string MsaAppId = "",
        string Logo150 = "",
        string Logo44 = "",
        string TargetDeviceFamily = "PC",
        IProgress<(int Percent, string Message)>? Progress = null);

    public sealed record Result(string SolutionPath, string VcxprojPath, string OutputExePath);

    public static string? FindGdkRoot()
    {
        var env = Environment.GetEnvironmentVariable("GameDK");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetFullPath(env.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft GDK"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft GDK")
                 })
        {
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
        }

        return null;
    }

    public static bool IsGdkInstalled() => FindGdkRoot() != null;

    public static string FindTemplateRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "Engine.Player.Gdk");
            if (File.Exists(Path.Combine(candidate, "Engine.Player.Gdk.vcxproj")))
                return candidate;
            var sibling = Path.Combine(dir, "..", "Engine.Player.Gdk");
            var siblingFull = Path.GetFullPath(sibling);
            if (File.Exists(Path.Combine(siblingFull, "Engine.Player.Gdk.vcxproj")))
                return siblingFull;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException(
            "Could not find Engine.Player.Gdk/ template next to the editor. " +
            "Ensure Engine.Player.Gdk ships with the solution.");
    }

    public static Result GenerateAndBuild(Options options)
    {
        void Progress(int percent, string message) =>
            options.Progress?.Report((percent, message));

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Xbox GDK packaging must be run on Windows.");

        var gdkRoot = FindGdkRoot()
            ?? throw new InvalidOperationException(
                "Microsoft GDK was not found. Install the GDK (sets GameDK) and the Visual Studio Gaming.* platforms. " +
                "See Engine.Player.Gdk/README.md. Xbox Live (XSAPI) is not packaged into Assets.dll.");

        if (string.IsNullOrWhiteSpace(options.Scid))
            throw new InvalidOperationException(
                "Xbox GDK build requires a Title SCID from Partner Center (Build Settings → Xbox GDK).");

        if (string.IsNullOrWhiteSpace(options.TitleId))
            throw new InvalidOperationException(
                "Xbox GDK build requires a Title Id from Partner Center (Build Settings → Xbox GDK).");

        Progress(10, "Staging Engine.Player.Gdk project...");
        var template = FindTemplateRoot();
        Directory.CreateDirectory(options.XboxGdkOutDir);
        CopyDirectory(template, options.XboxGdkOutDir);

        var configPath = Path.Combine(options.XboxGdkOutDir, "MicrosoftGame.config");
        WriteGameConfig(configPath, options);

        var sandboxPath = Path.Combine(options.XboxGdkOutDir, "gdk.sandbox.txt");
        File.WriteAllText(sandboxPath,
            string.IsNullOrWhiteSpace(options.Sandbox) ? "XDKS.1" : options.Sandbox.Trim());

        Progress(40, "Building Engine.Player.Gdk with MSBuild (x64 + GDK)...");
        var vsInstall = XboxVisualStudioPackager.FindVisualStudioInstallPath()
            ?? throw new InvalidOperationException("Visual Studio 2022 with MSBuild was not found.");
        var msbuild = Path.Combine(vsInstall, "MSBuild", "Current", "Bin", "MSBuild.exe");
        if (!File.Exists(msbuild))
            throw new FileNotFoundException("MSBuild.exe not found.", msbuild);

        var vcxproj = Path.Combine(options.XboxGdkOutDir, "Engine.Player.Gdk.vcxproj");
        var configuration = string.Equals(options.Configuration, "Debug", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";

        RunMsBuild(msbuild, vcxproj, configuration, gdkRoot);

        Progress(90, "Locating Engine.Player.Gdk.exe...");
        var exe = Directory.EnumerateFiles(options.XboxGdkOutDir, "Engine.Player.Gdk.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new Exception("GDK build completed but Engine.Player.Gdk.exe was not found under Builds/XboxGdk.");

        var sln = Path.Combine(options.XboxGdkOutDir, "Engine.Player.Gdk.sln");
        File.WriteAllText(Path.Combine(options.XboxGdkOutDir, "README.txt"),
            "Xbox GDK build output\r\n" +
            "=====================\r\n\r\n" +
            $"GDK root: {gdkRoot}\r\n" +
            $"SCID: {options.Scid}\r\n" +
            $"TitleId: {options.TitleId}\r\n" +
            $"Sandbox: {(string.IsNullOrWhiteSpace(options.Sandbox) ? "XDKS.1" : options.Sandbox)}\r\n" +
            $"Exe: {exe}\r\n\r\n" +
            "Run example:\r\n" +
            $"  \"{exe}\" --scid={options.Scid} --test-tone=1\r\n\r\n" +
            "Package with GDK MakePkg / wdapp — not the Avalonia MSIX packager.\r\n");

        Progress(100, "GDK host build finished.");
        return new Result(sln, vcxproj, exe);
    }

    public static string? FindExistingSolution(string xboxGdkOutDir)
    {
        var sln = Path.Combine(xboxGdkOutDir, "Engine.Player.Gdk.sln");
        return File.Exists(sln) ? sln : null;
    }

    private static void WriteGameConfig(string path, Options options)
    {
        var fallbackName = XboxVisualStudioPackager.SanitizePackageName(options.ProductName, options.ProjectRootPath);
        var name = string.IsNullOrWhiteSpace(options.IdentityName)
            ? fallbackName
            : SanitizeIdentityName(options.IdentityName.Trim());
        var publisher = XboxVisualStudioPackager.FormatPublisher(options.Company);
        var version = XboxVisualStudioPackager.PadPackageVersion(options.Version);
        var display = FirstNonEmpty(options.DisplayName, options.ProductName, "Engine Player GDK");
        var publisherDisplay = FirstNonEmpty(options.PublisherDisplayName, options.Company, "Game Engine");
        var description = FirstNonEmpty(options.Description, "GDK host for Xbox Live / XAudio2 / GameInput");
        var logo150 = FirstNonEmpty(options.Logo150, @"Assets\StoreLogo.png");
        var logo44 = FirstNonEmpty(options.Logo44, @"Assets\StoreLogo.png");
        var deviceFamily = FirstNonEmpty(options.TargetDeviceFamily, "PC");
        var titleId = options.TitleId.Trim();
        if (titleId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            titleId = titleId[2..];
        var storeId = options.StoreId.Trim();
        var msaAppId = options.MsaAppId.Trim();

        var storeIdXml = string.IsNullOrWhiteSpace(storeId) ? "" : $"\n  <StoreId>{EscapeXml(storeId)}</StoreId>";
        var msaXml = string.IsNullOrWhiteSpace(msaAppId) ? "" : $"\n  <MSAAppId>{EscapeXml(msaAppId)}</MSAAppId>";

        var xml =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<Game configVersion=""1"">
  <Identity Name=""{EscapeXml(name)}""
            Version=""{EscapeXml(version)}""
            Publisher=""{EscapeXml(publisher)}"" />
  <ExecutableList>
    <Executable Name=""Engine.Player.Gdk.exe""
                Id=""Game""
                TargetDeviceFamily=""{EscapeXml(deviceFamily)}"" />
  </ExecutableList>
  <ShellVisuals DefaultDisplayName=""{EscapeXml(display)}""
                PublisherDisplayName=""{EscapeXml(publisherDisplay)}""
                Square150x150Logo=""{EscapeXml(logo150)}""
                Square44x44Logo=""{EscapeXml(logo44)}""
                Description=""{EscapeXml(description)}"" />{msaXml}
  <TitleId>{EscapeXml(titleId)}</TitleId>{storeIdXml}
  <!-- SCID for XSAPI (also passed on the command line as --scid=) -->
  <ExtendedAttributeList>
    <ExtendedAttribute Name=""Scid"" Value=""{EscapeXml(options.Scid.Trim())}"" />
    <ExtendedAttribute Name=""Sandbox"" Value=""{EscapeXml(string.IsNullOrWhiteSpace(options.Sandbox) ? "XDKS.1" : options.Sandbox.Trim())}"" />
  </ExtendedAttributeList>
</Game>
";
        File.WriteAllText(path, xml.TrimStart() + Environment.NewLine);
        try { XDocument.Load(path); } catch { /* best-effort validate */ }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return "";
    }

    private static string SanitizeIdentityName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('.');
        }
        var s = sb.ToString().Trim('.');
        if (string.IsNullOrEmpty(s)) return "GameEngine.Title";
        if (!char.IsLetter(s[0])) s = "G." + s;
        return s;
    }

    private static void RunMsBuild(string msbuildPath, string vcxprojPath, string configuration, string gdkRoot)
    {
        var args =
            $"\"{vcxprojPath}\" " +
            $"/p:Configuration={configuration} " +
            "/p:Platform=x64 " +
            $"/p:GameDK=\"{gdkRoot.TrimEnd('\\', '/').Replace('\\', '/')}/\" " +
            "/verbosity:minimal";

        var psi = new ProcessStartInfo
        {
            FileName = msbuildPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(vcxprojPath)!
        };
        psi.Environment["GameDK"] = gdkRoot;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var combined = (stdout + "\n" + stderr).Trim();
            var errorLines = combined.Split('\n')
                .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                .Take(12);
            var summary = string.Join("\n", errorLines);
            if (string.IsNullOrWhiteSpace(summary))
                summary = combined.Length > 1200 ? combined[..1200] + "..." : combined;
            throw new Exception($"GDK MSBuild failed (exit {proc.ExitCode}):\n{summary}");
        }

        Log.Info("[Build] Engine.Player.Gdk MSBuild completed.");
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            // Skip build junk if re-staging over an old tree
            var name = Path.GetFileName(file);
            if (name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dst, name), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".vs") continue;
            CopyDirectory(dir, Path.Combine(dst, name));
        }
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}
#endif
