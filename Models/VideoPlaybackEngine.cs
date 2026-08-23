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
            _playerControl.WysiwygBoxManipulated += OnWysiwygBoxManipulated;
            _playerControl.WysiwygBoxGrabbed += OnWysiwygBoxGrabbed;
            _playerControl.SelectedMarkWheel += OnSelectedMarkWheel;
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
                
                WriteGeometryTelemetry();

                if (activeTransform != null) {
                    _playerControl.TelemetryOperationInfo.Text = $"Zoom/Pan  : Z:{activeTransform.ScaleX:F2} X:{activeTransform.TranslateX:F0} Y:{activeTransform.TranslateY:F0}";
                }
                
                if (activeOp != null && activeOp.StartMark != null && activeOp.EndMark != null
                    && TryGetMarkSpace(activeOp, out double W, out double H)) {
                    // The video FIT, not the whole pane: that is the space marks live in, and the
                    // boxes reported here are meant to match the ones the editor draws.
                    EnsureMarksNormalized(activeOp);

                    // ...and "match" means the PiP-shaped crop window, not the full frame. This
                    // reported W:1212 for a box the editor drew at W:409, which reads as a framing
                    // that overhangs the picture when it does not. Same derivation as
                    // UpdateWysiwygOverlay, so the two now agree.
                    double videoAspectT = W / H;
                    double pipAspectT = videoAspectT * (activeOp.PlacementWidth / activeOp.PlacementHeight);
                    double bwT = pipAspectT > videoAspectT ? W : H * pipAspectT;
                    double bhT = pipAspectT > videoAspectT ? W / pipAspectT : H;

                    double Sc = activeTransform != null ? activeTransform.ScaleX : 1.0;
                    double txc = activeTransform != null ? activeTransform.TranslateX : 0.0;
                    double tyc = activeTransform != null ? activeTransform.TranslateY : 0.0;

                    double St_s = activeOp.StartMark.Scale;
                    double txt_s = activeOp.StartMark.X * W;
                    double tyt_s = activeOp.StartMark.Y * H;
                    double startLeft = (-bwT / 2 - txt_s) * (Sc / St_s) + W / 2 + txc;
                    double startTop = (-bhT / 2 - tyt_s) * (Sc / St_s) + H / 2 + tyc;
                    double startWidth = bwT * (Sc / St_s);
                    double startHeight = bhT * (Sc / St_s);

                    double St_e = activeOp.EndMark.Scale;
                    double txt_e = activeOp.EndMark.X * W;
                    double tyt_e = activeOp.EndMark.Y * H;
                    double endLeft = (-bwT / 2 - txt_e) * (Sc / St_e) + W / 2 + txc;
                    double endTop = (-bhT / 2 - tyt_e) * (Sc / St_e) + H / 2 + tyc;
                    double endWidth = bwT * (Sc / St_e);
                    double endHeight = bhT * (Sc / St_e);

                    _playerControl.TelemetryStartMarkInfo.Text = $"Start Box : L:{startLeft:F0} T:{startTop:F0} W:{startWidth:F0} H:{startHeight:F0} (Z:{activeOp.StartMark.Scale:F2})";
                    
                    if (activeOp.MidMark != null) {
                        double St_m = activeOp.MidMark.Scale;
                        double txt_m = activeOp.MidMark.X * W;
                        double tyt_m = activeOp.MidMark.Y * H;
                        double midLeft = (-bwT / 2 - txt_m) * (Sc / St_m) + W / 2 + txc;
                        double midTop = (-bhT / 2 - tyt_m) * (Sc / St_m) + H / 2 + tyc;
                        double midWidth = bwT * (Sc / St_m);
                        double midHeight = bhT * (Sc / St_m);
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

        // ==================== Geometry HUD ====================
        //
        // The four numbers that decide what you actually see: where the box is on screen, where the
        // playhead is, what the motion transform is doing, and which part of the SOURCE frame that
        // combination ends up sampling. The last one is the point - it is the only line that tells
        // you whether black on screen is a framing you authored or a bug, and it is what took this
        // long to work out the first time round.
        //
        // Throttled to ~10Hz and skipped entirely when the HUD is hidden, so it costs nothing in
        // the render loop. Every value is read from the live visual tree rather than recomputed, so
        // it reports what the app IS doing, not what it intends to do.
        private DateTime _lastGeometryUpdate = DateTime.MinValue;

        private static string Secs(double s) => $"{s:00.00}";

        private void WriteGeometryTelemetry()
        {
            var line = _playerControl.TelemetryGeometry;
            if (line == null) return;

            if (!_viewModel.IsTelemetryVisible)
            {
                line.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            var now = DateTime.Now;
            if ((now - _lastGeometryUpdate).TotalMilliseconds < 100) return;
            _lastGeometryUpdate = now;

            var sb = new System.Text.StringBuilder();

            for (int slot = 0; slot < MaxOverlayTracks; slot++)
            {
                var op = _activeOverlay[slot];
                if (op == null) continue;

                var vis = _playerControl.OverlayVisuals[slot];
                bool still = vis.Still != null && vis.Still.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
                var surface = still ? (Microsoft.UI.Xaml.FrameworkElement)vis.Still
                                    : (Microsoft.UI.Xaml.FrameworkElement)vis.Video;
                var t = still ? vis.StillTransform : vis.Transform;

                double aspect = AspectOf(op, slot);
                if (aspect <= 0 || !TryGetMarkSpace(op, out double fitW, out double fitH))
                {
                    sb.AppendLine($"T{slot + 1}  waiting for source size");
                    continue;
                }

                bool editMode = _mode == EditorMode.Edit;
                double vpW = _playerControl.ActualWidth, vpH = _playerControl.ActualHeight;

                // Same functions the compositor uses, not a parallel copy - a readout that
                // recomputes its own geometry can agree with itself while disagreeing with what was
                // drawn, which is precisely how a HUD ends up lying.
                var box = ClipGeometry.Box(fitW, fitH, vpW, vpH,
                                           op.PlacementWidth, op.PlacementHeight,
                                           op.PlacementCenterX, op.PlacementCenterY, editMode);
                double boxW = box.W, boxH = box.H, left = box.X, top = box.Y;

                double contentW = surface != null && !double.IsNaN(surface.Width) ? surface.Width : boxW;
                double contentH = surface != null && !double.IsNaN(surface.Height) ? surface.Height : boxH;

                double S = t?.ScaleX ?? 1, tx = t?.TranslateX ?? 0, ty = t?.TranslateY ?? 0;
                if (S <= 0) S = 1;

                // Source pixel dimensions, so the sampled region reads in the units the footage is
                // actually in rather than in pane pixels.
                double srcW = 0, srcH = 0;
                var session = _overlayPlayer[slot]?.PlaybackSession;
                if (!still && session != null && session.NaturalVideoWidth > 0)
                {
                    srcW = session.NaturalVideoWidth; srcH = session.NaturalVideoHeight;
                }
                else if (op.StillFrame != null && op.StillFrame.PixelWidth > 0)
                {
                    srcW = op.StillFrame.PixelWidth; srcH = op.StillFrame.PixelHeight;
                }
                if (srcW <= 0 || srcH <= 0) { srcH = 1080; srcW = 1080 * aspect; }

                // The visible window expressed on the source frame. The content surface holds the
                // WHOLE frame drawn at contentW x contentH, so scaling that ratio converts a
                // pane-pixel window into source pixels.
                var seen = ClipGeometry.SampledSource(contentW, contentH, boxW, boxH, S, tx, ty, srcW, srcH);
                double x0 = seen.X, x1 = seen.Right, y0 = seen.Y, y1 = seen.Bottom;

                var over = new System.Collections.Generic.List<string>();
                if (x0 < -0.5) over.Add($"{-x0 * (boxW / (x1 - x0)):F0}px left");
                if (y0 < -0.5) over.Add($"{-y0 * (boxH / (y1 - y0)):F0}px top");
                if (x1 > srcW + 0.5) over.Add($"{(x1 - srcW) * (boxW / (x1 - x0)):F0}px right");
                if (y1 > srcH + 0.5) over.Add($"{(y1 - srcH) * (boxH / (y1 - y0)):F0}px bottom");

                double into = Math.Max(0, (_viewModel.CurrentStoryTime - op.StartTime).TotalSeconds);
                double dur = op.OpDuration.TotalSeconds;
                var srcPos = op.VideoStartTime + TimeSpan.FromSeconds(into * Math.Max(0, op.PlaybackSpeed));

                sb.AppendLine($"T{slot + 1} {(still ? "still" : "video")}  {System.IO.Path.GetFileName(op.FilePath)}");
                sb.AppendLine($"   time    {Secs(_viewModel.CurrentStoryTime.TotalSeconds)}s of {Secs(_viewModel.TotalStoryDuration.TotalSeconds)}s" +
                              $"   into clip {Secs(into)}s of {Secs(dur)}s" +
                              $"   source {srcPos:hh\\:mm\\:ss\\.ff}");
                sb.AppendLine($"   box     ({left:F0},{top:F0}) to ({left + boxW:F0},{top + boxH:F0})   {boxW:F0} x {boxH:F0}" +
                              $"   pane {vpW:F0} x {vpH:F0}");
                sb.AppendLine($"   motion  zoom {S:F2}x   pan {tx:+0;-0;0},{ty:+0;-0;0}   surface {contentW:F0} x {contentH:F0}");
                sb.AppendLine($"   showing source x {x0:F0}..{x1:F0} of {srcW:F0}   y {y0:F0}..{y1:F0} of {srcH:F0}" +
                              (over.Count == 0 ? "   (all inside)" : "   BLACK: " + string.Join(", ", over)));
            }

            if (sb.Length == 0)
            {
                line.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            line.Text = sb.ToString().TrimEnd();
            line.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }

        // ==================== Mark coordinate space ====================
        //
        // A mark's X/Y are fractions of the video's FIT rectangle — the area the video occupies in
        // the player pane at Scale 1, which is exactly the box Edit mode frames against. Every
        // read multiplies by this; every write divides by it. Keeping the conversion in one place
        // is what makes a mark mean the same thing at any window size.
        // THE aspect for a clip, in one place. Everything that derives a fit rectangle must come
        // through here or the geometry silently forks.
        //
        // It used to fork three ways: TryGetMarkSpace preferred op.SourceAspect and fell back to
        // 16:9, ApplyOverlayBox read only _overlayAspect[slot] and bailed, and the WYSIWYG rect
        // code read only _overlayAspect[0] and fell back to 16:9 without ever consulting the clip.
        // On a 2.39:1 source that last fallback drew and dragged the Start/Mid/End rects against a
        // 1.78 fit — 34% out — so a rect placed visibly INSIDE the picture wrote a mark outside it,
        // and the clip rendered with black down the edge. Same clip, three different ideas of how
        // big it is.
        //
        // The clip's own SourceAspect leads because it is persisted in the project and therefore
        // known at load, long before a decoder has opened; _overlayAspect is the live backstop for
        // clips saved before that field existed. Returns 0 for genuinely unknown — callers must
        // decide what to do about it rather than be handed a plausible-looking lie.
        private double AspectOf(CinematicOperation op, int slot)
        {
            double aspect = op?.SourceAspect ?? 0;
            if (aspect <= 0 && slot >= 0 && slot < MaxOverlayTracks) aspect = _overlayAspect[slot];
            return aspect > 0 ? aspect : 0;
        }

        public bool TryGetMarkSpace(CinematicOperation op, out double fitW, out double fitH)
        {
            fitW = 0; fitH = 0;

            double vpW = _playerControl.ActualWidth;
            double vpH = _playerControl.ActualHeight;
            if (vpW <= 0 || vpH <= 0) return false;

            // No 16:9 guess. Reporting false lets the caller hold off for a frame; inventing an
            // aspect produced a fit rect that disagreed with the one the surface was sized to, and
            // marks interpreted in the wrong space are exactly how framing lands off-picture.
            double aspect = AspectOf(op, 0);
            if (aspect <= 0) return false;

            var fit = ClipGeometry.Fit(aspect, vpW, vpH);
            fitW = fit.W; fitH = fit.H;
            return true;
        }

        // Turn the live edit transform into a mark. The transform is in pane pixels; the mark is
        // stored normalised, so reopening the project at a different window size reproduces the
        // framing rather than shifting it.
        public SpatialMark CaptureMark(CinematicOperation op, Microsoft.UI.Xaml.Media.CompositeTransform t)
        {
            if (t == null) return new SpatialMark(1f, 0, 0);

            EnsureMarksNormalized(op);
            if (!TryGetMarkSpace(op, out double fitW, out double fitH) || fitW <= 0 || fitH <= 0)
                return new SpatialMark((float)t.ScaleX, 0, 0);

            return new SpatialMark((float)t.ScaleX,
                                   (float)(t.TranslateX / fitW),
                                   (float)(t.TranslateY / fitH));
        }

        // Convert a legacy clip's marks from raw pane pixels to fractions of the fit.
        //
        // Done here — on first draw — rather than at load, because this is the first moment the
        // pane size is known for certain; at load the control may not have been measured yet, and
        // normalising against a zero-width pane would destroy the marks. Idempotent and cheap: one
        // bool test once the clip has been converted.
        //
        // The conversion itself is lossless: dividing by the fit here and multiplying by the same
        // fit at render round-trips exactly, so normalising costs nothing.
        //
        // It does NOT follow that a legacy project renders unchanged. Translate used to be scaled
        // per-axis by (PlacementWidth, PlacementHeight) and is now scaled uniformly by
        // max(width, height) — see KenBurnsMotion.PanScale. On a square or wide PiP those agree; on
        // a TALL one they do not, and such clips will reframe. That is the point: the old result
        // did not match what the editor drew.
        public void EnsureMarksNormalized(CinematicOperation op)
        {
            if (op == null || !op.MarksAreLegacyPixels) return;
            if (!TryGetMarkSpace(op, out double fitW, out double fitH) || fitW <= 0 || fitH <= 0) return;

            Norm(op.StartMark);
            Norm(op.MidMark);
            Norm(op.EndMark);
            op.MarksAreLegacyPixels = false;

            void Norm(SpatialMark m)
            {
                if (m == null) return;
                m.X = (float)(m.X / fitW);
                m.Y = (float)(m.Y / fitH);
            }
        }

        // Sweep every clip, not just the ones currently on screen. EnsureMarksNormalized alone
        // converts a clip the first time it is drawn, which leaves a project that is loaded and
        // immediately saved holding pixel marks under a schema that promises fractions. Called on
        // load and again before save, both points where the pane is certain to be measured.
        public void NormalizeAllMarks(System.Collections.Generic.IEnumerable<TimelineTrack> tracks)
        {
            if (tracks == null) return;
            foreach (var track in tracks)
            {
                if (track?.Clips == null) continue;
                foreach (var clip in track.Clips)
                {
                    EnsureMarksNormalized(clip);

                }
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
                if (_viewModel.SelectedMark != null)
                {
                    _viewModel.SelectedMark = null;
                    _playerControl.IsMarkSelected = false;
                }
                return;
            }

            var op = _viewModel.SelectedClip as CinematicOperation;
            var transform = _playerControl.ActiveTransform;
            if (op == null || transform == null) return;

            EnsureMarksNormalized(op);

            _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            UpdateTelemetryOverlay(true);

            double vpW = _playerControl.ActualWidth > 0 ? _playerControl.ActualWidth : 1920;
            double vpH = _playerControl.ActualHeight > 0 ? _playerControl.ActualHeight : 1080;

            // In Edit mode, the clip being edited is always isolated into slot 0.
            //
            // This read _overlayAspect[0] alone and fell back to 16:9, never consulting the clip.
            // The rects are the OUTPUT frames drawn over the picture, so a fit rect that disagrees
            // with the picture's puts them somewhere they do not belong: on a 2.39:1 source the
            // 1.78 fallback is 34% out, and a rect dropped visibly inside the frame writes a mark
            // outside it. Hide them rather than draw them in the wrong place — an absent rect is
            // obviously absent, a misplaced one looks authoritative.
            double aspect = AspectOf(op, 0);
            if (aspect <= 0)
            {
                _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

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
                // Marks are fractions of the fit (W x H here) — back to pixels to draw them.
                double txt = targetMark.X * W;
                double tyt = targetMark.Y * H;

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

            // Selection styling. Solid and full strength for the selected keyframe, thin dashed and
            // faded for the rest — the colour coding still says WHICH keyframe each one is, so the
            // highlight only has to say which one the wheel and the inspector will act on.
            var sel = _viewModel.SelectedMark;
            StyleMarkRect(_playerControl.WysiwygStartRect, _playerControl.WysiwygStartFrame, sel == EditTarget.Start);
            StyleMarkRect(_playerControl.WysiwygMidRect, _playerControl.WysiwygMidFrame, sel == EditTarget.Mid);
            StyleMarkRect(_playerControl.WysiwygEndRect, _playerControl.WysiwygEndFrame, sel == EditTarget.End);
        }

        private static void StyleMarkRect(Microsoft.UI.Xaml.FrameworkElement rect,
                                          Microsoft.UI.Xaml.Shapes.Rectangle frame, bool selected)
        {
            if (rect != null)
            {
                double opacity = selected ? 1.0 : 0.42;
                if (Math.Abs(rect.Opacity - opacity) > 0.001) rect.Opacity = opacity;
            }
            if (frame == null) return;

            double thickness = selected ? 3.0 : 1.5;
            if (Math.Abs(frame.StrokeThickness - thickness) > 0.001) frame.StrokeThickness = thickness;

            // Solid for the selected one; the dashes are what make an unselected rectangle read as
            // a guide rather than as the thing being manipulated.
            bool dashed = frame.StrokeDashArray != null && frame.StrokeDashArray.Count > 0;
            if (selected && dashed) frame.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection();
            else if (!selected && !dashed) frame.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 4 };
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

        // Positions, sizes and clips the placement box (the overlay grid) from the clip's
        // placement fields, shaped to the video aspect. In edit mode the box fills the screen
        // (placement bypassed) so content is framed full-size, identical to Track 1; at
        // playback it is the corner PiP. The grid clips its content so zoomed-in framing can't
        // spill outside the box.
        // Returns TRUE when the box was actually established. It reads _overlayAspect[slot] alone,
        // which is only filled from an OPEN decoder (CacheOverlayAspect), while the slot is marked
        // active the instant activation starts. In that window this bailed and left the grid at the
        // previous clip's size — or unsized — yet ApplyOverlayTransform went straight on to write a
        // full zoom/pan onto it, because its own fit came from op.SourceAspect and succeeded. A
        // transform computed for one rectangle applied to another: picture flung off the box, black
        // where the framing was well inside the frame. Hence both the shared resolver (SourceAspect
        // is in the project file, so the box can be built before any decoder opens) and the bool,
        // so a caller can never transform geometry that was never laid out.
        private bool ApplyOverlayBox(int slot, CinematicOperation overlay, bool editMode)
        {
            var grid = _playerControl.OverlayVisuals[slot].Grid;
            double aspect = AspectOf(overlay, slot);
            double vpW = _playerControl.ActualWidth;
            double vpH = _playerControl.ActualHeight;
            if (aspect <= 0 || vpW <= 0 || vpH <= 0) return false;

            // Video fit to viewport (contained), preserving aspect — the "scale 1" reference.
            double fitW, fitH;
            if (aspect >= vpW / vpH) { fitW = vpW; fitH = vpW / aspect; }
            else { fitH = vpH; fitW = vpH * aspect; }

            // Edit mode: box fills the video fit (framing at full size). Arrange: independent
            // width/height so the PiP can be reshaped; the video crop-fills (UniformToFill).
            var box = ClipGeometry.Box(fitW, fitH, vpW, vpH,
                                       overlay.PlacementWidth, overlay.PlacementHeight,
                                       overlay.PlacementCenterX, overlay.PlacementCenterY, editMode);
            double boxW = box.W, boxH = box.H, left = box.X, top = box.Y;

            // NOTE (§7A): this method does GEOMETRY ONLY. Deciding still-vs-video used to live here
            // and silently never fired — the render mode is now set explicitly by SetOverlayRender
            // at each state transition, never as a side effect of laying out a box.

            if (grid.Margin.Left != left || grid.Margin.Top != top)
            {
                grid.Margin = new Microsoft.UI.Xaml.Thickness(left, top, 0, 0);
            }
            // Only resize + reallocate the BOX when its dimensions actually change (avoids
            // per-frame allocation of the clip geometry during playback).
            if (grid.Width != boxW || grid.Height != boxH || _overlayContentAspect[slot] != aspect)
            {
                grid.Width = boxW;
                grid.Height = boxH;
                _overlayContentAspect[slot] = aspect;
                grid.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, boxW, boxH)
                };
            }

            // ---- The surfaces are sized to the FRAME, not to the box. ----
            //
            // UniformToFill into a BOX-sized element crops the frame to the box and DISCARDS
            // the surplus: MediaPlayerElement renders into a swapchain the size of the element,
            // and Image applies a layout clip on overflow. The RenderTransform then pans and
            // zooms that crop — so there is no picture outside it to bring back, and the box
            // behaves as if it were the whole video. That is the bug, and it hits video clips
            // and stills alike because both crop before they transform.
            //
            // Sized to the frame's drawn extent instead, the element's aspect equals the
            // source's, so UniformToFill discards nothing and the whole frame stays available
            // to the transform. The grid's clip above still crops what you see, so the neutral
            // framing is unchanged: at scale 1 with no pan, a centred frame-sized element shows
            // exactly the same crop it always did.
            //
            // THIS RUNS EVERY CALL, deliberately. It used to sit inside the box guard above, which
            // made it a ONE-SHOT: once the grid had its size that branch never ran again, so any
            // later loss of a surface's Width was permanent. The clip then rendered with content
            // exactly the size of its box - zero surplus - and the first pan of the Ken Burns ramp
            // slid it almost entirely out of frame: full picture at t=0, a ~50px strip against a
            // wall of black by the Mid mark. Re-asserting makes the size a function of the current
            // geometry instead of a state that can be silently dropped.
            //
            // The write is delta-guarded, which is what keeps the old warning here honest: this
            // method runs inside the per-frame render handler, and unconditionally writing layout
            // properties on a child from there retriggers measure up the tree and can become a
            // layout loop that starves the UI thread (playback freezes, scrubber goes dead).
            // contentW/H derive deterministically from boxW/boxH and the aspect, so once the box is
            // stable every comparison is false and not one property is touched.
            (double contentW, double contentH) = ClipGeometry.Content(boxW, boxH, aspect);

            // Centre the oversized surface on the box by hand. It used to rely on
            // HorizontalAlignment="Center" inside the box-sized grid, and that is precisely what
            // broke: WinUI hands an overflowing child a LAYOUT CLIP at the parent's size, and
            // RenderTransform is applied AFTER that clip. So the frame was cropped to the 556px box
            // first and only then panned - at pan 518 that leaves 556-518 = 38px of picture and a
            // wall of black, which is exactly what the Mid mark rendered. The surplus the whole
            // frame-sizing scheme exists to preserve was being thrown away one step before it was
            // needed. Inside a Canvas nothing constrains the child, so no layout clip is issued and
            // the transform pans a surface that still holds the entire frame; grid.Clip above then
            // crops at RENDER time, which is a mask rather than a constraint.
            double padX = (contentW - boxW) / 2;
            double padY = (contentH - boxH) / 2;

            var surfaces = _playerControl.OverlayVisuals[slot];

            // The defect this area exists to prevent was NOT a maths error - the numbers were right
            // while WinUI layout-clipped the surface to its parent before the RenderTransform ran,
            // discarding the surplus one step before the pan could use it. Arithmetic tests cannot
            // see that, so it is guarded structurally: the surfaces must live somewhere that does
            // not constrain them. A Canvas does not; a sized Grid does.
            System.Diagnostics.Debug.Assert(
                surfaces.Video == null || surfaces.Video.Parent is Microsoft.UI.Xaml.Controls.Canvas,
                "Video surface must sit in a Canvas: a sizing parent layout-clips it before the "
                + "RenderTransform, silently discarding the pan surplus.");
            System.Diagnostics.Debug.Assert(
                surfaces.Still == null || surfaces.Still.Parent is Microsoft.UI.Xaml.Controls.Canvas,
                "Still surface must sit in a Canvas (see above).");

            PlaceSurface(surfaces.Video, -padX, -padY, contentW, contentH);
            PlaceSurface(surfaces.Still, -padX, -padY, contentW, contentH);

            // BORDERS ARE DRAWN AS A CHILD, never as the Grid's own border.
            //
            // Grid.BorderBrush renders BENEATH the grid's children, and the video surface fills -
            // usually overflows - the grid. So a Solid border was painted and then covered, and the
            // only parts that survived were slivers where the picture happened not to reach: one
            // edge here, two there, apparently at random. Soft escaped it by accident, because its
            // CornerRadius clips the children to a rounded rectangle and cuts the corners away.
            // FilmStrip always worked because it alone was a child Rectangle.
            //
            // All three now take that same route, so they are all on top of the picture and all
            // four sides are drawn.
            grid.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);

            if (overlay.BorderType == BorderType.None || editMode)
            {
                grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                HideBorderRect(grid);
            }
            else
            {
                var c = overlay.BorderColor;
                switch (overlay.BorderType)
                {
                    case BorderType.FilmStrip:
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                        ShowBorderRect(grid, c, overlay.BorderThickness,
                                       new Microsoft.UI.Xaml.Media.DoubleCollection { 2, 1, 2, 1 }, 0);
                        break;

                    case BorderType.Soft:
                        // The rounded corner stays on the grid as well, so the PICTURE is rounded
                        // too rather than a rounded outline sitting on a square image.
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(16);
                        ShowBorderRect(grid, Windows.UI.Color.FromArgb(128, c.R, c.G, c.B),
                                       overlay.BorderThickness, null, 16);
                        break;

                    default:
                        grid.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                        ShowBorderRect(grid, c, overlay.BorderThickness, null, 0);
                        break;
                }
            }

            return true;
        }

        // Size AND position a content surface inside its Canvas, writing only on a real change.
        //
        // NaN-safe: an unset Width reads back as NaN and every comparison against NaN is false, so
        // the explicit IsNaN test is what makes a surface that has LOST its size get re-sized on the
        // next pass instead of being quietly left alone.
        //
        // Delta-guarding matters here: this runs inside the per-frame render handler, and
        // unconditionally writing layout properties from there retriggers measure up the tree and
        // can become a layout loop that starves the UI thread. Every value below derives
        // deterministically from the box and the aspect, so once the box is stable nothing is
        // written at all.
        private static void PlaceSurface(Microsoft.UI.Xaml.FrameworkElement el, double left, double top,
                                         double w, double h)
        {
            if (el == null) return;
            if (double.IsNaN(el.Width) || Math.Abs(el.Width - w) > 0.5) el.Width = w;
            if (double.IsNaN(el.Height) || Math.Abs(el.Height - h) > 0.5) el.Height = h;

            if (Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetLeft(el) - left) > 0.5)
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(el, left);
            if (Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetTop(el) - top) > 0.5)
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(el, top);
        }

        // The single border overlay for a clip, whatever its style. Reused rather than recreated,
        // because this runs from the per-frame render path.
        private static Microsoft.UI.Xaml.Shapes.Rectangle GetBorderRect(Microsoft.UI.Xaml.Controls.Grid grid, bool create)
        {
            foreach (var child in grid.Children)
                if (child is Microsoft.UI.Xaml.Shapes.Rectangle r && r.Name == "ClipBorderRect")
                    return r;

            if (!create) return null;
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Name = "ClipBorderRect",
                Fill = null,
                IsHitTestVisible = false
            };
            grid.Children.Add(rect);   // last child: on top of the picture
            return rect;
        }

        private static void HideBorderRect(Microsoft.UI.Xaml.Controls.Grid grid)
        {
            var rect = GetBorderRect(grid, create: false);
            if (rect != null) rect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private static void ShowBorderRect(Microsoft.UI.Xaml.Controls.Grid grid, Windows.UI.Color color,
                                           double thickness,
                                           Microsoft.UI.Xaml.Media.DoubleCollection dash, double radius)
        {
            var rect = GetBorderRect(grid, create: true);
            if (rect == null) return;

            rect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            rect.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            rect.StrokeThickness = thickness;
            rect.RadiusX = radius;
            rect.RadiusY = radius;

            // Null clears the dashes; assigning an empty collection leaves a solid line either way,
            // but clearing keeps the property honest about what the style is.
            if (dash == null) rect.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeDashArrayProperty);
            else rect.StrokeDashArray = dash;
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

            _activeOverlay[slot] = null;
            _overlayAspect[slot] = 0;
            _overlayContentAspect[slot] = 0;
        }

        // Which surface a clip renders on. A still renders as a bitmap once its frame has been
        // baked at source resolution; until then (and for every video) the MediaPlayerElement is
        // still the surface, so a clip is never blank while a decode is in flight.
        private static OverlayRender RenderModeFor(CinematicOperation clip)
            => clip != null && clip.IsStill && clip.StillFrame != null
                ? OverlayRender.Still
                : OverlayRender.Video;

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
            if (!ApplyOverlayBox(slot, overlay, false))
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
                double effectiveVolume = overlay.Volume;
                if (player.Volume != effectiveVolume) player.Volume = effectiveVolume;
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

        // ---- Arrange mode: drag / wheel the PiP under the cursor (the hit slot) ----

        // ==================== Layout prototypes ====================
        //
        // A layout arranges the clips that are ON SCREEN AT THE PLAYHEAD. That is the answer to
        // "which clips?" - the question a spatial arrangement has to settle before it can mean
        // anything on a timeline where tracks hold clips of different lengths. It arranges what you
        // are looking at, which is also the only set you can judge by eye.
        //
        // Deliberately a one-shot: it writes each clip's placement and stops. It is NOT reflow -
        // when one of these clips ends, its cell simply goes empty rather than the survivors
        // rearranging. Whether that is acceptable is the thing these prototypes exist to find out.
        private void OnLayoutRequested(object? sender, string layout)
        {
            if (_mode != EditorMode.Arrange) return;

            var live = new System.Collections.Generic.List<(int slot, CinematicOperation clip)>();
            for (int i = 0; i < MaxOverlayTracks; i++)
                if (_activeOverlay[i] != null) live.Add((i, _activeOverlay[i]));
            if (live.Count == 0) return;

            var cells = LayoutCells(layout, live.Count);
            if (cells == null) return;

            for (int i = 0; i < live.Count && i < cells.Length; i++)
                PlaceInCell(live[i].slot, live[i].clip, cells[i]);

            _viewModel.RecordIfChanged();
            Invalidate();
        }

        // Cells in PANE fractions: centre x, centre y, width, height.
        //
        // DERIVED FROM THE COUNT, never a fixed table. The first cut hardcoded two cells for "side"
        // and "stack", so with four clips on screen it arranged two of them and left the other two
        // wherever they already were - a half-applied layout on top of the old one, which looked
        // like nothing at all. A layout has to account for every clip it claims to arrange.
        //
        // side  = one row, N columns.  stack = N rows, one column.  grid = the squarest fit.
        //
        // The gutter is subtracted from each cell rather than inserted between them, so the outer
        // edge gets the same breathing room as the inner joins and the block stays centred.
        private static (double cx, double cy, double w, double h)[] LayoutCells(string layout, int count)
        {
            if (count <= 0) return null;
            const double Gutter = 0.02;

            int rows, cols;
            switch (layout)
            {
                case "side":  rows = 1; cols = count; break;
                case "stack": rows = count; cols = 1; break;
                case "grid":
                    cols = (int)Math.Ceiling(Math.Sqrt(count));
                    rows = (int)Math.Ceiling(count / (double)cols);
                    break;
                default: return null;
            }

            double cellW = 1.0 / cols, cellH = 1.0 / rows;
            double w = cellW - Gutter * (1 + 1.0 / cols);
            double h = cellH - Gutter * (1 + 1.0 / rows);
            if (w <= 0 || h <= 0) return null;

            var cells = new (double, double, double, double)[count];
            for (int i = 0; i < count; i++)
            {
                int row = i / cols;
                int col = i % cols;

                // A short last row is centred, so the odd one out reads as deliberate rather than
                // as a gap where a clip should have been.
                int inThisRow = Math.Min(cols, count - row * cols);
                double rowOffset = (cols - inThisRow) * cellW / 2.0;

                cells[i] = (rowOffset + (col + 0.5) * cellW,
                            (row + 0.5) * cellH,
                            w, h);
            }
            return cells;
        }

        // Put one clip in one cell, at the largest size that keeps its own shape.
        //
        // NOTHING IS CROPPED. A box whose PlacementWidth equals its PlacementHeight already has the
        // source's aspect - that is why the default 0.3 x 0.3 corner PiPs look right - because the
        // box is measured against the clip's own fit rectangle. So the cell is filled by the
        // largest source-shaped rectangle that fits inside it, and the remainder of the cell is
        // left empty rather than the picture being cut to fill it.
        //
        // Note the mixed units the placement model uses: width and height are fractions of the
        // clip's FIT, while the centre is a fraction of the PANE. The conversion here is what keeps
        // a cell expressed in pane terms from silently meaning something different per clip.
        private void PlaceInCell(int slot, CinematicOperation clip, (double cx, double cy, double w, double h) cell)
        {
            double vpW = _playerControl.ActualWidth, vpH = _playerControl.ActualHeight;
            if (vpW <= 0 || vpH <= 0) return;
            if (!TryGetMarkSpace(clip, out double fitW, out double fitH) || fitW <= 0 || fitH <= 0) return;

            double cellW = cell.w * vpW, cellH = cell.h * vpH;
            double aspect = fitW / fitH;

            // Largest rectangle of the source's aspect that fits the cell.
            double boxW = cellW, boxH = cellW / aspect;
            if (boxH > cellH) { boxH = cellH; boxW = cellH * aspect; }

            clip.PlacementWidth = Math.Clamp(boxW / fitW, 0.05, 1.0);
            clip.PlacementHeight = Math.Clamp(boxH / fitH, 0.05, 1.0);
            clip.PlacementCenterX = Math.Clamp(cell.cx, 0, 1);
            clip.PlacementCenterY = Math.Clamp(cell.cy, 0, 1);
        }

        /// <summary>
        /// Resize a PiP to a fraction of the frame, in place.
        /// </summary>
        /// <remarks>
        /// Position is preserved, not reset. Resizing and repositioning are separate decisions, and
        /// a preset that silently flung the clip back to a corner would undo placement work every
        /// time you changed its size. The centre is only nudged when the new box would hang off the
        /// frame, and full screen re-centres because there is nowhere else for it to be.
        /// </remarks>
        private void OnPipSizeRequested(object? sender, (int slot, string preset) e)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[e.slot];
            if (overlay == null) return;

            // 100% means the clip's WHOLE frame: the box is the fit rectangle, so a clip shaped
            // differently from the window is letterboxed and nothing is cropped. Covering the
            // window instead would need a fraction above 1.0, and PlacementWidth/Height clamp to
            // [0.05, 1.0] - so a "fill and crop" preset would silently do nothing here. Relaxing
            // that cap is a change to the placement model, not a menu entry.
            if (!double.TryParse(e.preset, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double fraction))
                return;

            double f = Math.Clamp(fraction, 0.05, 1.0);
            overlay.PlacementWidth = f;
            overlay.PlacementHeight = f;

            if (f >= 1.0)
            {
                overlay.PlacementCenterX = 0.5;
                overlay.PlacementCenterY = 0.5;
            }
            else
            {
                // Keep it on screen: the centre can sit no closer to an edge than half the box.
                overlay.PlacementCenterX = Math.Clamp(overlay.PlacementCenterX, f / 2, 1 - f / 2);
                overlay.PlacementCenterY = Math.Clamp(overlay.PlacementCenterY, f / 2, 1 - f / 2);
            }

            _dispatcher.TryEnqueue(() => ApplyOverlayBox(e.slot, overlay, false));
            _viewModel.RecordIfChanged();
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
                _viewModel.RecordIfChanged();
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
                _viewModel.RecordIfChanged();
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
                _viewModel.RecordIfChanged();
            }
        }
        private void OnHideRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                overlay.IsVideoHidden = !overlay.IsVideoHidden;
                _viewModel.RecordIfChanged();
                _dispatcher.TryEnqueue(() => RefreshComposite());
            }
        }

        private void OnLockRequested(object? sender, int slot)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[slot];
            if (overlay != null)
            {
                overlay.IsLocked = !overlay.IsLocked;
                _viewModel.RecordIfChanged();
                _dispatcher.TryEnqueue(() => RefreshComposite());
            }
        }
        private void OnOpacityRequested(object? sender, (int Slot, float Opacity) args)
        {
            if (_mode != EditorMode.Arrange) return;
            var overlay = _activeOverlay[args.Slot];
            if (overlay != null)
            {
                overlay.Opacity = args.Opacity;
                _viewModel.RecordIfChanged();
                _dispatcher.TryEnqueue(() => RefreshComposite());
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
            double aspect = AspectOf(overlay, e.slot);
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

            // Grabbing a rectangle selects it. Without this the app had no idea which keyframe you
            // were working on: the canvas seeked to it but left CurrentEditTarget alone, and there
            // was no selection state at all for the highlight or the wheel to key off.
            SetSelectedMark(markType switch
            {
                "Start" => EditTarget.Start,
                "Mid" => EditTarget.Mid,
                "End" => EditTarget.End,
                _ => (EditTarget?)null
            });

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

            // Poke the decoder so the paused frame updates immediately. A bitmap still has no
            // decoder to poke and never changes frame, so there is nothing to do for one.
            if (RenderModeFor(op) != OverlayRender.Still)
                _overlayPlayer[0]?.StepForwardOneFrame();
        }

        // The single place selection changes, so the view model, the control's wheel routing and
        // the on-screen highlight can never disagree.
        public void SetSelectedMark(EditTarget? target)
        {
            if (_mode != EditorMode.Edit) target = null;
            if (_viewModel.SelectedMark == target) return;

            _viewModel.SelectedMark = target;
            _playerControl.IsMarkSelected = target.HasValue;
            UpdateWysiwygOverlay();
            if (target.HasValue) PopMarkRect(target.Value);
        }

        // A single ease-out pop on selection, not a loop. A rectangle that keeps flashing while you
        // are judging a framing is noise; one short acknowledgement then a solid, static highlight
        // is what reads as deliberate.
        private void PopMarkRect(EditTarget target)
        {
            var scale = target switch
            {
                EditTarget.Start => _playerControl.WysiwygStartPop,
                EditTarget.Mid => _playerControl.WysiwygMidPop,
                _ => _playerControl.WysiwygEndPop
            };
            if (scale == null) return;

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var prop in new[] { "ScaleX", "ScaleY" })
            {
                var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.03,
                    To = 1.0,
                    Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(180)),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                    {
                        EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                    },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, scale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, prop);
                sb.Children.Add(anim);
            }
            try { sb.Begin(); } catch { }
        }

        // Wheel over a selected rectangle resizes THAT keyframe, about its own centre. Smaller
        // rectangle = tighter framing = higher mark scale, which is why the factor is inverted.
        //
        // X AND Y MUST TRACK THE SCALE. A mark's offset is stored relative to its own zoom: the
        // rectangle is drawn at (-boxW/2 - mark.X*W) * (Sc/St), so its centre lands at
        // -mark.X*W*(Sc/St). Change St on its own and that centre moves — by an amount proportional
        // to how far off-centre the rectangle already was, so one sitting left of frame crept
        // further left as it grew and right-of-frame crept right. Holding mark.X/St constant (i.e.
        // scaling X and Y by the same ratio as the scale) pins the centre and resizes about it,
        // which is what a zoom is expected to do. Moving the rectangle stays a separate gesture:
        // drag its title tab.
        private void OnSelectedMarkWheel(object? sender, int delta)
        {
            if (_mode != EditorMode.Edit) return;
            var target = _viewModel.SelectedMark;
            if (!target.HasValue) return;
            if (_viewModel.SelectedClip is not CinematicOperation op) return;

            var mark = target.Value switch
            {
                EditTarget.Start => op.StartMark,
                EditTarget.Mid => op.MidMark,
                _ => op.EndMark
            };
            if (mark == null) return;

            float current = mark.Scale;
            if (current <= 0) current = 1f;

            double factor = delta > 0 ? 1.08 : 1.0 / 1.08;
            float next = (float)Math.Clamp(current * factor, 0.1, 10.0);
            if (Math.Abs(next - current) < 0.0001f) return;

            float ratio = next / current;
            mark.Scale = next;
            mark.X *= ratio;
            mark.Y *= ratio;

            UpdateWysiwygOverlay();
        }

        private void OnWysiwygBoxManipulated(object? sender, (string markType, string action, double dx, double dy) e)
        {
            if (_mode != EditorMode.Edit || _viewModel.SelectedClip == null) return;
            var op = _viewModel.SelectedClip as CinematicOperation;
            if (op == null) return;

            EnsureMarksNormalized(op);

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

            // Must resolve identically to the rect the user is dragging (see UpdateWysiwygOverlay).
            // A drag converted in a different space than it was drawn in moves the mark somewhere
            // other than where the pointer went.
            double aspect = AspectOf(op, 0);
            if (aspect <= 0) return;

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
            // The whole of this method works in pane pixels; marks are fractions of the fit
            // (W x H), so convert in here and back out again on write.
            double txt = mark.X * W;
            double tyt = mark.Y * H;
            if (St <= 0) St = 1;

            if (e.action == "Translate")
            {
                mark.X -= (float)(e.dx / (Sc / St) / W);
                mark.Y -= (float)(e.dy / (Sc / St) / H);
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
                mark.X = (float)(-(cx - W / 2 - txc) / (Sc / newSt) / W);
                mark.Y = (float)(-(cy - H / 2 - tyc) / (Sc / newSt) / H);
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
            _viewModel.RecordIfChanged();
}
    }
}











