using System.Collections.Generic;
using Avalonia.Controls;

namespace Game_Engine.Docking;

public enum DockRegion { Left, Center, Right, BottomLeft, Bottom }

public sealed class DockManager
{
    private readonly TabControl _left, _center, _right, _bottomLeft, _bottom;

    private sealed class Registration
    {
        public TabItem Tab = null!;
        public DockRegion Home;
        public Window? FloatWindow;
        public string Header = "";
        public Control Content = null!;
    }

    private readonly Dictionary<Control, Registration> _map = new();

    public DockManager(TabControl left, TabControl center, TabControl right,
                       TabControl bottomLeft, TabControl bottom)
    {
        _left = left; _center = center; _right = right;
        _bottomLeft = bottomLeft; _bottom = bottom;
    }

    public void Add(Control content, string header, DockRegion region)
    {
        var tab = new TabItem { Header = header, Content = content };
        Host(region).Items.Add(tab);
        _map[content] = new Registration
        {
            Tab = tab,
            Home = region,
            Header = header,
            Content = content
        };
    }

    public void DockTo(Control content, DockRegion region)
    {
        if (!_map.TryGetValue(content, out var reg)) return;

        // Remove from current host, if any
        if (reg.Tab.Parent is TabControl oldHost)
            oldHost.Items.Remove(reg.Tab);

        // If floating, detach content and close the window without callbacks
        if (reg.FloatWindow is ToolWindow tw)
        {
            if (ReferenceEquals(tw.Content, reg.Content))
                tw.Content = null;      // avoid "already has a visual parent"
            tw.CloseFromManager();
            reg.FloatWindow = null;
        }

        reg.Tab.Content = reg.Content;
        Host(region).Items.Add(reg.Tab);
        reg.Home = region;
    }

    public void Float(Control content)
    {
        if (!_map.TryGetValue(content, out var reg)) return;

        if (reg.Tab.Parent is TabControl oldHost)
            oldHost.Items.Remove(reg.Tab);

        reg.Tab.Content = null; // content moves into floating window

        // On user close: REMOVE the panel, do NOT re-dock.
        var win = new ToolWindow(reg.Header, reg.Content, () => Close(content));
        reg.FloatWindow = win;
        win.Show();
    }

    public void Close(Control content)
    {
        if (!_map.TryGetValue(content, out var reg)) return;

        // Remove from host
        if (reg.Tab.Parent is TabControl host)
            host.Items.Remove(reg.Tab);

        // If floating, detach content then close without recursion
        if (reg.FloatWindow is ToolWindow tw)
        {
            if (ReferenceEquals(tw.Content, reg.Content))
                tw.Content = null;
            tw.CloseFromManager();
        }

        reg.FloatWindow = null;
        _map.Remove(content);
    }

    private TabControl Host(DockRegion r) => r switch
    {
        DockRegion.Left => _left,
        DockRegion.Center => _center,
        DockRegion.Right => _right,
        DockRegion.BottomLeft => _bottomLeft,
        _ => _bottom
    };
}
