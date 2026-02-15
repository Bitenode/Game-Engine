using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Reactive.Linq;         // for .Subscribe(Action<T>), .Skip(...)

using AGrid = Avalonia.Controls.Grid;
using ARowDefinitions = Avalonia.Controls.RowDefinitions;
using AColumnDefinitions = Avalonia.Controls.ColumnDefinitions;

namespace Game_Engine.Core.UIX
{
    public static class UIXRenderer
    {
        private static void DisposeWith(this IDisposable d, Control c)
        {
            if (d == null || c == null) return;
            // When the control is detached, kill the subscription.
            c.DetachedFromVisualTree += (_, __) =>
            {
                try { d.Dispose(); } catch { }
            };
        }

        public static Control Render(VNode node)
        {
            if (node == null) return new Canvas();
            var vstack = node as VStack;
            if (vstack != null)
            {
                var sp = new StackPanel { Orientation = Orientation.Vertical, Spacing = vstack.Spacing, Margin = vstack.Margin };
                for (int i = 0; i < vstack.Children.Count; i++) sp.Children.Add(Render(vstack.Children[i]));
                return sp;
            }

            var hstack = node as VHStack;
            if (hstack != null)
            {
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = hstack.Spacing,
                    Margin = hstack.Margin,
                    HorizontalAlignment = hstack.HAlign,
                    VerticalAlignment = hstack.VAlign
                };
                for (int i = 0; i < hstack.Children.Count; i++) sp.Children.Add(Render(hstack.Children[i]));
                return sp;
            }

            var head = node as VHeader;
            if (head != null)
            {
                var tb = new TextBlock
                {
                    Text = head.Text,
                    FontSize = head.Size,
                    FontWeight = head.Bold ? FontWeight.Bold : FontWeight.Normal,
                    Margin = head.Margin
                };
                return tb;
            }

