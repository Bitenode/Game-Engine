# Game Engine — Scripting and Extensibility

## C# Scripting

### Writing a Script
Scripts are standard C# files placed in the project's `Assets/` or `Packages/` folders. They inherit from `Behavior` and override lifecycle methods.

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

| Method          | When Called                                     |
|-----------------|-------------------------------------------------|
| `Awake()`       | Once, when the component is first created       |
| `Start()`       | Once, before the first Update call              |
| `Update()`      | Every frame during play mode                    |
| `FixedUpdate()`  | At fixed time intervals (physics simulation)   |
| `LateUpdate()`   | After all Update calls finish                  |
| `OnEnable()`     | When the component is enabled                  |
| `OnDisable()`    | When the component is disabled                 |
| `OnDestroy()`    | When the component is removed or scene unloads |

### Persistence
Mark properties with `[Persist]` to have them automatically saved and loaded with the scene:

```csharp
[Persist] public float Speed { get; set; } = 5f;
[Persist] public string Label { get; set; } = "Default";
[Persist] public bool Active { get; set; } = true;
[Persist] public int Count { get; set; } = 10;
```

Supported persisted types: `string`, `int`, `float`, `bool`, `Vector3`, `Color`, enums.

### Required Components
Use `[Require]` to declare component dependencies. Missing components are auto-added:

```csharp
[Require(typeof(MeshFilter))]
[Require(typeof(MeshRenderer))]
public class MyVisualBehavior : Behavior { }
```

### Accessing Engine APIs

```csharp
// Current game object
gameObject.Name
gameObject.Transform.Position
gameObject.Children
gameObject.Parent

// Find components
var cam = GetComponent<Camera>();
var renderer = GetOrAddComponent<MeshRenderer>();

// Input (during play mode)
float h = Input.GetAxis("Horizontal");
float v = Input.GetAxis("Vertical");
bool jumped = Input.GetActionDown("Jump");
Vector2 mouse = Input.MouseDelta;

// Time
float dt = Time.DeltaTime;
float elapsed = Time.ElapsedTime;

// Logging
Log("Something happened");
LogWarning("Watch out");
LogError("Something broke");

// Scene
SceneService.Root  // top-level GameObjects
SceneService.Add(newGameObject);
SceneService.Remove(gameObject);
```

---

## Compiling Scripts

### Built-In Script Editor
1. Double-click a `.cs` file in the Project Panel to open it
2. Edit the code in the Script Editor window
3. Click **Compile** (or press **Ctrl+B**) to compile all project scripts

### Compilation Process
1. All `.cs` files from `Assets/` and `Packages/` are collected
2. Roslyn compiles them into a DLL: `Builds/EditorScripts_<timestamp>.dll`
3. The assembly is loaded into a collectible `AssemblyLoadContext`
4. New Behavior types become available in the "Add Component" dropdown
5. Old assemblies are unloaded (hot-reload)

### Error Handling
Compilation errors appear in the **Console Panel** with file path, line number, and error message. Fix the error and recompile.

---

## Editor Extensions

Extensions add custom menus and commands to the editor.

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
    }
}
```

### Extension API

#### EditorUI
| Method          | Description                          |
|-----------------|--------------------------------------|
| `AddTopMenu(name)` | Add a top-level menu to the menu bar |

#### MenuBuilder
| Method                | Description                          |
|-----------------------|--------------------------------------|
| `AddItem(label, action)` | Add a clickable menu item         |
| `AddSeparator()`         | Add a visual separator            |
| `AddSubMenu(label)`      | Add a nested sub-menu             |

### Extension Loading
Extensions are discovered automatically when scripts are compiled. Any class inheriting from `EditorExtension` is instantiated and its `Contribute()` method is called. Extension menus appear in the main menu bar.

### Code Examples
The engine ships with example extensions in `Standard Assets/Code Examples/`:

| File                              | Description                          |
|-----------------------------------|--------------------------------------|
| `BigMenuExtension - No Middleware.cs` | Menu with many items (no middleware) |
| `FullFeatureDemoExtension.cs`     | Full demo of menus, submenus, commands |
| `ListAndPropsDemo.cs`             | List and property editing demo       |

---

## Command Registry

Commands are named actions that can be invoked from menus, shortcuts, or code.

### Registering a Command

```csharp
CommandRegistry.Register(new CommandDef
{
    Id = "myext.doSomething",
    DisplayName = "Do Something",
    Execute = () => { /* action */ },
    CanExecute = () => SelectionService.Current != null
});
```

### Invoking a Command

```csharp
CommandRegistry.Execute("myext.doSomething");
```

---

## Custom Inspectors

Override how a component appears in the Inspector panel.

### Using ICustomInspector

```csharp
public class MyComponent : Behavior, ICustomInspector
{
    [Persist] public float Value { get; set; } = 1f;

    public void BuildInspector(StackPanel panel)
    {
        // Build custom Avalonia UI for the inspector
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = Value };
        slider.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                Value = (float)slider.Value;
        };
        panel.Children.Add(slider);
    }
}
```

### Using [CustomInspector] Attribute
Alternatively, mark a separate class with `[CustomInspector(typeof(TargetComponent))]` to provide a custom inspector for any component type without modifying the component itself.

---

## Project Structure for Scripts

```
MyProject/
├── Assets/
│   ├── Scripts/
│   │   ├── PlayerHealth.cs
│   │   ├── EnemyAI.cs
│   │   └── GameManager.cs
│   └── ...
├── Packages/
│   ├── MyExtension.cs
│   └── UtilityLib.cs
├── Scenes/
│   └── MainScene.scene
├── Builds/
│   └── EditorScripts_20260209.dll  (auto-generated)
└── project.json
```

Scripts in both `Assets/` and `Packages/` are compiled together. Use `Assets/` for game logic and `Packages/` for reusable tools and extensions.
