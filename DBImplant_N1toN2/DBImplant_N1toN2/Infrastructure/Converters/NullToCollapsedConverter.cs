using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DBImplant_N1toN2.Infrastructure.Converters
{
    /// <summary>
    /// null → Collapsed, 비어있는 문자열 → Collapsed, 그 외 → Visible
    /// XAML: Converter={StaticResource NullToCollapsedConverter}
    /// </summary>
    [ValueConversion(typeof(object), typeof(Visibility))]
    public sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null) return Visibility.Collapsed;
            if (value is string s && string.IsNullOrEmpty(s)) return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
