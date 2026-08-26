using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace VideoDirector.Views
{
    public static class NumberBoxExtensions
    {
        public static readonly DependencyProperty FractionDigitsProperty =
            DependencyProperty.RegisterAttached("FractionDigits", typeof(int), typeof(NumberBoxExtensions), new PropertyMetadata(0, OnFractionDigitsChanged));

        public static int GetFractionDigits(DependencyObject obj)
        {
            return (int)obj.GetValue(FractionDigitsProperty);
        }

        public static void SetFractionDigits(DependencyObject obj, int value)
        {
            obj.SetValue(FractionDigitsProperty, value);
        }

        private static void OnFractionDigitsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is NumberBox nb && e.NewValue is int digits)
            {
                var formatter = new DecimalFormatter
                {
                    FractionDigits = digits,
                    NumberRounder = new IncrementNumberRounder
                    {
                        Increment = Math.Pow(10, -digits),
                        RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp
                    }
                };
                nb.NumberFormatter = formatter;
            }
        }
    }
}
