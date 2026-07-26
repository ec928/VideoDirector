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
    // Baked in: the Track 1 spine (each clip trimmed to Clip Start / Clip End, images held for
    // their Duration) plus overlay PiPs — one MediaOverlayLayer per overlay track, each PiP placed
    // by its box (position/size) and opacity, delayed to its timeline position.
    //
    // NOT yet in the export (all working in the live preview): per-clip Speed (needs a retiming
    // effect — the hardest), Ken Burns motion, and Transitions. Overlay PiPs are stretched into
    // their box rather than crop-filled as in the preview; matching UniformToFill is a refinement.
    public class VideoExporter
    {
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
            IEnumerable<CinematicOperation> spine, IEnumerable<OverlayTrack> overlayTracks, List<string> skipped)
        {
            var composition = new MediaComposition();

            foreach (var op in spine)
            {
                if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;
                var clip = await CreateClipAsync(op);
                if (clip != null) composition.Clips.Add(clip);
                else skipped?.Add(System.IO.Path.GetFileName(op.FilePath));
            }

            if (overlayTracks != null)
            {
                foreach (var track in overlayTracks)
                {
                    if (track?.Clips == null || track.Clips.Count == 0) continue;

                    var layer = new MediaOverlayLayer();
                    foreach (var op in track.Clips)
                    {
                        if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;
                        var clip = await CreateClipAsync(op);
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
            IEnumerable<CinematicOperation> spine, IEnumerable<OverlayTrack> overlayTracks,
            StorageFile output, IProgress<double> progress)
        {
            var skipped = new List<string>();
            MediaComposition composition;
            try
            {
                composition = await BuildCompositionAsync(spine, overlayTracks, skipped);
            }
            catch (Exception ex)
            {
                return new ExportResult { Outcome = ExportOutcome.Failed, Message = ex.Message, SkippedFiles = skipped };
            }

            if (composition.Clips.Count == 0)
                return new ExportResult { Outcome = ExportOutcome.NothingToRender, Message = "No renderable Track 1 clips.", SkippedFiles = skipped };

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
