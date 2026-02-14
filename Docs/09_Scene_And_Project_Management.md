# Game Engine — Scene and Project Management

## Projects

### What Is a Project?
A project is a folder on disk containing all game assets, scenes, scripts, configuration, and compiled assemblies. The `project.json` file at the root identifies the folder as an engine project and stores metadata.

### Project Structure
```
MyGame/
├── project.json            # Project metadata (ID, name, paths, timestamps)
├── Assets/                 # All game assets
│   ├── Models/             # 3D models (FBX, OBJ, glTF, GLB, DAE)
│   ├── Textures/           # Image files (PNG, JPG, BMP)
│   ├── Materials/          # Material definitions (.material)
│   ├── Scripts/            # C# game scripts (.cs)
│   ├── Animations/         # Bone animation data (.boneanim)
│   └── Terrain/            # Auto-generated terrain data (.terrain.json)
├── Scenes/                 # Scene files (.scene)
├── Packages/               # Editor extensions and reusable scripts (.cs)
├── Builds/                 # Compiled script assemblies (auto-generated)
│   └── EditorScripts_<timestamp>.dll
├── ProjectSettings/        # Project-level settings
│   └── input.bindings.json # Input axis and action bindings
└── Temp/                   # Temporary working files
```

### project.json
```json
{
  "Id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "Name": "MyGame",
  "RootPath": "C:/Users/Developer/Projects/MyGame",
  "Version": "1.0.0",
  "CreatedUtc": "2026-01-15T10:30:00Z",
  "ModifiedUtc": "2026-02-14T14:20:00Z"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Id` | GUID | Unique project identifier (auto-generated) |
| `Name` | string | Human-readable project name |
| `RootPath` | string | Absolute path to the project folder |
| `Version` | string | Project version string |
| `CreatedUtc` | DateTime | When the project was created |
| `ModifiedUtc` | DateTime | Last modification timestamp (updated by `TouchModified()`) |

### Project Lifecycle

| Action | Menu | Description |
|--------|------|-------------|
| **New Project** | File > New Project | Creates the folder structure, `project.json`, and all subdirectories |
| **Open Project** | File > Open Project | Loads `project.json`, input bindings, compiles extensions, restores last scene |
| **Close** | File > Close | Closes the current project and clears editor state |

### Opening a Project
When a project is opened, the following sequence executes:

1. `project.json` is read and validated
2. The project root path is resolved
3. Input bindings are loaded from `ProjectSettings/input.bindings.json`
4. All scripts in `Assets/` and `Packages/` are compiled via Roslyn
5. Extensions are discovered and their menus are built
6. Extension menus are appended to the editor menu bar
7. The last active scene is restored (if available)
8. The Inspector, Hierarchy, and Project panels are refreshed

### ProjectService API

| Method/Property | Description |
|-----------------|-------------|
| `CreateNew(path, name)` | Create a new project with folder structure |
| `Open(path)` | Load an existing project from `project.json` |
| `Close()` | Close the current project |
| `TouchModified()` | Update the `ModifiedUtc` timestamp |
| `SelectAssetForInspector(path)` | Select an asset file for display in the Inspector |
| `MaterialsLoad(path)` | Load a material from a `.material` file |
| `RootPath` | Current project root directory |
| `AssetsPath` | Path to the `Assets/` folder |
| `ScenesPath` | Path to the `Scenes/` folder |
| `BuildsPath` | Path to the `Builds/` folder |

---

## Scenes

### What Is a Scene?
A scene is a JSON file (`.scene`) containing the complete hierarchy of GameObjects, their components, and all serialized property data. Scenes represent a single level, environment, or state of the game.

### Scene Lifecycle

| Action | How | Shortcut |
|--------|-----|----------|
| **Save** | File > Save Scene | Ctrl+S |
| **Load** | Double-click `.scene` in Project Panel | — |
| **New** | Right-click in Project Panel > New Scene | — |

