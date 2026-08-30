using System.Windows;
using VisionInspection.UI.ViewModels;

namespace VisionInspection.UI.Views;

/// <summary>
/// SOP 生成向导窗口：让不懂 YAML 的用户通过"选模板 + 填参数"生成可直接运行的 SOP 流程文件。
/// </summary>
public partial class SopWizardWindow : Window
{
    /// <summary>是否保存过新配方（供调用方刷新产品配方下拉）</summary>
    public bool RecipeChanged { get; private set; }

    public SopWizardWindow()
    {
        InitializeComponent();
        var vm = new SopWizardViewModel();
        vm.RecipeSaved += () => RecipeChanged = true;
        DataContext = vm;
    }
}
