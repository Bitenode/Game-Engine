using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Layout;

namespace Game_Engine.Core.UIX
{
    // ---------- Virtual nodes (no Avalonia types here) ----------

    public abstract class VNode { }

    public sealed class VStack : VNode
    {
        public readonly List<VNode> Children = new List<VNode>();
        public Thickness Margin;
        public double Spacing = 6;
    }

    public sealed class VHStack : VNode
    {
        public readonly List<VNode> Children = new List<VNode>();
        public Thickness Margin;
        public double Spacing = 6;
        public HorizontalAlignment HAlign = HorizontalAlignment.Stretch;
        public VerticalAlignment VAlign = VerticalAlignment.Center;
    }

    public sealed class VHeader : VNode
    {
        public string Text;
        public double Size = 16;
        public bool Bold = true;
        public Thickness Margin;
    }

    public sealed class VText : VNode
    {
        public string Text;
        public Thickness Margin;
    }

    public sealed class VButton : VNode
    {
        public string Text;
        public Action OnClick;
        public Thickness Margin;
        public bool Primary;
    }

    public sealed class VSeparator : VNode
    {
        public Thickness Margin;
    }

    public sealed class VSpacer : VNode { public double Height; }

    public sealed class VCard : VNode
    {
        public VNode Child;
        public Thickness Margin;
        public double Corner = 8;
    }
    public sealed class VCheckbox : VNode
    {
        public string Label;
        public bool Value;
        public Action<bool> OnChanged;
        public Thickness Margin;
    }

    public sealed class VTextbox : VNode
    {
        public string Text;
        public string Placeholder;
        public bool Multiline;          // if true → AcceptsReturn
        public int MinLines = 1;       // only used when Multiline
        public Action<string> OnChanged;
        public Thickness Margin;
    }

    public sealed class VSlider : VNode
    {
        public double Min = 0;
        public double Max = 1;
        public double Value = 0.5;
        public double? Tick;            // null = no ticks
        public bool SnapToTick = false;
        public Action<double> OnChanged;
        public Thickness Margin;
    }

    public sealed class VListBox : VNode
    {
        public IList<string> Items = new List<string>();
        public int SelectedIndex = -1;
        public Action<int, string> OnSelectionChanged; // (index, item)
        public Thickness Margin;
        public double MinHeight = 120;
    }

    public sealed class VMount : VNode
    {
        // Renderer will create a StackPanel and pass it to you
        public Action<Avalonia.Controls.Panel> OnReady;
        public Thickness Margin;
    }

    public sealed class VComboBox : VNode
    {
        public IList<string> Items = new List<string>();
        public int SelectedIndex = 0;
        public Action<int, string> OnSelectionChanged;   // (index, item)
        public Thickness Margin;
    }

    public sealed class VNumericField : VNode
    {
        public double Value = 0;
        public double Min = double.MinValue;
        public double Max = double.MaxValue;
        public double Step = 1;
        public int Decimals = 0;            // decimal places shown
        public string Label;                // optional label prefix
        public Action<double> OnChanged;
        public Thickness Margin;
    }

    public sealed class VRadioGroup : VNode
    {
        public IList<string> Options = new List<string>();
        public int SelectedIndex = 0;
        public Action<int, string> OnChanged;   // (index, label)
        public Thickness Margin;
        public bool Horizontal = false;
    }

    public sealed class VProgressBar : VNode
    {
        public double Value = 0;            // 0-100
        public bool IsIndeterminate = false;
        public bool ShowText = true;        // overlay "42 %" label
        public Thickness Margin;
    }

    public sealed class VScrollViewer : VNode
    {
        public VNode Child;
        public double MaxHeight = double.NaN;
        public Thickness Margin;
    }

    public sealed class VGrid : VNode
    {
        /// <summary>List of (Label, Widget) rows for a two-column form layout.</summary>
        public List<(string Label, VNode Editor)> Rows = new();
        public double LabelWidth = 140;
        public Thickness Margin;
    }

    public sealed class VExpander : VNode
    {
        public string Header;
        public VNode Child;
        public bool IsExpanded = true;
        public Thickness Margin;
    }

    public sealed class VImage : VNode
    {
        public string Source;               // resource path or file path
        public double Width = double.NaN;
        public double Height = double.NaN;
        public Thickness Margin;
    }


    // ---------- Tiny builder helpers (fluent-ish, but simple) ----------

    public static class UIX
    {
        public static VStack Stack(params VNode[] kids)
        {
            var v = new VStack();
            if (kids != null) v.Children.AddRange(kids);
            return v;
        }

        public static VHStack Row(params VNode[] kids)
        {
            var v = new VHStack();
            if (kids != null) v.Children.AddRange(kids);
            return v;
        }

        public static VHeader Header(string text, double size = 16, bool bold = true)
            => new VHeader { Text = text, Size = size, Bold = bold, Margin = new Thickness(12, 12, 12, 6) };

        public static VText Text(string t) => new VText { Text = t, Margin = new Thickness(12, 6, 12, 6) };

        public static VButton Button(string text, Action onClick = null, bool primary = false)
            => new VButton { Text = text, OnClick = onClick, Primary = primary, Margin = new Thickness(12, 6, 12, 6) };

        public static VSeparator Sep() => new VSeparator { Margin = new Thickness(12, 6, 12, 6) };

        public static VSpacer Space(double h) => new VSpacer { Height = h };

        public static VCard Card(VNode child) => new VCard { Child = child, Margin = new Thickness(10) };

