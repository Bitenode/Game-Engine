# Game Engine — UIX Framework

## Overview

UIX is a lightweight **declarative UI framework** built on top of Avalonia that provides a virtual-DOM-like API for creating editor windows, dialogs, and custom inspector panels. It separates UI definition (virtual nodes) from rendering (Avalonia control generation) and windowing (window management), making it easy to build rich editor tools from scripts and extensions without directly working with Avalonia's control hierarchy.

### Architecture

```
UIX Static Builder API
    │ creates
    ▼
VNode Tree (virtual nodes — plain data)
    │ rendered by
    ▼
UIXRenderer.Render()
    │ produces
    ▼
Avalonia Control Tree (live UI controls)
    │ hosted in
    ▼
WindowKit.Show()  (standalone window)
 — or —
VMount callback   (embedded in existing panel)
```

**Design principles:**
- **Declarative** — describe what the UI should look like, not how to build it
- **Data-driven** — VNodes are plain data structures with no Avalonia dependencies (except `VMount`)
- **Callback-based** — user interactions are handled via `Action<T>` callbacks
- **Consistent styling** — default margins, colors, and spacing are applied automatically
- **Composable** — nest any VNode inside any container for flexible layouts

---

## Quick Start

### Creating a Simple Window

```csharp
using Game_Engine.Core.UIX;
using static Game_Engine.Core.UIX.UIX;

// Define the UI as a VNode tree
var content = Stack(
    Header("My Tool"),
    Text("This is a custom editor tool."),
    Sep(),
    Checkbox("Enable Feature", true, value => Log.Info($"Feature: {value}")),
    Slider(0, 100, 50, value => Log.Info($"Value: {value}")),
    Button("Apply", () => Log.Info("Applied!"), primary: true)
);

// Show it in a standalone window
WindowKit.Show(new WindowSpec
{
    Title = "My Tool",
    Width = 400,
    Height = 300,
    Content = content
});
```

### Using UIX in an Editor Extension

UIX is commonly used inside editor extensions to create custom tool windows. Extensions are covered in detail in the [Scripting & Extensibility](06_Scripting_And_Extensibility.md) document — below is an example combining both systems:

```csharp
using Game_Engine.Core.Extensibility;
using Game_Engine.Core.UIX;
using static Game_Engine.Core.UIX.UIX;

public class MyToolExtension : EditorExtension
{
    public override void Contribute(EditorUI ui)
    {
        var menu = ui.AddTopMenu("Tools");
        menu.AddItem("Open My Tool", ShowTool);
    }

    private void ShowTool()
    {
        WindowKit.Show(new WindowSpec
        {
            Title = "My Tool",
            Width = 500,
            Height = 400,
            Content = BuildUI()
        });
    }

    private VNode BuildUI()
    {
        return Stack(
            Header("Object Inspector"),
            Grid(new List<(string, VNode)>
            {
                ("Name", Textbox("Player")),
                ("Health", NumericField(100, 0, 999, 1, 0)),
                ("Speed", Slider(0, 20, 5)),
                ("Active", Checkbox("", true))
            }),
            Sep(),
            Row(
                Button("Save", () => Log.Info("Saved")),
                Button("Reset", () => Log.Info("Reset"), primary: true)
            )
        );
    }
}
```

---

## VNode Types Reference

UIX provides **21 virtual node types** organized by category. Each is created via a corresponding static method on the `UIX` class.

### Layout Containers

#### `Stack(params VNode[] kids)` → `VStack`
Vertical stack — arranges children top-to-bottom.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Children` | `List<VNode>` | — | Child nodes (passed as params) |
| `Spacing` | `double` | `6` | Gap between children (pixels) |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

```csharp
Stack(
    Header("Section"),
    Text("Item 1"),
    Text("Item 2")
)
```

#### `Row(params VNode[] kids)` → `VHStack`
Horizontal stack — arranges children left-to-right.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Children` | `List<VNode>` | — | Child nodes |
| `Spacing` | `double` | `6` | Gap between children |
| `HAlign` | `HorizontalAlignment` | `Stretch` | Horizontal alignment |
| `VAlign` | `VerticalAlignment` | `Center` | Vertical alignment |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

