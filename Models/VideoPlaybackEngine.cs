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
            _isAnimating = true;
            
            for (int i = 0; i < MaxOverlayTracks; i++) _activeOverlay[i] = null;

            if (!_isPlaybackLoopRunning)
            {
                _isPlaybackLoopRunning = true;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += PlaybackTimer_Tick;
            }
            _lastTickTime = TimeSpan.Zero;
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
                        return; // stall clock
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
