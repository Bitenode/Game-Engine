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
│   └── Standard Assets/            # Built-in assets
│
└── Engine.Player/                  # Standalone player project
    ├── Engine.Player.csproj        # Player project file
    ├── Program.cs                  # Player entry point (loads build.json)
    └── App.axaml                   # Player Avalonia application
```

| Project | Purpose | Output | Roslyn | UIX | Extensions | Runtime IDs |
|---------|---------|--------|--------|-----|------------|-------------|
| **Game_Engine** | Editor + development | `WinExe` | Yes (runtime C# compilation) | Yes | Yes | Any CPU |
| **Engine.Player** | Standalone game player | `WinExe` | No (loads pre-compiled DLLs) | No | No | Multi-platform |

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
| Debug | `DEBUG`, `TRACE` | Debug assertions, trace logging, development diagnostics |
| Release | `TRACE` | Trace logging only, no debug overhead |

### NuGet Dependencies

| Package | Version | Category | Purpose |
|---------|---------|----------|---------|
| `Avalonia` | `11.*` | UI | Core cross-platform UI framework |
| `Avalonia.Desktop` | `11.*` | UI | Desktop platform integration (Windows/macOS/Linux) |
| `Avalonia.Themes.Fluent` | `11.*` | UI | Fluent design theme (modern look) |
| `Avalonia.Fonts.Inter` | `11.*` | UI | Inter font family for the editor UI |
| `Silk.NET.OpenGL` | `2.23.0` | Rendering | OpenGL / OpenGL ES 3.0 bindings |
| `AssimpNet` | `4.1.0` | Import | 3D model loading (FBX, OBJ, glTF, DAE) |
| `SkiaSharp` | `2.88.9` | Image | 2D image decoding (PNG, JPG, BMP) |
| `Microsoft.CodeAnalysis.CSharp` | `4.14.0` | Scripting | Roslyn C# compiler for runtime script compilation |
| `NAudio` | `2.2.1` | Audio | Audio playback backend |
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

The editor project compiles everything under `Game-Engine/` (including `Standard Assets/Code Examples/`) into **Game_Engine.dll**. Scene files store behavior types by full name (e.g. `Game_Engine.Core.Component.UI.MainMenuController, Game_Engine`). The standalone player uses a **different** assembly name (**Engine.Player**), so types must still exist in that assembly.

The player project therefore **also compiles** the Standard Assets **UI sample behaviors** used by shipped scenes:

| Linked path | Purpose |
|-------------|---------|
| `Standard Assets/Code Examples/UI/*.cs` | **MainMenuController**, **ServerHostController**, and any future UI samples colocated there |

Other Standard Assets scripts (gameplay demos, planet tools, etc.) are **not** linked automatically; ship them via **GameScripts.dll** or add explicit `<Compile Include="...">` entries if your scenes reference those types.

### Runtime UI and game loop (PlayerView)

**PlayerView** mirrors the editor **Game View** for core runtime behavior:

- **OpenGL scene** — forward rendering path, terrain, optional post-processing
- **Canvas / screen-space UI** — uses **`CanvasRenderer`** + **`RenderOverlays`** so `Canvas` + **Screen Space Overlay** menus (e.g. Main Menu) draw on top of the 3D framebuffer
- **Input** — viewport size and **`UIEventSystem.ProcessEvents`** run in the update tick so UI hit-testing matches the editor; pointer move feeds **`Input.FeedMousePosition`**
- **Networking** — **`NetworkManager.Update()`** runs each frame while networking is active (see [Networking — Game loop integration](09_Scene_And_Project_Management.md#game-loop-integration))

On window close, **PlayerWindow** invokes **`NetworkManager.Stop()`** so the UDP transport can notify peers before exit.

### NuGet Dependencies (Player)

Same as the editor project **except**:
- **No `Microsoft.CodeAnalysis.CSharp`** — the player loads pre-compiled script DLLs instead of compiling C# at runtime

| Included | Excluded |
|----------|----------|
| Avalonia 11.* (all 4 packages) | Microsoft.CodeAnalysis.CSharp 4.14.0 |
| Silk.NET.OpenGL 2.23.0 | |
| AssimpNet 4.1.0 | |
| SkiaSharp 2.88.9 | |
| NAudio 2.2.1 | |
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
| Windows (ANGLE) | OpenGL ES 3.0 | `#version 300 es` | Adds `precision mediump float;` qualifiers |
| macOS | OpenGL 3.3 Core | `#version 330 core` | No adaptation needed |
| Linux | OpenGL 3.3 Core | `#version 330 core` | No adaptation needed |

The `ShaderSources.Adapt()` method handles this conversion transparently — no platform-specific shader files are needed.

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
| Game_Engine | `Game-Engine/bin/Debug/net9.0/` | `Game-Engine/bin/Release/net9.0/` |
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
