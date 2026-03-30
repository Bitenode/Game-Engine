using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Game_Engine.Core;
using Game_Engine.Core.Extensibility;
using Game_Engine.Core.UIX;
using static Game_Engine.Core.UIX.UIX;

public sealed class ListAndPropsDemo : EditorExtension
{
    // Simple data model for the demo
    private sealed class Thing
    {
        public string Name;
        public List<string> Props = new List<string>();
    }

    private readonly List<Thing> _things = new List<Thing>();
    private int _selected = -1;

    // UI refs (set by VMount)
    private ListBox _listRef;
    private Panel _propsPanel;
    private TextBox _addBox;

    public override void Contribute(EditorUI ui)
    {
        CommandRegistry.Register("demo.listprops", "List + Properties Demo", Open, () => true);
        ui.Menu("Tools").Command("List + Properties Demo", "demo.listprops");
    }

    private void Open()
    {
        // Seed a couple items the first time
        if (_things.Count == 0)
        {
            _things.Add(new Thing { Name = "Player", Props = new List<string>{ "Health", "Speed", "Jump" } });
            _things.Add(new Thing { Name = "Enemy",  Props = new List<string>{ "Damage", "AlertRadius" } });
        }

        // Build UI
        var content =
            Card(
                Stack(
                    Header("List + Properties"),
                    // Add row
                    Row(
                        Mount(p => {
                            // small trick: create a TextBox here so we can keep a reference
                            var tb = new TextBox { Watermark = "New item name…", Width = 180, Margin = new Thickness(12,6,6,6) };
                            _addBox = tb;
                            p.Children.Add(tb);
                        }),
                        Button("Add", OnAdd, primary: true)
                    ).WithMargin(new Thickness(12, 6, 12, 0)),

                    // Main area: left list, right props
                    Row(
                        // Left: List
                        Mount(p => {
                            _listRef = new ListBox
                            {
                                ItemsSource = GetNames(),
                                SelectedIndex = _selected,
                                MinWidth = 180,
                                MinHeight = 180,
                                Margin = new Thickness(12, 6, 6, 6)
                            };
                            _listRef.SelectionChanged += (_, __) => { _selected = _listRef.SelectedIndex; RebuildProps(); };
                            p.Children.Add(_listRef);
                        }),

                        // Right: Properties panel we will fill dynamically
                        Mount(p => { _propsPanel = p; RebuildProps(); })
                    )
                )
            );

        WindowKit.Show(new WindowSpec
        {
            Title       = "List + Properties Demo",
            Width       = 560,
            Height      = 420,
            Utility     = true,
            CloseOnBlur = false,
            DragAnywhere= true,
            Resizable   = true,
            Content     = content
        });
    }

    // ---- Actions ----

    private void OnAdd()
    {
        var name = (_addBox?.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) { Log.Info("Enter a name first."); return; }
        _things.Add(new Thing { Name = name, Props = new List<string> { "PropA", "PropB" } });
        _listRef.ItemsSource = GetNames();
        _selected = _things.Count - 1;
        _listRef.SelectedIndex = _selected;
        _addBox.Text = "";
        RebuildProps();
    }

    // Build (or rebuild) the right-hand properties list based on selection
    private void RebuildProps()
    {
        if (_propsPanel == null) return;
        _propsPanel.Children.Clear();

        if (_selected < 0 || _selected >= _things.Count)
        {
            _propsPanel.Children.Add(new TextBlock { Text = "Select an item to see its properties.", Margin = new Thickness(12) });
            return;
        }

        var t = _things[_selected];

        _propsPanel.Children.Add(new TextBlock
        {
            Text = $"Properties for {t.Name}",
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(12, 6, 12, 6)
        });

        // Show each property as a clickable row; clicking could open another editor, etc.
        foreach (var prop in t.Props)
        {
            var row = new Button { Content = prop, Margin = new Thickness(12, 3, 12, 3), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            row.Click += (_, __) => Log.Info($"Clicked property: {t.Name}.{prop}");
            _propsPanel.Children.Add(row);
        }

        // Add new property
        var addPropBox = new TextBox { Watermark = "New property…", Margin = new Thickness(12, 8, 6, 6) };
        var addPropBtn = new Button { Content = "Add Property", Margin = new Thickness(6, 8, 12, 6) };
        addPropBtn.Click += (_, __) =>
        {
            var pname = (addPropBox.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pname))
            {
                t.Props.Add(pname);
                RebuildProps(); // refresh panel
            }
        };

        var addRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        addRow.Children.Add(addPropBox);
        addRow.Children.Add(addPropBtn);
        _propsPanel.Children.Add(addRow);
    }

    private IList<string> GetNames()
    {
        var arr = new List<string>(_things.Count);
        for (int i = 0; i < _things.Count; i++) arr.Add(_things[i].Name);
        return arr;
    }
}
