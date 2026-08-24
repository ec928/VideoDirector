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

// VideoDirectorControl - the playhead and everything that moves it: frame stepping, zoom, mark seeks, dropped media, play/pause and the scrubber.

namespace VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        private void UpdatePlayhead()
        {
            if (_playhead == null || _timelinePxPerSec <= 0) return;
            double sec = ViewModel.CurrentStoryTime.TotalSeconds;
            double x = sec * _timelinePxPerSec;
            Canvas.SetLeft(_playhead, x);
            if (_playheadKnob != null) Canvas.SetLeft(_playheadKnob, x - 4.5);

            if (_loopRegionHighlight != null)
            {
                if (ViewModel.LoopRegionStart.HasValue && ViewModel.LoopRegionEnd.HasValue)
                {
                    double sX = ViewModel.LoopRegionStart.Value.TotalSeconds * _timelinePxPerSec;
                    double eX = ViewModel.LoopRegionEnd.Value.TotalSeconds * _timelinePxPerSec;
                    Canvas.SetLeft(_loopRegionHighlight, sX);
                    _loopRegionHighlight.Width = Math.Max(1, eX - sX);
                    _loopRegionHighlight.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else
                {
                    _loopRegionHighlight.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
            }

            if (_playheadTime != null && _playheadBadge != null)
            {
                int m = (int)(sec / 60);
                _playheadTime.Text = m > 0 ? $"{m}:{sec - m * 60:00.0}" : $"{sec:0.0}s";
                
                double w = TimelineBar?.ActualWidth ?? 0;
                double tx = x + 6;
                // If we get close to the right edge, flip the badge to the left of the playhead.
                // 35px is roughly the max width of the badge.
                if (tx > w - 35) tx = x - (_playheadBadge.ActualWidth > 0 ? _playheadBadge.ActualWidth : 35) - 4;
                
                Canvas.SetLeft(_playheadBadge, System.Math.Max(0, tx));
                Canvas.SetTop(_playheadBadge, 2); // Sit nicely centered inside the taller 20px ruler area
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
                double stepSec = direction > 0 ? (1.0/30.0) : -(1.0/30.0);
                double newStoryTime = Math.Clamp(ViewModel.CurrentStoryTime.TotalSeconds + stepSec, 0, ViewModel.TotalStoryDuration.TotalSeconds);
                _ = _playbackEngine?.SeekCompositeToStoryTime(TimeSpan.FromSeconds(newStoryTime));
                return;
            }

            var op = ViewModel.SelectedClip;
            if (op == null) return;

            double fps = 30.0;
            double frameDuration = 1.0 / fps;
            
            double minTime = op.VideoStartTime.TotalSeconds;
            double maxTime = op.VideoEndTime.TotalSeconds;
            
            if (op.PlaybackSpeed <= 0)
            {
                // For freeze frames, allow the playhead to roam the entire source to pick a frame
                minTime = 0;
                maxTime = op.SourceDurationSeconds > 0 ? op.SourceDurationSeconds : double.PositiveInfinity;
            }
            
            double target = Math.Clamp(ViewModel.CurrentOperationTimeSeconds + direction * frameDuration, minTime, maxTime);
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

        // Where the preview should sit while a mark is being set. A mark frames the picture; it
        // does not choose which frame — so the preview stays inside the clip's own SOURCE window,
        // and a still (which has exactly one frame) does not move at all.
        //
        // Returning null means "do not seek", and for a still that is essential rather than
        // merely tidy: CurrentOperationTimeSeconds doubles as the still's frame-picker and
        // rewrites VideoStartTime whenever PlaybackSpeed <= 0. Seeking a still from here would
        // silently re-freeze it on a different frame of the movie.
        private static double? MarkPreviewSeconds(CinematicOperation op, VideoDirector.ViewModels.EditTarget target)
        {
            if (op == null || op.IsStill) return null;

            double start = op.VideoStartTime.TotalSeconds;
            double end = op.VideoEndTime.TotalSeconds;
            if (end <= start) return null;

            double t = target switch
            {
                // Midpoint of the SOURCE window. The old form (VideoStartTime + OpDuration/2) used
                // the TIMELINE hold, which for a still is unrelated to the source: a 10s snapshot
                // jumped the preview five seconds deeper into the movie, and — via the frame-picker
                // above — re-froze the clip there.
                VideoDirector.ViewModels.EditTarget.Mid => start + (end - start) / 2.0,
                VideoDirector.ViewModels.EditTarget.End => end - 0.1,
                _ => start
            };
            return Math.Clamp(t, start, end);
        }

        private void SeekForMark(CinematicOperation op, VideoDirector.ViewModels.EditTarget target)
        {
            var seconds = MarkPreviewSeconds(op, target);
            if (seconds.HasValue) ViewModel.CurrentOperationTimeSeconds = seconds.Value;
        }

        private void SetStart_Click(object? sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null && _playbackEngine != null)
            {
                op.StartMark = _playbackEngine.CaptureMark(op, transform);
                _playbackEngine?.UpdateWysiwygOverlay();
                SeekForMark(op, VideoDirector.ViewModels.EditTarget.Start);
                _playbackEngine?.BeginEdit(op, VideoDirector.ViewModels.EditTarget.Start);
                _playbackEngine?.SetSelectedMark(VideoDirector.ViewModels.EditTarget.Start);
            }
        }

        private void SetMid_Click(object? sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null && _playbackEngine != null)
            {
                op.MidMark = _playbackEngine.CaptureMark(op, transform);
                _playbackEngine?.UpdateWysiwygOverlay();
                SeekForMark(op, VideoDirector.ViewModels.EditTarget.Mid);
                _playbackEngine?.BeginEdit(op, VideoDirector.ViewModels.EditTarget.Mid);
                _playbackEngine?.SetSelectedMark(VideoDirector.ViewModels.EditTarget.Mid);
            }
        }

        private void SetEnd_Click(object? sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null && _playbackEngine != null)
            {
                op.EndMark = _playbackEngine.CaptureMark(op, transform);
                _playbackEngine?.UpdateWysiwygOverlay();
                SeekForMark(op, VideoDirector.ViewModels.EditTarget.End);

                // Force the decoder to update the paused frame by switching edit targets
                _playbackEngine?.BeginEdit(op, VideoDirector.ViewModels.EditTarget.End);
                _playbackEngine?.SetSelectedMark(VideoDirector.ViewModels.EditTarget.End);
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

        // The one list of what can be dropped. The two drop handlers each carried their own
        // hardcoded set covering .mp4/.mkv/.avi/.jpg/.png, so .jpeg, .gif, .bmp, .webp, .tif,
        // .mov and .wmv were offered by the file picker and silently refused on drop.
        private static bool IsSupportedMedia(string ext)
        {
            foreach (var e in SupportedMediaExtensions)
                if (string.Equals(e, ext, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly string[] SupportedMediaExtensions =
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv",
            // Sound-only sources: a music bed or a voiceover is a clip like any other, it simply
            // has no picture. An .mp4 holding only audio is caught by detection, not by extension.
            ".mp3", ".m4a", ".wav", ".aac", ".flac", ".wma", ".ogg",
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff"
        };

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectorViewModel.IsCinematicMode))
            {
                ApplyCinematicPresenter(ViewModel.IsCinematicMode);
                // Restart the countdown so entering cinematic mode fades the pill on the
                // same 5s the rest of the app uses, without needing a pointer move first.
                _inactivityTimer.Stop();
                _inactivityTimer.Start();
                return;
            }
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
                // Zone F: the global timeline recedes in Edit so it cannot be confused with the
                // Playbar's per-clip scrubber. It stays clickable - a click on it exits Edit.
                //
                // The TIMELINE dims, not the whole dock. The dock now also holds the transport, and
                // dimming the lot took the playbar down to 50% in Edit along with it.
                if (TimelineSection != null)
                    TimelineSection.Opacity = ViewModel.IsEditMode ? 0.5 : 1.0;
                if (TrackDock != null)
                    TrackDock.Opacity = 1.0;

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
                    PlayPauseIcon.Glyph = ViewModel.IsPlaying ? "\uE769" : "\uE768";
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
    }
}
