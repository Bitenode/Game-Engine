/*using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Game_Engine.Core;
using Game_Engine.Core.Extensibility;
using Game_Engine.Core.UIX;
using static Game_Engine.Core.UIX.UIX;

public sealed class FullFeatureDemoExtension : EditorExtension
{
    // ---- basic widget state ----
    bool   _enabled  = true;
    double _strength = 0.35;
    string _note     = "hello world";

    // ---- list + properties demo state ----
    private sealed class Thing
    {
        public string Name;
        public List<string> Props = new List<string>();
    }
    private readonly List<Thing> _things = new List<Thing>();
    private int _selected = -1;

    // UI refs (wired via Mount)
    private ListBox _listRef;
    private Panel   _propsPanel;
    private TextBox _addBox;

    public override void Contribute(EditorUI ui)
    {
        CommandRegistry.Register("demo.full", "Full Feature Demo…", OpenDemoWindow, () => true);
        ui.Menu("Tools").Command("Full Feature Demo…", "demo.full");
    }

    private void OpenDemoWindow()
    {
        // seed data once
        if (_things.Count == 0)
        {
            _things.Add(new Thing { Name = "Player", Props = new List<string> { "Health", "Speed", "Jump" } });
            _things.Add(new Thing { Name = "Enemy",  Props = new List<string> { "Damage", "AlertRadius" } });
            _selected = 0;
        }

        var content =
            Card(
                Stack(
                    Header("UIX Widgets"),
                    Text("Everything here is built with the middleware UIX nodes — no Avalonia controls for the basic bits."),

                    Checkbox("Enabled", _enabled, v =>
                    {
                        _enabled = v;
                        Log.Info("Enabled = " + v);
                    }),

                    Slider(0, 1, _strength, v =>
                    {
                        _strength = v;
                        Log.Info("Strength = " + _strength.ToString("0.00"));
                    }, tick: 0.05, snap: true),

                    Textbox(_note, "Type a note…", multiline: true, onChanged: s =>
                    {
                        _note = s ?? "";
                        Log.Info("Note (" + Math.Min(_note.Length, 24) + " chars) updated");
                    }),

                    Sep(),

                    Row(
                        Button("Popup Menu…", ShowPopupMenu, primary: true),
                        Button("OK",     () => Log.Info("OK clicked")),
                        Button("Cancel", () => Log.Info("Cancel clicked"))
                    ).WithMargin(new Thickness(12, 8, 12, 12)),

                    Sep(),

                    // -------- List + Properties demo ----------
                    Header("List + Properties"),

                    // Add row
                    Row(
                        Mount(p =>
                        {
                            var tb = new TextBox { Watermark = "New item name…", Width = 180, Margin = new Thickness(12, 6, 6, 6) };
                            _addBox = tb;
                            p.Children.Add(tb);
                        }),
                        Button("Add", OnAdd, primary: true)
                    ).WithMargin(new Thickness(12, 6, 12, 0)),

                    // Main area: left list, right props
                    Row(
                        // Left: ListBox (hosted via Mount so we can set ItemsSource)
                        Mount(p =>
                        {
                            _listRef = new ListBox
                            {
                                ItemsSource   = GetNames(),
                                SelectedIndex = _selected,
                                MinWidth      = 200,
                                MinHeight     = 180,
                                Margin        = new Thickness(12, 6, 6, 12)
                            };
                            _listRef.SelectionChanged += (_, __) =>
                            {
                                _selected = _listRef.SelectedIndex;
                                RebuildProps();
                            };
                            p.Children.Add(_listRef);
                        }),

                        // Right: properties panel we repopulate based on selection
                        Mount(p =>
                        {
                            _propsPanel = p;
                            _propsPanel.Margin = new Thickness(6, 6, 12, 12);
                            RebuildProps();
                        })
                    )
                )
            );

        WindowKit.Show(new WindowSpec
        {
            Title        = "Full Feature Demo",
            Width        = 640,
            Height       = 480,
            Utility      = true,
            CloseOnBlur  = false,
            DragAnywhere = true,
            Resizable    = true,
            ShowTitleBar = true,
            Content      = content
        });
    }

    // ---------- Popup menu (no ToggleMenuItem dependency) ----------
    private void ShowPopupMenu()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var anchor = lifetime?.MainWindow as Control;
        if (anchor == null) return;

        var actions = new MenuItem { Header = "Actions" };
        actions.ItemsSource = new object[]
        {
            MakeItem("Log Enabled",  _enabled,  () => Log.Info("Enabled = " + _enabled)),
            MakeItem("Log Strength", false,     () => Log.Info("Strength = " + _strength.ToString("0.00"))),
            new Separator()
        };

        var strength = new MenuItem { Header = "Strength" };
        strength.ItemsSource = new object[]
        {
            MakeItem("Low (0.25)",    Math.Abs(_strength - 0.25) < 0.001, () => _strength = 0.25),
            MakeItem("Medium (0.50)", Math.Abs(_strength - 0.50) < 0.001, () => _strength = 0.50),
            MakeItem("High (0.75)",   Math.Abs(_strength - 0.75) < 0.001, () => _strength = 0.75),
        };

        var cm = new ContextMenu
        {
            ItemsSource = new object[]
            {
                actions,
                strength,
                new Separator(),
                MakeItem("Close (does nothing)", false, () => {  })
            },
            PlacementTarget = anchor,
            Placement = PlacementMode.Pointer
        };

        cm.Open(anchor);

        // Local helper: checkable item (works on all Avalonia versions)
        MenuItem MakeItem(string header, bool isChecked, Action onClick)
        {
            var mi = new MenuItem { Header = header };
            mi.Icon = new CheckBox
            {
                IsChecked = isChecked,
                IsHitTestVisible = false,
                Focusable = false,
                Margin = new Thickness(0, 0, 6, 0)
            };
            mi.Click += (_, __) => onClick();
            return mi;
        }
    }

    // ---------- List + Properties helpers ----------
    private void OnAdd()
    {
        var name = (_addBox?.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Log.Info("Enter a name first.");
            return;
        }
        _things.Add(new Thing { Name = name, Props = new List<string> { "PropA", "PropB" } });
        _listRef.ItemsSource = GetNames();
        _selected = _things.Count - 1;
        _listRef.SelectedIndex = _selected;
        _addBox.Text = "";
        RebuildProps();
    }

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
            Margin = new Thickness(4, 2, 4, 8)
        });

        foreach (var prop in t.Props)
        {
            var row = new Button
            {
                Content = prop,
                Margin = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            row.Click += (_, __) => Log.Info($"Clicked property: {t.Name}.{prop}");
            _propsPanel.Children.Add(row);
        }

        // add-new-property row
        var addPropBox = new TextBox { Watermark = "New property…", Margin = new Thickness(4, 8, 4, 4) };
        var addPropBtn = new Button { Content = "Add Property", Margin = new Thickness(4, 8, 4, 4) };
        addPropBtn.Click += (_, __) =>
        {
            var pname = (addPropBox.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pname))
            {
                t.Props.Add(pname);
                RebuildProps();
            }
        };
        var addRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        addRow.Children.Add(addPropBox);
        addRow.Children.Add(addPropBtn);
        _propsPanel.Children.Add(addRow);
    }

    private IList<string> GetNames()
    {
        var list = new List<string>(_things.Count);
        for (int i = 0; i < _things.Count; i++) list.Add(_things[i].Name);
        return list;
    }
}*/
