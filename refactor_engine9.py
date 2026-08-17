import sys
import re

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

# Fix 1: _playbackCts
text = re.sub(r"_playbackCts.*?;", "", text)
text = text.replace("if (_playbackCts != null && !_playbackCts.IsCancellationRequested)", "")
text = text.replace("_playbackCts?.Cancel()", "")

# Fix 2: _editPlayer -> _overlayPlayer[0] (or similar)
# Since the previous EnterEditMode was unified, let's see how _editPlayer was used.
text = text.replace("_editPlayer", "_overlayPlayer[0]")

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(text)
