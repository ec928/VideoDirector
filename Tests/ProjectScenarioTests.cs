using System;
using System.Linq;
using VideoDirector.Models;
using VideoDirector.ViewModels;
using Xunit;

namespace VideoDirector.Tests
{
    // End-to-end scenarios over a real DirectorViewModel: build a project, move clips between
    // tracks, undo, save, reload. No UI thread and no media files — these exercise the whole data
    // pipeline, which is where a structural refactor loses data silently.
    public class ProjectScenarioTests
    {
        private static CinematicOperation Clip(string name, double durSec)
        {
            var c = new CinematicOperation
            {
                FilePath = $@"C:\clips\{name}.mp4",
                SourceDuration = TimeSpan.FromSeconds(durSec + 60)
            };
            c.VideoStartTime = TimeSpan.Zero;
            c.VideoEndTime = TimeSpan.FromSeconds(durSec);
            return c;
        }

        private static DirectorViewModel Project()
        {
            var vm = new DirectorViewModel();
            vm.Tracks[0].Clips.Add(Clip("a", 10));
            vm.Tracks[0].Clips.Add(Clip("b", 5));
            vm.Tracks[2].Clips.Add(Clip("overlay", 4));
            vm.Tracks[2].Clips[0].StartTimeSeconds = 3;
            vm.RecordIfChanged();
            return vm;
        }

        [Fact]
        public void AProjectHasFourPeerTracks()
        {
            var vm = new DirectorViewModel();
            Assert.Equal(DirectorViewModel.TrackCount, vm.Tracks.Count);
            // Track 0 ships gapless so the default feel is unchanged; the rest are free.
            Assert.True(vm.Tracks[0].IsGapless);
            for (int i = 1; i < vm.Tracks.Count; i++) Assert.False(vm.Tracks[i].IsGapless);
        }

        [Fact]
        public void ClipsOnTrackZeroReflowAsTheyAreAdded()
        {
            var vm = Project();
            Assert.Equal(0, vm.Tracks[0].Clips[0].StartTimeSeconds, 6);
            Assert.Equal(10, vm.Tracks[0].Clips[1].StartTimeSeconds, 6);
        }

        [Fact]
        public void DurationIsTheLatestEndAcrossEveryTrack()
        {
            var vm = new DirectorViewModel();
            vm.Tracks[1].Clips.Add(Clip("only", 7));
            vm.Tracks[1].Clips[0].StartTimeSeconds = 100;

            // A project made only of upper-track clips used to report zero duration, which made it
            // both unplayable and invisible (the timeline scale divided by it).
            Assert.Equal(107, vm.TotalStoryDuration.TotalSeconds, 6);
        }

        [Fact]
        public void AClipCanLiveOnEveryTrackIncludingTrackZero()
        {
            var vm = new DirectorViewModel();
            for (int i = 0; i < vm.Tracks.Count; i++) vm.Tracks[i].Clips.Add(Clip("c" + i, 5));
            for (int i = 0; i < vm.Tracks.Count; i++) Assert.Single(vm.Tracks[i].Clips);
        }

        [Fact]
        public void MovingAClipBetweenAnyTwoTracksKeepsItInExactlyOnePlace()
        {
            var vm = Project();
            var clip = vm.Tracks[2].Clips[0];

            // Track 2 -> track 0 (used to be the only supported direction, "overlay to spine")...
            vm.Tracks[2].Clips.Remove(clip);
            vm.Tracks[0].Clips.Add(clip);
            vm.Tracks[0].Normalize();
            Assert.Equal(0, vm.TrackIndexOf(clip));
            Assert.DoesNotContain(clip, vm.Tracks[2].Clips);

            // ...and track 0 -> track 3, which had no code path at all before.
            vm.Tracks[0].Clips.Remove(clip);
            vm.Tracks[3].Clips.Add(clip);
            vm.Tracks[3].Normalize();
            Assert.Equal(3, vm.TrackIndexOf(clip));
            Assert.DoesNotContain(clip, vm.Tracks[0].Clips);
        }

        [Fact]
        public void TrackZeroCanHaveGapsWhenItIsNotGapless()
        {
            var vm = new DirectorViewModel();
            vm.Tracks[0].IsGapless = false;
            vm.Tracks[0].Clips.Add(Clip("a", 5));
            vm.Tracks[0].Clips.Add(Clip("b", 5));
            vm.Tracks[0].Clips[1].StartTimeSeconds = 40;
            vm.Tracks[0].Normalize();

            Assert.Equal(40, vm.Tracks[0].Clips[1].StartTimeSeconds, 6);
            Assert.Null(vm.Tracks[0].ClipAt(TimeSpan.FromSeconds(20)));   // a real gap
        }

