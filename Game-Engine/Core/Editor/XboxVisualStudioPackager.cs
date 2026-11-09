#if !PLAYER
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Generates a per-game Windows Application Packaging Project (.wapproj / .sln)
/// around a published Engine.Player layout and builds a signed MSIX with Visual Studio MSBuild.
/// </summary>
public static class XboxVisualStudioPackager
{
    private const string DevCertFileName = "XboxDev.pfx";
    private const string DevCertPublisherFileName = "XboxDev.publisher.txt";
    private const string DevCertPassword = "xboxdev";
    private const string SdkBuildToolsVersion = "10.0.28000.2705";

    public sealed record Options(
        string XboxOutDir,
        string LayoutDir,
        string ProjectRootPath,
        string ProductName,
        string Company,
        string Version,
        string? PlayerAssetsDir,
        IProgress<(int Percent, string Message)>? Progress = null);

    public sealed record Result(
        string SolutionPath,
        string WapprojPath,
        string MsixPath,
        string CertificatePath,
        string VisualStudioDir);

    /// <summary>Write the Visual Studio packaging project and build the MSIX.</summary>
    public static Result GenerateAndBuild(Options options)
    {
        void Progress(int percent, string message) =>
            options.Progress?.Report((percent, message));
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Xbox packaging must be run on Windows.");

        if (!Directory.Exists(options.LayoutDir))
            throw new DirectoryNotFoundException($"Xbox layout folder not found: {options.LayoutDir}");

        var exe = Path.Combine(options.LayoutDir, "Engine.Player.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                "Xbox layout is missing Engine.Player.exe. Publish the player before packaging.", exe);

        var vsInstall = FindVisualStudioInstallPath()
            ?? throw new InvalidOperationException(
                "Visual Studio 2022 with MSBuild was not found. Install Visual Studio 2022 and the " +
                "\"MSIX Packaging Tools\" / Windows Application Packaging Project workload, then rebuild.");

        var wapDir = Path.Combine(vsInstall, "MSBuild", "Microsoft", "DesktopBridge");
        var desktopBridgeTargets = Path.Combine(wapDir, "Microsoft.DesktopBridge.targets");
        if (!File.Exists(desktopBridgeTargets))
            throw new InvalidOperationException(
                "Microsoft.DesktopBridge.targets was not found. Install the Visual Studio \"MSIX Packaging Tools\" " +
                "component (Windows Application Packaging Project), then rebuild.");

        var msbuild = Path.Combine(vsInstall, "MSBuild", "Current", "Bin", "MSBuild.exe");
        if (!File.Exists(msbuild))
            throw new FileNotFoundException("MSBuild.exe was not found next to the Visual Studio install.", msbuild);

        var packageName = SanitizePackageName(options.ProductName, options.ProjectRootPath);
        var publisher = FormatPublisher(options.Company);
        var version = PadPackageVersion(options.Version);
        var displayName = string.IsNullOrWhiteSpace(options.ProductName)
            ? Path.GetFileName(options.ProjectRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : options.ProductName.Trim();
        var publisherDisplay = string.IsNullOrWhiteSpace(options.Company) ? "Game Engine" : options.Company.Trim();

        var vsDir = Path.Combine(options.XboxOutDir, "VisualStudio");
        var appPackagesDir = Path.Combine(options.XboxOutDir, "AppPackages");
        Directory.CreateDirectory(vsDir);
        Directory.CreateDirectory(appPackagesDir);

        var projectSettingsDir = Path.Combine(options.ProjectRootPath, "ProjectSettings");
        Directory.CreateDirectory(projectSettingsDir);
        var certPath = EnsureDevCertificate(projectSettingsDir, publisher);
        var certInVsDir = Path.Combine(vsDir, DevCertFileName);
        File.Copy(certPath, certInVsDir, overwrite: true);

        CopyPackageLogos(options.PlayerAssetsDir, Path.Combine(vsDir, "Assets"));

        var projectGuid = Guid.NewGuid();
        var wapprojPath = Path.Combine(vsDir, $"{packageName}.wapproj");
        var solutionPath = Path.Combine(vsDir, $"{packageName}.sln");
        var manifestPath = Path.Combine(vsDir, "Package.appxmanifest");

        Progress(82, "Writing Visual Studio packaging project...");
        WriteAppxManifest(manifestPath, packageName, publisher, version, displayName, publisherDisplay);
        WriteWapproj(wapprojPath, packageName, projectGuid, options.LayoutDir, DevCertFileName);
        WriteSolution(solutionPath, packageName, projectGuid);
        var buildJson = Path.Combine(options.LayoutDir, "Data", "build.json");
        WriteWapprojUser(wapprojPath + ".user", buildJson);

        Log.Info($"[Build] Generated Visual Studio packaging project: {solutionPath}");
        Log.Info("[Build] MSIX payload excludes layout/Data (keeps MakeAppx within normal RAM limits).");
        Progress(88, "Running MSBuild packaging (MSIX, player only)...");
        Log.Info($"[Build] Building MSIX with MSBuild ({vsInstall})...");

        RunMsBuild(msbuild, wapprojPath, wapDir, appPackagesDir);
        Progress(96, "Locating packaged MSIX...");

        var msix = Directory.EnumerateFiles(appPackagesDir, "*.msix", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? Directory.EnumerateFiles(appPackagesDir, "*.msixbundle", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

        if (msix == null)
            throw new Exception(
                "MSBuild completed but no .msix was produced under AppPackages. " +
                "Open the generated .sln in Visual Studio 2022 to inspect packaging errors.");

        WriteXboxReadme(options.XboxOutDir, packageName, msix);
        Log.Info($"[Build] MSIX created: {msix}");
        return new Result(solutionPath, wapprojPath, msix, certPath, vsDir);
    }

    private static void WriteXboxReadme(string xboxOutDir, string packageName, string msixPath)
    {
        var readme = Path.Combine(xboxOutDir, "README.txt");
        File.WriteAllText(readme,
            "Xbox / MSIX build output\r\n" +
            "========================\r\n\r\n" +
            $"Package identity: {packageName}\r\n" +
            $"MSIX: {msixPath}\r\n\r\n" +
            "The .msix contains only Engine.Player + runtime dependencies.\r\n" +
            "Game content stays in: layout\\Data\\ (Assets.dll, scenes, build.json).\r\n" +
            "This split avoids MakeAppx out-of-memory failures on large projects.\r\n\r\n" +
            "Build & Run sideloads the MSIX and launches:\r\n" +
            "  Engine.Player.exe  <path-to-layout\\Data\\build.json>\r\n\r\n" +
            "Visual Studio Local Machine:\r\n" +
            "  Use Local Machine (not Remote). The .sln enables Deploy, and the\r\n" +
            "  .wapproj.user passes layout\\Data\\build.json as the command-line arg.\r\n" +
            "  If you still see \"needs to be deployed\", open Configuration Manager\r\n" +
            "  and ensure Deploy is checked for this project on Debug|x64.\r\n");
    }

    /// <summary>Find an existing generated .sln under Builds/Xbox/VisualStudio.</summary>
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

    /// <summary>Import the packaging certificate so Add-AppxPackage can sideload the MSIX.</summary>
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

        // Identity Name must start with a letter.
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
        // Keep certificate subject / Publisher attribute aligned and free of quotes.
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
            else if (i < parts.Length)
                nums[i] = 0;
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
        // Subject must match Package.appxmanifest Publisher exactly.
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

    private static void CopyPackageLogos(string? playerAssetsDir, string destAssetsDir)
    {
        Directory.CreateDirectory(destAssetsDir);
        (string Name, int Width, int Height)[] logos =
        {
            ("StoreLogo.png", 50, 50),
            ("Square44x44Logo.png", 44, 44),
            ("Square150x150Logo.png", 150, 150),
            ("Wide310x150Logo.png", 310, 150)
        };

        foreach (var (name, width, height) in logos)
        {
            var dest = Path.Combine(destAssetsDir, name);
            var src = playerAssetsDir != null ? Path.Combine(playerAssetsDir, name) : null;
            if (src != null && File.Exists(src) && PngHasDimensions(src, width, height))
            {
                File.Copy(src, dest, overwrite: true);
            }
            else
            {
                // Appx packaging rejects wrong dimensions (player placeholders were 1x1).
                WriteSolidPng(dest, width, height);
            }
        }
    }

    private static bool PngHasDimensions(string path, int width, int height)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            return codec != null && codec.Info.Width == width && codec.Info.Height == height;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteSolidPng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void WriteAppxManifest(
        string path,
        string packageName,
        string publisher,
        string version,
        string displayName,
        string publisherDisplay)
    {
        var xml =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         IgnorableNamespaces=""uap rescap"">
  <Identity Name=""{EscapeXml(packageName)}""
            Publisher=""{EscapeXml(publisher)}""
            Version=""{EscapeXml(version)}"" />
  <Properties>
    <DisplayName>{EscapeXml(displayName)}</DisplayName>
    <PublisherDisplayName>{EscapeXml(publisherDisplay)}</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.17763.0"" MaxVersionTested=""10.0.22621.0"" />
    <TargetDeviceFamily Name=""Windows.Xbox"" MinVersion=""10.0.22000.0"" MaxVersionTested=""10.0.22621.0"" />
  </Dependencies>
  <Resources>
    <Resource Language=""en-us"" />
  </Resources>
  <Applications>
    <Application Id=""App"" Executable=""Engine.Player.exe"" EntryPoint=""Windows.FullTrustApplication"">
      <uap:VisualElements
        DisplayName=""{EscapeXml(displayName)}""
        Description=""{EscapeXml(displayName)}""
        Square150x150Logo=""Assets\Square150x150Logo.png""
        Square44x44Logo=""Assets\Square44x44Logo.png""
        BackgroundColor=""#000000"">
        <uap:DefaultTile Wide310x150Logo=""Assets\Wide310x150Logo.png"" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
  </Capabilities>
</Package>
";
        File.WriteAllText(path, xml.TrimStart() + Environment.NewLine);
    }

    private static void WriteWapproj(
        string path,
        string packageName,
        Guid projectGuid,
        string layoutDir,
        string certFileName)
    {
        // EngineLayoutDir is absolute so MSBuild can find the publish output from any cwd.
        var layoutFull = Path.GetFullPath(layoutDir);
        var xml =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup Condition=""'$(VisualStudioVersion)' == '' or '$(VisualStudioVersion)' &lt; '15.0'"">
    <VisualStudioVersion>15.0</VisualStudioVersion>
  </PropertyGroup>

  <ItemGroup Label=""ProjectConfigurations"">
    <ProjectConfiguration Include=""Debug|x64"">
      <Configuration>Debug</Configuration>
      <Platform>x64</Platform>
    </ProjectConfiguration>
    <ProjectConfiguration Include=""Release|x64"">
      <Configuration>Release</Configuration>
      <Platform>x64</Platform>
    </ProjectConfiguration>
  </ItemGroup>

  <PropertyGroup>
    <WapProjPath Condition=""'$(WapProjPath)'==''"">$(MSBuildExtensionsPath)\Microsoft\DesktopBridge\</WapProjPath>
  </PropertyGroup>

  <Import Project=""$(WapProjPath)\Microsoft.DesktopBridge.props"" />

  <PropertyGroup>
    <ProjectGuid>{{{projectGuid.ToString().ToUpperInvariant()}}}</ProjectGuid>
    <TargetPlatformVersion>10.0.19041.0</TargetPlatformVersion>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <DefaultLanguage>en-US</DefaultLanguage>
    <AppxPackageSigningEnabled>true</AppxPackageSigningEnabled>
    <PackageCertificateKeyFile>{EscapeXml(certFileName)}</PackageCertificateKeyFile>
    <PackageCertificatePassword>{EscapeXml(DevCertPassword)}</PackageCertificatePassword>
    <AppxBundle>Never</AppxBundle>
    <AppxPackageName>{EscapeXml(packageName)}</AppxPackageName>
    <!-- Required so Desktop Bridge packaging accepts a layout-only project (no ProjectReference). -->
    <EntryPointExe>Engine.Player.exe</EntryPointExe>
    <EngineLayoutDir>{EscapeXml(layoutFull)}</EngineLayoutDir>
    <!-- Engine.Player is .NET (CoreCLR), not Desktop CLR / .NET Framework. -->
    <DebuggerType>ManagedOnly</DebuggerType>
    <BackgroundTaskDebugEngines>CoreClr</BackgroundTaskDebugEngines>
    <NoWarn>$(NoWarn);NU1702</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <AppxManifest Include=""Package.appxmanifest"">
      <SubType>Designer</SubType>
    </AppxManifest>
    <None Include=""{EscapeXml(certFileName)}"" />
  </ItemGroup>

  <ItemGroup>
    <Content Include=""Assets\StoreLogo.png"" />
    <Content Include=""Assets\Square150x150Logo.png"" />
    <Content Include=""Assets\Square44x44Logo.png"" />
    <Content Include=""Assets\Wide310x150Logo.png"" />
  </ItemGroup>

  <!--
    Package only the Engine.Player runtime (exe + deps).
    Game Data/ (Assets.dll, scenes, scripts) is intentionally excluded — multi‑GB payloads
    make MakeAppx fail with 0x8007000e (out of memory) even on 30GB machines.
    Data stays in Builds/Xbox/layout/Data and is passed to the player at launch.
  -->
  <Target Name=""AddEngineLayoutToPayload"" AfterTargets=""_RemoveAllNonWapUWPItems"" BeforeTargets=""_WritePayloadFiles"">
    <ItemGroup>
      <_EngineLayoutFiles Include=""$(EngineLayoutDir)\**\*.*""
                          Exclude=""$(EngineLayoutDir)\Assets\**\*.*;$(EngineLayoutDir)\Data\**\*.*"" />
      <AppxPackagePayload Include=""@(_EngineLayoutFiles)"">
        <TargetPath>%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
      </AppxPackagePayload>
    </ItemGroup>
  </Target>

  <Import Project=""$(WapProjPath)\Microsoft.DesktopBridge.targets"" />

  <ItemGroup>
    <PackageReference Include=""Microsoft.Windows.SDK.BuildTools"" Version=""{SdkBuildToolsVersion}"" PrivateAssets=""all"" />
  </ItemGroup>
</Project>
";
        File.WriteAllText(path, xml.TrimStart() + Environment.NewLine);

        // Sanity: ensure the written project is well-formed XML.
        XDocument.Load(path);
    }

    private static void WriteSolution(string path, string packageName, Guid projectGuid)
    {
        var guidUpper = projectGuid.ToString().ToUpperInvariant();
        var typeGuid = "C7167F0D-BC9F-4E6E-AFE1-012C56B48DB5";
        var sln =
$@"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{{{typeGuid}}}"") = ""{packageName}"", ""{packageName}.wapproj"", ""{{{guidUpper}}}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|x64 = Debug|x64
		Release|x64 = Release|x64
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{{guidUpper}}}.Debug|x64.ActiveCfg = Debug|x64
		{{{guidUpper}}}.Debug|x64.Build.0 = Debug|x64
		{{{guidUpper}}}.Debug|x64.Deploy.0 = Debug|x64
		{{{guidUpper}}}.Release|x64.ActiveCfg = Release|x64
		{{{guidUpper}}}.Release|x64.Build.0 = Release|x64
		{{{guidUpper}}}.Release|x64.Deploy.0 = Release|x64
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
";
        File.WriteAllText(path, sln.TrimStart() + Environment.NewLine);
    }

    /// <summary>
    /// VS debug settings for Local Machine: deploy + pass external Data/build.json
    /// (game content is outside the MSIX to avoid MakeAppx OOM).
    /// </summary>
    private static void WriteWapprojUser(string userPath, string buildJsonPath)
    {
        var args = File.Exists(buildJsonPath)
            ? "\"" + buildJsonPath.Replace("\"", "\\\"") + "\""
            : "";
        var xml =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""Current"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <DebuggerFlavor>AppHostLocalDebugger</DebuggerFlavor>
    <DebuggerType>ManagedOnly</DebuggerType>
    <BackgroundTaskDebugEngines>CoreClr</BackgroundTaskDebugEngines>
    <_TargetImageClrType>Managed</_TargetImageClrType>
    <CommandLineArguments>{EscapeXml(args)}</CommandLineArguments>
    <!-- false: Local Machine can relaunch without reinstalling the package every F5 -->
    <AlwaysReinstall>false</AlwaysReinstall>
  </PropertyGroup>
</Project>
";
        File.WriteAllText(userPath, xml.TrimStart() + Environment.NewLine);
    }

    private static void RunMsBuild(string msbuildPath, string wapprojPath, string wapDir, string appPackagesDir)
    {
        // Use forward slashes so a trailing separator cannot escape the closing quote
        // on Windows (\" after \ swallows the rest of the command line).
        var packageDir = appPackagesDir.TrimEnd('\\', '/').Replace('\\', '/') + "/";
        var wapProjPath = wapDir.TrimEnd('\\', '/').Replace('\\', '/') + "/";
        var args =
            $"\"{wapprojPath}\" " +
            "/restore " +
            "/p:Configuration=Release " +
            "/p:Platform=x64 " +
            $"/p:AppxPackageDir=\"{packageDir}\" " +
            "/p:UapAppxPackageBuildMode=SideloadOnly " +
            "/p:AppxBundle=Never " +
            $"/p:WapProjPath=\"{wapProjPath}\" " +
            "/verbosity:minimal";

        var psi = new ProcessStartInfo
        {
            FileName = msbuildPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(wapprojPath)!
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
                .Take(12);
            var errorSummary = string.Join("\n", errorLines);
            if (string.IsNullOrWhiteSpace(errorSummary))
                errorSummary = combined.Length > 1200 ? combined[..1200] + "..." : combined;

            if (combined.Contains("0x8007000e", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("Not enough memory", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(
                    "MSBuild packaging failed: MakeAppx ran out of memory while creating the MSIX.\n" +
                    "Game Data is supposed to stay outside the package (layout/Data). If this still happens, " +
                    "close other apps and rebuild — the MSIX should only contain the player runtime.\n" +
                    errorSummary);
            }

            throw new Exception($"MSBuild packaging failed (exit code {proc.ExitCode}):\n{errorSummary}");
        }

        Log.Info("[Build] MSBuild packaging completed successfully.");
    }

    /// <summary>
    /// Sideload an MSIX, then start the installed Engine.Player with an external build.json
    /// (game Data lives beside the build, not inside the package).
    /// </summary>
    public static void SideloadAndLaunch(string msixPath, string packageIdentityName, string buildJsonPath)
    {
        if (!File.Exists(msixPath))
            throw new FileNotFoundException("MSIX not found.", msixPath);
        if (!File.Exists(buildJsonPath))
            throw new FileNotFoundException(
                "Game Data build.json not found. Expected Builds/Xbox/layout/Data/build.json beside the package.",
                buildJsonPath);

        var escapedMsix = msixPath.Replace("'", "''");
        var escapedJson = buildJsonPath.Replace("'", "''");
        var escapedName = packageIdentityName.Replace("'", "''");
        var script =
            "$ErrorActionPreference = 'Stop'\r\n" +
            $"Add-AppxPackage -Path '{escapedMsix}' -ForceUpdateFromAnyVersion -ForceApplicationShutdown\r\n" +
            $"$pkg = Get-AppxPackage -Name '{escapedName}' | Sort-Object {{ [version]$_.Version }} -Descending | Select-Object -First 1\r\n" +
            $"if (-not $pkg) {{ throw 'Installed package not found: {escapedName}' }}\r\n" +
            "$exe = Join-Path $pkg.InstallLocation 'Engine.Player.exe'\r\n" +
            "if (-not (Test-Path -LiteralPath $exe)) { throw \"Engine.Player.exe missing in $($pkg.InstallLocation)\" }\r\n" +
            $"Start-Process -FilePath $exe -ArgumentList @('{escapedJson}')\r\n";

        var scriptPath = Path.Combine(Path.GetTempPath(), "GameEngine_XboxLaunch_" + Guid.NewGuid().ToString("N") + ".ps1");
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
            if (proc.ExitCode != 0)
                throw new Exception($"Xbox sideload/launch failed:\n{(stderr + "\n" + stdout).Trim()}");
            Log.Info($"[Build] Sideloaded {packageIdentityName} and launched with Data at {buildJsonPath}");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
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
