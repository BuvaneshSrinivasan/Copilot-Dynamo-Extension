using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamoCopilot.Extension.Converters
{
    /// <summary>
    /// Converts bool to Visibility, inverted:
    ///   true  → Collapsed
    ///   false → Visible
    /// Used to show the login panel when IsLoggedIn = false.
    /// </summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v != Visibility.Visible;
    }
}