        [Fact]
        public void SelectionIsOneClipWhateverTrackItIsOn()
        {
            var vm = Project();
            vm.SelectedClip = vm.Tracks[0].Clips[0];
            Assert.Equal(0, vm.SelectedTrackIndex);
            Assert.True(vm.HasSelection);

            vm.SelectedClip = vm.Tracks[2].Clips[0];
            Assert.Equal(2, vm.SelectedTrackIndex);
            // Selecting on another track must not leave the first one selected too.
            Assert.Same(vm.Tracks[2].Clips[0], vm.SelectedClip);
        }

        [Fact]
        public void PositionIsEditableOnlyWhereTheUserOwnsIt()
        {
            var vm = Project();
            vm.SelectedClip = vm.Tracks[0].Clips[0];      // gapless: derived from order
            Assert.False(vm.IsSelectedPositionEditable);
            Assert.True(vm.IsSelectedTransitionApplicable);

            vm.SelectedClip = vm.Tracks[2].Clips[0];      // free: user places it
            Assert.True(vm.IsSelectedPositionEditable);
            Assert.False(vm.IsSelectedTransitionApplicable);
        }

        // ---- Locking (C3) --------------------------------------------------------------------

        [Fact]
        public void ALockedTrackStillReportsItsClips()
        {
            // Lock guards mutation, not visibility — a locked clip must still be findable and
            // inspectable.
            var vm = Project();
            vm.Tracks[0].IsLocked = true;
            var clip = vm.Tracks[0].Clips[0];

            Assert.Equal(0, vm.TrackIndexOf(clip));
            vm.SelectedClip = clip;
            Assert.True(vm.HasSelection);
            Assert.Same(clip, vm.SelectedClip);
        }

        // ---- Drop placement (C4) -------------------------------------------------------------
        // The preview a drag draws is ClampToFreeSlot's answer, so what it resolves to IS what a
        // drop commits. These pin that resolution.

        [Fact]
        public void ADroppedClipNeverLandsOnTopOfASibling()
        {
            var track = new TimelineTrack();
            track.Clips.Add(Clip("a", 10));                       // occupies [0,10]
            var moving = Clip("b", 4);

            // Every requested position resolves to somewhere that does not overlap.
            for (double want = -5; want <= 20; want += 0.5)
            {
                double at = track.ClampToFreeSlot(moving, want, 4);
                bool overlaps = at < 10 && at + 4 > 0;
                Assert.False(overlaps, $"asking for {want} resolved to {at}, which overlaps [0,10]");
            }
        }

        [Fact]
        public void DraggingAClipWithinItsOwnTrackCanStayPut()
        {
            var track = new TimelineTrack();
            var a = Clip("a", 5);
            a.StartTimeSeconds = 20;
            track.Clips.Add(a);

            // The clip being moved must not block itself, or a drag could never end where it began.
            Assert.Equal(20, track.ClampToFreeSlot(a, 20, 5), 6);
        }

        [Fact]
        public void MovingAClipToAGaplessTrackTakesItsPositionFromOrder()
        {
            var vm = new DirectorViewModel();
            vm.Tracks[0].Clips.Add(Clip("a", 10));
            var moving = Clip("b", 4);
            moving.StartTimeSeconds = 500;                        // nonsense from its old track
            vm.Tracks[2].Clips.Add(moving);

            vm.Tracks[2].Clips.Remove(moving);
            vm.Tracks[0].Clips.Insert(1, moving);
            vm.Tracks[0].Normalize();

            Assert.Equal(10, moving.StartTimeSeconds, 6);          // right after the first clip
        }

        // ---- Track flags (C3) ----------------------------------------------------------------

        [Fact]
        public void TrackFlagsDefaultOff()
        {
            var vm = new DirectorViewModel();
            foreach (var t in vm.Tracks)
            {
                Assert.False(t.IsMuted);
                Assert.False(t.IsHidden);
                Assert.False(t.IsLocked);
            }
        }

