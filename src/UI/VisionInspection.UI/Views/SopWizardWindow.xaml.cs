using System.Windows;
using VisionInspection.UI.ViewModels;

namespace VisionInspection.UI.Views;

/// <summary>
/// SOP 生成向导窗口：让不懂 YAML 的用户通过"选模板 + 填参数"生成可直接运行的 SOP 流程文件。
/// </summary>
public partial class SopWizardWindow : Window
{
    public SopWizardWindow()
    {
        InitializeComponent();
        DataContext = new SopWizardViewModel();
    }
}
