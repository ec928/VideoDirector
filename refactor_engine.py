import re

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    lines = f.readlines()

new_lines = []
skip = False
method_skip = False
bracket_count = 0

methods_to_remove = [
    "InitializePlayers",
    "PlaybackLoopAsync",
    "PlayOperationAsync",
    "PlayTransitionAsync",
    "WaitWithPauseAsync",
    "CompositionTarget_Rendering",
    "UpdateTimelineNodesIsPlayingState",
    "RenderPausedFrame"
]

def is_method_start(line):
    for m in methods_to_remove:
        if re.search(r'\b' + m + r'\b\s*\(', line):
            return True
    return False

i = 0
while i < len(lines):
    line = lines[i]
    
    # Remove A/B player fields
    if "private readonly MediaPlayerElement _playerA;" in line or \
       "private readonly MediaPlayerElement _playerB;" in line or \
       "private MediaPlayer _mediaPlayerA;" in line or \
       "private MediaPlayer _mediaPlayerB;" in line or \
       "private bool _isPlayerAActive" in line or \
       "// Animation state" in line or \
       "private bool _isAnimating" in line or \
       "private bool _isPreparingTransition" in line or \
       "private CinematicOperation _opA;" in line or \
       "private CinematicOperation _opB;" in line or \
       "private DateTime _opAStartTime;" in line or \
       "private DateTime _opBStartTime;" in line or \
       "private TimeSpan _opADuration;" in line or \
       "private TimeSpan _opBDuration;" in line or \
       "private bool _inTransition" in line or \
       "private DateTime _transitionStartTime;" in line or \
       "// Target state for rendering loop" in line or \
       "private TimeSpan _renderDuration;" in line or \
       "private MediaPlayerElement _fadeOutElement;" in line or \
       "private MediaPlayerElement _fadeInElement;" in line or \
       "private double _fadeOutVolume" in line or \
       "private double _fadeInVolume" in line:
        i += 1
        continue
        
    # Change _overlayPlayer to size 4
    if "private const int MaxOverlayTracks = DirectorViewModel.MaxTracks - 1;" in line:
        new_lines.append("        private const int MaxOverlayTracks = DirectorViewModel.MaxTracks;\n")
        i += 1
        continue
        
    if "public CinematicOperation? CurrentPlayingOperation { get; private set; }" in line:
        new_lines.append(line)
        # Add a DispatcherTimer for our master clock
        new_lines.append("        private Microsoft.UI.Xaml.DispatcherTimer _playbackTimer;\n")
        i += 1
        continue

    # Remove references in constructor
    if "_playerA = playerControl.PlayerA;" in line or \
       "_playerB = playerControl.PlayerB;" in line or \
       "InitializePlayers();" in line or \
       "Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += CompositionTarget_Rendering;" in line or \
       "Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CompositionTarget_Rendering;" in line:
        i += 1
        continue

    if is_method_start(line):
        method_skip = True
        bracket_count = 0
        
        # count brackets on the same line
        bracket_count += line.count('{')
        bracket_count -= line.count('}')
        
        i += 1
        while method_skip and i < len(lines):
            sub_line = lines[i]
            bracket_count += sub_line.count('{')
            bracket_count -= sub_line.count('}')
            
            if bracket_count == 0 and '{' in sub_line and '}' in sub_line:
                pass # single line braces
            elif bracket_count == 0 and '}' in sub_line:
                method_skip = False
            elif bracket_count == 0 and '{' not in sub_line and '}' not in sub_line:
                pass # Before the opening brace
                
            i += 1
        continue

    # Keep all other lines
    new_lines.append(line)
    i += 1

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine_Stage1.cs", "w", encoding="utf-8") as f:
    f.writelines(new_lines)

print("Stage 1 complete.")
