#if !PLAYER
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Stages <c>Engine.Player.XboxUwp</c> (real UWP, no FullTrust) into Builds/Xbox for
/// Visual Studio Remote Machine deploy to Xbox Dev Mode.
/// </summary>
public static class XboxVisualStudioPackager
{
    private const string DevCertFileName = "XboxDev.pfx";
    private const string DevCertPublisherFileName = "XboxDev.publisher.txt";
    private const string DevCertPassword = "xboxdev";
    private static readonly Guid UwpProjectGuid = new("B2C3D4E5-F6A7-8901-BCDE-F12345678901");

    public sealed record Options(
        string XboxOutDir,
        string DataDir,
        string ProjectRootPath,
        string ProductName,
        string Company,
        string Version,
        string? IdentityName = null,
        string? DisplayName = null,
        string? PublisherDisplayName = null,
        string? Description = null,
        string? RemoteMachine = null,
        string Configuration = "Release",
        IProgress<(int Percent, string Message)>? Progress = null);

    public sealed record Result(
        string SolutionPath,
        string ProjectPath,
        string? MsixPath,
        string CertificatePath,
        string VisualStudioDir);

    public static Result GenerateAndBuild(Options options)
    {
        void Progress(int percent, string message) =>
            options.Progress?.Report((percent, message));

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Xbox packaging must be run on Windows.");

        if (!Directory.Exists(options.DataDir))
            throw new DirectoryNotFoundException($"Xbox Data folder not found: {options.DataDir}");

        var vsInstall = FindVisualStudioInstallPath()
            ?? throw new InvalidOperationException(
                "Visual Studio 2022 with MSBuild was not found. Install VS 2022 with the UWP / Windows workload, then rebuild.");

        var msbuild = Path.Combine(vsInstall, "MSBuild", "Current", "Bin", "MSBuild.exe");
        if (!File.Exists(msbuild))
            throw new FileNotFoundException("MSBuild.exe was not found next to the Visual Studio install.", msbuild);

        var packageName = string.IsNullOrWhiteSpace(options.IdentityName)
            ? SanitizePackageName(options.ProductName, options.ProjectRootPath)
            : SanitizePackageName(options.IdentityName, options.ProjectRootPath);
        var publisher = FormatPublisher(options.Company);
        var version = PadPackageVersion(options.Version);
        var displayName = FirstNonEmpty(options.DisplayName, options.ProductName,
            Path.GetFileName(options.ProjectRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var publisherDisplay = FirstNonEmpty(options.PublisherDisplayName, options.Company, "Game Engine");
        var description = FirstNonEmpty(options.Description,
            "Xbox Dev Mode UWP host for Game Engine titles");

        var vsDir = Path.Combine(options.XboxOutDir, "VisualStudio");
        Progress(80, "Staging Engine.Player.XboxUwp (UWP, no FullTrust)...");
        if (Directory.Exists(vsDir))
        {
            try { Directory.Delete(vsDir, recursive: true); }
            catch (Exception ex)
            {
                Log.Warning($"[Build] Could not clear previous VisualStudio folder: {ex.Message}");
            }
        }

        var template = FindXboxUwpTemplateRoot();
        CopyDirectory(template, vsDir);

        // Embed game Data into the UWP package (scenes, build.json, Assets.dll).
        var stagedData = Path.Combine(vsDir, "Data");
        if (Directory.Exists(stagedData))
            Directory.Delete(stagedData, recursive: true);
        CopyDirectory(options.DataDir, stagedData);

        var projectSettingsDir = Path.Combine(options.ProjectRootPath, "ProjectSettings");
        Directory.CreateDirectory(projectSettingsDir);
        var certPath = EnsureDevCertificate(projectSettingsDir, publisher);
        var certInVsDir = Path.Combine(vsDir, DevCertFileName);
        File.Copy(certPath, certInVsDir, overwrite: true);

        var csprojPath = Path.Combine(vsDir, "Engine.Player.XboxUwp.csproj");
        var solutionPath = Path.Combine(vsDir, $"{packageName}.sln");
        var manifestPath = Path.Combine(vsDir, "Package.appxmanifest");

        WriteUwpAppxManifest(manifestPath, packageName, publisher, version, displayName, publisherDisplay, description);
        var coreDir = Path.GetFullPath(Path.Combine(template, "..", "Game-Engine", "Core"));
        PatchCsprojCoreDir(csprojPath, coreDir);
        PatchCsprojSigning(csprojPath, DevCertFileName);
        WriteLaunchSettings(Path.Combine(vsDir, "Properties", "launchSettings.json"), options.RemoteMachine);
        WriteUwpSolution(solutionPath, packageName);

        Progress(88, "Building UWP package with MSBuild...");
        var configuration = string.Equals(options.Configuration, "Debug", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        RunMsBuildUwp(msbuild, csprojPath, configuration, certInVsDir);

        Progress(96, "Locating packaged MSIX...");
        var msix = Directory.EnumerateFiles(vsDir, "*.msix", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        WriteXboxReadme(options.XboxOutDir, packageName, solutionPath, msix);
        Log.Info($"[Build] Xbox UWP project ready: {solutionPath}");
        if (msix != null)
            Log.Info($"[Build] MSIX created: {msix}");

        return new Result(solutionPath, csprojPath, msix, certPath, vsDir);
    }

    private static void WriteXboxReadme(string xboxOutDir, string packageName, string solutionPath, string? msixPath)
    {
        File.WriteAllText(Path.Combine(xboxOutDir, "README.txt"),
            "Xbox Dev Mode UWP build\r\n" +
            "=======================\r\n\r\n" +
            $"Package identity: {packageName}\r\n" +
            $"Solution: {solutionPath}\r\n" +
            (msixPath != null ? $"MSIX: {msixPath}\r\n" : "") +
            "\r\n" +
            "This is a real UWP app (EntryPoint = Engine.Player.XboxUwp.App).\r\n" +
            "It does NOT use Windows.FullTrustApplication / runFullTrust — Xbox will launch it.\r\n\r\n" +
            "Deploy to console:\r\n" +
            "  1. Open the .sln in Visual Studio 2022\r\n" +
            "  2. Platform x64\r\n" +
            "  3. In the debug toolbar profile dropdown, select \"Remote Machine\"\r\n" +
            "     (not the old Debug property page — this is a modern single-project MSIX UWP app)\r\n" +
            "  4. Sign in a Microsoft account on the Xbox (Dev Home)\r\n" +
            "  5. F5 / Start Debugging\r\n\r\n" +
            "Set the Xbox IP in Build Settings → Remote Machine (Dev Home shows it).\r\n\r\n" +
            "If something is missing in the game, read LocalState\\player.log. It lists every asset\r\n" +
            "or script that failed to load. On the Xbox: Device Portal → File explorer → the app's\r\n" +
            "LocalState. On this PC: %LOCALAPPDATA%\\Packages\\<package family>\\LocalState.\r\n" +
            "The first launch compiles shaders and is slow; later launches reuse LocalCache\\Shaders.\r\n");
    }

    public static string? FindExistingSolution(string xboxOutDir)
    {
        var vsDir = Path.Combine(xboxOutDir, "VisualStudio");
        if (!Directory.Exists(vsDir)) return null;
        try
        {
            return Directory.EnumerateFiles(vsDir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static string FindXboxUwpTemplateRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "Engine.Player.XboxUwp");
            if (File.Exists(Path.Combine(candidate, "Engine.Player.XboxUwp.csproj")))
                return candidate;
            var sibling = Path.GetFullPath(Path.Combine(dir, "..", "Engine.Player.XboxUwp"));
            if (File.Exists(Path.Combine(sibling, "Engine.Player.XboxUwp.csproj")))
                return sibling;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException(
            "Could not find Engine.Player.XboxUwp/ next to the editor. " +
            "Ensure Engine.Player.XboxUwp ships with the solution.");
    }

    public static string? FindVisualStudioInstallPath()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-latest -products * -requires Microsoft.Component.MSBuild -property installationPath",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;
            var path = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public static void OpenInVisualStudio(string solutionPath)
    {
        if (!File.Exists(solutionPath))
            throw new FileNotFoundException("Generated Visual Studio solution not found.", solutionPath);

        var vsInstall = FindVisualStudioInstallPath()
            ?? throw new InvalidOperationException("Visual Studio 2022 was not found.");
        var devenv = Path.Combine(vsInstall, "Common7", "IDE", "devenv.exe");
        if (!File.Exists(devenv))
            throw new FileNotFoundException("devenv.exe was not found.", devenv);

        Process.Start(new ProcessStartInfo
        {
            FileName = devenv,
            Arguments = $"\"{solutionPath}\"",
            UseShellExecute = false
        });
    }

    public static void TrustCertificate(string pfxPath)
    {
        if (!File.Exists(pfxPath)) return;

        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath,
                DevCertPassword,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
            Log.Info("[Build] Imported Xbox packaging certificate into CurrentUser\\TrustedPeople.");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Build] Could not import packaging certificate: {ex.Message}");
        }
    }

    public static string SanitizePackageName(string? productName, string projectRootPath)
    {
        var raw = string.IsNullOrWhiteSpace(productName)
            ? Path.GetFileName(projectRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : productName.Trim();

        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '_' or '/')
                sb.Append('.');
        }

        var name = Regex.Replace(sb.ToString(), @"\.+", ".").Trim('.');
        if (string.IsNullOrEmpty(name))
            name = "GameEngine.Game";

        if (!char.IsLetter(name[0]))
            name = "App." + name;

        if (name.Length < 3)
            name = name.PadRight(3, 'X');
        if (name.Length > 50)
            name = name[..50].TrimEnd('.');

        return name;
    }

    public static string FormatPublisher(string? company)
    {
        var cn = string.IsNullOrWhiteSpace(company) ? "GameEngine" : company.Trim();
        cn = cn.Replace("\"", "").Replace(",", " ");
        if (string.IsNullOrWhiteSpace(company))
            Log.Info("[Build] Company is empty — using Publisher CN=GameEngine for the Xbox package.");
        return "CN=" + cn;
    }

    public static string PadPackageVersion(string? version)
    {
        var parts = (version ?? "1.0.0").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nums = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (i < parts.Length && int.TryParse(parts[i], out var n) && n >= 0)
                nums[i] = n;
        }
        return $"{nums[0]}.{nums[1]}.{nums[2]}.{nums[3]}";
    }

    private static string EnsureDevCertificate(string projectSettingsDir, string publisher)
    {
        var pfxPath = Path.Combine(projectSettingsDir, DevCertFileName);
        var publisherPath = Path.Combine(projectSettingsDir, DevCertPublisherFileName);

        if (File.Exists(pfxPath) && File.Exists(publisherPath))
        {
            var previous = File.ReadAllText(publisherPath).Trim();
            if (string.Equals(previous, publisher, StringComparison.Ordinal))
                return pfxPath;
            Log.Info("[Build] Xbox package publisher changed — regenerating ProjectSettings/XboxDev.pfx.");
        }

        CreateSelfSignedPfx(pfxPath, publisher);
        File.WriteAllText(publisherPath, publisher);
        Log.Info($"[Build] Created packaging certificate: {pfxPath} ({publisher})");
        return pfxPath;
    }

    private static void CreateSelfSignedPfx(string pfxPath, string publisher)
    {
        var escapedPublisher = publisher.Replace("'", "''");
        var escapedPfx = pfxPath.Replace("'", "''");
        var script =
            "$ErrorActionPreference = 'Stop'\r\n" +
            $"$cert = New-SelfSignedCertificate -Type Custom -Subject '{escapedPublisher}' " +
            "-KeyUsage DigitalSignature -FriendlyName 'GameEngine Xbox Dev' " +
            "-CertStoreLocation 'Cert:\\CurrentUser\\My' " +
            "-TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')\r\n" +
            $"$pwd = ConvertTo-SecureString -String '{DevCertPassword}' -Force -AsPlainText\r\n" +
            $"Export-PfxCertificate -Cert $cert -FilePath '{escapedPfx}' -Password $pwd | Out-Null\r\n" +
            "Remove-Item -LiteralPath $cert.PSPath -Force\r\n";

        var scriptPath = Path.Combine(Path.GetTempPath(), "GameEngine_XboxDevCert_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(scriptPath, script);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || !File.Exists(pfxPath))
                throw new Exception($"Failed to create XboxDev.pfx:\n{(stderr + "\n" + stdout).Trim()}");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    private static void WriteUwpAppxManifest(
        string path,
        string packageName,
        string publisher,
        string version,
        string displayName,
        string publisherDisplay,
        string description)
    {
        var phoneId = Guid.NewGuid().ToString();
        var xml =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:mp=""http://schemas.microsoft.com/appx/2014/phone/manifest""
  xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
  IgnorableNamespaces=""uap mp"">

  <Identity
    Name=""{EscapeXml(packageName)}""
    Publisher=""{EscapeXml(publisher)}""
    Version=""{EscapeXml(version)}"" />

  <mp:PhoneIdentity PhoneProductId=""{phoneId}"" PhonePublisherId=""00000000-0000-0000-0000-000000000000""/>

  <Properties>
    <DisplayName>{EscapeXml(displayName)}</DisplayName>
    <PublisherDisplayName>{EscapeXml(publisherDisplay)}</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.17763.0"" MaxVersionTested=""10.0.26100.0"" />
    <TargetDeviceFamily Name=""Windows.Xbox"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>

  <Resources>
    <Resource Language=""en-us""/>
  </Resources>

  <Applications>
    <Application Id=""App""
          Executable=""Engine.Player.XboxUwp.exe""
      EntryPoint=""Engine.Player.XboxUwp.App"">
      <uap:VisualElements
        DisplayName=""{EscapeXml(displayName)}""
        Square150x150Logo=""Assets\Square150x150Logo.png""
        Square44x44Logo=""Assets\Square44x44Logo.png""
        Description=""{EscapeXml(description)}""
        BackgroundColor=""#000000"">
        <uap:DefaultTile Wide310x150Logo=""Assets\Wide310x150Logo.png""/>
        <uap:SplashScreen Image=""Assets\SplashScreen.png""/>
      </uap:VisualElements>
    </Application>
  </Applications>

  <Capabilities>
    <Capability Name=""internetClient"" />
    <uap:Capability Name=""userAccountInformation"" />
  </Capabilities>
</Package>
";
        File.WriteAllText(path, xml.TrimStart() + Environment.NewLine);
    }

    private static void PatchCsprojCoreDir(string csprojPath, string coreDir)
    {
        if (!File.Exists(csprojPath)) return;
        if (!Directory.Exists(coreDir))
            throw new DirectoryNotFoundException("Engine Core folder not found for the staged Xbox project: " + coreDir);

        var full = coreDir.TrimEnd('\\', '/') + "\\";
        var uiDir = Path.GetFullPath(Path.Combine(coreDir, "..", "Standard Assets", "Code Examples", "UI")) + "\\";
        var text = File.ReadAllText(csprojPath);
        var element = $"<GameEngineCoreDir>{EscapeXml(full)}</GameEngineCoreDir>";
        var uiElement = $"<GameEngineStdUiDir>{EscapeXml(uiDir)}</GameEngineStdUiDir>";
        if (text.Contains("<GameEngineCoreDir>", StringComparison.Ordinal))
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"<GameEngineCoreDir[^>]*>.*?</GameEngineCoreDir>",
                element,
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }
        else
        {
            text = text.Replace(
                "<RootNamespace>",
                element + "\r\n    <RootNamespace>",
                StringComparison.Ordinal);
        }

        if (text.Contains("<GameEngineStdUiDir>", StringComparison.Ordinal))
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"<GameEngineStdUiDir[^>]*>.*?</GameEngineStdUiDir>",
                uiElement,
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }
        else
        {
            text = text.Replace(
                "<RootNamespace>",
                uiElement + "\r\n    <RootNamespace>",
                StringComparison.Ordinal);
        }
        File.WriteAllText(csprojPath, text);
    }

    private static void PatchCsprojSigning(string csprojPath, string certFileName)
    {
        if (!File.Exists(csprojPath)) return;
        var text = File.ReadAllText(csprojPath);
        if (!text.Contains("<PackageCertificateKeyFile>", StringComparison.Ordinal))
        {
            text = text.Replace(
                "<AppxPackageSigningEnabled>true</AppxPackageSigningEnabled>",
                "<AppxPackageSigningEnabled>true</AppxPackageSigningEnabled>\r\n" +
                $"    <PackageCertificateKeyFile>{EscapeXml(certFileName)}</PackageCertificateKeyFile>\r\n" +
                $"    <PackageCertificatePassword>{EscapeXml(DevCertPassword)}</PackageCertificatePassword>",
                StringComparison.Ordinal);
            File.WriteAllText(csprojPath, text);
        }
    }

    private static void WriteLaunchSettings(string path, string? remoteMachine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var machine = string.IsNullOrWhiteSpace(remoteMachine) ? "" : remoteMachine.Trim();
        var remoteEnabled = machine.Length > 0 ? "true" : "false";
        // Modern UseUwp + EnableMsixTooling uses launch profiles (not classic Debug → Target device).
        var json =
            "{\r\n" +
            "  \"profiles\": {\r\n" +
            "    \"Local Machine\": {\r\n" +
            "      \"commandName\": \"MsixPackage\",\r\n" +
            "      \"alwaysReinstallApp\": false,\r\n" +
            "      \"nativeDebugging\": false\r\n" +
            "    },\r\n" +
            "    \"Remote Machine\": {\r\n" +
            "      \"commandName\": \"MsixPackage\",\r\n" +
            "      \"remoteDebugEnabled\": " + remoteEnabled + ",\r\n" +
            "      \"remoteDebugMachine\": \"" + EscapeJson(machine) + "\",\r\n" +
            "      \"authenticationMode\": \"Universal\",\r\n" +
            "      \"alwaysReinstallApp\": false,\r\n" +
            "      \"nativeDebugging\": false\r\n" +
            "    }\r\n" +
            "  }\r\n" +
            "}\r\n";
        File.WriteAllText(path, json);
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void WriteUwpSolution(string solutionPath, string packageName)
    {
        var guid = UwpProjectGuid.ToString().ToUpperInvariant();
        File.WriteAllText(solutionPath,
$@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{packageName}"", ""Engine.Player.XboxUwp.csproj"", ""{{{guid}}}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|x64 = Debug|x64
		Release|x64 = Release|x64
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{{guid}}}.Debug|x64.ActiveCfg = Debug|x64
		{{{guid}}}.Debug|x64.Build.0 = Debug|x64
		{{{guid}}}.Debug|x64.Deploy.0 = Debug|x64
		{{{guid}}}.Release|x64.ActiveCfg = Release|x64
		{{{guid}}}.Release|x64.Build.0 = Release|x64
		{{{guid}}}.Release|x64.Deploy.0 = Release|x64
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
");
    }

    private static void RunMsBuildUwp(string msbuildPath, string csprojPath, string configuration, string pfxPath)
    {
        var args =
            $"\"{csprojPath}\" " +
            "/t:Restore;Build " +
            $"/p:Configuration={configuration} " +
            "/p:Platform=x64 " +
            "/p:RuntimeIdentifier=win-x64 " +
            "/p:SelfContained=true " +
            "/p:AppxPackageSigningEnabled=true " +
            $"/p:PackageCertificateKeyFile=\"{pfxPath}\" " +
            $"/p:PackageCertificatePassword={DevCertPassword} " +
            "/restore " +
            "/verbosity:minimal";

        var psi = new ProcessStartInfo
        {
            FileName = msbuildPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csprojPath)!
        };

        using var proc = Process.Start(psi)!;
        // Reading stdout to the end before stderr deadlocks once MSBuild fills the other pipe.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var combined = (stdout.ToString() + "\n" + stderr.ToString()).Trim();
            var errorLines = combined.Split('\n')
                .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                .Take(12);
            var summary = string.Join("\n", errorLines);
            if (string.IsNullOrWhiteSpace(summary))
                summary = combined.Length > 1200 ? combined[..1200] + "..." : combined;
            throw new Exception($"Xbox UWP MSBuild failed (exit {proc.ExitCode}):\n{summary}");
        }

        Log.Info("[Build] Engine.Player.XboxUwp MSBuild completed.");
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) continue;
            var destPath = Path.Combine(dst, name);
            if (File.Exists(destPath))
            {
                var attrs = File.GetAttributes(destPath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destPath, attrs & ~FileAttributes.ReadOnly);
            }
            File.Copy(file, destPath, overwrite: true);
            var copied = File.GetAttributes(destPath);
            if ((copied & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(destPath, copied & ~FileAttributes.ReadOnly);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".vs" or "AppPackages") continue;
            CopyDirectory(dir, Path.Combine(dst, name));
        }
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

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}
#endif
