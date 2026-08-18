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
        private Microsoft.UI.Xaml.DispatcherTimer _playbackTimer;
        private DateTime _lastTickTime;
        private readonly MediaPlayer[] _overlayPlayer = new MediaPlayer[MaxOverlayTracks];
        private readonly CinematicOperation[] _activeOverlay = new CinematicOperation[MaxOverlayTracks];
        private readonly double[] _overlayAspect = new double[MaxOverlayTracks];
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
            _playerControl.WysiwygBoxManipulated += OnWysiwygBoxManipulated;
            _playerControl.WysiwygBoxGrabbed += OnWysiwygBoxGrabbed;
            _playerControl.OverlayBoxWheel += OnOverlayBoxWheel;
            _playerControl.OverlayBoxPointerPressed += OnOverlayBoxPointerPressed;
            _playerControl.MakeFullScreenRequested += OnMakeFullScreenRequested;
            _playerControl.MakeWindowRequested += OnMakeWindowRequested;
            _playerControl.EditClipRequested += OnEditClipRequested;
            _playerControl.BorderTypeRequested += OnBorderTypeRequested;
            _playerControl.BorderColorRequested += OnBorderColorRequested;
            _playerControl.BorderThicknessRequested += OnBorderThicknessRequested;
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
        }

        private void OnTimelineSequenceChanged()
        {
            
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
                    if (startIdx >= 0 && startIdx < _viewModel.Tracks.Count
                        && offset > _viewModel.TotalStoryTime) offset = TimeSpan.Zero;

                    _ = StartPlaybackAsync(startIdx, offset);
                }
            }
        }
private void ViewModel_PlaybackSpeedChanged(object? sender, double speed)
        {
            if (_playbackTimer != null) _playbackTimer.Interval = TimeSpan.FromMilliseconds(16);
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
            if (_playbackTimer == null || !_playbackTimer.IsEnabled)
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
            _lastTickTime = DateTime.Now;
            _viewModel.IsPlaying = true;
            
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

            if (_playbackTimer == null)
            {
                _playbackTimer = new Microsoft.UI.Xaml.DispatcherTimer();
                _playbackTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60fps
                _playbackTimer.Tick += PlaybackTimer_Tick;
            }
            _lastTickTime = DateTime.Now;
            _playbackTimer.Start();
        }

        public void StopPlayback()
        {
            _playbackTimer?.Stop();
            
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

            var now = DateTime.Now;
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
                    // Drive the master timeline clock directly from the Track 1 hardware decoder.
                    // This prevents the UI thread (which can drop frames during heavy Ken Burns zooming)
                    // from running ahead and forcing stuttering drift-correction seeks.
                    _viewModel.CurrentStoryTime = mainOp.StartTime + TimeSpan.FromSeconds(videoElapsed);
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
            
            if ((now - _lastTelemetryUpdate).TotalMilliseconds >= 100)
            {
                _lastTelemetryUpdate = now;
                UpdateTelemetryOverlay();
            }
        }

        public void SkipNext() 
        { 
            if (_mode == EditorMode.Arrange)
                SeekCompositeToStoryTime(_viewModel.CurrentStoryTime + TimeSpan.FromMilliseconds(33.33));
        }

        public void SkipPrevious() 
        { 
            if (_mode == EditorMode.Arrange)
                SeekCompositeToStoryTime(_viewModel.CurrentStoryTime - TimeSpan.FromMilliseconds(33.33));
        }

        


        private DateTime _lastTelemetryUpdate = DateTime.MinValue;
