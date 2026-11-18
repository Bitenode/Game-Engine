# Engine.Player.XboxUwp

This is the **Xbox Dev Mode** player: a UWP app that draws through **ANGLE** (OpenGL ES 3 → Direct3D 11) on a `SwapChainPanel`. It runs the same `Core/` planet and scene code as the desktop Avalonia player, with a few console-specific paths gated on `XBOX_UWP`.

## Getting builds onto the console

1. In the editor: **Build Settings → Xbox → Build** (or Build & Run). That stages `Builds/Xbox/VisualStudio/` with your `Data/`, scenes, and this project.
2. Open the generated `.sln` in Visual Studio 2022, pick **x64**, set the launch profile to **Remote Machine** (your Dev Home IP), sign in a user on the Xbox, then **Deploy** or F5.
3. In **Dev Home → your package → View details → App type**, choose **Game**. **App** mode caps memory around 3 GB and shares the GPU; planet scenes will feel broken long before you run out of ideas to optimize.

On first boot the title prints `GL_VERSION` and `GL_RENDERER`. You need **OpenGL ES 3** on a real **Direct3D11** device—not WARP, not ES 2. If that check fails, 3D stops there on purpose.

Retail Xbox still needs ID@Xbox and GDKX. This tree is a **Dev Mode UWP** title, not a GDK package.

## Native DLLs (`native/x64`)

Ship these next to the player executable:

| File | Role |
|------|------|
| `libEGL.dll`, `libGLESv2.dll` | **Release** ANGLE from vcpkg (`angle:x64-uwp-rel` with overlay triplet `VCPKG_BUILD_TYPE release`; same chromium_7258 / `d9fc4a372074` line as before) |
| `z.dll` | Release zlib companion for that ANGLE build |
| `MSVCP140_APP.dll`, `VCRUNTIME140_APP.dll`, `VCRUNTIME140_1_APP.dll` | Release UWP CRT imports (from **Microsoft.VCLibs.140.00** x64 in the Windows SDK store layout) |
| `assimp.dll` | Windows Store build of Assimp 4.1.0 |
| `d3dcompiler_47.dll` | HLSL compile at link time |

Do **not** ship debug ANGLE or `*D_APP.dll`, `ucrtbased.dll`, or `zd.dll` with a release player—the old debug GLES stack was tens of megabytes and made uniform uploads orders of magnitude slower under a debugger.

To rebuild ANGLE locally:

```text
vcpkg install angle:x64-uwp-rel
```

Copy the `bin` outputs into `Engine.Player.XboxUwp/native/x64` and into the staged `Builds/Xbox/VisualStudio/native/x64` if you test from a project build folder.

## What lands in the package

`Core/Editor/XboxContentStager.cs` fills `Data/` from:

- Assets referenced by the chosen build scenes (materials, prefabs, JSON, models, textures).
- Paths your game scripts mention as literals (folder names in strings get packed; paths built only at runtime from variables do not).
- For each imported model: sibling `_Materials`, `_Animations`, and textures the FBX names.

Paths that only exist on another PC are copied under `Data/Assets/_external` and the staged scene is rewritten to match. Missing files show up as build warnings.

The installed package is **read-only**. The player does not write materials, bone clips, terrain saves, or a project manifest back into `Data/`.

## Logs and “why is it slow?”

Everything the engine prints also goes to **`LocalState\player.log`**.

- **PC sideload:** `%LOCALAPPDATA%\Packages\<package family>\LocalState\player.log`
- **Xbox:** Device Portal → your app → `LocalState`

Recent host work added **`FrameProfile`** lines in that log: rolling FPS, time per frame slice (planet ground, plants, shadows, UI, …), GPU upload counts, chunk bake state, and app memory. Read the slowest-frame breakdown before chasing random GL crashes.

The loop also notes **Debug vs Release**, whether a **debugger is attached**, and **Device Family** (Desktop vs Xbox). Debug + F5 on a large planet is not comparable to a deployed Release build—especially while the height map is still baking.

**First launch after a shader or ANGLE change** compiles and caches linked programs under `LocalCache\Shaders` (`.glbin`). That pass can take tens of seconds; later launches reuse the cache. Cache keys include a stamp of `libGLESv2.dll` so swapping debug/release ANGLE does not reuse incompatible binaries.

## How the console build differs from desktop

These are the main switches—desktop editor Game View is unchanged unless noted.

**Rendering**

- **Forward-only** on UWP: deferred, SSAO, SSR, and volumetric fog shaders are not linked in the player (they used to hang ANGLE on first present).
- Planet crust uses **`PlanetTerrainFragXbox`** (lighter fragment than the full desktop planet shader).
- **GLES skinning** uses `uBones[48]` (128 matrices exceed `MAX_VERTEX_UNIFORM_VECTORS`); bone indices are clamped in the ES shader path.
- **No `glBindVertexArray(0)`** on Xbox after mesh/grass/fullscreen setup—ANGLE treats it as illegal and the next draw can AV.
- **GPUMesh** re-binds VAO/VBO/EBO and vertex layout each draw so grass instancing does not leave skinning attribs enabled on static meshes.
- **Shader binary cache** directory is set in `XboxGameLoop` before the first frame renders.

**Planet**

- Height **bake resolution** 256 on console (512 on desktop), aligned between terrain and density sampling.
- Biome **CPU textures** decode on a **background thread** one file at a time; the render thread only binds finished results (stops multi-second “planet ground” frames during load).
- Cubemap splat upload: **RGBA8 atlas**, one face per frame on Xbox.
- **PlanetScatterRenderer**: scene may still say `MaxInstances: 8192`; runtime caps tracked scatter at **96** on UWP.
- **Grass**: instanced draws split into batches of 64 instances on Xbox.
- Vegetation waits for biome streaming to finish before tree model imports; grass carpet still runs.

**Fauna**

- Rigs import **one species at a time** on a worker; the game thread does not queue the whole table at once.
- On UWP, finished rigs can become prototypes without a heavy JSON round-trip; spawn rate and herd size are capped so Assimp does not pile up with terrain work.
- **Duplicate planet behaviors** (second Scatter/Life Streaming from graph apply) are stripped on load—keep **one** of each on “Earth test” in the scene file.

**Materials**

- Animal color maps are **not** auto-cutout from loose alpha (that made bodies invisible). Leaf detection requires a real mask (clear background + solid subject).

**Host loop**

- `GpuMeshReleaseQueue.Drain` runs before each frame’s draw pass so replaced chunk meshes are freed without evicting the whole mesh cache mid-walk.

## Audio, input, textures

- **Audio:** WAV/MP3 through **AudioGraph**; Ogg is skipped on console.
- **Input:** `Windows.Gaming.Input` gamepads.
- **Textures:** build may emit BC1/BC3 `.dds` sidecars beside material paths; the console loads those instead of compressing at runtime. Engine-side TGA/TIFF decode applies where the desktop does.

## When something is missing or the planet scene fails to load

1. Read `player.log` for the scene load line and any JSON parse errors.
2. Confirm the staged `Data/Scenes/planet.scene` is valid JSON (a duplicated scatter block in the past broke loads with “`}` is invalid without a matching open”).
3. Confirm **Game** app type and **Release** natives if you are chasing freezes that only happen under Debug + debugger.

For editor-side packaging steps and GDK vs UWP, see [12_Build_Settings.md](../Game-Engine/Docs/12_Build_Settings.md).