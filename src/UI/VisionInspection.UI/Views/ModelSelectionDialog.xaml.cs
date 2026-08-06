using System.Windows;
using System.Windows.Controls;
using VisionInspection.Core.Services;
using VisionInspection.UI.Services;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// ModelSelectionDialog.xaml 的交互逻辑
    /// </summary>
    public partial class ModelSelectionDialog : Window
    {
        private readonly ModelManager _modelManager;
        private ModelInfo? _selectedModel;

        /// <summary>
        /// 选中的模型
        /// </summary>
        public ModelInfo? SelectedModel => _selectedModel;

        public ModelSelectionDialog()
        {
            InitializeComponent();
            _modelManager = new ModelManager();
            LoadModels();
        }

        /// <summary>
        /// 加载模型列表（若数据库为空则自动扫描默认目录并导入，恢复开箱即用）
        /// </summary>
        private async void LoadModels()
        {
            try
            {
                // 数据库为空时自动扫描并导入，避免"找不到模型"的空列表
                var existing = await _modelManager.GetAllModelsAsync();
                if (existing.Count == 0)
                {
                    try
                    {
                        await _modelManager.EnsureDatabaseSeededAsync();
                    }
                    catch (Exception seedEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"自动导入模型失败: {seedEx.Message}");
                    }
                }

                var models = await _modelManager.GetAllModelsAsync();
                ModelsDataGrid.ItemsSource = models;

                if (models.Count == 0)
                {
                    MessageBox.Show("数据库中没有模型，请先在「模型管理」中扫描并导入模型", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载模型列表失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 刷新按钮点击
        /// </summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadModels();
        }

        /// <summary>
        /// 选择按钮点击
        /// </summary>
        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModel();
        }

        /// <summary>
        /// 取消按钮点击
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 双击选择模型
        /// </summary>
        private void ModelsDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectModel();
        }

        /// <summary>
        /// 选择当前选中的模型
        /// </summary>
        private void SelectModel()
        {
            _selectedModel = ModelsDataGrid.SelectedItem as ModelInfo;
            if (_selectedModel == null)
            {
                MessageBox.Show("请先选择一个模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}
