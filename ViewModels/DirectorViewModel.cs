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
        // ---- Tracks --------------------------------------------------------------------------
        // Four peer tracks. There is no privileged "spine": what used to be Track 1's structural
        // behaviour is now Tracks[0].IsGapless being on, and it can be turned off, or turned on
        // for any other track. Compositing order is track index — Tracks[3] draws over Tracks[0]
        // (ARCHITECTURE.md §5.4).
        public const int TrackCount = 4;
        public ObservableCollection<TimelineTrack> Tracks { get; } = new();

        // Default PiP corners so stacked overlays don't land on top of each other. Track 0 is
        // full-frame by default, hence centre.
        private static readonly (double x, double y)[] TrackCorners =
            { (0.5, 0.5), (0.72, 0.72), (0.28, 0.72), (0.72, 0.28) };

        // The clips on track 0. Kept as a named property because a great deal of code still refers
        // to it; it is a view onto Tracks[0], not a separate collection. Its instance is stable for
        // the lifetime of the view model (load and undo repopulate in place), so anything
        // subscribed to its CollectionChanged stays subscribed.
        public ObservableCollection<CinematicOperation> TimelineNodes => Tracks[0].Clips;

        // Tracks 1..3, holding the SAME TimelineTrack instances as Tracks[1..3]. Tracks are created
        // once and never added or removed, so the two collections cannot drift.
        public ObservableCollection<TimelineTrack> OverlayTracks { get; } = new();
        public const int MaxOverlayTracks = TrackCount - 1;



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

        // The inspector/storyboard panel shows whenever there is something to inspect: a selected
        // clip, an active edit, or the user having PINNED it open. Selecting a clip used to force
        // Edit mode, which was the only way to see a clip's properties — so the panel could key off
        // edit state alone. Now selection stands on its own and the panel follows it.
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

        public bool IsStoryboardVisible => _isStoryboardPinned || _isEditMode || HasSelection;

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

        // Legacy alias. Duration is no longer track 0's to define — see TotalStoryDuration.
        public TimeSpan TotalStoryTime => TotalStoryDuration;

        private TimeSpan _currentStoryTime;
        public TimeSpan CurrentStoryTime
        {
            get => _currentStoryTime;
            set => SetProperty(ref _currentStoryTime, value);
        }

        // ---- Selection -----------------------------------------------------------------------
        // ONE selected clip, whatever track it is on. There used to be two mutually-exclusive
        // selection properties that had to null each other out, purely because track 0's clips
        // lived in a different collection from everyone else's.
        private CinematicOperation _selectedClip;
        public CinematicOperation SelectedClip
        {
            get => _selectedClip;
            set { if (SetProperty(ref _selectedClip, value)) RaiseSelectionChanged(); }
        }

        // Everything that derives from "which clip is selected". IsStoryboardVisible is in here
        // because the inspector now follows the selection, not the edit state.
        private void RaiseSelectionChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTrackIndex));
            OnPropertyChanged(nameof(SelectedTrack));
            OnPropertyChanged(nameof(SelectedTrackLabel));
            OnPropertyChanged(nameof(IsSelectedPositionEditable));
            OnPropertyChanged(nameof(IsSelectedTransitionApplicable));
            OnPropertyChanged(nameof(ModeLabel));
            OnPropertyChanged(nameof(IsStoryboardVisible));
            OnPropertyChanged(nameof(IsDockVisible));
        }

        public bool HasSelection => _selectedClip != null;

        // Index of the track owning a clip, or -1 if it is on none.
        public int TrackIndexOf(CinematicOperation clip)
        {
            if (clip == null) return -1;
            for (int i = 0; i < Tracks.Count; i++)
                if (Tracks[i].Clips.Contains(clip)) return i;
            return -1;
        }

        public TimelineTrack TrackOf(CinematicOperation clip)
        {
            int i = TrackIndexOf(clip);
            return i >= 0 ? Tracks[i] : null;
        }

        public int SelectedTrackIndex => TrackIndexOf(_selectedClip);
        public TimelineTrack SelectedTrack => TrackOf(_selectedClip);

        // A clip's timeline position is editable only where the user owns it. On a gapless track
        // position is derived from clip order, so the field would be writing a value that
        // Normalize immediately overwrites.
        public bool IsSelectedPositionEditable
        {
            get { var t = SelectedTrack; return t != null && !t.IsGapless; }
        }

        // Transitions bridge one clip into the next, which only means something where clips are
        // adjacent by construction — i.e. on a gapless track.
        public bool IsSelectedTransitionApplicable
        {
            get { var t = SelectedTrack; return t != null && t.IsGapless; }
        }

        public string SelectedTrackLabel
        {
            get
            {
                var track = SelectedTrack;
                if (track == null) return string.Empty;
                return $"Track {SelectedTrackIndex + 1} · {(track.IsGapless ? "sequence" : "free")}";
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
            EnsureTracks();        // the full fixed set, before anything can reference Tracks[0]
            TimelineNodes.CollectionChanged += TimelineNodes_CollectionChanged;
            ResetHistory();        // baseline = the empty project, so the first edit is undoable
        }

        // The track count is fixed at TrackCount, and the instances are created exactly once:
        // loading a project or undoing refills their clip lists in place rather than replacing
        // them, so every CollectionChanged subscription in the app survives.
        public void EnsureTracks()
        {
            while (Tracks.Count < TrackCount)
            {
                int i = Tracks.Count;
                var track = new TimelineTrack
                {
                    Name = "Track " + (i + 1),
                    // Track 0 ships gapless, so the default behaviour matches what Track 1 used to
                    // do structurally. Nothing depends on it any more — it can be switched off.
                    IsGapless = i == 0,
                    DefaultCenterX = TrackCorners[i].x,
                    DefaultCenterY = TrackCorners[i].y
                };
                Tracks.Add(track);
                if (i > 0) OverlayTracks.Add(track);
            }
        }

        // Back-compat name for the fixed-track guarantee.
        public void EnsureOverlayTracks() => EnsureTracks();

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
            // Adding, removing or reordering on a gapless track reflows everything after the
            // change. This is the explicit form of what the spine's cumulative walk did implicitly.
            Tracks[0].Normalize();
            OnPropertyChanged(nameof(TotalStoryTime));
        }

        private void CinematicOperation_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CinematicOperation.OpDuration) || e.PropertyName == nameof(CinematicOperation.TransitionDuration))
            {
                // A clip getting longer or shorter moves everything after it on a gapless track.
                Tracks[0].Normalize();
                OnPropertyChanged(nameof(TotalStoryTime));
            }
        }

        // ---- Adding clips --------------------------------------------------------------------
        // One path for every track. Track 0 used to have its own method that appended in order and
        // gave clips full volume, while every other track had a different one that placed by time
        // and muted them. Those differences are now DEFAULTS chosen from the track, not separate
        // code paths -- which is why LoadIntoTrack could no longer tell them apart correctly.

        private static async Task<CinematicOperation> CreateClipAsync(string path, TimelineTrack track, bool isBaseTrack)
        {
            TimeSpan duration = TimeSpan.FromSeconds(10);
            double sourceAspect = 0;
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? thumbnail = null;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var props = await file.Properties.GetVideoPropertiesAsync();
                if (props != null && props.Duration.TotalSeconds > 0) duration = props.Duration;

                // Real frame dimensions -- the PiP box is shaped from these. Never assume an
                // aspect: a .jpg/.png has no video properties, and a portrait photo must not be
                // forced into a landscape box.
                if (props != null && props.Width > 0 && props.Height > 0)
                {
                    sourceAspect = (double)props.Width / props.Height;
                }
                else
                {
                    var imageProps = await file.Properties.GetImagePropertiesAsync();
                    if (imageProps != null && imageProps.Width > 0 && imageProps.Height > 0)
                    {
                        sourceAspect = (double)imageProps.Width / imageProps.Height;
                        if (props == null || props.Duration.TotalSeconds <= 0)
                            duration = TimeSpan.FromSeconds(5);   // default hold for a still
                    }
                }

                var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 480, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb != null)
                {
                    thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await thumbnail.SetSourceAsync(thumb);
                }
            }
            catch { }

            return new CinematicOperation
            {
                FilePath = path,
                SourceDuration = duration,   // full source length; trim In/Out live within it
                OpDuration = duration,
                VideoEndTime = duration,
                SourceAspect = sourceAspect,
                // Defaults, not rules. The base track is the audio bed and fills the frame; upper
                // tracks start muted and land in their own corner so stacked PiPs do not collide.
                Volume = isBaseTrack ? 1.0 : 0.0,
                PlacementWidth = isBaseTrack ? 1.0 : 0.3,
                PlacementHeight = isBaseTrack ? 1.0 : 0.3,
                PlacementCenterX = track.DefaultCenterX,
                PlacementCenterY = track.DefaultCenterY,
                Thumbnail = thumbnail
            };
        }

        public async Task AddClipsToTrackAsync(IEnumerable<string> filePaths, int trackIndex, TimeSpan startTime)
        {
            EnsureTracks();
            trackIndex = Math.Clamp(trackIndex, 0, Tracks.Count - 1);
            var track = Tracks[trackIndex];

            foreach (var path in filePaths)
            {
                var clip = await CreateClipAsync(path, track, trackIndex == 0);

                if (track.IsGapless)
                {
                    // Order is position, so append. Give the previous clip a crossfade if it has
                    // none yet -- that is what makes a dropped sequence play as one piece.
                    if (track.Clips.Count > 0)
                    {
                        var last = track.Clips[^1];
                        if (last.TransitionDuration == TimeSpan.Zero)
                        {
                            last.TransitionDuration = TimeSpan.FromSeconds(1);
                            if (last.TransitionStyle == TransitionStyle.HardSnap)
                                last.TransitionStyle = TransitionStyle.Crossfade;
                        }
                    }
                    track.Clips.Add(clip);
                }
                else
                {
                    // Free track: place at the requested time, clamped off its siblings, since
                    // only one clip per track can be active at a time.
                    clip.StartTime = TimeSpan.FromSeconds(track.ClampToFreeSlot(
                        null, Math.Max(0, startTime.TotalSeconds), clip.OpDuration.TotalSeconds));
                    track.Clips.Add(clip);
                }

                track.Normalize();
            }

            RecordIfChanged();
        }

        public Task AddFilesAsync(IEnumerable<string> filePaths)
            => AddClipsToTrackAsync(filePaths, 0, TimeSpan.Zero);

        // Which track-0 clip a given absolute story time falls within. Reads the clips' real
        // StartTimes rather than re-walking a cumulative sum, so it agrees with the timeline by
        // construction. Past the end it returns the last clip, so playback resumes somewhere sane.
        public int GetTimelineIndexForStoryTime(TimeSpan storyTime)
        {
            var clips = TimelineNodes;
            for (int i = 0; i < clips.Count; i++)
            {
                if (storyTime < clips[i].StartTime + clips[i].OpDuration + clips[i].TransitionDuration
                    || i == clips.Count - 1)
                {
                    return i;
                }
            }
            return 0;
        }

        // --- Story-time model (§7C): single authority the trackbar + scrubber read, so they
        // agree with playback at transition boundaries. The spine (Track 1) defines total length;
        // transitions are ADDITIVE — each spine clip occupies OpDuration + TransitionDuration.

        // Total composite length = the latest point at which ANY track finishes. Duration used to
        // be track 0's to define, which is why a project made only of overlay clips was unplayable
        // and invisible: the timeline scale divided by a zero total.
        public TimeSpan TotalStoryDuration => ContentEnd;

        // The latest point at which any clip on any track ends.
        public TimeSpan ContentEnd
        {
            get
            {
                var end = TimeSpan.Zero;
                foreach (var track in Tracks)
                {
                    var trackEnd = track.ContentEnd;
                    if (trackEnd > end) end = trackEnd;
                }
                return end;
            }
        }

        // Story-time start of a track-0 clip. Now simply the clip's own StartTime: a gapless track
        // derives those from clip order in TimelineTrack.Normalize, so there is nothing to
        // recompute here and no way for the two to disagree.
        public TimeSpan GetSpineClipStart(int index)
        {
            var clips = TimelineNodes;
            return index >= 0 && index < clips.Count ? clips[index].StartTime : TimeSpan.Zero;
        }

        // Back-compat entry point. trackIndex is now a UNIFORM track index (0..n-1), not an
        // overlay-relative one -- the two used to differ by one, which is exactly the bug that
        // sent track 0 down the overlay path once the timeline started passing uniform indices.
        public Task AddOverlayAsync(string filePath, TimeSpan startTime, int trackIndex = 1)
            => AddClipsToTrackAsync(new[] { filePath }, trackIndex, startTime);

        // Serialization wrapper. OverlayTracks is the current shape; OverlayClips is the legacy
        // flat list, still read so older project files migrate into track 0.
        private class ProjectData
        {
            // Project schema version, so later structural changes can migrate rather than break.
            // Files written before this field existed simply omit it, and keep the initializer
            // default below — i.e. they read as v1, which is what they are.
            //   1 — TimelineNodes (track 0) + OverlayTracks (tracks 1..3), as separate shapes
            //   2 — Tracks: one uniform list of TimelineTrack
            // Bump this, and add a migration step in ApplyProjectData, whenever the shape changes.
            public int SchemaVersion { get; set; } = CurrentSchemaVersion;

            // v2 shape.
            public System.Collections.ObjectModel.ObservableCollection<TimelineTrack> Tracks { get; set; } = new();

            // v1 shapes, still read so older project files migrate. Never written any more.
            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> TimelineNodes { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<TimelineTrack> OverlayTracks { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> OverlayClips { get; set; } = new();
        }

        public const int CurrentSchemaVersion = 2;

        public async Task SaveAsync(Windows.Storage.StorageFile file)
        {
            var data = new ProjectData { Tracks = Tracks };
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0); // Clear existing content
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, data, options);
        }

        // Fill the fixed track set from deserialized data, migrating whichever shape it is in.
        // Clip lists are refilled IN PLACE: the TimelineTrack instances live for the lifetime of
        // the view model, so every CollectionChanged subscription in the app survives a load.
        private void ApplyProjectData(ProjectData data, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        {
            EnsureTracks();
            foreach (var track in Tracks) track.Clips.Clear();

            // v2: one uniform list, track i to track i.
            if (data.Tracks != null && data.Tracks.Count > 0)
            {
                for (int i = 0; i < data.Tracks.Count && i < Tracks.Count; i++)
                {
                    var src = data.Tracks[i];
                    var dst = Tracks[i];
                    dst.Name = src.Name;
                    dst.IsGapless = src.IsGapless;
                    dst.IsMuted = src.IsMuted;
                    dst.IsHidden = src.IsHidden;
                    dst.IsLocked = src.IsLocked;
                    dst.DefaultCenterX = src.DefaultCenterX;
                    dst.DefaultCenterY = src.DefaultCenterY;
                    foreach (var clip in src.Clips) dst.Clips.Add(clip);
                }
            }
            else
            {
                // v1: track 0's clips came from TimelineNodes, and had no StartTime of their own —
                // position was implied by order. Normalize() below derives the real start times.
                if (data.TimelineNodes != null)
                    foreach (var clip in data.TimelineNodes) Tracks[0].Clips.Add(clip);

                if (data.OverlayTracks != null && data.OverlayTracks.Count > 0)
                {
                    for (int i = 0; i < data.OverlayTracks.Count && i + 1 < Tracks.Count; i++)
                        foreach (var clip in data.OverlayTracks[i].Clips) Tracks[i + 1].Clips.Add(clip);
                }
                else if (data.OverlayClips != null && data.OverlayClips.Count > 0)
                {
                    // v0: a single flat overlay list, which belonged to the first overlay track.
                    foreach (var clip in data.OverlayClips) Tracks[1].Clips.Add(clip);
                }
            }

            foreach (var track in Tracks)
            {
                track.Normalize();
                foreach (var clip in track.Clips) _ = LoadThumbnailAsync(clip, dispatcher);
            }

            OnPropertyChanged(nameof(TotalStoryTime));
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

            ProjectData data;
            if (trimmed.StartsWith("["))
            {
                // v0: a bare array of clips, which was track 0 and nothing else.
                var nodes = System.Text.Json.JsonSerializer.Deserialize<
                    System.Collections.ObjectModel.ObservableCollection<CinematicOperation>>(json, options);
                data = new ProjectData { TimelineNodes = nodes ?? new(), Tracks = new() };
            }
            else
            {
                data = System.Text.Json.JsonSerializer.Deserialize<ProjectData>(json, options);
            }

            if (data == null) return;
            ApplyProjectData(data, dispatcher);
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

        public void Clear()
        {
            foreach (var track in Tracks) track.Clips.Clear();
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
            var data = new ProjectData { Tracks = Tracks };
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

            SelectedClip = null;

            ApplyProjectData(data, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        }
    }
}