        [Fact]
        public void TrackFlagsSurviveASaveAndReload()
        {
            var vm = Project();
            vm.Tracks[0].IsLocked = true;
            vm.Tracks[1].IsMuted = true;
            vm.Tracks[2].IsHidden = true;
            vm.Tracks[3].IsGapless = true;

            var reloaded = new DirectorViewModel();
            reloaded.LoadProjectJson(vm.ToProjectJson());

            Assert.True(reloaded.Tracks[0].IsLocked);
            Assert.True(reloaded.Tracks[1].IsMuted);
            Assert.True(reloaded.Tracks[2].IsHidden);
            Assert.True(reloaded.Tracks[3].IsGapless);
        }

        [Fact]
        public void TrackNamesSurviveASaveAndReload()
        {
            var vm = Project();
            vm.Tracks[2].Name = "B-roll";
            var reloaded = new DirectorViewModel();
            reloaded.LoadProjectJson(vm.ToProjectJson());
            Assert.Equal("B-roll", reloaded.Tracks[2].Name);
        }

        [Fact]
        public void SwitchingATrackToGaplessClosesItsGaps()
        {
            var vm = new DirectorViewModel();
            vm.Tracks[1].Clips.Add(Clip("a", 5));
            vm.Tracks[1].Clips.Add(Clip("b", 5));
            vm.Tracks[1].Clips[1].StartTimeSeconds = 90;

            vm.Tracks[1].IsGapless = true;

            Assert.Equal(0, vm.Tracks[1].Clips[0].StartTimeSeconds, 6);
            Assert.Equal(5, vm.Tracks[1].Clips[1].StartTimeSeconds, 6);
        }

        [Fact]
        public void ATrackDefaultPlacementIsPerTrack()
        {
            // Track 0 defaults to centre (full frame); the others to their own corners, so
            // stacked PiPs do not land on top of each other.
            var vm = new DirectorViewModel();
            Assert.Equal(0.5, vm.Tracks[0].DefaultCenterX, 6);
            Assert.Equal(0.5, vm.Tracks[0].DefaultCenterY, 6);

            var corners = new System.Collections.Generic.HashSet<(double, double)>();
            for (int i = 1; i < vm.Tracks.Count; i++)
                corners.Add((vm.Tracks[i].DefaultCenterX, vm.Tracks[i].DefaultCenterY));
            Assert.Equal(vm.Tracks.Count - 1, corners.Count);
        }

        // ---- Round trip and history ----------------------------------------------------------

        [Fact]
        public void SaveAndReloadPreservesEveryTrack()
        {
            var vm = Project();
            vm.Tracks[1].IsMuted = true;
            vm.Tracks[3].IsHidden = true;
            string json = vm.ToProjectJson();

            var reloaded = new DirectorViewModel();
            reloaded.LoadProjectJson(json);

            Assert.Equal(2, reloaded.Tracks[0].Clips.Count);
            Assert.Single(reloaded.Tracks[2].Clips);
            Assert.True(reloaded.Tracks[1].IsMuted);
            Assert.True(reloaded.Tracks[3].IsHidden);
            Assert.Equal(vm.TotalStoryDuration, reloaded.TotalStoryDuration);
        }

        [Fact]
        public void ReloadingPreservesClipTimingExactly()
        {
            var vm = Project();
            var reloaded = new DirectorViewModel();
            reloaded.LoadProjectJson(vm.ToProjectJson());

            for (int t = 0; t < vm.Tracks.Count; t++)
                for (int i = 0; i < vm.Tracks[t].Clips.Count; i++)
                {
                    Assert.Equal(vm.Tracks[t].Clips[i].StartTimeSeconds,
                                 reloaded.Tracks[t].Clips[i].StartTimeSeconds, 6);
                    Assert.Equal(vm.Tracks[t].Clips[i].OpDuration,
                                 reloaded.Tracks[t].Clips[i].OpDuration);
                }
        }

        [Fact]
        public void UndoRestoresARemovedClip()
        {
            var vm = Project();
            int before = vm.Tracks[0].Clips.Count;

            vm.Tracks[0].Clips.RemoveAt(0);
            vm.RecordIfChanged();
            Assert.Equal(before - 1, vm.Tracks[0].Clips.Count);

            vm.Undo();
            Assert.Equal(before, vm.Tracks[0].Clips.Count);
        }

        [Fact]
        public void RedoReappliesWhatUndoTookAway()
        {
            var vm = Project();
            vm.Tracks[0].Clips.RemoveAt(0);
            vm.RecordIfChanged();
            vm.Undo();
            vm.Redo();
            Assert.Single(vm.Tracks[0].Clips);
        }

