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

// VideoPlaybackEngine - moving a slot through time and space: seeking, the Ken Burns transform, still-frame motion, and drift correction.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        private void SeekAndPlayOverlay(MediaPlayer player, int slot, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            if (player.PlaybackSession == null) return;

            // Per-clip speed applies to overlays just like Track 1: the source advances `clipSpeed`
            // per story-second (0 = a still, frozen at the in-point), and the player runs at
            // clipSpeed * global so real-time playback matches.
            double clipSpeed = overlay.PlaybackSpeed;
            double advance = clipSpeed <= 0 ? 0 : clipSpeed;
            TimeSpan offsetIntoOverlay = currentStoryTime - overlay.StartTime;
            if (offsetIntoOverlay < TimeSpan.Zero) offsetIntoOverlay = TimeSpan.Zero;
            TimeSpan targetPosition = overlay.VideoStartTime + TimeSpan.FromSeconds(offsetIntoOverlay.TotalSeconds * advance);

            // The overlay's on-screen Duration is independent of the source clip's actual
            // length — if Duration outlasts the media, hold on the last frame instead of
            // seeking past end-of-media (which the player can't reach).
            bool pastEnd = TryClampToMediaLength(player, ref targetPosition);

            // No lead here, deliberately. The drift path aims ahead to cover the ~250ms a seek
            // takes to land, and that works while the clip is already running. At a fresh
            // activation the player does not advance at all until the seek settles, so aiming
            // ahead overshoots and then oscillates — measured as three startup corrections
            // instead of two. MarkSeekIssued still runs so drift correction leaves this seek
            // alone until it has actually landed.
            MarkSeekIssued(slot);
            player.PlaybackSession.Position = targetPosition;

            double combinedSpeed = clipSpeed * _viewModel.PlaybackSpeed;
            if (pastEnd || combinedSpeed <= 0)
            {
                player.Pause();   // held frame, or a still (speed 0)
            }
            else if (_isAnimating && !_isPaused)
            {
                player.PlaybackSession.PlaybackRate = combinedSpeed;
                player.Volume = overlay.Volume;
                player.Play();
            }
            else
            {
                player.Pause();
            }
        }

        // Clamps a target seek position to the media's actual playable length. Returns true
        // if the target was past end-of-media (i.e. the caller should hold, not keep seeking).
        private bool TryClampToMediaLength(MediaPlayer player, ref TimeSpan targetPosition)
        {
            var natural = player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
            if (natural <= TimeSpan.Zero || targetPosition < natural) return false;

            var holdPosition = natural - TimeSpan.FromMilliseconds(50);
            targetPosition = holdPosition > TimeSpan.Zero ? holdPosition : TimeSpan.Zero;
            return true;
        }

        private void ReleaseOverlaySlot(int slot)
        {
            var player = _overlayPlayer[slot];
            var grid = _playerControl.OverlayVisuals[slot].Grid;

            player.Pause();
            player.Source = null; // Release GPU decode pipeline
            SetOverlayRender(slot, OverlayRender.Hidden, null);
            grid.Opacity = 0;

            // Reset content transform + clear the placement box so no stale size/clip lingers.
            // (The still surface is reset by SetOverlayRender's Hidden case above.)
            var transform = _playerControl.OverlayVisuals[slot].Transform;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            grid.ClearValue(Microsoft.UI.Xaml.FrameworkElement.WidthProperty);
            grid.ClearValue(Microsoft.UI.Xaml.FrameworkElement.HeightProperty);
            var vis = _playerControl.OverlayVisuals[slot];
            vis.Video?.ClearValue(Microsoft.UI.Xaml.FrameworkElement.WidthProperty);
            vis.Video?.ClearValue(Microsoft.UI.Xaml.FrameworkElement.HeightProperty);
            vis.Still?.ClearValue(Microsoft.UI.Xaml.FrameworkElement.WidthProperty);
            vis.Still?.ClearValue(Microsoft.UI.Xaml.FrameworkElement.HeightProperty);
            // The surfaces now sit in a Canvas, so their offset is state too - a released slot that
            // kept a previous clip's Canvas.Left would draw the next one off-centre for a frame.
            if (vis.Video != null) { Microsoft.UI.Xaml.Controls.Canvas.SetLeft(vis.Video, 0); Microsoft.UI.Xaml.Controls.Canvas.SetTop(vis.Video, 0); }
            if (vis.Still != null) { Microsoft.UI.Xaml.Controls.Canvas.SetLeft(vis.Still, 0); Microsoft.UI.Xaml.Controls.Canvas.SetTop(vis.Still, 0); }
            grid.Clip = null;
            grid.Margin = new Microsoft.UI.Xaml.Thickness(0);
            // The frame is outside this grid, so it no longer vanishes when the opacity drops.
            HideFrameRect(slot);

            _activeOverlay[slot] = null;
            _overlayAspect[slot] = 0;
            _overlayContentAspect[slot] = 0;
        }

        // Which surface a clip renders on. A still renders as a bitmap once its frame has been
        // baked at source resolution; until then (and for every video) the MediaPlayerElement is
        // still the surface, so a clip is never blank while a decode is in flight.
        private static OverlayRender RenderModeFor(CinematicOperation clip)
        {
            if (clip == null) return OverlayRender.Video;

            // Sound first: an audio-only clip is never a still and never has a picture, so both of
            // the other tests would answer wrongly for it.
            if (clip.IsAudioOnly) return OverlayRender.Sound;

            return clip.IsStill && clip.StillFrame != null ? OverlayRender.Still : OverlayRender.Video;
        }

        private void ApplyOverlayTransform(int slot, CinematicOperation overlay, TimeSpan currentStoryTime, OverlayRender mode)
        {
            // First draw of a legacy clip is where its marks get converted to the normalised space.
            EnsureMarksNormalized(overlay);

            // Placement box FIRST. The still's motion is centred on the box, so its size has to be
            // settled before a centre point can be derived from it; the old order (marks, then box)
            // seeded the first frame of a ramp against a stale size.
            //
            // And if the box could NOT be established, do not transform. The return value used to
            // be absent and the box's early-out silent, so during a slot's activation window this
            // wrote a zoom/pan sized for one rectangle onto whatever the last clip left behind.
            // Parking at identity shows the clip un-framed for the frame or two until geometry
            // lands, which is a neutral picture rather than a mostly-black one.
            // EDIT MODE IS STATE, NOT A LITERAL. Entering Edit sets this slot to the full-fit box
            // via ApplyOverlayBox(0, overlay, true) - and then this line, which runs EVERY FRAME,
            // put the placement box straight back. On a 30% overlay the picture was confined to a
            // 30% window from the next frame onward, at any zoom: the telemetry showed "laid out
            // grid 2752 x 1146, want 2752 x 1147" - the geometry was right and being overwritten.
            // That is the clip window that appeared to cut the picture off.
            bool editingThisClip = _mode == EditorMode.Edit && ReferenceEquals(overlay, _editClip);

            if (!ApplyOverlayBox(slot, overlay, editingThisClip))
            {
                if (mode == OverlayRender.Still)
                    KenBurnsMotion.Reset(_playerControl.OverlayVisuals[slot].StillTransform);
                else
                    KenBurnsMotion.Reset(_playerControl.OverlayVisuals[slot].Transform);
                return;
            }

            // Content framing interpolated over the overlay's OWN duration (Ken Burns / push-in),
            // using the same marks + curve as Track 1. Static clip = StartMark == EndMark.
            double rawProgress = overlay.OpDuration.TotalMilliseconds > 0
                ? (currentStoryTime - overlay.StartTime).TotalMilliseconds / overlay.OpDuration.TotalMilliseconds
                : 0;

            // Marks are fractions of the video fit; the transform wants pixels of the PiP box.
            // fit * PanScale is that conversion — see KenBurnsMotion.PanScale for why the ratio
            // is a single uniform number rather than one per axis.
            //
            // The box above succeeded, so this resolves the same aspect and cannot disagree with
            // it. The guard is here because the return value was previously discarded: a false
            // silently gave fitW/fitH of 0, which zeroed the translate while leaving the scale
            // applied — a centred zoom instead of the framing that was authored.
            if (!TryGetMarkSpace(overlay, out double fitW, out double fitH)) return;
            double pan = KenBurnsMotion.PanScale(overlay);
            double panX = fitW * pan;
            double panY = fitH * pan;

            if (mode == OverlayRender.Still)
            {
                DriveStillMotion(slot, overlay, rawProgress, panX, panY);
                return;
            }

            // Video: the XAML transform on the MediaPlayerElement, written per frame.
            ClearStillMotion(slot);
            ApplyMarksAtProgress(overlay, rawProgress, _playerControl.OverlayVisuals[slot].Transform,
                                 panX, panY);
        }

        // Hands a still's push-in to the compositor once per run instead of writing a transform
        // every frame. Restarts only when something actually invalidates the running ramp, so a
        // clip that plays straight through gets exactly one handover.
        private void DriveStillMotion(int slot, CinematicOperation overlay, double rawProgress, double panX, double panY)
        {
            var stillT = _playerControl.OverlayVisuals[slot].StillTransform;

            KenBurnsMotion.Apply(stillT, overlay, rawProgress, panX, panY);
            _stillMotionOwned[slot] = true;
        }

        private void ClearStillMotion(int slot)
        {
            if (!_stillMotionOwned[slot]) return;

            KenBurnsMotion.Reset(_playerControl.OverlayVisuals[slot].StillTransform);
            _stillMotionOwned[slot] = false;
        }

        // Bakes a still's frozen frame at source resolution, once per (file, freeze point).
        // Idempotent and fire-and-forget: the clip keeps rendering on its video surface until the
        // frame lands, then flips to the bitmap on the next evaluation.
        private async Task EnsureStillFrameAsync(CinematicOperation op)
        {
            // Nothing to bake from a file with no picture; the decoder refuses it outright.
            if (op != null && op.IsAudioOnly) return;

            if (op == null || !op.IsStill || string.IsNullOrWhiteSpace(op.FilePath)) return;

            string key = op.StillFrameId;
            if (op.StillFrame != null && op.StillFrameKey == key) return;
            if (op.StillFramePending) return;

            op.StillFramePending = true;
            try
            {
                var frame = await StillFrameFactory.ExtractAsync(op.FilePath, op.VideoStartTime);
                if (frame == null) return;

                // The freeze point may have been retrimmed while we were decoding — only publish
                // a frame that still matches what the clip is asking for.
                if (op.StillFrameId != key) return;

                op.StillFrame = frame;
                op.StillFrameKey = key;

                // Last line of defence for the aspect, and the only one that reaches clips already
                // saved in a project. An image never opens a decoder, so CacheOverlayAspect can
                // never backfill it the way it does for video — and a clip with no aspect lays out
                // no box and draws nothing at all. The decoded bitmap knows its own dimensions, so
                // take them from there whenever the clip arrived without any.
                if (op.SourceAspect <= 0 && frame.PixelWidth > 0 && frame.PixelHeight > 0)
                    op.SourceAspect = (double)frame.PixelWidth / frame.PixelHeight;

                // A clip sitting under the playhead right now is showing its video surface; nudge
                // the composite so it picks up the bitmap without waiting for the next transition.
                _dispatcher.TryEnqueue(RefreshComposite);
            }
            catch
            {
                // Unreadable source, or no decoder for this container — the video surface stays
                // as the fallback and the still simply behaves as it did before.
            }
            finally { op.StillFramePending = false; }
        }

        // Applies a clip's Start/Mid/End marks to a transform at the given progress (0..1),
        // eased by the clip's CurveProfile. Shared by Track 1 (UpdateSpatial) and upper-track
        // overlay content so motion behaves identically on every track.
        private void ApplyMarksAtProgress(CinematicOperation op, double rawProgress,
                                          Microsoft.UI.Xaml.Media.CompositeTransform transform,
                                          double panScaleX = 1.0, double panScaleY = 1.0)
        {
            if (op == null || transform == null) return;

            // Delegates rather than duplicating: the still path and this one have to agree exactly,
            // or a clip would reframe as it flipped between the bitmap and the video surface.
            KenBurnsMotion.Evaluate(op, rawProgress, panScaleX, panScaleY,
                                    out double scale, out double tx, out double ty);

            transform.ScaleX = scale;
            transform.ScaleY = scale;
            transform.TranslateX = tx;
            transform.TranslateY = ty;
        }

        private void ApplyOverlayDriftCorrection(int slot, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            var player = _overlayPlayer[slot];
            if (overlay.IsStill || player.PlaybackSession == null) return;

            // Match SeekAndPlayOverlay: source advances at the clip's own speed.
            double clipSpeed = overlay.PlaybackSpeed;
            double advance = clipSpeed <= 0 ? 0 : clipSpeed;
            TimeSpan into = currentStoryTime - overlay.StartTime;
            if (into < TimeSpan.Zero) into = TimeSpan.Zero;
            TimeSpan expectedPosition = overlay.VideoStartTime + TimeSpan.FromSeconds(into.TotalSeconds * advance);

            if (TryClampToMediaLength(player, ref expectedPosition))
            {
                // Past end-of-media — hold the last frame instead of chasing an unreachable
                // position every frame (this was the cause of visible stutter).
                if (player.PlaybackSession.Position < expectedPosition)
                {
                    player.PlaybackSession.Position = expectedPosition;
                }
                player.Pause();
                return;
            }

            TimeSpan actualPosition = player.PlaybackSession.Position;
            TimeSpan drift = (expectedPosition - actualPosition).Duration();

            // Sound-only clips are corrected far more reluctantly. A seek in a picture is a frame
            // you may not even notice; a seek in audio is an audible click, and the tolerances
            // below would produce one whenever the transport is not animating. A music bed running
            // a fraction of a second free is better than one that keeps being nudged.
            bool soundOnly = overlay.IsAudioOnly;
            var loose = TimeSpan.FromMilliseconds(soundOnly ? 750 : 200);
            var tight = TimeSpan.FromMilliseconds(soundOnly ? 750 : 10);

            // Never stack a correction on top of one that has not landed yet. The player still
            // reports its pre-seek position while a seek is settling, so the drift measured above
            // is the drift we are already fixing — acting on it again seeks a second time, and on
            // the slot carrying audio every extra seek is an audible break.
            if ((drift > loose || (!_isAnimating && drift > tight) || (_isPaused && drift > tight))
                && !SeekSettling(slot))
            {
                // Aim at where the clip should be when the seek LANDS, not where it should be now.
                // A seek takes long enough that a correction aimed at "now" arrives already ~200ms
                // stale — which trips this same threshold again and fires a second seek a quarter
                // second later. One seek is inaudible; the second one is the stutter. Only the
                // running clock needs this: paused, the target is not moving.
                TimeSpan seekTarget = expectedPosition;
                if (_isAnimating && !_isPaused)
                {
                    double runRate = clipSpeed * _viewModel.PlaybackSpeed;
                    if (runRate > 0)
                    {
                        seekTarget += TimeSpan.FromSeconds(SeekLatencySeconds(slot) * runRate);
                        // Compensation must never push the target past end-of-media.
                        TryClampToMediaLength(player, ref seekTarget);
                    }
                }

                TraceEvent("DRIFT-SEEK   slot=" + slot + " vol=" + overlay.Volume
                           + " drift=" + (long)drift.TotalMilliseconds + "ms lead="
                           + (long)(seekTarget - expectedPosition).TotalMilliseconds + "ms");
                MarkSeekIssued(slot);
                player.PlaybackSession.Position = seekTarget;
            }

            // We're back in-bounds (not past end-of-media) — make sure the player is actually
            // playing. Without this, a transient overshoot that triggered the past-end-of-media
            // Pause() above on some earlier frame would leave the overlay frozen forever, since
            // nothing else in this correction path ever resumes it.
            double combinedSpeed = clipSpeed * _viewModel.PlaybackSpeed;
            if (_isAnimating && !_isPaused && combinedSpeed > 0)
            {
                if (player.PlaybackSession.PlaybackRate != combinedSpeed)
                {
                    player.PlaybackSession.PlaybackRate = combinedSpeed;
                }
                double effectiveVolume = overlay.Volume;
                if (player.Volume != effectiveVolume) player.Volume = effectiveVolume;
                if (player.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing)
                {
                    // Starting is not instant either, and Position lags while the pipeline spins
                    // up - measured as 200ms of apparent drift within 130ms of pressing play, far
                    // faster than drift can actually accumulate. Correcting against that reading
                    // seeks a player that was never out of sync. Hold corrections off the same way
                    // a seek does until it has had time to report honestly.
                    MarkSeekIssued(slot);
                    player.Play();
                }
            }
        }

        private void HideAllOverlays()
        {
            // While a Track 2 clip is being content-edited full-screen, slot 1 is the edit
            // surface — don't let a StopPlayback teardown wipe it.
            // Track 0 is the edit surface while a Track 2+ clip is being content-edited — don't
            // let a StopPlayback teardown wipe it. All other tracks always release.
            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                if (i == 0 && _isEditingOverlay) continue;
                if (_activeOverlay[i] != null) ReleaseOverlaySlot(i);
            }
        }
    }
}
