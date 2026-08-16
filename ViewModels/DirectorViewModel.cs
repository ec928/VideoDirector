using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using VideoDirector.Models;
using Windows.Storage;

namespace VideoDirector.ViewModels
{
    public enum EditTarget
    {
        Start,
        Mid,
        End
    }

    public class DirectorViewModel : ObservableObject
    {
        public ObservableCollection<CinematicOperation> TimelineNodes { get; } = new();

        // Upper tracks (Track 2..4). Same clip type as Track 1 — a clip is a clip. Upper-track
        // clips are freely placed on the timeline (editable StartTime, gaps allowed) and
        // composited over Track 1. Bounded at 3: track i maps 1:1 to overlay player/surface i.
        public const int MaxOverlayTracks = 3;
        public ObservableCollection<OverlayTrack> OverlayTracks { get; } = new();

        // Default corners so stacked PiPs don't land on top of each other.
        private static readonly (double x, double y)[] TrackCorners =
            { (0.72, 0.72), (0.28, 0.72), (0.72, 0.28) };

        public bool CanAddOverlayTrack => OverlayTracks.Count < MaxOverlayTracks;

        public OverlayTrack AddOverlayTrack()
        {
            if (OverlayTracks.Count >= MaxOverlayTracks) return null;
            int i = OverlayTracks.Count;
            var track = new OverlayTrack
            {
                Name = "Track " + (i + 2),
                DefaultCenterX = TrackCorners[i].x,
                DefaultCenterY = TrackCorners[i].y
            };
            OverlayTracks.Add(track);
            OnPropertyChanged(nameof(CanAddOverlayTrack));
            return track;
        }



        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (SetProperty(ref _isPlaying, value))
                {
                    OnPropertyChanged(nameof(IsDockVisible));
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        public bool IsDockVisible => IsStoryboardVisible && !_isPlaying;

        private bool _isLooping = true;
        public bool IsLooping
        {
            get => _isLooping;
            set => SetProperty(ref _isLooping, value);
        }

        private bool _isAutoPlayEnabled = true;
        public bool IsAutoPlayEnabled
        {
            get => _isAutoPlayEnabled;
            set => SetProperty(ref _isAutoPlayEnabled, value);
        }

        private bool _isTelemetryVisible = false;
        public bool IsTelemetryVisible
        {
            get => _isTelemetryVisible;
            private set => SetProperty(ref _isTelemetryVisible, value);
        }

        // The inspector/storyboard panel auto-shows while editing a clip; otherwise it shows only
        // if the user has PINNED it open. So editing always has its controls to hand, and arranging
        // stays uncluttered. The transport's storyboard toggle is the pin.
        private bool _isStoryboardPinned;
        public bool IsStoryboardPinned
        {
            get => _isStoryboardPinned;
            set
            {
                if (SetProperty(ref _isStoryboardPinned, value))
                {
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    OnPropertyChanged(nameof(IsDockVisible));
                    UpdateTelemetryVisibility();
                }
            }
        }

        public bool IsStoryboardVisible => _isStoryboardPinned || _isEditMode;

        private bool _isControlsVisible = true;
        public bool IsControlsVisible
        {
            get => _isControlsVisible;
            set => SetProperty(ref _isControlsVisible, value);
        }

        private void UpdateTelemetryVisibility()
        {
            // Telemetry is a debug HUD — only when the panel is deliberately pinned, not on every
            // edit (that HUD was the "wall of text" that used to clutter editing).
            IsTelemetryVisible = _isStoryboardPinned;
        }

        private double _playbackSpeed = 1.0;
        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                if (SetProperty(ref _playbackSpeed, value))
                {
                    OnPropertyChanged(nameof(IsPausedSpeed));
                    PlaybackSpeedChanged?.Invoke(this, value);
                }
            }
        }

        public event EventHandler<double> PlaybackSpeedChanged;

        public bool IsPausedSpeed => _playbackSpeed == 0.0;

        public List<double> AvailableSpeeds { get; } = new List<double> { 1.0, 0.5, 0.25, 0.0 };

        public TimeSpan TotalStoryTime
        {
            get
            {
                TimeSpan total = TimeSpan.Zero;
                foreach (var node in TimelineNodes)
                {
                    total += node.OpDuration;
                    total += node.TransitionDuration;
                }
                return total;
            }
        }

