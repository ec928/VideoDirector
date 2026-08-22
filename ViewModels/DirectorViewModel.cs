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
        public const int MaxTracks = 4;
        public ObservableCollection<TimelineTrack> Tracks { get; } = new();

        // Default corners so stacked PiPs don't land on top of each other.
        // We have 4 tracks now. Track 1 defaults to center/full screen.
        private static readonly (double x, double y, double w, double h)[] TrackDefaults =
            { 
                (0.5, 0.5, 1.0, 1.0), // Track 1: full screen center
                (0.72, 0.72, 0.3, 0.3), // Track 2
                (0.28, 0.72, 0.3, 0.3), // Track 3
                (0.72, 0.28, 0.3, 0.3)  // Track 4
            };

        public bool CanAddOverlayTrack => false; // Track count is now fixed.

        public TimelineTrack AddTrack(int index)
        {
            if (index >= MaxTracks) return null;
            var track = new TimelineTrack
            {
                Name = "Track " + (index + 1),
                DefaultCenterX = TrackDefaults[index].x,
                DefaultCenterY = TrackDefaults[index].y,
                IsGapless = false // All tracks allow gaps now
            };
            
            // Re-route clip property changes to TotalStoryTime
            track.Clips.CollectionChanged += (s, e) =>
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
            };

            Tracks.Insert(index, track);
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
                    OnPropertyChanged(nameof(ModeLabel));
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    if (!value) IsControlsVisible = true;
                }
            }
        }

        // CINEMATIC MODE — screening, with nothing on screen but the picture.
        //
        // The chrome is already dismissible: IsControlsVisible drives both the transport pill and
        // the track dock, and the inactivity timer already lowers it. What this adds is (a) the
        // inspector goes too, (b) the dock stays down even when the pointer wakes the pill, so a
        // mouse twitch does not throw the timeline back over the picture, and (c) the auto-hide
        // applies while paused, not only while rolling — you should be able to pause on a frame and
        // still be looking at just the frame.
        private bool _isCinematicMode;
        public bool IsCinematicMode
        {
            get => _isCinematicMode;
            set
            {
                if (SetProperty(ref _isCinematicMode, value))
                {
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    OnPropertyChanged(nameof(IsTrackDockVisible));
                    // Show the pill on the way in and on the way out: entering, so the exit control
                    // is visible rather than having to be hunted for; leaving, so the editor does
                    // not come back with its transport already faded out.
                    IsControlsVisible = true;
                }
            }
        }

        // The dock follows the pill EXCEPT in cinematic mode, where it stays down for good.
        public bool IsTrackDockVisible => !_isCinematicMode && _isControlsVisible;

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

        // Its own control, at last. This used to be driven by the panel pin, so the only way to
        // read the geometry HUD was to pin the inspector open - two unrelated things on one button,
        // and the tooltip mentioned only the other one.
        private bool _isTelemetryVisible = false;
        public bool IsTelemetryVisible
        {
            get => _isTelemetryVisible;
            set => SetProperty(ref _isTelemetryVisible, value);
        }

        // Whether the user WANTS the inspector, as distinct from whether there is anything to put
        // in it.
        //
        // This was a PIN - it could only ever add visibility, because the test was
        // (pinned || HasSelection). With a clip selected HasSelection was already true, so the
        // button was inert exactly when you would reach for it: you could not close the panel.
        // Now it is a straight toggle, and selection decides the panel's CONTENT rather than its
        // existence.
        //
        // Defaults open so selecting a clip behaves as it always has; closing is the new part.
        private bool _isInspectorOpen = true;
        public bool IsInspectorOpen
        {
            get => _isInspectorOpen;
            set
            {
                if (SetProperty(ref _isInspectorOpen, value))
                    OnPropertyChanged(nameof(IsStoryboardVisible));
            }
        }

        public bool IsStoryboardVisible =>
            !_isCinematicMode && !_isPlaying && _isInspectorOpen && HasSelection;

        private bool _isControlsVisible = true;
        public bool IsControlsVisible
        {
            get => _isControlsVisible;
            set
            {
                if (SetProperty(ref _isControlsVisible, value))
                    OnPropertyChanged(nameof(IsTrackDockVisible));
            }
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
                TimeSpan max = TimeSpan.Zero;
                foreach (var track in Tracks)
                {
                    foreach (var node in track.Clips)
                    {
                        var end = node.StartTime + node.OpDuration + node.TransitionDuration;
                        if (end > max) max = end;
                    }
                }
                return max;
            }
        }

        private TimeSpan _currentStoryTime;
        public TimeSpan CurrentStoryTime
        {
            get => _currentStoryTime;
            set => SetProperty(ref _currentStoryTime, value);
        }

        private TimeSpan? _loopRegionStart;
        public TimeSpan? LoopRegionStart
        {
            get => _loopRegionStart;
            set => SetProperty(ref _loopRegionStart, value);
        }

        private TimeSpan? _loopRegionEnd;
        public TimeSpan? LoopRegionEnd
        {
            get => _loopRegionEnd;
            set => SetProperty(ref _loopRegionEnd, value);
        }

        private CinematicOperation _selectedClip;
        public CinematicOperation SelectedClip
        {
            get => _selectedClip;
            set
            {
                if (SetProperty(ref _selectedClip, value))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    OnPropertyChanged(nameof(IsTrack1Selected));
                    OnPropertyChanged(nameof(IsOverlaySelected));
                    OnPropertyChanged(nameof(SelectedTrackLabel));
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        // SelectedTimelineNode and SelectedOverlay were used for splitting properties.
        // We map them roughly for backwards compatibility with UI bindings, though we should unify them.
        // Role-filtered views of SelectedClip. Assigning null DESELECTS - it used to be swallowed
        // by an `if (value != null)` guard, which made ExitEditMode's
        //
        //     ViewModel.SelectedTimelineNode = null;
        //     ViewModel.SelectedOverlay = null;
        //
        // a no-op despite the comment above it saying it clears the selection. Nothing else in
        // normal use clears one, so HasSelection went true on the first click and stayed true for
        // the rest of the session: the inspector could never be dismissed, and the panel-pin toggle
        // - whose only job is to keep it open when nothing is selected - had nothing left to do.
        //
        // Null clears only when the CURRENT selection belongs to that role, so setting one view to
        // null cannot silently drop a selection the other view owns.
        public CinematicOperation SelectedTimelineNode
        {
            get => (IsTrack1Selected) ? _selectedClip : null;
            set
            {
                if (value != null) SelectedClip = value;
                else if (IsTrack1Selected) SelectedClip = null;
            }
        }

        public CinematicOperation SelectedOverlay
        {
            get => (!IsTrack1Selected && _selectedClip != null) ? _selectedClip : null;
            set
            {
                if (value != null) SelectedClip = value;
                else if (_selectedClip != null && !IsTrack1Selected) SelectedClip = null;
            }
        }

        public bool HasSelection => _selectedClip != null;

        // IsTrack1Selected / IsOverlaySelected drive the visibility of the track-specific rows.
        public bool IsTrack1Selected
        {
            get
            {
                if (_selectedClip == null || Tracks.Count == 0) return false;
                return Tracks[0].Clips.Contains(_selectedClip);
            }
        }
        public bool IsOverlaySelected => _selectedClip != null && !IsTrack1Selected;

        public string SelectedTrackLabel
        {
            get
            {
                if (_selectedClip != null)
                {
                    for (int i = 0; i < Tracks.Count; i++)
                    {
                        if (Tracks[i].Clips.Contains(_selectedClip))
                        {
                            return i == 0 ? "Track 1 · main" : $"Track {i + 1} · overlay";
                        }
                    }
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

        public double CurrentOperationTimeSeconds
        {
            get => _currentOperationTime.TotalSeconds;
            set
            {
                if (Math.Abs(_currentOperationTime.TotalSeconds - value) > 0.001)
                {
                    CurrentOperationTime = TimeSpan.FromSeconds(value);
                    OperationSeekRequested?.Invoke(this, CurrentOperationTime);

                    if (SelectedClip is CinematicOperation clip && clip.PlaybackSpeed <= 0)
                    {
                        clip.VideoStartTime = CurrentOperationTime;
                    }
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

        // WHICH FRAMING RECTANGLE IS SELECTED, or null for none.
        //
        // Deliberately separate from CurrentEditTarget. That one decides which mark Edit mode
        // SEEDS ITS VIEW FROM, and changing it re-enters Edit — which is the wrong thing to do
        // half way through dragging a rectangle. This is selection only: it drives the highlight
        // and it decides whether the wheel resizes a keyframe or zooms the live view.
        //
        // Transient by design. Clicking empty canvas clears it and hands the wheel back to the
        // view, so both ways of authoring a mark stay available: drag the rectangle directly, or
        // frame the picture and press Set.
        private EditTarget? _selectedMark;
        public EditTarget? SelectedMark
        {
            get => _selectedMark;
            set
            {
                if (_selectedMark == value) return;
                _selectedMark = value;
                OnPropertyChanged(nameof(SelectedMark));
                OnPropertyChanged(nameof(HasMarkSelection));
            }
        }

        public bool HasMarkSelection => _selectedMark.HasValue;

        public int CurrentEditTargetIndex
        {
            get => (int)_currentEditTarget;
            set => CurrentEditTarget = (EditTarget)value;
        }

        public DirectorViewModel()
        {
            EnsureTracks(); // always the full set of tracks
            ResetHistory(); // baseline = the empty project, so the first edit is undoable
        }

        // The track count is fixed: 1 main + MaxOverlayTracks upper tracks are always present.
        public void EnsureTracks()
        {
            while (Tracks.Count < MaxTracks) AddTrack(Tracks.Count);
        }

        public event EventHandler ClipPropertyChanged;

        private void CinematicOperation_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CinematicOperation.OpDuration) || e.PropertyName == nameof(CinematicOperation.TransitionDuration) || e.PropertyName == nameof(CinematicOperation.StartTime))
            {
                OnPropertyChanged(nameof(TotalStoryTime));
            }
            ClipPropertyChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task AddFilesAsync(IEnumerable<string> filePaths)
        {
            foreach (var path in filePaths)
            {
                TimeSpan duration = TimeSpan.FromSeconds(10);
                double sourceAspect = 0;
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
                            // An image has no duration of its own, so this IS the hold.
                            duration = TimeSpan.FromSeconds(10);
                            if (imgProps.Height > 0)
                                sourceAspect = (double)imgProps.Width / imgProps.Height;
                        }
                    }

                    // The aspect, which this path never read at all. Without it AspectOf returns 0,
                    // ApplyOverlayBox refuses to lay out a box, and the clip renders nothing — the
                    // failure was invisible for video because CacheOverlayAspect backfills it from
                    // the decoder, and an image has no decoder to backfill from.
                    if (sourceAspect <= 0 && props != null && props.Width > 0 && props.Height > 0)
                        sourceAspect = (double)props.Width / props.Height;

                    // Get Thumbnail
                    var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 480, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                    if (thumb != null)
                    {
                        thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await thumbnail.SetSourceAsync(thumb);
                    }
                }
                catch { }

                // Update previous clip's transition if it doesn't have one, if dropping on Track 1 and it's gapless.
                var mainTrack = Tracks[0];
                double newStartTime = 0;
                
                if (mainTrack.Clips.Count > 0)
                {
                    var lastNode = mainTrack.Clips[^1];
                    if (mainTrack.IsGapless && lastNode.TransitionDuration == TimeSpan.Zero)
                    {
                        lastNode.TransitionDuration = TimeSpan.FromSeconds(1);
                        if (lastNode.TransitionStyle == TransitionStyle.HardSnap)
                        {
                            lastNode.TransitionStyle = TransitionStyle.Crossfade;
                        }
                    }
                    newStartTime = mainTrack.ClampToFreeSlot(null, mainTrack.Clips[^1].StartTimeSeconds + mainTrack.Clips[^1].OpDuration.TotalSeconds, duration.TotalSeconds);
                }

                // Insert the new operation on Track 1
                mainTrack.Clips.Add(new CinematicOperation
                {
                    FilePath = path,
                    SourceDuration = duration,   // full source length; trim In/Out live within it
                    OpDuration = duration,
                    VideoEndTime = duration,
                    SourceAspect = sourceAspect,
                    StartTime = TimeSpan.FromSeconds(newStartTime),
                    TransitionDuration = TimeSpan.Zero, // Default 0s transition for the new last clip
                    Thumbnail = thumbnail,
                    PlacementCenterX = mainTrack.DefaultCenterX,
                    PlacementCenterY = mainTrack.DefaultCenterY,
                    PlacementWidth = TrackDefaults[0].w,
                    PlacementHeight = TrackDefaults[0].h
                });
            }
            RecordIfChanged();
        }

        // Which Track 1 clip a story time falls within. Drives playback resume AND the timeline
        // highlight, so it must agree with what the compositor puts on screen.
        //
        // It did not. This was a second, subtly different rule: it extended each clip's window by
        // its TransitionDuration, and it returned the FIRST match in collection order. Either alone
        // is enough to disagree with the compositor at a join - so the picture, the HUD and the
        // inspector could all name the image while the timeline still highlighted the clip before
        // it. Same rule as ResolveActiveClip now: half-open window, and of the clips covering the
        // instant the one that STARTED LATER wins.
        public int GetTimelineIndexForStoryTime(TimeSpan storyTime)
        {
            if (Tracks.Count == 0 || Tracks[0].Clips.Count == 0) return 0;

            int best = -1;
            long bestStart = 0;
            for (int i = 0; i < Tracks[0].Clips.Count; i++)
            {
                var clip = Tracks[0].Clips[i];
                if (!ClipGeometry.Covers(clip.StartTime.Ticks, clip.OpDuration.Ticks, storyTime.Ticks))
                    continue;
                if (best < 0 || ClipGeometry.Supersedes(clip.StartTime.Ticks, bestStart))
                {
                    best = i;
                    bestStart = clip.StartTime.Ticks;
                }
            }
            if (best >= 0) return best;
            // If it falls in a gap, return the clip index just before it or the last clip
            for (int i = Tracks[0].Clips.Count - 1; i >= 0; i--)
            {
                if (storyTime >= Tracks[0].Clips[i].StartTime) return i;
            }
            return 0;
        }

        // --- Story-time model (§7C): single authority the trackbar + scrubber read, so they
        // agree with playback at transition boundaries. 

        // Total composite length = max end time across all tracks.
        public TimeSpan TotalStoryDuration => TotalStoryTime;

        // Story-time start of spine clip `index`.
        public TimeSpan GetSpineClipStart(int index)
        {
            if (Tracks.Count == 0 || index < 0 || index >= Tracks[0].Clips.Count) return TimeSpan.Zero;
            return Tracks[0].Clips[index].StartTime;
        }

        // Raised once a clip is in a track. The view wires this to the still-frame prebake:
        // an image is a still, and without a baked bitmap its first activation goes down the
        // video path - MediaSource tries to open a .jpg, fails, and the picture only appears
        // once the decode lands and nudges the composite. Baking on add skips that entirely.
        public event EventHandler<CinematicOperation>? ClipAdded;

        public async Task AddOverlayAsync(string filePath, TimeSpan startTime, int trackIndex = 0)
        {
            // Images have no duration of their own, so this default IS the hold. Ten seconds is
            // enough for a Ken Burns move to read; five was too short to see one land.
            TimeSpan duration = TimeSpan.FromSeconds(10);
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
            if (Tracks.Count == 0) EnsureTracks();
            trackIndex = Math.Clamp(trackIndex, 0, Tracks.Count - 1);
            var track = Tracks[trackIndex];

            // Same clamp as dragging: a dropped file must not land on top of an existing clip
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
                Volume = trackIndex == 0 ? 1.0 : 0.0,
                PlacementCenterX = track.DefaultCenterX,
                PlacementCenterY = track.DefaultCenterY,
                Thumbnail = thumbnail,
                PlacementWidth = TrackDefaults[trackIndex].w,
                PlacementHeight = TrackDefaults[trackIndex].h
            };
            track.Clips.Add(overlay);
            ClipAdded?.Invoke(this, overlay);
            RecordIfChanged();
            // Deliberately does NOT select the new clip: selecting an overlay enters Edit mode,
            // and in Edit mode Play previews that one clip instead of the composite (so the global
            // playhead appears frozen). Adding clips is an Arrange activity — stay in Arrange.
        }

        // Bumped when the meaning of saved fields changes, so a load can tell an old file from a
        // new one instead of guessing from the values.
        //   0 (absent) — SpatialMark X/Y are raw player-pane pixels.
        //   1          — SpatialMark X/Y are fractions of the video's fit rectangle.
        // Version 0 files are converted on first draw; see VideoPlaybackEngine.EnsureMarksNormalized.
        private const int CurrentSchemaVersion = 1;

        // Serialization wrapper.
        private class ProjectData
        {
            public int SchemaVersion { get; set; }   // absent in pre-versioned files => 0
            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> TimelineNodes { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<TimelineTrack> OverlayTracks { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<TimelineTrack> Tracks { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> OverlayClips { get; set; } = new();
        }

        // Tag every clip from a pre-normalisation file. Covers all four shapes a project can
        // arrive in — bare node array, Tracks, and the two legacy overlay collections — because a
        // clip that slips through would have its pixel translate multiplied by the fit again and
        // fly off screen.
        private static void MarkClipsLegacyIfNeeded(
            int schemaVersion,
            System.Collections.ObjectModel.ObservableCollection<CinematicOperation> nodes,
            System.Collections.ObjectModel.ObservableCollection<TimelineTrack> tracks,
            System.Collections.ObjectModel.ObservableCollection<TimelineTrack> legacyOverlayTracks,
            System.Collections.ObjectModel.ObservableCollection<CinematicOperation> legacyOverlays)
        {
            if (schemaVersion >= 1) return;

            void Flag(System.Collections.Generic.IEnumerable<CinematicOperation> clips)
            {
                if (clips == null) return;
                foreach (var c in clips) if (c != null) c.MarksAreLegacyPixels = true;
            }

            Flag(nodes);
            Flag(legacyOverlays);
            if (tracks != null) foreach (var t in tracks) Flag(t?.Clips);
            if (legacyOverlayTracks != null) foreach (var t in legacyOverlayTracks) Flag(t?.Clips);
        }

        public async Task SaveAsync(Windows.Storage.StorageFile file)
        {
            var data = new ProjectData
            {
                SchemaVersion = CurrentSchemaVersion,
                Tracks = Tracks
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
            System.Collections.ObjectModel.ObservableCollection<TimelineTrack> legacyOverlayTracks = null;
            System.Collections.ObjectModel.ObservableCollection<TimelineTrack> tracks = null;
            System.Collections.ObjectModel.ObservableCollection<CinematicOperation> legacyOverlays = null;
            int schemaVersion = 0;

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
                    schemaVersion = data.SchemaVersion;
                    nodes = data.TimelineNodes;
                    legacyOverlayTracks = data.OverlayTracks;
                    tracks = data.Tracks;
                    legacyOverlays = data.OverlayClips;
                }
            }

            // Flag pre-normalisation marks so the engine converts them on first draw, when the
            // pane size is actually known. Doing it here would risk dividing by an unmeasured pane.
            MarkClipsLegacyIfNeeded(schemaVersion, nodes, tracks, legacyOverlayTracks, legacyOverlays);

            Tracks.Clear();
            EnsureTracks();

            if (tracks != null && tracks.Count > 0)
            {
                Tracks.Clear();
                foreach (var track in tracks)
                {
                    if (Tracks.Count >= MaxTracks) break;
                    // Add listeners to loaded tracks
                    track.Clips.CollectionChanged += (s, e) =>
                    {
                        if (e.OldItems != null) foreach (CinematicOperation item in e.OldItems) item.PropertyChanged -= CinematicOperation_PropertyChanged;
                        if (e.NewItems != null) foreach (CinematicOperation item in e.NewItems) item.PropertyChanged += CinematicOperation_PropertyChanged;
                        OnPropertyChanged(nameof(TotalStoryTime));
                    };
                    foreach (var clip in track.Clips) { clip.PropertyChanged += CinematicOperation_PropertyChanged; _ = LoadThumbnailAsync(clip, dispatcher); }
                    Tracks.Add(track);
                }
            }
            else
            {
                // Migrate legacy formats
                if (nodes != null)
                {
                    double accumulated = 0;
                    foreach (var node in nodes)
                    {
                        node.StartTime = TimeSpan.FromSeconds(accumulated);
                        accumulated += node.OpDuration.TotalSeconds + node.TransitionDuration.TotalSeconds;
                        node.PlacementWidth = TrackDefaults[0].w;
                        node.PlacementHeight = TrackDefaults[0].h;
                        node.PlacementCenterX = TrackDefaults[0].x;
                        node.PlacementCenterY = TrackDefaults[0].y;
                        Tracks[0].Clips.Add(node);
                        _ = LoadThumbnailAsync(node, dispatcher);
                    }
                }

                if (legacyOverlayTracks != null && legacyOverlayTracks.Count > 0)
                {
                    int trackIdx = 1;
                    foreach (var track in legacyOverlayTracks)
                    {
                        if (trackIdx >= MaxTracks) break;
                        foreach (var clip in track.Clips)
                        {
                            Tracks[trackIdx].Clips.Add(clip);
                            _ = LoadThumbnailAsync(clip, dispatcher);
                        }
                        trackIdx++;
                    }
                }
                else if (legacyOverlays != null && legacyOverlays.Count > 0)
                {
                    foreach (var clip in legacyOverlays)
                    {
                        Tracks[1].Clips.Add(clip);
                        _ = LoadThumbnailAsync(clip, dispatcher);
                    }
                }
            }
            
            EnsureTracks(); // top up so the timeline always shows the full set
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
            Tracks.Clear();
            EnsureTracks();
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
            // In-memory round trip of already-live objects, so the marks are whatever convention
            // they are already in. RestoreSnapshot deliberately does NOT re-flag them as legacy —
            // undo must not re-run a migration that has already happened.
            var data = new ProjectData { SchemaVersion = CurrentSchemaVersion, Tracks = Tracks };
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

            SelectedClip = null;

            Tracks.Clear();
            EnsureTracks();

            if (data.Tracks != null)
            {
                Tracks.Clear();
                foreach (var track in data.Tracks)
                {
                    // Add listeners to loaded tracks
                    track.Clips.CollectionChanged += (s, e) =>
                    {
                        if (e.OldItems != null) foreach (CinematicOperation item in e.OldItems) item.PropertyChanged -= CinematicOperation_PropertyChanged;
                        if (e.NewItems != null) foreach (CinematicOperation item in e.NewItems) item.PropertyChanged += CinematicOperation_PropertyChanged;
                        OnPropertyChanged(nameof(TotalStoryTime));
                    };
                    foreach (var clip in track.Clips) { clip.PropertyChanged += CinematicOperation_PropertyChanged; _ = LoadThumbnailAsync(clip, dispatcher); }
                    Tracks.Add(track);
                }
            }
            // Legacy restore paths handled inside LoadAsync. Snapshot restoration only 
            // needs to deal with the current format since snapshots are created in-session.
            EnsureTracks();
            OnPropertyChanged(nameof(CanAddOverlayTrack));
            OnPropertyChanged(nameof(TotalStoryTime));
        }
    }
}
