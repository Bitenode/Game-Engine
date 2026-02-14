# Game Engine — Scripting and Extensibility

## C# Scripting

### Writing a Script
Scripts are standard C# files placed in the project's `Assets/` or `Packages/` folders. They inherit from `Behavior` and override lifecycle methods to add game logic to GameObjects.

```csharp
using Game_Engine.Core;
using System.Numerics;

public class Spinner : Behavior
{
    [Persist] public float Speed { get; set; } = 90f;

    public override void Update()
    {
        var rot = gameObject.Transform.Rotation;
        rot.Y += Speed * Time.DeltaTime;
        gameObject.Transform.Rotation = rot;
    }
}
```

### Lifecycle Methods

| Method | When Called | Frequency |
|--------|------------|-----------|
| `Awake()` | When the component is first created during play mode | Once |
| `Start()` | Before the first Update call | Once |
| `Update()` | Every frame during play mode | Per frame |
| `FixedUpdate()` | At fixed time intervals (physics simulation) | Fixed timestep |
| `LateUpdate()` | After all Update calls finish for the frame | Per frame |
| `OnEnable()` | When the component is enabled or first attached | Each enable |
| `OnDisable()` | When the component is disabled or detached | Each disable |
| `OnDestroy()` | When the component is removed or scene unloads | Once |
| `PostDeserialize()` | After scene deserialization is complete | Once (on load) |

**Lifecycle order during play mode:**
```
Awake() → OnEnable() → Start() → [Update() → FixedUpdate() → LateUpdate()] loop → OnDisable() → OnDestroy()
```

**Internal wrappers:** Each lifecycle method has an internal wrapper (`__Awake()`, `__Start()`, etc.) that handles lifecycle state tracking and the `LogLifecycle` debug feature.

### Persistence
Mark properties with `[Persist]` to have them automatically saved and loaded with the scene:

```csharp
[Persist] public float Speed { get; set; } = 5f;
[Persist] public string Label { get; set; } = "Default";
[Persist] public bool Active { get; set; } = true;
[Persist] public int Count { get; set; } = 10;
[Persist] public Vector3 Offset { get; set; } = Vector3.Zero;
[Persist] public Color Tint { get; set; } = Color.White;
```

**Supported persisted types:**
| Type | Serialization Format | Example |
|------|---------------------|---------|
| `string` | JSON string | `"Hello"` |
| `int` | JSON number | `42` |
| `float` | JSON number | `3.14` |
| `bool` | JSON boolean | `true` |
| `Vector3` | JSON array | `[1.0, 2.0, 3.0]` |
| `Color` | Hex string | `"#FF0000FF"` |
| `enum` | String name | `"Directional"` |
| `List<T>` | JSON array | `[1, 2, 3]` |
| `float[]` | JSON array | `[0.0, 0.5, 1.0]` |

### Required Components
Use `[Require]` to declare component dependencies. Missing components are auto-added when your component is attached to a GameObject:

```csharp
[Require(typeof(MeshFilter))]
[Require(typeof(MeshRenderer))]
public class MyVisualBehavior : Behavior
{
    // MeshFilter and MeshRenderer are guaranteed to exist
    // on the same GameObject
}
```

When `EnsureDependenciesNow()` runs (automatically on attach), any missing required components are created and added. If a required `MeshFilter` is added and has no mesh, a default cube mesh is created.

### Accessing Engine APIs

#### GameObject and Transform
```csharp
// Current game object
gameObject.Name                     // Get/set the object's name
gameObject.Transform.Position       // World position (Vector3)
gameObject.Transform.Rotation       // Euler rotation in degrees (Vector3)
gameObject.Transform.Scale          // Scale factor (Vector3)
gameObject.Children                 // ObservableCollection<GameObject>
gameObject.Parent                   // Parent GameObject (or null)
```

#### Component Access
```csharp
// Find components on the same GameObject
var cam = GetComponent<Camera>();                    // Returns null if not found
var renderer = GetComponentRequired<MeshRenderer>(); // Throws if not found
var filter = GetOrAddComponent<MeshFilter>();         // Creates if not found
bool hasMesh = HasComponent<MeshFilter>();            // Check existence
```

#### Input System (Play Mode Only)
```csharp
// Axis input (smoothed, -1 to 1)
float h = Input.GetAxis("Horizontal");  // A/D or Left/Right
float v = Input.GetAxis("Vertical");    // W/S or Up/Down

// Raw mouse movement
Vector2 mouse = Input.MouseDelta;       // Per-frame pixel delta
float mouseX = Input.GetAxis("Mouse X"); // Raw X delta
float mouseY = Input.GetAxis("Mouse Y"); // Raw Y delta

// Action input
bool jumpDown = Input.GetActionDown("Jump");     // True on the frame Space is pressed
bool jumping = Input.GetAction("Jump");           // True while Space is held
bool jumpUp = Input.GetActionUp("Jump");          // True on the frame Space is released

bool sprinting = Input.GetAction("Sprint");       // Left Shift held
bool fired = Input.GetActionDown("Fire1");         // Left mouse button pressed

// Key input
bool wHeld = Input.GetKey(KeyCode.W);             // Direct key state
bool wDown = Input.GetKeyDown(KeyCode.W);          // Key pressed this frame
bool wUp = Input.GetKeyUp(KeyCode.W);              // Key released this frame

// Mouse button input
bool leftMouse = Input.GetMouseButton(MouseButton.Left);
bool rightDown = Input.GetMouseButtonDown(MouseButton.Right);
```

