using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VisionInspection.Modules.SOP.Wizard;

namespace VisionInspection.UI.Converters;

/// <summary>
/// 步骤面板可见性：当绑定的 CurrentStep 等于参数指定的步骤号时显示
/// 用法：Visibility="{Binding CurrentStep, Converter={StaticResource EqualVisibility}, ConverterParameter=2}"
/// </summary>
public class EqualVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int cur && parameter is string p && int.TryParse(p, out var target))
            return cur == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 步骤面板可见性（反向）：当绑定的 CurrentStep 不等于参数指定的步骤号时显示
/// </summary>
public class NotEqualVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int cur && parameter is string p && int.TryParse(p, out var target))
            return cur == target ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 参数类型可见性：当绑定的 SopParamKind 等于参数指定的种类时显示
/// 用法：Visibility="{Binding Spec.Kind, Converter={StaticResource KindVisibility}, ConverterParameter=Region}"
/// </summary>
public class KindVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SopParamKind kind && parameter is string p &&
            Enum.TryParse<SopParamKind>(p, ignoreCase: true, out var target))
        {
            return kind == target ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