            var tbox = node as VTextbox;
            if (tbox != null)
            {
                var box = new TextBox
                {
                    Text = tbox.Text ?? "",
                    Watermark = tbox.Placeholder,
                    AcceptsReturn = tbox.Multiline,
                    MinLines = tbox.Multiline ? Math.Max(1, tbox.MinLines) : 1,
                    Margin = tbox.Margin,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                if (tbox.OnChanged != null)
                {
                    // Skip initial push so you only get user edits
                    var sub = box.GetObservable(TextBox.TextProperty)
                                 .Skip(1)
                                 .Subscribe(s => tbox.OnChanged(s ?? string.Empty));
                    sub.DisposeWith(box);
                }

                return box;
            }


            var btn = node as VButton;
            if (btn != null)
            {
                var b = new Button
                {
                    Content = btn.Text,
                    Margin = btn.Margin,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(14, 7),
                    FontSize = 13,
                    CornerRadius = new CornerRadius(5)
                };

                if (btn.Primary)
                {
                    b.Background = new SolidColorBrush(Color.Parse("#3478F6"));
                    b.Foreground = Brushes.White;
                    b.FontWeight = FontWeight.SemiBold;
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#5A9AF8"));
                    b.BorderThickness = new Thickness(1);
                }
                else
                {
                    b.Background = new SolidColorBrush(Color.Parse("#444850"));
                    b.Foreground = new SolidColorBrush(Color.Parse("#E0E4EA"));
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#5A5E66"));
                    b.BorderThickness = new Thickness(1);
                }

                if (btn.OnClick != null) b.Click += (_, __) => btn.OnClick();
                return b;
            }

            var sep = node as VSeparator;
            if (sep != null) return new Separator { Margin = sep.Margin };

            var spc = node as VSpacer;
            if (spc != null) return new Border { Height = spc.Height, IsHitTestVisible = false };

            var card = node as VCard;
            if (card != null)
            {
                return new Border
                {
                    Margin = card.Margin,
                    CornerRadius = new CornerRadius(card.Corner),
                    Background = new SolidColorBrush(Color.Parse("#383A40")),
                    Child = Render(card.Child)
                };
            }

            var cb = node as VCheckbox;
            if (cb != null)
            {
                var check = new CheckBox { Content = cb.Label, IsChecked = cb.Value, Margin = cb.Margin };
                if (cb.OnChanged != null)
                    check.Checked += (_, __) => cb.OnChanged(true);
                if (cb.OnChanged != null)
                    check.Unchecked += (_, __) => cb.OnChanged(false);
                return check;
            }

            var sl = node as VSlider;
            if (sl != null)
            {
                var slider = new Slider
                {
                    Minimum = sl.Min,
                    Maximum = sl.Max,
                    Value = sl.Value,
                    Margin = sl.Margin,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                if (sl.Tick.HasValue)
                {
                    slider.TickFrequency = sl.Tick.Value;
                    slider.IsSnapToTickEnabled = sl.SnapToTick;
                    slider.TickPlacement = TickPlacement.BottomRight;
                }

                if (sl.OnChanged != null)
                {
                    var sub = slider.GetObservable(Slider.ValueProperty)
                                    .Skip(1)
                                    .Subscribe(v => sl.OnChanged(v));
                    sub.DisposeWith(slider);
                }

                return slider;
            }

            var lb = node as VListBox;
            if (lb != null)
            {
                var list = new ListBox
                {
                    ItemsSource = lb.Items,
                    Margin = lb.Margin,
                    MinHeight = lb.MinHeight,
                    SelectedIndex = lb.SelectedIndex,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                if (lb.OnSelectionChanged != null)
                {
                    list.SelectionChanged += (_, __) =>
                    {
                        var i = list.SelectedIndex;
                        var s = i >= 0 && i < (lb.Items?.Count ?? 0) ? lb.Items[i] : null;
                        lb.OnSelectionChanged(i, s);
                    };
                }
                return list;
            }

            var mount = node as VMount;
            if (mount != null)
            {
                var panel = new StackPanel { Margin = mount.Margin };
                if (mount.OnReady != null) mount.OnReady(panel);
                return panel;
            }

            // ---------- new widget types ----------

            var combo = node as VComboBox;
            if (combo != null)
            {
                var cb2 = new ComboBox
                {
                    ItemsSource = combo.Items,
                    SelectedIndex = combo.SelectedIndex,
                    Margin = combo.Margin,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                if (combo.OnSelectionChanged != null)
                {
                    cb2.SelectionChanged += (_, __) =>
                    {
                        var i = cb2.SelectedIndex;
                        var s = i >= 0 && i < (combo.Items?.Count ?? 0) ? combo.Items[i] : null;
                        combo.OnSelectionChanged(i, s);
                    };
                }
                return cb2;
            }

            var nf = node as VNumericField;
            if (nf != null)
            {
                var nud = new NumericUpDown
                {
                    Value = (decimal)nf.Value,
                    Minimum = (decimal)nf.Min,
                    Maximum = (decimal)nf.Max,
                    Increment = (decimal)nf.Step,
                    FormatString = nf.Decimals > 0 ? $"F{nf.Decimals}" : "F0",
                    Margin = nf.Margin,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                if (nf.OnChanged != null)
                {
                    var sub = nud.GetObservable(NumericUpDown.ValueProperty)
                                 .Skip(1)
                                 .Subscribe(v => nf.OnChanged((double)(v ?? 0)));
                    sub.DisposeWith(nud);
                }

                if (!string.IsNullOrEmpty(nf.Label))
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0) };
                    row.Children.Add(new TextBlock
                    {
                        Text = nf.Label,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 4, 0)
                    });
                    row.Children.Add(nud);
                    return row;
                }
                return nud;
            }

            var rg = node as VRadioGroup;
            if (rg != null)
            {
                var groupName = $"rg_{Guid.NewGuid():N}";
                var sp2 = new StackPanel
                {
                    Orientation = rg.Horizontal ? Orientation.Horizontal : Orientation.Vertical,
                    Spacing = rg.Horizontal ? 12 : 4,
                    Margin = rg.Margin
                };
                for (int ri = 0; ri < rg.Options.Count; ri++)
                {
                    var idx = ri;
                    var rb = new RadioButton
                    {
                        Content = rg.Options[ri],
                        GroupName = groupName,
                        IsChecked = ri == rg.SelectedIndex
                    };
                    if (rg.OnChanged != null)
                        rb.Checked += (_, __) => rg.OnChanged(idx, rg.Options[idx]);
                    sp2.Children.Add(rb);
                }
                return sp2;
            }

            var pb = node as VProgressBar;
            if (pb != null)
            {
                var bar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = pb.Value,
                    IsIndeterminate = pb.IsIndeterminate,
                    Margin = pb.Margin,
                    Height = 22,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                if (pb.ShowText && !pb.IsIndeterminate)
                {
                    var wrapper = new AGrid { Margin = new Thickness(0) };
                    wrapper.Children.Add(bar);
                    wrapper.Children.Add(new TextBlock
                    {
                        Text = $"{pb.Value:0}%",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 11,
                        Foreground = Brushes.White
                    });
                    return wrapper;
                }
                return bar;
            }

            var sv = node as VScrollViewer;
            if (sv != null)
            {
                var scr = new ScrollViewer
                {
                    Content = sv.Child != null ? Render(sv.Child) : new Canvas(),
                    Margin = sv.Margin,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                if (!double.IsNaN(sv.MaxHeight))
                    scr.MaxHeight = sv.MaxHeight;
                return scr;
            }

            var grid = node as VGrid;
            if (grid != null)
            {
                var g = new AGrid { Margin = grid.Margin };
                g.ColumnDefinitions = new AColumnDefinitions($"{grid.LabelWidth},*");

                for (int gi = 0; gi < grid.Rows.Count; gi++)
                {
                    g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    var lbl = new TextBlock
                    {
                        Text = grid.Rows[gi].Label ?? "",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 4, 8, 4)
                    };
                    AGrid.SetRow(lbl, gi);
                    AGrid.SetColumn(lbl, 0);
                    g.Children.Add(lbl);

                    var editor = Render(grid.Rows[gi].Editor);
                    editor.Margin = new Thickness(0, 4, 0, 4);
                    AGrid.SetRow(editor, gi);
                    AGrid.SetColumn(editor, 1);
                    g.Children.Add(editor);
                }
                return g;
            }

            var exp = node as VExpander;
            if (exp != null)
            {
                var expander = new Expander
                {
                    Header = exp.Header ?? "",
                    IsExpanded = exp.IsExpanded,
                    Margin = exp.Margin,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = exp.Child != null ? Render(exp.Child) : new Canvas()
                };
                return expander;
            }

            var img = node as VImage;
            if (img != null)
            {
                var image = new Avalonia.Controls.Image
                {
                    Margin = img.Margin,
                    Stretch = Stretch.Uniform
                };
                if (!double.IsNaN(img.Width)) image.Width = img.Width;
                if (!double.IsNaN(img.Height)) image.Height = img.Height;

                if (!string.IsNullOrEmpty(img.Source))
                {
                    try
                    {
                        if (System.IO.File.Exists(img.Source))
                            image.Source = new Bitmap(img.Source);
                    }
                    catch { /* silently ignore missing/corrupt images */ }
                }
                return image;
            }

            return new Canvas();
        }
    }
}