        [Fact]
        public void UndoSurvivesTheTrackInstancesItRestoresInto()
        {
            // Undo used to REPLACE the track objects, which silently broke every CollectionChanged
            // subscription pointing at the old ones. The instances must be stable.
            var vm = Project();
            var trackObjects = vm.Tracks.ToArray();
            var clipsCollections = vm.Tracks.Select(t => t.Clips).ToArray();

            vm.Tracks[0].Clips.Clear();
            vm.RecordIfChanged();
            vm.Undo();

            Assert.Equal(trackObjects, vm.Tracks.ToArray());
            for (int i = 0; i < clipsCollections.Length; i++)
                Assert.Same(clipsCollections[i], vm.Tracks[i].Clips);
        }

        [Fact]
        public void ClearEmptiesEveryTrackAndIsUndoable()
        {
            var vm = Project();
            vm.Clear();
            foreach (var t in vm.Tracks) Assert.Empty(t.Clips);

            vm.Undo();
            Assert.Equal(2, vm.Tracks[0].Clips.Count);
            Assert.Single(vm.Tracks[2].Clips);
        }

        // ---- Unsaved work ----------------------------------------------------------------------
        // Closing the window used to discard everything silently. Dirtiness reuses the same
        // snapshot the undo history takes, so there is only one definition of "changed".

        [Fact]
        public void ANewProjectHasNothingToLose()
        {
            var vm = new DirectorViewModel();
            Assert.False(vm.IsDirty);
            Assert.False(vm.HasProjectPath);
        }

        [Fact]
        public void AddingAClipMakesTheProjectDirty()
        {
            var vm = new DirectorViewModel();
            vm.Tracks[0].Clips.Add(Clip("a", 5));
            vm.RecordIfChanged();
            Assert.True(vm.IsDirty);
        }

        [Fact]
        public void SavingClearsTheDirtyFlag()
        {
            var vm = Project();
            Assert.True(vm.IsDirty);

            vm.MarkSaved(@"C:\projects\demo.json");

            Assert.False(vm.IsDirty);
            Assert.True(vm.HasProjectPath);
            Assert.Equal("demo", vm.ProjectName);
        }

        [Fact]
        public void EditingAfterASaveMakesItDirtyAgain()
        {
            var vm = Project();
            vm.MarkSaved(@"C:\projects\demo.json");

            vm.Tracks[0].Clips.Add(Clip("late", 3));
            vm.RecordIfChanged();

            Assert.True(vm.IsDirty);
        }

        [Fact]
        public void UndoingBackToTheSavedStateIsNotDirty()
        {
            // Dirtiness compares against what is on disk, not "has anything happened", so undoing
            // a change back to the saved state correctly reports clean.
            var vm = Project();
            vm.MarkSaved(@"C:\projects\demo.json");

            vm.Tracks[0].Clips.Add(Clip("late", 3));
            vm.RecordIfChanged();
            Assert.True(vm.IsDirty);

            vm.Undo();
            Assert.False(vm.IsDirty);
        }

        [Fact]
        public void TheTitleShowsTheProjectAndWhetherItIsSaved()
        {
            var vm = Project();
            Assert.StartsWith("•", vm.WindowTitle);          // unsaved marker
            Assert.Contains("Untitled project", vm.WindowTitle);

            vm.MarkSaved(@"C:\projects\holiday.json");
            Assert.DoesNotContain("•", vm.WindowTitle);
            Assert.Contains("holiday", vm.WindowTitle);
        }

        // ---- Migration -----------------------------------------------------------------------

        [Fact]
        public void LoadsTheV0BareArrayFormat()
        {
            // The oldest format: a bare array of clips, which was track 0 and nothing else.
            string v0 = @"[{""FilePath"":""C:\\clips\\a.mp4"",""SourceDuration"":""00:00:30"",
                            ""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:10"",
                            ""OpDuration"":""00:00:10""}]";
            var vm = new DirectorViewModel();
            vm.LoadProjectJson(v0);

