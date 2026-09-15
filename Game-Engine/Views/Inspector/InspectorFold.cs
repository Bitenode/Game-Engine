using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Game_Engine.Core;

namespace Game_Engine.Views.Inspector;

/// <summary>Persists inspector expander state per GameObject while it stays alive.</summary>
static class InspectorFold
{
    sealed class Store
    {
        public bool ObjectOpen = true;
        public bool TransformOpen = true;
        public readonly Dictionary<Behavior, bool> Components = new(ReferenceEqualityComparer.Instance);
    }

    static readonly ConditionalWeakTable<GameObject, Store> ByObject = new();

    static Store For(GameObject go) => ByObject.GetValue(go, static _ => new Store());

    public static bool GetObject(GameObject go) => For(go).ObjectOpen;
    public static void SetObject(GameObject go, bool open) => For(go).ObjectOpen = open;

    public static bool GetTransform(GameObject go) => For(go).TransformOpen;
    public static void SetTransform(GameObject go, bool open) => For(go).TransformOpen = open;

    public static bool GetComponent(GameObject go, Behavior b)
        => For(go).Components.TryGetValue(b, out var open) ? open : true;

    public static void SetComponent(GameObject go, Behavior b, bool open)
        => For(go).Components[b] = open;

    public static string Glyph(bool open) => open ? "▾" : "▸";

    public static TextBlock Chevron(bool open) => new()
    {
        Text = Glyph(open),
        Width = 16,
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.9
    };

    /// <summary>Section fold (Object / Transform). Header click toggles.</summary>
    public static Control Section(string title, Control body, bool expanded, Action<bool> onChanged)
    {
        var open = expanded;
        var chevron = Chevron(open);
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2)
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        header.Children.Add(chevron);
        header.Children.Add(label);

        var host = new Border
        {
            Child = body,
            IsVisible = open,
            Margin = new Thickness(4, 0, 0, 4)
        };

        void Toggle()
        {
            open = !open;
            chevron.Text = Glyph(open);
            host.IsVisible = open;
            onChanged(open);
        }

        header.PointerPressed += (_, e) =>
        {
            Toggle();
            e.Handled = true;
        };
        header.Cursor = new Cursor(StandardCursorType.Hand);

        var root = new StackPanel { Spacing = 0 };
        root.Children.Add(header);
        root.Children.Add(host);
        return root;
    }
}