```csharp
Row(
    Button("Cancel"),
    Button("OK", OnOk, primary: true)
)
```

#### `Card(VNode child)` → `VCard`
Rounded card container with a dark background (`#383A40`).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Child` | `VNode` | — | Content node |
| `Corner` | `double` | `8` | Corner radius (pixels) |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

```csharp
Card(Stack(
    Header("Settings"),
    Checkbox("Dark Mode", true)
))
```

#### `Scroll(VNode child, double maxHeight)` → `VScrollViewer`
Scrollable container with vertical scrollbar.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Child` | `VNode` | — | Content node |
| `MaxHeight` | `double` | `NaN` | Maximum height before scrolling (NaN = unlimited) |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

```csharp
Scroll(Stack(
    // Many items...
), maxHeight: 300)
```

#### `Expander(string header, VNode child, bool expanded)` → `VExpander`
Collapsible section with a clickable header.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Header` | `string` | — | Section title |
| `Child` | `VNode` | — | Content node (shown when expanded) |
| `IsExpanded` | `bool` | `true` | Initial expanded state |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

```csharp
Expander("Advanced Settings", Stack(
    Checkbox("Debug Mode", false),
    Slider(0, 100, 50)
))
```

#### `Grid(List<(string Label, VNode Editor)> rows, double labelWidth)` → `VGrid`
Two-column form layout with labels on the left and editors on the right.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Rows` | `List<(string, VNode)>` | — | Label-editor pairs |
| `LabelWidth` | `double` | `140` | Fixed width of the label column (pixels) |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

```csharp
Grid(new List<(string, VNode)>
{
    ("Name", Textbox("Player")),
    ("Health", NumericField(100, 0, 999)),
    ("Speed", Slider(0, 20, 5)),
    ("Active", Checkbox("", true))
})
```

### Text & Display

#### `Header(string text, double size, bool bold)` → `VHeader`
Header text with configurable size and weight.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Text` | `string` | — | Header text |
| `Size` | `double` | `16` | Font size (pixels) |
| `Bold` | `bool` | `true` | Bold weight |
| `Margin` | `Thickness` | `(12,12,12,4)` | Outer margin |

#### `Text(string t)` → `VText`
Plain text label.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Text` | `string` | — | Display text |
| `Margin` | `Thickness` | `(12,6,12,6)` | Outer margin |

#### `Image(string source, double width, double height)` → `VImage`
Displays an image from a file path or resource.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Source` | `string` | — | File path or resource URI |
| `Width` | `double` | `NaN` | Display width (NaN = natural) |
| `Height` | `double` | `NaN` | Display height (NaN = natural) |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

#### `ProgressBar(double value, bool indeterminate, bool showText)` → `VProgressBar`
Progress indicator with optional percentage text overlay.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Value` | `double` | `0` | Progress value (0-100) |
| `IsIndeterminate` | `bool` | `false` | Show indeterminate animation |
| `ShowText` | `bool` | `true` | Overlay percentage text |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

### Spacing & Dividers

#### `Sep()` → `VSeparator`
Horizontal line separator.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Margin` | `Thickness` | `(8,4,8,4)` | Outer margin |

#### `Space(double h)` → `VSpacer`
Fixed-height empty space.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Height` | `double` | — | Space height (pixels) |

### Input Controls

#### `Button(string text, Action onClick, bool primary)` → `VButton`
Clickable button with optional primary styling.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Text` | `string` | — | Button label |
| `OnClick` | `Action` | `null` | Click handler |
| `Primary` | `bool` | `false` | Primary style (blue `#3478F6`) vs secondary (`#444850`) |
| `Margin` | `Thickness` | `(12,6,12,6)` | Outer margin |

