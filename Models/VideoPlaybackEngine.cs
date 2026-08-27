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

// VideoPlaybackEngine - state, construction, and the transport: play, pause, stop, and the per-frame tick that drives everything else.

namespace VideoDirector.Models
{
    public partial class VideoPlaybackEngine
    {
        private readonly Views.DirectorPlayerControl _playerControl;
        private bool _isPlaybackLoopRunning;
        private TimeSpan _lastTickTime = TimeSpan.Zero;
        private readonly System.Diagnostics.Stopwatch _editPreviewClock = new();
        private readonly MediaPlayer[] _overlayPlayer = new MediaPlayer[MaxOverlayTracks];
        // Previous source for this slot, kept open. A silent clip in the middle of a track
        // otherwise tears down the audible file, and looping back has to reopen it — Test7 seeks
        // an hour into an mkv every round-trip and stutters. All-sound timelines stay on one
        // decoder and loop clean; this makes the mixed case behave the same.
        private readonly MediaPlayer[] _overlayHold = new MediaPlayer[MaxOverlayTracks];
        private readonly CinematicOperation[] _activeOverlay = new CinematicOperation[MaxOverlayTracks];
        private readonly double[] _overlayAspect = new double[MaxOverlayTracks];

        // The aspect each slot's CONTENT surface was last sized against. Content sizing used to be
        // guarded on the box dimensions alone, so a clip that happened to land on the same box size
        // as the outgoing one inherited the outgoing one's contentW/contentH — wrong aspect, and
        // UniformToFill goes back to discarding picture. Sizing is keyed on this as well.
        private readonly double[] _overlayContentAspect = new double[MaxOverlayTracks];

        // Whether this slot's still surface currently carries any framing at all (animated or
        // parked). Lets the video path skip a reset it doesn't need — SetOverlayRender runs every
        // frame, and an unconditional reset would touch four visuals per tick for nothing.
        private readonly bool[] _stillMotionOwned = new bool[MaxOverlayTracks];

        private bool _isEditingOverlay = false;
        private TimeSpan _storyTimeAtClipStart = TimeSpan.Zero;
        private CinematicOperation _editClip;


        private readonly DirectorViewModel _viewModel;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
        private bool _isPaused = false;
        private bool _isAnimating = false;
        public enum EditorMode { Arrange, Edit }
        private EditorMode _mode = EditorMode.Arrange;
        private int _pendingMediaOpens = 0;

        // Frame number, so a diagnostic can say which frame something happened on.
        private long _frameSeq;

        // STARTUP TRACE - measures the UI thread, because that is what stutters.
        //
        // Ticks come from CompositionTarget.Rendering, so the interval between them IS the frame
        // time. A steady 7ms with occasional 160ms holes means the thread was blocked, and a blocked
        // UI thread starves whatever else needs it. Recording every tick makes the holes and what
        // caused them line up on the same timeline.
        //
        // Self-limiting: collects for four seconds from the start of playback, writes once, then
        // costs one bool test per frame. Reads state, never changes it. Enabled by VD_TRACE so a
        // normal run pays nothing.
        private static readonly bool TraceEnabled =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VD_TRACE"));
        private readonly System.Collections.Generic.List<string> _trace = new();
        private System.Diagnostics.Stopwatch _traceClock;
        private bool _traceWritten;
        private int _traceTick;
        private long _traceLastMs;

        // Long enough to cover several loops: the stutter recurs every few passes, not only on the
        // first, so a four second window could not see the pattern at all.
        private static readonly int TraceMs =
            int.TryParse(Environment.GetEnvironmentVariable("VD_TRACE_MS"), out var _ms) ? _ms : 25000;

        private bool Tracing => TraceEnabled && _traceClock != null && !_traceWritten
                                && _traceClock.ElapsedMilliseconds <= TraceMs;

        private int _gc0, _gc1, _gc2;
        private long _allocLast;

