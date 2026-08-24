using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace VideoDirector.Models
{
    // Renders the composite to a real .mp4 via Windows.Media.Editing.MediaComposition.
    //
    // Baked in: the Track 1 spine (each clip trimmed to Clip Start / Clip End, images held for
    // their Duration) plus overlay PiPs — one MediaOverlayLayer per overlay track, each PiP placed
    // by its box (position/size) and opacity, delayed to its timeline position.
    //
    // WHAT THIS CANNOT DO, AND WHY IT IS NOT A TODO.
    //
    // Ken Burns motion, fades, per-clip speed, borders and crop-fill are all per-frame work, and
    // MediaComposition has exactly one hook for that: a video effect on the clip. Measured on this
    // machine, unpackaged .NET 8, three renders of the same five seconds:
    //
    //     no effects                              -> None, 4,884,432 bytes
    //     VideoTransformEffectDefinition (crop)   -> 0xC00DA7FC "stream is not in a state..."
    //     the same effect with NOTHING set        -> 0xC00DA7FC
    //     custom managed IBasicVideoEffect        -> 0x80040154 "class not registered"
    //
    // So the custom effect will not activate (a managed type is not WinRT-activatable from an
    // unpackaged process), and even the SYSTEM-provided transform effect breaks the render - a
    // no-op instance of it is enough to kill it. The baseline render is fine, which is what makes
    // this attributable rather than a guess.
    //
    // Everything this exporter does NOT bake is therefore blocked on the API, not on effort:
    // getting it would mean a second renderer (Win2D frame server -> MediaStreamSource, with its
    // own audio mixing), not a few more lines here. The app itself is the delivery mechanism; this
    // is for handing someone a file. WhatIsNotBaked() below tells the user which of it applies to
    // the project in front of them, BEFORE they wait for a render.
    public class VideoExporter
    {
        // The output frame, and therefore the pixel space overlay positions are resolved into.
        // Taken from the project canvas rather than pinned at 1080p: a 9:16 or 2.39:1 project used
        // to be squeezed into 16:9 and letterboxed by a renderer that had been told the wrong shape.
        private double _outputWidth = 1920;
        private double _outputHeight = 1080;

        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        public enum ExportOutcome { Success, NothingToRender, Failed }

        /// <summary>
        /// The features this project uses that the render cannot carry, in the user's words.
        /// Empty when the export will match what is on screen.
        /// </summary>
        /// <remarks>
        /// Checked against the actual clips rather than listed as a blanket disclaimer, so a
        /// project with no motion and no fades gets no warning at all and the ones that do get a
        /// list they can act on - before the wait, not after it.
        /// </remarks>
        public static List<string> WhatIsNotBaked(IEnumerable<TimelineTrack> tracks)
        {
            bool motion = false, speed = false, fade = false, border = false, pip = false;

            var list = tracks?.ToList();
            if (list != null)
            {
                for (int t = 0; t < list.Count; t++)
                {
                    if (list[t]?.Clips == null) continue;
                    foreach (var op in list[t].Clips)
                    {
                        if (op == null) continue;

                        if (op.StartMark.Scale != 1.0f || op.StartMark.X != 0 || op.StartMark.Y != 0 ||
                            op.EndMark.Scale != 1.0f || op.EndMark.X != 0 || op.EndMark.Y != 0)
                            motion = true;

                        if (!op.HasNoSourceWindow && op.PlaybackSpeed != 1.0) speed = true;

                        if (op.TransitionStyle != TransitionStyle.HardSnap && op.TransitionDuration > TimeSpan.Zero)
                            fade = true;

                        if (op.BorderType != BorderType.None) border = true;

                        if (t > 0) pip = true;
                    }
                }
            }

            var lost = new List<string>();
            if (motion) lost.Add("Ken Burns pan and zoom - clips render on their opening frame");
            if (fade)   lost.Add("Fades - cuts will be hard");
            if (speed)  lost.Add("Per-clip speed - everything plays at 1x");
            if (border) lost.Add("Borders");
            if (pip)    lost.Add("Picture-in-picture is stretched into its box rather than cropped to fill");
            return lost;
        }

        public class ExportResult
        {
            public ExportOutcome Outcome { get; init; }
            public string Message { get; init; } = string.Empty;
            // Clips left out of the render because their source file was missing.
            public List<string> SkippedFiles { get; init; } = new();
        }

        // Create a trimmed MediaClip from a clip model — shared by spine and overlays. Returns null
        // if the source file is missing (caller skips it rather than failing the whole render).
        private async Task<MediaClip> CreateClipAsync(CinematicOperation op)
        {
            StorageFile file;
            try { file = await StorageFile.GetFileFromPathAsync(op.FilePath); }
            catch { return null; }

            var ext = System.IO.Path.GetExtension(op.FilePath);
            bool isImage = Array.Exists(ImageExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));

            if (isImage)
            {
                var hold = op.OpDuration > TimeSpan.Zero ? op.OpDuration : TimeSpan.FromSeconds(3);
                return await MediaClip.CreateFromImageFileAsync(file, hold);
            }

            var clip = await MediaClip.CreateFromFileAsync(file);
            clip.Volume = op.Volume; // per-clip audio level (overlays default muted)

            // Trim to the clip's source window (Clip Start / Clip End). TrimTimeFromEnd is measured
            // back from the source's real end, so derive it from OriginalDuration.
            var start = op.VideoStartTime;
            if (start > TimeSpan.Zero && start < clip.OriginalDuration)
                clip.TrimTimeFromStart = start;

            var end = op.VideoEndTime > TimeSpan.Zero ? op.VideoEndTime : clip.OriginalDuration;
            var fromEnd = clip.OriginalDuration - end;
            if (fromEnd > TimeSpan.Zero && fromEnd < clip.OriginalDuration)
                clip.TrimTimeFromEnd = fromEnd;

            return clip;
        }

        // Build the full composition: spine clips as the base video track, plus one overlay layer
        // per overlay track. Overlays within a track never overlap in time (enforced on add/move),
        // which is exactly the constraint MediaOverlayLayer requires.
        public async Task<MediaComposition> BuildCompositionAsync(
            IEnumerable<TimelineTrack> tracks, List<string> skipped)
        {
            var composition = new MediaComposition();
            var tracksList = tracks?.ToList();
            if (tracksList == null || tracksList.Count == 0) return composition;

            foreach (var op in tracksList[0].Clips)
            {
                if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;
                var clip = await CreateClipAsync(op);
                if (clip != null) composition.Clips.Add(clip);
                else skipped?.Add(System.IO.Path.GetFileName(op.FilePath));
            }

            if (tracksList.Count > 1)
            {
                foreach (var track in tracksList.Skip(1))
                {
                    if (track?.Clips == null || track.Clips.Count == 0) continue;

                    var layer = new MediaOverlayLayer();
                    foreach (var op in track.Clips)
                    {
                        if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;
                        var clip = await CreateClipAsync(op);
                        if (clip == null) { skipped?.Add(System.IO.Path.GetFileName(op.FilePath)); continue; }

                        double boxW = Math.Clamp(op.PlacementWidth, 0.01, 1.0) * _outputWidth;
                        double boxH = Math.Clamp(op.PlacementHeight, 0.01, 1.0) * _outputHeight;
                        double cx = Math.Clamp(op.PlacementCenterX, 0, 1) * _outputWidth;
                        double cy = Math.Clamp(op.PlacementCenterY, 0, 1) * _outputHeight;

                        var overlay = new MediaOverlay(clip)
                        {
                            Position = new Rect(cx - boxW / 2, cy - boxH / 2, boxW, boxH),
                            Opacity = Math.Clamp(op.Opacity, 0, 1),
                            Delay = op.StartTime < TimeSpan.Zero ? TimeSpan.Zero : op.StartTime,
                            AudioEnabled = op.Volume > 0 // muted overlays contribute no audio to the mix
                        };
                        layer.Overlays.Add(overlay);
                    }

                    if (layer.Overlays.Count > 0) composition.OverlayLayers.Add(layer);
                }
            }

            return composition;
        }

        // Render the composite to `output`. Reports 0..100 progress. Never throws for the expected
        // cases (missing files, nothing to render) — returns a described ExportResult.
        public async Task<ExportResult> ExportAsync(
            IEnumerable<TimelineTrack> tracks,
            StorageFile output, IProgress<double> progress,
            double canvasWidth = 1920, double canvasHeight = 1080)
        {
            // H.264 wants even dimensions, and a canvas can be any odd size the user typed.
            _outputWidth  = Math.Max(2, Math.Round(canvasWidth  / 2) * 2);
            _outputHeight = Math.Max(2, Math.Round(canvasHeight / 2) * 2);

            var skipped = new List<string>();
            MediaComposition composition;
            try
            {
                composition = await BuildCompositionAsync(tracks, skipped);
            }
            catch (Exception ex)
            {
                return new ExportResult { Outcome = ExportOutcome.Failed, Message = ex.Message, SkippedFiles = skipped };
            }

            if (composition.Clips.Count == 0)
                return new ExportResult { Outcome = ExportOutcome.NothingToRender, Message = "No renderable Track 1 clips.", SkippedFiles = skipped };

            // Start from the tier nearest the canvas height, then state the real frame size. The
            // tier still sets the bitrate, which is why it is picked by size rather than fixed.
            var tier = _outputHeight >= 2000 ? VideoEncodingQuality.Uhd2160p
                     : _outputHeight >= 1000 ? VideoEncodingQuality.HD1080p
                     : _outputHeight >=  700 ? VideoEncodingQuality.HD720p
                     :                         VideoEncodingQuality.Wvga;
            var profile = MediaEncodingProfile.CreateMp4(tier);
            if (profile.Video != null)
            {
                profile.Video.Width  = (uint)_outputWidth;
                profile.Video.Height = (uint)_outputHeight;
            }

            try
            {
                var render = composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile);
                if (progress != null)
                    render.Progress = (info, pct) => progress.Report(pct);

                var reason = await render;
                return reason == TranscodeFailureReason.None
                    ? new ExportResult { Outcome = ExportOutcome.Success, Message = output.Path, SkippedFiles = skipped }
                    : new ExportResult { Outcome = ExportOutcome.Failed, Message = reason.ToString(), SkippedFiles = skipped };
            }
            catch (Exception ex)
            {
                return new ExportResult { Outcome = ExportOutcome.Failed, Message = ex.Message, SkippedFiles = skipped };
            }
        }
    }
}