#### `Checkbox(string label, bool value, Action<bool> onChanged)` → `VCheckbox`
Toggle checkbox with label.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Label` | `string` | — | Checkbox label text |
| `Value` | `bool` | `false` | Initial checked state |
| `OnChanged` | `Action<bool>` | `null` | Called when toggled |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

#### `Textbox(string text, string placeholder, bool multiline, Action<string> onChanged)` → `VTextbox`
Single or multi-line text input.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Text` | `string` | `""` | Initial text value |
| `Placeholder` | `string` | `null` | Watermark text when empty |
| `Multiline` | `bool` | `false` | Enable multi-line editing |
| `MinLines` | `int` | `1` | Minimum visible lines (multiline only) |
| `OnChanged` | `Action<string>` | `null` | Called on text change |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

#### `Slider(double min, double max, double value, Action<double> onChanged, double? tick, bool snap)` → `VSlider`
Numeric slider with optional tick marks and snapping.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Min` | `double` | `0` | Minimum value |
| `Max` | `double` | `1` | Maximum value |
| `Value` | `double` | `0.5` | Initial value |
| `Tick` | `double?` | `null` | Tick mark interval (null = no ticks) |
| `SnapToTick` | `bool` | `false` | Snap to nearest tick |
| `OnChanged` | `Action<double>` | `null` | Called on value change |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

#### `NumericField(double value, double min, double max, double step, int decimals, string label, Action<double> onChanged)` → `VNumericField`
Numeric input with increment/decrement buttons.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Value` | `double` | `0` | Initial value |
| `Min` | `double` | `double.MinValue` | Minimum allowed value |
| `Max` | `double` | `double.MaxValue` | Maximum allowed value |
| `Step` | `double` | `1` | Increment/decrement step size |
| `Decimals` | `int` | `0` | Decimal places shown |
| `Label` | `string` | `null` | Optional prefix label |
| `OnChanged` | `Action<double>` | `null` | Called on value change |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

#### `ComboBox(IEnumerable<string> items, int selected, Action<int, string> onSel)` → `VComboBox`
Dropdown selection list.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Items` | `IList<string>` | `[]` | Available options |
| `SelectedIndex` | `int` | `0` | Initially selected index |
| `OnSelectionChanged` | `Action<int, string>` | `null` | Called with (index, item) on selection |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

#### `ListBox(IEnumerable<string> items, int selected, Action<int, string> onSel)` → `VListBox`
Scrollable list with single selection.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Items` | `IList<string>` | `[]` | List items |
| `SelectedIndex` | `int` | `-1` | Initially selected index (-1 = none) |
| `OnSelectionChanged` | `Action<int, string>` | `null` | Called with (index, item) on selection |
| `MinHeight` | `double` | `120` | Minimum list height |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

#### `RadioGroup(IEnumerable<string> options, int selected, Action<int, string> onChanged, bool horizontal)` → `VRadioGroup`
Mutually exclusive radio button group.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Options` | `IList<string>` | `[]` | Radio option labels |
| `SelectedIndex` | `int` | `0` | Initially selected option |
| `OnChanged` | `Action<int, string>` | `null` | Called with (index, label) on change |
| `Horizontal` | `bool` | `false` | Arrange horizontally instead of vertically |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

### Advanced

#### `Mount(Action<Panel> onReady)` → `VMount`
Mount point for embedding raw Avalonia controls.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `OnReady` | `Action<Panel>` | — | Callback receiving the host panel to add custom controls to |
| `Margin` | `Thickness` | `(0,0,0,0)` | Outer margin |

This is the escape hatch for cases where the VNode types are insufficient. You receive a live Avalonia `Panel` and can add any Avalonia control directly.

```csharp
Mount(panel =>
{
    var canvas = new Canvas { Width = 200, Height = 200 };
    // Draw custom content on canvas...
    panel.Children.Add(canvas);
})
```

---

## Margin Extension Method

Any VNode can have its margin customized using the `WithMargin<T>()` extension method:

```csharp
using Avalonia;