        private TimeSpan _currentStoryTime;
        public TimeSpan CurrentStoryTime
        {
            get => _currentStoryTime;
            set => SetProperty(ref _currentStoryTime, value);
        }

        private CinematicOperation _selectedTimelineNode;
        public CinematicOperation SelectedTimelineNode
        {
            get => _selectedTimelineNode;
            set
            {
                if (SetProperty(ref _selectedTimelineNode, value))
                {
                    if (value != null) SelectedOverlay = null;
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectedClip));
                    OnPropertyChanged(nameof(IsTrack1Selected));
                    OnPropertyChanged(nameof(IsOverlaySelected));
                    OnPropertyChanged(nameof(SelectedTrackLabel));
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        private CinematicOperation _selectedOverlay;
        public CinematicOperation SelectedOverlay
        {
            get => _selectedOverlay;
            set
            {
                if (SetProperty(ref _selectedOverlay, value))
                {
                    if (value != null) SelectedTimelineNode = null;
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectedClip));
                    OnPropertyChanged(nameof(IsTrack1Selected));
                    OnPropertyChanged(nameof(IsOverlaySelected));
                    OnPropertyChanged(nameof(SelectedTrackLabel));
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        // True when either a Track 1 clip or an overlay is selected — the right panel shows
        // the relevant inspector, otherwise a "nothing selected" hint.
        public bool HasSelection => _selectedTimelineNode != null || _selectedOverlay != null;

        // The single selected clip regardless of track — the unified inspector binds to this so
        // Track 1 and Track 2+ share one layout. IsTrack1Selected / IsOverlaySelected drive the
        // visibility of the track-specific rows (Speed/Transition vs Size/Opacity).
        public CinematicOperation SelectedClip => _selectedTimelineNode ?? _selectedOverlay;
        public bool IsTrack1Selected => _selectedTimelineNode != null;
        public bool IsOverlaySelected => _selectedOverlay != null;

        // Plain, correct track label for the selected clip — "Track 1 · main" for the spine, or the
        // real overlay track number (Track 2/3/4 · overlay). Replaces the old two hardcoded labels
        // that always said "Track 2" even for track 3/4.
        public string SelectedTrackLabel
        {
            get
            {
                if (_selectedTimelineNode != null) return "Track 1 · main";
                if (_selectedOverlay != null)
                {
                    for (int i = 0; i < OverlayTracks.Count; i++)
                        if (OverlayTracks[i].Clips.Contains(_selectedOverlay))
                            return $"Track {i + 2} · overlay";
                    return "Overlay";
                }
                return string.Empty;
            }
        }

        // The three distinct modes, shown in the mode indicator. Edit takes precedence (its preview
        // sets IsPlaying too); then Play; otherwise Arrange.
        // Pure state word for the Global Command Zone badge. Context (Edit) wins over the Play
        // verb; the clip name lives in the Properties header, not here.
        public string ModeLabel
        {
            get
            {
                if (_isEditMode) return "EDIT";
                return _isPlaying ? "PLAYBACK" : "ARRANGE";
            }
        }

        // Edit vs Arrange mode — set by the engine; drives the badge.
        private bool _isEditMode;
        public bool IsEditMode
        {
            get => _isEditMode;
            set
            {
                if (SetProperty(ref _isEditMode, value))
                {
                    OnPropertyChanged(nameof(ModeLabel));
                    OnPropertyChanged(nameof(IsStoryboardVisible)); // panel follows edit mode
                    OnPropertyChanged(nameof(IsDockVisible));
                }
            }
        }

        private TimeSpan _currentOperationTime;
        public TimeSpan CurrentOperationTime
        {
            get => _currentOperationTime;
            set
            {
                if (SetProperty(ref _currentOperationTime, value))
                {
                    OnPropertyChanged(nameof(CurrentOperationTimeSeconds));
                }
            }
        }

        private bool _isSnappingEnabled = true;
        public bool IsSnappingEnabled { get => _isSnappingEnabled; set => SetProperty(ref _isSnappingEnabled, value); }

        public double CurrentOperationTimeSeconds
        {
            get => _currentOperationTime.TotalSeconds;
            set
            {
                if (Math.Abs(_currentOperationTime.TotalSeconds - value) > 0.001)
                {
                    CurrentOperationTime = TimeSpan.FromSeconds(value);
                    OperationSeekRequested?.Invoke(this, CurrentOperationTime);
                }
            }
        }

        public event EventHandler<TimeSpan> OperationSeekRequested;

        private TimeSpan _currentOperationDuration = TimeSpan.FromSeconds(10);
        public TimeSpan CurrentOperationDuration
        {
            get => _currentOperationDuration;
            set
            {
                if (SetProperty(ref _currentOperationDuration, value))
                {
                    OnPropertyChanged(nameof(CurrentOperationDurationSeconds));
                }
            }
        }

        public double CurrentOperationDurationSeconds => _currentOperationDuration.TotalSeconds;

        private EditTarget _currentEditTarget = EditTarget.Start;
        public EditTarget CurrentEditTarget
        {
            get => _currentEditTarget;
            set
            {
                if (SetProperty(ref _currentEditTarget, value))
                {
                    OnPropertyChanged(nameof(CurrentEditTargetIndex));
                    // When the edit target changes, jump to it on whatever clip is selected — spine
                    // or overlay (SelectedClip), not only Track 1.
                    if (SelectedClip != null)
                    {
                        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        dispatcher.TryEnqueue(() =>
                        {
                            var evt = EditTargetChanged;
                            evt?.Invoke(this, SelectedClip);
                        });
                    }
                }
            }
        }
        
        public event EventHandler<CinematicOperation> EditTargetChanged;

        public int CurrentEditTargetIndex
        {
            get => (int)_currentEditTarget;
            set => CurrentEditTarget = (EditTarget)value;
        }

        public DirectorViewModel()
        {
            TimelineNodes.CollectionChanged += TimelineNodes_CollectionChanged;
            EnsureOverlayTracks(); // always the full set of upper tracks (Track 2..4)
            ResetHistory();        // baseline = the empty project, so the first edit is undoable
        }

        // The track count is fixed: 1 spine + MaxOverlayTracks upper tracks are always present,
        // so the timeline always shows Track 1..4 and nothing has to be "added".
        public void EnsureOverlayTracks()
        {
            while (OverlayTracks.Count < MaxOverlayTracks) AddOverlayTrack();
        }

        private void TimelineNodes_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (CinematicOperation item in e.OldItems)
                {
                    item.PropertyChanged -= CinematicOperation_PropertyChanged;
                }
            }
            if (e.NewItems != null)
            {
                foreach (CinematicOperation item in e.NewItems)
                {
                    item.PropertyChanged += CinematicOperation_PropertyChanged;
                }
            }
            OnPropertyChanged(nameof(TotalStoryTime));
        }

