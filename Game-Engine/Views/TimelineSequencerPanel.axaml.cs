#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Game_Engine.Core;
using Game_Engine.Core.Timeline;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AvGrid = Avalonia.Controls.Grid;

namespace Game_Engine.Views;

public partial class TimelineSequencerPanel : UserControl
{
    internal TimelinePlayer? _player;
    private TimelineAsset? _timeline;
    internal int _selectedTrackIdx = -1;
    private readonly DispatcherTimer _refreshTimer;

    public TimelineSequencerPanel()
    {
        InitializeComponent();

        BtnPlay.Click += (_, _) => _player?.Play();
        BtnPause.Click += (_, _) => _player?.Pause();
        BtnStop.Click += (_, _) => { _player?.Stop(); SeqCanvas.Playhead = 0; SeqCanvas.InvalidateVisual(); };

        BtnAddPlayer.Click += OnAddPlayer;
        BtnNewTimeline.Click += OnNewTimeline;
        BtnAddTrack.Click += OnAddTrack;

        ChkLoop.IsCheckedChanged += (_, _) =>
        {
            if (_timeline != null) _timeline.Loop = ChkLoop.IsChecked == true;
        };
        TxtDuration.LostFocus += (_, _) =>
        {
            if (_timeline != null && float.TryParse(TxtDuration.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
            {
                _timeline.Duration = Math.Max(0.1f, d);
                SeqCanvas.InvalidateVisual();
            }
        };
        TxtSpeed.LostFocus += (_, _) =>
        {
            if (_player != null && float.TryParse(TxtSpeed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float s))
                _player.Speed = s;
        };
        TxtTimelineName.LostFocus += (_, _) =>
        {
            if (_timeline != null && !string.IsNullOrWhiteSpace(TxtTimelineName.Text))
                _timeline.Name = TxtTimelineName.Text;
        };

        CbGameObject.SelectionChanged += (_, _) =>
        {
            if (CbGameObject.SelectedItem is GOItem item) BindToGameObject(item.GO);
        };

        SeqCanvas.Panel = this;

        SelectionService.Changed += () => Dispatcher.UIThread.Post(RefreshGOList);
        SceneService.Changed += () => Dispatcher.UIThread.Post(RefreshGOList);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _refreshTimer.Tick += (_, _) => TickUI();
        _refreshTimer.Start();

        RefreshGOList();
    }

    private void TickUI()
    {
        if (_player == null || _timeline == null) return;
        if (_player.IsPlaying)
        {
            SeqCanvas.Playhead = _player.CurrentTime;
            SeqCanvas.InvalidateVisual();
        }
        int min = (int)(_player.CurrentTime / 60f);
        float sec = _player.CurrentTime - min * 60;
        TxtTime.Text = $"{min}:{sec:00.000}";
    }

    private void RefreshGOList()
    {
        var prev = CbGameObject.SelectedItem as GOItem;
        var items = new List<GOItem>();
        foreach (var root in SceneService.Root)
            CollectGOs(root, items);
        CbGameObject.ItemsSource = items;
        CbGameObject.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(GOItem.Name));

        if (prev != null)
        {
            var match = items.FirstOrDefault(i => i.GO == prev.GO);
            if (match != null) { CbGameObject.SelectedItem = match; return; }
        }

        var sel = SelectionService.Selected;
        if (sel != null)
        {
            var match = items.FirstOrDefault(i => i.GO == sel);
            if (match != null) CbGameObject.SelectedItem = match;
        }
    }

    private void BindToGameObject(GameObject? go)
    {
        _player = null;
        _timeline = null;
        _selectedTrackIdx = -1;

        if (go != null)
        {
            foreach (var b in go.Behaviors)
                if (b is TimelinePlayer tp) { _player = tp; break; }
        }

        BtnAddPlayer.IsVisible = go != null && _player == null;
        BtnNewTimeline.IsVisible = _player != null && _player.Timeline == null;

        if (_player != null)
        {
            _timeline = _player.Timeline;
            if (_timeline == null)
            {
                _timeline = new TimelineAsset();
                _player.Timeline = _timeline;
            }
        }

        SyncUIFromTimeline();
    }

    private void SyncUIFromTimeline()
    {
        if (_timeline != null)
        {
            TxtTimelineName.Text = _timeline.Name;
            TxtDuration.Text = _timeline.Duration.ToString("F1", CultureInfo.InvariantCulture);
            ChkLoop.IsChecked = _timeline.Loop;
            TxtSpeed.Text = (_player?.Speed ?? 1f).ToString("F1", CultureInfo.InvariantCulture);
        }
        else
        {
            TxtTimelineName.Text = "";
            TxtDuration.Text = "10.0";
            ChkLoop.IsChecked = false;
            TxtSpeed.Text = "1.0";
        }

        RebuildTrackList();
        SeqCanvas.Timeline = _timeline;
        SeqCanvas.SelectedTrackIdx = _selectedTrackIdx;
        SeqCanvas.InvalidateVisual();
    }

    private void OnAddPlayer(object? sender, RoutedEventArgs e)
    {
        if (CbGameObject.SelectedItem is not GOItem item) return;
        var tp = new TimelinePlayer();
        item.GO.AddBehavior(tp);
        _player = tp;
        _timeline = new TimelineAsset();
        _player.Timeline = _timeline;
        BtnAddPlayer.IsVisible = false;
        BtnNewTimeline.IsVisible = false;
        SyncUIFromTimeline();
        SceneService.NotifyChanged();
    }

    private void OnNewTimeline(object? sender, RoutedEventArgs e)
    {
        if (_player == null) return;
        _timeline = new TimelineAsset();
        _player.Timeline = _timeline;
        BtnNewTimeline.IsVisible = false;
        SyncUIFromTimeline();
        SceneService.NotifyChanged();
    }

    private void OnAddTrack(object? sender, RoutedEventArgs e)
    {
        if (_timeline == null) return;

        var menu = new ContextMenu();
        foreach (TrackType tt in Enum.GetValues<TrackType>())
        {
            var captured = tt;
            var mi = new MenuItem { Header = tt.ToString() };
            mi.Click += (_, _) =>
            {
                _timeline.AddTrack($"{captured} Track", captured);
                RebuildTrackList();
                SeqCanvas.InvalidateVisual();
                SceneService.NotifyChanged();
            };
            menu.Items.Add(mi);
        }
        menu.Open(BtnAddTrack);
    }

    internal void RebuildTrackList()
    {
        TrackListPanel.Children.Clear();
        if (_timeline == null) return;

        for (int i = 0; i < _timeline.Tracks.Count; i++)
        {
            var track = _timeline.Tracks[i];
            int idx = i;

            var row = new AvGrid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
                Height = 28,
                Margin = new Thickness(0, 0, 0, 1)
            };

            var badge = new Border
            {
                Width = 6, CornerRadius = new CornerRadius(3),
                Background = TrackTypeBrush(track.Type),
                Margin = new Thickness(2, 4, 4, 4),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            AvGrid.SetColumn(badge, 0);
            row.Children.Add(badge);

            var nameBtn = new Button
            {
                Content = $"{track.Name}",
                Classes = { idx == _selectedTrackIdx ? "trackBtnSelected" : "trackBtn" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                FontSize = 11, Height = 26, Padding = new Thickness(4, 0)
            };
            nameBtn.Click += (_, _) =>
            {
                _selectedTrackIdx = idx;
                SeqCanvas.SelectedTrackIdx = idx;
                RebuildTrackList();
                SeqCanvas.InvalidateVisual();
            };
            AvGrid.SetColumn(nameBtn, 1);
            row.Children.Add(nameBtn);

            var muteBtn = new Button
            {
                Content = track.Muted ? "M" : "S",
                Classes = { "mute" }, Width = 22, Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            muteBtn.Click += (_, _) =>
            {
                track.Muted = !track.Muted;
                RebuildTrackList();
                SceneService.NotifyChanged();
            };
            AvGrid.SetColumn(muteBtn, 2);
            row.Children.Add(muteBtn);

            var delBtn = new Button
            {
                Content = "x", Classes = { "remove" },
                Width = 22, Height = 22, Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            delBtn.Click += (_, _) =>
            {
                _timeline.RemoveTrack(track);
                if (_selectedTrackIdx >= _timeline.Tracks.Count)
                    _selectedTrackIdx = _timeline.Tracks.Count - 1;
                SeqCanvas.SelectedTrackIdx = _selectedTrackIdx;
                RebuildTrackList();
                SeqCanvas.InvalidateVisual();
                SceneService.NotifyChanged();
            };
            AvGrid.SetColumn(delBtn, 3);
            row.Children.Add(delBtn);

            TrackListPanel.Children.Add(row);
        }
    }

    internal static IBrush TrackTypeBrush(TrackType t) => t switch
    {
        TrackType.Animation => new SolidColorBrush(Color.FromRgb(0x4A, 0x8C, 0xFF)),
        TrackType.Camera => new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x33)),
        TrackType.Audio => new SolidColorBrush(Color.FromRgb(0x66, 0xDD, 0x66)),
        TrackType.Activation => new SolidColorBrush(Color.FromRgb(0xDD, 0x66, 0xDD)),
        TrackType.Event => new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
        _ => Brushes.Gray
    };

    private static void CollectGOs(GameObject go, List<GOItem> items)
    {
        items.Add(new GOItem(go));
        foreach (var child in go.Children)
            CollectGOs(child, items);
    }

    internal sealed class GOItem
    {
        public GameObject GO { get; }
        public string Name => GO.Name;
        public GOItem(GameObject go) => GO = go;
    }
}

// ════════════════════════════════════════════════════════════════
// Timeline Sequencer Canvas — custom-drawn timeline with clips
// ════════════════════════════════════════════════════════════════

public sealed class TimelineSequencerCanvas : Control
{
    internal TimelineSequencerPanel? Panel { get; set; }
    internal TimelineAsset? Timeline { get; set; }
    internal int SelectedTrackIdx { get; set; } = -1;
    internal float Playhead { get; set; }

    private float _pixelsPerSecond = 80f;
    private float _scrollX;
    private const float RulerHeight = 24f;
    private const float TrackHeight = 28f;
    private const float TrackGap = 1f;

    private bool _dragging;
    private bool _resizingLeft;
    private bool _resizingRight;
    private float _dragStartX;
    private float _dragOrigStart;
    private float _dragOrigDuration;
    private int _dragTrackIdx = -1;
    private int _dragClipIdx = -1;
    private bool _scrubbing;

    public TimelineSequencerCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private float TimeToX(float t) => (t * _pixelsPerSecond) - _scrollX;
    private float XToTime(float x) => (x + _scrollX) / _pixelsPerSecond;

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        var bounds = Bounds;
        double w = bounds.Width, h = bounds.Height;

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1E)), null, new Rect(0, 0, w, h));

        DrawRuler(dc, w);
        DrawTracks(dc, w, h);
        DrawPlayhead(dc, h);
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x28)), null,
            new Rect(0, 0, w, RulerHeight));

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x55)), 1);
        var fgBrush = new SolidColorBrush(Color.FromRgb(0x99, 0xAA, 0xBB));

        float startTime = Math.Max(0, XToTime(0));
        float endTime = XToTime((float)w);
        float step = CalculateGridStep();

        float t = MathF.Floor(startTime / step) * step;
        while (t <= endTime)
        {
            double x = TimeToX(t);
            if (x >= 0 && x <= w)
            {
                dc.DrawLine(pen, new Point(x, RulerHeight - 8), new Point(x, RulerHeight));

                var text = new FormattedText(FormatTime(t), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, fgBrush);
                dc.DrawText(text, new Point(x + 2, 2));
            }
            t += step;
        }

        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48)), 1),
            new Point(0, RulerHeight), new Point(w, RulerHeight));
    }

    private void DrawTracks(DrawingContext dc, double w, double h)
    {
        if (Timeline == null) return;

        var bgAlt = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22));
        var bgSel = new SolidColorBrush(Color.FromRgb(0x24, 0x2D, 0x3E));
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x44)), 0.5);

        for (int i = 0; i < Timeline.Tracks.Count; i++)
        {
            var track = Timeline.Tracks[i];
            float y = RulerHeight + i * (TrackHeight + TrackGap);
            if (y > h) break;

            var bg = i == SelectedTrackIdx ? bgSel : (i % 2 == 0 ? null : bgAlt);
            if (bg != null)
                dc.DrawRectangle(bg, null, new Rect(0, y, w, TrackHeight));

            dc.DrawLine(borderPen, new Point(0, y + TrackHeight), new Point(w, y + TrackHeight));

            foreach (var clip in track.Clips)
                DrawClip(dc, clip, track.Type, y, track.Muted);
        }
    }

    private void DrawClip(DrawingContext dc, TimelineClip clip, TrackType type, float trackY, bool muted)
    {
        double x1 = TimeToX(clip.StartTime);
        double x2 = TimeToX(clip.EndTime);
        if (x2 < 0 || x1 > Bounds.Width) return;

        double cw = Math.Max(4, x2 - x1);
        var clipRect = new Rect(x1, trackY + 2, cw, TrackHeight - 4);

        var fillColor = type switch
        {
            TrackType.Animation => Color.FromArgb(muted ? (byte)0x55 : (byte)0xAA, 0x4A, 0x8C, 0xFF),
            TrackType.Camera => Color.FromArgb(muted ? (byte)0x55 : (byte)0xAA, 0xFF, 0xAA, 0x33),
            TrackType.Audio => Color.FromArgb(muted ? (byte)0x55 : (byte)0xAA, 0x66, 0xDD, 0x66),
            TrackType.Activation => Color.FromArgb(muted ? (byte)0x55 : (byte)0xAA, 0xDD, 0x66, 0xDD),
            TrackType.Event => Color.FromArgb(muted ? (byte)0x55 : (byte)0xAA, 0xFF, 0x66, 0x66),
            _ => Color.FromArgb(0x88, 0x88, 0x88, 0x88)
        };

        dc.DrawRectangle(new SolidColorBrush(fillColor), null,
            new Rect(clipRect.X, clipRect.Y, clipRect.Width, clipRect.Height), 3, 3);

        var borderColor = Color.FromArgb(0xCC, fillColor.R, fillColor.G, fillColor.B);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(borderColor), 1),
            new Rect(clipRect.X, clipRect.Y, clipRect.Width, clipRect.Height), 3, 3);

        if (clip.BlendIn > 0)
        {
            double bx = TimeToX(clip.StartTime + clip.BlendIn);
            var blendPen = new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), 1,
                new DashStyle(new[] { 2.0, 2.0 }, 0));
            dc.DrawLine(blendPen, new Point(bx, clipRect.Y), new Point(bx, clipRect.Bottom));
        }
        if (clip.BlendOut > 0)
        {
            double bx = TimeToX(clip.EndTime - clip.BlendOut);
            var blendPen = new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), 1,
                new DashStyle(new[] { 2.0, 2.0 }, 0));
            dc.DrawLine(blendPen, new Point(bx, clipRect.Y), new Point(bx, clipRect.Bottom));
        }

        string label = type switch
        {
            TrackType.Animation => string.IsNullOrEmpty(clip.AssetPath) ? "Clip" : clip.AssetPath,
            TrackType.Audio => string.IsNullOrEmpty(clip.AssetPath) ? "Audio" : System.IO.Path.GetFileName(clip.AssetPath),
            TrackType.Camera => string.IsNullOrEmpty(clip.TargetName) ? "Camera" : clip.TargetName,
            TrackType.Activation => string.IsNullOrEmpty(clip.TargetName) ? "Active" : clip.TargetName,
            TrackType.Event => string.IsNullOrEmpty(clip.EventName) ? "Event" : clip.EventName,
            _ => "Clip"
        };

        if (cw > 20)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, Brushes.White);
            double textX = Math.Max(clipRect.X + 3, 0);
            using (dc.PushClip(clipRect))
            {
                dc.DrawText(text, new Point(textX, clipRect.Y + (clipRect.Height - text.Height) / 2));
            }
        }
    }

    private void DrawPlayhead(DrawingContext dc, double h)
    {
        double x = TimeToX(Playhead);
        if (x < 0 || x > Bounds.Width) return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)), 2);
        dc.DrawLine(pen, new Point(x, 0), new Point(x, h));

        var headBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x - 5, 0), true);
            ctx.LineTo(new Point(x + 5, 0));
            ctx.LineTo(new Point(x, 8));
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(headBrush, null, geo);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos = e.GetPosition(this);
        float mx = (float)pos.X, my = (float)pos.Y;
        var props = e.GetCurrentPoint(this).Properties;

        if (my < RulerHeight)
        {
            _scrubbing = true;
            float t = Math.Max(0, XToTime(mx));
            Playhead = t;
            Panel?._player?.Seek(t);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (Timeline == null) return;

        int trackIdx = (int)((my - RulerHeight) / (TrackHeight + TrackGap));
        if (trackIdx < 0 || trackIdx >= Timeline.Tracks.Count) return;

        var track = Timeline.Tracks[trackIdx];

        if (props.IsRightButtonPressed)
        {
            ShowClipContextMenu(track, trackIdx, mx, my);
            e.Handled = true;
            return;
        }

        for (int ci = 0; ci < track.Clips.Count; ci++)
        {
            var clip = track.Clips[ci];
            double x1 = TimeToX(clip.StartTime);
            double x2 = TimeToX(clip.EndTime);

            if (mx >= x1 - 4 && mx <= x1 + 4)
            {
                _dragTrackIdx = trackIdx;
                _dragClipIdx = ci;
                _resizingLeft = true;
                _dragging = true;
                _dragStartX = mx;
                _dragOrigStart = clip.StartTime;
                _dragOrigDuration = clip.Duration;
                e.Handled = true;
                return;
            }
            if (mx >= x2 - 4 && mx <= x2 + 4)
            {
                _dragTrackIdx = trackIdx;
                _dragClipIdx = ci;
                _resizingRight = true;
                _dragging = true;
                _dragStartX = mx;
                _dragOrigStart = clip.StartTime;
                _dragOrigDuration = clip.Duration;
                e.Handled = true;
                return;
            }
            if (mx > x1 && mx < x2)
            {
                _dragTrackIdx = trackIdx;
                _dragClipIdx = ci;
                _dragging = true;
                _dragStartX = mx;
                _dragOrigStart = clip.StartTime;
                _dragOrigDuration = clip.Duration;
                SelectedTrackIdx = trackIdx;
                if (Panel != null) Panel._selectedTrackIdx = trackIdx;
                Panel?.RebuildTrackList();
                e.Handled = true;
                return;
            }
        }

        SelectedTrackIdx = trackIdx;
        if (Panel != null) Panel._selectedTrackIdx = trackIdx;
        Panel?.RebuildTrackList();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        float mx = (float)pos.X;

        if (_scrubbing)
        {
            float t = Math.Max(0, XToTime(mx));
            Playhead = t;
            Panel?._player?.Seek(t);
            InvalidateVisual();
            return;
        }

        if (!_dragging || Timeline == null) return;
        if (_dragTrackIdx < 0 || _dragTrackIdx >= Timeline.Tracks.Count) return;
        var track = Timeline.Tracks[_dragTrackIdx];
        if (_dragClipIdx < 0 || _dragClipIdx >= track.Clips.Count) return;
        var clip = track.Clips[_dragClipIdx];

        float dt = (mx - _dragStartX) / _pixelsPerSecond;

        if (_resizingLeft)
        {
            float newStart = Math.Max(0, _dragOrigStart + dt);
            float shrink = newStart - _dragOrigStart;
            float newDur = _dragOrigDuration - shrink;
            if (newDur >= 0.05f)
            {
                clip.StartTime = newStart;
                clip.Duration = newDur;
            }
        }
        else if (_resizingRight)
        {
            clip.Duration = Math.Max(0.05f, _dragOrigDuration + dt);
        }
        else
        {
            clip.StartTime = Math.Max(0, _dragOrigStart + dt);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging || _scrubbing)
        {
            _dragging = false;
            _resizingLeft = false;
            _resizingRight = false;
            _scrubbing = false;
            SceneService.NotifyChanged();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pos = e.GetPosition(this);
        float pivotTime = XToTime((float)pos.X);

        if (e.Delta.Y > 0)
            _pixelsPerSecond = Math.Min(500f, _pixelsPerSecond * 1.15f);
        else
            _pixelsPerSecond = Math.Max(10f, _pixelsPerSecond / 1.15f);

        _scrollX = pivotTime * _pixelsPerSecond - (float)pos.X;
        if (_scrollX < 0) _scrollX = 0;
        InvalidateVisual();
        e.Handled = true;
    }

    private void ShowClipContextMenu(TimelineTrack track, int trackIdx, float mx, float my)
    {
        var menu = new ContextMenu();

        int clipIdx = -1;
        for (int ci = 0; ci < track.Clips.Count; ci++)
        {
            double x1 = TimeToX(track.Clips[ci].StartTime);
            double x2 = TimeToX(track.Clips[ci].EndTime);
            if (mx >= x1 && mx <= x2) { clipIdx = ci; break; }
        }

        if (clipIdx >= 0)
        {
            var editMi = new MenuItem { Header = "Edit Clip..." };
            int capEdit = clipIdx;
            editMi.Click += (_, _) => ShowClipEditor(track, trackIdx, capEdit);
            menu.Items.Add(editMi);

            var dupMi = new MenuItem { Header = "Duplicate Clip" };
            int capDup = clipIdx;
            dupMi.Click += (_, _) =>
            {
                var src = track.Clips[capDup];
                var dup = new TimelineClip
                {
                    StartTime = src.EndTime + 0.1f,
                    Duration = src.Duration,
                    BlendIn = src.BlendIn,
                    BlendOut = src.BlendOut,
                    Speed = src.Speed,
                    AssetPath = src.AssetPath,
                    TargetName = src.TargetName,
                    EventName = src.EventName,
                    EventData = src.EventData
                };
                track.Clips.Add(dup);
                InvalidateVisual();
                SceneService.NotifyChanged();
            };
            menu.Items.Add(dupMi);

            var delMi = new MenuItem { Header = "Delete Clip" };
            int capDel = clipIdx;
            delMi.Click += (_, _) =>
            {
                track.Clips.RemoveAt(capDel);
                InvalidateVisual();
                SceneService.NotifyChanged();
            };
            menu.Items.Add(delMi);

            menu.Items.Add(new Separator());
        }

        var addMi = new MenuItem { Header = "Add Clip Here" };
        float clickTime = XToTime(mx);
        addMi.Click += (_, _) =>
        {
            var clip = new TimelineClip
            {
                StartTime = Math.Max(0, clickTime),
                Duration = 1f
            };
            track.Clips.Add(clip);
            InvalidateVisual();
            SceneService.NotifyChanged();
        };
        menu.Items.Add(addMi);

        menu.Open(this);
    }

    private async void ShowClipEditor(TimelineTrack track, int trackIdx, int clipIdx)
    {
        if (clipIdx < 0 || clipIdx >= track.Clips.Count) return;
        var clip = track.Clips[clipIdx];

        var win = new Window
        {
            Title = "Edit Clip",
            Width = 340, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x28)),
            CanResize = false
        };

        var sp = new StackPanel { Margin = new Thickness(12), Spacing = 6 };

        TextBox MakeRow(string label, string val)
        {
            sp.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0xAA, 0xBB)),
                FontSize = 11
            });
            var tb = new TextBox
            {
                Text = val,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xEA)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48)),
                FontSize = 11, Height = 24
            };
            sp.Children.Add(tb);
            return tb;
        }

        var tbStart = MakeRow("Start Time", clip.StartTime.ToString("F3", CultureInfo.InvariantCulture));
        var tbDur = MakeRow("Duration", clip.Duration.ToString("F3", CultureInfo.InvariantCulture));
        var tbBlendIn = MakeRow("Blend In", clip.BlendIn.ToString("F3", CultureInfo.InvariantCulture));
        var tbBlendOut = MakeRow("Blend Out", clip.BlendOut.ToString("F3", CultureInfo.InvariantCulture));
        var tbSpeed = MakeRow("Speed", clip.Speed.ToString("F2", CultureInfo.InvariantCulture));

        TextBox? tbAsset = null, tbTarget = null, tbEvtName = null, tbEvtData = null;
        if (track.Type == TrackType.Animation || track.Type == TrackType.Audio)
            tbAsset = MakeRow("Asset Path / State", clip.AssetPath);
        if (track.Type == TrackType.Animation || track.Type == TrackType.Camera || track.Type == TrackType.Activation)
            tbTarget = MakeRow("Target Name", clip.TargetName);
        if (track.Type == TrackType.Event)
        {
            tbEvtName = MakeRow("Event Name", clip.EventName);
            tbEvtData = MakeRow("Event Data", clip.EventData);
        }

        var okBtn = new Button
        {
            Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Height = 30, Margin = new Thickness(0, 10, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x6A, 0xBF)),
            Foreground = Brushes.White, FontWeight = FontWeight.SemiBold
        };
        sp.Children.Add(okBtn);

        win.Content = new ScrollViewer { Content = sp };

        okBtn.Click += (_, _) =>
        {
            if (float.TryParse(tbStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) clip.StartTime = Math.Max(0, v);
            if (float.TryParse(tbDur.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) clip.Duration = Math.Max(0.01f, v);
            if (float.TryParse(tbBlendIn.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) clip.BlendIn = Math.Max(0, v);
            if (float.TryParse(tbBlendOut.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) clip.BlendOut = Math.Max(0, v);
            if (float.TryParse(tbSpeed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) clip.Speed = v;
            if (tbAsset != null) clip.AssetPath = tbAsset.Text ?? "";
            if (tbTarget != null) clip.TargetName = tbTarget.Text ?? "";
            if (tbEvtName != null) clip.EventName = tbEvtName.Text ?? "";
            if (tbEvtData != null) clip.EventData = tbEvtData.Text ?? "";

            InvalidateVisual();
            SceneService.NotifyChanged();
            win.Close();
        };

        Window? owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    private float CalculateGridStep()
    {
        float[] steps = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 30f, 60f };
        float minPixelStep = 60f;
        foreach (float s in steps)
            if (s * _pixelsPerSecond >= minPixelStep) return s;
        return 60f;
    }

    private static string FormatTime(float t)
    {
        if (t < 60) return t.ToString("F2", CultureInfo.InvariantCulture) + "s";
        int m = (int)(t / 60f);
        float s = t - m * 60;
        return $"{m}:{s:00.0}";
    }
}
