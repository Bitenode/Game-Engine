# Game Engine — Scene and Project Management

## Projects

### What Is a Project?
A project is a folder on disk containing all game assets, scenes, scripts, and configuration. The `project.json` file at the root identifies the project.

### Project Structure
```
MyGame/
├── project.json         # Project metadata (name, ID, version, timestamps)
├── Assets/              # All game assets
│   ├── Models/          # 3D models (FBX, OBJ, GLTF)
│   ├── Textures/        # Image files (PNG, JPG)
│   ├── Materials/       # Material definitions (.material)
│   ├── Scripts/         # C# scripts (.cs)
│   └── Terrain/         # Auto-generated terrain data (.terrain.json)
├── Scenes/              # Scene files (.scene)
├── Packages/            # Editor extensions and reusable scripts
├── Builds/              # Compiled script DLLs (auto-generated)
└── Temp/                # Temporary working files
```

### project.json
```json
{
  "Id": "a1b2c3d4-...",
  "Name": "MyGame",
  "RootPath": "C:/Users/.../MyGame",
  "Version": "1.0.0",
  "CreatedUtc": "2026-01-15T10:30:00Z",
  "ModifiedUtc": "2026-02-09T14:20:00Z"
}
```

### Project Lifecycle
| Action          | Menu               | Description                        |
|-----------------|--------------------|------------------------------------|
| **New Project** | File > New Project | Creates folder structure + project.json |
| **Open Project**| File > Open Project| Loads project.json and restores state    |
| **Close**       | File > Close       | Closes the current project               |

When opening a project:
1. `project.json` is read
2. Input bindings are loaded from project settings
3. Extensions are compiled and loaded
4. The last active scene is restored (if available)

---

## Scenes

### What Is a Scene?
A scene is a JSON file (`.scene`) containing the complete hierarchy of GameObjects, their components, and serialized property data.

### Scene Lifecycle
| Action   | How                                  |
|----------|--------------------------------------|
| **Save** | Ctrl+S or File > Save Scene          |
| **Load** | Double-click `.scene` in Project Panel |
| **New**  | Right-click in Project Panel > New Scene |

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
            "NearClip": 0.1,
            "FarClip": 1000
          }
        }
      ],
      "Children": []
    }
  ]
}
```

### Serialization Rules
| Category       | Rule                                          |
|----------------|-----------------------------------------------|
| **Properties** | Only `[Persist]`-marked properties are saved  |
| **Meshes**     | Saved by `ModelPath` reference; geometry only saved if no model path exists |
| **Materials**   | Saved by `MaterialPath` reference to `.material` file |
| **Textures**    | Saved as project-relative file paths          |
| **Colors**      | Saved as hex strings (`#RRGGBBAA`)            |
| **Vectors**     | Saved as arrays (`[x, y, z]`)                |
| **Enums**       | Saved as string names                         |
| **Children**    | Recursively nested in `Children` array        |

### SceneSerialization Internals
The `SceneSerialization` class converts between live `GameObject` hierarchies and JSON DTOs:
- **Serialize**: Walks the scene graph, creates DTOs for each GameObject and Behavior, resolves paths to project-relative
- **Deserialize**: Creates GameObjects, instantiates Behaviors by type name, sets `[Persist]` properties, resolves model/material/texture paths

---

## Undo / Redo

### How It Works
`UndoService` implements a command pattern with two stacks:

```
[Undo Stack]  ←── Exec(cmd) pushes here
[Redo Stack]  ←── Undo() pops from undo, pushes here
```

| Action     | Shortcut | Description                      |
|------------|----------|----------------------------------|
| **Undo**   | Ctrl+Z   | Reverses the last command        |
| **Redo**   | Ctrl+Y   | Re-applies the last undone command |

### ICmd Interface
```csharp
public interface ICmd
{
    void Do();    // Apply the change
    void Undo();  // Reverse the change
}
```

### PropertyChangeCmd
The most common command type. Records the old and new value of a property:
```
PropertyChangeCmd:
  Target: gameObject.Transform
  Property: "Position"
  OldValue: (0, 0, 0)
  NewValue: (5, 0, 0)
```

After each undo/redo:
- `SelectionService.Touch()` refreshes the Inspector
- `SceneService.NotifyChanged()` triggers a scene repaint

---

## Selection

### SelectionService
Tracks the currently selected `GameObject`:
- `SelectionService.Current` — the selected object (or null)
- `SelectionService.Changed` — event fired on selection change
- `SelectionService.Set(go)` — change selection
- `SelectionService.Touch()` — refresh without changing selection

### Selection Flow
```
User clicks in Scene View
    │
    ▼
Raycast against scene meshes
    │
    ▼
Find closest hit → get owning GameObject
    │
    ▼
SelectionService.Set(hitObject)
    │
    ├─► Inspector Panel updates (shows properties)
    ├─► Hierarchy Panel highlights the node
    ├─► Scene View shows gizmos
    └─► Changed event fires
```

---

## Play Mode Snapshots

When pressing **Play** in the Game View:

1. **Snapshot**: The entire scene graph is serialized to a temporary JSON snapshot, including all material texture data
2. **Play**: Behaviors run their lifecycle (Awake -> Start -> Update loop)
3. **Stop**: The scene is deserialized from the snapshot, restoring the exact pre-play state

This ensures that runtime changes (moved objects, modified properties) don't persist after stopping. Material textures are specifically tracked and restored because play mode can modify material state.

### Terrain Data During Play/Stop
Terrain data (heights, layers, splatmaps) persists across play/stop cycles because it is stored in the `.terrain.json` asset file on disk:
- **Before Play**: Terrain data is already saved to `.terrain.json` (auto-saved after each brush stroke)
- **During Play**: Runtime terrain modifications are possible but transient
- **After Stop**: The scene is restored from the snapshot. `Terrain.OnEnable()` reloads data from the `.terrain.json` file, restoring the pre-play terrain state

This means terrain sculpting done in the editor (Scene View) is always preserved, while runtime terrain modifications during play mode are discarded on stop.

---

## Logging

### Log Levels
```csharp
Log.Info("General information");
Log.Warning("Non-critical issue");
Log.Error("Error occurred");
Log.Success("Operation completed");
Log.Debug("Debug details");
```

All log messages appear in the Console Panel with appropriate color coding. The `Log` class is globally accessible from any script or engine code.

---

## Default Scene

When a project is opened with no existing scene, a default scene is created:

| Object            | Components                      |
|-------------------|---------------------------------|
| **Skybox**        | Skybox (gradient sky)           |
| **Main Camera**   | Camera (perspective, FOV 60)    |
| **Directional Light** | Light (directional, white) |
| **Cube**          | MeshFilter + MeshRenderer       |

This gives users a starting point with basic lighting and an object to interact with.
