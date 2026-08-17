import sys

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

# Fix 1: OverlayTrack -> TimelineTrack
text = text.replace("private static CinematicOperation ResolveActiveClip(OverlayTrack track, TimeSpan t)", 
                    "private static CinematicOperation ResolveActiveClip(TimelineTrack track, TimeSpan t)")

# Fix 2: Remove the second declaration of _editClip at line 1093
# I will just replace the exact line using splitlines
lines = text.splitlines(keepends=True)
new_lines = []
for i, line in enumerate(lines):
    if "private CinematicOperation _editClip;" in line and i > 50:
        continue # skip the second one
    new_lines.append(line)

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.writelines(new_lines)
