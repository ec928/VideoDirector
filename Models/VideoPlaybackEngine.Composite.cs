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

// VideoPlaybackEngine - which clip is on which slot at this instant: the dirty flag, the per-frame evaluation, slot activation and release, and transition opacity.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        // Backfill the true source length from the opened media. Covers clips from older projects
        // saved before SourceDuration was captured (their "Source Length" read 0 and trim couldn't
        // clamp to real bounds). Only fills when missing; setting it re-clamps the trim safely.
        private static void BackfillSourceDuration(CinematicOperation op, MediaPlayer player)
        {
            if (op == null || player?.PlaybackSession == null || op.SourceDuration > TimeSpan.Zero) return;
            var natural = player.PlaybackSession.NaturalDuration;
            if (natural > TimeSpan.Zero) op.SourceDuration = natural;
        }
// async Task, not async void. An exception inside an async void cannot be caught by the
        // caller and takes the process down with it - and both of these await real work (seeking a
        // decoder, opening a source) that can fail on a missing or damaged file.
        public async System.Threading.Tasks.Task SeekCompositeToStoryTime(TimeSpan t)
        {
            if (_mode != EditorMode.Arrange) ExitToArrange();
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            _viewModel.CurrentStoryTime = t;
            EvaluateOverlays(t);
        }

        // ==================== Overlay Playback ====================

        private void InitializeOverlayPlayers()
        {
            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                var player = new MediaPlayer
                {
                    IsLoopingEnabled = false,
                    AutoPlay = false
                    // Audio is governed by the per-clip Volume (overlays default to 0 = silent, so
                    // Track 1 stays the audio bed unless a PiP's Volume is raised). Do NOT hard-mute
                    // here: that overrode Volume entirely, so the audio slider did nothing.
                };
                player.CommandManager.IsEnabled = false;
                _overlayPlayer[i] = player;
                _playerControl.OverlayVisuals[i].Video.SetMediaPlayer(player);
            }
        }

        // The generic per-track evaluation (§7B). One loop body, indexed by track — no slot
        // branches. Each track is strict (its clips never overlap), so at most ONE clip is active
        // per track, which is why track i can own exactly one player/surface.
        // ==================== Composite invalidation ====================
        //
        // WHAT IS ON SCREEN IS A FUNCTION OF STATE, NOT A CONSEQUENCE OF REMEMBERING.
        //
        // Re-resolving the composite used to require a call — RefreshComposite from a dozen places,
        // SeekCompositeToStoryTime, or the playback loop. Every path that changed what SHOULD be on
        // screen had to remember to make one, and SelectClip did not: it set SelectedClip and
        // CurrentStoryTime and returned, so selecting a clip moved the playhead and the inspector
        // while the compositor kept showing the clip that was already loaded. The failure is silent
        // — a stale picture, not an error — and the next path added would have repeated it.
        //
        // Now every input that determines the composite just marks it dirty, and one place acts on
        // that. Nothing has to remember anything.
        //
        // Note it is INPUTS that invalidate, not values. Keying this off CurrentStoryTime changing
        // was not enough on its own: SetProperty suppresses the notification when the value is
        // unchanged, and selecting a clip whose start equals the playhead assigns an unchanged
        // value. Selection invalidates because the selection changed, full stop.
        private bool _compositeDirty;
        private bool _compositeFlushScheduled;

        public void Invalidate()
        {
            _compositeDirty = true;
            if (_compositeFlushScheduled) return;

            // Coalesced: a gesture that invalidates twenty times costs one evaluation.
            _compositeFlushScheduled = true;
            _dispatcher.TryEnqueue(FlushComposite);
        }

        private void FlushComposite()
        {
            _compositeFlushScheduled = false;
            if (!_compositeDirty) return;
            _compositeDirty = false;

            // Same guards RefreshComposite always had: Edit mode manages its own surfaces, and
            // while rolling the playback loop already evaluates every frame. Paused counts as
            // Arrange, so a refresh still lands.
            if (IsActivelyPlaying) return;
            if (_mode != EditorMode.Arrange) return;

            EvaluateOverlays(_viewModel.CurrentStoryTime);
        }

        // Guards the CurrentStoryTime handler above from re-entering while an evaluation is
        // already in flight.
        private bool _evaluatingComposite;

        private void EvaluateOverlays(TimeSpan currentStoryTime)
        {
            if (_evaluatingComposite) return;
            _evaluatingComposite = true;
            try
            {
                EvaluateOverlaysCore(currentStoryTime);
            }
            finally { _evaluatingComposite = false; }
        }

        private void EvaluateOverlaysCore(TimeSpan currentStoryTime)
        {
            // EDIT MODE OWNS THE SCREEN. It shows exactly ONE clip full-screen, and it manages the
            // overlay surfaces itself (HideAllOverlays / EnterOverlayEditMode). If we ran here we
            // would paint the other tracks' stills over the clip being edited — three videos
            // instead of one — and could stomp the edit view that was just set up.
            if (_mode != EditorMode.Arrange) return;

            var tracks = _viewModel.Tracks;

            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                var desired = i < tracks.Count ? ResolveActiveClip(tracks[i], currentStoryTime) : null;

                // ---- ARRANGE: a pure-model, video-free path (§7A). Showing a still must NOT go
                // through the video-activation pipeline. It used to, which is why a still only
                // appeared after playing (a player had to be loaded first) and why reshaping could
                // re-attach a surface and go black. Nothing here touches a MediaPlayer.
                // ---- PLAYBACK (and full-screen content edit): the live video pipeline.
                if (_activeOverlay[i] != desired)
                {
                    if (desired != null) ActivateOverlaySlot(i, desired, currentStoryTime);
                    else ReleaseOverlaySlot(i);
                }
                else if (_activeOverlay[i] != null)
                {
                    // Drift correction: re-seek if this track's player drifts > 200ms
                    ApplyOverlayDriftCorrection(i, _activeOverlay[i], currentStoryTime);
                }

                if (_activeOverlay[i] != null)
                {
                    // A still with a baked frame renders as a bitmap; everything else is video.
                    // The render mode is decided once, here, and passed down — the transform path
                    // differs between the two and must not have to guess which surface is live.
                    var clip = _activeOverlay[i];
                    var mode = RenderModeFor(clip);

                    // ...except that "everything else is video" is not true of an image whose bake
                    // has not landed yet. Falling back to the video surface for one shows whatever
                    // the previous clip left in that element — the wrong picture, confidently
                    // presented. Nothing is the honest answer for the frame or two it takes.
                    if (mode == OverlayRender.Video && clip.IsImage)
                    {
                        _ = EnsureStillFrameAsync(clip);
                        SetOverlayRender(i, OverlayRender.Hidden, clip);
                        continue;
                    }

                    SetOverlayRender(i, mode, clip);
                    ApplyTransitionOpacity(i, clip, currentStoryTime);
                    ApplyOverlayTransform(i, clip, currentStoryTime, mode);
                }
                else SetOverlayRender(i, OverlayRender.Hidden, null);
            }

            // AFTER the slots have been resolved, never before.
            //
            // This call used to sit at the top of the method, so the HUD reported the slot contents
            // from the PREVIOUS evaluation against the CURRENT story time. In Arrange there is no
            // per-frame loop, so that is one whole selection behind: select a clip and the readout
            // names the clip that was showing before it, with an into-clip time that cannot exist.
            // Two rounds of diagnosis were spent on numbers this ordering invented.
            WriteGeometryTelemetry();
        }

        // ---- §7A: how an upper-track clip is rendered. Exactly one of these, set explicitly. ----
        //   Hidden — nothing on screen for this track.
        //   Still  — a plain bitmap (the clip's thumbnail). NO MediaPlayer is attached to the
        //            element, so there is no video surface at all: nothing that can blank, green,
        //            or composite over the handles when the box is resized/moved.
        //   Video  — the live MediaPlayerElement (playback, and full-screen content editing).
        private enum OverlayRender { Hidden, Still, Video }

        // "Playing" means actively rolling. PAUSED is not playing: pausing keeps the playback loop
        // alive (_isAnimating stays true), but a paused composite must behave like Arrange — stills
        // with handles that you can move — otherwise pause leaves you unable to arrange anything.
        private bool IsActivelyPlaying => _isAnimating && !_isPaused;

        // Idempotent: safe to call every frame. This is the ONLY place the still/video choice is
        // made — the seven failed attempts all inferred it as a side effect somewhere else.
        private void SetOverlayRender(int track, OverlayRender mode, CinematicOperation clip)
        {
            var v = _playerControl.OverlayVisuals[track];

            // The frame carries the track's identity colour, which is the whole point of
            // TrackPalette: the same hue marks a track's blocks in the timeline and its picture in
            // the composite, so you can tell at a glance which row a box on screen came from. The
            // frame was hardcoded white, so that correlation existed in the palette's comments and
            // nowhere on screen.
            if (v.Frame != null && v.Frame.Children.Count > 0
                && v.Frame.Children[0] is Microsoft.UI.Xaml.Shapes.Rectangle frameRect)
            {
                bool isSelected = clip != null && _viewModel?.SelectedClip == clip;
                var colour = track == 0 ? Views.TrackPalette.Spine : Views.TrackPalette.Overlay(track - 1);

                // Selected reads as solid and heavier; the rest stay dashed and quieter, matching
                // how the keyframe rectangles distinguish the one being worked on.
                frameRect.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    isSelected ? colour : Views.TrackPalette.At(colour, 0xB0));
                frameRect.StrokeThickness = isSelected ? 3 : 2;

                bool dashed = frameRect.StrokeDashArray != null && frameRect.StrokeDashArray.Count > 0;
                if (isSelected && dashed)
                    frameRect.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection();
                else if (!isSelected && !dashed)
                    frameRect.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 4 };
            }

            // Frames belong to Arrange, and only while nothing is rolling: they are a handle for
            // composing, and clutter over a screening. Both render paths use the same rule - the
            // video case used to collapse the frame unconditionally, so a video clip never got one
            // at all and only stills were outlined.
            bool showFrame = !IsActivelyPlaying && _mode == EditorMode.Arrange;

            switch (mode)
            {
                case OverlayRender.Hidden:
                    // The border is outside this grid, so it no longer vanishes for free when the
                    // grid opacity drops to zero. Nothing else clears it.
                    HideBorderRect(track);
                    DetachOverlayVideo(track);
                    ClearStillMotion(track);
                    v.Still.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    v.Still.Source = null;
                    if (v.Frame != null) v.Frame.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    v.Grid.Opacity = 0;
                    break;

                case OverlayRender.Still:
                    DetachOverlayVideo(track);              // the invariant
                    // The frame baked at SOURCE resolution, not the shell thumbnail: the whole
                    // point is that the compositor still has real pixels to sample as the
                    // push-in magnifies. See StillFrameFactory.
                    // Reference-compared so the every-frame call doesn't re-assign the source.
                    if (!ReferenceEquals(v.Still.Source, clip?.StillFrame))
                        v.Still.Source = clip?.StillFrame;
                    v.Still.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    // A frame marks every arrangeable PiP. No drawn handles: reshape grab-zones
                    // are geometric edge/corner bands on the InputLayer, so handles were decoration
                    // that also made chrome depend on a selection you cannot make while arranging.
                    if (v.Frame != null)
                        v.Frame.Visibility = showFrame
                            ? Microsoft.UI.Xaml.Visibility.Visible
                            : Microsoft.UI.Xaml.Visibility.Collapsed;
                    v.Grid.Opacity = clip != null && clip.IsVideoHidden ? 0.0 : (clip?.Opacity ?? 1.0);
                    break;

                case OverlayRender.Video:
                    ClearStillMotion(track);
                    v.Still.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    if (v.Frame != null)
                        v.Frame.Visibility = showFrame
                            ? Microsoft.UI.Xaml.Visibility.Visible
                            : Microsoft.UI.Xaml.Visibility.Collapsed;
                    AttachOverlayVideo(track);
                    v.Grid.Opacity = clip != null && clip.IsVideoHidden ? 0.0 : (clip?.Opacity ?? 1.0);
                    break;
            }
        }


        // A MediaPlayerElement with no MediaPlayer has no video surface to render at all.
        //
        // AND THE PLAYER HAS TO STOP, not just be unhooked. SetMediaPlayer(null) detaches the
        // picture; the MediaPlayer carries on decoding and, more to the point, carries on making
        // noise. Switching from a video to a still on the same track therefore left the outgoing
        // clip's audio playing underneath a silent image - the source is only replaced when the
        // next VIDEO clip loads one, and a still never does.
        private void DetachOverlayVideo(int track)
        {
            var video = _playerControl.OverlayVisuals[track].Video;
            var player = _overlayPlayer[track];
            if (player != null && player.PlaybackSession != null &&
                player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
            {
                player.Pause();
            }

            if (video == null) return;
            if (video.MediaPlayer != null) video.SetMediaPlayer(null);
            video.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private void AttachOverlayVideo(int track)
        {
            var video = _playerControl.OverlayVisuals[track].Video;
            if (video == null) return;
            if (video.MediaPlayer == null) video.SetMediaPlayer(_overlayPlayer[track]);
            video.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }

        // Re-render the Arrange composite from the model (e.g. after a clip is added, removed, or
        // moved in time). Cheap and video-free — it takes the still path in EvaluateOverlays.
        // Kept as the name a dozen call sites already use. It no longer forces an immediate
        // evaluation — it marks the composite dirty and the flush coalesces. Correctness no longer
        // depends on these calls existing at all; they are now belt to Invalidate's braces.
        public void RefreshComposite() => Invalidate();

        // Bake a still's frame up front (e.g. the moment a Snapshot clip is created) so it is
        // ready before the playhead ever reaches it, rather than on first activation.
        public void PrebakeStillFrame(CinematicOperation op) => _ = EnsureStillFrameAsync(op);

        // The overlay clip currently shown in a given track's box (null if none) — used by
        // double-tap-to-edit to know which clip a PiP represents.
        public CinematicOperation GetActiveOverlay(int track)
            => (track >= 0 && track < MaxOverlayTracks) ? _activeOverlay[track] : null;

        // Strict track ⇒ the first clip whose window contains t is the only one.
        // The clip on screen for this track at time t.
        //
        // "First one that covers t" was wrong: it made collection order decide the answer whenever
        // two clips overlapped, and a 1-tick overlap is all it took (see ClipGeometry.Covers). The
        // clip that started LATER is the one the playhead has most recently entered, so it wins -
        // which is also the right answer for a deliberate overlap, not just a rounding one.
        // Fades, applied on top of the clip's own opacity.
        //
        // Only styles that need ONE clip are implemented: a fade out to black at the end, and
        // optionally a fade in from black at the start. A true crossfade needs the outgoing and
        // incoming clips on screen simultaneously, which one slot holding one clip cannot do.
        //
        // Multiplied into the grid opacity that SetOverlayRender just wrote, so a clip already at
        // 35% fades from 35% rather than jumping to full.
        private void ApplyTransitionOpacity(int slot, CinematicOperation clip, TimeSpan now)
        {
            var v = _playerControl.OverlayVisuals[slot];
            if (v?.Grid == null || clip == null) return;

            double f = TransitionFade(clip, now);
            if (f >= 0.999) return;   // nothing to do, and no write on the common path

            double baseOpacity = clip.IsVideoHidden ? 0.0 : clip.Opacity;
            double want = baseOpacity * f;
            if (Math.Abs(v.Grid.Opacity - want) > 0.001) v.Grid.Opacity = want;
        }

        internal static double TransitionFade(CinematicOperation clip, TimeSpan now)
        {
            if (clip == null || clip.TransitionStyle == TransitionStyle.HardSnap) return 1.0;

            double d = clip.TransitionDuration.TotalSeconds;
            double len = clip.OpDuration.TotalSeconds;
            if (d <= 0 || len <= 0) return 1.0;

            // A fade longer than half the clip would never reach full brightness; cap it so the
            // middle of the clip is always the clip.
            d = Math.Min(d, len / 2);

            double into = (now - clip.StartTime).TotalSeconds;
            if (into < 0 || into > len) return 1.0;

            double f = 1.0;
            if (clip.TransitionStyle == TransitionStyle.CinematicBridge && into < d)
                f = Math.Min(f, into / d);

            double left = len - into;
            if (left < d) f = Math.Min(f, left / d);

            return Math.Clamp(f, 0.0, 1.0);
        }

        private static CinematicOperation ResolveActiveClip(TimelineTrack track, TimeSpan t)
        {
            CinematicOperation best = null;
            foreach (var clip in track.Clips)
            {
                if (!ClipGeometry.Covers(clip.StartTime.Ticks, clip.OpDuration.Ticks, t.Ticks)) continue;
                if (best == null || ClipGeometry.Supersedes(clip.StartTime.Ticks, best.StartTime.Ticks))
                    best = clip;
            }
            return best;
        }

        private void ActivateOverlaySlot(int slot, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            var player = _overlayPlayer[slot];
            var grid = _playerControl.OverlayVisuals[slot].Grid;

            // Mark active immediately so repeated per-frame EvaluateOverlays ticks don't
            // re-trigger this while the media is still opening asynchronously.
            _activeOverlay[slot] = overlay;

            grid.Opacity = overlay.IsVideoHidden ? 0.0 : overlay.Opacity;

            // Stills render from a frame baked at source resolution rather than from a parked
            // video surface. Idempotent, so kicking it off on every activation is free once done.
            if (overlay.IsStill) _ = EnsureStillFrameAsync(overlay);

            // A still whose frame is already baked needs no decoder at all — skip the media open
            // outright. That also keeps it out of the Opening/Buffering clock stall in the tick,
            // which would otherwise freeze story time for every track while this one loads.
            if (overlay.IsStill && overlay.StillFrame != null && overlay.SourceAspect > 0)
            {
                player.Pause();
                _overlayAspect[slot] = overlay.SourceAspect;
                ApplyOverlayBox(slot, overlay, false);
                return;
            }

            // An IMAGE never goes near the decoder, baked or not. It used to fall through to the
            // block below and set player.Source to a .jpg, which Media Foundation cannot open — so
            // the element kept presenting the PREVIOUS clip's last decoded frame and the timeline
            // appeared to show the wrong clip entirely. The bake above is already in flight; until
            // it lands the slot simply shows nothing (see EvaluateOverlays).
            if (overlay.IsImage)
            {
                player.Pause();
                if (overlay.SourceAspect > 0) _overlayAspect[slot] = overlay.SourceAspect;
                ApplyOverlayBox(slot, overlay, false);
                return;
            }

            bool needsNewSource = player.Source == null ||
                !string.Equals((player.Source as MediaSource)?.Uri?.LocalPath, overlay.FilePath, StringComparison.OrdinalIgnoreCase);

            if (needsNewSource)
            {
                System.Threading.Interlocked.Increment(ref _pendingMediaOpens);

                void OnOpened(MediaPlayer sender, object args)
                {
                    sender.MediaOpened -= OnOpened;
                    System.Threading.Interlocked.Decrement(ref _pendingMediaOpens);

                    // The overlay this slot wants may have changed while we were waiting
                    // (e.g. playback moved past it, or it got released) — bail if so.
                    var currentSlotOverlay = _activeOverlay[slot];
                    if (currentSlotOverlay != overlay) return;

                    SeekAndPlayOverlay(sender, overlay, _viewModel.CurrentStoryTime);
                    _dispatcher.TryEnqueue(() =>
                    {
                        CacheOverlayAspect(slot, sender);
                        ApplyOverlayBox(slot, overlay, false);
                    });
                }

                void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
                {
                    sender.MediaOpened -= OnOpened;
                    sender.MediaFailed -= OnFailed;
                    System.Threading.Interlocked.Decrement(ref _pendingMediaOpens);
                }

                player.MediaOpened += OnOpened;
                player.MediaFailed += OnFailed;
                player.Source = MediaSource.CreateFromUri(new Uri(overlay.FilePath));
            }
            else
            {
                // Source is already correct and open (e.g. re-entering this slot for the same
                // clip) — safe to seek immediately.
                SeekAndPlayOverlay(player, overlay, currentStoryTime);
                CacheOverlayAspect(slot, player);
                ApplyOverlayBox(slot, overlay, false);
            }
        }

        // Caches the overlay video's native aspect (w/h) for the slot, read once the media
        // has opened. Used to shape the placement box to the video (no black bars).
        private void CacheOverlayAspect(int slot, MediaPlayer player)
        {
            if (player?.PlaybackSession == null) return;
            uint vw = player.PlaybackSession.NaturalVideoWidth;
            uint vh = player.PlaybackSession.NaturalVideoHeight;
            if (vw == 0 || vh == 0) return;
            double aspect = (double)vw / vh;
            _overlayAspect[slot] = aspect;
            // Backfill the clip so Arrange can shape its box correctly without loading video
            // (covers clips from older projects that were saved before SourceAspect existed).
            var active = _activeOverlay[slot];
            if (active != null && active.SourceAspect <= 0) active.SourceAspect = aspect;
        }
    }
}
