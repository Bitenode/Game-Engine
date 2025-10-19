/*using Game_Engine.Core;

public class NewBehaviour : Behavior, ICustomInspector
{
    [Persist] public float Test { get; set; } = 1.0f;

    public Control? BuildInspectorUI(InspectorContext ctx)
    {
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(ctx.Header("My Custom Block"));
        // reuse built-in editor for the 'Test' property:
        var p = typeof(NewBehaviour).GetProperty(nameof(Test));
        root.Children.Add(ctx.Row("Cool Float", ctx.EditorForProperty(p)));
        // Or include the default panel as well:
        // root.Children.Add(ctx.DefaultInspector());
        return root;
    }
}*/