Text("Indented text").WithMargin(new Thickness(24, 6, 12, 6))
Header("Big Header", size: 20).WithMargin(new Thickness(0, 16, 0, 8))
Button("Wide", OnClick).WithMargin(new Thickness(0))
```

The method returns the same node for fluent chaining.

---

## WindowKit — Window Management

### WindowSpec
Configuration object for creating standalone windows.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Title` | `string` | `"Window"` | Window title text |
| `Width` | `double` | `360` | Initial window width |
| `Height` | `double` | `240` | Initial window height |
| `Utility` | `bool` | `true` | Utility style (border-only, no taskbar entry) |
| `CloseOnBlur` | `bool` | `true` | Auto-close when the window loses focus |
| `DragAnywhere` | `bool` | `true` | Click-drag anywhere on content to move the window |
| `Resizable` | `bool` | `true` | Show invisible resize grips at edges and corners |
| `ShowTitleBar` | `bool` | `true` | Show a custom title bar with minimize/maximize/close buttons |
| `Content` | `VNode` | — | The VNode tree to display |

### WindowKit.Show(WindowSpec spec)
Creates and displays a window from a `WindowSpec`.

```csharp
WindowKit.Show(new WindowSpec
{
    Title = "Settings",
    Width = 500,
    Height = 400,
    Utility = false,         // Show in taskbar
    CloseOnBlur = false,     // Stay open when clicking elsewhere
    Resizable = true,
    Content = Stack(
        Header("Application Settings"),
        Grid(new List<(string, VNode)>
        {
            ("Theme", ComboBox(new[] { "Dark", "Light" })),
            ("Font Size", NumericField(14, 8, 32, 1)),
            ("Auto-Save", Checkbox("Enabled", true))
        }),
        Sep(),
        Row(
            Button("Cancel"),
            Button("Save", OnSave, primary: true)
        )
    )
});
```

### Window Chrome
Windows are wrapped with a custom chrome that provides:

| Feature | Description |
|---------|-------------|
| **Title bar** | Custom title bar (`#313338`) with drag-to-move, double-click to maximize/restore |
| **Window buttons** | Minimize (—), Maximize (▢), Close (✕) buttons |
| **Resize grips** | 6px invisible borders at all edges and corners for drag-to-resize |
| **Drag anywhere** | When `DragAnywhere` is true, clicking and dragging anywhere on the content moves the window |
| **Background** | Dark background (`#2B2D31`) matching the editor theme |

---

## UIXRenderer — Rendering Engine

The `UIXRenderer` static class converts VNode trees into live Avalonia controls.

### `Control Render(VNode node)`
Takes any VNode and returns the corresponding Avalonia `Control`. Recursively renders children for container nodes.

### Rendering Implementation Details

| VNode Type | Avalonia Control | Notes |
|------------|-----------------|-------|
| `VStack` | `StackPanel` (Vertical) | Spacing and margin applied |
| `VHStack` | `StackPanel` (Horizontal) | Alignment properties applied |
| `VHeader` | `TextBlock` | FontSize and FontWeight configured |
| `VText` | `TextBlock` | Simple text display |
| `VButton` | `Button` | Primary: `#3478F6`, Secondary: `#444850` |
| `VSeparator` | `Separator` | Horizontal rule |
| `VSpacer` | `Border` | Transparent, fixed height |
| `VCard` | `Border` | Rounded corners, background `#383A40` |
| `VCheckbox` | `CheckBox` | Checked/unchecked event handlers |
| `VTextbox` | `TextBox` | Watermark, multiline, reactive text change |
| `VSlider` | `Slider` | Tick frequency, snap-to-tick support |
| `VListBox` | `ListBox` | Selection change handler |
| `VMount` | `StackPanel` | Calls `OnReady` with the panel |
| `VComboBox` | `ComboBox` | Selection change handler |
| `VNumericField` | `NumericUpDown` | Optional label prefix, reactive changes |
| `VRadioGroup` | `StackPanel` + `RadioButton`s | Unique group name via GUID |
| `VProgressBar` | `Grid` + `ProgressBar` | Optional text overlay |
| `VScrollViewer` | `ScrollViewer` | Vertical only, optional max height |
| `VGrid` | `Grid` (2-column) | Label width + auto editor column |
| `VExpander` | `Expander` | Collapsible with header |
| `VImage` | `Image` | Loads from file, silently handles errors |

