# Engine.Player.XboxUwp

UWP host for **Xbox Dev Mode**. ANGLE translates the engine's OpenGL ES 3 renderer to Direct3D 11 on a `SwapChainPanel`.

## Natives

From a machine with vcpkg:

```
vcpkg install angle:x64-uwp
```

`native/x64` contains the UWP **debug** `libEGL.dll` and `libGLESv2.dll` from `vcpkg install angle:x64-uwp` (the release link ran out of disk), plus the debug CRT they import (`VCRUNTIME140D_APP.dll`, `VCRUNTIME140_1D_APP.dll`, `MSVCP140D_APP.dll`). `ucrtbased.dll` is included only in Release; a Debug build already gets it from Microsoft.UniversalCRT.Debug, and packing a second copy fails with APPX1101. A WindowsStore build of assimp 4.1.0 is named `assimp.dll`. The extra CRT DLLs are what a Release deploy was missing, which surfaced as "Unable to load DLL libEGL.dll". A release ANGLE rebuild can replace the debug binaries later.

## On the console

1. Build Settings → **Xbox**, set **Remote Machine** to the Dev Home IP, then Build.
2. Open the staged `.sln`, platform **x64**, launch profile **Remote Machine**, F5.
3. In Dev Home, set the package App type to **Game** (full GPU and 5 GB; App mode is 1 GB and a shared GPU).
4. The status text must show `OpenGL ES 3` and a hardware Direct3D11 renderer. ES 2.0 or WARP stops 3D play.

Assets are loose files under `Data/Assets` (not `Assets.dll`). Textures named by a `.material` get BC1/BC3 `.dds` sidecars at build time; the console never compresses. Audio is WAV/MP3. Ogg is skipped. Pads use `Windows.Gaming.Input`.

## What the build packs

`Core/Editor/XboxContentStager.cs` fills `Data/` from three sources:

- Paths quoted in the build scenes, and in every material, prefab and JSON file those reach.
- Paths and folders named in the project's player scripts, such as `"Assets/Game/Data/Items.json"`, `"Assets/Game/Audio"`, or the literals of a `Path.Combine(root, "Assets", "Game", "Data", "Items.json")`. A file name built at run time from variables cannot be found; name the folder in a string literal so it is packed.
- For each model: its `_Materials` and `_Animations` folders and the textures the model file names.

An absolute path in a scene (for example a terrain file in another checkout) is copied to `Data/Assets/_external` and the staged scene is rewritten to point at it. A path that exists neither on the build PC nor under Assets is listed as a build warning.

The package is read-only. The player never writes materials, bone clips, terrain files or a project manifest.

## When something is missing

Read `LocalState\player.log`. It records every engine log line, each asset or script that failed to load, the time each scene took to load, and frame-rate samples.

- On a PC: `%LOCALAPPDATA%\Packages\<package family>\LocalState\player.log`
- On the Xbox: Device Portal → File explorer → the app → `LocalState`

The first launch compiles every shader through ANGLE and is slow (about 40 s on an Iris Xe laptop). The linked binaries are kept in `LocalCache\Shaders`, so later launches start in under a second.

Retail Xbox still needs ID@Xbox and GDKX. This package is a Dev Mode UWP title.