#### Time
```csharp
float dt = Time.DeltaTime;       // Seconds since last frame
float elapsed = Time.ElapsedTime; // Total elapsed seconds since play started
```

#### Logging
```csharp
// Instance methods (from Behavior)
LogInfo("General information");
LogWarning("Something to watch");
LogError("Something broke");
LogSuccess("Operation completed");
LogDebug("Debug details");

// Global static methods
Log.Info("Global info message");
Log.Warning("Global warning");
Log.Error("Global error");
Log.Success("Global success");
Log.Debug("Global debug");
```

#### Scene Management
```csharp
// Access scene objects
SceneService.Root           // ObservableCollection<GameObject> of top-level objects
SceneService.Add(go);       // Add a GameObject to the root
SceneService.Remove(go);    // Remove a GameObject from the root

// Create objects
var go = new GameObject("MyObject");
go.AddBehavior<MeshFilter>();
go.AddBehavior<MeshRenderer>();
SceneService.Add(go);

// Hierarchy manipulation
parent.AddChild(child);     // Set parent-child relationship
child.RemoveFromParent();   // Detach from parent
```

#### Physics (Play Mode)
```csharp
// Raycasting
if (Physics.Raycast(origin, direction, out var hit, maxDistance))
{
    // hit contains collision information
}

// Overlap queries
var colliders = Physics.OverlapSphere(center, radius);

// Gravity
Vector3 gravity = Physics.Gravity; // Default: (0, -9.81, 0)
```

---

## Compiling Scripts

