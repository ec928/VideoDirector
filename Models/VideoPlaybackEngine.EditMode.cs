using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using VideoDirector.ViewModels;
using Microsoft.UI.Dispatching;

// VideoPlaybackEngine - Arrange and Edit. The mode alone decides what input does; Edit shows ONE clip full-screen and previews only its own motion.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        // ==================== Two modes: Arrange (default) and Edit ====================
        //
        // Strict segregation — the mode alone decides what input does, nothing else:
        //   Arrange (default): the whole composite; Play plays everything; drag a PiP to move
        //                      it, wheel to resize (InputMode = ArrangePips).
        //   Edit:              ONE clip full-screen; frame its content + motion; Play previews
        //                      ONLY that clip's Ken Burns (InputMode = Content).
        // You enter Edit by selecting a clip in the dock; Exit returns to Arrange.

        public bool IsEditMode => _mode == EditorMode.Edit;

        // The single clip being edited + the player showing it (main player for Track 1, overlay
        // player for Track 2). Used by the clip-scoped Edit-mode preview.
        

        // Put the app into Edit mode for the given clip/player. isOverlayEdit = true when the clip
        // is a Track 2 overlay (edited in the overlay player); false for a Track 1 clip. The flag
        // decides whether the subsequent HideAllOverlays keeps overlay slot 1 (the edit surface).
private void SetEditModeState(CinematicOperation clip, MediaPlayer player, bool isOverlayEdit)
        {
            StopEditPreview();
            _mode = EditorMode.Edit;
            _editClip = clip;
            _isEditingOverlay = isOverlayEdit;
            _playerControl.InputMode = Views.PlayerInputMode.Content;
            _viewModel.IsEditMode = true;
        }

        // Return to Arrange (the default composite view). Releases the edit surface and lays the
        // composite PiPs out at the current playhead.
public void ExitToArrange()
        {
            StopEditPreview();
            _mode = EditorMode.Arrange;
            _isEditingOverlay = false;
            _editClip = null;
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
            _viewModel.IsEditMode = false;
            UpdateWysiwygOverlay();
            EvaluateOverlays(_viewModel.CurrentStoryTime);
        }

        // Single entry point for editing ANY clip. Dispatches to the correct surface (spine clips
        // live in the main players; overlay clips in the overlay player) but every clip goes
        // through one call, one Edit state, one WYSIWYG/telemetry path — that's the "one Edit
        // pipeline" contract the UI relies on.
