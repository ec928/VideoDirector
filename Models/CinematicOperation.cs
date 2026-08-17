using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace VideoDirector.Models
{
    public class CinematicOperation : ObservableObject
    {
        private string _filePath = string.Empty;
        public string FilePath
        {
            get => _filePath;
            set
            {
                if (SetProperty(ref _filePath, value))
                {
                    OnPropertyChanged(nameof(FileName));
                }
            }
        }

        public string FileName => System.IO.Path.GetFileName(_filePath);

        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        // A clip behaves as a still when it's an image file OR its playback speed is 0 (frozen
        // frame). Everything else is a video whose end is measured from the decode position.
        // Do NOT gate video detection on a hardcoded video-extension whitelist: any container we
        // can't positively identify as an image is a video (.mkv, .avi, .mov, … all count), or a
        // trimmed clip in an unlisted format silently falls through to still handling and overruns.
        [JsonIgnore]
        public bool IsStill
        {
            get
            {
                if (_playbackSpeed <= 0) return true;
                if (string.IsNullOrWhiteSpace(_filePath)) return true;
                var ext = System.IO.Path.GetExtension(_filePath);
                return Array.Exists(ImageExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
            }
        }

        private BitmapImage? _thumbnail;
        [JsonIgnore]
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }

        [JsonIgnore]
        public bool HasModifications => 
            StartMark.Scale != 1.0f || StartMark.X != 0 || StartMark.Y != 0 ||
            EndMark.Scale != 1.0f || EndMark.X != 0 || EndMark.Y != 0 ||
            PlaybackSpeed != 1.0 ||
            TransitionDuration > TimeSpan.Zero ||
            TransitionStyle != TransitionStyle.HardSnap ||
            CurveProfile != CurveProfile.Linear ||
            VideoStartTime > TimeSpan.Zero ||
            (VideoEndTime > TimeSpan.Zero && VideoEndTime != OpDuration);

        private bool _isPlaying;
        [JsonIgnore]
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        private bool _isUpdatingTiming = false;
        private const double MinClipSeconds = 0.1;

        // The trim window and timeline duration are kept mutually consistent at all times by the
        // setters below — you can never leave the model in a state where In >= Out yet Duration
        // claims a length the clip doesn't have (the bug that let a clip render a full block on the
        // timeline but end instantly on playback). The single invariant enforced everywhere:
        //   0 <= VideoStartTime  <  VideoEndTime <= SourceDuration   (>= MinClipSeconds apart)
        //   OpDuration == (VideoEndTime - VideoStartTime) / PlaybackSpeed   (for a video)
        // A still (PlaybackSpeed == 0) has no advancing source window, so its OpDuration is an
        // independent hold time.

        private TimeSpan _videoStartTime = TimeSpan.Zero;
        public TimeSpan VideoStartTime
        {
            get => _videoStartTime;
            set
            {
                if (_isUpdatingTiming) { SetProperty(ref _videoStartTime, value); return; }
                _isUpdatingTiming = true;
                try { NormalizeTrim(value.TotalSeconds, _videoEndTime.TotalSeconds, changedStart: true); }
                finally { _isUpdatingTiming = false; }
            }
        }

        private TimeSpan _videoEndTime = TimeSpan.Zero;
        public TimeSpan VideoEndTime
        {
            get => _videoEndTime;
            set
            {
                if (_isUpdatingTiming) { SetProperty(ref _videoEndTime, value); return; }
                _isUpdatingTiming = true;
                try { NormalizeTrim(_videoStartTime.TotalSeconds, value.TotalSeconds, changedStart: false); }
                finally { _isUpdatingTiming = false; }
            }
        }

        // Full length of the source file, captured when the clip is added. The trim In/Out points
        // (VideoStartTime/VideoEndTime) live within [0, SourceDuration]. Not the timeline duration
        // (that's OpDuration = trimmed length / speed).
        private TimeSpan _sourceDuration;
        public TimeSpan SourceDuration
        {
            get => _sourceDuration;
            set
            {
                if (SetProperty(ref _sourceDuration, value))
                {
                    OnPropertyChanged(nameof(SourceDurationSeconds));
                    // Learning the true source length (e.g. backfilled after the media opens) can
                    // retroactively invalidate a trim; re-clamp so the window stays inside it.
                    if (!_isUpdatingTiming)
                    {
                        _isUpdatingTiming = true;
                        try { NormalizeTrim(_videoStartTime.TotalSeconds, _videoEndTime.TotalSeconds, changedStart: false); }
                        finally { _isUpdatingTiming = false; }
                    }
                }
            }
        }

        [JsonIgnore]
        public double SourceDurationSeconds => _sourceDuration.TotalSeconds;

        private TimeSpan _opDuration = TimeSpan.Zero;
        public TimeSpan OpDuration
        {
            get => _opDuration;
            set
            {
                if (_isUpdatingTiming) { SetProperty(ref _opDuration, value); return; }
                _isUpdatingTiming = true;
                try { ApplyDurationEdit(value.TotalSeconds); }
                finally { _isUpdatingTiming = false; }
            }
        }

        private double _playbackSpeed = 1.0;
        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                if (SetProperty(ref _playbackSpeed, value < 0 ? 0 : value))
                {
                    if (!_isUpdatingTiming)
                    {
                        _isUpdatingTiming = true;
                        try { RecomputeOpDurationFromTrim(); }
                        finally { _isUpdatingTiming = false; }
                    }
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        // Enforces the trim invariant. Whichever endpoint the user just changed is honoured; the
        // other yields if they would cross (or come within MinClipSeconds). Then OpDuration is
        // recomputed from the real window so the timeline block can never lie about its length.
        private void NormalizeTrim(double startSec, double endSec, bool changedStart)
        {
            double src = _sourceDuration.TotalSeconds > 0 ? _sourceDuration.TotalSeconds : double.PositiveInfinity;
            double start = Math.Clamp(startSec, 0, src);
            double end = Math.Clamp(endSec, 0, src);

            if (end - start < MinClipSeconds)
            {
                if (changedStart)
                {
                    end = Math.Min(src, start + MinClipSeconds);
                    start = Math.Max(0, end - MinClipSeconds);
                }
                else
                {
                    start = Math.Max(0, end - MinClipSeconds);
                    end = Math.Min(src, start + MinClipSeconds);
                }
            }

            if (Math.Abs(start - _videoStartTime.TotalSeconds) > 1e-9)
            {
                _videoStartTime = TimeSpan.FromSeconds(start);
                OnPropertyChanged(nameof(VideoStartTime));
            }
            if (Math.Abs(end - _videoEndTime.TotalSeconds) > 1e-9)
            {
                _videoEndTime = TimeSpan.FromSeconds(end);
                OnPropertyChanged(nameof(VideoEndTime));
            }
            RecomputeOpDurationFromTrim();
            OnPropertyChanged(nameof(HasModifications));
        }

        private void RecomputeOpDurationFromTrim()
        {
            if (_playbackSpeed <= 0) return; // still: OpDuration is an independent hold time
            double dur = (_videoEndTime - _videoStartTime).TotalSeconds / _playbackSpeed;
            if (dur < MinClipSeconds) dur = MinClipSeconds;
            if (Math.Abs(dur - _opDuration.TotalSeconds) > 1e-9)
            {
                _opDuration = TimeSpan.FromSeconds(dur);
                OnPropertyChanged(nameof(OpDuration));
            }
        }

        // Editing the timeline Duration re-trims the OUT point (In and Speed fixed): "make this
        // 10s long" pulls exactly 10s x speed of source from the In point. This is what makes a
        // precise segment extractable from a very long source by typing a number. For a still
        // (speed 0) there is no source window, so Duration is set directly as the hold time.
        private void ApplyDurationEdit(double durationSec)
        {
            double d = Math.Max(MinClipSeconds, durationSec);

            if (_playbackSpeed <= 0)
            {
                if (Math.Abs(d - _opDuration.TotalSeconds) > 1e-9)
                {
                    _opDuration = TimeSpan.FromSeconds(d);
                    OnPropertyChanged(nameof(OpDuration));
                }
                OnPropertyChanged(nameof(HasModifications));
                return;
            }

            double src = _sourceDuration.TotalSeconds > 0 ? _sourceDuration.TotalSeconds : double.PositiveInfinity;
            double desiredEnd = _videoStartTime.TotalSeconds + d * _playbackSpeed;
            double end = Math.Clamp(desiredEnd, _videoStartTime.TotalSeconds + MinClipSeconds, src);
            if (Math.Abs(end - _videoEndTime.TotalSeconds) > 1e-9)
            {
                _videoEndTime = TimeSpan.FromSeconds(end);
                OnPropertyChanged(nameof(VideoEndTime));
            }
            // Reflect the ACTUAL (possibly source-capped) window, keeping the displayed number honest.
            double actual = (end - _videoStartTime.TotalSeconds) / _playbackSpeed;
            if (actual < MinClipSeconds) actual = MinClipSeconds;
            if (Math.Abs(actual - _opDuration.TotalSeconds) > 1e-9)
            {
                _opDuration = TimeSpan.FromSeconds(actual);
                OnPropertyChanged(nameof(OpDuration));
            }
            OnPropertyChanged(nameof(HasModifications));
        }

        // --- Upper-track (Track 2/3) clip properties ---
        // Track 1 ignores these (its timeline position is computed sequentially). Upper
        // tracks use StartTime as the editable master-timeline placement and Opacity for
        // compositing. Upper-track clips are the same CinematicOperation type as Track 1.

        // Editable placement on the master timeline. Display-only/computed for Track 1;
        // the freely-editable start position for Track 2/3 (gaps allowed).
        private TimeSpan _startTime = TimeSpan.Zero;
        public TimeSpan StartTime
        {
            get => _startTime;
            set
            {
                if (SetProperty(ref _startTime, value))
                {
                    OnPropertyChanged(nameof(StartTimeSeconds));
                }
            }
        }

        [JsonIgnore]
        public double StartTimeSeconds
        {
            get => _startTime.TotalSeconds;
            set => StartTime = TimeSpan.FromSeconds(value);
        }

        [JsonIgnore]
        public string StartTimeFormatted => FormatTime(_startTime);
        [JsonIgnore]
        public string SourceDurationFormatted => FormatTime(_sourceDuration);
        [JsonIgnore]
        public string OpDurationFormatted => FormatTime(_opDuration);
        [JsonIgnore]
        public string VideoStartTimeFormatted => FormatTime(_videoStartTime);
        [JsonIgnore]
        public string VideoEndTimeFormatted => FormatTime(_videoEndTime);

        private static string FormatTime(TimeSpan t)
        {
            int h = (int)t.TotalHours;
            int m = t.Minutes;
            double s = t.Seconds + t.Milliseconds / 1000.0;
            return h > 0 ? $"{h:00}:{m:00}:{s:00.0}" : $"{m:00}:{s:00.0}";
        }

        protected override void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(StartTime) || propertyName == nameof(StartTimeSeconds))
                base.OnPropertyChanged(nameof(StartTimeFormatted));
            else if (propertyName == nameof(SourceDuration) || propertyName == nameof(SourceDurationSeconds))
                base.OnPropertyChanged(nameof(SourceDurationFormatted));
            else if (propertyName == nameof(OpDuration))
                base.OnPropertyChanged(nameof(OpDurationFormatted));
            else if (propertyName == nameof(VideoStartTime))
                base.OnPropertyChanged(nameof(VideoStartTimeFormatted));
            else if (propertyName == nameof(VideoEndTime))
                base.OnPropertyChanged(nameof(VideoEndTimeFormatted));
        }

        // Compositing opacity when this clip sits on an upper track. 1 = opaque.
        private float _opacity = 1.0f;
        public float Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0f, 1f));
        }

        // Audio level for this clip, 0 (mute) .. 1 (full). Applied in both the live preview and the
        // export. Track 1 defaults to full (the main narrative); overlays are added muted so stacked
        // PiPs don't fight the main track — raise this to let a PiP's audio through.
        private double _volume = 1.0;
        public double Volume
        {
            get => _volume;
            set => SetProperty(ref _volume, Math.Clamp(value, 0.0, 1.0));
        }

        private BorderType _borderType = BorderType.None;
        public BorderType BorderType
        {
            get => _borderType;
            set => SetProperty(ref _borderType, value);
        }

        private Windows.UI.Color _borderColor = Microsoft.UI.Colors.White;
        public Windows.UI.Color BorderColor
        {
            get => _borderColor;
            set => SetProperty(ref _borderColor, value);
        }

        private double _borderThickness = 4.0;
        public double BorderThickness
        {
            get => _borderThickness;
            set => SetProperty(ref _borderThickness, Math.Clamp(value, 0.0, 50.0));
        }

        // End of this clip's window on the master timeline (upper tracks).
        [JsonIgnore]
        public TimeSpan EndTimeOnTimeline => _startTime + _opDuration;

        // True if this clip is visible at the given master-timeline position (upper tracks).
        public bool IsActiveAt(TimeSpan storyTime) => storyTime >= _startTime && storyTime < EndTimeOnTimeline;

        // --- Placement (upper-track PiP box) ---
        // Where and how big the clip appears in the composite, INDEPENDENT of its content
        // framing (marks). Track 1 ignores placement (it is always full-frame). This is what
        // lets a clip be framed full-screen while editing but shown as a corner PiP at playback.

        // The SOURCE video's natural aspect (width/height), captured when the clip is added. The
        // PiP box is shaped from this. Do NOT infer it from the thumbnail: ThumbnailMode.VideosView
        // returns a letterboxed 16:9 image even for portrait video, which makes every box landscape
        // and crops the subject. 0 = not yet known (backfilled once the media opens).
        private double _sourceAspect;
        public double SourceAspect
        {
            get => _sourceAspect;
            set => SetProperty(ref _sourceAspect, value);
        }

        // Box size as INDEPENDENT fractions of the video's viewport-fit size (0.3 = 30%).
        // Width and Height are decoupled so the PiP box can be reshaped to any aspect; the
        // video content crop-fills the box (UniformToFill + clip) so it never distorts.
        // Default 0.3 x 0.3 reproduces the old aspect-locked 30% corner PiP exactly.
        private double _placementWidth = 0.3;
        public double PlacementWidth
        {
            get => _placementWidth;
            set => SetProperty(ref _placementWidth, Math.Clamp(value, 0.05, 1.0));
        }

        private double _placementHeight = 0.3;
        public double PlacementHeight
        {
            get => _placementHeight;
            set => SetProperty(ref _placementHeight, Math.Clamp(value, 0.05, 1.0));
        }

        // Box centre as a fraction of the viewport (0.5,0.5 = centre). Default lower-right.
        private double _placementCenterX = 0.72;
        public double PlacementCenterX
        {
            get => _placementCenterX;
            set => SetProperty(ref _placementCenterX, Math.Clamp(value, 0.0, 1.0));
        }

        private double _placementCenterY = 0.72;
        public double PlacementCenterY
        {
            get => _placementCenterY;
            set => SetProperty(ref _placementCenterY, Math.Clamp(value, 0.0, 1.0));
        }

        private SpatialMark _startMark = new();
        public SpatialMark StartMark
        {
            get => _startMark;
            set
            {
                if (_startMark != null) _startMark.PropertyChanged -= Mark_PropertyChanged;
                if (SetProperty(ref _startMark, value) && _startMark != null)
                {
                    _startMark.PropertyChanged += Mark_PropertyChanged;
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private SpatialMark? _midMark;
        public SpatialMark? MidMark
        {
            get => _midMark;
            set
            {
                if (_midMark != null) _midMark.PropertyChanged -= Mark_PropertyChanged;
                if (SetProperty(ref _midMark, value) && _midMark != null)
                {
                    _midMark.PropertyChanged += Mark_PropertyChanged;
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private SpatialMark _endMark = new();
        public SpatialMark EndMark
        {
            get => _endMark;
            set
            {
                if (_endMark != null) _endMark.PropertyChanged -= Mark_PropertyChanged;
                if (SetProperty(ref _endMark, value) && _endMark != null)
                {
                    _endMark.PropertyChanged += Mark_PropertyChanged;
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        public CinematicOperation()
        {
            _startMark.PropertyChanged += Mark_PropertyChanged;
            _endMark.PropertyChanged += Mark_PropertyChanged;
        }

        public void Reset()
        {
            StartMark.Scale = 1.0f;
            StartMark.X = 0;
            StartMark.Y = 0;
            
            if (MidMark != null)
            {
                MidMark.Scale = 1.0f;
                MidMark.X = 0;
                MidMark.Y = 0;
            }
            MidMark = null;

            EndMark.Scale = 1.0f;
            EndMark.X = 0;
            EndMark.Y = 0;

            PlaybackSpeed = 1.0;
            TransitionDuration = TimeSpan.Zero;
            TransitionStyle = TransitionStyle.HardSnap;
            CurveProfile = CurveProfile.Linear;
            VideoStartTime = TimeSpan.Zero;
            
            // Revert duration to match the full clip duration
            if (_videoEndTime > TimeSpan.Zero)
            {
                OpDuration = _videoEndTime;
            }
            
            RecordedPath.Clear();
            OnPropertyChanged(nameof(HasModifications));
        }

        private void Mark_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasModifications));
        }

        private List<TransformKeyframe> _recordedPath = new();
        public List<TransformKeyframe> RecordedPath
        {
            get => _recordedPath;
            set => SetProperty(ref _recordedPath, value);
        }

        private CurveProfile _curveProfile = CurveProfile.Linear;
        public CurveProfile CurveProfile
        {
            get => _curveProfile;
            set
            {
                if (SetProperty(ref _curveProfile, value))
                {
                    OnPropertyChanged(nameof(CurveProfileIndex));
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public int CurveProfileIndex
        {
            get => (int)_curveProfile;
            set
            {
                if (Enum.IsDefined(typeof(CurveProfile), value))
                {
                    CurveProfile = (CurveProfile)value;
                }
            }
        }

        private TimeSpan _transitionDuration = TimeSpan.Zero;
        public TimeSpan TransitionDuration
        {
            get => _transitionDuration;
            set 
            {
                if (SetProperty(ref _transitionDuration, value))
                {
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private TransitionStyle _transitionStyle = TransitionStyle.HardSnap;
        public TransitionStyle TransitionStyle
        {
            get => _transitionStyle;
            set
            {
                if (SetProperty(ref _transitionStyle, value))
                {
                    // HardSnap is an instant cut — it has no duration. Force it to 0 and lock it
                    // (the UI disables the field via TransitionEditable).
                    if (_transitionStyle == TransitionStyle.HardSnap) TransitionDuration = TimeSpan.Zero;
                    OnPropertyChanged(nameof(TransitionStyleIndex));
                    OnPropertyChanged(nameof(TransitionIconGlyph));
                    OnPropertyChanged(nameof(TransitionIconTooltip));
                    OnPropertyChanged(nameof(TransitionEditable));
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        // A HardSnap cut has a fixed 0 length; every other style has an editable duration.
        [JsonIgnore]
        public bool TransitionEditable => _transitionStyle != TransitionStyle.HardSnap;

        public int TransitionStyleIndex
        {
            get => (int)_transitionStyle;
            set
            {
                if (Enum.IsDefined(typeof(TransitionStyle), value))
                {
                    TransitionStyle = (TransitionStyle)value;
                }
            }
        }

        public string TransitionIconGlyph
        {
            get
            {
                return _transitionStyle switch
                {
                    TransitionStyle.Crossfade => "\uE88E", // Half-filled circle/star
                    TransitionStyle.CinematicBridge => "\uE811", // Bridge/Merge
                    TransitionStyle.DipToColor => "\uE790", // Color bucket
                    _ => "\uE8C6" // Cut/Scissors
                };
            }
        }

        public string TransitionIconTooltip => $"Transition Out: {_transitionStyle}";
    }
}
