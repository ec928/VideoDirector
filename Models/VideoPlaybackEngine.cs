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

namespace VideoDirector.Models
{
    public class VideoPlaybackEngine
    {
        private readonly Views.DirectorPlayerControl _playerControl;
        private readonly MediaPlayerElement _playerA;
        private readonly MediaPlayerElement _playerB;
        private readonly DirectorViewModel _viewModel;
        private readonly DispatcherQueue _dispatcher;

        private MediaPlayer _mediaPlayerA;
        private MediaPlayer _mediaPlayerB;

        private bool _isPlayerAActive = true;
        private CancellationTokenSource _playbackCts;
        private bool _isPaused = false;
        private DateTime _pauseStartTime;
        private TaskCompletionSource<bool> _skipTcs;

        // Animation state
        private bool _isAnimating = false;
        private bool _isPreparingTransition = false;
        private CinematicOperation _opA;
        private CinematicOperation _opB;
        private DateTime _opAStartTime;
        private DateTime _opBStartTime;
        private TimeSpan _opADuration;
        private TimeSpan _opBDuration;
        private bool _inTransition = false;
        private DateTime _transitionStartTime;
        
        // Target state for rendering loop
        private TimeSpan _renderDuration;
        private MediaPlayerElement _fadeOutElement;
        private MediaPlayerElement _fadeInElement;
        private double _fadeOutVolume = 1.0; // per-clip volumes captured for the crossfade audio ramp
        private double _fadeInVolume = 1.0;

        public CinematicOperation? CurrentPlayingOperation { get; private set; }

        // Overlay state — one entry per upper track, indexed 0..MaxOverlayTracks-1. Track i owns
        // player[i] and the pre-declared render surface OverlayVisuals[i] (§7B): no per-track code
        // paths, so adding a track is data, not new branches.
        private const int MaxOverlayTracks = DirectorViewModel.MaxOverlayTracks;
        private readonly MediaPlayer[] _overlayPlayer = new MediaPlayer[MaxOverlayTracks];
        private readonly CinematicOperation[] _activeOverlay = new CinematicOperation[MaxOverlayTracks];
        // Native aspect (w/h) of each track's overlay video, cached when the media opens, so
        // the placement box can be sized to the video's shape (no black bars).
        private readonly double[] _overlayAspect = new double[MaxOverlayTracks];
        private bool _isEditingOverlay = false;
        // Story time as of the start of the currently-playing clip; CurrentStoryTime is
        // derived from this plus the active player's real position every render frame.
        private TimeSpan _storyTimeAtClipStart = TimeSpan.Zero;

        public VideoPlaybackEngine(Views.DirectorPlayerControl playerControl, DirectorViewModel viewModel)
        {
            _playerControl = playerControl;
            _playerA = playerControl.PlayerA;
            _playerB = playerControl.PlayerB;
            _viewModel = viewModel;
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            InitializePlayers();
            InitializeOverlayPlayers();

            _viewModel.PlaybackSpeedChanged += ViewModel_PlaybackSpeedChanged;
            _viewModel.OperationSeekRequested += ViewModel_OperationSeekRequested;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.TimelineNodes.CollectionChanged += (s, e) => OnTimelineSequenceChanged();

            // Arrange mode: drag/wheel the PiP under the cursor.
            _playerControl.OverlayBoxDragged += OnOverlayBoxDragged;
            _playerControl.OverlayBoxWheel += OnOverlayBoxWheel;

            // Start in Arrange (the default mode) — PiP input active.
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
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
        }

