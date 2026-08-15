using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VisionInspection.UI.Converters;

/// <summary>
/// bool -> 画刷：true 浅绿（通过），false 浅灰（未通过/未生成）。单例供 XAML 直接引用。
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Green : Grey;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
