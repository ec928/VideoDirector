import sys
import re

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

# 1. ViewModel_PlaybackSpeedChanged
p = r"        private void ViewModel_PlaybackSpeedChanged\(object\? sender, double speed\).*?        \}"
rep = """        private void ViewModel_PlaybackSpeedChanged(object? sender, double speed)
        {
            if (_playbackTimer != null) _playbackTimer.Interval = TimeSpan.FromMilliseconds(16);
            if (_isPaused) return;

            for (int i = 0; i < MaxOverlayTracks; i++)
            {
                if (_overlayPlayer[i]?.PlaybackSession != null)
                {
                    double trackSpeed = speed;
                    if (_activeOverlay[i] != null) trackSpeed *= _activeOverlay[i].PlaybackSpeed;
                    
                    if (trackSpeed == 0)
                    {
                        _overlayPlayer[i].Pause();
                    }
                    else
                    {
                        _overlayPlayer[i].PlaybackSession.PlaybackRate = trackSpeed;
                        _overlayPlayer[i].Play();
                    }
                }
            }
        }"""
text = re.sub(p, rep, text, flags=re.DOTALL)

# 2. SeekActiveOperation
p2 = r"        public void SeekActiveOperation\(TimeSpan position\).*?        \}"
rep2 = """        public void SeekActiveOperation(TimeSpan position)
        {
            if (_mode == EditorMode.Edit && _overlayPlayer[0]?.PlaybackSession != null)
            {
                _overlayPlayer[0].PlaybackSession.Position = position;
                RenderPausedFrame(_overlayPlayer[0]);
            }
        }"""
text = re.sub(p2, rep2, text, flags=re.DOTALL)

# 3. InitializePlayers
p3 = r"        private void InitializePlayers\(\).*?        \}"
text = re.sub(p3, "", text, flags=re.DOTALL)

# 4. UpdateTelemetryOverlay
p4 = r"        private void UpdateTelemetryOverlay\(bool isEditMode = false\).*?        \}"
rep4 = """        private void UpdateTelemetryOverlay(bool isEditMode = false)
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
            }
            else
            {
                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }"""
text = re.sub(p4, rep4, text, flags=re.DOTALL)

# 5. ExitToArrange Opacity clear
text = text.replace("            _playerA.Opacity = _isPlayerAActive ? 1 : 0;", "")
text = text.replace("            _playerB.Opacity = _isPlayerAActive ? 0 : 1;", "")

# 6. EnterEditMode Opacity clear (from previous name EnterOverlayEditMode)
text = text.replace("                _playerA.Opacity = 0;", "")
text = text.replace("                _playerB.Opacity = 0;", "")

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(text)

print("Replaced all remaining references.")