### Scene Format
```json
{
  "Objects": [
    {
      "Name": "Main Camera",
      "Transform": {
        "Position": [0, 5, -10],
        "Rotation": [15, 0, 0],
        "Scale": [1, 1, 1]
      },
      "Behaviors": [
        {
          "Type": "Camera",
          "Properties": {
            "FieldOfView": 60,
            "Near": 0.1,
            "Far": 1000,
            "Projection": "Perspective",
            "Clear": "Skybox",
            "IsMain": true
          }
        }
      ],
      "Children": []
    },
    {
      "Name": "Directional Light",
      "Transform": {
        "Position": [0, 10, 0],
        "Rotation": [50, -30, 0],
        "Scale": [1, 1, 1]
      },
      "Behaviors": [
        {
          "Type": "Light",
          "Properties": {
            "Type": "Directional",
            "Intensity": 1.0,
            "Color": "#FFFFFFFF",
            "CastShadows": true
          }
        }
      ],
      "Children": []
    }
  ]
}
```

### Serialization Rules

| Category | Rule | Format |
|----------|------|--------|
| **Properties** | Only `[Persist]`-marked properties are saved | Varies by type |
| **Meshes** | Saved by `ModelPath` reference; raw geometry only saved if no model path exists | String path |
| **Materials** | Saved by `MaterialPath` reference to `.material` file | String path |
| **Textures** | Saved as project-relative file paths | String path |
| **Colors** | Saved as hex strings | `#RRGGBBAA` |
| **Vectors** | Saved as JSON arrays | `[x, y, z]` |
| **Enums** | Saved as string names | `"Directional"` |
| **Booleans** | Saved as JSON booleans | `true` / `false` |
| **Numbers** | Saved as JSON numbers | `42`, `3.14` |
| **Lists** | Saved as JSON arrays | `[...]` |
| **Children** | Recursively nested in `Children` array | Nested objects |

### SceneSerialization Internals
The `SceneSerialization` class handles conversion between live `GameObject` hierarchies and JSON DTOs:

**Serialize (Save):**
1. Walks the scene graph depth-first
2. Creates DTOs for each GameObject (name, transform, children)
3. Creates DTOs for each Behavior (type name, persisted properties)
4. Resolves file paths to project-relative format
5. Writes to JSON file

**Deserialize (Load):**
1. Reads JSON and creates DTO objects
2. Creates `GameObject` instances with names
3. Instantiates `Behavior` components by type name (supports both built-in and scripted types)
4. Sets all `[Persist]` properties from the DTO data
5. Resolves model paths → loads meshes via registered resolvers
6. Resolves material paths → loads materials via registered resolvers
7. Calls `PostDeserialize()` on all components
8. Rebuilds the hierarchy (parent-child relationships)

**Registered resolvers (from `Program.cs`):**
- `ResolveMeshesFromModelPath` — multi-mesh resolver using `ModelImporter.ImportModel()`
- `ResolveMeshFromModelPath` — single-mesh fallback (returns first mesh)
- `ResolveMaterialFromPath` — material resolver via `ProjectService.MaterialsLoad()`

---

## SceneService

The `SceneService` manages the runtime scene graph and provides change notification.

| Member | Type | Description |
|--------|------|-------------|
| `Root` | `ObservableCollection<GameObject>` | Top-level scene objects |
| `Changed` | `event` | Fired when the scene is modified (property changes, add/remove) |
| `SaveToFile(path)` | method | Serialize the scene to a `.scene` JSON file |
| `LoadFromFile(path)` | method | Deserialize a `.scene` file and replace the current scene |
| `Add(go)` | method | Add a GameObject to the root collection |
| `Remove(go)` | method | Remove a GameObject from the root collection |
| `NotifyChanged()` | method | Manually trigger a scene change notification |
| `RebuildVegetation()` | method | Rebuild grass/vegetation instances on scene load |

The `Root` collection uses `ObservableCollection` so the Hierarchy panel automatically updates when objects are added or removed.

---

## Undo / Redo