        private void TraceBegin()
        {
            if (!TraceEnabled) return;
            _trace.Clear();
            _traceWritten = false;
            _traceTick = 0;
            _traceLastMs = 0;
            _traceClock = System.Diagnostics.Stopwatch.StartNew();
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _trace.Add("ms	tick	gapMs	event");
        }

        // Buffering/seek callbacks arrive on the media pipeline's thread, not the UI thread, so
        // every writer to _trace has to take the same lock or the list tears under load.
        internal void TraceEvent(string what)
        {
            if (!Tracing) return;
            lock (_trace)
                _trace.Add(string.Join("	", _traceClock.ElapsedMilliseconds, _traceTick, "", what));
        }

        private void TraceTick()
        {
            if (!Tracing) { TraceFlush(); return; }
            lock (_trace) {
            _traceTick++;
            long ms = _traceClock.ElapsedMilliseconds;

            // A collection between two ticks blocks the thread, and a periodic stutter is exactly
            // what allocation pressure looks like. Recorded per gen so the cost is attributable.
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            if (g0 != _gc0 || g1 != _gc1 || g2 != _gc2)
            {
                _trace.Add(string.Join("	", ms, _traceTick, "",
                    "GC gen0+" + (g0 - _gc0) + " gen1+" + (g1 - _gc1) + " gen2+" + (g2 - _gc2)
                    + " heap=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MB"));
                _gc0 = g0; _gc1 = g1; _gc2 = g2;
            }

            // How hard this thread is allocating. A full collection every couple of seconds has to
            // be fed by something, and the per-frame render path is the candidate.
            long alloc = GC.GetAllocatedBytesForCurrentThread();
            if (_allocLast > 0 && _traceTick % 60 == 0)
                _trace.Add(string.Join("	", ms, _traceTick, "",
                    "ALLOC " + ((alloc - _allocLast) / 1024) + "KB over 60 frames"));
            if (_traceTick % 60 == 0) _allocLast = alloc;
            if (_allocLast == 0) _allocLast = alloc;

            _trace.Add(string.Join("	", ms, _traceTick, ms - _traceLastMs, ""));
            _traceLastMs = ms;
            }
        }

        private void TraceFlush()
        {
            if (!TraceEnabled || _traceClock == null || _traceWritten) return;
            if (_traceClock.ElapsedMilliseconds <= TraceMs) return;
            _traceWritten = true;
            try
            {
                System.IO.File.WriteAllLines(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vd-trace.log"), _trace);
            }
            catch { }   // a diagnostic must never take the app down
        }
        public CinematicOperation? CurrentPlayingOperation { get; private set; }
        private const int MaxOverlayTracks = DirectorViewModel.MaxTracks;

        public VideoPlaybackEngine(Views.DirectorPlayerControl playerControl, DirectorViewModel viewModel)
        {
            _playerControl = playerControl;
            _viewModel = viewModel;
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            
            InitializeOverlayPlayers();

            _viewModel.PlaybackSpeedChanged += ViewModel_PlaybackSpeedChanged;
            _viewModel.OperationSeekRequested += ViewModel_OperationSeekRequested;
            
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.Tracks.CollectionChanged += (s, e) => OnTimelineSequenceChanged();

            // Arrange mode: drag/wheel the PiP under the cursor.
            _playerControl.OverlayBoxDragged += OnOverlayBoxDragged;
            _playerControl.OverlayBoxReleased += OnOverlayBoxReleased;
            _playerControl.WysiwygBoxManipulated += OnWysiwygBoxManipulated;
            _playerControl.WysiwygBoxGrabbed += OnWysiwygBoxGrabbed;
            _playerControl.CanvasCleared += (s, e) => SetSelectedMark(null);
            _playerControl.OverlayBoxWheel += OnOverlayBoxWheel;
            _playerControl.OverlayBoxPointerPressed += OnOverlayBoxPointerPressed;
            _playerControl.PipSizeRequested += OnPipSizeRequested;
            _playerControl.LayoutRequested += OnLayoutRequested;
            _playerControl.EditClipRequested += OnEditClipRequested;
            _playerControl.BorderTypeRequested += OnBorderTypeRequested;
            _playerControl.BorderColorRequested += OnBorderColorRequested;
            _playerControl.BorderThicknessRequested += OnBorderThicknessRequested;
            _playerControl.OpacityRequested += OnOpacityRequested;
            
            _playerControl.HideRequested += OnHideRequested;
            _playerControl.LockRequested += OnLockRequested;
            _playerControl.ContextMenuOpening += OnContextMenuOpening;

            // Start in Arrange (the default mode) — PiP input active.
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
        }

        private void OnContextMenuOpening(object? sender, int slot)
        {
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                _playerControl.UpdateBorderMenuState(overlay.BorderType, overlay.BorderColor, overlay.BorderThickness);
                _playerControl.UpdateOpacityMenuState(overlay.Opacity);
            }
        }

