using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia.Controls;
using Game_Engine.Core;

namespace Game_Engine.Views;

/// <summary>Marks an instance method as the custom inspector builder for a <see cref="Behavior"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class CustomInspectorAttribute : Attribute { }

/// <summary>Optional interface (users may implement instead of using <see cref="CustomInspectorAttribute"/>).</summary>
public interface ICustomInspector
{
    /// <summary>Return a root Control, or null to fall back to the default inspector.</summary>
    Control? BuildInspectorUI(InspectorContext ctx);
}

/// <summary>Helper handed to user code so they can reuse the built-in editors safely.</summary>
public sealed class InspectorContext
{
    public Behavior Target { get; }
    public Func<PropertyInfo, Control> EditorForProperty { get; }
    public Func<string, Control> Header { get; }
    public Func<string, Control, Control> Row { get; }
    public Func<Control> DefaultInspector { get; }
    public IEnumerable<PropertyInfo> Properties => _props();
    private readonly Func<IEnumerable<PropertyInfo>> _props;

    public InspectorContext(
        Behavior target,
        Func<PropertyInfo, Control> editorForProperty,
        Func<string, Control> header,
        Func<string, Control, Control> row,
        Func<Control> defaultInspector,
        Func<IEnumerable<PropertyInfo>> props)
    {
        Target = target;
        EditorForProperty = editorForProperty;
        Header = header;
        Row = row;
        DefaultInspector = defaultInspector;
        _props = props;
    }
}