### How It Works
`UndoService` implements a **command pattern** with two stacks:

```
[Undo Stack]  ←── Exec(cmd) pushes here
[Redo Stack]  ←── Undo() pops from undo, pushes here
```

| Action | Shortcut | Description |
|--------|----------|-------------|
| **Undo** | Ctrl+Z | Pops the last command from the undo stack, calls `Undo()`, pushes to redo stack |
| **Redo** | Ctrl+Y | Pops the last command from the redo stack, calls `Do()`, pushes to undo stack |

### ICmd Interface
```csharp
public interface ICmd
{
    void Do();    // Apply the change
    void Undo();  // Reverse the change
}
```

### PropertyChangeCmd
The most common command type. Records the old and new value of a property on any object:

```
PropertyChangeCmd:
  Target: gameObject.Transform
  Property: "Position"
  OldValue: (0, 0, 0)
  NewValue: (5, 0, 0)
```

### After Each Undo/Redo
The following refresh operations occur:
1. `SelectionService.Touch()` — refreshes the Inspector panel to display updated values
2. `SceneService.NotifyChanged()` — triggers a scene repaint in all views

### UndoService API
| Method | Description |
|--------|-------------|
| `Exec(ICmd)` | Execute a command and push to undo stack (clears redo stack) |
| `Undo()` | Undo the last command |
| `Redo()` | Redo the last undone command |
| `RefreshUI()` | Force Inspector and Scene refresh |
| `Clear()` | Clear both stacks |

---

## Selection

### SelectionService
Tracks the currently selected GameObjects with **multi-select support**:

| Member | Type | Description |
|--------|------|-------------|
| `Current` | `GameObject?` | The primary selected object (or null) |
| `Selected` | `List<GameObject>` | All selected objects (for multi-select) |
| `IsMultiSelect` | `bool` | True when multiple objects are selected |
| `Changed` | `event` | Fired when selection changes |

### Selection Methods
| Method | Description |
|--------|-------------|
| `Set(go)` | Select a single object (clears previous selection) |
| `Add(go)` | Add an object to the multi-selection |
| `Remove(go)` | Remove an object from the multi-selection |
| `Toggle(go)` | Toggle an object in/out of the selection |
| `SetMultiple(list)` | Replace the selection with a list of objects |
| `Clear()` | Deselect everything |
| `Touch()` | Refresh the Inspector without changing selection |

### Selection Flow
```
User clicks in Scene View
    │
    ▼
Raycast against scene meshes (Möller-Trumbore)
    │
    ▼
Find closest hit → get owning GameObject
    │
    ▼
SelectionService.Set(hitObject)
    │
    ├─► Inspector Panel updates (shows properties of selected object)
    ├─► Hierarchy Panel highlights the corresponding tree node
    ├─► Scene View shows transform gizmos on the selected object
    └─► Changed event fires (notifies all listeners)
```

**Multi-select:** Hold Ctrl while clicking to add/remove objects from the selection. The Inspector shows shared properties when multiple objects are selected.

---

## Play Mode Snapshots

When pressing **Play** in the Game View, the engine creates a snapshot of the entire scene to allow safe experimentation during play mode.

### Play Mode Lifecycle

**Press Play:**
1. The entire scene graph is serialized to a temporary JSON snapshot
2. All material texture data is captured (textures can be modified at runtime)
3. The game loop starts — all Behaviors receive `Awake()` then `Start()`
4. `Update()`, `FixedUpdate()`, `LateUpdate()` run each frame
5. Input is routed to the game, physics simulation runs
6. Audio sources begin playback (if `PlayOnAwake` is set)
7. Particle emitters begin emitting
8. Animators start their default animation state

**Press Stop:**
1. The game loop stops
2. All Behaviors receive `OnDisable()` then `OnDestroy()`
3. All audio playback is stopped (`AudioManager.StopAll()`)
4. The scene is **deserialized from the snapshot**, restoring the exact pre-play state
5. Material textures are restored
6. The selection, hierarchy, and inspector are refreshed

