import sys

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

# Replace _viewModel.TimelineNodes with _viewModel.Tracks
text = text.replace("_viewModel.TimelineNodes", "_viewModel.Tracks")

# Replace _viewModel.OverlayTracks with _viewModel.Tracks
text = text.replace("_viewModel.OverlayTracks", "_viewModel.Tracks")

# Replace _playbackCts references
text = text.replace("private CancellationTokenSource _playbackCts;", "")
text = text.replace("_playbackCts?.Cancel();", "")
text = text.replace("_playbackCts = new CancellationTokenSource();", "")

# Remove InitializePlayers
import re
text = re.sub(r"\s*private void InitializePlayers\(\)\s*\{.*?\}", "", text, flags=re.DOTALL)
text = text.replace("InitializePlayers();", "")

# Remove ViewModel_OperationSeekRequested
text = re.sub(r"\s*private void ViewModel_OperationSeekRequested\(object\? sender, TimeSpan e\)\s*\{.*?\}", "", text, flags=re.DOTALL)
text = text.replace("_viewModel.OperationSeekRequested += ViewModel_OperationSeekRequested;", "")

# Add CurrentPlayingOperation (temporary stub for Views/VideoDirectorControl.xaml.cs)
# public CinematicOperation? CurrentPlayingOperation { get; private set; } is already added

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(text)

# Fix Views/VideoDirectorControl.xaml.cs
with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Views\VideoDirectorControl.xaml.cs", "r", encoding="utf-8") as f:
    view_text = f.read()

view_text = view_text.replace("Engine.CurrentPlayingOperation", "null")
view_text = view_text.replace("var mpA = PlayerControl.PlayerA?.MediaPlayer;", "var mpA = (Windows.Media.Playback.MediaPlayer)null;")
view_text = view_text.replace("var mpB = PlayerControl.PlayerB?.MediaPlayer;", "var mpB = (Windows.Media.Playback.MediaPlayer)null;")
view_text = view_text.replace("var activePlayer = PlayerControl.PlayerA.Opacity > 0.5 ? mpA : mpB;", "var activePlayer = (Windows.Media.Playback.MediaPlayer)null;")

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Views\VideoDirectorControl.xaml.cs", "w", encoding="utf-8") as f:
    f.write(view_text)