        private void CinematicOperation_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CinematicOperation.OpDuration) || e.PropertyName == nameof(CinematicOperation.TransitionDuration))
            {
                OnPropertyChanged(nameof(TotalStoryTime));
            }
        }

        public async Task AddFilesAsync(IEnumerable<string> filePaths)
        {
            foreach (var path in filePaths)
            {
                TimeSpan duration = TimeSpan.FromSeconds(10);
                Microsoft.UI.Xaml.Media.Imaging.BitmapImage? thumbnail = null;
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    var props = await file.Properties.GetVideoPropertiesAsync();
                    if (props != null && props.Duration.TotalSeconds > 0)
                    {
                        duration = props.Duration;
                    }
                    else
                    {
                        var imgProps = await file.Properties.GetImagePropertiesAsync();
                        if (imgProps != null && imgProps.Width > 0)
                        {
                            duration = TimeSpan.FromSeconds(5); // Default 5s for images
                        }
                    }

                    // Get Thumbnail
                    var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 480, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                    if (thumb != null)
                    {
                        thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await thumbnail.SetSourceAsync(thumb);
                    }
                }
                catch { }

                // Update previous clip's transition if it doesn't have one
                if (TimelineNodes.Count > 0)
                {
                    var lastNode = TimelineNodes[^1];
                    if (lastNode.TransitionDuration == TimeSpan.Zero)
                    {
                        lastNode.TransitionDuration = TimeSpan.FromSeconds(1);
                        if (lastNode.TransitionStyle == TransitionStyle.HardSnap)
                        {
                            lastNode.TransitionStyle = TransitionStyle.Crossfade;
                        }
                    }
                }

                // Insert the new operation
                TimelineNodes.Add(new CinematicOperation
                {
                    FilePath = path,
                    SourceDuration = duration,   // full source length; trim In/Out live within it
                    OpDuration = duration,
                    VideoEndTime = duration,
                    TransitionDuration = TimeSpan.Zero, // Default 0s transition for the new last clip
                    Thumbnail = thumbnail
                });
            }
            RecordIfChanged();
        }

        // Finds which Track 1 clip a given absolute story time falls within. Used to resume
        // playback from the correct clip when there's no selected timeline node to go by
        // (e.g. an overlay is selected instead) rather than silently restarting from clip 0.
        public int GetTimelineIndexForStoryTime(TimeSpan storyTime)
        {
            TimeSpan accumulated = TimeSpan.Zero;
            for (int i = 0; i < TimelineNodes.Count; i++)
            {
                var nodeSpan = TimelineNodes[i].OpDuration + TimelineNodes[i].TransitionDuration;
                if (storyTime < accumulated + nodeSpan || i == TimelineNodes.Count - 1)
                {
                    return i;
                }
                accumulated += nodeSpan;
            }
            return 0;
        }

        // --- Story-time model (§7C): single authority the trackbar + scrubber read, so they
        // agree with playback at transition boundaries. The spine (Track 1) defines total length;
        // transitions are ADDITIVE — each spine clip occupies OpDuration + TransitionDuration.

        // Total composite length = the spine's cumulative span. Overlays live within it.
        public TimeSpan TotalStoryDuration
        {
            get
            {
                var total = TimeSpan.Zero;
                foreach (var clip in TimelineNodes) total += clip.OpDuration + clip.TransitionDuration;
                return total;
            }
        }

        // Story-time start of spine clip `index` = cumulative span of everything before it.
        public TimeSpan GetSpineClipStart(int index)
        {
            var start = TimeSpan.Zero;
            for (int i = 0; i < index && i < TimelineNodes.Count; i++)
                start += TimelineNodes[i].OpDuration + TimelineNodes[i].TransitionDuration;
            return start;
        }

        public async Task AddOverlayAsync(string filePath, TimeSpan startTime, int trackIndex = 0)
        {
            TimeSpan duration = TimeSpan.FromSeconds(5);
            double sourceAspect = 0;
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? thumbnail = null;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                var props = await file.Properties.GetVideoPropertiesAsync();
                if (props != null && props.Duration.TotalSeconds > 0)
                {
                    duration = props.Duration;
                }
                // Real frame dimensions — the PiP box is shaped from these. We never assume an
                // aspect, so read it properly for images too (a .jpg/.png overlay has no video
                // properties, and a portrait photo must not be forced into a landscape box).
                if (props != null && props.Width > 0 && props.Height > 0)
                {
                    sourceAspect = (double)props.Width / props.Height;
                }
                else
                {
                    var imageProps = await file.Properties.GetImagePropertiesAsync();
                    if (imageProps != null && imageProps.Width > 0 && imageProps.Height > 0)
                        sourceAspect = (double)imageProps.Width / imageProps.Height;
                }

                var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 480, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb != null)
                {
                    thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await thumbnail.SetSourceAsync(thumb);
                }
            }
            catch { }

            // An upper-track clip is a normal CinematicOperation placed at the current playhead.
            // Content framing defaults to full-frame (marks at scale 1); the clip appears as a
            // 30% corner PiP via its placement (PlacementScale/Center defaults on the clip).
            if (OverlayTracks.Count == 0) AddOverlayTrack();
            trackIndex = Math.Clamp(trackIndex, 0, OverlayTracks.Count - 1);
            var track = OverlayTracks[trackIndex];

            // Same clamp as dragging: a dropped file must not land on top of an existing clip,
            // since only one clip per track can be active at a time.
            startTime = TimeSpan.FromSeconds(
                track.ClampToFreeSlot(null, Math.Max(0, startTime.TotalSeconds), duration.TotalSeconds));

            var overlay = new CinematicOperation
            {
                FilePath = filePath,
                SourceDuration = duration,   // full source length; trim In/Out live within it
                OpDuration = duration,
                VideoEndTime = duration,
                StartTime = startTime,
                SourceAspect = sourceAspect,
                Volume = 0.0,                // overlays start muted; opt in per-clip if the PiP's audio matters
                PlacementCenterX = track.DefaultCenterX,
                PlacementCenterY = track.DefaultCenterY,
                Thumbnail = thumbnail
            };
            track.Clips.Add(overlay);
            RecordIfChanged();
            // Deliberately does NOT select the new clip: selecting an overlay enters Edit mode,
            // and in Edit mode Play previews that one clip instead of the composite (so the global
            // playhead appears frozen). Adding clips is an Arrange activity — stay in Arrange.
        }

        // Serialization wrapper. OverlayTracks is the current shape; OverlayClips is the legacy
        // flat list, still read so older project files migrate into track 0.
        private class ProjectData
        {
            // Project schema version, so later structural changes can migrate rather than break.
            // Files written before this field existed simply omit it, and keep the initializer
            // default below — i.e. they read as v1, which is what they are.
            //   1 — TimelineNodes + OverlayTracks (current)
            // Bump this, and add a migration step in LoadAsync, whenever the persisted shape changes.
            public int SchemaVersion { get; set; } = CurrentSchemaVersion;

            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> TimelineNodes { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<OverlayTrack> OverlayTracks { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> OverlayClips { get; set; } = new();
        }

        public const int CurrentSchemaVersion = 1;

        public async Task SaveAsync(Windows.Storage.StorageFile file)
        {
            var data = new ProjectData
            {
                TimelineNodes = TimelineNodes,
                OverlayTracks = OverlayTracks
            };
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0); // Clear existing content
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, data, options);
        }

        public async Task LoadAsync(Windows.Storage.StorageFile file)
        {
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            using var stream = await file.OpenStreamForReadAsync();
            
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // Read the raw JSON to determine format (old array vs new wrapper)
            using var reader = new System.IO.StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var trimmed = json.TrimStart();

            System.Collections.ObjectModel.ObservableCollection<CinematicOperation> nodes = null;
            System.Collections.ObjectModel.ObservableCollection<OverlayTrack> tracks = null;
            System.Collections.ObjectModel.ObservableCollection<CinematicOperation> legacyOverlays = null;

            if (trimmed.StartsWith("["))
            {
                // Legacy format: bare array of CinematicOperations
                nodes = System.Text.Json.JsonSerializer.Deserialize<System.Collections.ObjectModel.ObservableCollection<CinematicOperation>>(json, options);
            }
            else
            {
                // New format: ProjectData wrapper
                var data = System.Text.Json.JsonSerializer.Deserialize<ProjectData>(json, options);
                if (data != null)
                {
                    nodes = data.TimelineNodes;
                    tracks = data.OverlayTracks;
                    legacyOverlays = data.OverlayClips;
                }
            }

            if (nodes != null)
            {
                TimelineNodes.Clear();
                foreach (var node in nodes)
                {
                    TimelineNodes.Add(node);
                    _ = LoadThumbnailAsync(node, dispatcher);
                }
            }

            OverlayTracks.Clear();
            if (tracks != null && tracks.Count > 0)
            {
                foreach (var track in tracks)
                {
                    if (OverlayTracks.Count >= MaxOverlayTracks) break;
                    OverlayTracks.Add(track);
                    foreach (var clip in track.Clips) _ = LoadOverlayThumbnailAsync(clip, dispatcher);
                }
            }
            else if (legacyOverlays != null && legacyOverlays.Count > 0)
            {
                // Migrate an older flat overlay list into track 0.
                var track = AddOverlayTrack();
                foreach (var clip in legacyOverlays)
                {
                    track.Clips.Add(clip);
                    _ = LoadOverlayThumbnailAsync(clip, dispatcher);
                }
            }
            EnsureOverlayTracks(); // top up so the timeline always shows the full Track 1..4 set
            OnPropertyChanged(nameof(CanAddOverlayTrack));
            ResetHistory(); // the loaded project is the new baseline, not an undo step
        }

        private async Task LoadThumbnailAsync(CinematicOperation node, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(node.FilePath)) return;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(node.FilePath);
                var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 480, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb != null && dispatcher != null)
                {
                    // Ensure UI thread update
                    dispatcher.TryEnqueue(async () =>
                    {
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(thumb);
                        node.Thumbnail = bitmap;
                    });
                }
            }
            catch { }
        }

        private async Task LoadOverlayThumbnailAsync(CinematicOperation overlay, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(overlay.FilePath)) return;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(overlay.FilePath);
                var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 480, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb != null && dispatcher != null)
                {
                    dispatcher.TryEnqueue(async () =>
                    {
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(thumb);
                        overlay.Thumbnail = bitmap;
                    });
                }
            }
            catch { }
        }

        public void Clear()
        {
            TimelineNodes.Clear();
            OverlayTracks.Clear();
            EnsureOverlayTracks();
            RecordIfChanged();
        }

        // ---- Undo / redo -------------------------------------------------------------------
        // Snapshot-based history reusing the project serialization. One step is recorded per
        // "settle point" (add, remove, move, clear, exit-edit), so a burst of edits within one
        // edit session collapses into a single undo. RecordIfChanged compares the current state to
        // the last settled state and only records a step when something actually changed — so
        // calling it defensively (e.g. on every exit-edit) never litters the history with no-ops.
        private readonly System.Collections.Generic.Stack<string> _undo = new();
        private readonly System.Collections.Generic.Stack<string> _redo = new();
        private string _settled = string.Empty;
        private const int MaxHistory = 50;
        private static readonly System.Text.Json.JsonSerializerOptions _snapshotOptions = new();

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        private string CaptureSnapshot()
        {
            var data = new ProjectData { TimelineNodes = TimelineNodes, OverlayTracks = OverlayTracks };
            return System.Text.Json.JsonSerializer.Serialize(data, _snapshotOptions);
        }

        // Establish the "nothing to undo" baseline — call after construction and after a project load
        // so neither the empty start nor the load itself is an undo step.
        public void ResetHistory()
        {
            _undo.Clear();
            _redo.Clear();
            _settled = CaptureSnapshot();
            RaiseHistoryChanged();
        }

        public void RecordIfChanged()
        {
            var current = CaptureSnapshot();
            if (current == _settled) return;

            if (_settled.Length > 0)
            {
                _undo.Push(_settled);
                if (_undo.Count > MaxHistory)
                {
                    // Drop the oldest entry (bottom of the stack) to cap memory.
                    var kept = _undo.ToArray(); // index 0 = newest
                    _undo.Clear();
                    for (int i = MaxHistory - 1; i >= 0; i--) _undo.Push(kept[i]);
                }
                _redo.Clear();
            }
            _settled = current;
            RaiseHistoryChanged();
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Push(CaptureSnapshot());
            var target = _undo.Pop();
            _settled = target;
            RestoreSnapshot(target);
            RaiseHistoryChanged();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Push(CaptureSnapshot());
            var target = _redo.Pop();
            _settled = target;
            RestoreSnapshot(target);
            RaiseHistoryChanged();
        }

        private void RaiseHistoryChanged()
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        private void RestoreSnapshot(string json)
        {
            ProjectData data;
            try { data = System.Text.Json.JsonSerializer.Deserialize<ProjectData>(json, _snapshotOptions); }
            catch { return; }
            if (data == null) return;

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            SelectedTimelineNode = null;
            SelectedOverlay = null;

            TimelineNodes.Clear();
            if (data.TimelineNodes != null)
                foreach (var node in data.TimelineNodes)
                {
                    TimelineNodes.Add(node);
                    _ = LoadThumbnailAsync(node, dispatcher);
                }

            OverlayTracks.Clear();
            if (data.OverlayTracks != null)
                foreach (var track in data.OverlayTracks)
                {
                    if (OverlayTracks.Count >= MaxOverlayTracks) break;
                    OverlayTracks.Add(track);
                    foreach (var clip in track.Clips) _ = LoadOverlayThumbnailAsync(clip, dispatcher);
                }

            EnsureOverlayTracks();
            OnPropertyChanged(nameof(CanAddOverlayTrack));
            OnPropertyChanged(nameof(TotalStoryTime));
        }
    }
}
