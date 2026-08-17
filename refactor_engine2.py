import sys
import re

def parse_methods(code):
    # Returns a dict of method_name -> (start_idx, end_idx)
    # using a very simple regex and brace counting.
    methods = {}
    pattern = re.compile(r"^\s*(?:public|private|protected|internal)\s+(?:async\s+)?(?:static\s+)?[a-zA-Z0-9_<>\s\[\]]+\s+([a-zA-Z0-9_]+)\s*\(", re.MULTILINE)
    
    for match in pattern.finditer(code):
        method_name = match.group(1)
        start_idx = match.start()
        
        # Find the first '{' after start_idx
        brace_start = code.find('{', start_idx)
        if brace_start == -1:
            continue
            
        brace_count = 1
        i = brace_start + 1
        while i < len(code) and brace_count > 0:
            if code[i] == '{':
                brace_count += 1
            elif code[i] == '}':
                brace_count -= 1
            i += 1
            
        if brace_count == 0:
            methods[method_name] = (start_idx, i)
            
    return methods

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

# 1. Update MaxOverlayTracks
text = text.replace(
    "private const int MaxOverlayTracks = DirectorViewModel.MaxTracks - 1;",
    "private const int MaxOverlayTracks = DirectorViewModel.MaxTracks;"
)

# 2. Add _playbackTimer and remove legacy fields
text = re.sub(r"\s*private readonly MediaPlayerElement _playerA;.*?(?=public VideoPlaybackEngine)", 
"""
        private Microsoft.UI.Xaml.DispatcherTimer _playbackTimer;
        private DateTime _lastTickTime;
        private readonly MediaPlayer[] _overlayPlayer = new MediaPlayer[MaxOverlayTracks];
        private readonly CinematicOperation[] _activeOverlay = new CinematicOperation[MaxOverlayTracks];
        private readonly double[] _overlayAspect = new double[MaxOverlayTracks];
        private bool _isEditingOverlay = false;
        private TimeSpan _storyTimeAtClipStart = TimeSpan.Zero;
        private CinematicOperation _editClip;

""", text, flags=re.DOTALL)

# 3. Clean up VideoPlaybackEngine constructor
methods = parse_methods(text)
if "VideoPlaybackEngine" in methods:
    start, end = methods["VideoPlaybackEngine"]
    ctor = text[start:end]
    ctor = re.sub(r"\s*_playerA = playerControl\.PlayerA;.*?(?=        \})", "", ctor, flags=re.DOTALL)
    ctor = ctor.replace("InitializePlayers();", "")
    ctor = ctor.replace("Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += CompositionTarget_Rendering;", "")
    text = text[:start] + ctor + text[end:]

# 4. Remove unwanted methods entirely
methods_to_remove = [
    "InitializePlayers",
    "PlaybackLoopAsync",
    "PlayOperationAsync",
    "PlayTransitionAsync",
    "WaitWithPauseAsync",
    "CompositionTarget_Rendering",
    "UpdateTimelineNodesIsPlayingState",
    "RenderPausedFrame",
    "ViewModel_OperationSeekRequested"
]

# We must re-parse because indices changed
for m in methods_to_remove:
    methods = parse_methods(text)
    if m in methods:
        start, end = methods[m]
        # Remove up to preceding newline if possible
        while start > 0 and text[start-1] in [' ', '\t']:
            start -= 1
        if start > 0 and text[start-1] == '\n':
            start -= 1
        text = text[:start] + text[end:]

# 5. Replace Playback and Timer Logic
methods = parse_methods(text)
start1, end1 = methods["TogglePlayPauseAsync"]
start2, end2 = methods["StopPlayback"] # Assuming contiguous block for TogglePlayPauseAsync to StopPlayback

replacement_playback = """public async Task TogglePlayPauseAsync()
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
            if (_viewModel.IsRecordingMotion)
                _dispatcher.TryEnqueue(() => _viewModel.IsRecordingMotion = false);
            
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
        }"""
text = text[:start1] + replacement_playback + text[end2:]

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine_Stage2.cs", "w", encoding="utf-8") as f:
    f.write(text)

print("Stage 2 complete.")
