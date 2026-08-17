import sys
import re

def parse_methods(code):
    methods = {}
    pattern = re.compile(r"^\s*(?:public|private|protected|internal)\s+(?:async\s+)?(?:static\s+)?[a-zA-Z0-9_<>\s\[\]]+\s+([a-zA-Z0-9_]+)\s*\(", re.MULTILINE)
    for match in pattern.finditer(code):
        method_name = match.group(1)
        start_idx = match.start()
        brace_start = code.find('{', start_idx)
        if brace_start == -1: continue
        brace_count = 1
        i = brace_start + 1
        while i < len(code) and brace_count > 0:
            if code[i] == '{': brace_count += 1
            elif code[i] == '}': brace_count -= 1
            i += 1
        if brace_count == 0:
            methods[method_name] = (start_idx, i)
    return methods

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine_Stage2.cs", "r", encoding="utf-8") as f:
    text = f.read()

# Replace UpdateTelemetryOverlay
methods = parse_methods(text)
start, end = methods["UpdateTelemetryOverlay"]
rep4 = """private void UpdateTelemetryOverlay(bool isEditMode = false)
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
                _playerControl.TelemetryStoryTime.Text = $"Timeline  : {currentStoryTime:hh\\\\:mm\\\\:ss\\\\.ff} / {_viewModel.TotalStoryTime:hh\\\\:mm\\\\:ss\\\\.ff}";
                
                if (currentActivePlayer?.PlaybackSession != null)
                {
                    _playerControl.TelemetryClipTime.Text = $"Clip Time : {currentActivePlayer.PlaybackSession.Position:hh\\\\:mm\\\\:ss\\\\.ff} / {clipEndTime:hh\\\\:mm\\\\:ss\\\\.ff} [{currentFileName}]";
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
        }"""
text = text[:start] + rep4 + text[end:]

# Replace ExitToArrange
methods = parse_methods(text)
start, end = methods["ExitToArrange"]
rep5 = """public void ExitToArrange()
        {
            StopEditPreview();
            _mode = EditorMode.Arrange;
            _isEditingOverlay = false;
            _editClip = null;
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
            _viewModel.IsEditMode = false;
            UpdateWysiwygOverlay();
            EvaluateOverlays(_viewModel.CurrentStoryTime);
        }"""
text = text[:start] + rep5 + text[end:]

# Replace SeekActiveOperation
methods = parse_methods(text)
if "SeekActiveOperation" in methods:
    start, end = methods["SeekActiveOperation"]
    rep_seek = """public void SeekActiveOperation(TimeSpan position)
        {
            if (_mode == EditorMode.Edit && _overlayPlayer[0]?.PlaybackSession != null)
            {
                _overlayPlayer[0].PlaybackSession.Position = position;
            }
        }"""
    text = text[:start] + rep_seek + text[end:]

# Replace ViewModel_PlaybackSpeedChanged
methods = parse_methods(text)
start, end = methods["ViewModel_PlaybackSpeedChanged"]
rep_speed = """private void ViewModel_PlaybackSpeedChanged(object? sender, double speed)
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
        }"""
text = text[:start] + rep_speed + text[end:]

# SetEditModeState
methods = parse_methods(text)
start, end = methods["SetEditModeState"]
rep_setedit = """private void SetEditModeState(CinematicOperation clip, MediaPlayer player, bool isOverlayEdit)
        {
            StopEditPreview();
            _mode = EditorMode.Edit;
            _editClip = clip;
            _isEditingOverlay = isOverlayEdit;
            _playerControl.InputMode = Views.PlayerInputMode.Content;
            _viewModel.IsEditMode = true;
        }"""
text = text[:start] + rep_setedit + text[end:]

# Now replace EnterEditMode completely
methods = parse_methods(text)
start, _ = methods["EnterEditMode"]
_, end = methods["RecordMotion_Rendering"]

new_code_recording = """public async void SeekCompositeToStoryTime(TimeSpan t)
        {
            if (_mode != EditorMode.Arrange) ExitToArrange();
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            _viewModel.CurrentStoryTime = t;
            EvaluateOverlays(t);
        }

        private DateTime _recordStartTime;

        public async void StartRecordingMotion(CinematicOperation op)
        {
            if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) return;
            
            StopPlayback();
            
            op.RecordedPath.Clear();
            var activePlayer = _overlayPlayer[0];
            var activeElement = _playerControl.OverlayVisuals[0].Grid;
            var activeTransform = _playerControl.OverlayVisuals[0].Transform;

            if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                activePlayer.MediaOpened += handler;
                activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                activePlayer.MediaOpened -= handler;
            }

            activePlayer.PlaybackSession.Position = op.VideoStartTime;
            activePlayer.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
            if (_viewModel.PlaybackSpeed == 0.0)
            {
                activePlayer.Pause();
            }
            else
            {
                activePlayer.Play();
                _dispatcher.TryEnqueue(() => _viewModel.IsPlaying = true);
            }
            
            _recordStartTime = DateTime.Now;
            _editClip = op;
            _playerControl.ActiveTransform = activeTransform;

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += RecordMotion_Rendering;
        }

        public void StopRecordingMotion(CinematicOperation op)
        {
            if (op == null) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= RecordMotion_Rendering;
            DistillRecordedPath(op);
            EnterEditMode(op, EditTarget.Start);
        }

        private void RecordMotion_Rendering(object? sender, object e)
        {
            if (_editClip == null || _playerControl.ActiveTransform == null) return;
            
            var activePlayer = _overlayPlayer[0];
            var activeTransform = _playerControl.ActiveTransform;
            var mark = new SpatialMark((float)activeTransform.ScaleX, (float)activeTransform.TranslateX, (float)activeTransform.TranslateY);
            
            var realTimeElapsed = DateTime.Now - _recordStartTime;
            var speed = _viewModel.PlaybackSpeed;
            if (speed == 0) speed = 1.0;
            
            var time = TimeSpan.FromSeconds(realTimeElapsed.TotalSeconds * speed);
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            _editClip.RecordedPath.Add(new TransformKeyframe(time, mark));
            
            _viewModel.CurrentOperationTime = _editClip.VideoStartTime + time;
            if (activePlayer.PlaybackSession != null)
            {
                activePlayer.PlaybackSession.Position = _viewModel.CurrentOperationTime;
                _viewModel.CurrentOperationDuration = activePlayer.PlaybackSession.NaturalDuration;
            }

            _dispatcher.TryEnqueue(() => 
            {
                UpdateTelemetryOverlay(false);
                UpdateWysiwygOverlay();
            });

            if (time >= _editClip.OpDuration)
            {
                _dispatcher.TryEnqueue(() => 
                {
                    if (_viewModel.IsRecordingMotion)
                        _viewModel.IsRecordingMotion = false;
                });
            }
        }"""

text = text[:start] + new_code_recording + text[end:]

# Now replace EnterOverlayEditMode (and BeginEdit) to just EnterEditMode
methods = parse_methods(text)
start, _ = methods["BeginEdit"]
_, end = methods["EnterOverlayEditMode"]

new_code_edit = """public void BeginEdit(CinematicOperation clip, EditTarget target)
        {
            if (clip == null) return;
            EnterEditMode(clip, target);
        }

        public async void EnterEditMode(CinematicOperation overlay, EditTarget target = EditTarget.Start)
        {
            if (overlay == null || string.IsNullOrWhiteSpace(overlay.FilePath)) return;

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
            });
        }"""

text = text[:start] + new_code_edit + text[end:]


with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(text)

print("Stage 3 complete.")