This ensures that **runtime changes don't persist** — moved objects, modified properties, spawned/destroyed objects all revert on stop.

### Terrain Data During Play/Stop
Terrain data (heights, layers, splatmaps) persists across play/stop cycles because it's stored on disk:

| Phase | Terrain Behavior |
|-------|------------------|
| **Before Play** | Terrain data already saved to `.terrain.json` (auto-saved after each brush stroke) |
| **During Play** | Runtime terrain modifications are possible but transient |
| **After Stop** | Scene restored from snapshot; `Terrain.OnEnable()` reloads from `.terrain.json` |

Editor terrain sculpting (done in Scene View) is always preserved. Runtime terrain modifications during play mode are discarded on stop.

---

## Logging

### Log Levels
The `Log` class provides global logging with 5 severity levels:

| Method | Level | Console Color | Description |
|--------|-------|---------------|-------------|
| `Log.Info(msg)` | Info | Blue | General information |
| `Log.Warning(msg)` | Warning | Yellow | Non-critical issues |
| `Log.Error(msg)` | Error | Red | Errors and exceptions |
| `Log.Success(msg)` | Success | Green | Completed operations |
| `Log.Debug(msg)` | Debug | Gray | Debug output |

### Usage from Scripts
```csharp
// From a Behavior (instance methods)
LogInfo("Player spawned at " + gameObject.Transform.Position);
LogWarning("Health is low: " + health);
LogError("Failed to load resource: " + path);

// From anywhere (static methods)
Log.Info("Scene loaded successfully");
Log.Error("Compilation failed with 3 errors");
```

### Console Panel
All log messages appear in the Console Panel with:
- Color-coded severity icons
- Timestamp
- Source information
- Scrollable message list
- Command input field (`help`, `clear`, `log <message>`)

---

## Default Scene

When a project is opened with no existing scene, a default scene is created to provide a starting point:

| Object | Components | Details |
|--------|------------|---------|
| **Skybox** | Skybox | Gradient sky (top: #1f1f1f, bottom: #0a0a0a), ambient: 0.9, sun elevation: 45° |
| **Main Camera** | Camera | Perspective, FOV 60°, near 0.1, far 1000, at position (0, 5, -10) |
| **Directional Light** | Light | Directional, white, intensity 1.0, shadows enabled |
| **Cube** | MeshFilter + MeshRenderer | Default cube mesh with white material |

This gives users immediate visual feedback with basic lighting, a skybox, and an object to interact with.

---

## Audio System

### AudioManager
The `AudioManager` provides global audio management:

| Feature | Description |
|---------|-------------|
| **Volume Channels** | Master, Music, SFX — independent volume control |
| **Component Registry** | Tracks all `AudioSource` components in the scene |
| **Listener Management** | Single active `AudioListener` per scene |
| **Global Control** | `PlayOneShot()`, `StopAll()`, `PauseAll()`, `ResumeAll()` |

### AudioBackend
Low-level audio playback via **NAudio**:

| Feature | Description |
|---------|-------------|
| **Per-sound playback** | Each sound gets its own `WaveOutEvent` |
| **AudioHandle** | Wraps playback state with volume, pause, resume control |
| **LoopingReader** | `WaveStream` wrapper for seamless audio looping |
| **Path resolution** | Tries absolute, project-relative, Assets folder, filename search |
| **Auto-cleanup** | Handles dispose automatically on playback end |

### Spatial Audio
`AudioSource` components support 3D spatial audio:
- **Distance attenuation** — volume decreases with distance from the `AudioListener`
- **Stereo panning** — sounds pan left/right based on the listener's orientation
- **SpatialBlend** — 0.0 = full 2D (no spatialization), 1.0 = full 3D
- **Min/Max Distance** — configurable attenuation range
- **Channel routing** — each source belongs to Master, Music, or SFX channel

Volume computation: `finalVolume = sourceVolume × channelVolume × masterVolume × distanceAttenuation`
