using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Reactive.Linq;         // for .Subscribe(Action<T>), .Skip(...)

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
                var b = new Button { Content = btn.Text, Margin = btn.Margin, HorizontalAlignment = HorizontalAlignment.Stretch };
                if (btn.Primary) b.FontWeight = FontWeight.SemiBold;
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
                    Background = new SolidColorBrush(Color.Parse("#2B2E31")),
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

            var ctb = node as VTextbox;
            if (ctb != null)
            {
                var box = new TextBox
                {
                    Text = ctb.Text ?? "",
                    Watermark = ctb.Placeholder,
                    AcceptsReturn = ctb.Multiline,
                    MinLines = ctb.Multiline ? Math.Max(1, ctb.MinLines) : 1,
                    Margin = ctb.Margin,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                if (ctb.OnChanged != null)
                {
                    box.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == TextBox.TextProperty)
                            ctb.OnChanged(box.Text ?? string.Empty);
                    };
                }
                return box;
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


            return new Canvas();
        }
    }
}
