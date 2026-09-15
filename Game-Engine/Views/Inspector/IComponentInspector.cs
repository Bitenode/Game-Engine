using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Game_Engine.Core;

namespace Game_Engine.Views.Inspector;

/// <summary>
/// Built-in per-component inspector: hide default rows and/or add extra chrome.
/// Extra chrome above the property body stays enabled when the component is disabled
/// (same layout as the previous type-switch in <c>EditorForBehavior</c>).
/// </summary>
public interface IComponentInspector
{
    bool CanHandle(Behavior b);
    bool IsHiddenProperty(Behavior b, string propertyName);
    IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, Behavior b);
    IEnumerable<Control> BuildBodySuffix(IInspectorHost host, Behavior b);
}

/// <summary>Typed adapter with empty defaults.</summary>
public abstract class ComponentInspector<T> : IComponentInspector where T : Behavior
{
    static readonly Control[] None = Array.Empty<Control>();
    static readonly HashSet<string> NoHidden = new(StringComparer.Ordinal);

    public virtual bool CanHandle(Behavior b) => b is T;

    protected virtual IReadOnlyCollection<string> HiddenProperties => NoHidden;

    public virtual bool IsHiddenProperty(Behavior b, string propertyName)
        => HiddenProperties.Count > 0 && HiddenProperties.Contains(propertyName);

    public IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, Behavior b)
        => b is T t ? BuildChrome(host, owner, t) : None;

    public IEnumerable<Control> BuildBodySuffix(IInspectorHost host, Behavior b)
        => b is T t ? BuildBodySuffix(host, t) : None;

    protected virtual IEnumerable<Control> BuildChrome(IInspectorHost host, GameObject owner, T target)
        => None;

    protected virtual IEnumerable<Control> BuildBodySuffix(IInspectorHost host, T target)
        => None;
}
