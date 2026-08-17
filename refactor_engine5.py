import sys

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

fields_to_add = """
        private readonly DirectorViewModel _viewModel;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
        private bool _isPaused = false;
        private bool _isAnimating = false;
        public enum EditorMode { Arrange, Edit }
        private EditorMode _mode = EditorMode.Arrange;
        public CinematicOperation? CurrentPlayingOperation { get; private set; }
        private const int MaxOverlayTracks = DirectorViewModel.MaxTracks;

"""

text = text.replace("public VideoPlaybackEngine(Views.DirectorPlayerControl playerControl, DirectorViewModel viewModel)",
                    fields_to_add + "        public VideoPlaybackEngine(Views.DirectorPlayerControl playerControl, DirectorViewModel viewModel)")

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(text)
