using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace VideoDirector.Models
{
    // Renders the composite to a real .mp4 via Windows.Media.Editing.MediaComposition.
    //
    // ⚠ THE EXPORT IS NOT YET WYSIWYG. MediaComposition can place and trim clips but cannot
    // express per-frame motion, so several things that work in the live preview are not baked in.
    // Whatever is missing here MUST be listed in Limitations below, because the export dialog
    // shows that list to the user before they render — silently dropping their work is worse than
    // telling them. Keep the two in step.
    //
    // Baked in: every track's clips trimmed to their source window, images held for their
    // duration, upper tracks as overlay layers placed by their box and opacity and delayed to
    // their timeline position, per-track mute and hide.
    public class VideoExporter
    {
        // What this renderer cannot currently reproduce from the preview. Shown to the user in the
        // export dialog.
        public static readonly string[] Limitations =
        {
            "Ken Burns motion (zoom and pan) is not applied — clips render at their Start framing.",
            "Per-clip speed is not applied — every clip renders at 1x.",
            "Transitions between clips are not applied — cuts are hard.",
            "Picture-in-picture boxes are stretched to fit rather than cropped to fill."
        };

        // The export renders at 1080p, so overlay positions are in this output pixel space.
        private const double OutputWidth = 1920;
        private const double OutputHeight = 1080;

        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        public enum ExportOutcome { Success, NothingToRender, Failed }

        public class ExportResult
        {
            public ExportOutcome Outcome { get; init; }
            public string Message { get; init; } = string.Empty;
            // Clips left out of the render because their source file was missing.
            public List<string> SkippedFiles { get; init; } = new();
        }

        // Create a trimmed MediaClip from a clip model — shared by spine and overlays. Returns null
        // if the source file is missing (caller skips it rather than failing the whole render).
        private async Task<MediaClip> CreateClipAsync(CinematicOperation op, bool trackMuted = false)
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
            clip.Volume = trackMuted ? 0.0 : op.Volume; // per-clip level, silenced by a muted track

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
            IReadOnlyList<TimelineTrack> tracks, List<string> skipped)
        {
            var composition = new MediaComposition();
            if (tracks == null || tracks.Count == 0) return composition;

            // Track 0 is the base video track; every other track becomes an overlay layer, added
            // in track order so compositing matches the preview (ARCHITECTURE.md §5.4).
            //
            // A hidden track contributes nothing, and a muted one contributes no audio — the live
            // compositor has honoured both since C2b, and an export that ignored them would render
            // something the user had explicitly told the app not to show.
            if (!tracks[0].IsHidden)
            {
                foreach (var op in tracks[0].Clips)
                {
                    if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;
                    var clip = await CreateClipAsync(op, tracks[0].IsMuted);
                    if (clip != null) composition.Clips.Add(clip);
                    else skipped?.Add(System.IO.Path.GetFileName(op.FilePath));
                }
            }

            {
                for (int ti = 1; ti < tracks.Count; ti++)
                {
                    var track = tracks[ti];
                    if (track.IsHidden) continue;
                    if (track?.Clips == null || track.Clips.Count == 0) continue;

                    var layer = new MediaOverlayLayer();
                    foreach (var op in track.Clips)
                    {
                        if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;
                        var clip = await CreateClipAsync(op, track.IsMuted);
                        if (clip == null) { skipped?.Add(System.IO.Path.GetFileName(op.FilePath)); continue; }

                        double boxW = Math.Clamp(op.PlacementWidth, 0.01, 1.0) * OutputWidth;
                        double boxH = Math.Clamp(op.PlacementHeight, 0.01, 1.0) * OutputHeight;
                        double cx = Math.Clamp(op.PlacementCenterX, 0, 1) * OutputWidth;
                        double cy = Math.Clamp(op.PlacementCenterY, 0, 1) * OutputHeight;

                        var overlay = new MediaOverlay(clip)
                        {
                            Position = new Rect(cx - boxW / 2, cy - boxH / 2, boxW, boxH),
                            Opacity = Math.Clamp(op.Opacity, 0, 1),
                            Delay = op.StartTime < TimeSpan.Zero ? TimeSpan.Zero : op.StartTime,
                            AudioEnabled = !track.IsMuted && op.Volume > 0 // muted track or clip adds no audio
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
            IReadOnlyList<TimelineTrack> tracks, StorageFile output, IProgress<double> progress)
        {
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
                return new ExportResult { Outcome = ExportOutcome.NothingToRender, Message = "No renderable clips on the base track.", SkippedFiles = skipped };

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

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
