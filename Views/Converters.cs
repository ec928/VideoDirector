using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Linq;

namespace VideoDirector.Views
{
    public class EmptyToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int count)
            {
                return count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class TimeSpanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TimeSpan ts)
            {
                // Formats to hh:mm:ss.ff, stripping trailing zeros from decimals
                return ts.ToString(@"hh\:mm\:ss\.ff");
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string s && TimeSpan.TryParse(s, out TimeSpan ts))
            {
                return ts;
            }
            return TimeSpan.Zero;
        }
    }

    public class TimeSpanToDoubleSecondsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TimeSpan ts)
            {
                return ts.TotalSeconds;
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is double d && !double.IsNaN(d))
            {
                return TimeSpan.FromSeconds(d);
            }
            return TimeSpan.Zero;
        }
    }

    public class BoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool bValue = value is bool b && b;
            if (parameter?.ToString() == "Reverse")
            {
                bValue = !bValue;
            }
            return bValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            bool bValue = value is Visibility v && v == Visibility.Visible;
            if (parameter?.ToString() == "Reverse")
            {
                bValue = !bValue;
            }
            return bValue;
        }
    }

    public class FloatFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is float f)
            {
                return f.ToString("0.##");
            }
            if (value is double d)
            {
                return d.ToString("0.##");
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class NullToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // Mode-badge background: accent-tinted while editing (draws the eye to the active Edit
    // context), neutral translucent otherwise. Keeps the badge read-only but state-expressive.
    public class BoolToBadgeBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isEdit = value is bool b && b;
            if (isEdit
                && Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accent)
                && accent is Microsoft.UI.Xaml.Media.Brush accentBrush)
            {
                return accentBrush;
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value != null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // Converts ModeLabel ("EDIT", "PLAYBACK"/"PLAY", "ARRANGE") into vibrant foreground colors
    public class ModeLabelToForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string mode = (value as string)?.ToUpperInvariant() ?? "ARRANGE";
            if (mode == "EDIT")
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 255, 82, 82)); // Red
            if (mode == "PLAYBACK" || mode == "PLAY")
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 76, 175, 80)); // Green
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 188, 212)); // Cyan
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // Converts ModeLabel ("EDIT", "PLAYBACK"/"PLAY", "ARRANGE") into translucent background badge tints
    public class ModeLabelToBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string mode = (value as string)?.ToUpperInvariant() ?? "ARRANGE";
            if (mode == "EDIT")
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 255, 82, 82)); // Red tint
            if (mode == "PLAYBACK" || mode == "PLAY")
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 76, 175, 80)); // Green tint
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 0, 188, 212)); // Cyan tint
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class TimeSpanToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TimeSpan ts)
            {
                // Format as MM:SS.f (e.g. 01:23.4)
                return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
            }
            return "00:00.0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                if (TimeSpan.TryParse(s, out TimeSpan result))
                    return result;

                // Try fallback format by prepending "00:" to parse MM:SS.f
                if (s.Count(c => c == ':') == 1 && TimeSpan.TryParse("00:" + s, out TimeSpan fallbackResult))
                    return fallbackResult;
            }
            return TimeSpan.Zero;
        }
    }
}