public void BeginEdit(CinematicOperation clip, EditTarget target)
        {
            if (clip == null) return;
            _ = EnterEditMode(clip, target);
        }

        public async System.Threading.Tasks.Task EnterEditMode(CinematicOperation overlay, EditTarget target = EditTarget.Start)
        {
            if (overlay == null || string.IsNullOrWhiteSpace(overlay.FilePath)) return;

            SetEditModeState(overlay, _overlayPlayer[0], isOverlayEdit: true);
            StopPlayback();
            for (int i = 1; i < MaxOverlayTracks; i++)
                if (_activeOverlay[i] != null) ReleaseOverlaySlot(i);

            _activeOverlay[0] = overlay;

            var player = _overlayPlayer[0];
            var grid = _playerControl.OverlayVisuals[0].Grid;

            // WHICH SURFACE, decided first, because it decides which transform the whole edit
            // session drives.
            //
            // This used to be hardcoded to the video surface and the video transform. A speed-0
            // VIDEO snapshot survived that: Media Foundation opens the file and parks on a frame,
            // so the MediaPlayerElement really can show it. An IMAGE cannot be opened that way at
            // all — StillFrameFactory exists precisely because Media Foundation will not reliably
            // load a .jpg — so Edit mode showed an empty video surface with the bitmap hidden
            // behind it, and the wheel and drag moved a transform on an element nobody could see.
            // Same Ken Burns as playback now, on the same surface playback uses.
            if (overlay.IsStill) await EnsureStillFrameAsync(overlay);
            if (_activeOverlay[0] != overlay) return;

            var mode = RenderModeFor(overlay);
            var transform = mode == OverlayRender.Still
                ? _playerControl.OverlayVisuals[0].StillTransform
                : _playerControl.OverlayVisuals[0].Transform;

            // A baked still needs no decoder. Opening one for an image also cost a dead 1500ms
            // every time Edit was entered, since MediaOpened never fires and the wait always ran
            // to its timeout.
            if (mode != OverlayRender.Still &&
                (player.Source == null || !string.Equals((player.Source as MediaSource)?.Uri?.LocalPath, overlay.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                player.MediaOpened += handler;
                player.Source = MediaSource.CreateFromUri(new Uri(overlay.FilePath));
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                player.MediaOpened -= handler;
            }
            if (_activeOverlay[0] != overlay) return;

            SpatialMark markToEdit;
            TimeSpan seekPos;
            if (target == EditTarget.Mid && overlay.MidMark != null)
            {
                seekPos = overlay.VideoStartTime + TimeSpan.FromSeconds((overlay.VideoEndTime - overlay.VideoStartTime).TotalSeconds / 2);
                markToEdit = overlay.MidMark;
            }
            else if (target == EditTarget.End)
            {
                seekPos = overlay.VideoEndTime;
                if (seekPos.TotalMilliseconds > 100)
                {
                    seekPos -= TimeSpan.FromMilliseconds(100);
                    if (seekPos < overlay.VideoStartTime) seekPos = overlay.VideoStartTime;
                }
                markToEdit = overlay.EndMark;
            }
            else
            {
                seekPos = overlay.VideoStartTime;
                markToEdit = overlay.StartMark;
            }

            if (mode != OverlayRender.Still)
            {
                if (player.PlaybackSession != null) player.PlaybackSession.Position = seekPos;
                player.Pause();
                player.StepForwardOneFrame();
            }
            else
            {
                player.Pause();
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (_activeOverlay[0] != overlay) return;
                // Seed the live transform from the mark: mark X/Y are fractions of the fit, the
                // transform is in pane pixels, and Edit mode's box IS the fit.
                //
                // CacheOverlayAspect moved ABOVE the seed. It ran after, so a clip with no stored
                // SourceAspect seeded its translate against whatever the previous clip's aspect
                // implied and only got the right one from the next frame — the framing visibly
                // settled after the fact. The decoder is open by here, so this is the point at
                // which the aspect is knowable.
                EnsureMarksNormalized(overlay);

                // CacheOverlayAspect reads the decoder, which a bitmap still does not have. The
                // clip carries the aspect already (it is persisted), so take it from there.
                if (mode == OverlayRender.Still)
                {
                    if (overlay.SourceAspect > 0) _overlayAspect[0] = overlay.SourceAspect;
                }
                else
                {
                    CacheOverlayAspect(0, player);
                }

                transform.ScaleX = markToEdit.Scale;
                transform.ScaleY = markToEdit.Scale;
                if (TryGetMarkSpace(overlay, out double seedFitW, out double seedFitH))
                {
                    transform.TranslateX = markToEdit.X * seedFitW;
                    transform.TranslateY = markToEdit.Y * seedFitH;
                }
                _playerControl.ActiveTransform = transform;
                SetOverlayRender(0, mode, overlay); 
                ApplyOverlayBox(0, overlay, true);
                grid.Opacity = 1.0;
                
                if (player.PlaybackSession != null)
                {
                    BackfillSourceDuration(overlay, player);
                    _viewModel.CurrentOperationDuration = player.PlaybackSession.NaturalDuration;
                    _viewModel.CurrentOperationTime = player.PlaybackSession.Position;
                }
                
                UpdateWysiwygOverlay();
            });
        }

        // Back-compat name used by the selection wiring — now just returns to Arrange.
        public void ClearOverlayEditMode() => ExitToArrange();

        public void OnViewportResized()
        {
            UpdateWysiwygOverlay();
            if (_isEditingOverlay && _activeOverlay[0] != null)
                ApplyOverlayBox(0, _activeOverlay[0], true);
        }

        // ---- Clip-scoped Edit-mode preview (Play in Edit mode = this clip's Ken Burns only) ----

        private bool _editPreviewPlaying;

        public void ToggleEditPreview()
        {
            if (_editPreviewPlaying) StopEditPreview();
            else StartEditPreview();
        }

        private void StartEditPreview()
        {
            if (_editClip == null || _overlayPlayer[0]?.PlaybackSession == null) return;
            _editPreviewPlaying = true;
            _editPreviewClock.Restart();

            // A clip rendering from a baked bitmap has no open source to seek or roll — the marks
            // animate off the wall clock in EditPreview_Rendering and nothing else is needed.
            // Testing PlaybackSpeed alone missed this: an image is a still by EXTENSION and keeps
            // speed 1, so it took the play path and asked a player with no source to seek.
            //
            // This must NOT return early. It did, and the return jumped the two lines below —
            // the render subscription and IsPlaying — so previewing an image started a clock that
            // nothing read and appeared to do nothing at all.
            if (RenderModeFor(_editClip) == OverlayRender.Still)
            {
                _overlayPlayer[0].Pause();
            }
            else
            {
                _overlayPlayer[0].PlaybackSession.Position = _editClip.VideoStartTime;

                // Respect the clip's own speed. Speed 0 = a STILL: freeze the frame; the Ken Burns
                // marks still animate over OpDuration below. (Was hardcoded to 1.0 + Play, so a
                // speed-0 clip wrongly ran at full speed.)
                double clipSpeed = _editClip.PlaybackSpeed;
                _overlayPlayer[0].Volume = _editClip.Volume;
                if (clipSpeed > 0)
                {
                    _overlayPlayer[0].PlaybackSession.PlaybackRate = clipSpeed;
                    _overlayPlayer[0].Play();
                }
                else
                {
                    _overlayPlayer[0].Pause();
                }
            }
            CompositionTarget.Rendering += EditPreview_Rendering;
            _viewModel.IsPlaying = true;
        }

        private void StopEditPreview()
        {
            if (!_editPreviewPlaying) return;
            _editPreviewPlaying = false;
            CompositionTarget.Rendering -= EditPreview_Rendering;
            _overlayPlayer[0]?.Pause();
            _viewModel.IsPlaying = false;
        }

        private void EditPreview_Rendering(object? sender, object e)
        {
            if (_editClip == null || _playerControl.ActiveTransform == null) return;
            // Apply Volume live so the audio slider works while the preview is playing (overlays
            // start muted, so a one-time apply at play meant raising it did nothing until restart).
            if (_overlayPlayer[0] != null) _overlayPlayer[0].Volume = _editClip.Volume;
            double dur = _editClip.OpDuration.TotalSeconds;
            if (dur <= 0) dur = 1;
            double progress = _editPreviewClock.Elapsed.TotalSeconds / dur;
            
            if (progress >= 1.0)
            {
                _editPreviewClock.Restart(); // loop the preview
                progress = 0;
                if (_overlayPlayer[0]?.PlaybackSession != null &&
                    RenderModeFor(_editClip) != OverlayRender.Still)
                {
                    _overlayPlayer[0].PlaybackSession.Position = _editClip.VideoStartTime;
                    // Resume if it hit end-of-media mid-loop; a still (speed 0) stays paused.
                    if (_editClip.PlaybackSpeed > 0) _overlayPlayer[0].Play();
                }
            }
            // Edit mode frames against the whole fit (the box is not a PiP here), so the mark
            // fractions convert with the fit itself — no PanScale.
            EnsureMarksNormalized(_editClip);
            // A false here means the fit is unknowable this tick; framing against the 0/0 it hands
            // back would apply the scale with the pan zeroed — a centred zoom, not the authored
            // framing. Skipping leaves the previous frame's framing until it resolves.
            if (TryGetMarkSpace(_editClip, out double editFitW, out double editFitH))
                ApplyMarksAtProgress(_editClip, Math.Clamp(progress, 0.0, 1.0), _playerControl.ActiveTransform,
                                     editFitW, editFitH);

            // Drive the per-clip scrubber off the real decode position so it tracks the preview.
            // (Assigning CurrentOperationTime — not …Seconds — only notifies the slider; it does
            // not fire a seek back into the player, so there's no feedback loop.)
            // A bitmap still has no decode position to follow — its progress IS the wall clock, so
            // drive the scrubber from that or it sits at zero for the whole preview.
            if (RenderModeFor(_editClip) == OverlayRender.Still)
                _viewModel.CurrentOperationTime = TimeSpan.FromSeconds(Math.Clamp(progress, 0.0, 1.0) * dur);
            else if (_overlayPlayer[0]?.PlaybackSession != null)
                _viewModel.CurrentOperationTime = _overlayPlayer[0].PlaybackSession.Position;

            // Keep the telemetry HUD live while previewing in Edit — the composite render loop that
            // normally drives it doesn't run here, so without this it froze until you paused.
            if ((DateTime.Now - _lastTelemetryUpdate).TotalMilliseconds >= 100)
            {
                _lastTelemetryUpdate = DateTime.Now;
                UpdateTelemetryOverlay(true);
            }

            // Ensure the WYSIWYG zoom rectangles stay perfectly synced with the video as it animates.
            UpdateWysiwygOverlay();
        }
    }
}
