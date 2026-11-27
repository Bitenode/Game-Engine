# Game Engine — Build Settings

## Solution Structure

The solution (`Game_Engine.sln`) contains two projects that share the same engine core but serve different purposes:

```
Github Engine/
├── Game-Engine/                    # Editor project (Game_Engine)
│   ├── Game_Engine.csproj          # Editor project file
│   ├── Game_Engine.sln             # Solution file (both projects)
│   ├── Core/                       # Shared engine source code
│   ├── Views/                      # Editor-only UI panels
│   ├── Docking/                    # Editor-only docking system
│   ├── Program.cs                  # Editor entry point
│   ├── App.axaml                   # Editor Avalonia application
│   ├── MainWindow.axaml            # Editor main window
│   └── Standard Assets/            # Built-in assets (BiomeGraph, Planet Textures, Planet Vegetation, Skybox, Shader, Scenes, Prefabs, Code Examples)
│
├── Engine.Player/                  # Standalone player (desktop Avalonia + Windows TFM for PC MSIX)
│   ├── Engine.Player.csproj
│   ├── Program.cs                  # build.json + PlayerBootstrap / PlayerFrameRenderer
│   └── App.axaml
├── Engine.Player.XboxUwp/          # Xbox Dev Mode UWP host (SwapChainPanel + ANGLE ES 3)
│   └── README.md
├── Engine.Player.Gdk/              # Native Microsoft GDK scaffold (XAudio2, GameInput, XSAPI)
│   └── README.md
└── Engine.Player.Android/          # Avalonia Android host (APK)
    └── Engine.Player.Android.csproj
```

