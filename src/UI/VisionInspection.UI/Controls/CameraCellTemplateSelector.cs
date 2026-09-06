using System.Windows;
using System.Windows.Controls;
using VisionInspection.UI.ViewModels;

namespace VisionInspection.UI.Controls
{
    /// <summary>
    /// 多相机画面模板选择器：主相机使用 ROI 编辑控件（可标定），其余相机使用预览控件
    /// </summary>
    public class CameraCellTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? PrimaryTemplate { get; set; }
        public DataTemplate? SecondaryTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is CameraViewItem view && !view.IsPrimary)
            {
                return SecondaryTemplate ?? base.SelectTemplate(item, container);
            }
            return PrimaryTemplate ?? base.SelectTemplate(item, container);
        }
    }
}
