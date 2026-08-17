import sys

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

start_marker = "        public async Task StartPlaybackAsync("
end_marker = "        private DateTime _lastTelemetryUpdate = DateTime.MinValue;"

start_idx = text.find(start_marker)
end_idx = text.find(end_marker)

if start_idx == -1 or end_idx == -1:
    print("Markers not found!")
    sys.exit(1)

new_code = """        public async Task StartPlaybackAsync()
        {
            if (System.Linq.Enumerable.All(_viewModel.Tracks, t => t.Clips.Count == 0)) return;

            _isEditingOverlay = false;
            _mode = EditorMode.Arrange;
            _editClip = null;
            _editPlayer = null;
            _playerControl.InputMode = Views.PlayerInputMode.ArrangePips;
            _viewModel.IsEditMode = false;
            StopEditPreview();
            StopPlayback();

            _viewModel.IsPlaying = true;
            _isPaused = false;
            
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

        public void StopPlayback(bool cancelRecording = true)
        {
            _playbackTimer?.Stop();
            
            HideAllOverlays();
            
            if (_viewModel != null)
            {
                _viewModel.IsPlaying = false;
                _isPaused = false;
                if (cancelRecording && _viewModel.IsRecordingMotion)
                {
                    _dispatcher.TryEnqueue(() => _viewModel.IsRecordingMotion = false);
                }
            }

            if (_mode == EditorMode.Arrange)
            {
                _dispatcher.TryEnqueue(() => EvaluateOverlays(_viewModel.CurrentStoryTime));
            }
        }

        public void SkipNext()
        {
        }

        public void SkipPrevious()
        {
        }

        private void PlaybackTimer_Tick(object sender, object e)
        {
            if (_isPaused) return;

            var now = DateTime.Now;
            var elapsed = now - _lastTickTime;
            _lastTickTime = now;
            
            _viewModel.CurrentStoryTime += TimeSpan.FromSeconds(elapsed.TotalSeconds * _viewModel.PlaybackSpeed);

            if (_viewModel.TotalStoryTime > TimeSpan.Zero && _viewModel.CurrentStoryTime >= _viewModel.TotalStoryTime)
            {
                if (_viewModel.IsLooping)
                {
                    _viewModel.CurrentStoryTime = TimeSpan.Zero;
                }
                else
                {
                    _viewModel.CurrentStoryTime = _viewModel.TotalStoryTime;
                    StopPlayback(false);
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

"""

new_text = text[:start_idx] + new_code + text[end_idx:]

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(new_text)

print("Replaced StartPlaybackAsync successfully.")