| Project | Purpose | Output | Roslyn | UIX | Extensions | Runtime IDs |
|---------|---------|--------|--------|-----|------------|-------------|
| **Game_Engine** | Editor + development | `WinExe` | Yes (runtime C# compilation) | Yes | Yes | Any CPU |
| **Engine.Player** | Standalone game player | `WinExe` | No (loads pre-compiled DLLs) | No | No | Multi-platform + `net9.0-windows10.0.19041.0` for PC MSIX / Store |
| **Engine.Player.XboxUwp** | Xbox Dev Mode title | UWP `App` | No | No | No | x64 only |
| **Engine.Player.Gdk** | GDK native bootstrap | Win32 console | — | — | — | x64 (GDK layout) |
| **Engine.Player.Android** | Same core, Android APK | `Exe` (net9.0-android35.0) | No | No | No | android-arm64, android-x64 |

### Android, MSIX / Store, and Xbox from Build Settings

- **Asset packing** — `Data/Assets.dll` includes **only files referenced by the scenes in the build list** (plus material textures and common model sidecars), not the entire project `Assets/` folder. A Main Menu–only build therefore stays small; unused GB of models/textures are left out. The player caches extracted assets across launches.
- **Android** — Build Settings runs `dotnet publish` on `Engine.Player.Android` with `-f net9.0-android35.0`. The Android host currently pins **Avalonia 11.3.11** (desktop/editor use **Avalonia 12** on .NET 9); **Avalonia.Android 12.x requires .NET 10** (`net10.0-android36.0`) — upgrade the Android TFM and packages after installing the .NET 10 SDK. Game `Data` is zipped and passed as **`Data.zip`** in the APK **assets**; the player extracts it on first launch (see `Engine.Player.Android` and player `Program.cs` for asset loading). Install the [.NET Android workload](https://learn.microsoft.com/dotnet/mobile/get-started) (`dotnet workload install android`). Release APK signing: set keystore properties as in [Avalonia Android deployment](https://docs.avaloniaui.net/docs/deployment/android).
- **Windows MSIX / Microsoft Store** — The `Engine.Player` project includes **`Properties/Package.appxmanifest`** for packaged desktop identity. Publishing with **`net9.0-windows10.0.19041.0`** produces a layout suitable for **MSIX** / Store tooling (Visual Studio “Packaging” or `dotnet publish` with Windows App SDK / Windows packaging projects). The editor build panel should use the same output folder conventions as the CLI (`publish/` under the player project, plus copied `Data/`). Replace placeholder `Assets/` tiles and set a valid package **identity** publisher before Store submission.
- **Xbox** — See [Xbox platforms (UWP vs GDK)](#xbox-platforms-uwp-vs-gdk) and [UWP Dev Mode packaging](#xbox--visual-studio-packaging-uwp-dev-mode) below.

### Xbox platforms (UWP vs GDK)

| Build Settings platform | Runtime | Audio | Xbox Live | Output |
|-------------------------|---------|-------|-----------|--------|
| **Xbox** | UWP `Engine.Player.XboxUwp` (C#) | **AudioGraph** (WAV/MP3) | none in host | `Builds/Xbox/VisualStudio/` UWP `.sln` |
| **Xbox GDK** | Native `Engine.Player.Gdk` (C++) | **XAudio2** | **XSAPI** ([xbox-live-api](https://github.com/microsoft/xbox-live-api) via GDK) | `Builds/XboxGdk/` |
| **Windows** | Avalonia `Engine.Player` (C#) | **NAudio** on Windows TFM | `NullXboxLiveService` | unpackaged `Engine.Player.exe` |

XSAPI / xbox-live-api is a **GDK native dependency**, never copied into `Assets.dll`. Game scripts use `Game_Engine.Core.Xbox.XboxLive.Current`; the Avalonia / UWP hosts stay without Live until a later interop layer exists.

### Xbox / Visual Studio packaging (UWP Dev Mode)

Desktop Bridge / FullTrust Avalonia MSIX packages **do not run on Xbox** (console shows “we don’t support …”). The **Xbox** platform now stages a real UWP app.

| Output under `Builds/Xbox/` | Contents |
|-----------------------------|----------|
| `Data/` | Scripts, optional `Assets.dll`, scenes, `build.json` |
| `VisualStudio/` | Staged `Engine.Player.XboxUwp`, `{Name}.sln`, `Package.appxmanifest`, embedded `Data/`, signing cert |
| `README.txt` | Remote Machine deploy steps |

**Prerequisites:** Visual Studio 2022 with UWP support, MSBuild. Editor: `Core/Editor/XboxVisualStudioPackager.cs`. Template: [`Engine.Player.XboxUwp/`](../../Engine.Player.XboxUwp/README.md).

1. Build `Data/` (scripts, scene-referenced assets, scenes, manifest).
2. Copy `Engine.Player.XboxUwp` into `VisualStudio/`, write identity into `Package.appxmanifest` (**no** `runFullTrust`, EntryPoint = `Engine.Player.XboxUwp.App`).
3. MSBuild **x64** produces a signed `.msix` for sideload/deploy.

**Deploy to console:** Open in Visual Studio → platform **x64** → Target device **Remote Machine** → Authentication **Universal** → sign in a user on the Xbox → Deploy / F5.

**3D on console:** the host creates an ANGLE ES 3 context on a `SwapChainPanel`. Ship **release** ANGLE (`libEGL.dll` / `libGLESv2.dll` from `vcpkg install angle:x64-uwp-rel` with a release overlay triplet—see [`Engine.Player.XboxUwp/README.md`](../../Engine.Player.XboxUwp/README.md)) plus release `z.dll` and the MSVC UWP CRT DLLs in `native/x64`. In Dev Home set the package App type to **Game** (full GPU, ~5 GB). The first screen prints `GL_VERSION` and `GL_RENDERER`. 3D play continues only when that string is OpenGL ES 3.0 on a hardware Direct3D11 renderer.

Assets are staged loose under `Data/Assets` (not a zip) by `Core/Editor/XboxContentStager.cs`. Build-time `.dds` sidecars are BC1/BC3 only and are made for material textures. Audio is WAV/MP3 via AudioGraph. Pads use `Windows.Gaming.Input`. Ogg is not decoded on the console.

The player writes `LocalState\player.log` (engine log, failed asset and script loads, scene load times, and `FrameProfile` timing summaries). Check it first when a console build looks different from the editor. Linked shader binaries are cached in `LocalCache\Shaders` (keys include the ANGLE DLL stamp); only the first launch after a shader or ANGLE swap pays the full compile.

**Limits:** Retail certification still needs ID@Xbox + GDKX. This path is a Dev Mode UWP title, not a GDK console package.

### Xbox GDK native host

Template: [`Engine.Player.Gdk/`](../../Engine.Player.Gdk/README.md). Editor staging: `Core/Editor/XboxGdkPackager.cs`.

| Piece | Role |
|-------|------|
| `src/AudioXAudio2.*` | XAudio2 mastering voice + test tone |
| `src/InputGameInput.*` | GameInput pad poll (engine Xbox-layout button names) |
| `src/XboxLiveXsapi.*` | `XGameRuntimeInitialize`, XSAPI sign-in, achievement unlock |
| `MicrosoftGame.config` | Title identity written from Build Settings |

**Prerequisites:** [Microsoft GDK](https://aka.ms/gdk) (`winget install Microsoft.Gaming.GDK`; `GameDK` / `GameDKCoreLatest`), Visual Studio C++ **x64** (Scarlett console still needs GDKX under a partner account), Partner Center **SCID**, **Title Id**, and **Sandbox**.

Build Settings → **Xbox** or **Xbox GDK** shows an **Xbox Game Config** panel (`MicrosoftGame.config` fields: Identity Name, Display Name, Publisher Display, Description, Title Id, Store Id, MSA App Id, Device Family, logos). **Xbox GDK** also shows SCID / Sandbox. Empty SCID or Title Id fails a GDK build. Missing GDK fails with an install message (no fallback that packs XSAPI into assets). Output goes to `Builds/XboxGdk/`. Package with GDK **MakePkg** / **wdapp**, not MakeAppx.

Retail certification still needs ID@Xbox (or managed partner) credentials; the repo scaffolds and compiles the host when the GDK is installed.

### Player asset loading

`Engine.Player` reads `build.json`, loads scenes from `Data/Scenes/`, and optionally unpacks `Data/Assets.dll` (zip) into a cache under `%TEMP%/EnginePlayerCache/` keyed by file size and timestamp. Extraction runs **off the UI thread** so the window does not sit at “Not Responding” while a large pack expands. UI-only builds with no external asset paths may omit `Assets.dll` entirely.

### Scene-referenced asset collection

`Core/Editor/BuildAssetCollector.cs` walks the **scenes in the build list**, collects quoted asset paths (models, textures, materials, prefabs, audio, etc.), follows `.material` / `.prefab` / `.json` dependencies, and includes model `_Materials` and `_Animations` folders and the textures a model file names. Those files alone are zipped into `Data/Assets.dll` (or copied loose for Xbox).

It also reads the **project's player scripts**. A string literal that names a file or folder under `Assets/` is packed, including the joined literals of a `Path.Combine(root, "Assets", "Game", "Data", "Items.json")` and a bare file name such as `"Items.json"`. This is how data a script loads by path (item catalogs, icon folders, audio folders) reaches the build. A path assembled from variables at run time cannot be detected; name its folder in a literal.

A scene path that is absolute and outside the project is reported in the collector result: Xbox builds copy that file to `Data/Assets/_external` and rewrite the staged scene. At run time, `Core/AssetPathRemap.cs` maps an absolute path that does not exist to the same file under the player's Assets folder.

---

## Solution Configurations

The solution defines 6 build configurations:

| Configuration | Platform | Defines | Usage |
|---------------|----------|---------|-------|
| **Debug \| Any CPU** | Any CPU | `DEBUG`, `TRACE` | Day-to-day development |
| **Debug \| x64** | Any CPU* | `DEBUG`, `TRACE` | 64-bit development |
| **Debug \| x86** | Any CPU* | `DEBUG`, `TRACE` | 32-bit development |
| **Release \| Any CPU** | Any CPU | `TRACE` | Release builds |
| **Release \| x64** | Any CPU* | `TRACE` | 64-bit release |
| **Release \| x86** | Any CPU* | `TRACE` | 32-bit release |

*All x64 and x86 configurations map to `Any CPU` in the actual project build — the platform selector does not produce architecture-specific binaries for the editor.

Both projects build under all configurations.

---

## Editor Project — Game_Engine.csproj

### Project Properties

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

| Property | Value | Description |
|----------|-------|-------------|
| `OutputType` | `WinExe` | Windows executable (no console window) |
| `TargetFramework` | `net9.0` | .NET 9.0 runtime |
| `Nullable` | `enable` | Nullable reference types enabled project-wide |
| `ImplicitUsings` | `enable` | Common `using` directives are auto-imported |
| `AllowUnsafeBlocks` | `true` | Required for OpenGL interop and pointer manipulation via Silk.NET |
| `LangVersion` | `latest` | Enables the latest C# language features |

### Conditional Compilation

| Configuration | Defined Constants | Purpose |
|---------------|-------------------|---------|
| Debug | `DEBUG`, `TRACE`, `WINDOWS` | Debug assertions, trace logging, and the Windows NAudio path (stereo pan and reverb zones) |
| Release | `TRACE`, `WINDOWS` | Trace logging and the same Windows audio path, without debug overhead |

### NuGet Dependencies

| Package | Version | Category | Purpose |
|---------|---------|----------|---------|
| `Avalonia` | `12.*` | UI | Core cross-platform UI framework |
| `Avalonia.Desktop` | `12.*` | UI | Desktop platform integration (Windows/macOS/Linux) |
| `Avalonia.Themes.Fluent` | `12.*` | UI | Fluent design theme (modern look) |
| `Avalonia.Fonts.Inter` | `12.*` | UI | Inter font family for the editor UI |
| `Silk.NET.OpenGL` | `2.23.0` | Rendering | OpenGL / OpenGL ES 3.0 bindings |
| `AssimpNet` | `5.0.0-beta1` | Import | 3D model loading (FBX, OBJ, glTF 2, DAE). Engine.Player and the Android player still reference `4.1.0` |
| `SkiaSharp` | `2.88.9` | Image | 2D image decoding (PNG, JPG, BMP) |
| `Microsoft.CodeAnalysis.CSharp` | `4.14.0` | Scripting | Roslyn C# compiler for runtime script compilation |
| `NAudio` | `3.0.1` | Audio | Audio playback backend |
| `System.Reactive` | `6.1.0` | Reactive | Observable-based event programming |
| `AutoConstructor` | `5.6.0` | Codegen | Source generator for constructor boilerplate |

**AutoConstructor** is configured as a build-only dependency:
```xml
<PackageReference Include="AutoConstructor" Version="5.6.0">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```
This means it runs at compile time only and is not distributed with the built application.

### Included Folders

The project explicitly includes standard asset folders for code examples:
```xml
<ItemGroup>
  <Folder Include="Standard Assets\Code Examples\Custom Menu's\" />
  <Folder Include="Standard Assets\Code Examples\Inspector\" />
</ItemGroup>
```

---

## Player Project — Engine.Player.csproj

The Engine.Player project builds a standalone game player that runs without the editor UI. It shares the engine's `Core/` source code via file linking.

### Project Properties

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>latest</LangVersion>
    <RootNamespace>Game_Engine</RootNamespace>
    <AssemblyName>Engine.Player</AssemblyName>
    <RuntimeIdentifiers>win-x64;win-arm64;osx-x64;osx-arm64;linux-x64;linux-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

| Property | Value | Description |
|----------|-------|-------------|
| `RootNamespace` | `Game_Engine` | Shares the same namespace as the editor for source compatibility |
| `AssemblyName` | `Engine.Player` | Output assembly name |
| `RuntimeIdentifiers` | `win-x64;win-arm64;osx-x64;osx-arm64;linux-x64;linux-arm64` | Multi-platform publish targets |

### Supported Platforms

| RID | Platform | Architecture |
|-----|----------|-------------|
| `win-x64` | Windows | x86-64 |
| `win-arm64` | Windows | ARM64 |
| `osx-x64` | macOS | Intel x86-64 |
| `osx-arm64` | macOS | Apple Silicon (M1/M2/M3+) |
| `linux-x64` | Linux | x86-64 |
| `linux-arm64` | Linux | ARM64 |

### PLAYER Define Constant

The player project adds a `PLAYER` preprocessor define:
```xml
<PropertyGroup>
  <DefineConstants>$(DefineConstants);PLAYER</DefineConstants>
</PropertyGroup>
```

This allows shared Core source files to conditionally exclude editor-only code:
```csharp
#if !PLAYER
    // Editor-only logic (e.g., gizmo rendering, inspector integration)
#endif
```

### Source File Linking

The player does **not** duplicate engine source code. Instead, it links the shared `Core/` files from the editor project:

```xml
<ItemGroup>
  <Compile Include="..\Game-Engine\Core\**\*.cs"
           Exclude="..\Game-Engine\Core\UIX\**;
                    ..\Game-Engine\Core\EditorWindows\**;
                    ..\Game-Engine\Core\Extensibility\**"
           Link="Core\%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

| Include | Description |
|---------|-------------|
| `Core\**\*.cs` | All C# files in the shared Core directory |

| Exclude | Reason |
|---------|--------|
| `Core\UIX\**` | UIX declarative UI framework — editor-only tool windows |
| `Core\EditorWindows\**` | Editor window classes — not needed at runtime |
| `Core\Extensibility\**` | Extension system — editor-only plugin loading |

This means the player gets all engine systems (rendering, physics, audio, animation, components) but none of the editor UI or extension systems.

### Standard Assets UI scripts (linked into the player)

The editor builds **Game_Engine.dll** from the whole `Game-Engine/` tree, including `Standard Assets/Code Examples/`. Scene JSON stores behavior types with an assembly-qualified name (e.g. `..., Game_Engine`). **Engine.Player** is a different assembly name, so any behavior type referenced from a scene file must be compiled into the player.

The player **`<Compile Include="...">`** list adds the Standard Assets **UI** samples that ship with scenes:

| Linked path | Purpose |
|-------------|---------|
| `Standard Assets/Code Examples/UI/*.cs` | **MainMenuController**, **ServerHostController**, **HotbarController**, and any future UI samples colocated there |

Other Standard Assets scripts (gameplay demos, planet tools, etc.) are **not** linked automatically; ship them via **GameScripts.dll** or add explicit `<Compile Include="...">` entries if your scenes reference those types.

### Shared player runtime (`Core/PlayerBootstrap`, `PlayerFrameRenderer`)

All C# player hosts share the same load and draw path:

| Type | Role |
|------|------|
| `PlayerBootstrap` | Reads `build.json`, unpacks `Assets.dll` / loose `Data/Assets`, loads `GameScripts.dll`, wires scene serialization (`WireSceneSerialization`), loads the startup scene |
| `PlayerSimulation` | Fixed-update timestep, behavior lifecycle, deferred scene loads |
| `PlayerFrameState` | Per-frame camera, viewport, and scene handles for the renderer |
| `PlayerFrameRenderer` | One entry point used by **PlayerView** (Avalonia) and **Engine.Player.XboxUwp** (`XboxGameLoop`): rebuild caches, `SceneRenderer` forward pass, `CanvasRenderer` overlays |

Desktop **Engine.Player** calls `PlayerBootstrap` from `Program.cs` / `PlayerRoot`; Xbox UWP calls `Load` on the GL thread after ANGLE init. **`AssetPathRemap`** rewrites absolute asset paths staged under `Data/Assets/_external`.

### Runtime UI and game loop (PlayerView)

**PlayerView** (standalone build) runs the same class of work as **Game View**: render the 3D frame, then overlay UI, then pump input and networking.

- **OpenGL** — forward path, heightmap terrain, **planet** terrain / atmosphere / clouds / water (`PlanetTerrain*` / `PlanetAtmosphere*` / `PlanetClouds*` / `PlanetWater*` shaders), `PlanetTerrain.RefreshLodAroundCamera`, optional post-processing
- **Canvas** — **`CanvasRenderer.RenderOverlays`** draws **Screen Space Overlay** canvases on top of the framebuffer
- **Input** — **`UIEventSystem.ProcessEvents`**, viewport size, **`Input.FeedMousePosition`** on pointer move
- **Networking** — **`NetworkManager.Update()`** when `IsActive` ([Game loop integration](09_Scene_And_Project_Management.md#game-loop-integration))

**PlayerWindow** calls **`NetworkManager.Stop()`** on close so **`Dispose()`** can send disconnect packets.

### NuGet Dependencies (Player)

Same as the editor project **except**:
- **No `Microsoft.CodeAnalysis.CSharp`** — the player loads pre-compiled script DLLs instead of compiling C# at runtime

| Included | Excluded |
|----------|----------|
| Avalonia 12.* (all 4 packages) | Microsoft.CodeAnalysis.CSharp 4.14.0 |
| Silk.NET.OpenGL 2.23.0 | |
| AssimpNet 4.1.0 | |
| SkiaSharp 2.88.9 | |
| NAudio 3.0.1 | |
| System.Reactive 6.1.0 | |
| AutoConstructor 5.6.0 | |

### Player Startup

The player's `Program.cs` locates a `build.json` file that defines the game to run:

```
Engine.Player.exe [path/to/build.json]
```

**Resolution order:**
1. First command-line argument (if it's a valid file path)
2. `Data/build.json` next to the executable (default location)
3. If neither found, exits with an error message

The `build.json` path is stored in `App.BuildJsonPath` and used to load the game project, scenes, and pre-compiled script assemblies.

---

## Application Configuration

### Editor — App.axaml
```xml
<Application>
  <Application.Styles>
    <FluentTheme/>
  </Application.Styles>
  <Application.Resources>
    <views:EnumEqConverter x:Key="EnumEq"/>
  </Application.Resources>
</Application>
```

| Setting | Value | Purpose |
|---------|-------|---------|
| Theme | `FluentTheme` | Modern Fluent design system from Avalonia |
| Font | Inter (via `.WithInterFont()`) | Clean sans-serif font for the editor UI |
| Resources | `EnumEqConverter` | Value converter for enum equality checks in XAML bindings |

### Player — App.axaml
```xml
<Application>
  <Application.Styles>
    <FluentTheme/>
  </Application.Styles>
</Application>
```

The player uses the same Fluent theme but does not register editor-specific converters or resources.

### Startup Initialization (Both Projects)

Both projects share the same Avalonia builder configuration:
```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()    // Auto-detect Windows/macOS/Linux platform
    .WithInterFont()        // Load Inter font family
    .LogToTrace()           // Route Avalonia logs to System.Diagnostics.Trace
```

And both wire up the same scene serialization resolvers:
- `ResolveMeshesFromModelPath` — multi-mesh resolver via `ModelImporter.ImportModel()`
- `ResolveMeshFromModelPath` — single-mesh fallback (first mesh from DFS)
- `ResolveMaterialFromPath` — material resolver via `ProjectService.MaterialsLoad()`

---

## OpenGL / ANGLE Configuration

The engine uses **Silk.NET OpenGL** for GPU rendering. On Windows, Avalonia provides an OpenGL context through **ANGLE** (translating OpenGL ES 3.0 calls to Direct3D).

### Runtime Detection
`GLContext.cs` detects the GL context type at startup:
- Checks the OpenGL version string for `"OpenGL ES"`
- Sets `GLContext.IsES = true` if ANGLE is detected
- Desktop Linux/macOS typically use native OpenGL 3.3 Core

### Shader Adaptation
All shaders are written as desktop GLSL (`#version 330 core`) and automatically converted at runtime:

| Platform | GL Version | Shader Version | Adaptation |
|----------|-----------|----------------|------------|
| Windows (ANGLE) | OpenGL ES 3.0 | `#version 300 es` | Adds `precision highp` for float, int, sampler2D, and samplerCube. `#version` stays the first token |
| macOS | OpenGL 3.3 Core | `#version 330 core` | No adaptation needed |
| Linux | OpenGL 3.3 Core | `#version 330 core` | No adaptation needed |

`ShaderPreprocessor.AdaptSource()` (via `ShaderSources.Adapt()`) handles this conversion from the single modular `.glsl` tree — no duplicate per-platform shader files are needed. Linked program binaries for ANGLE are cached under `%LOCALAPPDATA%\GameEngine\ShaderCache` on desktop (see [Rendering Pipeline — Shaders](04_Rendering_Pipeline.md#shaders)).

### Native Libraries
All native libraries (ANGLE, OpenGL, Skia, Assimp, NAudio) are bundled automatically by their NuGet packages. No manual DLL copying or `DllImport` configuration is required.

---

## Building the Projects

### Prerequisites
- **.NET 9.0 SDK** — [download from dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Visual Studio 2022 17.8+** or **JetBrains Rider** (recommended IDEs)
- No additional SDKs or native toolchains required — all dependencies come from NuGet

### Building from IDE
1. Open `Game-Engine/Game_Engine.sln` in Visual Studio or Rider
2. Select **Debug | Any CPU** configuration
3. Build the solution (Ctrl+Shift+B)
4. Run `Game_Engine` for the editor or `Engine.Player` for the standalone player

### Building from Command Line

**Editor (Debug):**
```bash
cd Game-Engine
dotnet build Game_Engine.csproj -c Debug
dotnet run --project Game_Engine.csproj
```

**Editor (Release):**
```bash
cd Game-Engine
dotnet build Game_Engine.csproj -c Release
```

**Player (platform-specific publish):**
```bash
cd Engine.Player

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained

# macOS Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained
```

### Build Output

| Project | Debug Output | Release Output |
|---------|-------------|----------------|
| Game_Engine | `Game-Engine/bin/Debug/net9.0-windows/` | `Game-Engine/bin/Release/net9.0-windows/` |
| Engine.Player | `Engine.Player/bin/Debug/net9.0/` | `Engine.Player/bin/Release/net9.0/{rid}/publish/` |

### Self-Contained vs Framework-Dependent

**`dotnet publish --self-contained`** bundles the .NET runtime with the player, creating a standalone package that doesn't require .NET to be installed on the target machine.

**`dotnet publish`** (without `--self-contained`) produces a smaller output but requires .NET 9.0 runtime to be installed on the target.

---

## Preprocessor Defines Reference

| Define | Where | Purpose |
|--------|-------|---------|
| `DEBUG` | Editor (Debug config) | Enables debug assertions and diagnostics |
| `TRACE` | Both projects (all configs) | Enables `System.Diagnostics.Trace` logging |
| `PLAYER` | Engine.Player only | Excludes editor-only code paths in shared Core files |

Use these in shared code to branch between editor and player behavior:
```csharp
#if DEBUG
    Log.Debug("Debug-only diagnostics");
#endif

#if !PLAYER
    // Editor-only: show gizmos, inspector hooks, etc.
    SelectionService.Touch();
#endif

#if PLAYER
    // Player-only: load from build.json, skip editor systems
    LoadBuildManifest();
#endif
```
