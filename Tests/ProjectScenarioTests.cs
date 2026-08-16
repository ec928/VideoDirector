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
