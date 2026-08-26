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

// VideoDirectorControl - the clip context menu and the operations behind it: split, duplicate, snapshot, remove, borders, layouts.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
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

            if (_contextClip != null)
            {
                TimelineHideItem.IsChecked = _contextClip.IsVideoHidden;
                TimelineLockItem.IsChecked = _contextClip.IsLocked;

                TimelineBorderTypeNone.IsChecked = _contextClip.BorderType == Models.BorderType.None;
                TimelineBorderTypeSolid.IsChecked = _contextClip.BorderType == Models.BorderType.Solid;
                TimelineBorderTypeSoft.IsChecked = _contextClip.BorderType == Models.BorderType.Soft;
                TimelineBorderTypeFilmStrip.IsChecked = _contextClip.BorderType == Models.BorderType.FilmStrip;

                TimelineBorderColorWhite.IsChecked = _contextClip.BorderColor == Microsoft.UI.Colors.White;
                TimelineBorderColorBlack.IsChecked = _contextClip.BorderColor == Microsoft.UI.Colors.Black;
                TimelineBorderColorRed.IsChecked = _contextClip.BorderColor == Microsoft.UI.Colors.Red;
                TimelineBorderColorGold.IsChecked = _contextClip.BorderColor == Microsoft.UI.Colors.Gold;
                TimelineBorderColorBlue.IsChecked = _contextClip.BorderColor == Microsoft.UI.Colors.DodgerBlue;
                TimelineBorderColorGreen.IsChecked = _contextClip.BorderColor == Microsoft.UI.Colors.LimeGreen;
                TimelineBorderColorDarkGrey.IsChecked = _contextClip.BorderColor == Microsoft.UI.Colors.DarkGray;

                TimelineBorderThick2.IsChecked = _contextClip.BorderThickness == 2;
                TimelineBorderThick4.IsChecked = _contextClip.BorderThickness == 4;
                TimelineBorderThick8.IsChecked = _contextClip.BorderThickness == 8;
            }
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
            if (_contextClip != null && !_contextClip.IsLocked) RemoveClip(_contextClip, _contextIsSpine);
        }

        private void TimelineHide_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip == null) return;
            _contextClip.IsVideoHidden = !_contextClip.IsVideoHidden;
            ViewModel.RecordIfChanged();
            BuildTimelineBar();
            _playbackEngine?.RefreshComposite();
        }

        private void TimelineLock_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip == null) return;
            _contextClip.IsLocked = !_contextClip.IsLocked;
            ViewModel.RecordIfChanged();
            BuildTimelineBar();
        }

        private void TimelineEdit_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null && !_contextClip.IsLocked) SelectClip(_contextClip, _contextIsSpine);
        }

        private void TimelineOpacity_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (float.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out float opacity))
                    _contextClip.Opacity = opacity;
                ViewModel.RecordIfChanged();
                _playbackEngine?.RefreshComposite();
            }
        }

        private void TimelineLayoutFull_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null)
            {
                _contextClip.PlacementWidth = 1.0;
                _contextClip.PlacementHeight = 1.0;
                _contextClip.PlacementCenterX = 0.5;
                _contextClip.PlacementCenterY = 0.5;
                ViewModel.RecordIfChanged();
                _playbackEngine?.RefreshComposite();
            }
        }

        private void TimelineLayoutWindow_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null)
            {
                _contextClip.PlacementWidth = 0.3;
                _contextClip.PlacementHeight = 0.3;
                _contextClip.PlacementCenterX = 0.72;
                _contextClip.PlacementCenterY = 0.72;
                ViewModel.RecordIfChanged();
                _playbackEngine?.RefreshComposite();
            }
        }
        private void TimelineBorderType_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (Enum.TryParse(tag, out Models.BorderType type))
                    _contextClip.BorderType = type;
                _playbackEngine?.RefreshComposite();
                ViewModel.RecordIfChanged();
            }
        }

        private void TimelineBorderColor_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (tag == "White") _contextClip.BorderColor = Microsoft.UI.Colors.White;
                else if (tag == "Black") _contextClip.BorderColor = Microsoft.UI.Colors.Black;
                else if (tag == "Red") _contextClip.BorderColor = Microsoft.UI.Colors.Red;
                else if (tag == "Gold") _contextClip.BorderColor = Microsoft.UI.Colors.Gold;
                else if (tag == "Blue") _contextClip.BorderColor = Microsoft.UI.Colors.DodgerBlue;
                else if (tag == "Green") _contextClip.BorderColor = Microsoft.UI.Colors.LimeGreen;
                else if (tag == "DarkGrey") _contextClip.BorderColor = Microsoft.UI.Colors.DarkGray;
                _playbackEngine?.RefreshComposite();
                ViewModel.RecordIfChanged();
            }
        }

        private void TimelineBorderThickness_Click(object? sender, RoutedEventArgs e)
        {
            if (_contextClip != null && sender is FrameworkElement fe && fe.Tag is string tag)
            {
                if (double.TryParse(tag, out double t))
                    _contextClip.BorderThickness = t;
                _playbackEngine?.RefreshComposite();
                ViewModel.RecordIfChanged();
            }
        }

        // A full clone of a clip's editable state. SourceDuration and PlaybackSpeed must precede
        // the trim: the trim setters clamp against the source length and derive OpDuration from speed.
        // A faithful copy of everything that makes the clip look and behave as it does.
        //
        // ORDER MATTERS, and OpDuration must come last. It never used to be copied at all, which
        // was invisible for an ordinary video: RecomputeOpDurationFromTrim derives the duration
        // from the trim window, so the copy landed on the right length by accident. A clip with no
        // source window has nothing to derive from - a freeze frame has VideoStartTime ==
        // VideoEndTime, and an image has no timeline in the file at all - so the duplicate came out
        // at the TimeSpan.Zero default. That is a clip no instant falls inside, so duplicating an
        // image produced nothing visible at all, and duplicating a freeze frame produced a clip
        // whose duration box showed its 0.1s minimum.
        //
        // Setting it after PlaybackSpeed and the trim times is what makes the setter treat it as a
        // hold time rather than re-trimming a window - the same ordering SnapshotClip relies on.
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
            // Mid is optional and was being dropped, so duplicating a three-keyframe move silently
            // flattened it to a straight Start -> End.
            MidMark = clip.MidMark == null
                ? null
                : new SpatialMark(clip.MidMark.Scale, clip.MidMark.X, clip.MidMark.Y),
            EndMark = new SpatialMark(clip.EndMark.Scale, clip.EndMark.X, clip.EndMark.Y),
            TransitionDuration = clip.TransitionDuration,
            TransitionStyle = clip.TransitionStyle,
            Opacity = clip.Opacity,
            Volume = clip.Volume,
            PlacementWidth = clip.PlacementWidth,
            PlacementHeight = clip.PlacementHeight,
            PlacementCenterX = clip.PlacementCenterX,
            PlacementCenterY = clip.PlacementCenterY,
            // Styling is part of what you see, so a copy that loses it is not a copy.
            BorderType = clip.BorderType,
            BorderColor = clip.BorderColor,
            BorderThickness = clip.BorderThickness,
            // Hidden travels: it is a property of the clip. Locked deliberately does not - it is a
            // guard against moving THAT clip, and a fresh copy you cannot drag is a copy you cannot
            // put where you wanted it.
            IsVideoHidden = clip.IsVideoHidden,
            SourceHasVideo = clip.SourceHasVideo,
            SourceHasAudio = clip.SourceHasAudio,
            Thumbnail = clip.Thumbnail,
            OpDuration = clip.OpDuration
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
                SourceHasVideo = clip.SourceHasVideo,
                SourceHasAudio = clip.SourceHasAudio,
                Thumbnail = clip.Thumbnail
            };

            InsertAfter(clip, isSpine, snap, clip.StartTime.TotalSeconds + clip.OpDuration.TotalSeconds);

            // Decode the frozen frame at source resolution now, so the push-in has real pixels to
            // resample from the first time this clip is played rather than after a warm-up.
            _playbackEngine?.PrebakeStillFrame(snap);
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
    }
}
