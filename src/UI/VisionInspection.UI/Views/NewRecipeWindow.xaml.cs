using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// 新建产品配方对话框：输入产品名称并选择模板（空白 / 现有配方），
    /// 由 SOPModuleView 负责按结果生成 YAML 文件。
    /// </summary>
    public partial class NewRecipeWindow : Window
    {
        /// <summary>新建的产品名称</summary>
        public string ProductName { get; private set; } = "";

        /// <summary>所选模板的完整路径；空字符串表示“空白模板”</summary>
        public string TemplatePath { get; private set; } = "";

        public NewRecipeWindow(IEnumerable<RecipeItem> templates)
        {
            InitializeComponent();

            // 第一项固定为空白模板
            TemplateBox.Items.Add(new RecipeItem
            {
                DisplayName = "📄 空白模板（最小骨架）",
                FullPath = ""
            });

            foreach (var t in templates)
                TemplateBox.Items.Add(t);

            TemplateBox.SelectedIndex = 0;
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ProductName = (NameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(ProductName))
            {
                MessageBox.Show("请输入产品名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sel = TemplateBox.SelectedItem as RecipeItem;
            TemplatePath = sel?.FullPath ?? "";
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
