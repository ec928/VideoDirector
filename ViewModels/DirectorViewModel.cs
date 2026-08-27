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

    // How the canvas - the composition space everything is measured against - gets its size.
    //
    // Auto and Custom are implemented (presets write Custom). MatchWorkingArea and FollowWindow
    // are unused integer slots kept so older project files still deserialise.
    public enum CanvasSizeMode
    {
        Auto,               // the app window as it was when the project began, then fixed
        MatchWorkingArea,   // unused; kept for serialised values
        FollowWindow,       // unused; kept for serialised values
        Custom              // chosen explicitly (including the HD/UHD/scope/vertical presets)
    }

    public class DirectorViewModel : ObservableObject
    {
        // ---- Canvas ----------------------------------------------------------------------
        // Size is the COMPOSITION's, not the pane's. The pane only decides how big it looks, so
        // hiding the dock or going fullscreen rescales the view and never the arrangement.
        private CanvasSizeMode _canvasSizeMode = CanvasSizeMode.Auto;
        public CanvasSizeMode CanvasSizeMode
        {
            get => _canvasSizeMode;
            set => SetProperty(ref _canvasSizeMode, value);
        }

        private double _canvasWidth;
        private double _canvasHeight;

        public double CanvasWidth  { get => _canvasWidth;  set => SetProperty(ref _canvasWidth, value); }
        public double CanvasHeight { get => _canvasHeight; set => SetProperty(ref _canvasHeight, value); }

        public bool HasCanvasSize => _canvasWidth > 0 && _canvasHeight > 0;

        /// <summary>
        /// Clips whose source file is no longer where the project says it is.
        /// </summary>
        /// <remarks>
        /// A project is a list of paths into a media library, so the failure that actually bites is
        /// not a bad render - it is a clip that will not load because something moved. Hits the file
        /// system once per clip, so it is called at deliberate moments (opening, and arming a
        /// performance) rather than from anything that runs per frame or per timeline rebuild.
        /// </remarks>
        public List<CinematicOperation> MissingSources()
        {
            var missing = new List<CinematicOperation>();
            foreach (var track in Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (clip == null || string.IsNullOrWhiteSpace(clip.FilePath)) continue;
                    try { if (!File.Exists(clip.FilePath)) missing.Add(clip); }
                    catch { missing.Add(clip); }   // unreadable counts as missing
                }
            }
            return missing;
        }

        // Which project is open. Nothing on screen said so, and with several similar test projects
        // there was no way to tell which one had been loaded.
        // Which display a performance takes over. -1 means "wherever the window already is", which
        // is the right default for a single-screen machine and for anyone who has not chosen.

        private string _projectName = "Untitled";
        public string ProjectName
        {
            get => _projectName;
            private set => SetProperty(ref _projectName, string.IsNullOrWhiteSpace(value) ? "Untitled" : value);
        }

        // Which display a performance takes over. -1 means "wherever the window already is", the
        // right default on a single-screen machine and for anyone who has not chosen.
        private int _presentDisplayIndex = -1;
        public int PresentDisplayIndex
        {
            get => _presentDisplayIndex;
            set => SetProperty(ref _presentDisplayIndex, value);
        }

        /// <summary>Nothing on any track. Such a project has not "begun" yet, which is what lets
        /// Auto keep following the window until there is something to protect.</summary>
        public bool IsEmptyProject
        {
            get
            {
                foreach (var t in Tracks) if (t.Clips.Count > 0) return false;
                return true;
            }
        }

        /// <summary>
        /// Give the canvas a size from the window, once. Auto means "as the project began", so an
        /// existing size is never overwritten - that is what stops the frame moving under you when
        /// the window changes.
        /// </summary>
        public void InitialiseCanvasIfUnset(double windowW, double windowH)
        {
            if (HasCanvasSize || windowW <= 0 || windowH <= 0) return;
            CanvasWidth = windowW;
            CanvasHeight = windowH;
        }

        // Hard ceiling on track slots. DirectorPlayerControl builds exactly this many render
        // surfaces in a loop from this same constant, so the two cannot drift apart and there
        // is no way to address a slot with no visual behind it.
        public const int MaxTracks = 6;

        // What a NEW project starts with. Slots 4-6 exist but are not shown until asked for.
        public const int DefaultTracks = 3;

        // The floor. Removing the last track would leave nowhere to put a clip.
        public const int MinTracks = 1;
        public ObservableCollection<TimelineTrack> Tracks { get; } = new();

        // Default placement per track. Y increases downward (top = cyPx - boxH/2), so 0.28 is
        // the UPPER half. T1 is full screen and acts as the spine by convention - nothing in the
        // model privileges it. T2-T5 take the four corners; T6 sits in the middle, on top of
        // everything, since Z-order is track order.
        //
        // T2-T4 are deliberately left exactly as they were. A track default is what Reset
        // restores a clip to, so nudging these would silently change what Reset does to every
        // project already saved.
        private static readonly (double x, double y, double w, double h)[] TrackDefaults =
            {
                (0.50, 0.50, 1.0, 1.0), // T1 full screen, centre
                (0.72, 0.72, 0.3, 0.3), // T2 bottom-right
                (0.28, 0.72, 0.3, 0.3), // T3 bottom-left
                (0.72, 0.28, 0.3, 0.3), // T4 top-right
                (0.28, 0.28, 0.3, 0.3), // T5 top-left
                // T6 overlaps each corner PiP by about 8% of the pane. That is arithmetic, not a
                // bug: corners span 0.13-0.43 and 0.57-0.87, centre spans 0.35-0.65, and full
                // clearance would need T6 below 0.14 wide - too small to be useful. It is the top
                // layer and is meant to sit over things.
                (0.50, 0.50, 0.3, 0.3)  // T6 centre
            };

        public bool CanAddTrack => Tracks.Count < MaxTracks;
        public bool CanRemoveTrack => Tracks.Count > MinTracks;

        public TimelineTrack AddTrack(int index)
        {
            if (index >= MaxTracks) return null;
            var track = new TimelineTrack
            {
                Name = "Track " + (index + 1),
                DefaultCenterX = TrackDefaults[index].x,
                DefaultCenterY = TrackDefaults[index].y,
                DefaultWidth = TrackDefaults[index].w,
                DefaultHeight = TrackDefaults[index].h,
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
                // Playback is a presentation, so the inspector steps aside - EXCEPT in Edit, where
                // playing is how you watch the Ken Burns move you are building. Closing the panel
                // there meant reopening it after every preview, which is not a presentation at all.
                if (value && !IsEditMode) IsInspectorOpen = false;

                if (SetProperty(ref _isPlaying, value))
                {
                    OnPropertyChanged(nameof(IsEditorChromeVisible));
                    OnPropertyChanged(nameof(IsPerforming));
                    OnPropertyChanged(nameof(ModeLabel));
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    OnPropertyChanged(nameof(CanToggleEditMode));
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
                    OnPropertyChanged(nameof(CanToggleEditMode));
                    OnPropertyChanged(nameof(IsTrackDockVisible));
                    OnPropertyChanged(nameof(IsChromeVisible));
                    OnPropertyChanged(nameof(IsEditorChromeVisible));
                    OnPropertyChanged(nameof(IsPerforming));
                    OnPropertyChanged(nameof(IsTrackDockReopenVisible));
                    // Only on the way OUT. Forcing it on the way in undid the hide that entering a
                    // performance had just applied - the chrome vanished for a frame and came
                    // straight back, because this line runs after the handler that hid it.
                    if (!value) IsControlsVisible = true;
                }
            }
        }

        // The dock follows the pill EXCEPT in cinematic mode, where it stays down for good - and
        // except when you have closed it yourself, which is what IsTrackDockOpen is for. Auto-hide
        // during playback is a convenience; this is a decision, and a decision outranks it.
        public bool IsTrackDockVisible => ChromeRules.IsTrackDockVisible(_isCinematicMode, _isPlaying, _isControlsVisible, _isTrackDockOpen, _isRecording);

        // Whether the editing chrome is up at all. The panel TABS follow this rather than
        // IsTrackDockVisible: a tab that disappeared when its own panel closed would be a panel
        // you could shut and never reopen.
        // Cinematic no longer special-cases the chrome away. Entering it collapses the timeline as
        // though the hide button had been pressed, and the inactivity timer then takes the rest -
        // the same path playback already uses. One rule, one behaviour, and the transport stays
        // where it lives instead of being rebuilt as a floating object for one mode.
        public bool IsChromeVisible => ChromeRules.IsChromeVisible(_isControlsVisible, _isRecording);

        // EDITOR chrome - undo, project, export, the panel toggles - as distinct from the transport.
        // During a performance the transport is all that should be on screen: the rest is editing
        // furniture and has no business in front of an audience.
        // THE ONLY THING CINEMATIC CHANGES IS A PERFORMANCE.
        //
        // Arming it must do nothing at all - not full screen, not the chrome, not the view lock, not
        // the inspector. Everything that used to test _isCinematicMode on its own tests this instead,
        // so a rule cannot be added later that forgets the playing half.
        public bool IsPerforming => ChromeRules.IsPerforming(_isCinematicMode, _isPlaying);

        private bool _isRecording;

        /// <summary>
        /// A take is rolling. Locks the chrome away until it ends - see ChromeRules.
        /// </summary>
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(IsChromeVisible));
                    OnPropertyChanged(nameof(IsEditorChromeVisible));
                    OnPropertyChanged(nameof(IsTrackDockVisible));
                    OnPropertyChanged(nameof(IsTrackDockReopenVisible));
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    OnPropertyChanged(nameof(CanToggleEditMode));
                }
            }
        }

        public bool IsEditorChromeVisible => ChromeRules.IsEditorChromeVisible(_isCinematicMode, _isPlaying, _isRecording);

        // The REOPEN affordance, and only that. While the dock is open its collapse control lives
        // inside the dock toolbar, where it cannot collide with the transport sitting in the middle
        // of the same row; the floating tab exists purely so a closed dock is not unreachable.
        public bool IsTrackDockReopenVisible => ChromeRules.IsTrackDockReopenVisible(_isControlsVisible, _isTrackDockOpen, _isRecording);

        private bool _isTrackDockOpen = true;
        public bool IsTrackDockOpen
        {
            get => _isTrackDockOpen;
            set
            {
                if (SetProperty(ref _isTrackDockOpen, value))
                {
                    OnPropertyChanged(nameof(IsTrackDockVisible));
                    OnPropertyChanged(nameof(IsTrackDockReopenVisible));
                }
            }
        }

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
                {
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    OnPropertyChanged(nameof(CanToggleEditMode));
                }
            }
        }

        // Hidden while playing - EXCEPT in Edit, where playing is previewing the very move the panel
        // is used to set. IsInspectorOpen was already exempted for Edit, but this expression hid the
        // panel anyway, so it vanished for the length of every preview and came back afterwards.
        // Leaving Edit always works; entering needs a clip. Playback is not a mode you switch out of
        // with this control.
        public bool CanToggleEditMode => ChromeRules.CanToggleEditMode(_isPlaying, _isEditMode, _selectedClip != null, _isRecording);

        // No selection means nothing is being denied to anyone, so the sections behave as they
        // always did; only a sound-only clip removes them.
        private bool SelectedHasPicture => _selectedClip == null || _selectedClip.HasPicture;

        public bool IsMotionSectionVisible => ChromeRules.IsMotionSectionVisible(_isEditMode, SelectedHasPicture);
        public bool IsBordersSectionVisible => ChromeRules.IsBordersSectionVisible(_isEditMode, SelectedHasPicture);
        public bool IsTransitionsSectionVisible => ChromeRules.IsTransitionsSectionVisible(_isEditMode, SelectedHasPicture);
        public bool IsOpacityRowVisible => ChromeRules.IsOpacityRowVisible(SelectedHasPicture);

        private void RaiseInspectorSections()
        {
            OnPropertyChanged(nameof(IsMotionSectionVisible));
            OnPropertyChanged(nameof(IsBordersSectionVisible));
            OnPropertyChanged(nameof(IsTransitionsSectionVisible));
            OnPropertyChanged(nameof(IsOpacityRowVisible));
        }

        public bool IsStoryboardVisible =>
            ChromeRules.IsInspectorVisible(_isCinematicMode, _isPlaying, _isEditMode, _isInspectorOpen, HasSelection, _isRecording);

        private bool _isControlsVisible = true;
        public bool IsControlsVisible
        {
            get => _isControlsVisible;
            set
            {
                if (SetProperty(ref _isControlsVisible, value))
                {
                    OnPropertyChanged(nameof(IsTrackDockVisible));
                    OnPropertyChanged(nameof(IsChromeVisible));
                    OnPropertyChanged(nameof(IsEditorChromeVisible));
                    OnPropertyChanged(nameof(IsPerforming));
                    OnPropertyChanged(nameof(IsTrackDockReopenVisible));
                }
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

        // 2x was simply missing - the list only ever went down from 1. Fastest first, so the order
        // reads the way the numbers do.
        public List<double> AvailableSpeeds { get; } = new List<double> { 2.0, 1.0, 0.5, 0.25, 0.0 };

        public TimeSpan TotalStoryTime
        {
            get
            {
                TimeSpan max = TimeSpan.Zero;
                foreach (var track in Tracks)
                {
                    foreach (var node in track.Clips)
                    {
                        var end = ClipRules.StoryEnd(node.StartTime, node.OpDuration);
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

        /// <summary>A set of clips treated as one block: a slice of time across every track.</summary>
        /// <remarks>
        /// The span runs from the earliest member start to the latest member end, so it reads as a
        /// column rather than an outline around scattered clips - which is what it is, since clips
        /// at the same moment on different tracks play together as one picture.
        ///
        /// Membership is by SET, not by span. A clip that happens to sit inside the span is not a
        /// member: the existing rules apply to it unchanged, so it is pushed clear the moment the
        /// group forms, exactly as it would be if the block had been dragged there.
        /// </remarks>
        public sealed class ClipGroup
        {
            public List<CinematicOperation> Clips { get; } = new();

            public TimeSpan Start
            {
                get
                {
                    var min = TimeSpan.MaxValue;
                    foreach (var c in Clips) if (c.StartTime < min) min = c.StartTime;
                    return min == TimeSpan.MaxValue ? TimeSpan.Zero : min;
                }
            }

            public TimeSpan End
            {
                get
                {
                    var max = TimeSpan.Zero;
                    foreach (var c in Clips)
                    {
                        var e = c.StartTime + c.OpDuration;
                        if (e > max) max = e;
                    }
                    return max;
                }
            }
        }

        public ObservableCollection<ClipGroup> Groups { get; } = new();

        public ClipGroup GroupOf(CinematicOperation clip)
        {
            if (clip == null) return null;
            foreach (var g in Groups) if (g.Clips.Contains(clip)) return g;
            return null;
        }

        /// <summary>Which group a clip belongs to, or -1. The index picks its colour.</summary>
        public int GroupIndexOf(CinematicOperation clip)
        {
            if (clip == null) return -1;
            for (int i = 0; i < Groups.Count; i++)
                if (Groups[i].Clips.Contains(clip)) return i;
            return -1;
        }

        public bool CanGroupSelection => MultiSelectedCount > 1;

        /// <summary>Turn the current selection into one block, pushing strangers clear of its span.</summary>
        public ClipGroup GroupSelection()
        {
            if (!CanGroupSelection) return null;

            var g = new ClipGroup();
            foreach (var c in SelectedClips)
            {
                // A clip belongs to one block at a time; regrouping moves it.
                GroupOf(c)?.Clips.Remove(c);
                g.Clips.Add(c);
            }
            Groups.Add(g);

            // The block is now declared, so its whole slice belongs to it: anything inside that
            // span is pushed clear, on every track, including tracks where the block has no clip.
            SettleAround(g);

            for (int i = Groups.Count - 1; i >= 0; i--)
                if (Groups[i].Clips.Count < 2) Groups.RemoveAt(i);

            OnPropertyChanged(nameof(CanGroupSelection));
            RecordIfChanged();
            return g;
        }

        /// <summary>Clear a group's whole time slice, across every track.</summary>
        /// <remarks>
        /// A GROUP IS A COLUMN, NOT A SET OF CELLS. It runs from the earliest member start to the
        /// latest member end and occupies that span on ALL SIX TRACKS, whether or not it has a clip
        /// on each. So anything inside the span is pushed clear of it - including on a track where
        /// the group has no member at all. Resolving track by track against member clips misses
        /// exactly those, which is the difference between a time slice and a handful of clips.
        ///
        /// ANYTHING PUSHED THAT IS ITSELF A GROUP MOVES WHOLE. Shifting one member of another block
        /// would shred it, and a block that survives being moved but not being pushed is not a block.
        ///
        /// The loop repeats because clearing one span can land something on the next; it is bounded
        /// because every pass moves at least one unit strictly later and nothing is ever moved back.
        /// </remarks>
        public void PushClearOfSpan(ClipGroup g)
        {
            if (g == null || g.Clips.Count == 0) return;

            for (int pass = 0; pass < 32; pass++)
            {
                var gs = g.Start;
                var ge = g.End;
                if (ge <= gs) return;

                CinematicOperation offender = null;
                foreach (var trk in Tracks)
                {
                    foreach (var c in trk.Clips)
                    {
                        if (g.Clips.Contains(c)) continue;
                        if (c.StartTime < ge && gs < c.StartTime + c.OpDuration) { offender = c; break; }
                    }
                    if (offender != null) break;
                }
                if (offender == null) return;

                // Move the offender - or the whole block it belongs to - just past the slice.
                var other = GroupOf(offender);
                if (other != null && other != g)
                {
                    var delta = ge - other.Start;
                    if (delta <= TimeSpan.Zero) delta = ge - offender.StartTime;
                    foreach (var c in other.Clips) c.StartTime += delta;
                }
                else
                {
                    offender.StartTime = ge;
                }
            }
        }

        private static TimeSpan SpanStart(ICollection<CinematicOperation> set)
        {
            var min = TimeSpan.MaxValue;
            foreach (var c in set) if (c.StartTime < min) min = c.StartTime;
            return min == TimeSpan.MaxValue ? TimeSpan.Zero : min;
        }

        private static TimeSpan SpanEnd(ICollection<CinematicOperation> set)
        {
            var max = TimeSpan.Zero;
            foreach (var c in set)
            {
                var e = c.StartTime + c.OpDuration;
                if (e > max) max = e;
            }
            return max;
        }

        /// <summary>One unit that can be in the way: a whole block, or a single loose clip.</summary>
        private sealed class Incumbent
        {
            public List<CinematicOperation> Clips = new();
            public TimeSpan Start;
            public TimeSpan End;
        }

        /// <summary>Everything the incoming drop could collide with, as whole units.</summary>
        /// <remarks>
        /// A GROUP IS ALWAYS SPAN-WIDE. Its slice is reserved on every track, so it is in the way of
        /// anything landing in that time - including a lone clip on a track the group has no member
        /// on. Missing that is what let a single clip settle inside a block.
        ///
        /// A LOOSE CLIP ONLY OWNS ITS OWN ROW. Overlapping it in time on a different track is a
        /// composite, not a collision - so it counts only when the drop shares its track, or when the
        /// drop is itself a slice and claims every track.
        /// </remarks>
        private List<Incumbent> IncumbentsFor(ICollection<CinematicOperation> incoming, bool claimsWholeSlice)
        {
            var list = new List<Incumbent>();

            foreach (var g in Groups)
            {
                bool isIncoming = false;
                foreach (var c in g.Clips) if (incoming.Contains(c)) { isIncoming = true; break; }
                if (isIncoming || g.Clips.Count == 0) continue;
                list.Add(new Incumbent { Clips = new List<CinematicOperation>(g.Clips), Start = g.Start, End = g.End });
            }

            var grouped = AllGroupedClips();
            foreach (var trk in Tracks)
            {
                bool sharesTrack = claimsWholeSlice;
                if (!sharesTrack)
                    foreach (var c in incoming) if (trk.Clips.Contains(c)) { sharesTrack = true; break; }
                if (!sharesTrack) continue;

                foreach (var c in trk.Clips)
                {
                    if (incoming.Contains(c) || grouped.Contains(c)) continue;
                    list.Add(new Incumbent
                    {
                        Clips = new List<CinematicOperation> { c },
                        Start = c.StartTime,
                        End = c.StartTime + c.OpDuration
                    });
                }
            }
            return list;
        }

        /// <summary>Settle a drop: where it landed on something decides which of them moves.</summary>
        /// <remarks>
        /// WHERE YOU DROPPED SAYS WHAT YOU MEANT. Land on the FRONT half of something and you are
        /// claiming its place, so it gives way. Land on the BACK half and you were aiming past it, so
        /// you go after it - which is how one thing is butted against the end of another.
        ///
        /// Both sides are treated as UNITS. A block is judged by its whole span and moves whole,
        /// whether it is the thing dropped or the thing pushed; a lone clip stands for itself.
        /// </remarks>
        public void SettleDrop(ICollection<CinematicOperation> incoming, bool claimsWholeSlice)
        {
            if (incoming == null || incoming.Count == 0) return;

            var inStart = SpanStart(incoming);
            var inEnd = SpanEnd(incoming);
            if (inEnd <= inStart) return;

            var incumbents = IncumbentsFor(incoming, claimsWholeSlice);

            // PASS A - anything whose BACK half you landed on was aimed past, so the drop goes after
            // it. The furthest such end wins, so landing across two lands after both.
            var landAt = inStart;
            foreach (var inc in incumbents)
            {
                if (inStart >= inc.End || inEnd <= inc.Start) continue;
                var half = inc.Start + TimeSpan.FromTicks((inc.End - inc.Start).Ticks / 2);
                if (inStart >= half && inc.End > landAt) landAt = inc.End;
            }

            if (landAt > inStart)
            {
                var shift = landAt - inStart;
                foreach (var c in incoming) c.StartTime += shift;
                inStart = SpanStart(incoming);
                inEnd = SpanEnd(incoming);
            }

            // PASS B - whatever the drop still covers had its place claimed, so it gives way. EVERY
            // one of them, not just the first: a drop can land across several, and settling only
            // against the first is how two clips were left overlapping on one row.
            for (int guard = 0; guard < 64; guard++)
            {
                Incumbent hit = null;
                foreach (var inc in incumbents)
                {
                    var s0 = SpanStart(inc.Clips);
                    var e0 = SpanEnd(inc.Clips);
                    if (s0 < inEnd && inStart < e0) { hit = inc; break; }
                }
                if (hit == null) break;

                var delta = inEnd - SpanStart(hit.Clips);
                if (delta <= TimeSpan.Zero) break;
                foreach (var c in hit.Clips) c.StartTime += delta;
            }

            // Tidy what is left. Blocks and the thing just dropped hold their shape; loose clips
            // give way, so no track is left carrying two clips at once.
            var anchored = AllGroupedClips();
            foreach (var c in incoming) anchored.Add(c);
            foreach (var trk in Tracks) trk.ResolveOverlapsAnchoring(anchored);
        }

        /// <summary>Every clip that belongs to any group. Blocks hold their shape; loose clips give way.</summary>
        private HashSet<CinematicOperation> AllGroupedClips()
        {
            var set = new HashSet<CinematicOperation>();
            foreach (var grp in Groups) foreach (var c in grp.Clips) set.Add(c);
            return set;
        }

        /// <summary>Clear the slice, then tidy what is left so no track carries two clips at once.</summary>
        public void SettleAround(ClipGroup g)
        {
            PushClearOfSpan(g);

            var anchored = AllGroupedClips();
            foreach (var trk in Tracks) trk.ResolveOverlapsAnchoring(anchored);
        }

        public void Ungroup(ClipGroup g)
        {
            if (g == null) return;
            Groups.Remove(g);
            OnPropertyChanged(nameof(CanGroupSelection));
            RecordIfChanged();
        }

        /// <summary>Clips picked out with shift-click, on top of the primary selection.</summary>
        /// <remarks>
        /// SelectedClip is left alone deliberately. It stays "the one you last clicked", so the
        /// inspector, Edit mode and ChromeRules carry on reading it and need no changes - only the
        /// timeline knows about this set. Shift-click adds, ctrl-click removes, a plain click clears
        /// it and starts again.
        /// </remarks>
        private readonly HashSet<CinematicOperation> _multiSelected = new();

        public bool IsSelected(CinematicOperation clip)
            => clip != null && (ReferenceEquals(clip, SelectedClip) || _multiSelected.Contains(clip));

        public int MultiSelectedCount => _multiSelected.Count;

        public IEnumerable<CinematicOperation> SelectedClips
        {
            get
            {
                if (SelectedClip != null) yield return SelectedClip;
                foreach (var c in _multiSelected)
                    if (!ReferenceEquals(c, SelectedClip)) yield return c;
            }
        }

        public void AddToSelection(CinematicOperation clip)
        {
            if (clip == null) return;
            // The primary comes along, so shift-clicking a second clip leaves both marked rather
            // than silently dropping the first.
            if (SelectedClip != null) _multiSelected.Add(SelectedClip);
            _multiSelected.Add(clip);
            OnPropertyChanged(nameof(MultiSelectedCount));
        }

        public void RemoveFromSelection(CinematicOperation clip)
        {
            if (clip == null) return;
            if (SelectedClip != null) _multiSelected.Add(SelectedClip);
            _multiSelected.Remove(clip);
            if (ReferenceEquals(clip, SelectedClip)) SelectedClip = null;
            OnPropertyChanged(nameof(MultiSelectedCount));
        }

        public void ClearMultiSelection()
        {
            if (_multiSelected.Count == 0) return;
            _multiSelected.Clear();
            OnPropertyChanged(nameof(MultiSelectedCount));
        }

        private CinematicOperation _selectedClip;
        public CinematicOperation SelectedClip
        {
            get => _selectedClip;
            set
            {
                if (SetProperty(ref _selectedClip, value))
                {
                    // Selecting a clip opens the inspector; dropping the selection closes it.
                    // Entering Edit selects the clip too, so that path is covered by the same line.
                    // Closing it by hand still works - the next selection change is what reopens it,
                    // which is the behaviour you get from every panel that follows a selection.
                    IsInspectorOpen = value != null;

                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(IsStoryboardVisible));
                    OnPropertyChanged(nameof(CanToggleEditMode));
                    OnPropertyChanged(nameof(IsTrack1Selected));
                    OnPropertyChanged(nameof(IsOverlaySelected));
                    OnPropertyChanged(nameof(SelectedTrackLabel));
                    OnPropertyChanged(nameof(ModeLabel));
                    RaiseInspectorSections();   // a sound-only clip shows fewer of them
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
                            return "Track " + (i + 1);
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
                    RaiseInspectorSections();   // which sections apply depends on the mode too
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
            EnsureTracks(DefaultTracks); // a new project opens with T1-T3
            ResetHistory(); // baseline = the empty project, so the first edit is undoable
        }

        // Bring the list up to the given floor. Called with MinTracks after a load or an undo, so
        // a project saved with two tracks opens with two; called with DefaultTracks for a new one.
        //
        // This used to pad unconditionally to MaxTracks, which is precisely why removing a track
        // was not expressible: the next load or undo silently put it back.
        public void EnsureTracks(int minimum = MinTracks)
        {
            int target = Math.Clamp(minimum, MinTracks, MaxTracks);
            while (Tracks.Count < target) AddTrack(Tracks.Count);
        }

        /// <summary>Add one track on top. Returns null at the ceiling.</summary>
        public TimelineTrack AddTopTrack()
        {
            if (!CanAddTrack) return null;
            var track = AddTrack(Tracks.Count);
            RaiseTrackCountChanged();
            RecordIfChanged();
            return track;
        }

        /// <summary>
        /// Remove the highest track. Top-only by design: a track index IS its identity - clips
        /// address their track by position and Z-order is index order - so removing from the
        /// middle would renumber every track above it and move those clips between layers.
        /// </summary>
        public bool RemoveTopTrack()
        {
            if (!CanRemoveTrack) return false;
            var track = Tracks[Tracks.Count - 1];

            // The inspector binds to SelectedClip. Leaving it pointed at a clip on a track that no
            // longer exists shows an editor for something the user can no longer see or reach.
            if (SelectedClip != null && track.Clips.Contains(SelectedClip)) SelectedClip = null;

            foreach (var clip in track.Clips) clip.PropertyChanged -= CinematicOperation_PropertyChanged;
            Tracks.RemoveAt(Tracks.Count - 1);

            RaiseTrackCountChanged();
            OnPropertyChanged(nameof(TotalStoryTime));
            RecordIfChanged();
            return true;
        }

        /// <summary>Clips on the top track - what the removal prompt needs in order to warn.</summary>
        public int TopTrackClipCount => Tracks.Count > 0 ? Tracks[Tracks.Count - 1].Clips.Count : 0;

        /// <summary>
        /// Drop empty tracks from the TOP of the stack, down to MinTracks. Trailing-only is
        /// deliberate: a project with clips on T1 and T4 keeps all four, because dropping the
        /// empty T2/T3 would move T4 down two layers and change the composition.
        /// </summary>
        private void TrimTrailingEmptyTracks()
        {
            while (Tracks.Count > MinTracks && Tracks[Tracks.Count - 1].Clips.Count == 0)
                Tracks.RemoveAt(Tracks.Count - 1);
        }

        private void RaiseTrackCountChanged()
        {
            OnPropertyChanged(nameof(CanAddTrack));
            OnPropertyChanged(nameof(CanRemoveTrack));
        }

        public event EventHandler ClipPropertyChanged;

        // Guards the ripple below against its own writes: ResolveOverlaps assigns StartTime, which
        // comes straight back through here.
        private bool _rippling;

        private void CinematicOperation_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            bool lengthChanged = e.PropertyName == nameof(CinematicOperation.OpDuration)
                              || e.PropertyName == nameof(CinematicOperation.TransitionDuration);

            if (lengthChanged || e.PropertyName == nameof(CinematicOperation.StartTime))
            {
                OnPropertyChanged(nameof(TotalStoryTime));
            }

            // RIPPLE. A gapless track is defined by having no gaps, so when a clip changes length
            // the ones after it have to move - that is what "ripple" means, and it is the only
            // trim rule this app needs (roll, dragging a shared boundary between two clips, is a
            // separate gesture that does not exist here).
            //
            // It was already happening after a drag and after add/remove, but not after a TRIM,
            // which changes OpDuration just as surely: OpDuration == (VideoEnd - VideoStart) /
            // Speed. So shortening a clip on a gapless track opened a hole in a track that is not
            // supposed to have any, and it stayed there until some later edit happened to re-lay
            // the track. Doing it here rather than in the trim handler catches every route to a
            // length change at once - the scrubber, a duration typed into the inspector, a speed
            // change, adding a fade.
            if (lengthChanged && !_rippling && sender is CinematicOperation clip)
            {
                _rippling = true;
                try
                {
                    foreach (var track in Tracks)
                    {
                        if (track.IsGapless && track.Clips.Contains(clip)) { track.ResolveOverlaps(); break; }
                    }
                }
                finally { _rippling = false; }
            }

            ClipPropertyChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Does this file actually carry an audio stream.</summary>
        /// <remarks>
        /// Asked once per clip and remembered on the clip, because the answer is a property of the
        /// file. MediaClip is used rather than the shell properties because the shell reports a
        /// bitrate of 0 for the mkv sources here and says nothing about tracks at all, whereas
        /// GetAudioEncodingProperties is a direct answer from the decoder.
        ///
        /// Unknown counts as YES. Being wrong that way leaves a control enabled that does nothing;
        /// being wrong the other way silently mutes a clip that had sound, which is much worse.
        /// </remarks>
        /// <summary>What streams a file actually carries. One open, both answers.</summary>
        /// <remarks>
        /// Unknown counts as "has both". Being wrong that way leaves a control enabled that does
        /// nothing; being wrong the other way silently mutes a clip or hides a picture, which is
        /// much worse.
        /// </remarks>
        public static async Task<(bool audio, bool video)> ProbeStreamsAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return (true, true);

            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                await source.OpenAsync();
                var item = new Windows.Media.Playback.MediaPlaybackItem(source);
                var result = (item.AudioTracks.Count > 0, item.VideoTracks.Count > 0);
                source.Dispose();
                return result;
            }
            catch
            {
                return (true, true);
            }
        }

        /// <summary>
        /// Fill in SourceHasAudio for every clip that has not been asked yet.
        /// </summary>
        /// <remarks>
        /// Runs after a load. Projects saved before this existed carry the default (true), so
        /// without this pass an old project would keep showing a live volume control on a silent
        /// clip - which is the bug being fixed. Deliberately not awaited by the loader: it touches
        /// the decoder once per clip and the timeline should not wait on it.
        /// </remarks>
        public async Task RefreshAudioCapabilityAsync()
        {
            foreach (var track in Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (clip == null || string.IsNullOrWhiteSpace(clip.FilePath)) continue;
                    if (clip.IsImage) { clip.SourceHasAudio = false; clip.SourceHasVideo = true; continue; }

                    var streams = await ProbeStreamsAsync(clip.FilePath);
                    clip.SourceHasAudio = streams.audio;
                    clip.SourceHasVideo = streams.video;
                }
            }
        }

        public async Task AddFilesAsync(IEnumerable<string> filePaths)
        {
            foreach (var path in filePaths)
            {
                TimeSpan duration = TimeSpan.FromSeconds(10);
                double sourceAspect = 0;
                bool hasAudio = true, hasVideo = true;
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

                    // Whether there is any sound to offer. An image never has any; anything else
                    // gets asked, so a silent video shows a disabled volume rather than a live one.
                    if (CinematicOperation.IsImageFile(path)) { hasAudio = false; hasVideo = true; }
                    else { var st = await ProbeStreamsAsync(path); hasAudio = st.audio; hasVideo = st.video; }

                    // Get Thumbnail
                    var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 480, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                    if (thumb != null)
                    {
                        thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await thumbnail.SetSourceAsync(thumb);
                    }
                }
                catch { }

                // Dropping a clip does NOT touch the one before it.
                //
                // This used to give the previous clip a one-second Crossfade whenever Track 1 was
                // gapless and it had no transition yet. Both halves were wrong. Crossfade is not
                // implemented - it needs two clips on screen at once, which the compositor cannot
                // express - so it rendered as nothing. And since transitions became ADDITIVE, the
                // assignment silently grew the previous clip by a second on every drop, which reads
                // as the timeline shifting under you for no reason. A transition is a choice you
                // make in the inspector, not something a drop makes on your behalf.
                var mainTrack = Tracks[0];
                double newStartTime = 0;
                
                if (mainTrack.Clips.Count > 0)
                {
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
                    SourceHasAudio = hasAudio,   // a silent source gets a disabled volume, not a live one
                    SourceHasVideo = hasVideo,   // sound-only clips draw nothing and say so
                    StartTime = TimeSpan.FromSeconds(newStartTime),
                    TransitionDuration = TimeSpan.Zero, // Default 0s transition for the new last clip
                    Thumbnail = thumbnail,
                    PlacementCenterX = mainTrack.DefaultCenterX,
                    PlacementCenterY = mainTrack.DefaultCenterY,
                    DefaultPlacementCenterX = mainTrack.DefaultCenterX,
                    DefaultPlacementCenterY = mainTrack.DefaultCenterY,
                    DefaultPlacementWidth = TrackDefaults[0].w,
                    DefaultPlacementHeight = TrackDefaults[0].h,
                    PlacementWidth = TrackDefaults[0].w,
                    PlacementHeight = TrackDefaults[0].h
                });
                ClipAdded?.Invoke(this, mainTrack.Clips[^1]);
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

            // Same question as the Track 1 path: is there any sound to offer at all.
            bool hasAudio = true, hasVideo = true;
            if (CinematicOperation.IsImageFile(filePath)) { hasAudio = false; }
            else { var st = await ProbeStreamsAsync(filePath); hasAudio = st.audio; hasVideo = st.video; }

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
                SourceHasAudio = hasAudio,
                SourceHasVideo = hasVideo,
                Volume = (trackIndex == 0 && hasAudio) ? 1.0 : 0.0,
                PlacementCenterX = track.DefaultCenterX,
                PlacementCenterY = track.DefaultCenterY,
                DefaultPlacementCenterX = track.DefaultCenterX,
                DefaultPlacementCenterY = track.DefaultCenterY,
                DefaultPlacementWidth = TrackDefaults[trackIndex].w,
                DefaultPlacementHeight = TrackDefaults[trackIndex].h,
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
            // GROUPS SURVIVE UNDO BY POSITION. RestoreSnapshot rebuilds every clip as a new object,
            // so a group holding clip references points at objects that no longer exist anywhere -
            // which is why undo silently dissolved every group. Clips carry no stable id, but the
            // snapshot and the restore see the same ordering, so (track, index) identifies a member
            // unambiguously at the moment it matters.
            public System.Collections.Generic.List<System.Collections.Generic.List<int[]>> Groups { get; set; }

            public int SchemaVersion { get; set; }   // absent in pre-versioned files => 0

            // Canvas. Absent in older files, which read back as 0 and are then initialised from
            // the window on first open - the same path a new project takes.
            public int CanvasSizeMode { get; set; }
            public double CanvasWidth { get; set; }
            public double CanvasHeight { get; set; }
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
            ProjectName = System.IO.Path.GetFileNameWithoutExtension(file?.Name);

            var data = new ProjectData
            {
                SchemaVersion = CurrentSchemaVersion,
                CanvasSizeMode = (int)_canvasSizeMode,
                CanvasWidth = _canvasWidth,
                CanvasHeight = _canvasHeight,
                Tracks = Tracks
            };
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0); // Clear existing content
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, data, options);
        }

        public async Task LoadAsync(Windows.Storage.StorageFile file)
        {
            ProjectName = System.IO.Path.GetFileNameWithoutExtension(file?.Name);

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

                    // Zero for a project saved before the canvas existed. ApplyCanvasSize then
                    // initialises it from the window, the same path a new project takes.
                    CanvasSizeMode = (CanvasSizeMode)data.CanvasSizeMode;
                    CanvasWidth = data.CanvasWidth;
                    CanvasHeight = data.CanvasHeight;
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
                        // These formats predate a variable track count and address tracks by
                        // index, so the slot has to exist before it is written to. EnsureTracks
                        // no longer pads to the ceiling, which is what makes this necessary.
                        EnsureTracks(trackIdx + 1);
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
                    EnsureTracks(2); // legacy loose overlays all land on T2
                    foreach (var clip in legacyOverlays)
                    {
                        Tracks[1].Clips.Add(clip);
                        _ = LoadThumbnailAsync(clip, dispatcher);
                    }
                }
            }
            
            EnsureTracks();            // floor of one, no longer a top-up to the ceiling
            TrimTrailingEmptyTracks(); // unused tracks on top of a saved project do not reopen
            StampPlacementDefaults();  // old projects have no DefaultPlacement*; Reset needs the track default
            RaiseTrackCountChanged();
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
            ProjectName = "Untitled";
            Tracks.Clear();
            // DefaultTracks, not the bare floor: clearing gives you a NEW project, and a new
            // project opens with T1-T3.
            EnsureTracks(DefaultTracks);
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

        // The project as it stood at the last save - or load, or new project, which are equally
        // "nothing to lose" points. Compared against live state rather than kept as a dirty flag,
        // so editing and then undoing back to the saved state correctly counts as unmodified and
        // does not nag on the way out.
        private string _savedSnapshot = string.Empty;
        private string _savedContent = string.Empty;

        // Dirty state compares CONTENT only - the clips - not the canvas.
        //
        // The canvas is part of the saved file, but under Auto it is derived from the window and
        // rewritten whenever the pane resizes. Including it meant a project with nothing on it
        // declared itself modified before you had touched anything, and asked to be saved on close.
        public bool HasUnsavedChanges => CaptureContentSnapshot() != _savedContent;

        public void MarkSaved()
        {
            _savedSnapshot = CaptureSnapshot();
            _savedContent = CaptureContentSnapshot();
        }

        private System.Collections.Generic.List<System.Collections.Generic.List<int[]>> CaptureGroupPositions()
        {
            var outer = new System.Collections.Generic.List<System.Collections.Generic.List<int[]>>();
            foreach (var g in Groups)
            {
                var members = new System.Collections.Generic.List<int[]>();
                foreach (var c in g.Clips)
                    for (int t = 0; t < Tracks.Count; t++)
                    {
                        int i = Tracks[t].Clips.IndexOf(c);
                        if (i >= 0) { members.Add(new[] { t, i }); break; }
                    }
                if (members.Count > 1) outer.Add(members);
            }
            return outer;
        }

        private void RestoreGroupPositions(System.Collections.Generic.List<System.Collections.Generic.List<int[]>> saved)
        {
            Groups.Clear();
            if (saved == null) { OnPropertyChanged(nameof(CanGroupSelection)); return; }

            foreach (var members in saved)
            {
                var g = new ClipGroup();
                foreach (var pos in members)
                {
                    if (pos == null || pos.Length < 2) continue;
                    int t = pos[0], i = pos[1];
                    if (t < 0 || t >= Tracks.Count) continue;
                    if (i < 0 || i >= Tracks[t].Clips.Count) continue;
                    g.Clips.Add(Tracks[t].Clips[i]);
                }
                if (g.Clips.Count > 1) Groups.Add(g);
            }
            OnPropertyChanged(nameof(CanGroupSelection));
        }

        private string CaptureContentSnapshot()
        {
            var data = new ProjectData { SchemaVersion = CurrentSchemaVersion, Tracks = Tracks };
            return System.Text.Json.JsonSerializer.Serialize(data, _snapshotOptions);
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        private string CaptureSnapshot()
        {
            // In-memory round trip of already-live objects, so the marks are whatever convention
            // they are already in. RestoreSnapshot deliberately does NOT re-flag them as legacy —
            // undo must not re-run a migration that has already happened.
            var data = new ProjectData
            {
                Groups = CaptureGroupPositions(),
                SchemaVersion = CurrentSchemaVersion,
                CanvasSizeMode = (int)_canvasSizeMode,
                CanvasWidth = _canvasWidth,
                CanvasHeight = _canvasHeight,
                Tracks = Tracks
            };
            return System.Text.Json.JsonSerializer.Serialize(data, _snapshotOptions);
        }

        // Establish the "nothing to undo" baseline — call after construction and after a project load
        // so neither the empty start nor the load itself is an undo step.
        public void ResetHistory()
        {
            _undo.Clear();
            _redo.Clear();
            _settled = CaptureSnapshot();
            _savedSnapshot = _settled; // a load or a new project is a clean slate, not a change
            _savedContent = CaptureContentSnapshot();
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

        /// <summary>
        /// Give every clip the default placement of the track it sits on.
        /// </summary>
        /// <remarks>
        /// The default is a property of the TRACK, so a clip loaded from a project has to be told
        /// what its baseline is before HasModifications or Reset mean anything. Re-stamping
        /// unconditionally is what makes projects saved before these fields existed behave
        /// correctly, rather than reporting every picture-in-picture as permanently modified.
        /// </remarks>
        private void StampPlacementDefaults()
        {
            for (int i = 0; i < Tracks.Count && i < TrackDefaults.Length; i++)
            {
                foreach (var clip in Tracks[i].Clips)
                {
                    if (clip == null) continue;
                    clip.DefaultPlacementWidth = TrackDefaults[i].w;
                    clip.DefaultPlacementHeight = TrackDefaults[i].h;
                    clip.DefaultPlacementCenterX = TrackDefaults[i].x;
                    clip.DefaultPlacementCenterY = TrackDefaults[i].y;
                }
            }
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

            CanvasSizeMode = (CanvasSizeMode)data.CanvasSizeMode;
            CanvasWidth = data.CanvasWidth;
            CanvasHeight = data.CanvasHeight;

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

            // Membership is rebuilt from positions now the new clip objects exist. A member that
            // no longer resolves - deleted in the state being restored - is dropped, and a group
            // left with fewer than two members goes with it.
            RestoreGroupPositions(data.Groups);
            // Legacy restore paths handled inside LoadAsync. Snapshot restoration only 
            // needs to deal with the current format since snapshots are created in-session.
            // Floor only, and deliberately NO trim: undo must reproduce what was snapshotted,
            // including empty tracks the user added on purpose.
            EnsureTracks();
            StampPlacementDefaults();
            RaiseTrackCountChanged();
            OnPropertyChanged(nameof(TotalStoryTime));
        }
    }
}