            Assert.Single(vm.Tracks[0].Clips);
            Assert.Equal(0, vm.Tracks[0].Clips[0].StartTimeSeconds, 6);
        }

        [Fact]
        public void LoadsTheV1TwoCollectionFormatOntoTheRightTracks()
        {
            // v1 kept track 0 in TimelineNodes and the rest in OverlayTracks, and track 0's clips
            // had no StartTime of their own — position was implied by order.
            string v1 = @"{
              ""TimelineNodes"":[
                {""FilePath"":""C:\\clips\\a.mp4"",""SourceDuration"":""00:01:00"",""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:10"",""OpDuration"":""00:00:10""},
                {""FilePath"":""C:\\clips\\b.mp4"",""SourceDuration"":""00:01:00"",""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:05"",""OpDuration"":""00:00:05""}],
              ""OverlayTracks"":[
                {""Clips"":[]},
                {""Clips"":[{""FilePath"":""C:\\clips\\ov.mp4"",""SourceDuration"":""00:01:00"",""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:04"",""OpDuration"":""00:00:04"",""StartTime"":""00:00:03""}]}]
            }";
            var vm = new DirectorViewModel();
            vm.LoadProjectJson(v1);

            Assert.Equal(2, vm.Tracks[0].Clips.Count);
            // Derived from order, since v1 never stored them.
            Assert.Equal(0, vm.Tracks[0].Clips[0].StartTimeSeconds, 6);
            Assert.Equal(10, vm.Tracks[0].Clips[1].StartTimeSeconds, 6);
            // v1 overlay track 1 is now track 2, and keeps its own start time.
            Assert.Single(vm.Tracks[2].Clips);
            Assert.Equal(3, vm.Tracks[2].Clips[0].StartTimeSeconds, 6);
        }

        [Fact]
        public void LoadsTheV0FlatOverlayList()
        {
            string legacy = @"{""TimelineNodes"":[],""OverlayClips"":[
                {""FilePath"":""C:\\clips\\ov.mp4"",""SourceDuration"":""00:01:00"",""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:04"",""OpDuration"":""00:00:04"",""StartTime"":""00:00:02""}]}";
            var vm = new DirectorViewModel();
            vm.LoadProjectJson(legacy);

            Assert.Single(vm.Tracks[1].Clips);   // the flat list belonged to the first upper track
        }

        [Fact]
        public void LoadsPreNormalisationFramingMarks()
        {
            // v1/v2 marks stored Scale plus a PIXEL translation. They must convert on load, not
            // arrive as an identity mark (silently throwing away the user's framing) or as a zoom
            // of 0 (a clip that renders as nothing).
            string legacy = @"{""SchemaVersion"":2,""Tracks"":[{""IsGapless"":true,""Clips"":[
                {""FilePath"":""C:\\clips\\a.mp4"",""SourceDuration"":""00:01:00"",
                 ""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:10"",""OpDuration"":""00:00:10"",
                 ""StartMark"":{""Scale"":1.0,""X"":0.0,""Y"":0.0},
                 ""EndMark"":{""Scale"":2.0,""X"":-200.0,""Y"":0.0}}]}]}";

            var vm = new DirectorViewModel();
            vm.LoadProjectJson(legacy);

            var clip = vm.Tracks[0].Clips[0];
            Assert.True(clip.StartMark.IsIdentity);          // an unframed mark converts exactly
            Assert.Equal(2.0, clip.EndMark.Zoom, 6);         // zoom survives
            Assert.True(clip.EndMark.CenterX > 0.5);         // and the pan still points the same way
        }

        [Fact]
        public void MigratedFramingSurvivesTheNextSaveInNormalisedForm()
        {
            string legacy = @"{""SchemaVersion"":2,""Tracks"":[{""IsGapless"":true,""Clips"":[
                {""FilePath"":""C:\\clips\\a.mp4"",""SourceDuration"":""00:01:00"",
                 ""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:10"",""OpDuration"":""00:00:10"",
                 ""EndMark"":{""Scale"":2.0,""X"":-200.0,""Y"":0.0}}]}]}";

            var vm = new DirectorViewModel();
            vm.LoadProjectJson(legacy);
            double centerX = vm.Tracks[0].Clips[0].EndMark.CenterX;

            // Round-tripping must not re-apply the conversion, or framing would drift a little
            // further from the original every time the project was opened and saved.
            var again = new DirectorViewModel();
            again.LoadProjectJson(vm.ToProjectJson());

            Assert.Equal(centerX, again.Tracks[0].Clips[0].EndMark.CenterX, 6);
            Assert.Equal(2.0, again.Tracks[0].Clips[0].EndMark.Zoom, 6);
        }

        [Fact]
        public void FramingSurvivesASaveAndReload()
        {
            var vm = Project();
            vm.Tracks[0].Clips[0].StartMark = new SpatialMark(1.0, 0.5, 0.5);
            vm.Tracks[0].Clips[0].MidMark = new SpatialMark(1.5, 0.3, 0.4);
            vm.Tracks[0].Clips[0].EndMark = new SpatialMark(2.0, 0.8, 0.2);

            var reloaded = new DirectorViewModel();
            reloaded.LoadProjectJson(vm.ToProjectJson());

            var clip = reloaded.Tracks[0].Clips[0];
            Assert.Equal(1.5, clip.MidMark.Zoom, 6);
            Assert.Equal(0.3, clip.MidMark.CenterX, 6);
            Assert.Equal(2.0, clip.EndMark.Zoom, 6);
            Assert.Equal(0.8, clip.EndMark.CenterX, 6);
            Assert.Equal(0.2, clip.EndMark.CenterY, 6);
        }

        [Fact]
        public void OlderProjectsGetTrackZeroBackAtFullFrame()
        {
            // Before v4, track 0 was always drawn full-frame and ignored placement, so every clip
            // on it carries the corner-PiP DEFAULTS that were written out and never read. Honouring
            // placement on track 0 made those dormant values suddenly real, shrinking every clip in
            // an existing project into the bottom-right corner.
            string older = @"{""SchemaVersion"":3,""Tracks"":[{""IsGapless"":true,""Clips"":[
                {""FilePath"":""C:/clips/a.mp4"",""SourceDuration"":""00:01:00"",
                 ""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:10"",""OpDuration"":""00:00:10"",
                 ""PlacementWidth"":0.3,""PlacementHeight"":0.3,
                 ""PlacementCenterX"":0.72,""PlacementCenterY"":0.72}]}]}";

            var vm = new DirectorViewModel();
            vm.LoadProjectJson(older);

            var clip = vm.Tracks[0].Clips[0];
            Assert.Equal(1.0, clip.PlacementWidth, 6);
            Assert.Equal(1.0, clip.PlacementHeight, 6);
            Assert.Equal(0.5, clip.PlacementCenterX, 6);
            Assert.Equal(0.5, clip.PlacementCenterY, 6);
        }

        [Fact]
        public void OlderProjectsKeepUpperTrackPlacement()
        {
            // Only track 0 is corrected. An upper track's placement was always real and must
            // survive untouched.
            string older = @"{""SchemaVersion"":3,""Tracks"":[
                {""IsGapless"":true,""Clips"":[]},
                {""Clips"":[{""FilePath"":""C:/clips/ov.mp4"",""SourceDuration"":""00:01:00"",
                 ""VideoStartTime"":""00:00:00"",""VideoEndTime"":""00:00:04"",""OpDuration"":""00:00:04"",
                 ""PlacementWidth"":0.3,""PlacementHeight"":0.3,
                 ""PlacementCenterX"":0.72,""PlacementCenterY"":0.72}]}]}";

            var vm = new DirectorViewModel();
            vm.LoadProjectJson(older);

            var clip = vm.Tracks[1].Clips[0];
            Assert.Equal(0.3, clip.PlacementWidth, 6);
            Assert.Equal(0.72, clip.PlacementCenterX, 6);
        }

        [Fact]
        public void CurrentProjectsKeepDeliberateTrackZeroPlacement()
        {
            // Once a project is at the current version, a track-0 clip the user has deliberately
            // placed must not be dragged back to full frame on every load.
            var vm = new DirectorViewModel();
            vm.Tracks[0].Clips.Add(Clip("a", 5));
            vm.Tracks[0].Clips[0].PlaceAt(0.72, 0.72);

            var reloaded = new DirectorViewModel();
            reloaded.LoadProjectJson(vm.ToProjectJson());

            Assert.Equal(0.72, reloaded.Tracks[0].Clips[0].PlacementCenterX, 6);
        }

        [Fact]
        public void ACorruptProjectLeavesTheCurrentOneAlone()
        {
            var vm = Project();
            vm.LoadProjectJson("{ this is not json");
            Assert.Equal(2, vm.Tracks[0].Clips.Count);
        }

        [Fact]
        public void LoadingReplacesRatherThanAppends()
        {
            var vm = Project();
            var other = new DirectorViewModel();
            other.Tracks[0].Clips.Add(Clip("solo", 3));

            vm.LoadProjectJson(other.ToProjectJson());

            Assert.Single(vm.Tracks[0].Clips);
            Assert.Empty(vm.Tracks[2].Clips);
        }
    }
}
