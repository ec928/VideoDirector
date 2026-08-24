using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace VideoDirector.Models
{
    /// <summary>
    /// Puts the project's sound onto a recorded performance.
    /// </summary>
    /// <remarks>
    /// The recording is silent — screen capture takes pixels, not audio. Rather than capture the
    /// speakers as well and then fight to line two independently clocked streams up, the sound is
    /// mixed from the source files and laid onto the finished video in one pass.
    ///
    /// WHY THERE IS AN EXTRACTION STEP. BackgroundAudioTrack is what carries a Delay, a Volume and
    /// its own trim, and it does so WITHOUT a video overlay layer — which matters, because the
    /// overlay video path is precisely where MediaComposition refuses real projects. But it will
    /// not accept a video file at all: it answers "source clip cannot be video file" for every
    /// source in every test project. So each audible clip's sound is rendered to a small M4A first
    /// and that is what gets mixed. Measured at 0.4s for a twelve-second segment, so a project's
    /// worth costs a second or two, not another real-time pass.
    ///
    /// The video is a single clip — the recording — which is plain, even-dimensioned H.264 that the
    /// compositor handles without complaint.
    /// </remarks>
    public static class PerformanceAudio
    {
        public sealed class Result
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public int TracksMixed { get; init; }
        }

        /// <summary>
        /// Combine <paramref name="silentVideo"/> with the project's audio into <paramref name="output"/>.
        /// </summary>
        public static async Task<Result> MuxAsync(StorageFile silentVideo,
                                                  IEnumerable<TimelineTrack> tracks,
                                                  StorageFile output,
                                                  double canvasWidth, double canvasHeight)
        {
            var scratch = new List<StorageFile>();
            int mixed = 0;

            try
            {
                var composition = new MediaComposition();
                composition.Clips.Add(await MediaClip.CreateFromFileAsync(silentVideo));

                StorageFolder temp;
                try { temp = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetTempPath()); }
                catch (Exception ex) { return new Result { Message = "no temp folder: " + ex.Message }; }

                int n = 0;
                foreach (var track in tracks ?? Array.Empty<TimelineTrack>())
                {
                    if (track?.Clips == null) continue;

                    foreach (var op in track.Clips)
                    {
                        if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;
                        if (op.Volume <= 0) continue;               // muted contributes nothing
                        if (op.HasNoSourceWindow) continue;          // a still has no sound to take

                        // A sound-only source goes straight in. BackgroundAudioTrack takes it as it
                        // stands, and the extraction path cannot: MediaClip refuses an audio-only
                        // file outright. The two APIs are exactly complementary - each rejects what
                        // the other requires - so this branch is not an optimisation, it is the
                        // only way either kind of clip gets mixed at all.
                        StorageFile audioFile;
                        if (op.IsAudioOnly)
                        {
                            try { audioFile = await StorageFile.GetFileFromPathAsync(op.FilePath); }
                            catch { continue; }
                        }
                        else
                        {
                            audioFile = await ExtractAudioAsync(op, temp, n++);
                            if (audioFile == null) continue;         // no audio stream, or unreadable
                            scratch.Add(audioFile);
                        }

                        BackgroundAudioTrack audio;
                        try { audio = await BackgroundAudioTrack.CreateFromFileAsync(audioFile); }
                        catch { continue; }

                        // An extracted file arrives already trimmed. A sound-only source is the
                        // whole file, so its window still has to be applied here.
                        if (op.IsAudioOnly)
                        {
                            var st = op.VideoStartTime;
                            if (st > TimeSpan.Zero && st < audio.OriginalDuration)
                                audio.TrimTimeFromStart = st;

                            var en = op.VideoEndTime > TimeSpan.Zero ? op.VideoEndTime : audio.OriginalDuration;
                            var fe = audio.OriginalDuration - en;
                            if (fe > TimeSpan.Zero && fe < audio.OriginalDuration)
                                audio.TrimTimeFromEnd = fe;
                        }

                        audio.Delay = op.StartTime < TimeSpan.Zero ? TimeSpan.Zero : op.StartTime;
                        audio.Volume = Math.Clamp(op.Volume, 0, 1);

                        composition.BackgroundAudioTracks.Add(audio);
                        mixed++;
                    }
                }

                if (mixed == 0)
                    return new Result { Message = "no audible clips", TracksMixed = 0 };

                double h = Math.Max(2, Math.Round(canvasHeight / 2) * 2);
                var profile = MediaEncodingProfile.CreateMp4(
                    h >= 2000 ? VideoEncodingQuality.Uhd2160p :
                    h >= 1000 ? VideoEncodingQuality.HD1080p : VideoEncodingQuality.HD720p);

                var reason = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Fast, profile);
                return reason == TranscodeFailureReason.None
                    ? new Result { Success = true, Message = output.Path, TracksMixed = mixed }
                    : new Result { Message = reason.ToString(), TracksMixed = mixed };
            }
            catch (Exception ex)
            {
                return new Result { Message = ex.Message, TracksMixed = mixed };
            }
            finally
            {
                foreach (var f in scratch)
                {
                    try { await f.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                }
            }
        }

        /// <summary>
        /// Render one clip's trimmed audio to a small M4A. Null when the source has nothing usable.
        /// </summary>
        private static async Task<StorageFile> ExtractAudioAsync(CinematicOperation op, StorageFolder temp, int index)
        {
            try
            {
                var src = await StorageFile.GetFileFromPathAsync(op.FilePath);
                var clip = await MediaClip.CreateFromFileAsync(src);

                // The same source window the picture used, so sound and picture agree.
                var start = op.VideoStartTime;
                if (start > TimeSpan.Zero && start < clip.OriginalDuration)
                    clip.TrimTimeFromStart = start;

                var end = op.VideoEndTime > TimeSpan.Zero ? op.VideoEndTime : clip.OriginalDuration;
                var fromEnd = clip.OriginalDuration - end;
                if (fromEnd > TimeSpan.Zero && fromEnd < clip.OriginalDuration)
                    clip.TrimTimeFromEnd = fromEnd;

                if (clip.TrimmedDuration <= TimeSpan.Zero) return null;

                var comp = new MediaComposition();
                comp.Clips.Add(clip);

                var file = await temp.CreateFileAsync("vd-audio-" + index + ".m4a",
                                                      CreationCollisionOption.ReplaceExisting);

                var reason = await comp.RenderToFileAsync(
                    file, MediaTrimmingPreference.Fast, MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium));

                if (reason != TranscodeFailureReason.None) return null;
                return file;
            }
            catch
            {
                return null;
            }
        }
    }
}
