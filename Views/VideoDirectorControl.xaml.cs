using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoDirector.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private double _preRecordSpeed = 1.0;

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
        private bool _dragIsSpine;
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
            if (!ViewModel.IsRecordingMotion && !_isPointerOverPill)
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

            ViewModel.Tracks.CollectionChanged += (s, ev) => { HookOverlayTrackClips(); BuildTimelineBar(); _playbackEngine?.RefreshComposite(); };
            HookOverlayTrackClips();
            BuildTimelineBar();
        }

        // Each track owns its own clip collection, so the timeline has to watch them all.
        private readonly System.Collections.Generic.HashSet<TimelineTrack> _hookedTracks = new();
        private void HookOverlayTrackClips()
        {
            foreach (var track in ViewModel.Tracks)
                if (_hookedTracks.Add(track))
                    track.Clips.CollectionChanged += (s, ev) => { BuildTimelineBar(); _playbackEngine?.RefreshComposite(); };
        }

        // The track that owns a given clip.
        private TimelineTrack TrackOf(CinematicOperation clip)
        {
            foreach (var track in ViewModel.Tracks)
                if (track.Clips.Contains(clip)) return track;
            return null;
        }

        private void TimelineBar_SizeChanged(object? sender, SizeChangedEventArgs e) => BuildTimelineBar();

        // Timeline layout: a scrub ruler on top, then track rows — all on
        // one shared px=seconds scale (§7E). Scrub on the ruler; drag clips in their rows.
        private const double RulerH = 14, RowSpineY = 16, RowOvY = 34, BlockH = 16, RowPitch = 18;

        // Bar height grows with the number of tracks. 
        private double TimelineBarHeight =>
            RowSpineY + (Math.Max(1, ViewModel.Tracks.Count)) * RowPitch + BlockH + 6;

        private void BuildTimelineBar()
        {
            if (TimelineBar == null) return;
            TimelineBar.Children.Clear();
            _clipBlockElements.Clear();
            _playhead = null; _playheadKnob = null;

            TimelineBar.Height = TimelineBarHeight;   // grows with the upper-track count
            double viewportW = TimelineScroll.ActualWidth;
            if (viewportW <= 0) viewportW = TimelineBar.ActualWidth;
            double w = viewportW * _timelineZoomFactor;
            if (w > 0) TimelineBar.Width = w;
            double h = TimelineBarHeight;
            double total = Math.Max(30.0, ViewModel.TotalStoryDuration.TotalSeconds);

            // Add a 20% pad at the end for comfortable dragging
            total *= 1.2;

            BuildTrackLabels(); // Build track headers regardless of whether the timeline is empty

            if (w <= 0 || total <= 0) { _timelinePxPerSec = 0; return; }
            _timelinePxPerSec = w / total;

            // Per-lane bands: a faint tint of the track's own colour distinguishes the lanes by
            // colour rather than by height (space is at a premium), and ties each lane to its
            // identity colour. Drawn first so blocks/gridlines paint over them.
            if (ViewModel.Tracks.Count > 0)
                DrawRowBand(RowSpineY, w, TrackPalette.Spine);
            for (int ti = 1; ti < ViewModel.Tracks.Count; ti++)
                DrawRowBand(RowOvY + (ti - 1) * RowPitch, w, TrackPalette.Overlay(ti - 1));

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

            var spineColor = TrackPalette.Spine;
            var transColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B);
            bool spineGhost = _timelineMovingClip && _dragIsSpine && _dragClip != null;

            if (ViewModel.Tracks.Count > 0)
            {
                var mainTrack = ViewModel.Tracks[0];
                if (!spineGhost)
                {
                    for (int i = 0; i < mainTrack.Clips.Count; i++)
                    {
                        var clip = mainTrack.Clips[i];
                        double x = clip.StartTime.TotalSeconds * _timelinePxPerSec;
                        double cw = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                        AddTimelineBlock(x, RowSpineY, cw, BlockH, spineColor, clip, mainTrack.ShowAudioWaveforms);
                        double tw = clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
                        if (tw > 0.5)
                            AddTimelineBlock(x + cw, RowSpineY, tw, BlockH, transColor, null, false);
                    }
                }
                else
                {
                    // Spine is order-based in gapless mode, so there is no continuous position to write. Instead the
                    // other clips reflow with a gap at the insertion point, and the grabbed clip is
                    // drawn as a free ghost under the cursor. The order only changes on release.
                    double dragW = _dragClip.OpDuration.TotalSeconds * _timelinePxPerSec;
                    double x = 0;
                    int drawn = 0;
                    foreach (var clip in mainTrack.Clips)
                    {
                        if (clip == _dragClip) continue;
                        if (drawn == _dragInsertIndex) x += dragW;   // open the drop gap
                        
                        double actualStartX = mainTrack.IsGapless ? x : (clip.StartTime.TotalSeconds * _timelinePxPerSec);
                        
                        double cw = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                        AddTimelineBlock(actualStartX, RowSpineY, cw, BlockH, spineColor, clip, mainTrack.ShowAudioWaveforms);
                        double tw = clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
                        if (tw > 0.5) AddTimelineBlock(actualStartX + cw, RowSpineY, tw, BlockH, transColor, null, false);
                        x += cw + tw;
                        drawn++;
                    }

                    double ghostX = _dragCursorX - _dragGrabOffsetSec * _timelinePxPerSec;
                    AddTimelineBlock(ghostX, RowSpineY, dragW, BlockH,
                        Microsoft.UI.ColorHelper.FromArgb(0xCC, 0x93, 0xC5, 0xFD), _dragClip); // ghost
                }
            }

            // One row per upper track (§7B) — same loop for 1 track or 3, each in its own colour.
            for (int ti = 1; ti < ViewModel.Tracks.Count; ti++)
            {
                double rowY = RowOvY + (ti - 1) * RowPitch;
                var trackColor = TrackPalette.Overlay(ti - 1);
                foreach (var ov in ViewModel.Tracks[ti].Clips)
                {
                    double x = ov.StartTimeSeconds * _timelinePxPerSec;
                    double ow = ov.OpDuration.TotalSeconds * _timelinePxPerSec;
                    AddTimelineBlock(x, rowY, ow, BlockH, trackColor, ov, ViewModel.Tracks[ti].ShowAudioWaveforms);
                }
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

        // "Track 1".."Track 4" in the left gutter, vertically aligned to each row.
        private void BuildTrackLabels()
        {
            if (TimelineLabels == null) return;
            TimelineLabels.Children.Clear();
            TimelineLabels.Height = TimelineBarHeight;

            if (ViewModel.Tracks.Count > 0)
                AddTrackLabel("T1", RowSpineY, TrackPalette.Spine, 0);        // 0 = spine
            
            for (int ti = 1; ti < ViewModel.Tracks.Count; ti++)
                AddTrackLabel("T" + (ti + 1), RowOvY + (ti - 1) * RowPitch, TrackPalette.Overlay(ti - 1), ti);
        }

        // Each track label is a button: click it to load a video into that track via a file picker
        // (trackIndex -1 = spine/Track 1, 0..2 = overlay tracks). Drag & drop still works too.
        private void AddTrackLabel(string text, double y, Windows.UI.Color color, int trackIndex)
        {
            var track = ViewModel.Tracks[trackIndex];
            // A colour cap ties the label to the track's identity colour (same as its blocks/PiP).
            var cap = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 4, Height = BlockH - 2, RadiusX = 2, RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
            };
            var label = new TextBlock
            {
                Text = text, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1) // Nudge text slightly to center visually
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(cap); content.Children.Add(label);

            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                MinHeight = 0,
                MinWidth = 0,
                Height = 18 // Fits precisely in RowPitch (18)
            };
            ToolTipService.SetToolTip(btn, "Load a video into " + text + " (or drag & drop)");
            btn.Click += (s, e) => LoadIntoTrack(trackIndex);

            var settingsMenu = new MenuFlyout();

            var snapItem = new ToggleMenuFlyoutItem { Text = "Snapping", IsChecked = track.IsSnappingEnabled };
            snapItem.Click += (s, e) => { track.IsSnappingEnabled = snapItem.IsChecked; };

            var magItem = new ToggleMenuFlyoutItem { Text = "Magnetic Timeline", IsChecked = track.IsGapless };
            magItem.Click += (s, e) => { track.IsGapless = magItem.IsChecked; };

            var waveItem = new ToggleMenuFlyoutItem { Text = "Show Waveforms", IsChecked = track.ShowAudioWaveforms };
            waveItem.Click += (s, e) => { track.ShowAudioWaveforms = waveItem.IsChecked; BuildTimelineBar(); };

            settingsMenu.Items.Add(snapItem);
            settingsMenu.Items.Add(magItem);
            settingsMenu.Items.Add(waveItem);

            var settingsBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE713", FontSize = 10 },
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                MinHeight = 0,
                MinWidth = 0,
                Height = 18,
                Flyout = settingsMenu
            };
            ToolTipService.SetToolTip(settingsBtn, text + " Settings");

            var wrapper = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            wrapper.Children.Add(btn);
            wrapper.Children.Add(settingsBtn);

            Canvas.SetLeft(wrapper, 2);
            Canvas.SetTop(wrapper, y - 1);
            TimelineLabels.Children.Add(wrapper);
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

            if (trackIndex == 0)
            {
                var paths = new List<string>();
                foreach (var f in files) paths.Add(f.Path);
                await ViewModel.AddFilesAsync(paths);
                EditNewestSpineClip();
            }
            else
            {
                foreach (var f in files)
                    await ViewModel.AddOverlayAsync(f.Path, TimeSpan.Zero, trackIndex);
                if (trackIndex < ViewModel.Tracks.Count)
                {
                    var track = ViewModel.Tracks[trackIndex];
                    if (track.Clips.Count > 0)
                    {
                        if (ViewModel.IsPlaying) _playbackEngine?.StopPlayback();
                        SelectClip(track.Clips[^1], isSpine: false);
                    }
                }
            }
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
            if (ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Contains(clip))   // spine: the clip at the playhead
                return ViewModel.Tracks[0].Clips.IndexOf(clip) == ViewModel.GetTimelineIndexForStoryTime(t);
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

        private void AddTimelineBlock(double x, double y, double width, double height, Windows.UI.Color color, CinematicOperation clip = null, bool showWaveforms = false)
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

            if (showWaveforms && width > 10 && height > 10)
            {
                var wf = new Microsoft.UI.Xaml.Shapes.Polyline
                {
                    Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(110, 255, 255, 255)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                    Opacity = dim
                };
                int pointsCount = (int)Math.Max(5, Math.Min(width / 3, 150));
                double stepX = width / (pointsCount - 1);
                double midY = height * 0.75;
                for (int i = 0; i < pointsCount; i++)
                {
                    double amp = (Math.Sin(i * 1.3 + (clip?.GetHashCode() ?? 0)) * Math.Cos(i * 0.7) * 0.45) * (height * 0.22);
                    wf.Points.Add(new Windows.Foundation.Point(i * stepX, midY + amp));
                }
                Canvas.SetLeft(wf, x);
                Canvas.SetTop(wf, y);
                TimelineBar.Children.Add(wf);

                if (clip != null && _clipBlockElements.TryGetValue(clip, out var listWf)) listWf.Add(wf);
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

            // While editing a clip, the timeline is a "back to Arrange" target: a click here exits
            // Edit and does nothing else (click again to scrub/select once back in Arrange).
            if (ViewModel.IsEditMode) { ExitEditMode(); return; }

            var p = point.Position;
            _timelinePressPoint = p;
            _timelinePressed = true;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            TimelineBar.CapturePointer(e.Pointer);

            if (p.Y < RulerH) { _timelineScrubbing = true; ScrubToX(p.X); return; }

            var hit = HitClip(p);
            if (hit.clip != null)
            {
                _dragClip = hit.clip;
                _dragIsSpine = hit.isSpine;
                _dragGrabOffsetSec = (p.X / _timelinePxPerSec) - hit.startSec;
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

            var currentTrk = TrackOf(_dragClip);
            bool isGapless = currentTrk != null && currentTrk.IsGapless;

            if (isGapless)
            {
                // Live transfer between Track 1 (Spine) and Track 2/3/4 (Overlays)
                if (_dragIsSpine && p.Y >= RowOvY && ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Count > 1)
                {
                    ViewModel.Tracks[0].Clips.Remove(_dragClip);
                    int targetIndex = Math.Clamp((int)((p.Y - RowOvY) / RowPitch), 0, ViewModel.Tracks.Count - 2) + 1;
                    var targetTrk = ViewModel.Tracks[targetIndex];
                    double newStart = Math.Max(0, (p.X / _timelinePxPerSec) - _dragGrabOffsetSec);
                    _dragClip.StartTime = TimeSpan.FromSeconds(newStart);
                    targetTrk.Clips.Add(_dragClip);
                    targetTrk.ResolveOverlaps();
                    _dragIsSpine = false;
                }
                else if (!_dragIsSpine && p.Y < RowOvY && ViewModel.Tracks.Count > 0)
                {
                    var cTrk = TrackOf(_dragClip);
                    cTrk?.Clips.Remove(_dragClip);
                    int insertIdx = ComputeSpineInsertIndex(p.X);
                    insertIdx = Math.Clamp(insertIdx, 0, ViewModel.Tracks[0].Clips.Count);
                    ViewModel.Tracks[0].Clips.Insert(insertIdx, _dragClip);
                    _dragIsSpine = true;
                }

                if (_dragIsSpine)
                {
                    // Ghost follows the cursor; the order itself is committed on release.
                    _dragCursorX = p.X;
                    int newIndex = ComputeSpineInsertIndex(p.X);
                    if (newIndex != _dragInsertIndex)
                    {
                        _dragInsertIndex = newIndex;
                        BuildTimelineBar();
                    }
                    else if (_clipBlockElements.TryGetValue(_dragClip, out var ghostElements))
                    {
                        double ghostX = _dragCursorX - _dragGrabOffsetSec * _timelinePxPerSec;
                        foreach (var el in ghostElements)
                        {
                            Canvas.SetLeft(el, el is StackPanel ? ghostX + 6 : ghostX);
                        }
                    }
                }
                else MoveOverlayTo(p);   // x = time, y = which track
            }
            else
            {
                MoveOverlayTo(p);
            }
        }

        // Insertion index = how many OTHER spine clips have their centre left of the cursor,
        // measured in the layout with the dragged clip removed. Monotonic, so it can't oscillate.
        private int ComputeSpineInsertIndex(double cursorX)
        {
            int insert = 0;
            double x = 0;
            if (ViewModel.Tracks.Count == 0) return 0;
            var mainTrack = ViewModel.Tracks[0];
            foreach (var clip in mainTrack.Clips)
            {
                if (clip == _dragClip) continue;
                double w = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                if (mainTrack.IsGapless)
                {
                    if (x + w / 2 < cursorX) insert++;
                    x += w + clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
                }
                else
                {
                    if (clip.StartTime.TotalSeconds * _timelinePxPerSec + w / 2 < cursorX) insert++;
                }
            }
            return insert;
        }

        private void TimelineBar_PointerReleased(object? sender, PointerRoutedEventArgs e)
        {
            // This fires for the RIGHT button too. If we never started a left-press, do nothing —
            // in particular do NOT rebuild the bar: rebuilding destroys every Canvas child,
            // including the element the right-tap gesture started on, which kills the pending
            // context gesture before the flyout can open.
            if (!_timelinePressed) return;

            TimelineBar.ReleasePointerCapture(e.Pointer);
            bool wasMoving = _timelineMovingClip;

            if (_dragClip != null)
            {
                if (!wasMoving) SelectClip(_dragClip, _dragIsSpine); // a tap selects
                else
                {
                    var track = TrackOf(_dragClip);
                    bool isGapless = track != null && track.IsGapless;
                    
                    if (isGapless && _dragIsSpine)
                    {
                        if (ViewModel.Tracks.Count > 0)
                        {
                            var mainTrack = ViewModel.Tracks[0];
                            // Commit the reorder exactly once, at the ghost's drop position.
                            int cur = mainTrack.Clips.IndexOf(_dragClip);
                            int target = Math.Clamp(_dragInsertIndex, 0, mainTrack.Clips.Count - 1);
                            if (cur >= 0 && target != cur) mainTrack.Clips.Move(cur, target);
                            mainTrack.ResolveOverlaps();
                        }
                    }
                    else
                    {
                        track?.ResolveOverlaps();
                        _playbackEngine?.RefreshComposite();
                    }
                }
            }
            _timelinePressed = false;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            if (wasMoving)
            {
                BuildTimelineBar();          // clear the drag ghost
                ViewModel.RecordIfChanged(); // record the move/reorder as one undo step
            }
        }

        // Duplicate / Remove for the block under the cursor. The platform opens the ContextFlyout;
        // we just resolve which clip it applies to from the last pointer position.
        private CinematicOperation _contextClip;
        private bool _contextIsSpine;

        private void TimelineContextMenu_Opening(object? sender, object e)
        {
            // Keep this trivial: just record what was under the cursor. It previously also called
            // SelectClip (which changes mode / starts async work) — an exception in Opening aborts
            // the flyout, which is a candidate for "right-click does nothing".
            var hit = HitClip(_lastHoverPoint);
            _contextClip = hit.clip;
            _contextIsSpine = hit.isSpine;
        }

        private void TimelineSplit_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) SplitClip(_contextClip, _contextIsSpine);
        }

        private void TimelineSnapshot_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) SnapshotClip(_contextClip, _contextIsSpine);
        }

        private void TimelineDuplicate_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) DuplicateClip(_contextClip, _contextIsSpine);
        }

        private void TimelineRemove_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null) RemoveClip(_contextClip, _contextIsSpine);
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
            StartMark = new SpatialMark(clip.StartMark.Scale, clip.StartMark.X, clip.StartMark.Y),
            EndMark = new SpatialMark(clip.EndMark.Scale, clip.EndMark.X, clip.EndMark.Y),
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
        private void InsertAfter(CinematicOperation after, bool isSpine, CinematicOperation toInsert, double overlayStartSec)
        {
            var track = TrackOf(after);
            int i = track?.Clips.IndexOf(after) ?? -1;
            if (i < 0) return;
            toInsert.StartTime = TimeSpan.FromSeconds(
                track.ClampToFreeSlot(null, overlayStartSec, toInsert.OpDuration.TotalSeconds));
            track.Clips.Insert(i + 1, toInsert);
            
            ViewModel.RecordIfChanged();
            BuildTimelineBar();
            _playbackEngine?.RefreshComposite();
        }

        private void DuplicateClip(CinematicOperation clip, bool isSpine)
        {
            var copy = CloneClip(clip);
            InsertAfter(clip, isSpine, copy, clip.StartTime.TotalSeconds + clip.OpDuration.TotalSeconds);
        }

        // Cut the clip in two at the playhead (or its midpoint if the playhead isn't inside it).
        private void SplitClip(CinematicOperation clip, bool isSpine)
        {
            double startS = clip.VideoStartTime.TotalSeconds;
            double endS = clip.VideoEndTime.TotalSeconds;
            double window = endS - startS;
            if (window < 0.4) return; // too short to split into two usable halves

            double cutS = startS + SplitFraction(clip, isSpine) * window;

            var second = CloneClip(clip);
            clip.VideoEndTime = TimeSpan.FromSeconds(cutS);      // first half ends at the cut
            second.VideoStartTime = TimeSpan.FromSeconds(cutS);  // second half starts at the cut
            second.VideoEndTime = TimeSpan.FromSeconds(endS);

            // Second half sits immediately after the (now shorter) first half.
            // For gapless tracks, the start time parameter is ignored by ClampToFreeSlot anyway.
            InsertAfter(clip, isSpine, second, clip.StartTime.TotalSeconds + clip.OpDuration.TotalSeconds);
        }

        // Freeze the current frame as a 10s still with a slow Ken Burns push-in — a one-click
        // alternative to duplicate-then-set-speed-0-and-marks.
        private void SnapshotClip(CinematicOperation clip, bool isSpine)
        {
            double startS = clip.VideoStartTime.TotalSeconds;
            double window = clip.VideoEndTime.TotalSeconds - startS;
            double frozen = startS + SplitFraction(clip, isSpine) * window;
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
                StartMark = new SpatialMark(1.0f, 0, 0),
                EndMark = new SpatialMark(1.25f, 0, 0), // default push-in
                Opacity = clip.Opacity,
                PlacementWidth = clip.PlacementWidth,
                PlacementHeight = clip.PlacementHeight,
                PlacementCenterX = clip.PlacementCenterX,
                PlacementCenterY = clip.PlacementCenterY,
                Thumbnail = clip.Thumbnail
            };

            InsertAfter(clip, isSpine, snap, clip.StartTime.TotalSeconds + clip.OpDuration.TotalSeconds);
        }

        // Where to cut/freeze within a clip's source window, as a 0..1 fraction: the playhead if it
        // falls inside this clip, otherwise the midpoint. Kept off the very edges so no sliver.
        private double SplitFraction(CinematicOperation clip, bool isSpine)
        {
            double op = clip.OpDuration.TotalSeconds;
            if (op <= 0) return 0.5;
            double clipStartStory = isSpine
                ? ViewModel.GetSpineClipStart(ViewModel.Tracks[0].Clips.IndexOf(clip)).TotalSeconds
                : clip.StartTime.TotalSeconds;
            double into = ViewModel.CurrentStoryTime.TotalSeconds - clipStartStory;
            double f = (into > 0 && into < op) ? into / op : 0.5;
            return Math.Clamp(f, 0.05, 0.95);
        }

        private void RemoveClip(CinematicOperation clip, bool isSpine)
        {
            var track = TrackOf(clip);
            track?.Clips.Remove(clip);
            if (ViewModel.SelectedClip == clip) ViewModel.SelectedClip = null;
            ViewModel.RecordIfChanged();
        }

        private List<double> GetTimelineSnapPoints(CinematicOperation? ignoreClip, bool includePlayhead)
        {
            var points = new List<double> { 0.0 };
            if (includePlayhead && ViewModel != null)
                points.Add(ViewModel.CurrentStoryTime.TotalSeconds);
            
            if (ViewModel?.Tracks != null)
            {
                foreach (var trk in ViewModel.Tracks)
                {
                    if (!trk.IsSnappingEnabled) continue;
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
            if (ViewModel == null || _timelinePxPerSec <= 0 || !ViewModel.Tracks.Any(t => t.IsSnappingEnabled)) return sec;
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

        private double ApplyClipSnapping(double desiredStartSec, double durSec, CinematicOperation ignoreClip, Models.TimelineTrack targetTrack)
        {
            if (ViewModel == null || targetTrack == null || !targetTrack.IsSnappingEnabled || _timelinePxPerSec <= 0) return desiredStartSec;
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

        // Which clip (and its start-second) sits under a point in the clip rows, if any.
        private (CinematicOperation clip, bool isSpine, double startSec) HitClip(Windows.Foundation.Point p)
        {
            if (_timelinePxPerSec <= 0) return (null, false, 0);
            var t = TimeSpan.FromSeconds(Math.Max(0, p.X / _timelinePxPerSec));

            if (p.Y >= RowSpineY && p.Y < RowSpineY + BlockH && ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Count > 0)
            {
                int idx = ViewModel.GetTimelineIndexForStoryTime(t);
                if (idx >= 0 && idx < ViewModel.Tracks[0].Clips.Count)
                    return (ViewModel.Tracks[0].Clips[idx], true, ViewModel.GetSpineClipStart(idx).TotalSeconds);
            }
            else if (p.Y >= RowOvY)
            {
                int ti = (int)((p.Y - RowOvY) / RowPitch) + 1;   // which upper-track row
                if (ti > 0 && ti < ViewModel.Tracks.Count)
                {
                    foreach (var ov in ViewModel.Tracks[ti].Clips)
                        if (t >= ov.StartTime && t < ov.StartTime + ov.OpDuration)
                            return (ov, false, ov.StartTimeSeconds);
                }
            }
            return (null, false, 0);
        }

        private void SelectClip(CinematicOperation clip, bool isSpine)
        {
            if (isSpine)
            {
                ViewModel.SelectedTimelineNode = clip;
                int idx = ViewModel.Tracks.Count > 0 ? ViewModel.Tracks[0].Clips.IndexOf(clip) : -1;
                if (ViewModel.IsPlaying)
                {
                    if (_playbackEngine?.CurrentPlayingOperation != clip && idx >= 0)
                        _ = _playbackEngine?.StartPlaybackAsync(idx);
                    return;
                }
            }
            else ViewModel.SelectedOverlay = clip;

            // One entry point for both tracks — selecting a clip (while not playing) edits it.
            if (!ViewModel.IsPlaying)
            {
                ViewModel.CurrentStoryTime = clip.StartTime;
                _playbackEngine?.BeginEdit(ViewModel.SelectedClip, ViewModel.CurrentEditTarget);
            }
        }

        // Overlay drag: horizontally = reposition in time, vertically = move to another track.
        private void MoveOverlayTo(Windows.Foundation.Point p)
        {
            if (_dragClip == null || _timelinePxPerSec <= 0) return;

            // Vertical: which track row is the cursor over?
            int targetIndex = p.Y >= RowOvY ? (int)((p.Y - RowOvY) / RowPitch) + 1 : 0;
            targetIndex = Math.Clamp(targetIndex, 0, ViewModel.Tracks.Count - 1);
            var target = ViewModel.Tracks[targetIndex];
            var current = TrackOf(_dragClip);
            bool trackChanged = current != null && !ReferenceEquals(current, target);
            if (trackChanged)
            {
                current.Clips.Remove(_dragClip);
                target.Clips.Add(_dragClip);
            }

            // Horizontal: set the start time
            // (tracks are strict — an overlap would silently hide one clip at playback).
            double dur = _dragClip.OpDuration.TotalSeconds;
            double newStart = (p.X / _timelinePxPerSec) - _dragGrabOffsetSec;
            newStart = Math.Max(0, newStart);
            newStart = ApplyClipSnapping(newStart, dur, _dragClip, target);
            _dragClip.StartTime = TimeSpan.FromSeconds(newStart);

            if (trackChanged)
            {
                target.ResolveOverlaps();
                BuildTimelineBar();
                _playbackEngine?.RefreshComposite();
            }
            else if (_clipBlockElements.TryGetValue(_dragClip, out var elements))
            {
                double newX = newStart * _timelinePxPerSec;
                double rowY = targetIndex == 0 ? RowSpineY : RowOvY + (targetIndex - 1) * RowPitch;
                foreach (var el in elements)
                {
                    Canvas.SetLeft(el, el is StackPanel ? newX + 6 : newX);
                    Canvas.SetTop(el, rowY);
                }
            }
            // History is recorded once on drop (PointerReleased), not per move-tick.
        }


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
            _playbackEngine?.UpdateWysiwygOverlay();
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



        private void TimelineScroll_PointerWheelChanged(object? sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(TimelineScroll);
            if (p.Properties.MouseWheelDelta > 0)
            {
                _timelineZoomFactor = Math.Min(16.0, _timelineZoomFactor * 1.3333333);
            }
            else
            {
                _timelineZoomFactor = Math.Max(1.0, _timelineZoomFactor / 1.3333333);
                if (_timelineZoomFactor <= 1.01) _timelineZoomFactor = 1.0;
            }
            BuildTimelineBar();
            e.Handled = true;
        }

        private void ZoomInTimeline_Click(object? sender, RoutedEventArgs e)
        {
            _timelineZoomFactor = Math.Min(16.0, _timelineZoomFactor * 1.3333333);
            BuildTimelineBar();
        }

        private void ZoomOutTimeline_Click(object? sender, RoutedEventArgs e)
        {
            _timelineZoomFactor = Math.Max(1.0, _timelineZoomFactor / 1.3333333);
            if (_timelineZoomFactor <= 1.01) _timelineZoomFactor = 1.0;
            BuildTimelineBar();
        }

        private void SetStart_Click(object? sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.StartMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
                ViewModel.CurrentOperationTimeSeconds = op.VideoStartTime.TotalSeconds;
            }
        }

        private void SetMid_Click(object? sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.MidMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
                ViewModel.CurrentOperationTimeSeconds = op.VideoStartTime.TotalSeconds + (op.OpDuration.TotalSeconds / 2.0);
            }
        }

        private void SetEnd_Click(object? sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.EndMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
                ViewModel.CurrentOperationTimeSeconds = op.VideoEndTime.TotalSeconds;
            }
        }

        // Right-click the Mid button to clear it (back to a two-point Start -> End motion).
        private void ClearMid_RightTapped(object? sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            if (op != null)
            {
                op.MidMark = null;
                _playbackEngine?.UpdateWysiwygOverlay();
            }
            e.Handled = true;
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
                _playbackEngine?.UpdateWysiwygOverlay();

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
            else if (e.PropertyName == nameof(DirectorViewModel.IsRecordingMotion))
            {
                if (ViewModel.IsRecordingMotion)
                {
                    var op = ViewModel.SelectedClip ?? _playbackEngine?.CurrentPlayingOperation;
                    if (op != null)
                    {
                        _preRecordSpeed = ViewModel.PlaybackSpeed;
                        ViewModel.PlaybackSpeed = 0.5;
                        _playbackEngine?.StartRecordingMotion(op);
                    }
                    else
                    {
                        ViewModel.IsRecordingMotion = false; // Cannot record without a selected or playing node
                    }
                }
                else
                {
                    var op = ViewModel.SelectedClip ?? _playbackEngine?.CurrentPlayingOperation;
                    if (op != null)
                    {
                        _playbackEngine?.StopRecordingMotion(op);
                    }
                    ViewModel.PlaybackSpeed = _preRecordSpeed;
                }
            }
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
                    if (item is Windows.Storage.StorageFile file && (file.FileType == ".mp4" || file.FileType == ".mkv" || file.FileType == ".avi" || file.FileType == ".jpg" || file.FileType == ".png"))
                    {
                        paths.Add(item.Path);
                    }
                }

                if (paths.Count > 0)
                {
                    await ViewModel.AddFilesAsync(paths);
                    EditNewestSpineClip();   // open the new clip so you can trim/frame it
                }
            }
        }

        // After adding to Track 1, drop straight into Edit on the newest clip so it can be trimmed
        // and framed immediately (adding a clip is usually the prelude to editing it).
        private void EditNewestSpineClip()
        {
            if (ViewModel.Tracks.Count == 0 || ViewModel.Tracks[0].Clips.Count == 0) return;
            var endSlot = ViewModel.Tracks[0].ClampToFreeSlot(null, ViewModel.TotalStoryDuration.TotalSeconds, 0);
            SelectClip(ViewModel.Tracks[0].Clips[^1], isSpine: true);
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

        private async void FitWindow_Click(object? sender, RoutedEventArgs e)
        {
            if (_timelineZoomFactor > 1.0)
            {
                _timelineZoomFactor = 1.0;
                BuildTimelineBar();
            }
            double targetAspect = 16.0 / 9.0;
            var mpA = (Windows.Media.Playback.MediaPlayer)null;
            var mpB = (Windows.Media.Playback.MediaPlayer)null;
            
            // Get the true video aspect ratio directly from the file container
            // This bypasses Windows Media Foundation padding (e.g. 1918x804 padded to 1920x816)
            CinematicOperation activeClip = null;
            if (ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Count > 0)
            {
                activeClip = ViewModel.Tracks[0].Clips[0]; // just grab the first one for baseline
            }
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
                var activePlayer = (Windows.Media.Playback.MediaPlayer)null;
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
                if (ViewModel.IsAutoPlayEnabled && ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Count > 0)
                {
                    _ = _playbackEngine?.StartPlaybackAsync(0);
                }
            }
        }

        private async void Clear_Click(object? sender, RoutedEventArgs e)
        {
            bool hasContent = false;
            if (ViewModel.Tracks.Count > 0)
            {
                foreach (var t in ViewModel.Tracks)
                {
                    if (t.Clips.Count > 0) { hasContent = true; break; }
                }
            }

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
            bool hasClips = false;
            if (ViewModel.Tracks.Count > 0)
            {
                foreach (var track in ViewModel.Tracks)
                    if (track.Clips.Count > 0) { hasClips = true; break; }
            }

            if (!hasClips)
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
                _playbackEngine?.UpdateWysiwygOverlay();
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
                bool isSpine = ViewModel.SelectedOverlay == null;
                SplitClip(ViewModel.SelectedClip, isSpine);
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
                if (clip != null) SelectClip(clip, isSpine: false);
            }
            else if (ViewModel.Tracks.Count > 0 && ViewModel.Tracks[0].Clips.Count > 0)
            {
                int idx = ViewModel.GetTimelineIndexForStoryTime(ViewModel.CurrentStoryTime);
                if (idx >= 0 && idx < ViewModel.Tracks[0].Clips.Count)
                    SelectClip(ViewModel.Tracks[0].Clips[idx], isSpine: true);
            }
        }

        private void ExitEditMode()
        {
            if (!ViewModel.IsEditMode) return;

            if (ViewModel.SelectedOverlay != null && ViewModel.Tracks.Count > 1)
            {
                for (int i = 1; i < ViewModel.Tracks.Count; i++)
                {
                    var track = ViewModel.Tracks[i];
                    if (track.Clips.Contains(ViewModel.SelectedOverlay))
                    {
                        track.ResolveOverlaps();
                        break;
                    }
                }
            }

            // Clear the selection so we don't immediately re-enter Edit, then return to Arrange.
            ViewModel.SelectedTimelineNode = null;
            ViewModel.SelectedOverlay = null;
            _playbackEngine?.ExitToArrange();
            // An edit session (trim/speed/framing changes) collapses into one undo step here.
            ViewModel.RecordIfChanged();
            BuildTimelineBar();
        }

        private void EscapeAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.IsEditMode) { ExitEditMode(); args.Handled = true; }
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
            bool isSpine = ViewModel.IsTrack1Selected;
            if (ViewModel.IsEditMode) ExitEditMode();
            RemoveClip(clip, isSpine);
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
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                // Name the row being hovered, so it's obvious where the drop will land.
                e.DragUIOverride.Caption = "Add to " + TrackNameAt(e.GetPosition(TimelineBar).Y);
                e.Handled = true;
            }
        }

        // Which track a given y in the timeline belongs to: the Track 1 row (and the ruler above
        // it) is the spine; the rows below are the overlay tracks.
        private string TrackNameAt(double y)
        {
            if (y < RowOvY) return "Track 1";
            int i = Math.Clamp((int)((y - RowOvY) / RowPitch), 0, Math.Max(0, ViewModel.Tracks.Count - 2));
            return "Track " + (i + 2);
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

                // The row you drop on decides the destination: the Track 1 row (and the ruler
                // above it) adds to the spine; the rows below add to that overlay track.
                bool toSpine = drop.Y < RowOvY;
                int trackIndex = toSpine ? 0 : (int)((drop.Y - RowOvY) / RowPitch) + 1;
                trackIndex = Math.Clamp(trackIndex, 0, Math.Max(0, ViewModel.Tracks.Count - 1));

                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file && (file.FileType == ".mp4" || file.FileType == ".mkv" || file.FileType == ".avi" || file.FileType == ".jpg" || file.FileType == ".png"))
                    {
                        await ViewModel.AddOverlayAsync(item.Path, startTime, trackIndex);
                    }
                }

                // Open the newest clip for editing
                var track = ViewModel.Tracks[trackIndex];
                if (track.Clips.Count > 0)
                {
                    if (ViewModel.IsPlaying) _playbackEngine?.StopPlayback();
                    SelectClip(track.Clips[^1], isSpine: trackIndex == 0);
                }
            }
        }
    }
}
