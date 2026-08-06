using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VisionInspection.UI.Converters
{
    /// <summary>
    /// 对象到可见性转换器（反转语义）：对象为 null / 空字符串 -> Visible，否则 -> Collapsed。
    /// 用途：当没有图像时显示"点击加载图像"提示，有图像时隐藏提示。
    /// </summary>
    public class ObjectToVisibilityReConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasValue = value != null;
            if (value is string s)
            {
                hasValue = !string.IsNullOrEmpty(s);
            }

            // 反转语义：有值 -> 隐藏提示，无值 -> 显示提示
            return hasValue ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