        private void ViewModel_OperationSeekRequested(object? sender, TimeSpan e)
        {
            SeekActiveOperation(e);
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectorViewModel.IsTelemetryVisible))
            {
                _dispatcher.TryEnqueue(() => UpdateTelemetryOverlay());
            }
            else if (e.PropertyName == nameof(DirectorViewModel.TotalStoryTime))
            {
                OnTimelineSequenceChanged();
            }
            else if (e.PropertyName == nameof(DirectorViewModel.CurrentStoryTime)
                  || e.PropertyName == nameof(DirectorViewModel.SelectedClip)
                  || e.PropertyName == nameof(DirectorViewModel.HasSelection)
                  || e.PropertyName == nameof(DirectorViewModel.IsEditMode)
                  || e.PropertyName == nameof(DirectorViewModel.IsPlaying))
            {
                // Every input the composite depends on. See Invalidate.
                Invalidate();
            }
        }

        private void OnTimelineSequenceChanged()
        {
            if (_isPaused)
            {
                // If paused when the timeline sequence changes, stop the stale playback loop.
                // Clicking Play later will start a clean loop from the current playhead position.
                StopPlayback();
                _isPaused = false;
                _viewModel.IsPlaying = false;
            }
            else if (_isAnimating)
            {
                _ = StartPlaybackAsync();
            }
        }

        private void ViewModel_PlaybackSpeedChanged(object? sender, double speed)
        {
            if (_isPaused) return;

            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                if (_overlayPlayer[i]?.PlaybackSession != null)
                {
                    double trackSpeed = speed;
                    if (_activeOverlay[i] != null) trackSpeed *= _activeOverlay[i].PlaybackSpeed;
                    
                    if (trackSpeed == 0) _overlayPlayer[i].Pause();
                    else
                    {
                        _overlayPlayer[i].PlaybackSession.PlaybackRate = trackSpeed;
                        _overlayPlayer[i].Play();
                    }
                }
            }
        }
        public void SeekActiveOperation(TimeSpan position)
        {
            if (_mode == EditorMode.Edit && _overlayPlayer[0]?.PlaybackSession != null)
            {
                _overlayPlayer[0].PlaybackSession.Position = position;
            }
        }

        // A paused MediaPlayer that was just seeked or freshly attached (the overlay edit surface is
        // attached on demand) often shows no frame until it plays. StepForwardOneFrame forces the
        // current frame to decode and display while staying paused — otherwise: blank preview.
        public async Task TogglePlayPauseAsync()
        {
            if (!_isPlaybackLoopRunning)
            {
                await StartPlaybackAsync();
                return;
            }

            if (_isPaused)
                ResumePlayback();
            else
                PausePlayback();
        }

        private void PausePlayback()
        {
            _isPaused = true;
            _viewModel.IsPlaying = false;
            
            for (int i = 0; i < MaxOverlayTracks; i++) _overlayPlayer[i]?.Pause();

            _dispatcher.TryEnqueue(() =>
            {
                UpdateWysiwygOverlay();
                RefreshComposite();
            });
        }

        private void ResumePlayback()
        {
            _isPaused = false;
            _lastTickTime = TimeSpan.Zero;
            _viewModel.IsPlaying = true;
            
            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                if (_activeOverlay[i] == null || _overlayPlayer[i]?.PlaybackSession == null) continue;
                double combined = _activeOverlay[i].PlaybackSpeed * _viewModel.PlaybackSpeed;
                if (combined <= 0)
                {
                    _overlayPlayer[i].Pause();
                    continue;
                }
                _overlayPlayer[i].PlaybackSession.PlaybackRate = combined;
                _overlayPlayer[i].Volume = _activeOverlay[i].Volume;
                _overlayPlayer[i].Play();
            }
        }

        public async Task StartPlaybackAsync()
        {
            if (System.Linq.Enumerable.All(_viewModel.Tracks, t => t.Clips.Count == 0)) return;

            _isEditingOverlay = false;
            _mode = EditorMode.Arrange;
            _editClip = null;
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
            _viewModel.IsEditMode = false;
            StopEditPreview();
            StopPlayback();

            _viewModel.IsPlaying = true;
            _isPaused = false;

            // NOT animating yet. Loading a source activates its slot, and an activation while the
            // transport is running presses play — so the players used to start rolling partway
            // through the preload and were already ahead of the story clock by the time it
            // started, which drift correction then jumped. Staying stopped until the clock is
            // about to start parks each slot on its in-point instead. Set true below.
            _isAnimating = false;
            
            for (int i = 0; i < MaxOverlayTracks; i++) _activeOverlay[i] = null;

            TraceBegin();

            // BEFORE THE CLOCK, not during it. The loading has to be finished by the time the
            // transport starts, or the work lands in the middle of playback - which is the whole
            // point of doing it here.
            await PreloadActiveSourcesAsync(_viewModel.CurrentStoryTime);
            await PrimeActiveSlotsAsync();

            _isAnimating = true;   // clock and players start together

            if (!_isPlaybackLoopRunning)
            {
                _isPlaybackLoopRunning = true;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += PlaybackTimer_Tick;
            }
            _lastTickTime = TimeSpan.Zero;
        }

        /// <summary>Put every slot on its first frame before the story clock starts.</summary>
        /// <remarks>
        /// Opening a source is not the same as being ready to play it. A seek costs 130-340ms to
        /// land, and the player does not advance while it settles - so a slot activated on the
        /// first tick starts a quarter second behind the clock and stays there until drift
        /// correction jumps it, roughly half a second in. That jump is audible on whichever slot
        /// carries the audio, and it always lands just after the track starts.
        ///
        /// Priming with the transport stopped avoids it: SeekAndPlayOverlay parks a slot rather
        /// than playing it while _isAnimating is false, so each player settles onto its in-point
        /// and waits there. The first tick then finds zero drift and simply presses play (see the
        /// resume at the end of ApplyOverlayDriftCorrection).
        /// </remarks>
        private async Task PrimeActiveSlotsAsync()
        {
            EvaluateOverlays(_viewModel.CurrentStoryTime);   // transport is stopped: parks, not plays

            // Bounded like the preload wait: a seek that never reports back must not hold up the
            // transport. 600ms clears the worst latency measured with room to spare.
            var settleClock = System.Diagnostics.Stopwatch.StartNew();
            while (settleClock.ElapsedMilliseconds < 600)
            {
                bool settling = false;
                for (int i = 0; i < MaxOverlayTracks; i++)
                    if (SeekSettling(i)) { settling = true; break; }
                if (!settling) break;
                await Task.Delay(15);
            }
            TraceEvent("PRIMED after " + settleClock.ElapsedMilliseconds + "ms");
        }
        /// <summary>Open every source the playhead needs before playback begins.</summary>
        /// <remarks>
        /// StartPlaybackAsync clears every slot, so each clip at the playhead used to open during
        /// the first frames of playback - and the work at COMPLETION (seek, cache the aspect,
        /// re-lay out) is what blocks the UI thread. Measured on 0-Test8 with trace-startup.ps1:
        /// frame time is a steady 7ms with holes of 39ms and 86ms, eleven frames lost, every hole
        /// sitting on an open. A blocked UI thread starves whatever else needs it, which is the
        /// stutter heard at the start.
        ///
        /// Spreading the opens over separate frames was tried and measured: it redistributes the
        /// blocking rather than reducing it, because three completions cost the same whether they
        /// land on one frame or three. The only way to stop it interrupting audio is for it not to
        /// happen while audio is playing.
        ///
        /// Nothing is lost by waiting: the clock already stalls on _pendingMediaOpens for exactly
        /// this period, so the delay before the first frame is the same - it is simply spent before
        /// the transport starts rather than inside it.
        /// </remarks>
        private async Task PreloadActiveSourcesAsync(TimeSpan at)
        {
            var waits = new System.Collections.Generic.List<Task>();
            var tracks = _viewModel.Tracks;

            for (int i = 0; i < MaxOverlayTracks && i < tracks.Count; i++)
            {
                var clip = ResolveActiveClip(tracks[i], at);
                if (clip == null || clip.IsStill || clip.IsImage) continue;
                if (string.IsNullOrWhiteSpace(clip.FilePath)) continue;

                var player = _overlayPlayer[i];
                if (player == null) continue;
                if (player.Source != null &&
                    string.Equals(PlayerPath(player), clip.FilePath, StringComparison.OrdinalIgnoreCase))
                    continue;   // already open on the right file

                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void Done(MediaPlayer sender, object args)
                {
                    sender.MediaOpened -= Done;
                    sender.MediaFailed -= Failed;
                    ready.TrySetResult(true);
                }
                void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
                {
                    sender.MediaOpened -= Done;
                    sender.MediaFailed -= Failed;
                    ready.TrySetResult(false);   // a bad file must not hold up the transport
                }

                player.MediaOpened += Done;
                player.MediaFailed += Failed;
                TraceEvent("PRELOAD slot=" + i + " " + System.IO.Path.GetFileName(clip.FilePath));
                HookSeekTracking(player, i);
                try { player.Source = MediaSource.CreateFromUri(new Uri(clip.FilePath)); }
                catch { Failed(player, null); }

                waits.Add(ready.Task);
            }

            if (waits.Count == 0) return;

            // Bounded, so a source that never opens cannot stop playback starting at all.
            await Task.WhenAny(Task.WhenAll(waits), Task.Delay(3000));
            TraceEvent("PRELOAD done");
        }

        public void StopPlayback()
        {
            if (_isPlaybackLoopRunning)
            {
                _isPlaybackLoopRunning = false;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= PlaybackTimer_Tick;
            }
            
            HideAllOverlays();
            
            if (_viewModel != null)
            {
                _viewModel.IsPlaying = false;
                _isPaused = false;
                _isAnimating = false;
            }

            if (_mode == EditorMode.Arrange)
            {
                _dispatcher.TryEnqueue(() => EvaluateOverlays(_viewModel.CurrentStoryTime));
            }
        }

        private void PlaybackTimer_Tick(object sender, object e)
        {
            if (_isPaused) return;

            var now = ((Microsoft.UI.Xaml.Media.RenderingEventArgs)e).RenderingTime;
            if (_lastTickTime == TimeSpan.Zero) _lastTickTime = now;
            var elapsed = now - _lastTickTime;
            _lastTickTime = now;
            _frameSeq++;
            TraceTick();

            // Stall the clock if we're waiting for media to load, so clips don't skip the first 500ms
            if (System.Threading.Interlocked.CompareExchange(ref _pendingMediaOpens, 0, 0) > 0) return;

            // Also stall the clock if any active player is buffering or opening, to prevent the clock
            // from running ahead and triggering continuous drift correction seeks (which causes stutter).
            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                if (_activeOverlay[i] != null && _overlayPlayer[i]?.PlaybackSession != null)
                {
                    var state = _overlayPlayer[i].PlaybackSession.PlaybackState;
                    if (state == MediaPlaybackState.Buffering || state == MediaPlaybackState.Opening)
                    {
                        return;
                    }
                }
            }

            bool drivenByHardware = false;
            var mainOp = _activeOverlay[0];
            var mainPlayer = _overlayPlayer[0];
            
            if (mainOp != null && !mainOp.IsStill && mainPlayer?.PlaybackSession != null)
            {
                double clipSpeed = mainOp.PlaybackSpeed > 0 ? mainOp.PlaybackSpeed : 1.0;
                double videoElapsed = (mainPlayer.PlaybackSession.Position - mainOp.VideoStartTime).TotalSeconds / clipSpeed;
                if (videoElapsed >= 0 && videoElapsed <= mainOp.OpDuration.TotalSeconds + 0.5)
                {
                    double targetStoryTime = (mainOp.StartTime + TimeSpan.FromSeconds(videoElapsed)).TotalSeconds;
                    double currentStoryTime = _viewModel.CurrentStoryTime.TotalSeconds;
                    double newStoryTime = currentStoryTime + elapsed.TotalSeconds * _viewModel.PlaybackSpeed;
                    
                    // Soft PLL: smoothly pull the wall clock towards the hardware decoder to prevent long-term drift
                    // without adopting its discrete, jittery Position updates.
                    double drift = targetStoryTime - newStoryTime;
                    if (Math.Abs(drift) > 0.05)
                    {
                        // Maximum slew rate: 2ms per frame (~120ms per sec) to completely eliminate visual jitter
                        newStoryTime += Math.Clamp(drift * 0.1, -0.002, 0.002);
                    }
                    
                    _viewModel.CurrentStoryTime = TimeSpan.FromSeconds(newStoryTime);
                    drivenByHardware = true;
                }
            }

            if (!drivenByHardware)
            {
                _viewModel.CurrentStoryTime += TimeSpan.FromSeconds(elapsed.TotalSeconds * _viewModel.PlaybackSpeed);
            }

            if (_viewModel.LoopRegionStart.HasValue && _viewModel.LoopRegionEnd.HasValue)
            {
                if (_viewModel.CurrentStoryTime >= _viewModel.LoopRegionEnd.Value)
                {
                    _viewModel.CurrentStoryTime = _viewModel.LoopRegionStart.Value;
                }
                else if (_viewModel.CurrentStoryTime < _viewModel.LoopRegionStart.Value)
                {
                    // If playhead was manually dragged before the loop region, let it play INTO the loop region,
                    // or immediately snap it? Usually it snaps, or NLEs let you play into it. We'll let it play into it.
                }
            }
            else if (_viewModel.TotalStoryTime > TimeSpan.Zero && _viewModel.CurrentStoryTime >= _viewModel.TotalStoryTime)
            {
                if (_viewModel.IsLooping)
                {
                    TraceEvent("LOOP wrap to 0");
                    _viewModel.CurrentStoryTime = TimeSpan.Zero;
                }
                else
                {
                    _viewModel.CurrentStoryTime = _viewModel.TotalStoryTime;
                    StopPlayback();
                    return;
                }
            }

            EvaluateOverlays(_viewModel.CurrentStoryTime);
            
            if ((DateTime.Now - _lastTelemetryUpdate).TotalMilliseconds >= 100)
            {
                _lastTelemetryUpdate = DateTime.Now;
                UpdateTelemetryOverlay();
            }
        }
    }
}
