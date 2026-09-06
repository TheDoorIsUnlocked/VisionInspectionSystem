using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VisionInspection.UI.Converters
{
    /// <summary>
    /// 数量到可见性转换器：数量为 0 时显示提示（Visible），大于 0 时隐藏（Collapsed）。
    /// 用于多相机画面为空时的"连接相机后显示实时画面"提示。
    /// </summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = value switch
            {
                int i => i,
                System.Collections.ICollection c => c.Count,
                _ => 0
            };
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