        public static VCheckbox Checkbox(string label, bool value = false, Action<bool> onChanged = null)
        => new VCheckbox { Label = label, Value = value, OnChanged = onChanged, Margin = new Thickness(12, 6, 12, 6) };

        public static VTextbox Textbox(string text = "", string placeholder = null, bool multiline = false, Action<string> onChanged = null)
            => new VTextbox { Text = text, Placeholder = placeholder, Multiline = multiline, OnChanged = onChanged, Margin = new Thickness(12, 6, 12, 6) };

        public static VSlider Slider(double min, double max, double value, Action<double> onChanged = null, double? tick = null, bool snap = false)
            => new VSlider { Min = min, Max = max, Value = value, OnChanged = onChanged, Tick = tick, SnapToTick = snap, Margin = new Thickness(12, 6, 12, 6) };

        public static VListBox ListBox(IEnumerable<string> items = null, int selected = -1,
                               Action<int, string> onSel = null)
        => new VListBox
        {
            Items = items != null ? new List<string>(items) : new List<string>(),
            SelectedIndex = selected,
            OnSelectionChanged = onSel,
            Margin = new Thickness(12, 6, 12, 6),
            MinHeight = 120
        };

        public static VMount Mount(Action<Avalonia.Controls.Panel> onReady)
            => new VMount { OnReady = onReady, Margin = new Thickness(12, 6, 12, 6) };

        // ---- new widget builders ----

        public static VComboBox ComboBox(IEnumerable<string> items, int selected = 0,
                                         Action<int, string> onSel = null)
            => new VComboBox
            {
                Items = items != null ? new List<string>(items) : new List<string>(),
                SelectedIndex = selected,
                OnSelectionChanged = onSel,
                Margin = new Thickness(12, 6, 12, 6)
            };

        public static VNumericField NumericField(double value = 0, double min = double.MinValue,
                                                  double max = double.MaxValue, double step = 1,
                                                  int decimals = 0, string label = null,
                                                  Action<double> onChanged = null)
            => new VNumericField
            {
                Value = value, Min = min, Max = max, Step = step,
                Decimals = decimals, Label = label, OnChanged = onChanged,
                Margin = new Thickness(12, 6, 12, 6)
            };

        public static VRadioGroup RadioGroup(IEnumerable<string> options, int selected = 0,
                                              Action<int, string> onChanged = null, bool horizontal = false)
            => new VRadioGroup
            {
                Options = options != null ? new List<string>(options) : new List<string>(),
                SelectedIndex = selected, OnChanged = onChanged, Horizontal = horizontal,
                Margin = new Thickness(12, 6, 12, 6)
            };

        public static VProgressBar ProgressBar(double value = 0, bool indeterminate = false, bool showText = true)
            => new VProgressBar
            {
                Value = value, IsIndeterminate = indeterminate, ShowText = showText,
                Margin = new Thickness(12, 6, 12, 6)
            };

        public static VScrollViewer Scroll(VNode child, double maxHeight = double.NaN)
            => new VScrollViewer { Child = child, MaxHeight = maxHeight, Margin = new Thickness(0) };

        public static VGrid Grid(List<(string Label, VNode Editor)> rows, double labelWidth = 140)
            => new VGrid { Rows = rows ?? new(), LabelWidth = labelWidth, Margin = new Thickness(12, 6, 12, 6) };

        public static VExpander Expander(string header, VNode child, bool expanded = true)
            => new VExpander { Header = header, Child = child, IsExpanded = expanded, Margin = new Thickness(12, 6, 12, 6) };

        public static VImage Image(string source, double width = double.NaN, double height = double.NaN)
            => new VImage { Source = source, Width = width, Height = height, Margin = new Thickness(12, 6, 12, 6) };

        // Convenience margins
        public static T WithMargin<T>(this T n, Thickness m) where T : VNode
        {
            var s = n as VStack; if (s != null) { s.Margin = m; return n; }
            var r = n as VHStack; if (r != null) { r.Margin = m; return n; }
            var h = n as VHeader; if (h != null) { h.Margin = m; return n; }
            var t = n as VText; if (t != null) { t.Margin = m; return n; }
            var b = n as VButton; if (b != null) { b.Margin = m; return n; }
            var sp = n as VSeparator; if (sp != null) { sp.Margin = m; return n; }
            var c = n as VCard; if (c != null) { c.Margin = m; return n; }
            var cb2 = n as VCheckbox; if (cb2 != null) { cb2.Margin = m; return n; }
            var tb = n as VTextbox; if (tb != null) { tb.Margin = m; return n; }
            var sl = n as VSlider; if (sl != null) { sl.Margin = m; return n; }
            var lb = n as VListBox; if (lb != null) { lb.Margin = m; return n; }
            var mt = n as VMount; if (mt != null) { mt.Margin = m; return n; }
            var combo = n as VComboBox; if (combo != null) { combo.Margin = m; return n; }
            var nf = n as VNumericField; if (nf != null) { nf.Margin = m; return n; }
            var rg = n as VRadioGroup; if (rg != null) { rg.Margin = m; return n; }
            var pb = n as VProgressBar; if (pb != null) { pb.Margin = m; return n; }
            var sv = n as VScrollViewer; if (sv != null) { sv.Margin = m; return n; }
            var g = n as VGrid; if (g != null) { g.Margin = m; return n; }
            var ex = n as VExpander; if (ex != null) { ex.Margin = m; return n; }
            var img = n as VImage; if (img != null) { img.Margin = m; return n; }
            return n;
        }
    }
}