private void UpdateTelemetryOverlay(bool isEditMode = false)
        {
            if (_viewModel.IsTelemetryVisible)
            {
                var activeTransform = _playerControl.ActiveTransform;
                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                var currentActivePlayer = isEditMode ? _overlayPlayer[0] : null;
                var activeOp = isEditMode ? _viewModel.SelectedClip as CinematicOperation : null;

                string currentFileName = activeOp != null ? System.IO.Path.GetFileName(activeOp.FilePath) : "";
                
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

                    double St_s = activeOp.StartMark.Scale;
                    double txt_s = activeOp.StartMark.X;
                    double tyt_s = activeOp.StartMark.Y;
                    double startLeft = (-W / 2 - txt_s) * (Sc / St_s) + W / 2 + txc;
                    double startTop = (-H / 2 - tyt_s) * (Sc / St_s) + H / 2 + tyc;
                    double startWidth = W * (Sc / St_s);
                    double startHeight = H * (Sc / St_s);

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

            double vpW = _playerControl.ActualWidth > 0 ? _playerControl.ActualWidth : 1920;
            double vpH = _playerControl.ActualHeight > 0 ? _playerControl.ActualHeight : 1080;

            // In Edit mode, the clip being edited is always isolated into slot 0.
            double aspect = _overlayAspect[0];
            if (aspect <= 0) aspect = 16.0 / 9.0;

            // Compute the true physical bounds of the video on the screen in Edit mode (scale 1.0)
            double W, H;
            if (aspect >= vpW / vpH) { W = vpW; H = vpW / aspect; }
            else { H = vpH; W = vpH * aspect; }

            // The crop box aspect ratio depends on the video's intrinsic aspect ratio
            double videoAspect = W / H;
            double pipAspect = videoAspect * (op.PlacementWidth / op.PlacementHeight);

            double boxW = W;
            double boxH = H;

            // When Scale=1 (UniformToFill), the crop box fits the video on one axis.
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

            void DrawRect(Microsoft.UI.Xaml.FrameworkElement rect, SpatialMark targetMark, bool show)
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

                // W/2 and H/2 place it relative to the video bounds. Add the centering offset (vpW - W)/2 to map to canvas.
                double currentLeft = (-boxW / 2 - txt) * (Sc / St) + W / 2 + txc + (vpW - W) / 2;
                double currentTop = (-boxH / 2 - tyt) * (Sc / St) + H / 2 + tyc + (vpH - H) / 2;
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
public async void SeekCompositeToStoryTime(TimeSpan t)
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
        private void EvaluateOverlays(TimeSpan currentStoryTime)
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

            if (v.Frame != null && v.Frame.Children.Count > 0 && v.Frame.Children[0] is Microsoft.UI.Xaml.Controls.Border border)
            {
                bool isSelected = clip != null && _viewModel?.SelectedClip == clip;
                border.BorderThickness = new Microsoft.UI.Xaml.Thickness(isSelected ? 5 : 2);
            }

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
                    // are geometric edge/corner bands on the InputLayer, so handles were decoration
                    // that also made chrome depend on a selection you cannot make while arranging.
                    if (v.Frame != null) v.Frame.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
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

            // Apply border styling
            if (overlay.BorderType == BorderType.None || editMode)
            {
                grid.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                HideFilmStrip(grid);
            }
            else
            {
                if (overlay.BorderType == BorderType.FilmStrip)
                {
                    grid.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                    ShowFilmStrip(grid, overlay.BorderColor, overlay.BorderThickness);
                }
                else
                {
                    HideFilmStrip(grid);
                    grid.BorderThickness = new Microsoft.UI.Xaml.Thickness(overlay.BorderThickness);
                    
                    if (overlay.BorderType == BorderType.Soft)
                    {
                        var c = overlay.BorderColor;
                        grid.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(128, c.R, c.G, c.B));
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(16);
                    }
                    else // Solid
                    {
                        grid.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(overlay.BorderColor);
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                    }
                }
            }
        }

        private void HideFilmStrip(Microsoft.UI.Xaml.Controls.Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is Microsoft.UI.Xaml.Shapes.Rectangle r && r.Name == "FilmStripRect")
                {
                    r.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    break;
                }
            }
        }

        private void ShowFilmStrip(Microsoft.UI.Xaml.Controls.Grid grid, Windows.UI.Color color, double thickness)
        {
            Microsoft.UI.Xaml.Shapes.Rectangle dashRect = null;
            foreach (var child in grid.Children)
            {
                if (child is Microsoft.UI.Xaml.Shapes.Rectangle r && r.Name == "FilmStripRect")
                {
                    dashRect = r;
                    break;
                }
            }
            if (dashRect == null)
            {
                dashRect = new Microsoft.UI.Xaml.Shapes.Rectangle() { Name = "FilmStripRect" };
                grid.Children.Add(dashRect);
            }
            dashRect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            dashRect.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            dashRect.StrokeThickness = thickness;
            // A filmstrip-like pattern
            dashRect.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection() { 2, 1, 2, 1 };
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

            if (drift > TimeSpan.FromMilliseconds(200) || (!_isAnimating && drift > TimeSpan.FromMilliseconds(10)) || (_isPaused && drift > TimeSpan.FromMilliseconds(10)))
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
            EnterEditMode(clip, target);
        }

        public async void EnterEditMode(CinematicOperation overlay, EditTarget target = EditTarget.Start)
        {
            if (overlay == null || string.IsNullOrWhiteSpace(overlay.FilePath)) return;

            SetEditModeState(overlay, _overlayPlayer[0], isOverlayEdit: true);
            StopPlayback();
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

            if (player.PlaybackSession != null) player.PlaybackSession.Position = seekPos;
            player.Pause();
            player.StepForwardOneFrame();

            _dispatcher.TryEnqueue(() =>
            {
                if (_activeOverlay[0] != overlay) return;
                transform.ScaleX = markToEdit.Scale;
                transform.ScaleY = markToEdit.Scale;
                transform.TranslateX = markToEdit.X;
                transform.TranslateY = markToEdit.Y;
                _playerControl.ActiveTransform = transform;
                CacheOverlayAspect(0, player);
                SetOverlayRender(0, OverlayRender.Video, overlay); 
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
        private DateTime _editPreviewStart;

        public void ToggleEditPreview()
        {
            if (_editPreviewPlaying) StopEditPreview();
            else StartEditPreview();
        }

        private void StartEditPreview()
        {
            if (_editClip == null || _overlayPlayer[0]?.PlaybackSession == null) return;
            _editPreviewPlaying = true;
            _editPreviewStart = DateTime.Now;
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
            double progress = (DateTime.Now - _editPreviewStart).TotalSeconds / dur;
            if (_overlayPlayer[0]?.PlaybackSession != null && _editClip.PlaybackSpeed > 0 && !_editClip.IsStill)
            {
                double videoElapsed = (_overlayPlayer[0].PlaybackSession.Position - _editClip.VideoStartTime).TotalSeconds / _editClip.PlaybackSpeed;
                progress = videoElapsed / dur;
            }
            if (progress >= 1.0)
            {
                _editPreviewStart = DateTime.Now; // loop the preview
                progress = 0;
                if (_overlayPlayer[0]?.PlaybackSession != null)
                {
                    _overlayPlayer[0].PlaybackSession.Position = _editClip.VideoStartTime;
                    // Resume if it hit end-of-media mid-loop; a still (speed 0) stays paused.
                    if (_editClip.PlaybackSpeed > 0) _overlayPlayer[0].Play();
                }
            }
            ApplyMarksAtProgress(_editClip, Math.Clamp(progress, 0.0, 1.0), _playerControl.ActiveTransform);

            // Drive the per-clip scrubber off the real decode position so it tracks the preview.
            // (Assigning CurrentOperationTime — not …Seconds — only notifies the slider; it does
            // not fire a seek back into the player, so there's no feedback loop.)
            if (_overlayPlayer[0]?.PlaybackSession != null)
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

        // ---- Arrange mode: drag / wheel the PiP under the cursor (the hit slot) ----

        private void OnMakeFullScreenRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                overlay.PlacementWidth = 1.0;
                overlay.PlacementHeight = 1.0;
                overlay.PlacementCenterX = 0.5;
                overlay.PlacementCenterY = 0.5;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(slot, overlay, false));
            }
        }

        private void OnMakeWindowRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                overlay.PlacementWidth = 0.3;
                overlay.PlacementHeight = 0.3;
                overlay.PlacementCenterX = 0.72;
                overlay.PlacementCenterY = 0.72;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(slot, overlay, false));
            }
        }

        private void OnEditClipRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                BeginEdit(overlay, EditTarget.Start);
            }
        }

        private void OnBorderTypeRequested(object? sender, (int Slot, Models.BorderType Type) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.BorderType = args.Type;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(args.Slot, overlay, false));
            }
        }

        private void OnBorderColorRequested(object? sender, (int Slot, Windows.UI.Color Color) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.BorderColor = args.Color;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(args.Slot, overlay, false));
            }
        }

        private void OnBorderThicknessRequested(object? sender, (int Slot, double Thickness) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.BorderThickness = args.Thickness;
                _dispatcher.TryEnqueue(() => ApplyOverlayBox(args.Slot, overlay, false));
            }
        }

        private void OnOverlayBoxPointerPressed(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null) _viewModel.SelectedClip = overlay;
        }

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

        private void OnWysiwygBoxGrabbed(object? sender, string markType)
        {
            if (_mode != EditorMode.Edit || _viewModel.SelectedClip == null) return;
            var op = _viewModel.SelectedClip as CinematicOperation;
            if (op == null) return;

            if (_editPreviewPlaying) StopEditPreview();

            if (markType == "Start") SeekActiveOperation(op.VideoStartTime);
            else if (markType == "Mid" && op.MidMark != null) 
            {
                var midTime = op.VideoStartTime + TimeSpan.FromSeconds((op.VideoEndTime - op.VideoStartTime).TotalSeconds / 2);
                SeekActiveOperation(midTime);
            }
            else if (markType == "End")
            {
                var endSeek = op.VideoEndTime;
                // Unconditionally back off slightly from the end trim point to guarantee we hit a visible frame.
                // If it's the end of the file, this avoids EOS. If it's a trim, it shows the last included frame.
                if (endSeek.TotalMilliseconds > 100)
                {
                    endSeek -= TimeSpan.FromMilliseconds(100);
                    if (endSeek < op.VideoStartTime) endSeek = op.VideoStartTime;
                }
                SeekActiveOperation(endSeek);
            }

            // Poke the decoder so the paused frame updates immediately
            _overlayPlayer[0]?.StepForwardOneFrame();
        }

        private void OnWysiwygBoxManipulated(object? sender, (string markType, string action, double dx, double dy) e)
        {
            if (_mode != EditorMode.Edit || _viewModel.SelectedClip == null) return;
            var op = _viewModel.SelectedClip as CinematicOperation;
            if (op == null) return;

            SpatialMark mark;
            if (e.markType == "Start") mark = op.StartMark;
            else if (e.markType == "Mid") mark = op.MidMark;
            else if (e.markType == "End") mark = op.EndMark;
            else return;

            if (mark == null) return;

            var transform = _playerControl.ActiveTransform;
            if (transform == null) return;

            double vpW = _playerControl.ActualWidth > 0 ? _playerControl.ActualWidth : 1920;
            double vpH = _playerControl.ActualHeight > 0 ? _playerControl.ActualHeight : 1080;

            double aspect = _overlayAspect[0];
            if (aspect <= 0) aspect = 16.0 / 9.0;

            double W, H;
            if (aspect >= vpW / vpH) { W = vpW; H = vpW / aspect; }
            else { H = vpH; W = vpH * aspect; }

            double videoAspect = W / H;
            double pipAspect = videoAspect * (op.PlacementWidth / op.PlacementHeight);

            double boxW = W;
            double boxH = H;
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

            double Sc = transform.ScaleX;
            double txc = transform.TranslateX;
            double tyc = transform.TranslateY;

            double St = mark.Scale;
            double txt = mark.X;
            double tyt = mark.Y;
            if (St <= 0) St = 1;

            if (e.action == "Translate")
            {
                mark.X -= (float)(e.dx / (Sc / St));
                mark.Y -= (float)(e.dy / (Sc / St));
            }
            else
            {
                double deltaW = 0;
                if (e.action == "TL" || e.action == "BL") deltaW = -e.dx;
                else if (e.action == "TR" || e.action == "BR") deltaW = e.dx;

                double currentWidth = boxW * (Sc / St);
                double newWidth = currentWidth + deltaW;
                if (newWidth < 50) newWidth = 50; 

                double newSt = boxW * Sc / newWidth;

                double cx = -txt * (Sc / St) + W / 2 + txc;
                double cy = -tyt * (Sc / St) + H / 2 + tyc;

                double dcx = 0;
                double dcy = 0;
                double deltaH = deltaW * (boxH / boxW);
                
                if (e.action == "TR") { dcy = -deltaH / 2; dcx = deltaW / 2; }
                else if (e.action == "TL") { dcy = -deltaH / 2; dcx = -deltaW / 2; }
                else if (e.action == "BR") { dcy = deltaH / 2; dcx = deltaW / 2; }
                else if (e.action == "BL") { dcy = deltaH / 2; dcx = -deltaW / 2; }

                cx += dcx;
                cy += dcy;

                mark.Scale = (float)newSt;
                mark.X = (float)(-(cx - W / 2 - txc) / (Sc / newSt));
                mark.Y = (float)(-(cy - H / 2 - tyc) / (Sc / newSt));
            }

            UpdateWysiwygOverlay();
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


