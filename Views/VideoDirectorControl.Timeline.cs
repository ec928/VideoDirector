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

// VideoDirectorControl - drawing the timeline: the ruler, the track labels, the clip blocks, and the shading that shows where a fade eats into one.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        private void TimelineBar_SizeChanged(object? sender, SizeChangedEventArgs e) => BuildTimelineBar();

        // Timeline layout: a scrub ruler on top, then track rows — all on
        // one shared px=seconds scale (§7E). Scrub on the ruler; drag clips in their rows.
        private const double RulerH = 20, RowSpineY = 22, RowOvY = 40, BlockH = 16, RowPitch = 18;

        // Bar height grows with the number of tracks. 
        private double TimelineBarHeight =>
            RowSpineY + (Math.Max(1, ViewModel.Tracks.Count)) * RowPitch + BlockH + 6;

        private void BuildTimelineBar()
        {
            if (TimelineBar == null) return;
            TimelineBar.Children.Clear();
            _clipBlockElements.Clear();
            _playhead = null; _playheadKnob = null; _playheadTime = null; _playheadBadge = null;

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
                        x += cw;
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
            _loopRegionHighlight = new Microsoft.UI.Xaml.Shapes.Rectangle { 
                Height = h, 
                IsHitTestVisible = false, 
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x40, 0xEF, 0xAA, 0x44)),
                Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
            };
            TimelineBar.Children.Add(_loopRegionHighlight);
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
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            };
            _playheadBadge = new Border
            {
                Background = red,
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(3),
                Padding = new Microsoft.UI.Xaml.Thickness(4, 1, 4, 1),
                Child = _playheadTime,
                IsHitTestVisible = false
            };
            TimelineBar.Children.Add(_playheadBadge);

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
                Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1) // Nudge text slightly to center visually
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(cap); content.Children.Add(label);

            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                MinHeight = 0,
                MinWidth = 40, Width = 40,
                Height = 18 // Fits precisely in RowPitch (18)
            };
            ToolTipService.SetToolTip(btn, "Load a video into " + text + " (or drag & drop)");
            btn.Click += (s, e) => LoadIntoTrack(trackIndex);

            var snapIcon = new FontIcon { Glyph = "\uE144", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,1), Opacity = track.IsSnappingEnabled ? 1.0 : 0.3 };
            var snapBtn = new Button
            {
                Content = snapIcon,
                Padding = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                MinHeight = 0,
                MinWidth = 0,
                Height = 18
            };
            ToolTipService.SetToolTip(snapBtn, text + " Snapping");
            snapBtn.Click += (s, e) => { track.IsSnappingEnabled = !track.IsSnappingEnabled; snapIcon.Opacity = track.IsSnappingEnabled ? 1.0 : 0.3; };

            var magIcon = new FontIcon { Glyph = "\uE71B", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,1), Opacity = track.IsGapless ? 1.0 : 0.3 };
            var magBtn = new Button
            {
                Content = magIcon,
                Padding = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                MinHeight = 0,
                MinWidth = 0,
                Height = 18
            };
            ToolTipService.SetToolTip(magBtn, text + " Magnetic Timeline");
            magBtn.Click += (s, e) => { track.IsGapless = !track.IsGapless; magIcon.Opacity = track.IsGapless ? 1.0 : 0.3; };

            var wrapper = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            wrapper.Children.Add(btn);
            wrapper.Children.Add(snapBtn);
            wrapper.Children.Add(magBtn);

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

        // Show which part of a block is the fade rather than the picture.
        //
        // A transition adds to the clip's length, so the block already includes it - but nothing said
        // where the material stopped and the fade began, which matters most on the short clips these
        // transitions are for.
        //
        // Drawn as a gradient running to black, which is literally what the fade does, plus a hairline
        // at the boundary so the split can be read precisely rather than estimated from a gradient.
        private void AddTransitionShading(double x, double y, double width, double height,
                                          double dim, CinematicOperation clip)
        {
            if (clip == null || clip.TransitionStyle == TransitionStyle.HardSnap) return;

            double secs = clip.TransitionDuration.TotalSeconds;
            double len = clip.OpDuration.TotalSeconds;
            if (secs <= 0 || len <= 0 || _timelinePxPerSec <= 0) return;

            // The engine caps a fade at half the clip; the drawing has to agree or it would show a
            // longer fade than actually plays.
            secs = Math.Min(secs, len / 2);

            double w = secs * _timelinePxPerSec;
            if (w < 2) return;                       // too short to read; drawing it would be noise
            w = Math.Min(w, width / 2);

            bool fadeIn = clip.TransitionStyle == TransitionStyle.CinematicBridge;
            if (fadeIn) AddFadeWedge(x, y, w, height, dim, toBlackOnLeft: true);
            AddFadeWedge(x + width - w, y, w, height, dim, toBlackOnLeft: false);
        }

        private void AddFadeWedge(double x, double y, double w, double h, double dim, bool toBlackOnLeft)
        {
            var g = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0)
            };
            byte a = 165;
            g.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            { Color = Microsoft.UI.ColorHelper.FromArgb(toBlackOnLeft ? a : (byte)0, 0, 0, 0), Offset = 0.0 });
            g.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            { Color = Microsoft.UI.ColorHelper.FromArgb(toBlackOnLeft ? (byte)0 : a, 0, 0, 0), Offset = 1.0 });

            var wedge = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Fill = g,
                Opacity = dim,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(wedge, x);
            Canvas.SetTop(wedge, y);
            TimelineBar.Children.Add(wedge);

            // The boundary between picture and fade, on the inner edge of the wedge.
            var edge = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 1,
                Height = h,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(150, 255, 255, 255)),
                Opacity = dim,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(edge, toBlackOnLeft ? x + w : x);
            Canvas.SetTop(edge, y);
            TimelineBar.Children.Add(edge);
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

        // Active means one thing on every track: the playhead is inside the clip's window. Track 1
        // used to be asked differently — GetTimelineIndexForStoryTime — which is the SEQUENTIAL
        // lookup from when track 1 was a gapless spine that always had exactly one clip showing. It
        // deliberately falls back to "the clip just before, or the last clip" when the playhead is
        // in a gap or past the end, so a track 1 clip that had already finished still reported as
        // active and drew at full strength while every other track dimmed correctly. Tracks are
        // interchangeable now (§5.2) and track 1 may have gaps like any other, so it takes the same
        // test. That lookup still has callers who WANT the fallback; this is not one of them.
        private bool IsActiveAtPlayhead(CinematicOperation clip)
            => clip.IsActiveAt(ViewModel.CurrentStoryTime);

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
            if (clip != null && clip.IsVideoHidden) dim *= 0.4;

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

            bool isSelected = ViewModel.IsSelected(clip);

            // A GROUPED CLIP WEARS ITS GROUP COLOUR. Groups take the track palette in order - the
            // first gets T1 blue, the sixth T6, the seventh starts again at T1 - so a block reads as
            // one thing at a glance without any extra chrome on the timeline. White stays the mark
            // of an individual selection, so the two never say the same thing.
            int groupIndex = ViewModel.GroupIndexOf(clip);
            var strokeColour = groupIndex >= 0
                ? (groupIndex % 6 == 0 ? TrackPalette.Spine : TrackPalette.Overlay(groupIndex % 6 - 1))
                : Microsoft.UI.Colors.White;
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = 4,
                RadiusY = 4,
                Opacity = dim,
                Fill = gradient,
                Stroke = (isSelected || groupIndex >= 0)
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(strokeColour)
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(50, 0, 0, 0)),
                StrokeThickness = (isSelected || groupIndex >= 0) ? 2 : 1
            };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            TimelineBar.Children.Add(r);

            AddTransitionShading(x, y, width, height, dim, clip);

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
                
                
                if (clip != null)
                {
                    var lockColor = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold);
                    var muteColor = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);

                    // WHAT THE CLIP IS, before what you did to it. A row of state markers could
                    // never say whether a block was a photo or a piece of music, so both had to be
                    // inferred from the file name.
                    if (clip.IsImage)
                    {
                        var photo = new FontIcon
                        {
                            Glyph = "\uEB9F",                     // Picture
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = textColor,
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        ToolTipService.SetToolTip(photo, "Still image");
                        sp.Children.Add(photo);
                    }
                    else if (clip.IsAudioOnly)
                    {
                        var music = new FontIcon
                        {
                            Glyph = "\uEC4F",                     // MusicNote
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = textColor,
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        ToolTipService.SetToolTip(music, "Audio only - no picture");
                        sp.Children.Add(music);
                    }

                    if (clip.IsLocked)
                        sp.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = lockColor, Margin = new Thickness(4,0,0,0) });
                    
                    if (clip.IsVideoHidden)
                        sp.Children.Add(new FontIcon { Glyph = "\uED1A", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = textColor, Opacity = 0.5, Margin = new Thickness(4,0,0,0) });
                    
                    // Partial opacity: a half-filled disc, the mark every imaging tool uses for it.
                    //
                    // Deliberately NOT an eye - the eye-with-slash above already means hidden, and two
                    // eye shapes meaning different things at 12px is noise. Drawn rather than a font
                    // glyph so the hard half-and-half edge is exact at this size, and stroked so the
                    // transparent half still reads as a circle rather than a blob.
                    if (clip.Opacity < 0.999)
                    {
                        var half = new Microsoft.UI.Xaml.Media.LinearGradientBrush
                        {
                            StartPoint = new Windows.Foundation.Point(0, 0),
                            EndPoint = new Windows.Foundation.Point(1, 0)
                        };
                        half.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = TrackPalette.TextOn(color), Offset = 0.5 });
                        half.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0.5 });

                        var disc = new Microsoft.UI.Xaml.Shapes.Ellipse
                        {
                            Width = 11,
                            Height = 11,
                            Fill = half,
                            Stroke = textColor,
                            StrokeThickness = 1,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        ToolTipService.SetToolTip(disc, "Opacity " + Math.Round(clip.Opacity * 100) + "%");
                        sp.Children.Add(disc);
                    }

                    // Silent and muted are different things and used to look identical. A clip with
                    // no audio to give - an image, a file with no audio track, a frozen frame - gets
                    // a dimmed neutral marker, not the red one that means "you turned this off".
                    //
                    // Not on images: the photo glyph beside it already says there is no sound, and
                    // two marks for one fact is clutter on a 12px row.
                    // Images get NO audio marker at all - the picture glyph already says there is
                    // no sound, and this has to be the first branch rather than a condition on the
                    // second. Excluding images from the "no audio" case alone dropped them through
                    // to the muted case below, so every image wore the RED marker that means you
                    // turned its sound off. An image has no sound to turn off.
                    if (clip.IsImage)
                    {
                        // nothing: the picture glyph carries it
                    }
                    else if (!clip.CanHaveAudio)
                    {
                        var none = new FontIcon
                        {
                            Glyph = "\uE74F",
                            FontSize = 14,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = textColor,
                            Opacity = 0.35,
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        ToolTipService.SetToolTip(none, clip.AudioTooltip);
                        sp.Children.Add(none);
                    }
                    else if (clip.Volume == 0)
                    {
                        var muted = new FontIcon
                        {
                            Glyph = "\uE74F",
                            FontSize = 14,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = muteColor,
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        ToolTipService.SetToolTip(muted, "Muted");
                        sp.Children.Add(muted);
                    }
                }
                sp.Children.Add(label);
                
                Canvas.SetLeft(sp, x + 6); // Extra breathing room on the left
                Canvas.SetTop(sp, y); // Align exactly to top to let VerticalAlignment.Center do its job
                TimelineBar.Children.Add(sp);

                if (clip != null && _clipBlockElements.TryGetValue(clip, out var listSp)) listSp.Add(sp);
            }
        }

    }
}
