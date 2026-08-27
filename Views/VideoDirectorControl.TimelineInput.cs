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

// VideoDirectorControl - pointer work on the timeline: ruler scrubs, a tap selects, a drag moves or reorders.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        // Timeline pointer model (standard NLE): the top ruler scrubs; the clip rows drag clips.
        // Tap in a row = select; drag in a row = move (overlay = reposition in time, spine =
        // reorder). Empty space in the rows also scrubs.
        // Whether shift was held when the press began, read at the release.
        private bool _selectToggle;

        // Where every selected clip sat when a group drag began. The move is then ONE delta
        // applied to the original layout, rather than each clip nudged from wherever the last
        // pointer move left it - which would drift apart over a long drag.
        private readonly System.Collections.Generic.Dictionary<Models.CinematicOperation, TimeSpan>
            _dragGroupOrigin = new();

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
            // Captured at PRESS: by the time the release runs, the key may be up.
            // ONE MODIFIER. Shift toggles - it adds a clip that is not in the selection and removes
            // one that is. A second key for removing was needless: the clip already tells you which
            // it will do, because you can see whether it is outlined.
            _selectToggle = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
            _timelinePressPoint = p;
            _timelinePressed = true;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            TimelineBar.CapturePointer(e.Pointer);

            if (p.Y < RulerH) 
            { 
                _timelineScrubbing = true; 
                _timelineLoopStartX = p.X;
                ViewModel.LoopRegionStart = null;
                ViewModel.LoopRegionEnd = null;
                UpdatePlayhead();
                ScrubToX(p.X); 
                return; 
            }

            var hit = HitClip(p);
            if (hit.clip != null)
            {
                if (!hit.clip.IsLocked)
                {
                    _dragClip = hit.clip;
                    _dragIsSpine = hit.isSpine;
                    _dragGrabOffsetSec = (p.X / _timelinePxPerSec) - hit.startSec;

                    // A GROUP TRAVELS AS A GROUP. Dragging any member moves all of it, whether or
                    // not it is selected - that is what being grouped means. Failing that, dragging
                    // a member of a multi-selection moves the selection.
                    _dragGroupOrigin.Clear();
                    var grp = ViewModel.GroupOf(hit.clip);
                    if (grp != null)
                    {
                        foreach (var c in grp.Clips)
                            if (!c.IsLocked) _dragGroupOrigin[c] = c.StartTime;
                    }
                    else if (ViewModel.MultiSelectedCount > 0 && ViewModel.IsSelected(hit.clip))
                    {
                        foreach (var c in ViewModel.SelectedClips)
                            if (!c.IsLocked) _dragGroupOrigin[c] = c.StartTime;
                    }
                }
            }
            else
            {
                // Empty lane: scrub, and drop the selection. Every NLE deselects on a click into
                // empty timeline, and until the null-assignment above was fixed there was no way
                // to deselect at all. The ruler is deliberately excluded (handled earlier) -
                // scrubbing the time ruler should not disturb what you have selected.
                ViewModel.SelectedClip = null;
                ViewModel.ClearMultiSelection();
                _timelineScrubbing = true;
                ScrubToX(p.X);
            }
        }

        private void TimelineBar_PointerMoved(object? sender, PointerRoutedEventArgs e)
        {
            // Recorded even when not dragging: the context menu resolves its target from here.
            _lastHoverPoint = e.GetCurrentPoint(TimelineBar).Position;

            if (!_timelinePressed) return;
            var p = _lastHoverPoint;

            if (_timelineScrubbing) 
            { 
                if (Math.Abs(p.X - _timelineLoopStartX) > 4 && _timelinePressPoint.Y < RulerH)
                {
                    double startSec = Math.Max(0, Math.Min(_timelineLoopStartX, p.X) / _timelinePxPerSec);
                    double endSec = Math.Max(0, Math.Max(_timelineLoopStartX, p.X) / _timelinePxPerSec);
                    ViewModel.LoopRegionStart = TimeSpan.FromSeconds(startSec);
                    ViewModel.LoopRegionEnd = TimeSpan.FromSeconds(endSec);
                    ViewModel.IsLooping = true;
                }
                ScrubToX(p.X); 
                return; 
            }
            if (_dragClip == null) return;
            if (!_timelineMovingClip && Math.Abs(p.X - _timelinePressPoint.X) < 4) return;
            _timelineMovingClip = true;

            var currentTrk = TrackOf(_dragClip);
            bool isGapless = currentTrk != null && currentTrk.IsGapless;

            if (isGapless)
            {
                // Live transfer between Track 1 (Spine) and Track 2/3/4 (Overlays)
                // Count > 1, not > 0: with a single track there is no upper row to drop onto, and
                // the clamp below would be Math.Clamp(x, 0, -1), which throws.
                if (_dragIsSpine && p.Y >= RowOvY && ViewModel.Tracks.Count > 1 && ViewModel.Tracks[0].Clips.Count > 1)
                {
                    ViewModel.Tracks[0].Clips.Remove(_dragClip);
                    int targetIndex = Math.Clamp((int)((p.Y - RowOvY) / RowPitch), 0, ViewModel.Tracks.Count - 2) + 1;
                    var targetTrk = ViewModel.Tracks[targetIndex];
                    double newStart = Math.Max(0, (p.X / _timelinePxPerSec) - _dragGrabOffsetSec);
                    _dragClip.StartTime = TimeSpan.FromSeconds(newStart);
                    targetTrk.Clips.Add(_dragClip);
                    // NO ResolveOverlaps here. This runs on every pointer move, so dragging a clip
                    // from T1 up to T6 crossed - and permanently rearranged - every track on the
                    // way. ResolveOverlaps shifts the START TIMES of the clips already on a track
                    // to make room, and taking the dragged clip away again does not put them back.
                    // The commit belongs on release, where TimelineBar_PointerReleased already
                    // does it once, for the track the clip actually landed on.
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

        private void TimelineBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var p = e.GetPosition(TimelineBar);
            var hit = HitClip(p);
            if (hit.clip != null && !ViewModel.IsPlaying)
            {
                SelectClip(hit.clip, hit.isSpine);
                _playbackEngine?.BeginEdit(ViewModel.SelectedClip, ViewModel.CurrentEditTarget);
            }
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
                if (!wasMoving)
                {
                    // Shift extends, Ctrl removes, a plain tap replaces. The primary selection is
                    // still set on a plain tap so the inspector and Edit mode behave as before.
                    if (_selectToggle)
                    {
                        if (ViewModel.IsSelected(_dragClip)) ViewModel.RemoveFromSelection(_dragClip);
                        else ViewModel.AddToSelection(_dragClip);
                        BuildTimelineBar();
                    }
                    else
                    {
                        ViewModel.ClearMultiSelection();
                        SelectClip(_dragClip, _dragIsSpine);
                    }
                }
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
                        // THE BLOCK YOU MOVED STAYS THE BLOCK YOU MOVED, AND NO TRACK EVER CARRIES
                        // TWO CLIPS AT ONCE. Plain ResolveOverlaps honours the second and breaks the
                        // first - it pushes whatever sorts later, so a group landing on occupied
                        // space gets shuffled apart. Anchoring the group holds both: its members do
                        // not move, and everything else gives way around them.
                        //
                        // Every track the group touches has to be resolved, not just the one under
                        // the pointer, because the members are spread across several.
                        // ONE RULE FOR EVERY DROP. Where it landed decides who moves: the front half
                        // of something means you claimed its place, the back half means you were
                        // aiming past it. A block is judged by its whole span, a lone clip by itself,
                        // and anything that gives way moves whole if it belongs to a block.
                        var dropped = _dragGroupOrigin.Count > 1
                            ? new System.Collections.Generic.HashSet<Models.CinematicOperation>(_dragGroupOrigin.Keys)
                            : new System.Collections.Generic.HashSet<Models.CinematicOperation> { _dragClip };
                        // Only a real group claims the whole slice; a lone clip or an ad-hoc
                        // multi-selection settles against its own track.
                        bool isBlock = ViewModel.GroupOf(_dragClip) != null;
                        ViewModel.SettleDrop(dropped, isBlock);
                        _playbackEngine?.RefreshComposite();
                    }
                }
            }
            _timelinePressed = false;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            _dragGroupOrigin.Clear();
            if (wasMoving)
            {
                BuildTimelineBar();          // clear the drag ghost
                ViewModel.RecordIfChanged(); // record the move/reorder as one undo step
            }
        }
    }
}
