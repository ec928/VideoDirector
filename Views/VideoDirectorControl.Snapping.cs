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

// VideoDirectorControl - snapping, hit-testing and selection: where an edge wants to land, what is under the cursor, and moving a clip there.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
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

        // Dropping a clip onto the FRONT half of another means "put me before this", not "find me
        // a gap after it". Without this there was no way to move a clip in front of one that
        // starts at 0: ResolveOverlaps sorts by start time, the dropped clip sorted second, and it
        // got pushed out to the far end. The only route was to shuffle the other clip out of the
        // way first and then back.
        //
        // One tick earlier is enough to win the sort; ResolveOverlaps then pushes the other clip
        // right, which is what "insert before" means on a timeline.
        private static void InsertBeforeIfDroppedOnFrontHalf(Models.TimelineTrack track,
                                                             CinematicOperation moving)
        {
            if (track == null || moving == null) return;

            long start = moving.StartTime.Ticks;
            foreach (var other in track.Clips)
            {
                if (ReferenceEquals(other, moving)) continue;

                long os = other.StartTime.Ticks;
                long oe = os + other.OpDuration.Ticks;
                if (start <= os || start >= oe) continue; // did not land inside this clip

                if (start - os < (oe - os) / 2)
                    moving.StartTime = TimeSpan.FromTicks(Math.Max(0, os - 1));
                return;
            }
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
            _ = _playbackEngine?.SeekCompositeToStoryTime(TimeSpan.FromSeconds(sec));
        }

        // Which clip (and its start-second) sits under a point in the clip rows, if any.
        private (CinematicOperation clip, bool isSpine, double startSec) HitClip(Windows.Foundation.Point p)
        {
            if (_timelinePxPerSec <= 0) return (null, false, 0);
            var t = TimeSpan.FromSeconds(Math.Max(0, p.X / _timelinePxPerSec));

            if (p.Y >= RowSpineY && p.Y < RowSpineY + BlockH && ViewModel.Tracks.Count > 0)
            {
                foreach (var clip in ViewModel.Tracks[0].Clips)
                    if (t >= clip.StartTime && t < clip.StartTime + clip.OpDuration)
                        return (clip, true, clip.StartTimeSeconds);
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
                if (ViewModel.IsPlaying)
                {
                    if (!ReferenceEquals(_playbackEngine?.CurrentPlayingOperation, clip))
                        _ = _playbackEngine?.SeekCompositeToStoryTime(clip.StartTime);
                    return;
                }
            }
            else ViewModel.SelectedOverlay = clip;

            if (!ViewModel.IsPlaying)
            {
                ViewModel.CurrentStoryTime = clip.StartTime;
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
                // Deliberately no ResolveOverlaps: see the note in the gapless branch above. The
                // clip follows the cursor between rows so you can see where it is going, but no
                // track it merely passes over is rewritten. Release commits.
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
    }
}
