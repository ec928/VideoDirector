namespace VideoDirector.Models
{
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