        private void OnTimelineSequenceChanged()
        {
            if (_playbackCts != null && !_playbackCts.IsCancellationRequested)
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
                    // If actively playing when the sequence changes, restart playback at the current playhead position.
                    var at = _viewModel.CurrentStoryTime;
                    int startIdx = _viewModel.GetTimelineIndexForStoryTime(at);
                    var offset = at - _viewModel.GetSpineClipStart(startIdx);
                    if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
                    if (startIdx >= 0 && startIdx < _viewModel.TimelineNodes.Count
                        && offset > _viewModel.TimelineNodes[startIdx].OpDuration) offset = TimeSpan.Zero;

                    _ = StartPlaybackAsync(startIdx, offset);
                }
            }
        }

        private void ViewModel_PlaybackSpeedChanged(object? sender, double speed)
        {
            double speedA = _opA != null ? speed * _opA.PlaybackSpeed : speed;
            double speedB = _opB != null ? speed * _opB.PlaybackSpeed : speed;

            _mediaPlayerA.PlaybackSession.PlaybackRate = speedA;
            _mediaPlayerB.PlaybackSession.PlaybackRate = speedB;
            
            if (speed == 0.0)
            {
                _mediaPlayerA.Pause();
                _mediaPlayerB.Pause();
            }
            else if (_isAnimating && !_isPaused)
            {
                if (_isPlayerAActive && speedA > 0) _mediaPlayerA.Play();
                else if (!_isPlayerAActive && speedB > 0) _mediaPlayerB.Play();
            }
        }

        public void SeekActiveOperation(TimeSpan position)
        {
            // In Edit mode the subject is the edit player — the main player for a spine clip, the
            // OVERLAY player for an overlay. Seeking the main player here left the overlay preview
            // blank while trimming (the overlay frame was never moved).
            if (_mode == EditorMode.Edit && _editPlayer?.PlaybackSession != null)
            {
                _editPlayer.PlaybackSession.Position = position;
                if (!_editPreviewPlaying) RenderPausedFrame(_editPlayer);
                return;
            }

            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            activePlayer.PlaybackSession.Position = position;
        }

        // A paused MediaPlayer that was just seeked or freshly attached (the overlay edit surface is
        // attached on demand) often shows no frame until it plays. StepForwardOneFrame forces the
        // current frame to decode and display while staying paused — otherwise: blank preview.
        private static void RenderPausedFrame(MediaPlayer player)
        {
            var s = player?.PlaybackSession;
            if (s == null || s.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing) return;
            try { player.StepForwardOneFrame(); } catch { }
        }

        private void InitializePlayers()
        {
            _mediaPlayerA = new MediaPlayer { IsLoopingEnabled = false, AutoPlay = false };
            _mediaPlayerB = new MediaPlayer { IsLoopingEnabled = false, AutoPlay = false };

            // Each MediaPlayer auto-registers with the OS's System Media Transport Controls
            // (lock-screen/media-key "Now Playing" session) unless disabled. With multiple
            // MediaPlayer instances playing concurrently (main track + overlays), that
            // background negotiation overhead is a known cause of stutter. This app has no
            // use for OS transport-control integration, so turn it off on every player.
            _mediaPlayerA.CommandManager.IsEnabled = false;
            _mediaPlayerB.CommandManager.IsEnabled = false;

            _playerA.SetMediaPlayer(_mediaPlayerA);
            _playerB.SetMediaPlayer(_mediaPlayerB);

            _playerControl.ActiveTransform = _playerControl.TransformA;
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_playbackCts == null || _playbackCts.IsCancellationRequested)
            {
                // The playhead is the source of truth: resume exactly where the scrubber sits,
                // including part-way into a clip. (Selecting a clip parks the playhead at that
                // clip's start, so selection still plays the clip from its beginning.)
                var at = _viewModel.CurrentStoryTime;
                int startIdx = _viewModel.GetTimelineIndexForStoryTime(at);
                var offset = at - _viewModel.GetSpineClipStart(startIdx);
                if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
                if (startIdx >= 0 && startIdx < _viewModel.TimelineNodes.Count
                    && offset > _viewModel.TimelineNodes[startIdx].OpDuration) offset = TimeSpan.Zero;

                await StartPlaybackAsync(startIdx, offset);
                return;
            }

            if (_isPaused)
            {
                ResumePlayback();
            }
            else
            {
                PausePlayback();
            }
        }

        private void PausePlayback()
        {
            _isPaused = true;
            _pauseStartTime = DateTime.Now;
            _viewModel.IsPlaying = false;

            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            activePlayer.Pause();
            if (_inTransition || _isPreparingTransition)
            {
                var otherPlayer = _isPlayerAActive ? _mediaPlayerB : _mediaPlayerA;
                otherPlayer.Pause();
            }

            // Overlay players are driven from the per-frame render loop, which stops running
            // entirely while paused — so they must be paused explicitly here rather than
            // relying on the render loop to catch up to the paused state.
            for (int i = 0; i < MaxOverlayTracks; i++) _overlayPlayer[i]?.Pause();

            _dispatcher.TryEnqueue(() =>
            {
                UpdateWysiwygOverlay();
                // The render loop is stopped now, so nothing else will re-evaluate the composite:
                // switch the PiPs back to arrangeable stills (frame + handles) explicitly.
                RefreshComposite();
            });
        }

        private void ResumePlayback()
        {
            _isPaused = false;
            var pauseDuration = DateTime.Now - _pauseStartTime;
            _opAStartTime += pauseDuration;
            _opBStartTime += pauseDuration;
            _transitionStartTime += pauseDuration;
            _viewModel.IsPlaying = true;
            
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            activePlayer.Play();
            if (_inTransition || _isPreparingTransition)
            {
                var otherPlayer = _isPlayerAActive ? _mediaPlayerB : _mediaPlayerA;
                otherPlayer.Play();
            }

            // Resume whichever overlay tracks are currently occupied; EvaluateOverlays will
            // re-sync their exact position on the next render tick via drift correction.
            if (_viewModel.PlaybackSpeed > 0)
            {
                for (int i = 0; i < MaxOverlayTracks; i++)
                {
                    if (_activeOverlay[i] == null || _overlayPlayer[i]?.PlaybackSession == null) continue;
                    _overlayPlayer[i].PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
                    _overlayPlayer[i].Volume = _activeOverlay[i].Volume;
                    _overlayPlayer[i].Play();
                }
            }
        }

        public async Task StartPlaybackAsync(int startIndex = 0, TimeSpan startOffset = default)
        {
            if (_viewModel.TimelineNodes.Count == 0) return;

            // Composite playback is Arrange mode: exit any Edit state so the whole timeline plays.
            _isEditingOverlay = false;
            _mode = EditorMode.Arrange;
            _editClip = null;
            _editPlayer = null;
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
            _viewModel.IsEditMode = false;
            StopEditPreview();
            StopPlayback(); // Ensure we stop cleanly first
            var myCts = new CancellationTokenSource();
            _playbackCts = myCts;
            var token = myCts.Token;

            _viewModel.IsPlaying = true;
            _isPaused = false;
            
            // Calculate CurrentStoryTime based on startIndex
            _viewModel.CurrentStoryTime = TimeSpan.Zero;
            for(int i=0; i<startIndex; i++)
            {
                _viewModel.CurrentStoryTime += _viewModel.TimelineNodes[i].OpDuration + _viewModel.TimelineNodes[i].TransitionDuration;
            }
            _storyTimeAtClipStart = _viewModel.CurrentStoryTime;   // baseline = clip boundary
            // Resume mid-clip (e.g. after scrubbing): the render loop derives CurrentStoryTime as
            // baseline + elapsed-into-clip, so it lands back on the scrubbed position.
            if (startOffset > TimeSpan.Zero) _viewModel.CurrentStoryTime += startOffset;

            // Arrange marks tracks active WITHOUT loading any video (still-only path), so clear the
            // active set here — otherwise playback would see them as already active and skip
            // ActivateOverlaySlot, leaving the overlay players with no source loaded.
            for (int i = 0; i < MaxOverlayTracks; i++) _activeOverlay[i] = null;

            _isAnimating = true;
            CompositionTarget.Rendering += CompositionTarget_Rendering;

            try
            {
                await PlaybackLoopAsync(startIndex, token, startOffset);
            }
            catch (OperationCanceledException)
            {
                // Playback stopped or skipped
            }
            finally
            {
                if (_playbackCts == myCts)
                {
                    bool wasCancelled = myCts.IsCancellationRequested;
                    StopPlayback();

                    if (!wasCancelled)
                    {
                        // Reaching the end used to blank the main players and silently drop into
                        // Edit mode — the same trap as before (Play then previews one clip and the
                        // playhead looks frozen). Stay in Arrange and just re-render the composite.
                        _dispatcher.TryEnqueue(() => RefreshComposite());
                    }
                }
            }
        }

        public void StopPlayback()
        {
            _playbackCts?.Cancel();
            _isAnimating = false;
            _isPreparingTransition = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;

            _mediaPlayerA?.Pause();
            _mediaPlayerB?.Pause();
            HideAllOverlays();

            if (_viewModel != null)
            {
                _viewModel.IsPlaying = false;
                _isPaused = false;
            }

            // Stopping while arranging (not on the way into Edit, which sets _mode first) should
            // leave the static composite on screen — HideAllOverlays just tore the PiPs down, so
            // rebuild them (with their handles) at the current story time for continued arranging.
            if (_mode == EditorMode.Arrange)
            {
                _dispatcher.TryEnqueue(() => EvaluateOverlays(_viewModel.CurrentStoryTime));
            }
        }

        public void SkipNext()
        {
            if (_viewModel.SelectedTimelineNode != null)
            {
                int idx = _viewModel.TimelineNodes.IndexOf(_viewModel.SelectedTimelineNode as CinematicOperation);
                if (idx < _viewModel.TimelineNodes.Count - 1)
                {
                    _ = StartPlaybackAsync(idx + 1);
                }
            }
        }

        public void SkipPrevious()
        {
            if (_viewModel.SelectedTimelineNode != null)
            {
                int idx = _viewModel.TimelineNodes.IndexOf(_viewModel.SelectedTimelineNode as CinematicOperation);
                if (idx > 0) idx--;
                _ = StartPlaybackAsync(idx);
            }
        }

        private async Task PlaybackLoopAsync(int startIndex, CancellationToken token, TimeSpan startOffset = default)
        {
            bool startedByTransition = false;
            TimeSpan previousTransitionDuration = TimeSpan.Zero;
            int currentIndex = startIndex;

            while (true)
            {
                for (int i = currentIndex; i < _viewModel.TimelineNodes.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var op = _viewModel.TimelineNodes[i] as CinematicOperation;
                    
                    CurrentPlayingOperation = op;
                    _dispatcher.TryEnqueue(() => 
                    {
                        _viewModel.SelectedTimelineNode = op;
                    });

                    
                    var nextOp = i + 1 < _viewModel.TimelineNodes.Count ? _viewModel.TimelineNodes[i + 1] as CinematicOperation : null;
                    if (nextOp == null && _viewModel.IsLooping && _viewModel.TimelineNodes.Count > 0)
                    {
                        nextOp = _viewModel.TimelineNodes[0] as CinematicOperation;
                    }
                    bool hasNextTransition = nextOp != null;

                    _skipTcs = new TaskCompletionSource<bool>();
                    
                    // 1. Play the main portion of the clip
                    await PlayOperationAsync(op, nextOp, startedByTransition, hasNextTransition, previousTransitionDuration, token, startOffset);
                    startOffset = TimeSpan.Zero; // only the first clip resumes mid-way
                    
                    // Advance the clip-start baseline by exactly this clip's story contribution.
                    // The render loop drives CurrentStoryTime continuously off this baseline, so
                    // accumulate into the baseline (not CurrentStoryTime) — adding to
                    // CurrentStoryTime here would double-count, since the render loop has already
                    // advanced it to this clip's end.
                    if (_skipTcs.Task.IsCompleted)
                    {
                        // Skipped!
                        startedByTransition = false;
                        _storyTimeAtClipStart += op.OpDuration + op.TransitionDuration;
                        _viewModel.CurrentStoryTime = _storyTimeAtClipStart;
                        continue;
                    }
                    _storyTimeAtClipStart += op.OpDuration;
                    _viewModel.CurrentStoryTime = _storyTimeAtClipStart;

                    // 2. Play the transition into the next clip if applicable
                    if (hasNextTransition)
                    {
                        await PlayTransitionAsync(op, nextOp, token);
                        startedByTransition = true;
                        previousTransitionDuration = op.TransitionDuration;
                        _storyTimeAtClipStart += op.TransitionDuration;
                        _viewModel.CurrentStoryTime = _storyTimeAtClipStart;
                    }
                    else
                    {
                        startedByTransition = false;
                    }
                }

                if (_viewModel.IsLooping)
                {
                    currentIndex = 0;
                    _viewModel.CurrentStoryTime = TimeSpan.Zero;
                    _storyTimeAtClipStart = TimeSpan.Zero;
                }
                else
                {
                    break;
                }
            }
        }

        private async Task PlayOperationAsync(CinematicOperation op, CinematicOperation nextOp, bool startedByTransition, bool hasNextTransition, TimeSpan previousTransitionDuration, CancellationToken token, TimeSpan startOffset = default)
        {
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var activeElement = _isPlayerAActive ? _playerA : _playerB;
            var standbyPlayer = _isPlayerAActive ? _mediaPlayerB : _mediaPlayerA;
            var standbyElement = _isPlayerAActive ? _playerB : _playerA;

            if (!startedByTransition)
            {
                if (!string.IsNullOrWhiteSpace(op.FilePath))
                {
                    if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        void OnMediaOpened(MediaPlayer sender, object args) => tcs.TrySetResult(true);
                        activePlayer.MediaOpened += OnMediaOpened;

                        activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                        
                        await Task.WhenAny(tcs.Task, WaitWithPauseAsync(TimeSpan.FromSeconds(2), token));
                        activePlayer.MediaOpened -= OnMediaOpened;
                    }

                    activePlayer.PlaybackSession.Position = op.VideoStartTime + startOffset;
                    
                    double combinedSpeed = _viewModel.PlaybackSpeed * op.PlaybackSpeed;
                    activePlayer.PlaybackSession.PlaybackRate = combinedSpeed;
                    activePlayer.Volume = op.Volume;
                    
                    if (!_isPaused && combinedSpeed > 0) activePlayer.Play();
                    else if (combinedSpeed == 0) activePlayer.Pause();
                }

                _dispatcher.TryEnqueue(() =>
                {
                    activeElement.Opacity = 1;
                    standbyElement.Opacity = 0;
                });
            }
            
            // Preload next operation into standby player to ensure gapless playback
            if (hasNextTransition && nextOp != null && !string.IsNullOrWhiteSpace(nextOp.FilePath))
            {
                if (standbyPlayer.Source == null || !string.Equals((standbyPlayer.Source as MediaSource)?.Uri?.LocalPath, nextOp.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    standbyPlayer.Source = MediaSource.CreateFromUri(new Uri(nextOp.FilePath));
                    standbyPlayer.PlaybackSession.Position = nextOp.VideoStartTime;
                    standbyPlayer.Volume = 0.0;
                    standbyPlayer.Pause();
                }
            }
            
            // Back-date by startOffset so elapsed accounting matches the seeked-in position and
            // the clip still ends at its proper boundary.
            var opStartTime = startedByTransition ? DateTime.Now - previousTransitionDuration : DateTime.Now - startOffset;
            var totalVisibleDuration = op.OpDuration + previousTransitionDuration + (hasNextTransition ? op.TransitionDuration : TimeSpan.Zero);
            
            double globalSpeed = _viewModel.PlaybackSpeed == 0 ? 1.0 : _viewModel.PlaybackSpeed;
            if (globalSpeed != 1.0)
            {
                totalVisibleDuration = TimeSpan.FromSeconds(totalVisibleDuration.TotalSeconds / globalSpeed);
            }
            
            if (_isPlayerAActive)
            {
                _opA = op;
                _opADuration = totalVisibleDuration;
                _opAStartTime = opStartTime;
            }
            else
            {
                _opB = op;
                _opBDuration = totalVisibleDuration;
                _opBStartTime = opStartTime;
            }
            
            _inTransition = false;

            TimeSpan elapsed = TimeSpan.Zero;
            DateTime lastTick = DateTime.Now;

            // Stills (image files or speed-0 frozen frames) have no advancing decode position, so
            // they can only be timed by wall clock. Real videos end at their trimmed out-point
            // (decode position) — but with a wall-clock backstop: a slow seek deep into a long
            // source (e.g. a 50-min .mkv) can leave Position lagging for seconds, and without the
            // backstop the clip runs past its out-point until the file ends. The two measures are
            // designed to coincide across every speed combination, so breaking on whichever fires
            // first is frame-accurate in the normal case and self-correcting when Position stalls.
            bool isStill = op.IsStill;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                var now = DateTime.Now;
                if (!_isPaused)
                {
                    double currentSpeed = globalSpeed == 0 ? 1.0 : globalSpeed;
                    elapsed += TimeSpan.FromSeconds((now - lastTick).TotalSeconds * currentSpeed);

                    if (isStill)
                    {
                        if (elapsed >= op.OpDuration) break;
                    }
                    else
                    {
                        if (activePlayer.PlaybackSession.Position >= op.VideoEndTime || elapsed >= op.OpDuration) break;
                    }
                }
                lastTick = now;

                // Polled at ~1 frame instead of 50ms so a clip can't keep playing noticeably
                // past its nominal end before the transition machinery notices — that overshoot
                // window was a source of transient story-time inaccuracy at clip boundaries.
                await Task.Delay(15, token);
            }

            if (!hasNextTransition)
            {
                activePlayer.Pause();
                _isPlayerAActive = !_isPlayerAActive;
                _playerControl.ActiveTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;
            }
        }

        private async Task PlayTransitionAsync(CinematicOperation op, CinematicOperation nextOp, CancellationToken token)
        {
            var fadingOutPlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var fadingOutElement = _isPlayerAActive ? _playerA : _playerB;
            var fadingOutGrid = _isPlayerAActive ? _playerControl.GridA : _playerControl.GridB;
            
            _isPreparingTransition = true;
            _isPlayerAActive = !_isPlayerAActive; // Swap to next
            _playerControl.ActiveTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;

            // Update _opA/_opB atomically with the _isPlayerAActive flip above (not after the
            // awaits below) — otherwise there's a window where _isPlayerAActive already points
            // at the next clip's player but _opA/_opB still reference the previous clip, which
            // the per-frame story-time calc reads and briefly computes a garbage value from.
            if (nextOp != null)
            {
                var nextTotalDuration = nextOp.OpDuration + op.TransitionDuration;

                double opGlobalSpeed = _viewModel.PlaybackSpeed == 0 ? 1.0 : _viewModel.PlaybackSpeed;
                if (opGlobalSpeed != 1.0)
                {
                    nextTotalDuration = TimeSpan.FromSeconds(nextTotalDuration.TotalSeconds / opGlobalSpeed);
                }

                if (!_isPlayerAActive)
                {
                    _opB = nextOp;
                    _opBDuration = nextTotalDuration;
                    _opBStartTime = DateTime.Now;
                }
                else
                {
                    _opA = nextOp;
                    _opADuration = nextTotalDuration;
                    _opAStartTime = DateTime.Now;
                }
            }

            var fadingInPlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var fadingInElement = _isPlayerAActive ? _playerA : _playerB;
            var fadingInGrid = _isPlayerAActive ? _playerControl.GridA : _playerControl.GridB;

            if (nextOp != null && !string.IsNullOrWhiteSpace(nextOp.FilePath))
            {
                if (fadingInPlayer.Source == null || !string.Equals((fadingInPlayer.Source as MediaSource)?.Uri?.LocalPath, nextOp.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var tcs = new TaskCompletionSource<bool>();
                    Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                    fadingInPlayer.MediaOpened += handler;
                    
                    fadingInPlayer.Source = MediaSource.CreateFromUri(new Uri(nextOp.FilePath));
                    
                    await Task.WhenAny(tcs.Task, Task.Delay(1500));
                    fadingInPlayer.MediaOpened -= handler;
                }

                fadingInPlayer.PlaybackSession.Position = nextOp.VideoStartTime;
                
                double combinedNextSpeed = _viewModel.PlaybackSpeed * nextOp.PlaybackSpeed;
                fadingInPlayer.PlaybackSession.PlaybackRate = combinedNextSpeed;
                fadingInPlayer.Volume = 0.0;
                
                if (!_isPaused && combinedNextSpeed > 0) fadingInPlayer.Play();
                else if (combinedNextSpeed == 0) fadingInPlayer.Pause();
            }
            
            if (op.TransitionDuration <= TimeSpan.Zero || op.TransitionStyle == TransitionStyle.HardSnap)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    Canvas.SetZIndex(fadingInGrid, 1);
                    Canvas.SetZIndex(fadingOutGrid, 0);
                    fadingInElement.Opacity = 1.0;
                });
                
                fadingInPlayer.Volume = nextOp?.Volume ?? 1.0;

                _ = Task.Delay(150).ContinueWith(_ =>
                {
                    _dispatcher.TryEnqueue(() => fadingOutElement.Opacity = 0.0);
                    fadingOutPlayer.Pause();
                });
                _isPreparingTransition = false;
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                Canvas.SetZIndex(fadingOutGrid, 1); // Current clip on top for Transition Out
                Canvas.SetZIndex(fadingInGrid, 0);  // Next clip underneath
            });

            _fadeOutElement = fadingOutElement;
            _fadeInElement = fadingInElement;
            _fadeOutVolume = op.Volume;
            _fadeInVolume = nextOp?.Volume ?? 1.0;
            _isPreparingTransition = false;
            _inTransition = true;
            _transitionStyle = op.TransitionStyle;
            _renderDuration = op.TransitionDuration;
            _transitionStartTime = DateTime.Now;

            double activeGlobalSpeed = _viewModel.PlaybackSpeed == 0 ? 1.0 : _viewModel.PlaybackSpeed;
            TimeSpan realTransitionDuration = op.TransitionDuration;
            if (activeGlobalSpeed != 1.0)
            {
                realTransitionDuration = TimeSpan.FromSeconds(realTransitionDuration.TotalSeconds / activeGlobalSpeed);
            }

            await WaitWithPauseAsync(realTransitionDuration, token);

            fadingOutPlayer.Pause();
            fadingOutPlayer.Volume = 1.0;
            
            _dispatcher.TryEnqueue(() =>
            {
                fadingInElement.Opacity = 1.0;
                fadingInPlayer.Volume = nextOp?.Volume ?? 1.0;
                fadingOutElement.Opacity = 0.0;
            });
            _inTransition = false;
        }

        private async Task WaitWithPauseAsync(TimeSpan duration, CancellationToken token)
        {
            var targetTime = DateTime.Now + duration;
            while (DateTime.Now < targetTime)
            {
                token.ThrowIfCancellationRequested();
                if (_isPaused)
                {
                    var pauseStart = DateTime.Now;
                    await Task.Delay(50, token);
                    targetTime += (DateTime.Now - pauseStart);
                    continue;
                }
                
                var remaining = targetTime - DateTime.Now;
                if (remaining <= TimeSpan.Zero) break;
                
                var delay = remaining > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
                await Task.Delay(delay, token);
            }
        }

        private TransitionStyle _transitionStyle;

        private void CompositionTarget_Rendering(object? sender, object e)
        {
            if (!_isAnimating || _isPaused) return;
            
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            if (activePlayer != null && activePlayer.PlaybackSession != null)
            {
                _viewModel.CurrentOperationTime = activePlayer.PlaybackSession.Position;
                _viewModel.CurrentOperationDuration = activePlayer.PlaybackSession.NaturalDuration;
            }

            if (_inTransition)
            {
                var transElapsed = DateTime.Now - _transitionStartTime;
                var transProgress = _renderDuration.TotalMilliseconds > 0 
                    ? Math.Clamp(transElapsed.TotalMilliseconds / _renderDuration.TotalMilliseconds, 0, 1) 
                    : 1;

                if (_transitionStyle == TransitionStyle.DipToColor)
                {
                    if (transProgress < 0.5)
                    {
                        _fadeOutElement.Opacity = 1.0 - (transProgress * 2.0);
                        _fadeInElement.Opacity = 0.0;
                    }
                    else
                    {
                        _fadeOutElement.Opacity = 0.0;
                        _fadeInElement.Opacity = (transProgress - 0.5) * 2.0;
                    }
                }
                else if (_transitionStyle == TransitionStyle.CinematicBridge)
                {
                    double smoothProgress = transProgress * transProgress * (3.0 - 2.0 * transProgress);
                    _fadeOutElement.Opacity = 1.0 - smoothProgress;
                    _fadeInElement.Opacity = 1.0; // Keep bottom opaque
                }
                else
                {
                    _fadeOutElement.Opacity = 1.0 - transProgress; // Fade out top clip
                    _fadeInElement.Opacity = 1.0; // Keep bottom clip fully opaque
                }

                // Audio Crossfade
                var fadingOutPlayer = _fadeOutElement == _playerA ? _mediaPlayerA : _mediaPlayerB;
                var fadingInPlayer = _fadeInElement == _playerA ? _mediaPlayerA : _mediaPlayerB;
                
                fadingOutPlayer.Volume = (1.0 - transProgress) * _fadeOutVolume;
                fadingInPlayer.Volume = transProgress * _fadeInVolume;
            }

            void UpdateSpatial(CinematicOperation op, Windows.Media.Playback.MediaPlayer player, DateTime startTime, TimeSpan duration, Microsoft.UI.Xaml.Media.CompositeTransform transform)
            {
                if (op == null || transform == null) return;
                double spatialProgress = 1.0;
                if (duration.TotalMilliseconds > 0)
                {
                    if (!op.IsStill && player?.PlaybackSession != null)
                    {
                        var actualElapsed = player.PlaybackSession.Position - op.VideoStartTime;
                        spatialProgress = Math.Clamp(actualElapsed.TotalMilliseconds / duration.TotalMilliseconds, 0.0, 1.0);
                    }
                    else
                    {
                        var spatialElapsed = DateTime.Now - startTime;
                        spatialProgress = Math.Clamp(spatialElapsed.TotalMilliseconds / duration.TotalMilliseconds, 0.0, 1.0);
                    }
                }
                ApplyMarksAtProgress(op, spatialProgress, transform);
            }

            UpdateSpatial(_opA, _mediaPlayerA, _opAStartTime, _opADuration, _playerControl.TransformA);
            UpdateSpatial(_opB, _mediaPlayerB, _opBStartTime, _opBDuration, _playerControl.TransformB);

            // CurrentStoryTime only gets bumped at clip boundaries elsewhere in this class —
            // it does not tick on its own. Advance it continuously here from the active
            // player's real decode position, or overlay drift-correction (which compares
            // against this value every frame) sees a stale target and fights the overlay's
            // real playback, which is what caused the overlay stutter.
            var storyTimePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var storyTimeOp = _isPlayerAActive ? _opA : _opB;
            var storyTimeStart = _isPlayerAActive ? _opAStartTime : _opBStartTime;
            if (storyTimeOp != null)
            {
                // Video position advances at the clip's own playback rate, but story time is
                // measured in real sequence seconds — a clip at 2x contributes half as much
                // story time as video watched. Divide by the clip speed so the per-frame
                // advance maxes out at exactly OpDuration, matching the boundary accounting.
                double videoElapsed;
                if (!storyTimeOp.IsStill && storyTimePlayer?.PlaybackSession != null)
                {
                    double clipSpeed = storyTimeOp.PlaybackSpeed > 0 ? storyTimeOp.PlaybackSpeed : 1.0;
                    videoElapsed = (storyTimePlayer.PlaybackSession.Position - storyTimeOp.VideoStartTime).TotalSeconds / clipSpeed;
                }
                else
                {
                    videoElapsed = (DateTime.Now - storyTimeStart).TotalSeconds;
                }
                if (videoElapsed < 0) videoElapsed = 0;
                _viewModel.CurrentStoryTime = _storyTimeAtClipStart + TimeSpan.FromSeconds(videoElapsed);
            }

            // Evaluate overlay clips against master story time
            EvaluateOverlays(_viewModel.CurrentStoryTime);

            var activeOp = _isPlayerAActive ? _opA : _opB;
            
            UpdateTimelineNodesIsPlayingState(activeOp);
        }

        private void UpdateTimelineNodesIsPlayingState(CinematicOperation activeOp)
        {
            if (_viewModel.TimelineNodes != null)
            {
                foreach (var node in _viewModel.TimelineNodes)
                {
                    bool isPlaying = (node == activeOp);
                    if (node.IsPlaying != isPlaying)
                    {
                        node.IsPlaying = isPlaying;
                    }
                }
            }

            // The telemetry HUD is diagnostic text a human can't usefully read at 60fps —
            // throttle it to ~10/sec instead of recomputing/re-laying-out text every frame,
            // which otherwise competes with video decode for CPU during overlay playback.
            if ((DateTime.Now - _lastTelemetryUpdate).TotalMilliseconds >= 100)
            {
                _lastTelemetryUpdate = DateTime.Now;
                UpdateTelemetryOverlay();
            }
        }

        private DateTime _lastTelemetryUpdate = DateTime.MinValue;

        private void UpdateTelemetryOverlay(bool isEditMode = false)
        {
            if (_viewModel.IsTelemetryVisible)
            {
                // Telemetry follows the SUBJECT: in Edit that's the edited clip, its player, and its
                // transform — whatever track it's on. Reading the Track-1 player/transform was why
                // editing an overlay showed irrelevant numbers.
                var activeTransform = _playerControl.ActiveTransform
                                      ?? (_isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB);

                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                var currentActivePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
                var activeOp = _isPlayerAActive ? _opA : _opB;
                if (isEditMode)
                {
                    activeOp = _viewModel.SelectedClip as CinematicOperation;
                    if (_editPlayer != null) currentActivePlayer = _editPlayer;
                }

                string currentFileName = activeOp != null ? System.IO.Path.GetFileName(activeOp.FilePath) : "Transition";
                
                var currentStoryTime = _viewModel.CurrentStoryTime;
                var clipEndTime = activeOp != null ? (activeOp.VideoStartTime + activeOp.OpDuration) : TimeSpan.Zero;
                _playerControl.TelemetryStoryTime.Text = $"Timeline  : {currentStoryTime:hh\\:mm\\:ss\\.ff} / {_viewModel.TotalStoryTime:hh\\:mm\\:ss\\.ff}";
                
                if (currentActivePlayer?.PlaybackSession != null)
                {
                    _playerControl.TelemetryClipTime.Text = $"Clip Time : {currentActivePlayer.PlaybackSession.Position:hh\\:mm\\:ss\\.ff} / {clipEndTime:hh\\:mm\\:ss\\.ff} [{currentFileName}]";
                    uint nw = currentActivePlayer.PlaybackSession.NaturalVideoWidth;
                    uint nh = currentActivePlayer.PlaybackSession.NaturalVideoHeight;
                    if (activeOp != null && (_viewModel.IsOverlaySelected || _isEditingOverlay || activeOp.PlacementWidth < 1.0 || activeOp.PlacementHeight < 1.0))
                    {
                        _playerControl.TelemetryVideoSize.Text = $"PiP Size  : W:{activeOp.PlacementWidth * 100:F1}% H:{activeOp.PlacementHeight * 100:F1}% (Res: {nw}x{nh})";
                    }
                    else
                    {
                        _playerControl.TelemetryVideoSize.Text = $"Resolution: {nw}x{nh} px (100% Full Frame)";
                    }
                    _playerControl.TelemetryVideoSize.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else
                {
                    _playerControl.TelemetryVideoSize.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                
                if (activeTransform != null) {
                    _playerControl.TelemetryOperationInfo.Text = $"Zoom/Pan  : Z:{activeTransform.ScaleX:F2} X:{activeTransform.TranslateX:F0} Y:{activeTransform.TranslateY:F0}";
                }
                
                if (activeOp != null && activeOp.StartMark != null && activeOp.EndMark != null && _playerControl.ActualWidth > 0) {
                    double W = _playerControl.ActualWidth;
                    double H = _playerControl.ActualHeight;
                    
                    double Sc = activeTransform != null ? activeTransform.ScaleX : 1.0;
                    double txc = activeTransform != null ? activeTransform.TranslateX : 0.0;
                    double tyc = activeTransform != null ? activeTransform.TranslateY : 0.0;

                    // Start Mark Box
                    double St_s = activeOp.StartMark.Scale;
                    double txt_s = activeOp.StartMark.X;
                    double tyt_s = activeOp.StartMark.Y;
                    double startLeft = (-W / 2 - txt_s) * (Sc / St_s) + W / 2 + txc;
                    double startTop = (-H / 2 - tyt_s) * (Sc / St_s) + H / 2 + tyc;
                    double startWidth = W * (Sc / St_s);
                    double startHeight = H * (Sc / St_s);

                    // End Mark Box
                    double St_e = activeOp.EndMark.Scale;
                    double txt_e = activeOp.EndMark.X;
                    double tyt_e = activeOp.EndMark.Y;
                    double endLeft = (-W / 2 - txt_e) * (Sc / St_e) + W / 2 + txc;
                    double endTop = (-H / 2 - tyt_e) * (Sc / St_e) + H / 2 + tyc;
                    double endWidth = W * (Sc / St_e);
                    double endHeight = H * (Sc / St_e);

                    _playerControl.TelemetryStartMarkInfo.Text = $"Start Box : L:{startLeft:F0} T:{startTop:F0} W:{startWidth:F0} H:{startHeight:F0} (Z:{activeOp.StartMark.Scale:F2})";
                    
                    if (activeOp.MidMark != null) {
                        double St_m = activeOp.MidMark.Scale;
                        double txt_m = activeOp.MidMark.X;
                        double tyt_m = activeOp.MidMark.Y;
                        double midLeft = (-W / 2 - txt_m) * (Sc / St_m) + W / 2 + txc;
                        double midTop = (-H / 2 - tyt_m) * (Sc / St_m) + H / 2 + tyc;
                        double midWidth = W * (Sc / St_m);
                        double midHeight = H * (Sc / St_m);
                        _playerControl.TelemetryMidMarkInfo.Text   = $"MidBox   : L:{midLeft:F0} T:{midTop:F0} W:{midWidth:F0} H:{midHeight:F0} (Z:{activeOp.MidMark.Scale:F2})";
                        _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    } else {
                        _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    }

                    _playerControl.TelemetryEndMarkInfo.Text   = $"End Box   : L:{endLeft:F0} T:{endTop:F0} W:{endWidth:F0} H:{endHeight:F0} (Z:{activeOp.EndMark.Scale:F2})";
                    _playerControl.TelemetryStartMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    _playerControl.TelemetryEndMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else {
                    _playerControl.TelemetryStartMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    _playerControl.TelemetryEndMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
            }
            else
            {
                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }

        public void UpdateWysiwygOverlay()
        {
            // The Ken Burns edit rectangles belong to Edit mode only, and to the CURRENT SUBJECT
            // (SelectedClip) whatever track it's on — not just Track 1. Keying this off
            // SelectedTimelineNode was why editing an overlay drew nothing. Mode is the authority
            // (during composite play _mode is Arrange, so the rects stay hidden).
            if (_mode != EditorMode.Edit || _viewModel.SelectedClip == null)
            {
                _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            var op = _viewModel.SelectedClip as CinematicOperation;
            var transform = _playerControl.ActiveTransform;
            if (op == null || transform == null) return;

            _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            UpdateTelemetryOverlay(true);

            double W = _playerControl.ActualWidth > 0 ? _playerControl.ActualWidth : 1920;
            double H = _playerControl.ActualHeight > 0 ? _playerControl.ActualHeight : 1080;

            // If we're editing an overlay track, the crop box aspect ratio must match the PiP's aspect ratio.
            double boxW = W;
            double boxH = H;
            if (_viewModel.IsOverlaySelected)
            {
                double pipAspect = (16.0 / 9.0) * (op.PlacementWidth / op.PlacementHeight);
                double videoAspect = W / H;

                // When Scale=1 (UniformToFill), the PiP crop box fits the video on one axis.
                if (pipAspect > videoAspect)
                {
                    boxW = W;
                    boxH = W / pipAspect;
                }
                else
                {
                    boxH = H;
                    boxW = H * pipAspect;
                }
            }

            void DrawRect(Microsoft.UI.Xaml.Shapes.Rectangle rect, SpatialMark targetMark, bool show)
            {
                if (!show || targetMark == null)
                {
                    rect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    return;
                }

                rect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                double Sc = transform.ScaleX;
                double txc = transform.TranslateX;
                double tyc = transform.TranslateY;

                double St = targetMark.Scale;
                double txt = targetMark.X;
                double tyt = targetMark.Y;

                if (St <= 0) St = 1;

                double currentLeft = (-boxW / 2 - txt) * (Sc / St) + W / 2 + txc;
                double currentTop = (-boxH / 2 - tyt) * (Sc / St) + H / 2 + tyc;
                double currentWidth = boxW * (Sc / St);
                double currentHeight = boxH * (Sc / St);

                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, currentLeft);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, currentTop);
                rect.Width = Math.Max(0, currentWidth);
                rect.Height = Math.Max(0, currentHeight);
            }

            DrawRect(_playerControl.WysiwygStartRect, op.StartMark, true);
            DrawRect(_playerControl.WysiwygMidRect, op.MidMark, true);
            DrawRect(_playerControl.WysiwygEndRect, op.EndMark, true);
        }

        // Backfill the true source length from the opened media. Covers clips from older projects
        // saved before SourceDuration was captured (their "Source Length" read 0 and trim couldn't
        // clamp to real bounds). Only fills when missing; setting it re-clamps the trim safely.
        private static void BackfillSourceDuration(CinematicOperation op, MediaPlayer player)
        {
            if (op == null || player?.PlaybackSession == null || op.SourceDuration > TimeSpan.Zero) return;
            var natural = player.PlaybackSession.NaturalDuration;
            if (natural > TimeSpan.Zero) op.SourceDuration = natural;
        }

        public async void EnterEditMode(CinematicOperation op, EditTarget target)
        {
            StopPlayback();
            UpdateTimelineNodesIsPlayingState(op);

            if (string.IsNullOrWhiteSpace(op.FilePath)) return;

            // Track 1 clip entering Edit mode: content input + clip-scoped preview target.
            // isOverlayEdit:false so HideAllOverlays (in StopPlayback above) releases BOTH overlay
            // slots — only the Track 1 clip is shown, nothing on top of it.
            SetEditModeState(op, _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB, isOverlayEdit: false);
            HideAllOverlays();

            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var activeElement = _isPlayerAActive ? _playerA : _playerB;
            var standbyElement = _isPlayerAActive ? _playerB : _playerA;
            var activeTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;

            if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                activePlayer.MediaOpened += handler;
                
                activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                activePlayer.MediaOpened -= handler;
            }
            
            _dispatcher.TryEnqueue(() =>
            {
                activeElement.Opacity = 1;
                standbyElement.Opacity = 0;
            });
            
            activePlayer.Pause();
            
            TimeSpan globalStartTime = TimeSpan.Zero;
            foreach (var node in _viewModel.TimelineNodes)
            {
                if (node == op) break;
                if (node is CinematicOperation prevOp)
                {
                    globalStartTime += prevOp.OpDuration + prevOp.TransitionDuration;
                }
            }
            
            _dispatcher.TryEnqueue(() =>
            {
                _viewModel.CurrentStoryTime = globalStartTime;
            });

            SpatialMark markToEdit;
            if (target == EditTarget.Start)
            {
                activePlayer.PlaybackSession.Position = op.VideoStartTime;
                markToEdit = op.StartMark;
            }
            else if (target == EditTarget.Mid && op.MidMark != null)
            {
                activePlayer.PlaybackSession.Position = op.VideoStartTime + TimeSpan.FromSeconds(op.OpDuration.TotalSeconds / 2);
                markToEdit = op.MidMark;
            }
            else
            {
                activePlayer.PlaybackSession.Position = op.VideoStartTime + op.OpDuration;
                markToEdit = op.EndMark;
            }
            
            _dispatcher.TryEnqueue(() =>
            {
                if (activePlayer.PlaybackSession != null)
                {
                    BackfillSourceDuration(op, activePlayer);
                    _viewModel.CurrentOperationDuration = activePlayer.PlaybackSession.NaturalDuration;
                    _viewModel.CurrentOperationTime = activePlayer.PlaybackSession.Position;
                }
                activeTransform.ScaleX = markToEdit.Scale;
                activeTransform.ScaleY = markToEdit.Scale;
                activeTransform.TranslateX = markToEdit.X;
                activeTransform.TranslateY = markToEdit.Y;
                _playerControl.ActiveTransform = activeTransform;
                UpdateWysiwygOverlay();
            });
        }

        // Static composite seek (§7G): show the composite at story-time t WITHOUT playing — the
        // spine frame (main player seeked to the right clip + offset, marks applied) plus the
        // overlays active then. Drives the global scrubber. A drag on the timeline means "navigate
        // the whole thing", so if we were in Edit we drop back to Arrange first (once — the mode
        // flip self-limits repeat calls).
        public async void SeekCompositeToStoryTime(TimeSpan t)
        {
            // Pausing leaves the playback loop alive (_isAnimating stays true, _isPaused flips),
            // so guarding on _isAnimating alone made the scrubber dead after play->pause until
            // something else called StopPlayback. Scrubbing while paused means "take over": end
            // the playback loop and settle into Arrange at the scrubbed point.
            if (_isAnimating)
            {
                if (!_isPaused) return;   // actively playing — don't fight it
                StopPlayback();
            }
            if (_mode != EditorMode.Arrange) ExitToArrange();

            var nodes = _viewModel.TimelineNodes;
            if (nodes.Count == 0) return;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            _viewModel.CurrentStoryTime = t;

            int index = _viewModel.GetTimelineIndexForStoryTime(t);
            if (index < 0 || index >= nodes.Count) return;
            var op = nodes[index];
            if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) return;

            TimeSpan offset = t - _viewModel.GetSpineClipStart(index);
            if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
            if (offset > op.OpDuration) offset = op.OpDuration;

            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var activeElement = _isPlayerAActive ? _playerA : _playerB;
            var standbyElement = _isPlayerAActive ? _playerB : _playerA;
            var activeTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;

            if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                activePlayer.MediaOpened += handler;
                activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                activePlayer.MediaOpened -= handler;
            }

            activeElement.Opacity = 1;
            standbyElement.Opacity = 0;
            activePlayer.Pause();
            if (activePlayer.PlaybackSession != null)
                activePlayer.PlaybackSession.Position = op.VideoStartTime + offset;

            double progress = op.OpDuration.TotalMilliseconds > 0 ? offset.TotalMilliseconds / op.OpDuration.TotalMilliseconds : 0;
            ApplyMarksAtProgress(op, progress, activeTransform);
            _playerControl.ActiveTransform = activeTransform;

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
        private void EvaluateOverlays(TimeSpan currentStoryTime)
        {
            // EDIT MODE OWNS THE SCREEN. It shows exactly ONE clip full-screen, and it manages the
            // overlay surfaces itself (HideAllOverlays / EnterOverlayEditMode). If we ran here we
            // would paint the other tracks' stills over the clip being edited — three videos
            // instead of one — and could stomp the edit view that was just set up.
            if (_mode != EditorMode.Arrange) return;

            var tracks = _viewModel.OverlayTracks;

            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                var desired = i < tracks.Count ? ResolveActiveClip(tracks[i], currentStoryTime) : null;

                // ---- ARRANGE: a pure-model, video-free path (§7A). Showing a still must NOT go
                // through the video-activation pipeline. It used to, which is why a still only
                // appeared after playing (a player had to be loaded first) and why reshaping could
                // re-attach a surface and go black. Nothing here touches a MediaPlayer.
                if (!IsActivelyPlaying)
                {
                    _activeOverlay[i] = desired;
                    if (desired == null) { SetOverlayRender(i, OverlayRender.Hidden, null); continue; }

                    // Shape the box from the clip's REAL aspect. Never assume a default — an
                    // assumed 16:9 silently forces portrait clips (and portrait photos) into a
                    // landscape box and crops the subject. If we genuinely don't know it yet,
                    // leave it unknown: ApplyOverlayBox skips, and we render once we do know.
                    double aspect = desired.SourceAspect;
                    if (aspect <= 0)
                    {
                        // SingleItem thumbnails preserve the item's own aspect, so this is a valid
                        // secondary source (it was not, back when we used VideosView).
                        var thumb = desired.Thumbnail;
                        if (thumb != null && thumb.PixelWidth > 0 && thumb.PixelHeight > 0)
                            aspect = (double)thumb.PixelWidth / thumb.PixelHeight;
                    }
                    if (aspect > 0) _overlayAspect[i] = aspect;

                    // If we still don't know the shape, ApplyOverlayBox would bail and leave the
                    // box at whatever the PREVIOUS clip set — showing this still in the wrong
                    // rectangle. Show nothing until the aspect is known instead.
                    if (_overlayAspect[i] <= 0) { SetOverlayRender(i, OverlayRender.Hidden, null); continue; }

                    SetOverlayRender(i, OverlayRender.Still, desired);
                    ApplyOverlayBox(i, desired, false);
                    continue;
                }

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
                    ApplyOverlayTransform(i, _activeOverlay[i], currentStoryTime);
                    SetOverlayRender(i, OverlayRender.Video, _activeOverlay[i]);
                }
                else SetOverlayRender(i, OverlayRender.Hidden, null);
            }
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

            switch (mode)
            {
                case OverlayRender.Hidden:
                    DetachOverlayVideo(track);
                    v.Still.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    v.Still.Source = null;
                    if (v.Frame != null) v.Frame.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    v.Grid.Opacity = 0;
                    break;

                case OverlayRender.Still:
                    DetachOverlayVideo(track);              // the invariant
                    v.Still.Source = clip?.Thumbnail;
                    v.Still.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    // A frame marks every arrangeable PiP. No drawn handles: reshape grab-zones
                    // are geometric edge/corner bands on the InputLayer, so handles were decoration.
                    // The SELECTED clip's frame shows at full strength and the rest recede — now
                    // that selection is possible while arranging, the chrome can reflect it.
                    if (v.Frame != null)
                    {
                        v.Frame.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        bool selected = clip != null && ReferenceEquals(clip, _viewModel.SelectedClip);
                        v.Frame.Opacity = selected ? 1.0 : 0.55;
                    }
                    v.Grid.Opacity = clip?.Opacity ?? 1.0;
                    break;

                case OverlayRender.Video:
                    v.Still.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    if (v.Frame != null) v.Frame.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    AttachOverlayVideo(track);
                    v.Grid.Opacity = clip?.Opacity ?? 1.0;
                    break;
            }
        }


        // A MediaPlayerElement with no MediaPlayer has no video surface to render at all.
        private void DetachOverlayVideo(int track)
        {
            var video = _playerControl.OverlayVisuals[track].Video;
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
        public void RefreshComposite()
        {
            if (IsActivelyPlaying) return;
            if (_mode != EditorMode.Arrange) return;   // never redraw the composite over Edit mode
            EvaluateOverlays(_viewModel.CurrentStoryTime);
        }

        // The overlay clip currently shown in a given track's box (null if none) — used by
        // double-tap-to-edit to know which clip a PiP represents.
        public CinematicOperation GetActiveOverlay(int track)
            => (track >= 0 && track < MaxOverlayTracks) ? _activeOverlay[track] : null;

        // Strict track ⇒ the first clip whose window contains t is the only one.
        private static CinematicOperation ResolveActiveClip(TimelineTrack track, TimeSpan t)
        {
            foreach (var clip in track.Clips)
                if (clip.IsActiveAt(t)) return clip;
            return null;
        }

        private void ActivateOverlaySlot(int slot, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            var player = _overlayPlayer[slot];
            var grid = _playerControl.OverlayVisuals[slot].Grid;

            // Mark active immediately so repeated per-frame EvaluateOverlays ticks don't
            // re-trigger this while the media is still opening asynchronously.
            _activeOverlay[slot] = overlay;

            grid.Opacity = overlay.Opacity;

            bool needsNewSource = player.Source == null ||
                !string.Equals((player.Source as MediaSource)?.Uri?.LocalPath, overlay.FilePath, StringComparison.OrdinalIgnoreCase);

            if (needsNewSource)
            {
                // MediaPlayer.PlaybackSession isn't seekable until MediaOpened fires — seeking
                // (or even touching PlaybackSession) before then throws. Defer the seek/play
                // until the media actually finishes opening instead of doing it synchronously.
                void OnOpened(MediaPlayer sender, object args)
                {
                    sender.MediaOpened -= OnOpened;

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

                player.MediaOpened += OnOpened;
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

        // Positions, sizes and clips the placement box (the overlay grid) from the clip's
        // placement fields, shaped to the video aspect. In edit mode the box fills the screen
        // (placement bypassed) so content is framed full-size, identical to Track 1; at
        // playback it is the corner PiP. The grid clips its content so zoomed-in framing can't
        // spill outside the box.
        private void ApplyOverlayBox(int slot, CinematicOperation overlay, bool editMode)
        {
            var grid = _playerControl.OverlayVisuals[slot].Grid;
            double aspect = _overlayAspect[slot];
            double vpW = _playerControl.ActualWidth;
            double vpH = _playerControl.ActualHeight;
            if (aspect <= 0 || vpW <= 0 || vpH <= 0) return;

            // Video fit to viewport (contained), preserving aspect — the "scale 1" reference.
            double fitW, fitH;
            if (aspect >= vpW / vpH) { fitW = vpW; fitH = vpW / aspect; }
            else { fitH = vpH; fitW = vpH * aspect; }

            // Edit mode: box fills the video fit (framing at full size). Arrange: independent
            // width/height so the PiP can be reshaped; the video crop-fills (UniformToFill).
            double sw = editMode ? 1.0 : overlay.PlacementWidth;
            double sh = editMode ? 1.0 : overlay.PlacementHeight;
            double cx = editMode ? 0.5 : overlay.PlacementCenterX;
            double cy = editMode ? 0.5 : overlay.PlacementCenterY;

            double boxW = fitW * sw;
            double boxH = fitH * sh;
            double left = cx * vpW - boxW / 2;
            double top = cy * vpH - boxH / 2;

            // NOTE (§7A): this method does GEOMETRY ONLY. Deciding still-vs-video used to live here
            // and silently never fired — the render mode is now set explicitly by SetOverlayRender
            // at each state transition, never as a side effect of laying out a box.

            if (grid.Margin.Left != left || grid.Margin.Top != top)
            {
                grid.Margin = new Microsoft.UI.Xaml.Thickness(left, top, 0, 0);
            }
            // Only resize + reallocate the clip when the box dimensions actually change
            // (avoids per-frame allocation during playback).
            if (grid.Width != boxW || grid.Height != boxH)
            {
                grid.Width = boxW;
                grid.Height = boxH;
                grid.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, boxW, boxH)
                };
            }
        }

        private void SeekAndPlayOverlay(MediaPlayer player, CinematicOperation overlay, TimeSpan currentStoryTime)
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

            player.PlaybackSession.Position = targetPosition;

            double combinedSpeed = clipSpeed * _viewModel.PlaybackSpeed;
            if (pastEnd || combinedSpeed <= 0)
            {
                player.Pause();   // held frame, or a still (speed 0)
            }
            else if (_isAnimating && !_isPaused)
            {
                player.PlaybackSession.PlaybackRate = combinedSpeed;
                player.Volume = overlay.Volume; // overlays default muted; per-clip volume opts in
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
            var transform = _playerControl.OverlayVisuals[slot].Transform;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            grid.ClearValue(Microsoft.UI.Xaml.FrameworkElement.WidthProperty);
            grid.ClearValue(Microsoft.UI.Xaml.FrameworkElement.HeightProperty);
            grid.Clip = null;
            grid.Margin = new Microsoft.UI.Xaml.Thickness(0);

            _activeOverlay[slot] = null;
            _overlayAspect[slot] = 0;
        }

        private void ApplyOverlayTransform(int slot, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            var transform = _playerControl.OverlayVisuals[slot].Transform;

            // Content framing interpolated over the overlay's OWN duration (Ken Burns / push-in),
            // using the same marks + curve as Track 1. Static clip = StartMark == EndMark.
            double rawProgress = overlay.OpDuration.TotalMilliseconds > 0
                ? (currentStoryTime - overlay.StartTime).TotalMilliseconds / overlay.OpDuration.TotalMilliseconds
                : 0;
            ApplyMarksAtProgress(overlay, rawProgress, transform, overlay.PlacementWidth, overlay.PlacementHeight);

            // Placement box (where/how big on screen), clipped so framing can't spill out.
            ApplyOverlayBox(slot, overlay, false);
        }

        // Applies a clip's Start/Mid/End marks to a transform at the given progress (0..1),
        // eased by the clip's CurveProfile. Shared by Track 1 (UpdateSpatial) and upper-track
        // overlay content so motion behaves identically on every track.
        private void ApplyMarksAtProgress(CinematicOperation op, double rawProgress, Microsoft.UI.Xaml.Media.CompositeTransform transform, double panScaleX = 1.0, double panScaleY = 1.0)
        {
            if (op == null || transform == null) return;
            double progress = Math.Clamp(rawProgress, 0, 1);

            double easedProgress = progress;
            if (op.CurveProfile == CurveProfile.Bezier)
                easedProgress = progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2;
            else if (op.CurveProfile == CurveProfile.DirectorsArc)
                easedProgress = 1 - Math.Pow(1 - progress, 3);

            double newScaleX, newTranslateX, newTranslateY;
            if (op.MidMark != null)
            {
                if (easedProgress < 0.5)
                {
                    double p = easedProgress * 2;
                    newScaleX = op.StartMark.Scale + (op.MidMark.Scale - op.StartMark.Scale) * p;
                    newTranslateX = op.StartMark.X + (op.MidMark.X - op.StartMark.X) * p;
                    newTranslateY = op.StartMark.Y + (op.MidMark.Y - op.StartMark.Y) * p;
                }
                else
                {
                    double p = (easedProgress - 0.5) * 2;
                    newScaleX = op.MidMark.Scale + (op.EndMark.Scale - op.MidMark.Scale) * p;
                    newTranslateX = op.MidMark.X + (op.EndMark.X - op.MidMark.X) * p;
                    newTranslateY = op.MidMark.Y + (op.EndMark.Y - op.MidMark.Y) * p;
                }
            }
            else
            {
                newScaleX = op.StartMark.Scale + (op.EndMark.Scale - op.StartMark.Scale) * easedProgress;
                newTranslateX = op.StartMark.X + (op.EndMark.X - op.StartMark.X) * easedProgress;
                newTranslateY = op.StartMark.Y + (op.EndMark.Y - op.StartMark.Y) * easedProgress;
            }

            newTranslateX *= panScaleX;
            newTranslateY *= panScaleY;

            if (Math.Abs(transform.ScaleX - newScaleX) > 0.0001)
            {
                transform.ScaleX = newScaleX;
                transform.ScaleY = newScaleX;
            }
            if (Math.Abs(transform.TranslateX - newTranslateX) > 0.01) transform.TranslateX = newTranslateX;
            if (Math.Abs(transform.TranslateY - newTranslateY) > 0.01) transform.TranslateY = newTranslateY;
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

            if (drift > TimeSpan.FromMilliseconds(200))
            {
                player.PlaybackSession.Position = expectedPosition;
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
                if (player.Volume != overlay.Volume) player.Volume = overlay.Volume;
                if (player.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing)
                {
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

        // ==================== Two modes: Arrange (default) and Edit ====================
        //
        // Strict segregation — the mode alone decides what input does, nothing else:
        //   Arrange (default): the whole composite; Play plays everything; drag a PiP to move
        //                      it, wheel to resize (InputMode = ArrangePips).
        //   Edit:              ONE clip full-screen; frame its content + motion; Play previews
        //                      ONLY that clip's Ken Burns (InputMode = Content).
        // You enter Edit by selecting a clip in the dock; Exit returns to Arrange.

        private enum EditorMode { Arrange, Edit }
        private EditorMode _mode = EditorMode.Arrange;
        public bool IsEditMode => _mode == EditorMode.Edit;

        // The single clip being edited + the player showing it (main player for Track 1, overlay
        // player for Track 2). Used by the clip-scoped Edit-mode preview.
        private CinematicOperation _editClip;
        private MediaPlayer _editPlayer;

        // Put the app into Edit mode for the given clip/player. isOverlayEdit = true when the clip
        // is a Track 2 overlay (edited in the overlay player); false for a Track 1 clip. The flag
        // decides whether the subsequent HideAllOverlays keeps overlay slot 1 (the edit surface).
        private void SetEditModeState(CinematicOperation clip, MediaPlayer player, bool isOverlayEdit)
        {
            StopEditPreview();
            _mode = EditorMode.Edit;
            _editClip = clip;
            _editPlayer = player;
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
            _editPlayer = null;
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
            _viewModel.IsEditMode = false;
            UpdateWysiwygOverlay();                        // hide Track-1 edit rectangles
            // Restore the Track 1 base (a Track 2 edit had hidden the main players).
            _playerA.Opacity = _isPlayerAActive ? 1 : 0;
            _playerB.Opacity = _isPlayerAActive ? 0 : 1;
            EvaluateOverlays(_viewModel.CurrentStoryTime); // show the composite's active PiPs
        }

        // Single entry point for editing ANY clip. Dispatches to the correct surface (spine clips
        // live in the main players; overlay clips in the overlay player) but every clip goes
        // through one call, one Edit state, one WYSIWYG/telemetry path — that's the "one Edit
        // pipeline" contract the UI relies on.
        public void BeginEdit(CinematicOperation clip, EditTarget target)
        {
            if (clip == null) return;
            if (_viewModel.TimelineNodes.Contains(clip)) EnterEditMode(clip, target);
            else EnterOverlayEditMode(clip, target);
        }

        // Edit an overlay clip's content full-screen — same idea as Track 1's EnterEditMode, but
        // the clip lives in the overlay player. Zoom & Motion controls apply to its content, and
        // Start/Mid/End target selection works identically.
        public async void EnterOverlayEditMode(CinematicOperation overlay, EditTarget target = EditTarget.Start)
        {
            if (overlay == null || string.IsNullOrWhiteSpace(overlay.FilePath)) return;

            // Track 0's surface is reused as the full-screen content-edit surface for ANY upper
            // clip, whichever track it belongs to; every other track is hidden while editing.
            SetEditModeState(overlay, _overlayPlayer[0], isOverlayEdit: true);
            StopPlayback();
            UpdateWysiwygOverlay();
            for (int i = 1; i < MaxOverlayTracks; i++)
                if (_activeOverlay[i] != null) ReleaseOverlaySlot(i);

            _activeOverlay[0] = overlay;

            var player = _overlayPlayer[0];
            var grid = _playerControl.OverlayVisuals[0].Grid;
            var transform = _playerControl.OverlayVisuals[0].Transform;

            if (player.Source == null || !string.Equals((player.Source as MediaSource)?.Uri?.LocalPath, overlay.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                player.MediaOpened += handler;
                player.Source = MediaSource.CreateFromUri(new Uri(overlay.FilePath));
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                player.MediaOpened -= handler;
            }
            if (_activeOverlay[0] != overlay) return;

            // Resolve the edit target to a source position + the mark being framed (same three
            // targets as Track 1). Seek within the clip's own trimmed source window.
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
                markToEdit = overlay.EndMark;
            }
            else
            {
                seekPos = overlay.VideoStartTime;
                markToEdit = overlay.StartMark;
            }

            if (player.PlaybackSession != null) player.PlaybackSession.Position = seekPos;
            player.Pause();

            _dispatcher.TryEnqueue(() =>
            {
                if (_activeOverlay[0] != overlay) return;
                transform.ScaleX = markToEdit.Scale;
                transform.ScaleY = markToEdit.Scale;
                transform.TranslateX = markToEdit.X;
                transform.TranslateY = markToEdit.Y;
                // Must cache into the SAME index the edit surface uses (0) — caching elsewhere
                // leaves a stale aspect here, which sizes the box to the wrong shape and makes
                // UniformToFill crop the clip (e.g. a portrait clip shown as a cropped landscape).
                CacheOverlayAspect(0, player);
                SetOverlayRender(0, OverlayRender.Video, overlay); // live video for content framing
                ApplyOverlayBox(0, overlay, true);                 // full-screen box
                grid.Opacity = 1.0;
                // Hide the Track 1 base so ONLY this overlay clip is shown while editing it.
                _playerA.Opacity = 0;
                _playerB.Opacity = 0;
                _playerControl.ActiveTransform = transform;
                // Per-clip scrubber range + the WYSIWYG framing rects, exactly as Track 1 does.
                if (player.PlaybackSession != null)
                {
                    BackfillSourceDuration(overlay, player);
                    _viewModel.CurrentOperationDuration = player.PlaybackSession.NaturalDuration;
                    _viewModel.CurrentOperationTime = player.PlaybackSession.Position;
                }
                RenderPausedFrame(player); // show the frame now, not just after you press play
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
        private DateTime _editPreviewStart;

        public void ToggleEditPreview()
        {
            if (_editPreviewPlaying) StopEditPreview();
            else StartEditPreview();
        }

        private void StartEditPreview()
        {
            if (_editClip == null || _editPlayer?.PlaybackSession == null) return;
            _editPreviewPlaying = true;
            _editPreviewStart = DateTime.Now;
            _editPlayer.PlaybackSession.Position = _editClip.VideoStartTime;

            // Respect the clip's own speed. Speed 0 = a STILL: freeze the frame; the Ken Burns
            // marks still animate over OpDuration below. (Was hardcoded to 1.0 + Play, so a
            // speed-0 clip wrongly ran at full speed.)
            double clipSpeed = _editClip.PlaybackSpeed;
            _editPlayer.Volume = _editClip.Volume;
            if (clipSpeed > 0)
            {
                _editPlayer.PlaybackSession.PlaybackRate = clipSpeed;
                _editPlayer.Play();
            }
            else
            {
                _editPlayer.Pause();
            }
            CompositionTarget.Rendering += EditPreview_Rendering;
            _viewModel.IsPlaying = true;
        }

        private void StopEditPreview()
        {
            if (!_editPreviewPlaying) return;
            _editPreviewPlaying = false;
            CompositionTarget.Rendering -= EditPreview_Rendering;
            _editPlayer?.Pause();
            _viewModel.IsPlaying = false;
        }

        private void EditPreview_Rendering(object? sender, object e)
        {
            if (_editClip == null || _playerControl.ActiveTransform == null) return;
            // Apply Volume live so the audio slider works while the preview is playing (overlays
            // start muted, so a one-time apply at play meant raising it did nothing until restart).
            if (_editPlayer != null) _editPlayer.Volume = _editClip.Volume;
            double dur = _editClip.OpDuration.TotalSeconds;
            if (dur <= 0) dur = 1;
            double progress = (DateTime.Now - _editPreviewStart).TotalSeconds / dur;
            if (_editPlayer?.PlaybackSession != null)
            {
                progress = (_editPlayer.PlaybackSession.Position - _editClip.VideoStartTime).TotalSeconds / dur;
            }
            if (progress >= 1.0)
            {
                _editPreviewStart = DateTime.Now; // loop the preview
                progress = 0;
                if (_editPlayer?.PlaybackSession != null)
                {
                    _editPlayer.PlaybackSession.Position = _editClip.VideoStartTime;
                    // Resume if it hit end-of-media mid-loop; a still (speed 0) stays paused.
                    if (_editClip.PlaybackSpeed > 0) _editPlayer.Play();
                }
            }
            ApplyMarksAtProgress(_editClip, Math.Clamp(progress, 0.0, 1.0), _playerControl.ActiveTransform);

            // Drive the per-clip scrubber off the real decode position so it tracks the preview.
            // (Assigning CurrentOperationTime — not …Seconds — only notifies the slider; it does
            // not fire a seek back into the player, so there's no feedback loop.)
            if (_editPlayer?.PlaybackSession != null)
                _viewModel.CurrentOperationTime = _editPlayer.PlaybackSession.Position;

            // Keep the telemetry HUD live while previewing in Edit — the composite render loop that
            // normally drives it doesn't run here, so without this it froze until you paused.
            if ((DateTime.Now - _lastTelemetryUpdate).TotalMilliseconds >= 100)
            {
                _lastTelemetryUpdate = DateTime.Now;
                UpdateTelemetryOverlay(true);
            }
        }

        // ---- Arrange mode: drag / wheel the PiP under the cursor (the hit slot) ----

        private void OnOverlayBoxDragged(object? sender, (int slot, Views.BoxGrab grab, double dx, double dy) e)
        {
            if (_mode != EditorMode.Arrange) return;
            // §7A invariant: never manipulate a live video surface. While ACTIVELY playing the PiP
            // IS that surface (handles hidden for the same reason). Paused counts as Arrange.
            if (IsActivelyPlaying) return;
            var overlay = _activeOverlay[e.slot];
            if (overlay == null) return;
            double vpW = _playerControl.ActualWidth, vpH = _playerControl.ActualHeight;
            if (vpW <= 0 || vpH <= 0) return;

            // Interior grab = translate the whole box.
            if (e.grab == Views.BoxGrab.Move)
            {
                overlay.PlacementCenterX += e.dx / vpW;
                overlay.PlacementCenterY += e.dy / vpH;
                ApplyOverlayBox(e.slot, overlay, false);
                return;
            }

            // Edge/corner grab = reshape. Work in pixels: move only the grabbed edges, keep the
            // opposite edges anchored, then convert back to independent width/height + centre.
            double aspect = _overlayAspect[e.slot];
            if (aspect <= 0) return;
            double fitW, fitH;
            if (aspect >= vpW / vpH) { fitW = vpW; fitH = vpW / aspect; }
            else { fitH = vpH; fitW = vpH * aspect; }

            double boxW = fitW * overlay.PlacementWidth;
            double boxH = fitH * overlay.PlacementHeight;
            double cxPx = overlay.PlacementCenterX * vpW;
            double cyPx = overlay.PlacementCenterY * vpH;
            double left = cxPx - boxW / 2, right = cxPx + boxW / 2;
            double top = cyPx - boxH / 2, bottom = cyPx + boxH / 2;

            var g = e.grab;
            bool moveLeft = g == Views.BoxGrab.Left || g == Views.BoxGrab.TopLeft || g == Views.BoxGrab.BottomLeft;
            bool moveRight = g == Views.BoxGrab.Right || g == Views.BoxGrab.TopRight || g == Views.BoxGrab.BottomRight;
            bool moveTop = g == Views.BoxGrab.Top || g == Views.BoxGrab.TopLeft || g == Views.BoxGrab.TopRight;
            bool moveBottom = g == Views.BoxGrab.Bottom || g == Views.BoxGrab.BottomLeft || g == Views.BoxGrab.BottomRight;

            const double minPx = 24;
            if (moveLeft) left = Math.Min(left + e.dx, right - minPx);
            if (moveRight) right = Math.Max(right + e.dx, left + minPx);
            if (moveTop) top = Math.Min(top + e.dy, bottom - minPx);
            if (moveBottom) bottom = Math.Max(bottom + e.dy, top + minPx);

            overlay.PlacementWidth = (right - left) / fitW;
            overlay.PlacementHeight = (bottom - top) / fitH;
            overlay.PlacementCenterX = ((left + right) / 2) / vpW;
            overlay.PlacementCenterY = ((top + bottom) / 2) / vpH;
            ApplyOverlayBox(e.slot, overlay, false);
        }

        private void OnOverlayBoxWheel(object? sender, (int slot, int delta) e)
        {
            if (_mode != EditorMode.Arrange) return;
            if (IsActivelyPlaying) return;   // same invariant: no resizing a live video surface
            var overlay = _activeOverlay[e.slot];
            if (overlay == null) return;
            // Wheel = uniform resize: scales both dimensions, preserving the box's current shape.
            double f = e.delta > 0 ? 1.08 : 1.0 / 1.08;
            overlay.PlacementWidth *= f;
            overlay.PlacementHeight *= f;
            ApplyOverlayBox(e.slot, overlay, false);
        }
    }
}
