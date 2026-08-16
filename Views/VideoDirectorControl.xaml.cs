using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoDirector.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using VideoDirector.Models;
using Microsoft.UI.Xaml.Input;

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        public DirectorViewModel ViewModel { get; } = new DirectorViewModel();
        private VideoPlaybackEngine _playbackEngine;
        private DispatcherTimer _inactivityTimer;

        // Proportional timeline bar (§7E/F): px-per-second scale + the playhead line & handle.
        private double _timelinePxPerSec;
        private double _timelineZoomFactor = 1.0;
        private Microsoft.UI.Xaml.Shapes.Rectangle _playhead;
        private Microsoft.UI.Xaml.Shapes.Polygon _playheadKnob;
        private TextBlock _playheadTime;
        // Pointer state: ruler = scrub; clip row tap = select; clip row drag = move/reorder.
        private Windows.Foundation.Point _timelinePressPoint;
        private bool _timelinePressed;
        private bool _timelineScrubbing;
        private bool _timelineMovingClip;
        private CinematicOperation _dragClip;
        private int _dragTrackIndex = -1;         // track the drag started on
        private int _dragTargetTrackIndex = -1;   // track it would land on (preview only)
        private double _dragTargetStartSec = double.NaN;  // NaN = position comes from clip order
        private double _dragGrabOffsetSec;
        private double _dragCursorX;      // live cursor x, for the spine ghost
        private int _dragInsertIndex;     // where the ghost would drop
        private Windows.Foundation.Point _lastHoverPoint;  // for the context menu's target
        private int _lastActiveSignature = -1;             // playback spotlight refresh guard
        private DispatcherTimer _pulseTimer;
        private double _pulsePhase = 0;
        private readonly Dictionary<CinematicOperation, List<UIElement>> _clipBlockElements = new();

        public VideoDirectorControl()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;

            _inactivityTimer = new DispatcherTimer();
            _inactivityTimer.Interval = TimeSpan.FromSeconds(5);
            _inactivityTimer.Tick += InactivityTimer_Tick;

            _pulseTimer = new DispatcherTimer();
            _pulseTimer.Interval = TimeSpan.FromMilliseconds(50);
            _pulseTimer.Tick += PulseTimer_Tick;
            
            this.PointerMoved += VideoDirectorControl_PointerMoved;

            // Wire up the engine once the control loads
            this.Loaded += VideoDirectorControl_Loaded;
        }

        private void VideoDirectorControl_PointerMoved(object? sender, PointerRoutedEventArgs e)
        {
            _inactivityTimer.Stop();
            ViewModel.IsControlsVisible = true;
            _inactivityTimer.Start();
        }

        private bool _isPointerOverPill = false;
        private void FloatingPill_PointerEntered(object? sender, PointerRoutedEventArgs e) => _isPointerOverPill = true;
        private void FloatingPill_PointerExited(object? sender, PointerRoutedEventArgs e) => _isPointerOverPill = false;

        private void InactivityTimer_Tick(object? sender, object e)
        {
            _inactivityTimer.Stop();
            if (!_isPointerOverPill)
            {
                ViewModel.IsControlsVisible = false;
            }
        }

        private void VideoDirectorControl_Loaded(object? sender, RoutedEventArgs e)
        {
            _playbackEngine = new VideoPlaybackEngine(PlayerControl, ViewModel);
            PlayerControl.ViewportTransformChanged += PlayerControl_ViewportTransformChanged;
            PlayerControl.SizeChanged += PlayerControl_SizeChanged;
            PlayerControl.EditRequested += PlayerControl_EditRequested;
            PlayerControl.ExitEditRequested += (s, ev) => ExitEditMode();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.EditTargetChanged += ViewModel_EditTargetChanged;

            ViewModel.TimelineNodes.CollectionChanged += (s, ev) => BuildTimelineBar();
            HookTrackClips();
            BuildTimelineBar();
            UpdateZoomReadout();
        }

        // Each track owns its own clip collection, so the timeline watches them all. The track
        // instances are created once and live for the lifetime of the view model -- loading a
        // project refills their clip lists in place -- so hooking once here is enough.
        private void HookTrackClips()
        {
            foreach (var track in ViewModel.Tracks)
                track.Clips.CollectionChanged += (s, ev) => { BuildTimelineBar(); _playbackEngine?.RefreshComposite(); };
        }


        // The track that owns a clip, on any track.
        private TimelineTrack TrackOf(CinematicOperation clip) => ViewModel.TrackOf(clip);

        // Whether the clip currently being dragged sits on a gapless track. A gapless drag is a
        // REORDER (ghost preview, order committed on release); a free drag is a move in time.
        private bool DragIsGapless =>
            _dragTrackIndex >= 0 && _dragTrackIndex < ViewModel.Tracks.Count
            && ViewModel.Tracks[_dragTrackIndex].IsGapless;

        private void TimelineBar_SizeChanged(object? sender, SizeChangedEventArgs e) => BuildTimelineBar();

        // ---- Timeline row geometry (§7E) ---------------------------------------------------
        // A scrub ruler on top, then one lane per track, all on one shared px=seconds scale.
        // Scrub on the ruler; drag clips in their lanes.
        //
        // The maths lives in TimelineGeometry (pure, WinUI-free, unit-tested) — these are just
        // the bindings of it to this control's track count. It used to be inlined at seven call
        // sites, each free to drift from the others.
        private const double RulerH = TimelineGeometry.RulerH;
        private const double RowTop = TimelineGeometry.RowTop;
        private const double BlockH = TimelineGeometry.BlockH;
        private const double RowPitch = TimelineGeometry.RowPitch;

        private int TrackCount => ViewModel.Tracks.Count;

        private double RowYForTrack(int trackIndex) => TimelineGeometry.RowYForTrack(trackIndex, TrackCount);
        private static bool IsRulerY(double y) => TimelineGeometry.IsRulerY(y);
        private int TrackIndexAtY(double y) => TimelineGeometry.TrackAtY(y, TrackCount);
        private double TimelineBarHeight => TimelineGeometry.BarHeight(TrackCount);

        private void BuildTimelineBar()
        {
            if (TimelineBar == null) return;
            TimelineBar.Children.Clear();
            _clipBlockElements.Clear();
            _playhead = null; _playheadKnob = null;

            TimelineBar.Height = TimelineBarHeight;   // grows with the upper-track count
            double viewportW = (TimelineBar.Parent as FrameworkElement)?.ActualWidth ?? TimelineBar.ActualWidth;
            if (viewportW <= 0) viewportW = TimelineBar.ActualWidth;
            double w = viewportW * _timelineZoomFactor;
            if (w > 0) TimelineBar.Width = w;
            double h = TimelineBarHeight;

            // The drawn span is the content plus runway, never the content exactly — otherwise
            // there is nowhere to drag a clip TO in order to extend the project, and an empty
            // project draws nothing at all (no ruler, no lanes, just four floating labels).
            double total = TimelineGeometry.ExtentSeconds(ViewModel.ContentEnd.TotalSeconds);

            BuildTrackLabels(); // Build track headers regardless of whether the timeline is empty

            if (w <= 0) { _timelinePxPerSec = 0; return; }
            _timelinePxPerSec = w / total;

            // Per-lane bands: a faint tint of the track's own colour distinguishes the lanes by
            // colour rather than by height (space is at a premium), and ties each lane to its
            // identity colour. Drawn first so blocks/gridlines paint over them.
            for (int ti = 0; ti < TrackCount; ti++)
                DrawRowBand(RowYForTrack(ti), w, TrackPalette.For(ti));

            // Faint ruler strip marks the scrub zone.
            var ruler = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = w, Height = RulerH, IsHitTestVisible = false,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, 0x88, 0x88, 0x88))
            };
            Canvas.SetLeft(ruler, 0); Canvas.SetTop(ruler, 0);
            TimelineBar.Children.Add(ruler);

            // Time scale: labelled ticks in the ruler + faint full-height gridlines behind the blocks.
            var gridBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x16, 0x88, 0x88, 0x88));
            var tickBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x77, 0x88, 0x88, 0x88));
            var labelBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            double step = NiceTimeStep(total, w);
            for (double t = 0; t <= total + 0.001 && step > 0; t += step)
            {
                double gx = t * _timelinePxPerSec;
                var grid = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = h - RulerH, IsHitTestVisible = false, Fill = gridBrush };
                Canvas.SetLeft(grid, gx); Canvas.SetTop(grid, RulerH);
                TimelineBar.Children.Add(grid);

                var tick = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = 4, IsHitTestVisible = false, Fill = tickBrush };
                Canvas.SetLeft(tick, gx); Canvas.SetTop(tick, RulerH - 4);
                TimelineBar.Children.Add(tick);

                if (gx < w - 26)
                {
                    var tl = new TextBlock { Text = FormatTimeShort(t), FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsHitTestVisible = false, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray) };
                    Canvas.SetLeft(tl, gx + 2); Canvas.SetTop(tl, 1);
                    TimelineBar.Children.Add(tl);
                }
            }

            // Every track drawn by ONE loop (§7B). There used to be a bespoke branch for track 0
            // and a separate loop for the rest, which is why transitions could only ever be drawn
            // on track 0 — they are a property of a clip, not of a privileged track.
            var transColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B);
            var ghostColor = Microsoft.UI.ColorHelper.FromArgb(0xCC, 0x93, 0xC5, 0xFD);

            // A drag in progress is drawn as a PREVIEW: the clip is omitted from wherever it
            // currently lives, the destination lane opens a gap (if gapless) or leaves its
            // resolved slot free, and the clip itself is drawn as a ghost. Nothing here is
            // committed to the model — see CommitDrag.
            bool dragging = _timelineMovingClip && _dragClip != null && _dragTargetTrackIndex >= 0;

            for (int ti = 0; ti < TrackCount; ti++)
            {
                var track = ViewModel.Tracks[ti];
                double rowY = RowYForTrack(ti);
                var color = TrackPalette.For(ti);
                bool isTarget = dragging && ti == _dragTargetTrackIndex;
                bool gaplessTarget = isTarget && track.IsGapless;

                double gapWidth = gaplessTarget ? _dragClip.OpDuration.TotalSeconds * _timelinePxPerSec : 0;
                double flowX = 0;
                int drawn = 0;

                foreach (var clip in track.Clips)
                {
                    if (dragging && ReferenceEquals(clip, _dragClip)) continue;   // it is the ghost

                    double cw = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                    double tw = clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
                    double x;

                    if (gaplessTarget)
                    {
                        if (drawn == _dragInsertIndex) flowX += gapWidth;   // open the drop gap
                        x = flowX;
                        flowX += cw + tw;
                        drawn++;
                    }
                    else
                    {
                        x = clip.StartTimeSeconds * _timelinePxPerSec;
                    }

                    AddTimelineBlock(x, rowY, cw, BlockH, color, clip);
                    if (tw > 0.5) AddTimelineBlock(x + cw, rowY, tw, BlockH, transColor);
                }
            }

            if (dragging)
            {
                AddTimelineBlock(GhostX(), RowYForTrack(_dragTargetTrackIndex),
                    _dragClip.OpDuration.TotalSeconds * _timelinePxPerSec, BlockH,
                    ghostColor, _dragClip);
            }

            // Playhead: a bright red line the full height with a downward triangle handle in the ruler.
            var red = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44));
            var shadowStroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x80, 0x00, 0x00, 0x00));
            _playhead = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 3, Height = h, IsHitTestVisible = false, Fill = red, Stroke = shadowStroke, StrokeThickness = 1 };
            TimelineBar.Children.Add(_playhead);
            _playheadKnob = new Microsoft.UI.Xaml.Shapes.Polygon { IsHitTestVisible = false, Fill = red, Stroke = shadowStroke, StrokeThickness = 1, StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round };
            _playheadKnob.Points.Add(new Windows.Foundation.Point(0, 0));
            _playheadKnob.Points.Add(new Windows.Foundation.Point(11, 0));
            _playheadKnob.Points.Add(new Windows.Foundation.Point(5.5, 9));
            TimelineBar.Children.Add(_playheadKnob);

            // A small time readout that rides the playhead.
            _playheadTime = new TextBlock
            {
                FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsHitTestVisible = false,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44))
            };
            TimelineBar.Children.Add(_playheadTime);

            UpdatePlayhead();
        }

        // Faint band across a lane in the track's own colour — lane separation without extra height.
        private void DrawRowBand(double rowY, double w, Windows.UI.Color color)
        {
            var band = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = w, Height = RowPitch, IsHitTestVisible = false,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(TrackPalette.At(color, 0x1E))
            };
            Canvas.SetLeft(band, 0); Canvas.SetTop(band, rowY - 1);
            TimelineBar.Children.Add(band);
        }

        // A "nice" tick interval (seconds) aiming for ~80px between ticks.
        private double NiceTimeStep(double totalSeconds, double w)
        {
            if (_timelinePxPerSec <= 0) return 0;
            double rough = 80.0 / _timelinePxPerSec;   // seconds per ~80px
            double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            foreach (var s in steps) if (s >= rough) return s;
            return steps[steps.Length - 1];
        }

        private static string FormatTimeShort(double seconds)
        {
            int m = (int)(seconds / 60);
            int s = (int)System.Math.Round(seconds - m * 60);
            if (s == 60) { m++; s = 0; }
            return m > 0 ? $"{m}:{s:00}" : $"{s}s";
        }

        // "Track 1".."Track 4" in the left gutter, vertically aligned to each lane. Drawn in track
        // order but positioned by RowYForTrack, so they land in display order (highest track at
        // the top) without the loop needing to know that.
        private void BuildTrackLabels()
        {
            if (TimelineLabels == null) return;
            TimelineLabels.Children.Clear();
            TimelineLabels.Height = TimelineBarHeight;

            for (int ti = 0; ti < TrackCount; ti++)
                AddTrackHeader(RowYForTrack(ti), TrackPalette.For(ti), ti);
        }

        // Each track label is a button: click it to load a video into that track via a file picker
        // (trackIndex -1 = spine/Track 1, 0..2 = overlay tracks). Drag & drop still works too.
        // A track header: identity chip + name, the four state toggles, and an overflow menu.
        // This replaces a single 18px button whose only action was opening a file picker, which
        // meant a track had no way to be silenced, hidden, protected, or switched between
        // sequence and free placement.
        private void AddTrackHeader(double y, Windows.UI.Color color, int trackIndex)
        {
            var track = ViewModel.Tracks[trackIndex];
            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Identity chip — the same colour as this track's clip blocks and its PiP frame.
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 4, Height = BlockH - 8, RadiusX = 2, RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center, Fill = brush,
                Margin = new Thickness(0, 0, 4, 0)
            });

            var name = new TextBlock
            {
                Text = track.Name,
                FontSize = 10,
                Width = 46,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(name, track.Name + " — double-click a clip to edit it");
            row.Children.Add(name);

            row.Children.Add(TrackToggle("", "", track.IsMuted,
                "Mute this track", v => { track.IsMuted = v; _playbackEngine?.RefreshComposite(); }));
            row.Children.Add(TrackToggle("", "", track.IsHidden,
                "Hide this track from the composite", v => { track.IsHidden = v; _playbackEngine?.RefreshComposite(); }));
            row.Children.Add(TrackToggle("", "", track.IsLocked,
                "Lock this track against edits", v => track.IsLocked = v));
            row.Children.Add(TrackToggle("", "", track.IsGapless,
                "Sequence: clips butt up end-to-end with no gaps", v =>
                {
                    track.IsGapless = v;
                    ViewModel.RecordIfChanged();
                    BuildTimelineBar();
                    _playbackEngine?.RefreshComposite();
                }));

            row.Children.Add(TrackOverflow(trackIndex, track));

            Canvas.SetLeft(row, 4);
            Canvas.SetTop(row, y);
            TimelineLabels.Children.Add(row);
        }

        // A compact state toggle. `onGlyph` shows when the flag is set.
        private static Microsoft.UI.Xaml.Controls.Primitives.ToggleButton TrackToggle(
            string offGlyph, string onGlyph, bool isOn, string tooltip, Action<bool> apply)
        {
            var icon = new FontIcon { Glyph = isOn ? onGlyph : offGlyph, FontSize = 11 };
            var btn = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = icon,
                IsChecked = isOn,
                Width = 22, Height = 22, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            ToolTipService.SetToolTip(btn, tooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, tooltip);
            btn.Click += (s, e) =>
            {
                bool on = btn.IsChecked ?? false;
                icon.Glyph = on ? onGlyph : offGlyph;
                apply(on);
            };
            return btn;
        }

        // Everything a track needs but not often enough to spend a button on.
        private Button TrackOverflow(int trackIndex, TimelineTrack track)
        {
            var menu = new MenuFlyout();

            var load = new MenuFlyoutItem { Text = "Add clips to this track…" };
            load.Click += (s, e) => LoadIntoTrack(trackIndex);
            menu.Items.Add(load);

            menu.Items.Add(new MenuFlyoutSeparator());

            // Track-level placement defaults. DefaultCenterX/Y already existed with no UI at all,
            // and no default size to go with them.
            var fullFrame = new MenuFlyoutItem { Text = "New clips: full frame" };
            fullFrame.Click += (s, e) => { track.DefaultCenterX = 0.5; track.DefaultCenterY = 0.5; ViewModel.RecordIfChanged(); };
            menu.Items.Add(fullFrame);

            var corner = new MenuFlyoutItem { Text = "New clips: corner PiP" };
            corner.Click += (s, e) => { track.DefaultCenterX = 0.72; track.DefaultCenterY = 0.72; ViewModel.RecordIfChanged(); };
            menu.Items.Add(corner);

            menu.Items.Add(new MenuFlyoutSeparator());

            var clear = new MenuFlyoutItem { Text = "Clear this track" };
            clear.Click += (s, e) =>
            {
                if (track.IsLocked) return;
                track.Clips.Clear();
                track.Normalize();
                if (ViewModel.TrackIndexOf(ViewModel.SelectedClip) < 0) ViewModel.SelectedClip = null;
                ViewModel.RecordIfChanged();
                BuildTimelineBar();
                _playbackEngine?.RefreshComposite();
            };
            menu.Items.Add(clear);

            var btn = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 11 },
                Width = 22, Height = 22, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Flyout = menu
            };
            ToolTipService.SetToolTip(btn, "More actions for " + track.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, "More actions for " + track.Name);
            return btn;
        }

        // Open a file picker and add the chosen video(s)/image(s) to a track, then drop into Edit —
        // the click-to-load alternative to dragging from Explorer.
        private async void LoadIntoTrack(int trackIndex)
        {
            var openPicker = new FileOpenPicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            foreach (var ext in new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".jpg", ".jpeg", ".png", ".gif", ".bmp" })
                openPicker.FileTypeFilter.Add(ext);

            var files = await openPicker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            var paths = new List<string>();
            foreach (var f in files) paths.Add(f.Path);
            await ViewModel.AddClipsToTrackAsync(paths, trackIndex, ViewModel.CurrentStoryTime);
            SelectNewestClipOn(trackIndex);
        }

        // Spotlight opacity for a clip block, by mode:
        //   Arrange (not playing, nothing selected) -> everything full.
        //   Edit    (a clip selected)               -> that clip full, the rest dim.
        //   Play                                    -> every clip active at the playhead full, rest dim.
        private double BlockDim(CinematicOperation clip)
        {
            if (clip == null) return 1.0;                       // transitions / drag ghost

            // Edit spotlights the one edited clip; Play AND Arrange both spotlight whatever is on
            // screen at the playhead (the composite), so the timeline mirrors what you see.
            if (ViewModel.IsEditMode)
                return ReferenceEquals(clip, ViewModel.SelectedClip) ? 1.0 : 0.5;
            return IsActiveAtPlayhead(clip) ? 1.0 : 0.5;
        }

        private bool IsActiveAtPlayhead(CinematicOperation clip)
        {
            var t = ViewModel.CurrentStoryTime;
            if (ViewModel.TimelineNodes.Contains(clip))   // spine: the clip at the playhead
                return ViewModel.TimelineNodes.IndexOf(clip) == ViewModel.GetTimelineIndexForStoryTime(t);
            return clip.IsActiveAt(t);                    // overlay: live in its window
        }

        // Which clips are on screen right now — as a signature, so playback can rebuild the
        // highlights only when the active set actually changes (an overlay starts/ends), not every
        // frame. Spine boundaries already rebuild via the SelectedClip change.
        private int ActiveSignature()
        {
            var t = ViewModel.CurrentStoryTime;
            int sig = 17 * 31 + ViewModel.GetTimelineIndexForStoryTime(t);
            foreach (var track in ViewModel.Tracks)
                foreach (var ov in track.Clips)
                    if (ov.IsActiveAt(t)) sig = sig * 31 + ov.GetHashCode();
            return sig;
        }

        private void AddTimelineBlock(double x, double y, double width, double height, Windows.UI.Color color, CinematicOperation clip = null)
        {
            if (width < 1) width = 1;

            // Spotlight (#1): the in-focus clip(s) stay full strength, the rest dim. "In focus"
            // depends on mode — Arrange: all; Edit: the edited clip; Play: everything on screen now.
            double dim = BlockDim(clip);

            var topColor = Microsoft.UI.ColorHelper.FromArgb(color.A,
                (byte)Math.Min(255, color.R + 30),
                (byte)Math.Min(255, color.G + 30),
                (byte)Math.Min(255, color.B + 30));
            var gradient = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1)
            };
            gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = topColor, Offset = 0.0 });
            gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = color, Offset = 1.0 });

            bool isSelected = clip != null && ReferenceEquals(clip, ViewModel.SelectedClip);
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = 4,
                RadiusY = 4,
                Opacity = dim,
                Fill = gradient,
                Stroke = isSelected 
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) 
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(50, 0, 0, 0)),
                StrokeThickness = isSelected ? 2 : 1
            };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            TimelineBar.Children.Add(r);

            if (clip != null)
            {
                if (!_clipBlockElements.TryGetValue(clip, out var list))
                {
                    list = new List<UIElement>();
                    _clipBlockElements[clip] = list;
                }
                list.Add(r);
            }

            // File-name label inside the block, in whichever of black/white reads on this colour.
            if (clip != null && !string.IsNullOrEmpty(clip.FileName) && width > 24)
            {
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    IsHitTestVisible = false,
                    Opacity = dim,
                    Height = height // Constrain height to block height for proper vertical centering
                };
                
                var textColor = new Microsoft.UI.Xaml.Media.SolidColorBrush(TrackPalette.TextOn(color));
                var icon = new FontIcon
                {
                    Glyph = "\uE714", // Video icon
                    FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = textColor,
                    Margin = new Thickness(0, 0, 0, 0) // No artificial nudge
                };
                var label = new TextBlock
                {
                    Text = clip.FileName,
                    FontSize = 9,
                    MaxWidth = width - 24, // Provide enough room to prevent text clipping the right edge
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = textColor,
                    Margin = new Thickness(0, 0, 0, 0) // No artificial nudge
                };
                
                sp.Children.Add(icon);
                sp.Children.Add(label);
                
                Canvas.SetLeft(sp, x + 6); // Extra breathing room on the left
                Canvas.SetTop(sp, y); // Align exactly to top to let VerticalAlignment.Center do its job
                TimelineBar.Children.Add(sp);

                if (clip != null && _clipBlockElements.TryGetValue(clip, out var listSp)) listSp.Add(sp);
            }
        }

        // Timeline pointer model (standard NLE): the top ruler scrubs; the clip rows drag clips.
        // Tap in a row = select; drag in a row = move (overlay = reposition in time, spine =
        // reorder). Empty space in the rows also scrubs.
        private void TimelineBar_PointerPressed(object? sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(TimelineBar);
            // Only the left button (or a touch/pen contact, which also reports it) drives
            // scrub/select/drag. Without this, a right-click starts a drag and captures the
            // pointer, which suppresses RightTapped — i.e. no context menu.
            if (!point.Properties.IsLeftButtonPressed) return;

            // A click on the timeline leaves Edit and then does its normal job in the SAME gesture.
            // It used to only exit, so every edit cost a wasted click to get back to scrubbing.
            if (ViewModel.IsEditMode) ExitEditMode();

            var p = point.Position;
            _timelinePressPoint = p;
            _timelinePressed = true;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            TimelineBar.CapturePointer(e.Pointer);

            // The ruler scrubs — and so does Ctrl+drag anywhere, because a 14px strip is a small
            // target to have to hit whenever you want to move the playhead.
            bool forceScrub = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
            if (forceScrub || IsRulerY(p.Y)) { _timelineScrubbing = true; ScrubToX(p.X); return; }

            var hit = HitClip(p);
            if (hit.clip != null)
            {
                // A locked track's clips still SELECT — you can inspect them — but never move.
                // Lock guards mutation, not visibility.
                SelectClip(hit.clip);
                if (!ViewModel.Tracks[hit.trackIndex].IsLocked)
                {
                    _dragClip = hit.clip;
                    _dragTrackIndex = hit.trackIndex;
                    _dragGrabOffsetSec = (p.X / _timelinePxPerSec) - hit.startSec;
                }
            }
            else { _timelineScrubbing = true; ScrubToX(p.X); }
        }

        private void TimelineBar_PointerMoved(object? sender, PointerRoutedEventArgs e)
        {
            // Recorded even when not dragging: the context menu resolves its target from here.
            _lastHoverPoint = e.GetCurrentPoint(TimelineBar).Position;

            if (!_timelinePressed) return;
            var p = _lastHoverPoint;

            if (_timelineScrubbing) { ScrubToX(p.X); return; }
            if (_dragClip == null) return;
            if (!_timelineMovingClip && Math.Abs(p.X - _timelinePressPoint.X) < 4) return;
            _timelineMovingClip = true;

            // PREVIEW ONLY (ARCHITECTURE.md §5.7). A drag computes where the clip WOULD land and
            // redraws; it does not touch the model until the pointer is released. This used to
            // remove the clip from one track's collection and insert it into another's on every
            // pointer move, so a vertical wobble permanently reshuffled the project and there was
            // no way to back out.
            int previousTarget = _dragTargetTrackIndex;
            int previousInsert = _dragInsertIndex;
            double previousStart = _dragTargetStartSec;

            var from = ViewModel.Tracks[_dragTrackIndex];
            int hover = TrackIndexAtY(p.Y);
            var to = ViewModel.Tracks[hover];

            // A locked destination, or emptying a gapless source of its last clip, is not a legal
            // drop — so the preview stays on the source track rather than lying about the outcome.
            bool canLeaveSource = from.Clips.Count > 1 || !from.IsGapless;
            if (to.IsLocked || (hover != _dragTrackIndex && !canLeaveSource)) hover = _dragTrackIndex;

            _dragTargetTrackIndex = hover;
            _dragCursorX = p.X;

            if (ViewModel.Tracks[hover].IsGapless)
            {
                _dragInsertIndex = Math.Clamp(InsertIndexAt(ViewModel.Tracks[hover], p.X),
                                              0, ViewModel.Tracks[hover].Clips.Count);
                _dragTargetStartSec = double.NaN;   // position comes from order, not from x
            }
            else
            {
                double dur = _dragClip.OpDuration.TotalSeconds;
                double extent = TimelineGeometry.ExtentSeconds(ViewModel.ContentEnd.TotalSeconds);
                double want = Math.Clamp((p.X / _timelinePxPerSec) - _dragGrabOffsetSec,
                                         0, Math.Max(0, extent - dur));
                want = ApplyClipSnapping(want, dur, _dragClip);
                _dragTargetStartSec = ViewModel.Tracks[hover].ClampToFreeSlot(_dragClip, want, dur);
            }

            // Only redraw when the preview actually changed; otherwise nudge the ghost, which is
            // far cheaper than rebuilding every Canvas child at pointer rate.
            bool changed = previousTarget != _dragTargetTrackIndex
                           || previousInsert != _dragInsertIndex
                           || !NearlyEqual(previousStart, _dragTargetStartSec);
            if (changed) BuildTimelineBar();
            else MoveGhostTo(GhostX());
        }

        private static bool NearlyEqual(double a, double b)
            => (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) < 1e-6;

        // Where the dragged clip's ghost is drawn. On a gapless target it free-follows the cursor
        // while the drop gap opens beneath it; on a free target it sits at the resolved start time.
        private double GhostX()
            => double.IsNaN(_dragTargetStartSec)
                ? _dragCursorX - _dragGrabOffsetSec * _timelinePxPerSec
                : _dragTargetStartSec * _timelinePxPerSec;

        private void MoveGhostTo(double x)
        {
            if (_dragClip == null || !_clipBlockElements.TryGetValue(_dragClip, out var elements)) return;
            foreach (var el in elements) Canvas.SetLeft(el, el is StackPanel ? x + 6 : x);
        }

        // Apply the previewed move, exactly once. Everything up to here has been drawing.
        private void CommitDrag()
        {
            if (_dragClip == null || _dragTargetTrackIndex < 0) return;
            var from = ViewModel.Tracks[_dragTrackIndex];
            var to = ViewModel.Tracks[_dragTargetTrackIndex];
            if (from.IsLocked || to.IsLocked) return;

            if (!ReferenceEquals(from, to)) from.Clips.Remove(_dragClip);
            else to.Clips.Remove(_dragClip);

            if (to.IsGapless)
            {
                to.Clips.Insert(Math.Clamp(_dragInsertIndex, 0, to.Clips.Count), _dragClip);
            }
            else
            {
                if (!double.IsNaN(_dragTargetStartSec))
                    _dragClip.StartTime = TimeSpan.FromSeconds(_dragTargetStartSec);
                to.Clips.Add(_dragClip);
            }

            from.Normalize();
            to.Normalize();
        }

        // Abandon a drag without touching the model. The preview was never applied, so this is
        // just clearing state and redrawing.
        private void CancelDrag()
        {
            if (_dragClip == null && !_timelinePressed) return;
            bool wasDragging = _timelineMovingClip;
            _timelinePressed = false;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            _dragTrackIndex = -1;
            _dragTargetTrackIndex = -1;
            _dragTargetStartSec = double.NaN;
            if (wasDragging) BuildTimelineBar();
        }

        // Where a dragged clip would drop into a gapless track: how many OTHER clips have their
        // centre left of the cursor, measured in the layout with the dragged clip removed.
        // Monotonic, so it can't oscillate as the cursor moves.
        private int InsertIndexAt(TimelineTrack track, double cursorX)
        {
            int insert = 0;
            double x = 0;
            foreach (var clip in track.Clips)
            {
                if (clip == _dragClip) continue;
                double w = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                if (x + w / 2 < cursorX) insert++;
                x += w + clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
            }
            return insert;
        }

        private int ComputeSpineInsertIndex(double cursorX) => InsertIndexAt(ViewModel.Tracks[0], cursorX);

        // Losing capture (alt-tab, a system gesture) abandons the drag rather than leaving the
        // preview stranded on screen with no way to finish it.
        private void TimelineBar_PointerCaptureLost(object? sender, PointerRoutedEventArgs e) => CancelDrag();

        private void TimelineBar_PointerReleased(object? sender, PointerRoutedEventArgs e)
        {
            // This fires for the RIGHT button too. If we never started a left-press, do nothing —
            // in particular do NOT rebuild the bar: rebuilding destroys every Canvas child,
            // including the element the right-tap gesture started on, which kills the pending
            // context gesture before the flyout can open.
            if (!_timelinePressed) return;

            TimelineBar.ReleasePointerCapture(e.Pointer);
            bool wasMoving = _timelineMovingClip;

            if (wasMoving && _dragClip != null) CommitDrag();

            _timelinePressed = false;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            _dragTrackIndex = -1;
            _dragTargetTrackIndex = -1;
            _dragTargetStartSec = double.NaN;

            if (wasMoving)
            {
                BuildTimelineBar();              // redraw from the committed model, not the preview
                _playbackEngine?.RefreshComposite();
                ViewModel.RecordIfChanged();     // the whole move is one undo step
            }
        }

        // Duplicate / Remove for the block under the cursor. The platform opens the ContextFlyout;
        // we just resolve which clip it applies to from the last pointer position.
        private CinematicOperation _contextClip;

        private void TimelineContextMenu_Opening(object? sender, object e)
        {
            // Keep this trivial: just record what was under the cursor. It previously also called
            // SelectClip (which changes mode / starts async work) — an exception in Opening aborts
            // the flyout, which is a candidate for "right-click does nothing".
            var hit = HitClip(_lastHoverPoint);
            _contextClip = hit.clip;
            _contextTrackIndex = hit.clip != null ? hit.trackIndex : TrackIndexAtY(_lastHoverPoint.Y);

            // Clip actions are disabled on bare lane space and on locked tracks. They used to stay
            // enabled with no clip resolved, so every one of them silently did nothing — a menu
            // that looks live and isn't is worse than no menu.
            bool locked = _contextTrackIndex >= 0 && _contextTrackIndex < ViewModel.Tracks.Count
                          && ViewModel.Tracks[_contextTrackIndex].IsLocked;
            bool canEditClip = _contextClip != null && !locked;
            foreach (var item in new[] { TimelineSplitItem, TimelineSnapshotItem,
                                         TimelineDuplicateItem, TimelineRemoveItem })
                if (item != null) item.IsEnabled = canEditClip;

            if (TimelineAddHereItem != null)
            {
                TimelineAddHereItem.IsEnabled = !locked;
                TimelineAddHereItem.Text = _contextTrackIndex >= 0
                    ? $"Add clips to Track {_contextTrackIndex + 1}…"
                    : "Add clips…";
            }
        }

        private int _contextTrackIndex = -1;

        private void TimelineAddHere_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextTrackIndex >= 0) LoadIntoTrack(_contextTrackIndex);
        }

        private void TimelineSplit_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) SplitClip(_contextClip);
        }

        private void TimelineSnapshot_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) SnapshotClip(_contextClip);
        }

        private void TimelineDuplicate_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) DuplicateClip(_contextClip);
        }

        private void TimelineRemove_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) RemoveClip(_contextClip);
        }

        // A full clone of a clip's editable state. SourceDuration and PlaybackSpeed must precede
        // the trim: the trim setters clamp against the source length and derive OpDuration from speed.
        private static CinematicOperation CloneClip(CinematicOperation clip) => new CinematicOperation
        {
            FilePath = clip.FilePath,
            SourceDuration = clip.SourceDuration,
            SourceAspect = clip.SourceAspect,
            PlaybackSpeed = clip.PlaybackSpeed,
            VideoStartTime = clip.VideoStartTime,
            VideoEndTime = clip.VideoEndTime,
            CurveProfile = clip.CurveProfile,
            StartMark = clip.StartMark.Clone(),
            MidMark = clip.MidMark?.Clone(),
            EndMark = clip.EndMark.Clone(),
            TransitionDuration = clip.TransitionDuration,
            TransitionStyle = clip.TransitionStyle,
            Opacity = clip.Opacity,
            Volume = clip.Volume,
            PlacementWidth = clip.PlacementWidth,
            PlacementHeight = clip.PlacementHeight,
            PlacementCenterX = clip.PlacementCenterX,
            PlacementCenterY = clip.PlacementCenterY,
            Thumbnail = clip.Thumbnail
        };

        // Insert a new clip right after `after` on the same track (spine order, or the overlay
        // track), placing overlays at the requested start time clamped to a free slot.
        // Insert a new clip immediately after `after` on the same track, whichever track that is.
        // A gapless track just needs the list position — Normalize derives the start time. A free
        // track needs a real start time, clamped so it does not land on a sibling.
        private void InsertAfter(CinematicOperation after, CinematicOperation toInsert, double desiredStartSec)
        {
            var track = TrackOf(after);
            if (track == null || track.IsLocked) return;
            int i = track.Clips.IndexOf(after);
            if (i < 0) return;

            if (!track.IsGapless)
                toInsert.StartTime = TimeSpan.FromSeconds(
                    track.ClampToFreeSlot(null, desiredStartSec, toInsert.OpDuration.TotalSeconds));

            track.Clips.Insert(i + 1, toInsert);
            track.Normalize();

            ViewModel.RecordIfChanged();
            BuildTimelineBar();
            _playbackEngine?.RefreshComposite();
        }

        private void DuplicateClip(CinematicOperation clip)
        {
            var copy = CloneClip(clip);
            InsertAfter(clip, copy, clip.StartTime.TotalSeconds + clip.OpDuration.TotalSeconds);
        }

        // Cut the clip in two at the playhead (or its midpoint if the playhead isn't inside it).
        private void SplitClip(CinematicOperation clip)
        {
            double startS = clip.VideoStartTime.TotalSeconds;
            double endS = clip.VideoEndTime.TotalSeconds;
            double window = endS - startS;
            if (window < 0.4) return; // too short to split into two usable halves

            double cutS = startS + SplitFraction(clip) * window;

            var second = CloneClip(clip);
            clip.VideoEndTime = TimeSpan.FromSeconds(cutS);      // first half ends at the cut
            second.VideoStartTime = TimeSpan.FromSeconds(cutS);  // second half starts at the cut
            second.VideoEndTime = TimeSpan.FromSeconds(endS);

            // Second half sits immediately after the (now shorter) first half.
            InsertAfter(clip, second, clip.StartTime.TotalSeconds + clip.OpDuration.TotalSeconds);
        }

        // Freeze the current frame as a 10s still with a slow Ken Burns push-in — a one-click
        // alternative to duplicate-then-set-speed-0-and-marks.
        private void SnapshotClip(CinematicOperation clip)
        {
            double startS = clip.VideoStartTime.TotalSeconds;
            double window = clip.VideoEndTime.TotalSeconds - startS;
            double frozen = startS + SplitFraction(clip) * window;
            double srcLen = clip.SourceDuration.TotalSeconds > 0 ? clip.SourceDuration.TotalSeconds : frozen + 1;
            frozen = Math.Clamp(frozen, 0, Math.Max(0, srcLen - 0.2));

            var snap = new CinematicOperation
            {
                FilePath = clip.FilePath,
                SourceDuration = clip.SourceDuration,
                SourceAspect = clip.SourceAspect,
                PlaybackSpeed = 0,   // still — set before OpDuration so 10s is a hold time, not a re-trim
                VideoStartTime = TimeSpan.FromSeconds(frozen),
                VideoEndTime = TimeSpan.FromSeconds(Math.Min(srcLen, frozen + 0.1)),
                OpDuration = TimeSpan.FromSeconds(10),
                StartMark = new SpatialMark(1.0, 0.5, 0.5),
                EndMark = new SpatialMark(1.25, 0.5, 0.5), // default push-in
                Opacity = clip.Opacity,
                PlacementWidth = clip.PlacementWidth,
                PlacementHeight = clip.PlacementHeight,
                PlacementCenterX = clip.PlacementCenterX,
                PlacementCenterY = clip.PlacementCenterY,
                Thumbnail = clip.Thumbnail
            };

            InsertAfter(clip, snap, clip.StartTime.TotalSeconds + clip.OpDuration.TotalSeconds);
        }

        // Where to cut/freeze within a clip's source window, as a 0..1 fraction: the playhead if it
        // falls inside this clip, otherwise the midpoint. Kept off the very edges so no sliver.
        private double SplitFraction(CinematicOperation clip)
        {
            double op = clip.OpDuration.TotalSeconds;
            if (op <= 0) return 0.5;
            double clipStartStory = clip.StartTime.TotalSeconds;
            double into = ViewModel.CurrentStoryTime.TotalSeconds - clipStartStory;
            double f = (into > 0 && into < op) ? into / op : 0.5;
            return Math.Clamp(f, 0.05, 0.95);
        }

        private void RemoveClip(CinematicOperation clip)
        {
            var track = TrackOf(clip);
            if (track == null || track.IsLocked) return;
            track.Clips.Remove(clip);
            track.Normalize();
            if (ReferenceEquals(ViewModel.SelectedClip, clip)) ViewModel.SelectedClip = null;
            ViewModel.RecordIfChanged();
        }

        private List<double> GetTimelineSnapPoints(CinematicOperation? ignoreClip, bool includePlayhead)
        {
            var points = new List<double> { 0.0 };
            if (includePlayhead && ViewModel != null)
                points.Add(ViewModel.CurrentStoryTime.TotalSeconds);
            
            // Every track contributes its clip edges. Track 0 used to be walked separately as
            // well, which double-counted its edges into the snap set.
            if (ViewModel?.Tracks != null)
            {
                foreach (var trk in ViewModel.Tracks)
                {
                    foreach (var c in trk.Clips)
                    {
                        if (c == ignoreClip) continue;
                        points.Add(c.StartTimeSeconds);
                        points.Add(c.StartTimeSeconds + c.OpDuration.TotalSeconds);
                    }
                }
            }
            return points;
        }

        private double ApplyScrubSnapping(double sec)
        {
            if (ViewModel == null || !ViewModel.IsSnappingEnabled || _timelinePxPerSec <= 0) return sec;
            double threshold = 8.0 / _timelinePxPerSec; // 8px magnetic radius
            double best = sec;
            double minDiff = threshold;
            foreach (double sp in GetTimelineSnapPoints(null, includePlayhead: false))
            {
                double diff = Math.Abs(sec - sp);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    best = sp;
                }
            }
            return best;
        }

        private double ApplyClipSnapping(double desiredStartSec, double durSec, CinematicOperation ignoreClip)
        {
            if (ViewModel == null || !ViewModel.IsSnappingEnabled || _timelinePxPerSec <= 0) return desiredStartSec;
            double threshold = 8.0 / _timelinePxPerSec; // 8px magnetic radius
            double best = desiredStartSec;
            double minDiff = threshold;
            foreach (double sp in GetTimelineSnapPoints(ignoreClip, includePlayhead: true))
            {
                double diffLeft = Math.Abs(desiredStartSec - sp);
                if (diffLeft < minDiff)
                {
                    minDiff = diffLeft;
                    best = sp;
                }
                double diffRight = Math.Abs((desiredStartSec + durSec) - sp);
                if (diffRight < minDiff)
                {
                    minDiff = diffRight;
                    best = sp - durSec;
                }
            }
            return best;
        }

        // Map x -> story time and seek the composite (spine frame + active overlays).
        private void ScrubToX(double x)
        {
            if (_timelinePxPerSec <= 0) return;
            double total = ViewModel.TotalStoryDuration.TotalSeconds;
            double sec = Math.Clamp(x / _timelinePxPerSec, 0, total);
            sec = ApplyScrubSnapping(sec);
            _playbackEngine?.SeekCompositeToStoryTime(TimeSpan.FromSeconds(sec));
        }

        // Which clip (and its start-second) sits under a point in the clip lanes, if any.
        // The ruler owns no clips — a press there scrubs.
        private (CinematicOperation clip, int trackIndex, double startSec) HitClip(Windows.Foundation.Point p)
        {
            if (_timelinePxPerSec <= 0 || IsRulerY(p.Y)) return (null, -1, 0);
            var t = TimeSpan.FromSeconds(Math.Max(0, p.X / _timelinePxPerSec));

            // Every lane resolves the same way: ask the track what is visible at t. Track 0 used
            // to take a different path that assumed no gaps, so a click in a gap resolved to
            // whichever clip happened to precede it.
            int trackIndex = TrackIndexAtY(p.Y);
            if (trackIndex < 0 || trackIndex >= ViewModel.Tracks.Count) return (null, -1, 0);

            var clip = ViewModel.Tracks[trackIndex].ClipAt(t);
            return clip != null ? (clip, trackIndex, clip.StartTimeSeconds) : (null, -1, 0);
        }

        // Selection is JUST selection. It shows the clip in the inspector and marks it on the
        // timeline and canvas; it does not change mode. Selecting used to call BeginEdit, which
        // meant you could not look at a clip's properties without the screen swapping to that one
        // clip full-frame. Edit is now entered deliberately — see BeginEditSelected.
        // Selecting a clip means "work on this clip" and nothing else, on every track.
        private void SelectClip(CinematicOperation clip) => ViewModel.SelectedClip = clip;

        // The one way into Edit mode, whatever triggered it (double-click a timeline block,
        // double-tap the canvas, Enter, or the inspector's Edit framing button).
        private void BeginEditSelected()
        {
            if (ViewModel.IsPlaying || ViewModel.SelectedClip == null) return;
            _playbackEngine?.BeginEdit(ViewModel.SelectedClip, ViewModel.CurrentEditTarget);
        }

        // Double-clicking a clip block on the timeline opens it for framing. The first click of the
        // pair has already selected it via PointerPressed.
        private void TimelineBar_DoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
        {
            if (ViewModel.IsEditMode) return;
            var hit = HitClip(e.GetPosition(TimelineBar));
            if (hit.clip == null) return;
            SelectClip(hit.clip);
            BeginEditSelected();
            e.Handled = true;
        }

        private void EditFraming_Click(object? sender, RoutedEventArgs e) => BeginEditSelected();

        private void UpdatePlayhead()
        {
            if (_playhead == null || _timelinePxPerSec <= 0) return;
            double sec = ViewModel.CurrentStoryTime.TotalSeconds;
            double x = sec * _timelinePxPerSec;
            Canvas.SetLeft(_playhead, x);
            if (_playheadKnob != null) Canvas.SetLeft(_playheadKnob, x - 4.5);

            if (_playheadTime != null)
            {
                int m = (int)(sec / 60);
                _playheadTime.Text = m > 0 ? $"{m}:{sec - m * 60:00.0}" : $"{sec:0.0}s";
                // Sit just right of the playhead, flipping to the left near the right edge.
                double w = TimelineBar?.ActualWidth ?? 0;
                double tx = x + 4;
                if (tx > w - 40) tx = x - 40;
                Canvas.SetLeft(_playheadTime, System.Math.Max(0, tx));
                Canvas.SetTop(_playheadTime, RulerH + 1);
            }

            // Follow the playhead while it is moving under its own steam. Only while playing —
            // during Arrange the user drives the scroll and auto-scrolling would fight them.
            if (ViewModel.IsPlaying) ScrollPlayheadIntoView(centre: false);
        }

        private void PlayerControl_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            // The canvas resizes when the bottom dock is toggled — keep WYSIWYG/overlay aligned.
            _playbackEngine?.OnViewportResized();
        }


        private void ViewModel_EditTargetChanged(object? sender, CinematicOperation op)
        {
            if (!ViewModel.IsPlaying)
            {
                _playbackEngine?.BeginEdit(op, ViewModel.CurrentEditTarget);
            }
        }

        private void PlayerControl_ViewportTransformChanged(object? sender, EventArgs e)
        {
            if (ViewModel.IsPlaying || ViewModel.SelectedClip == null) return;
            var op = ViewModel.SelectedClip as CinematicOperation;
            var transform = PlayerControl.ActiveTransform;
            if (op == null || transform == null) return;
            
            // Only update the WYSIWYG overlay visual positions based on current viewport
            _playbackEngine?.RefreshEditView();
        }

        // Keyframe capture is identical for every track: it grabs the current content framing
        // (the edit-mode transform) onto the selected clip. One handler, whichever track is live.
        // Collapse the scrubber to just the trimmed range so it plays/scrubs the resulting short
        // clip like any other clip. Double-clicking the scrubber returns to the full source.
        private void Trim_Click(object? sender, RoutedEventArgs e)
        {
            ClipScrubber?.EnterTrimmedView();
        }

        // ==================== Advanced NLE Mechanic Stubs (Visual Foundations) ====================
        //
        // These stub handlers establish the structural blueprint for upcoming NLE features, ensuring
        // clean domain separation between Arrange Mode and Edit Mode without piecemeal architectural drift.

        private void FrameStepBack_Click(object? sender, RoutedEventArgs e)
        {
            StepFrame(-1);
        }

        private void FrameStepForward_Click(object? sender, RoutedEventArgs e)
        {
            StepFrame(1);
        }

        private void StepFrame(int direction)
        {
            if (!ViewModel.IsEditMode)
            {
                double stepSec = direction > 0 ? 1.0 : -1.0;
                double newStoryTime = Math.Clamp(ViewModel.CurrentStoryTime.TotalSeconds + stepSec, 0, ViewModel.TotalStoryDuration.TotalSeconds);
                _playbackEngine?.SeekCompositeToStoryTime(TimeSpan.FromSeconds(newStoryTime));
                return;
            }

            var op = ViewModel.SelectedClip;
            if (op == null) return;

            double fps = 30.0;
            double frameDuration = 1.0 / fps;
            double target = Math.Clamp(ViewModel.CurrentOperationTimeSeconds + direction * frameDuration, op.VideoStartTime.TotalSeconds, op.VideoEndTime.TotalSeconds);
            ViewModel.CurrentOperationTimeSeconds = target;
        }

        private void MagnetButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
            {
                ViewModel.IsSnappingEnabled = tb.IsChecked ?? true;
            }
        }

        // ---- Timeline zoom ----------------------------------------------------------------
        // The scale is proportional (px/sec = width / totalDuration), so zoom is a multiplier on
        // the viewport width: 1.0 == the whole project fits. Every zoom change keeps the playhead
        // on screen, otherwise zooming in on a long project strands you at an arbitrary offset.

        private const double ZoomStep = 1.3333333;
        private const double MaxZoom = 16.0;

        private void SetTimelineZoom(double factor)
        {
            factor = Math.Clamp(factor, 1.0, MaxZoom);
            if (factor <= 1.01) factor = 1.0;
            if (Math.Abs(factor - _timelineZoomFactor) < 0.0001) return;
            _timelineZoomFactor = factor;
            BuildTimelineBar();
            UpdateZoomReadout();
            ScrollPlayheadIntoView(centre: true);
        }

        private void UpdateZoomReadout()
        {
            if (ZoomReadout != null) ZoomReadout.Text = $"{_timelineZoomFactor * 100:0}%";
        }

        private void ZoomInTimeline_Click(object? sender, RoutedEventArgs e) => SetTimelineZoom(_timelineZoomFactor * ZoomStep);
        private void ZoomOutTimeline_Click(object? sender, RoutedEventArgs e) => SetTimelineZoom(_timelineZoomFactor / ZoomStep);
        private void FitTimeline_Click(object? sender, RoutedEventArgs e) => SetTimelineZoom(1.0);

        // Ctrl+scroll over the timeline zooms; a bare scroll falls through to the ScrollViewer so
        // it still pans horizontally.
        private void TimelineBar_PointerWheelChanged(object? sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(TimelineBar);
            if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
            e.Handled = true;
            SetTimelineZoom(point.Properties.MouseWheelDelta > 0
                ? _timelineZoomFactor * ZoomStep
                : _timelineZoomFactor / ZoomStep);
        }

        // Keep the playhead visible when the timeline is wider than its viewport. Called on zoom
        // (centring it, since the user's focus is the playhead) and during playback (nudging it
        // back into view only once it leaves, so it doesn't fight a manual scroll).
        private void ScrollPlayheadIntoView(bool centre)
        {
            if (TimelineScroller == null || _timelinePxPerSec <= 0) return;

            // The zoom path calls this immediately after BuildTimelineBar has set a new canvas
            // width, so the ScrollViewer's extent is still the old one. Force layout first.
            // Only on that path: the playback path runs per frame, where layout is already settled
            // and an UpdateLayout call every frame would be far too expensive.
            if (centre) TimelineScroller.UpdateLayout();

            double viewport = TimelineScroller.ViewportWidth;
            double extent = TimelineScroller.ExtentWidth;
            if (viewport <= 0 || extent <= viewport) return;

            double x = ViewModel.CurrentStoryTime.TotalSeconds * _timelinePxPerSec;
            double offset = TimelineScroller.HorizontalOffset;

            double target;
            if (centre)
            {
                target = x - viewport / 2;
            }
            else
            {
                const double margin = 48;
                if (x >= offset + margin && x <= offset + viewport - margin) return; // already comfortable
                target = x - viewport / 2;
            }

            TimelineScroller.ChangeView(Math.Clamp(target, 0, extent - viewport), null, null, disableAnimation: true);
        }

        // Choose which keyframe the framing canvas is working on. These used to CAPTURE the
        // current on-screen framing into a mark; now the rectangles are the marks, so picking a
        // target just moves the selection (and the canvas seeks to that point in the clip).
        private void PickTarget(EditTarget target)
        {
            if (!ViewModel.IsEditMode || ViewModel.SelectedClip == null) return;
            ViewModel.CurrentEditTarget = target;
            _playbackEngine?.RefreshEditView();
        }

        private void PickStart_Click(object? sender, RoutedEventArgs e) => PickTarget(EditTarget.Start);
        private void PickEnd_Click(object? sender, RoutedEventArgs e) => PickTarget(EditTarget.End);

        // Mid is optional, so picking it creates one if the clip has none — starting from the
        // framing that is already interpolated there, which is the least surprising place for it.
        private void PickMid_Click(object? sender, RoutedEventArgs e)
        {
            var clip = ViewModel.SelectedClip;
            if (!ViewModel.IsEditMode || clip == null) return;
            if (clip.MidMark == null)
            {
                clip.MidMark = new SpatialMark(
                    (clip.StartMark.Zoom + clip.EndMark.Zoom) / 2,
                    (clip.StartMark.CenterX + clip.EndMark.CenterX) / 2,
                    (clip.StartMark.CenterY + clip.EndMark.CenterY) / 2);
                ViewModel.RecordIfChanged();
            }
            PickTarget(EditTarget.Mid);
        }

        // ---- Placement presets ---------------------------------------------------------------
        // Applied straight to the selected clip, on any track — track 0 has a placement box like
        // everything else since C2b, so these work on it too.

        private void ApplyPlacement(Action<CinematicOperation> change)
        {
            var clip = ViewModel.SelectedClip;
            if (clip == null) return;
            var track = ViewModel.TrackOf(clip);
            if (track != null && track.IsLocked) return;

            change(clip);
            ViewModel.RecordIfChanged();
            _playbackEngine?.RefreshComposite();
            _playbackEngine?.RefreshEditView();
        }

        private void PlaceFullFrame_Click(object? sender, RoutedEventArgs e)
            => ApplyPlacement(c => c.PlaceFullFrame());

        // Fill depends on how the source's shape compares to the output's, so it needs the viewport.
        private void PlaceFill_Click(object? sender, RoutedEventArgs e)
            => ApplyPlacement(c => c.PlaceFill(PlayerControl.ActualWidth, PlayerControl.ActualHeight));

        private void PlaceCorner_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not string tag) return;
            var parts = tag.Split(',');
            if (parts.Length != 2) return;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cx)) return;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cy)) return;
            ApplyPlacement(c => c.PlaceAt(cx, cy));
        }

        private void ResetPlacement_Click(object? sender, RoutedEventArgs e)
            => ApplyPlacement(c => c.ResetPlacement());

        private void ResultView_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
                _playbackEngine?.SetResultView(tb.IsChecked ?? false);
        }

        // Explicit Mid removal. Right-clicking the Mid button still works, but it was the ONLY
        // way to do this, which made it undiscoverable.
        private void RemoveMid_Click(object? sender, RoutedEventArgs e) => RemoveMid();

        private void ClearMid_RightTapped(object? sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            RemoveMid();
            e.Handled = true;
        }

        private void RemoveMid()
        {
            var op = ViewModel.SelectedClip;
            if (op == null || op.MidMark == null) return;
            op.MidMark = null;
            op.MidTime = 0.5;
            if (ViewModel.CurrentEditTarget == EditTarget.Mid) ViewModel.CurrentEditTarget = EditTarget.Start;
            ViewModel.RecordIfChanged();
            _playbackEngine?.RefreshEditView();
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectorViewModel.CurrentStoryTime))
            {
                UpdatePlayhead();
                // Play OR Arrange (scrubbing): refresh the spotlight when the set of clips on
                // screen changes (not every frame). Edit ignores the playhead.
                if (!ViewModel.IsEditMode)
                {
                    int sig = ActiveSignature();
                    if (sig != _lastActiveSignature) { _lastActiveSignature = sig; BuildTimelineBar(); }
                }
                return;
            }
            if (e.PropertyName == nameof(DirectorViewModel.IsEditMode))
            {
                BuildTimelineBar(); // spotlight switches between Edit and Arrange
                // Zone F: the global timeline recedes (dims) in Edit so it can't be confused with the
                // Playbar's per-clip scrubber. It stays clickable — a click on it exits Edit.
                if (TrackDock != null)
                    TrackDock.Opacity = ViewModel.IsEditMode ? 0.5 : 1.0;

                if (ViewModel.IsEditMode)
                {
                    _pulsePhase = 0;
                    _pulseTimer.Start();
                    ClipScrubber?.AutoFitTrimRange();
                }
                else
                {
                    _pulseTimer.Stop();
                    if (ModeBadgeButton != null) ModeBadgeButton.Opacity = 1.0;
                }
                return;
            }
            if (e.PropertyName == nameof(DirectorViewModel.SelectedClip))
            {
                BuildTimelineBar();                  // redraw so the selection highlight moves
                _playbackEngine?.RefreshComposite(); // and so the PiP chrome follows the selection
                if (ViewModel.IsEditMode)
                {
                    ClipScrubber?.AutoFitTrimRange();
                }
            }
            if (e.PropertyName == nameof(DirectorViewModel.IsPlaying))
            {
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Symbol = ViewModel.IsPlaying ? Symbol.Pause : Symbol.Play;
                }
                _playbackEngine?.RefreshEditView();

                // Whenever playback stops by ANY route (pause, stop, reaching the end), put the
                // PiPs back into arrangeable stills. Keying off the observable state rather than
                // one specific method means no path can miss it.
                if (!ViewModel.IsPlaying) _playbackEngine?.RefreshComposite();

                // Rebuild so the spotlight switches between play-mode (active clips) and
                // selection-mode logic; reset the signature so the next play refreshes.
                _lastActiveSignature = -1;
                BuildTimelineBar();
            }
            // Note: entering Edit on selection is owned by SelectClip -> BeginEdit (one entry point
            // for both tracks), so there's no per-track edit trigger here anymore.
        }

        // The media types a drop accepts. Was duplicated inline at both drop handlers, which is
        // how they drifted -- the canvas accepted a different set from the timeline.
        private static bool IsSupportedMedia(string fileType)
        {
            foreach (var ext in new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" })
                if (string.Equals(ext, fileType, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void Grid_DragOver(object? sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
            }
        }

        private async void Grid_Drop(object? sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new System.Collections.Generic.List<string>();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file && IsSupportedMedia(file.FileType))
                    {
                        paths.Add(item.Path);
                    }
                }

                if (paths.Count > 0)
                {
                    await ViewModel.AddFilesAsync(paths);
                    SelectNewestClipOn(0);  // select the new clip so its properties are to hand
                }
            }
        }

        // After adding, select the newest clip so its properties are to hand. It does NOT drop
        // into Edit: adding is an Arrange activity, and the inspector is now available without
        // leaving the composite.
        private void SelectNewestClipOn(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= ViewModel.Tracks.Count) return;
            var clips = ViewModel.Tracks[trackIndex].Clips;
            if (clips.Count == 0) return;
            if (ViewModel.IsPlaying) _playbackEngine?.StopPlayback();
            SelectClip(clips[^1]);
        }

        private async void PlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (_playbackEngine == null) return;
            // Strict segregation: in Edit mode, Play previews ONLY the edited clip's motion;
            // in Arrange mode, Play plays the whole composite.
            if (_playbackEngine.IsEditMode)
            {
                _playbackEngine.ToggleEditPreview();
            }
            else
            {
                await _playbackEngine.TogglePlayPauseAsync();
            }
        }

        private bool _wasPlayingBeforeDrag = false;

        // The scrubber's trim handles are OneWay-bound (display only); a drag writes the model here.
        // Doing it explicitly (not via a TwoWay binding on a shared control) is what stops one clip's
        // trim from being clobbered when you switch between clips.
        private void ClipScrubber_TrimChanged(object? sender, EventArgs e)
        {
            if (ViewModel.SelectedClip is not CinematicOperation clip) return;
            clip.VideoStartTime = TimeSpan.FromSeconds(ClipScrubber.TrimStart);
            clip.VideoEndTime = TimeSpan.FromSeconds(ClipScrubber.TrimEnd);
        }

        private void TimelineRangeSlider_InteractionStarted(object? sender, EventArgs e)
        {
            _wasPlayingBeforeDrag = ViewModel.IsPlaying;
            if (_wasPlayingBeforeDrag && _playbackEngine != null)
            {
                _ = _playbackEngine.TogglePlayPauseAsync(); // Pauses playback while dragging
            }
        }

        private async void TimelineRangeSlider_InteractionCompleted(object? sender, EventArgs e)
        {
            if (_wasPlayingBeforeDrag && !ViewModel.IsPlaying && _playbackEngine != null)
            {
                await Task.Delay(100); // Give the player a tiny moment to settle the final scrub
                _ = _playbackEngine.TogglePlayPauseAsync(); // Resumes playback
            }
        }

        /// <summary>
        /// Opens the project's issue tracker in the default browser. Most users reach the app
        /// through a Releases zip and never see the repository, so the feedback route has to be
        /// reachable from inside the app to be used at all.
        /// </summary>
        private void ReportProblem_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/ec928/VideoDirector/issues/new/choose",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                // Never let a missing browser association take the app down.
                System.Diagnostics.Debug.WriteLine($"[ReportProblem] Could not open issue tracker: {ex.Message}");
            }
        }

        private void Prev_Click(object? sender, RoutedEventArgs e)
        {
            _playbackEngine?.SkipPrevious();
        }

        private void Next_Click(object? sender, RoutedEventArgs e)
        {
            _playbackEngine?.SkipNext();
        }

        // Resizes the APPLICATION WINDOW to match the video's aspect ratio. Deliberately no longer
        // touches timeline zoom — that is FitTimeline_Click, and conflating the two was why this
        // button read as a view control.
        private async void FitWindow_Click(object? sender, RoutedEventArgs e)
        {
            double targetAspect = 16.0 / 9.0;
            var mpA = PlayerControl.PlayerA?.MediaPlayer;
            var mpB = PlayerControl.PlayerB?.MediaPlayer;
            
            // Get the true video aspect ratio directly from the file container
            // This bypasses Windows Media Foundation padding (e.g. 1918x804 padded to 1920x816)
            var activeClip = System.Linq.Enumerable.FirstOrDefault(ViewModel.TimelineNodes);
            if (activeClip != null && !string.IsNullOrEmpty(activeClip.FilePath))
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(activeClip.FilePath);
                    var props = await file.Properties.GetVideoPropertiesAsync();
                    if (props != null && props.Width > 0 && props.Height > 0)
                    {
                        targetAspect = (double)props.Width / props.Height;
                    }
                }
                catch { }
            }

            // Fallback to WMF dimensions if file properties failed
            if (targetAspect == 16.0 / 9.0)
            {
                var activePlayer = PlayerControl.PlayerA.Opacity > 0.5 ? mpA : mpB;
                if (activePlayer != null && activePlayer.PlaybackSession != null)
                {
                    uint vw = activePlayer.PlaybackSession.NaturalVideoWidth;
                    uint vh = activePlayer.PlaybackSession.NaturalVideoHeight;
                    if (vw > 0 && vh > 0)
                    {
                        targetAspect = (double)vw / vh;
                    }
                }
            }

            double w = PlayerControl.ActualWidth;
            double h = PlayerControl.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var appWindow = MainWindow.Instance.AppWindow;
            if (appWindow == null) return;

            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;

            // Calculate chrome (timeline dock, etc.) from the exact physical client size
            double physicalClientW = appWindow.ClientSize.Width;
            double physicalClientH = appWindow.ClientSize.Height;
            double logicalClientW = physicalClientW / scale;
            double logicalClientH = physicalClientH / scale;

            double chromeW = logicalClientW - w;
            double chromeH = logicalClientH - h;

            // USER RULE: Baseline the window against the CURRENT horizontal width.
            // Horizontal does not change. Shrink the vertical height to remove empty space.
            double newClientLogicalWidth = w + chromeW;
            double newClientLogicalHeight = (w / targetAspect) + chromeH;

            // Floor the height to ensure we don't get a 1px gap from rounding up
            int winWidthPhysical = (int)System.Math.Round(newClientLogicalWidth * scale);
            int winHeightPhysical = (int)System.Math.Floor(newClientLogicalHeight * scale);

            appWindow.ResizeClient(new Windows.Graphics.SizeInt32(winWidthPhysical, winHeightPhysical));
        }

        private async void Save_Click(object? sender, RoutedEventArgs e)
        {
            var savePicker = new FileSavePicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Director Sequence", new List<string>() { ".json" });
            savePicker.SuggestedFileName = "NewSequence";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                await ViewModel.SaveAsync(file);
            }
        }

        private async void Load_Click(object? sender, RoutedEventArgs e)
        {
            var openPicker = new FileOpenPicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);

            openPicker.ViewMode = PickerViewMode.List;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".json");

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                await ViewModel.LoadAsync(file);
                if (ViewModel.IsAutoPlayEnabled && ViewModel.TimelineNodes.Count > 0)
                {
                    _ = _playbackEngine?.StartPlaybackAsync(0);
                }
            }
        }

        private async void Clear_Click(object? sender, RoutedEventArgs e)
        {
            bool hasContent = false;
            foreach (var t in ViewModel.Tracks)
                if (t.Clips.Count > 0) { hasContent = true; break; }

            if (hasContent)
            {
                var dialog = new ContentDialog
                {
                    Title = "Clear project?",
                    Content = "This removes every clip from all tracks. You can undo it with Ctrl+Z.",
                    PrimaryButtonText = "Clear",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            ViewModel.Clear();
        }

        private async void Export_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.ContentEnd <= TimeSpan.Zero)
            {
                await ShowExportMessage("Nothing to export", "Add at least one clip first.");
                return;
            }

            var savePicker = new FileSavePicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            savePicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            savePicker.FileTypeChoices.Add("MP4 Video", new List<string>() { ".mp4" });
            savePicker.SuggestedFileName = "Export";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            var bar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 320 };
            var status = new TextBlock { Text = "Rendering the composite (spine + overlays) — this can take a while for long clips." };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(status);
            panel.Children.Add(bar);
            var progressDialog = new ContentDialog
            {
                Title = "Exporting video",
                Content = panel,
                XamlRoot = this.XamlRoot
            };

            var exporter = new Models.VideoExporter();
            var progress = new Progress<double>(p => bar.Value = p);

            _ = progressDialog.ShowAsync(); // non-blocking; hidden when the render finishes
            var result = await exporter.ExportAsync(ViewModel.Tracks, file, progress);
            progressDialog.Hide();

            switch (result.Outcome)
            {
                case Models.VideoExporter.ExportOutcome.Success:
                    var msg = $"Saved to:\n{result.Message}";
                    if (result.SkippedFiles.Count > 0)
                        msg += $"\n\nSkipped {result.SkippedFiles.Count} clip(s) with missing files:\n• " + string.Join("\n• ", result.SkippedFiles);
                    await ShowExportMessage("Export complete", msg);
                    break;
                case Models.VideoExporter.ExportOutcome.NothingToRender:
                    await ShowExportMessage("Nothing to export", result.Message);
                    break;
                default:
                    await ShowExportMessage("Export failed", result.Message);
                    break;
            }
        }

        private async Task ShowExportMessage(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }





        private void ResetClip_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedClip != null)
            {
                ViewModel.SelectedClip.Reset();
                _playbackEngine?.RefreshEditView();
            }
        }


        private void PulseTimer_Tick(object? sender, object e)
        {
            _pulsePhase += 0.15;
            if (ModeBadgeButton != null)
            {
                // Smooth sine wave oscillation between opacity 0.55 and 1.0
                ModeBadgeButton.Opacity = 0.775 + 0.225 * Math.Sin(_pulsePhase);
            }
        }

        private void ModeBadge_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.IsEditMode)
            {
                ExitEditMode();
            }
        }

        private void PlaybarSplit_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedClip != null)
            {
                SplitClip(ViewModel.SelectedClip);
            }
        }

        private void ExitToArrange_Click(object? sender, RoutedEventArgs e) => ExitEditMode();

        // Double-tap the image (Arrange) to edit the clip under the cursor: an overlay PiP, or the
        // Track 1 clip at the playhead if the tap wasn't on a PiP. Routes through SelectClip so
        // there's one entry path (selection + enter-edit) shared with the timeline.
        private void PlayerControl_EditRequested(object? sender, int slot)
        {
            if (ViewModel.IsPlaying) return;
            if (slot >= 0)
            {
                var clip = _playbackEngine?.GetActiveOverlay(slot);
                if (clip != null) SelectClip(clip);
            }
            else if (ViewModel.TimelineNodes.Count > 0)
            {
                int idx = ViewModel.GetTimelineIndexForStoryTime(ViewModel.CurrentStoryTime);
                if (idx >= 0 && idx < ViewModel.TimelineNodes.Count)
                    SelectClip(ViewModel.Tracks[0].Clips[idx]);
            }
            // Selecting no longer enters Edit, so this double-tap has to ask for it explicitly.
            BeginEditSelected();
        }

        private void ExitEditMode()
        {
            if (!ViewModel.IsEditMode) return;

            // Settle the edited clip's track: a trim or speed change during the edit can have
            // altered its length, which reflows a gapless track and can collide on a free one.
            ViewModel.TrackOf(ViewModel.SelectedClip)?.Normalize();

            // The selection deliberately SURVIVES leaving Edit: you stay on the clip you were just
            // working on, with its properties still in the inspector. This used to clear the
            // selection, because selecting re-entered Edit and would have trapped you in a loop.
            _playbackEngine?.ExitToArrange();
            // An edit session (trim/speed/framing changes) collapses into one undo step here.
            ViewModel.RecordIfChanged();
            BuildTimelineBar();
        }

        private void EscapeAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            // A drag in flight takes priority: Esc abandons it and leaves the project untouched.
            if (_timelinePressed || _dragClip != null)
            {
                CancelDrag();
                args.Handled = true;
                return;
            }
            if (ViewModel.IsEditMode) { ExitEditMode(); args.Handled = true; }
        }

        // Enter opens the selected clip for framing — the keyboard route into Edit.
        private void EnterAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                              Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused() || ViewModel.IsEditMode || !ViewModel.HasSelection) return;
            args.Handled = true;
            BeginEditSelected();
        }

        // Space = play/pause. Ignored while typing so it doesn't hijack text entry.
        private void PlayPauseAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                                  Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            PlayPause_Click(this, null);
        }

        // Delete = remove the selected clip (never while typing or during playback). If the clip is
        // being edited, drop back to Arrange first so we don't linger in Edit on a deleted clip.
        private void DeleteAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused() || ViewModel.IsPlaying || ViewModel.SelectedClip == null) return;
            args.Handled = true;
            var clip = ViewModel.SelectedClip;
            if (ViewModel.IsEditMode) ExitEditMode();
            RemoveClip(clip);
        }

        private void LeftAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            StepFrame(-1);
        }

        private void RightAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                              Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            StepFrame(1);
        }

        private void SplitAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                              Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused() || !ViewModel.HasSelection) return;
            args.Handled = true;
            PlaybarSplit_Click(this, null);
        }

        private void SnapAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            ViewModel.IsSnappingEnabled = !ViewModel.IsSnappingEnabled;
            if (MagnetButton != null) MagnetButton.IsChecked = ViewModel.IsSnappingEnabled;
        }

        private void HomeAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            _playbackEngine?.SeekCompositeToStoryTime(TimeSpan.Zero);
        }

        private void EndAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                            Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            _playbackEngine?.SeekCompositeToStoryTime(ViewModel.TotalStoryDuration);
        }

        private void ZoomInAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            SetTimelineZoom(_timelineZoomFactor * ZoomStep);
        }

        private void ZoomOutAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                                Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            SetTimelineZoom(_timelineZoomFactor / ZoomStep);
        }

        private void FitTimelineAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                                    Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            SetTimelineZoom(1.0);
        }

        // A NumberBox hosts an inner TextBox, so a focused TextBox means the user is typing —
        // in which case Space/Delete/Ctrl+Z must reach the field, not trigger a shortcut.
        private bool IsTextInputFocused()
            => FocusManager.GetFocusedElement(this.XamlRoot) is TextBox;

        private void UndoAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return; // let Ctrl+Z undo text in a field
            args.Handled = true;
            ApplyHistory(ViewModel.Undo);
        }

        private void RedoAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                             Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            ApplyHistory(ViewModel.Redo);
        }

        private void Undo_Click(object? sender, RoutedEventArgs e) => ApplyHistory(ViewModel.Undo);
        private void Redo_Click(object? sender, RoutedEventArgs e) => ApplyHistory(ViewModel.Redo);

        // Undo/redo swap the whole clip collection, so any engine references to the old clips (edit
        // target, playing op) go stale. Settle the engine into a clean Arrange first, apply the
        // history step, then rebuild the timeline and composite from the restored state.
        private void ApplyHistory(Action historyOp)
        {
            if (ViewModel.IsPlaying) _playbackEngine?.StopPlayback();
            if (ViewModel.IsEditMode) _playbackEngine?.ExitToArrange();
            historyOp();
            BuildTimelineBar();
            _playbackEngine?.RefreshComposite();
        }





        private void OverlaySection_DragOver(object? sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;

            double y = e.GetPosition(TimelineBar).Y;
            int track = TrackIndexAtY(y);
            bool locked = track >= 0 && track < ViewModel.Tracks.Count && ViewModel.Tracks[track].IsLocked;

            e.AcceptedOperation = locked
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.None
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = locked
                ? TrackNameAt(y) + " is locked"
                : "Add to " + TrackNameAt(y);

            // Light up the destination lane. The caption alone told you where the drop would land
            // only if you happened to read it; the lane itself is where you are looking.
            ShowDropHighlight(locked ? -1 : track);
            e.Handled = true;
        }

        private void OverlaySection_DragLeave(object? sender, DragEventArgs e) => ShowDropHighlight(-1);

        private Microsoft.UI.Xaml.Shapes.Rectangle _dropHighlight;

        private void ShowDropHighlight(int trackIndex)
        {
            if (TimelineBar == null) return;

            if (trackIndex < 0 || trackIndex >= TrackCount)
            {
                if (_dropHighlight != null)
                {
                    TimelineBar.Children.Remove(_dropHighlight);
                    _dropHighlight = null;
                }
                return;
            }

            var color = TrackPalette.For(trackIndex);
            if (_dropHighlight == null)
            {
                _dropHighlight = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Height = RowPitch, RadiusX = 3, RadiusY = 3,
                    IsHitTestVisible = false, StrokeThickness = 1
                };
                TimelineBar.Children.Add(_dropHighlight);
            }
            else if (!TimelineBar.Children.Contains(_dropHighlight))
            {
                TimelineBar.Children.Add(_dropHighlight);
            }

            _dropHighlight.Width = Math.Max(0, TimelineBar.Width);
            _dropHighlight.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(TrackPalette.At(color, 0x44));
            _dropHighlight.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            Canvas.SetLeft(_dropHighlight, 0);
            Canvas.SetTop(_dropHighlight, RowYForTrack(trackIndex) - 1);
            Canvas.SetZIndex(_dropHighlight, 50);
        }

        // Human-readable name of the track a given y falls in, for the drag caption.
        private string TrackNameAt(double y)
        {
            return "Track " + (TrackIndexAtY(y) + 1);
        }

        // Drop a video/image onto the timeline strip to add it. Which row you drop on decides the
        // track (Track 1 row = spine, lower rows = that overlay track); the drop x sets the start
        // time (falls back to the playhead if the scale isn't ready).
        private async void OverlaySection_Drop(object? sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var drop = e.GetPosition(TimelineBar);
                TimeSpan startTime = _timelinePxPerSec > 0
                    ? TimeSpan.FromSeconds(Math.Max(0, drop.X / _timelinePxPerSec))
                    : ViewModel.CurrentStoryTime;

                // The lane you drop on decides the destination. A drop above or below the lanes
                // resolves to the nearest one (TrackAtY clamps) rather than silently defaulting
                // to the spine, which is what the old ruler-is-Track-1 rule did.
                int trackIndex = Math.Clamp(TrackIndexAtY(drop.Y), 0, ViewModel.Tracks.Count - 1);
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new System.Collections.Generic.List<string>();
                foreach (var item in items)
                    if (item is Windows.Storage.StorageFile file && IsSupportedMedia(file.FileType))
                        paths.Add(item.Path);

                ShowDropHighlight(-1);
                if (paths.Count == 0) return;
                if (ViewModel.Tracks[trackIndex].IsLocked) return;
                await ViewModel.AddClipsToTrackAsync(paths, trackIndex, startTime);
                SelectNewestClipOn(trackIndex);
            }
        }
    }
}
