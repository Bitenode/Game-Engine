# Engine.Player.Gdk

Native **Microsoft GDK** bootstrap for Xbox Live (XSAPI), **XAudio2**, and **GameInput**.

This is **not** the Avalonia `Engine.Player` MSIX build. The C# player keeps OpenGL/Avalonia and uses `NullXboxLiveService`. Console / GDK PC titles use this host.

## Prerequisites

1. [Microsoft Game Development Kit (GDK)](https://aka.ms/gdk) — install with `winget install Microsoft.Gaming.GDK`
2. Visual Studio 2022 **Desktop development with C++** workload
3. ID@Xbox or managed partner credentials for Live sandboxes (sign-in / achievements)
4. Title **SCID**, **Title Id**, and **Sandbox** from Partner Center (fill Build Settings or edit `MicrosoftGame.config`)

Detection looks for:

- `GameDK` / `GameDKCoreLatest` (set by the GDK installer), or
- `C:\Program Files (x86)\Microsoft GDK\`

The October 2026+ public GDK uses the **simplified** `windows\include` / `windows\lib\x64` layout. This project builds as stock **x64** against that layout (not the old `Gaming.Desktop.x64` custom platform). Xbox console Scarlett still needs GDKX under a managed partner account.

## Build

From Visual Studio: open `Game_Engine.sln` (or `Engine.Player.Gdk.sln`), select platform **x64**, then build **Engine.Player.Gdk**. Reload Visual Studio after installing the GDK so `GameDK` / `GameDKCoreLatest` are visible.

From the editor: Build Settings → platform **Xbox GDK** (requires SCID / Title Id / Sandbox). The editor copies this project into `Builds/XboxGdk/`, writes config, and runs MSBuild `/p:Platform=x64`.

## Layout

| Path | Role |
|------|------|
| `src/main.cpp` | Game loop stub + CLI (`--scid`, `--test-tone`, `--unlock-achievement`) |
| `src/AudioXAudio2.*` | Mastering voice + test tone |
| `src/InputGameInput.*` | Pad poll mapped to engine Xbox-layout names |
| `src/XboxLiveXsapi.*` | Sign-in + achievement unlock via XSAPI |
| `MicrosoftGame.config` | Title identity (generated/overwritten by Build Settings) |

## Packaging

Use GDK tools (`MakePkg` / `wdapp`), not the Avalonia `.wapproj` MakeAppx path. See Microsoft GDK packaging docs.

## Xbox Live source

XSAPI lives in the GDK (`xbox-live-api`). Do **not** copy that repo into `Assets/` or `Assets.dll`. Pin the GDK version your studio uses; update the host when you upgrade the GDK.