### Reactive Subscriptions
`TextBox`, `Slider`, and `NumericUpDown` controls use Avalonia's `GetObservable()` with `.Skip(1)` to avoid firing the callback for the initial value. Subscriptions are automatically disposed when the control is detached from the visual tree via the `DisposeWith` extension method.

---

## Complete Example — Custom Tool Window

```csharp
using Game_Engine.Core;
using Game_Engine.Core.Extensibility;
using Game_Engine.Core.UIX;
using static Game_Engine.Core.UIX.UIX;

public class SceneStatsExtension : EditorExtension
{
    public override void Contribute(EditorUI ui)
    {
        var menu = ui.AddTopMenu("Diagnostics");
        menu.AddItem("Scene Statistics", ShowStats);
    }

    private void ShowStats()
    {
        int objCount = SceneService.Root.Count;
        var selected = SelectionService.Current;

        WindowKit.Show(new WindowSpec
        {
            Title = "Scene Statistics",
            Width = 420,
            Height = 360,
            CloseOnBlur = false,
            Content = Stack(
                Header("Scene Statistics", size: 18),
                Sep(),
                Card(Stack(
                    Grid(new List<(string, VNode)>
                    {
                        ("Root Objects", Text($"{objCount}")),
                        ("Selected", Text(selected?.Name ?? "(none)")),
                    })
                )),
                Space(8),
                Expander("Performance", Stack(
                    ProgressBar(75, showText: true),
                    Text("GPU Memory: 128 MB")
                )),
                Space(12),
                Row(
                    Button("Refresh", ShowStats),
                    Button("Close", () => { }, primary: true)
                )
            )
        });
    }
}
```

---

## Built-In Code Examples

The engine ships with working UIX examples in `Standard Assets/Code Examples/` (see also the [Scripting & Extensibility](06_Scripting_And_Extensibility.md) doc for the full extension system):

| File | UIX Features Demonstrated |
|------|---------------------------|
| `FullFeatureDemoExtension.cs` | All 21 UIX widget types showcased in a single `WindowKit` window |
| `ListAndPropsDemo.cs` | `VListBox` + `VGrid` property editing pattern, `VMount` for custom Avalonia controls |

These are runnable examples — compile them with **Ctrl+B** and they appear in the editor's menu bar.

---

## Styling Reference

### Default Colors
| Element | Color | Hex |
|---------|-------|-----|
| Window background | Dark gray | `#2B2D31` |
| Title bar | Slightly lighter gray | `#313338` |
| Card background | Medium gray | `#383A40` |
| Primary button | Blue | `#3478F6` |
| Secondary button | Gray | `#444850` |

### Default Margins
| Element | Margin (L, T, R, B) |
|---------|---------------------|
| Header | `(12, 12, 12, 4)` |
| Text | `(12, 6, 12, 6)` |
| Button | `(12, 6, 12, 6)` |
| Separator | `(8, 4, 8, 4)` |
| All others | `(0, 0, 0, 0)` |

### Default Spacing
| Container | Spacing |
|-----------|---------|
| VStack | `6px` |
| VHStack | `6px` |

### Window Chrome
| Element | Size |
|---------|------|
| Title bar height | `32px` |
| Resize grip size | `6px` |
