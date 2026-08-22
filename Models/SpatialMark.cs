namespace VideoDirector.Models
{
    // One framing keyframe: how far the content is zoomed, and where it sits.
    //
    // COORDINATE SPACE — X and Y are FRACTIONS of the video's fit rectangle (the size the video
    // occupies in the player pane at Scale 1), NOT pixels. X = 0.25 means "a quarter of the fit
    // width to the right", whatever size the window happens to be.
    //
    // They used to be raw pane pixels, which tied a mark to the pane size it was captured at:
    // resize the window between framing a clip and playing it and the framing moved. Projects
    // written before that change carry SchemaVersion 0 and are converted on first draw — see
    // VideoPlaybackEngine.EnsureMarksNormalized.
    //
    // Scale needs no such treatment: it is already a pure ratio.
    public class SpatialMark : ObservableObject
    {
        private float _scale = 1.0f;
        public float Scale
        {
            get => _scale;
            set => SetProperty(ref _scale, value);
        }

        private float _x = 0.0f;
        public float X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        private float _y = 0.0f;
        public float Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        public SpatialMark() { }

        public SpatialMark(float scale, float x, float y)
        {
            Scale = scale;
            X = x;
            Y = y;
        }
    }
}
