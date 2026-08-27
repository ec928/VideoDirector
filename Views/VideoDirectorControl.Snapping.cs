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

        /// <summary>Snap a block by its own leading and trailing edges.</summary>
        /// <remarks>
        /// Every member is excluded from the snap points, or the block snaps to itself and is pulled
        /// to whatever offset one of its own clips happens to sit at. Snapping counts as on if the
        /// track under the pointer has it on - a block spans several, and asking all of them to agree
        /// would mean one track with it switched off disabled it for the whole block.
        /// </remarks>
        private double ApplyGroupSnapping(double desiredStartSec, double durSec, Models.TimelineTrack targetTrack)
        {
            if (ViewModel == null || targetTrack == null || !targetTrack.IsSnappingEnabled || _timelinePxPerSec <= 0)
                return desiredStartSec;

            double threshold = 8.0 / _timelinePxPerSec;
            double best = desiredStartSec;
            double minDiff = threshold;

            foreach (double sp in GetTimelineSnapPoints(null, includePlayhead: true))
            {
                bool ownEdge = false;
                foreach (var kv in _dragGroupOrigin)
                {
                    double st = kv.Value.TotalSeconds;
                    if (Math.Abs(sp - st) < 1e-6 || Math.Abs(sp - (st + kv.Key.OpDuration.TotalSeconds)) < 1e-6)
                    {
                        ownEdge = true;
                        break;
                    }
                }
                if (ownEdge) continue;

                double dLeft = Math.Abs(desiredStartSec - sp);
                if (dLeft < minDiff) { minDiff = dLeft; best = sp; }

                double dRight = Math.Abs((desiredStartSec + durSec) - sp);
                if (dRight < minDiff) { minDiff = dRight; best = sp - durSec; }
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

            // A GROUP MOVES IN TIME ONLY. Reordering sections of the piece is a horizontal job, and
            // a group has no single answer to "which track did you mean" - the clips are spread over
            // several. Dragging one clip on its own still changes track exactly as before.
            bool groupDrag = _dragGroupOrigin.Count > 1;

            bool trackChanged = !groupDrag && current != null && !ReferenceEquals(current, target);
            if (trackChanged)
            {
                current.Clips.Remove(_dragClip);
                target.Clips.Add(_dragClip);
            }
            if (groupDrag) target = current;

            // Horizontal: set the start time
            // (tracks are strict — an overlap would silently hide one clip at playback).
            double dur = _dragClip.OpDuration.TotalSeconds;
            double newStart = (p.X / _timelinePxPerSec) - _dragGrabOffsetSec;
            newStart = Math.Max(0, newStart);
            if (!groupDrag)
            {
                newStart = ApplyClipSnapping(newStart, dur, _dragClip, target);
            }
            else
            {
                // A BLOCK SNAPS BY ITS OWN EDGES. Snapping the grabbed clip aligns whichever member
                // you happened to take hold of, which has nothing to do with where the block lands -
                // so trying to butt a block against something never quite worked. Convert to the
                // block's leading edge, snap that, and convert back.
                double blockStart = double.MaxValue, blockEnd = 0;
                foreach (var kv in _dragGroupOrigin)
                {
                    double st = kv.Value.TotalSeconds;
                    if (st < blockStart) blockStart = st;
                    double en = st + kv.Key.OpDuration.TotalSeconds;
                    if (en > blockEnd) blockEnd = en;
                }

                double lead = blockStart + (newStart - _dragGroupOrigin[_dragClip].TotalSeconds);
                double snappedLead = ApplyGroupSnapping(lead, blockEnd - blockStart, target);
                newStart += snappedLead - lead;
            }
            if (!groupDrag)
            {
                _dragClip.StartTime = TimeSpan.FromSeconds(newStart);
            }
            else
            {
                // ONE DELTA, applied to where everything started. Nudging each clip from wherever
                // the last pointer move left it would let the group drift apart over a long drag.
                double delta = newStart - _dragGroupOrigin[_dragClip].TotalSeconds;

                // Nothing may be pushed before zero, so the whole group stops when its earliest
                // member reaches the start rather than piling up against it.
                double earliest = double.MaxValue;
                foreach (var kv in _dragGroupOrigin)
                    if (kv.Value.TotalSeconds < earliest) earliest = kv.Value.TotalSeconds;
                if (earliest + delta < 0) delta = -earliest;

                foreach (var kv in _dragGroupOrigin)
                    kv.Key.StartTime = TimeSpan.FromSeconds(kv.Value.TotalSeconds + delta);
            }

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