### Built-In Script Editor
1. Double-click a `.cs` file in the Project Panel to open it in the Script Editor
2. Edit the code (syntax highlighting for C# is provided)
3. Click **Compile** (or press **Ctrl+B**) to compile all project scripts

### Compilation Process
1. All `.cs` files from `Assets/` and `Packages/` directories are collected
2. **Roslyn** (`Microsoft.CodeAnalysis.CSharp` v4.14.0) compiles them into a DLL
3. The output DLL is saved as `Builds/EditorScripts_<timestamp>.dll`
4. The assembly is loaded into a **collectible `AssemblyLoadContext`** (allows unloading)
5. New `Behavior` types are discovered and added to the "Add Component" dropdown
6. New `EditorExtension` types are discovered and their `Contribute()` methods are called
7. The previous assembly's `AssemblyLoadContext` is released for garbage collection (hot-reload)

### Error Handling
Compilation errors appear in the **Console Panel** with:
- File path (project-relative)
- Line number and column
- Error code (e.g., CS0103)
- Error message

Fix the errors in the Script Editor and recompile.

### References
The compiled scripts automatically have access to:
- The engine assembly (all engine types: `Behavior`, `GameObject`, `Input`, `Time`, etc.)
- `System.Numerics` (for `Vector2`, `Vector3`, `Matrix4x4`, etc.)
- Standard .NET libraries

---

## Editor Extensions

Extensions add custom menus and commands to the editor without modifying engine source code.

### Creating an Extension

```csharp
using Game_Engine.Core.Extensibility;

public class MyExtension : EditorExtension
{
    public override void Contribute(EditorUI ui)
    {
        var menu = ui.AddTopMenu("My Tools");

        menu.AddItem("Say Hello", () =>
        {
            Game_Engine.Core.Log.Info("Hello from extension!");
        });

        menu.AddSeparator();

        menu.AddItem("Count Objects", () =>
        {
            int count = Game_Engine.Core.SceneService.Root.Count;
            Game_Engine.Core.Log.Info($"Scene has {count} root objects");
        });

        // Sub-menus
        var sub = menu.AddSubMenu("Advanced");
        sub.AddItem("Sub-option 1", () => { /* ... */ });
        sub.AddItem("Sub-option 2", () => { /* ... */ });
    }
}
```

### Extension API

#### EditorUI
| Method | Description |
|--------|-------------|
| `AddTopMenu(name)` | Add a top-level menu to the editor's menu bar |

#### MenuBuilder
| Method | Description |
|--------|-------------|
| `AddItem(label, action)` | Add a clickable menu item with an action callback |
| `AddSeparator()` | Add a visual separator line between items |
| `AddSubMenu(label)` | Add a nested sub-menu (returns a new MenuBuilder) |

### Menu Model
Menus are represented as a tree structure using `MenuNode`:

| MenuNodeKind | Description |
|--------------|-------------|
| `Separator` | Visual divider between menu items |
| `Menu` | Container for sub-items (sub-menu) |
| `Item` | Clickable leaf item with an action |

**Action kinds:**
- **Command** — executes a registered command by ID
- **Toggle** — toggles a boolean property on the selected object's behavior (reflection-based)
- **Invoke** — calls a method on the selected object's behavior (reflection-based)

### Extension Loading
1. Extensions are discovered automatically when scripts are compiled
2. Any class inheriting from `EditorExtension` in the compiled assembly is instantiated
3. Its `Contribute()` method is called with an `EditorUI` instance
4. Extension menus are appended to the main menu bar
5. Menus are rebuilt on project open/close and recompilation

### Code Examples
The engine ships with example extensions in `Standard Assets/Code Examples/`:

| File | Description |
|------|-------------|
| `BigMenuExtension - No Middleware.cs` | Menu with many items (demonstrates basic menu building) |
| `FullFeatureDemoExtension.cs` | Full demo of menus, sub-menus, separators, and commands |
| `ListAndPropsDemo.cs` | List and property editing demonstration |

---

## Command Registry

Commands are named actions that can be invoked from menus, keyboard shortcuts, or code. They provide a centralized system for editor actions.

### Registering a Command

```csharp
CommandRegistry.Register(new CommandDef
{
    Id = "myext.doSomething",
    DisplayName = "Do Something",
    Execute = () =>
    {
        // Action to perform
        Log.Info("Command executed!");
    },
    CanExecute = () => SelectionService.Current != null
});
```

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Unique command identifier (use namespace prefix) |
| `DisplayName` | `string` | Human-readable name for menus |
| `Execute` | `Action` | The action to perform |
| `CanExecute` | `Func<bool>` | Optional predicate — disables the command when false |

### Invoking a Command

```csharp
CommandRegistry.Execute("myext.doSomething");
```

Commands can be invoked from:
- Extension menus (via `MenuNodeKind.Item` with a Command action)
- Keyboard shortcuts
- Other scripts and commands

---

## Custom Inspectors

Override how a component appears in the Inspector panel for specialized editing experiences.

### Method 1: ICustomInspector Interface

Implement `ICustomInspector` directly on your Behavior:

```csharp
using Avalonia.Controls;

public class MyComponent : Behavior, ICustomInspector
{
    [Persist] public float Value { get; set; } = 1f;

    public void BuildInspector(StackPanel panel)
    {
        // Build custom Avalonia UI for the inspector
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Value
        };
        slider.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                Value = (float)slider.Value;
        };
        panel.Children.Add(slider);
    }
}
```

### Method 2: [CustomInspector] Attribute

Create a separate inspector class without modifying the target component:

```csharp
[CustomInspector(typeof(TargetComponent))]
public class TargetComponentInspector
{
    public void BuildInspector(StackPanel panel, Behavior target)
    {
        var component = (TargetComponent)target;
        // Build custom UI using the target component's data
    }
}
```

This is useful for providing custom inspectors for built-in components or third-party behaviors.

---

## Project Structure for Scripts

```
MyProject/
├── Assets/
│   ├── Scripts/
│   │   ├── PlayerHealth.cs          # Game logic scripts
│   │   ├── EnemyAI.cs
│   │   └── GameManager.cs
│   ├── Models/                       # 3D models
│   ├── Textures/                     # Image files
│   └── Materials/                    # Material definitions
├── Packages/
│   ├── MyExtension.cs               # Editor extensions
│   └── UtilityLib.cs                # Reusable utility scripts
├── Scenes/
│   └── MainScene.scene              # Scene files
├── Builds/
│   └── EditorScripts_20260214.dll   # Auto-generated compiled assembly
├── ProjectSettings/
│   └── input.bindings.json          # Input configuration
└── project.json                     # Project manifest
```

**Organization guidelines:**
- Place game logic scripts in `Assets/Scripts/`
- Place editor extensions and reusable tools in `Packages/`
- Both `Assets/` and `Packages/` are compiled together into a single DLL
- Use `Assets/` for game-specific code and `Packages/` for cross-project utilities

---

## Hot-Reload Workflow

The engine supports a fast iteration workflow:

1. **Edit** a script in the built-in Script Editor (or any external editor)
2. **Compile** with Ctrl+B — Roslyn compiles all scripts
3. **Instant update** — the new assembly is loaded immediately
4. **Test** in Play mode — new behavior logic takes effect without restarting
5. **Iterate** — make changes and recompile as often as needed

**Behind the scenes:**
- The old assembly is loaded in a **collectible `AssemblyLoadContext`**
- When a new assembly is compiled, the old context is released
- The .NET garbage collector eventually unloads the old assembly
- Component instances on existing GameObjects are updated to use the new types
- Extension menus are rebuilt to reflect any changes

**Limitations:**
- Scene data persisted with old type definitions may need re-serialization if property names change
- Adding/removing `[Persist]` properties requires careful migration
