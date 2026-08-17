import sys

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

# I will just replace the duplicates at line 1097!
# Wait, let's just delete the FIRST occurrence of EditorMode and _mode which I added at the top.
# Or better, delete the SECOND occurrence using a reliable splitlines loop.

lines = text.splitlines(keepends=True)
new_lines = []
editor_mode_count = 0
mode_count = 0

for line in lines:
    if "public enum EditorMode { Arrange, Edit }" in line:
        editor_mode_count += 1
        if editor_mode_count > 1:
            continue
    if "private EditorMode _mode = EditorMode.Arrange;" in line:
        mode_count += 1
        if mode_count > 1:
            continue
    new_lines.append(line)

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.writelines(new_lines)
