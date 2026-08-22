using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VideoDirector.Models;

namespace VideoDirector.Tests
{
    // Reads a saved VideoDirector project the way a test needs it: as plain data, with no
    // dependency on the app's model types (which drag in WinUI and cannot load headlessly).
    //
    // Reading the real saved file rather than hand-built fixtures is the point. The Ken Burns
    // defect was only ever reproducible with a specific clip - a 2.39:1 source in a 0.285 x 0.844
    // box, whose Mid mark pans a third of the frame across - and no fixture anyone invented from
    // the source code would have had those numbers in it.
    public sealed class Clip
    {
        public string FilePath = "";
        public TimeSpan StartTime, OpDuration, VideoStartTime;
        public double PlaybackSpeed = 1;
        public double SourceAspect;
        public double PlacementWidth = 1, PlacementHeight = 1;
        public double PlacementCenterX = 0.5, PlacementCenterY = 0.5;
        public CurveProfile CurveProfile;
        public ClipGeometry.Mark StartMark, EndMark;
        public ClipGeometry.Mark? MidMark;

        public string Name => System.IO.Path.GetFileName(FilePath);
        public bool IsStill => PlaybackSpeed <= 0;

        // Raw 0..1 position along this clip's motion at a given story time.
        public double ProgressAt(double storySeconds)
            => OpDuration.TotalMilliseconds > 0
                ? (storySeconds - StartTime.TotalSeconds) / OpDuration.TotalSeconds
                : 0;

        public bool IsActiveAt(double storySeconds)
            => storySeconds >= StartTime.TotalSeconds
            && storySeconds < StartTime.TotalSeconds + OpDuration.TotalSeconds;
    }

    public static class ProjectFixture
    {
        public static bool Exists(string fileName)
            => System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, fileName));

        public static List<List<Clip>> LoadTracks(string fileName)
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));

            var tracks = new List<List<Clip>>();
            CollectTracks(doc.RootElement, tracks);
            return tracks;
        }

        // The schema keeps clips under per-track collections, and the deserialiser still honours the
        // pre-unification names, so walk for anything holding clip objects rather than assuming one
        // shape.
        private static void CollectTracks(JsonElement el, List<List<Clip>> tracks)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var clips = prop.Value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("FilePath", out _))
                            .Select(ReadClip).ToList();
                        if (clips.Count > 0) { tracks.Add(clips); continue; }
                    }
                    CollectTracks(prop.Value, tracks);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) CollectTracks(item, tracks);
            }
        }

        private static Clip ReadClip(JsonElement e)
        {
            var c = new Clip
            {
                FilePath = Str(e, "FilePath"),
                StartTime = Span(e, "StartTime"),
                OpDuration = Span(e, "OpDuration"),
                VideoStartTime = Span(e, "VideoStartTime"),
                PlaybackSpeed = Num(e, "PlaybackSpeed", 1),
                SourceAspect = Num(e, "SourceAspect", 0),
                PlacementWidth = Num(e, "PlacementWidth", 1),
                PlacementHeight = Num(e, "PlacementHeight", 1),
                PlacementCenterX = Num(e, "PlacementCenterX", 0.5),
                PlacementCenterY = Num(e, "PlacementCenterY", 0.5),
                CurveProfile = (CurveProfile)(int)Num(e, "CurveProfile", 0),
                StartMark = Mark(e, "StartMark") ?? new ClipGeometry.Mark(1, 0, 0),
                EndMark = Mark(e, "EndMark") ?? new ClipGeometry.Mark(1, 0, 0),
                MidMark = Mark(e, "MidMark"),
            };
            return c;
        }

        private static string Str(JsonElement e, string n)
            => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        private static double Num(JsonElement e, string n, double dflt)
            => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : dflt;

        private static TimeSpan Span(JsonElement e, string n)
            => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
               && TimeSpan.TryParse(v.GetString(), out var t) ? t : TimeSpan.Zero;

        private static ClipGeometry.Mark? Mark(JsonElement e, string n)
        {
            if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Object) return null;
            return new ClipGeometry.Mark(Num(v, "Scale", 1), Num(v, "X", 0), Num(v, "Y", 0));
        }
    }
}
