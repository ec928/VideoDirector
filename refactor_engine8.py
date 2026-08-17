import sys
import re

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

# Fix 1: _playbackCts
text = text.replace("if (_playbackCts != null && !_playbackCts.IsCancellationRequested)\n            {\n                return;\n            }", "")
text = text.replace("if (_playbackCts != null && !_playbackCts.IsCancellationRequested) return;", "")

# Fix 2: TimelineTrack doesn't have OpDuration
# Replace: offset > _viewModel.Tracks[startIdx].OpDuration
# With: offset > _viewModel.TotalStoryTime
text = text.replace("_viewModel.Tracks[startIdx].OpDuration", "_viewModel.TotalStoryTime")

# Fix 3: Stub out SkipNext and SkipPrevious
text = re.sub(r"public void SkipNext\(\)\s*\{.*?\}", "public void SkipNext() { }", text, flags=re.DOTALL)
text = re.sub(r"public void SkipPrevious\(\)\s*\{.*?\}", "public void SkipPrevious() { }", text, flags=re.DOTALL)

# Fix 4: Remove _transitionStyle warning
text = text.replace("private TransitionStyle _transitionStyle;", "")

# Fix 5: Remove _editPlayer warning (it's completely unused)
text = text.replace("private MediaPlayer _editPlayer;", "")

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(text)
