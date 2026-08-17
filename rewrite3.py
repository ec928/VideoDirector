import sys
import re

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "r", encoding="utf-8") as f:
    text = f.read()

start_marker2 = "        public async void EnterEditMode(CinematicOperation op, EditTarget target)"
end_marker2 = "        private void InitializeOverlayPlayers()"

start_idx2 = text.find(start_marker2)
end_idx2 = text.find(end_marker2)

if start_idx2 == -1 or end_idx2 == -1:
    print("Markers 2 not found!")
    sys.exit(1)

# Because we want to keep InitializeOverlayPlayers but need to account for its preceding comments
# We will just replace everything from start_marker2 to end_marker2
# Let's find the exact end index of our chunk to replace:
chunk_end = text.rfind("        // ==================== Overlay Playback ====================", start_idx2, end_idx2)
if chunk_end != -1:
    end_idx2 = chunk_end

new_code2 = """        public async void SeekCompositeToStoryTime(TimeSpan t)
        {
            if (_mode != EditorMode.Arrange) ExitToArrange();
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            _viewModel.CurrentStoryTime = t;
            EvaluateOverlays(t);
        }

        private DateTime _recordStartTime;

        public async void StartRecordingMotion(CinematicOperation op)
        {
            if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) return;
            
            StopPlayback();
            
            op.RecordedPath.Clear();
            var activePlayer = _overlayPlayer[0];
            var activeElement = _playerControl.OverlayVisuals[0].Grid;
            var activeTransform = _playerControl.OverlayVisuals[0].Transform;

            if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                activePlayer.MediaOpened += handler;
                activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                activePlayer.MediaOpened -= handler;
            }

            activePlayer.PlaybackSession.Position = op.VideoStartTime;
            activePlayer.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
            if (_viewModel.PlaybackSpeed == 0.0)
            {
                activePlayer.Pause();
            }
            else
            {
                activePlayer.Play();
                _dispatcher.TryEnqueue(() => _viewModel.IsPlaying = true);
            }
            
            _recordStartTime = DateTime.Now;
            _editClip = op;
            _playerControl.ActiveTransform = activeTransform;

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += RecordMotion_Rendering;
        }

        public void StopRecordingMotion(CinematicOperation op)
        {
            if (op == null) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= RecordMotion_Rendering;
            DistillRecordedPath(op);
            EnterEditMode(op, EditTarget.Start);
        }

        private void RecordMotion_Rendering(object? sender, object e)
        {
            if (_editClip == null || _playerControl.ActiveTransform == null) return;
            
            var activePlayer = _overlayPlayer[0];
            var activeTransform = _playerControl.ActiveTransform;
            var mark = new SpatialMark((float)activeTransform.ScaleX, (float)activeTransform.TranslateX, (float)activeTransform.TranslateY);
            
            var realTimeElapsed = DateTime.Now - _recordStartTime;
            var speed = _viewModel.PlaybackSpeed;
            if (speed == 0) speed = 1.0;
            
            var time = TimeSpan.FromSeconds(realTimeElapsed.TotalSeconds * speed);
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            _editClip.RecordedPath.Add(new TransformKeyframe(time, mark));
            
            _viewModel.CurrentOperationTime = _editClip.VideoStartTime + time;
            if (activePlayer.PlaybackSession != null)
            {
                activePlayer.PlaybackSession.Position = _viewModel.CurrentOperationTime;
                _viewModel.CurrentOperationDuration = activePlayer.PlaybackSession.NaturalDuration;
            }

            _dispatcher.TryEnqueue(() => 
            {
                UpdateTelemetryOverlay(false);
                UpdateWysiwygOverlay();
            });

            if (time >= _editClip.OpDuration)
            {
                _dispatcher.TryEnqueue(() => 
                {
                    if (_viewModel.IsRecordingMotion)
                        _viewModel.IsRecordingMotion = false;
                });
            }
        }

        private void DistillRecordedPath(CinematicOperation op)
        {
            if (op.RecordedPath.Count < 2) return;
            op.StartMark = op.RecordedPath[0].Mark.Clone();
            op.EndMark = op.RecordedPath[op.RecordedPath.Count - 1].Mark.Clone();
            op.MidMark = op.RecordedPath[op.RecordedPath.Count / 2].Mark.Clone();
        }

"""

text = text[:start_idx2] + new_code2 + text[end_idx2:]

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Models\VideoPlaybackEngine.cs", "w", encoding="utf-8") as f:
    f.write(text)

print("Replaced Markers 2 successfully.")
