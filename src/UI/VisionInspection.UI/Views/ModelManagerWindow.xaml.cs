using Microsoft.Win32;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VisionInspection.Core.Services;
using VisionInspection.Modules.Detection;
using VisionInspection.UI.Services;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// ModelManagerWindow.xaml 的交互逻辑
    /// </summary>
    public partial class ModelManagerWindow : Window
    {
        private readonly ModelManager _modelManager;
        private readonly YoloDetectionService _detectionService;
        private ModelInfo? _selectedModel;

        public ModelManagerWindow()
        {
            InitializeComponent();
            _modelManager = new ModelManager();
            _detectionService = new YoloDetectionService();

            // 绑定滑块事件
            ConfidenceSlider.ValueChanged += (s, e) =>
                ConfidenceValueText.Text = ConfidenceSlider.Value.ToString("F2");
            IouSlider.ValueChanged += (s, e) =>
                IouValueText.Text = IouSlider.Value.ToString("F2");
            SmoothEmaSlider.ValueChanged += (s, e) =>
                SmoothEmaValueText.Text = SmoothEmaSlider.Value.ToString("F2");
            SmoothConfirmHitsSlider.ValueChanged += (s, e) =>
                SmoothConfirmHitsValueText.Text = SmoothConfirmHitsSlider.Value.ToString("F0");
            SmoothMaxMissedSlider.ValueChanged += (s, e) =>
                SmoothMaxMissedValueText.Text = SmoothMaxMissedSlider.Value.ToString("F0");
            SmoothIouSlider.ValueChanged += (s, e) =>
                SmoothIouValueText.Text = SmoothIouSlider.Value.ToString("F2");

            LoadModels();
        }

        private async void LoadModels()
        {
            var models = await _modelManager.GetAllModelsAsync();
            ModelsDataGrid.ItemsSource = models;
        }

        private void ModelsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedModel = ModelsDataGrid.SelectedItem as ModelInfo;
            if (_selectedModel != null)
            {
                DisplayModelDetails(_selectedModel);
            }
        }

        private void DisplayModelDetails(ModelInfo model)
        {
            ModelNameTextBox.Text = model.Name;
            ModelDescriptionTextBox.Text = model.Description;
            ModelPathTextBox.Text = model.ModelPath;
            ModelTypeComboBox.SelectedIndex = (int)model.Type;
            InputSizeTextBox.Text = model.InputSize.ToString();
            GpuIdTextBox.Text = model.GpuId.ToString();
            UseGpuCheckBox.IsChecked = model.UseGpu;
            ConfidenceSlider.Value = model.ConfidenceThreshold;
            IouSlider.Value = model.IouThreshold;
            SmoothEmaSlider.Value = model.SmoothEma;
            SmoothConfirmHitsSlider.Value = model.SmoothConfirmHits;
            SmoothMaxMissedSlider.Value = model.SmoothMaxMissed;
            SmoothIouSlider.Value = model.SmoothIouThreshold;
            ClassesTextBox.Text = string.Join(", ", model.Classes);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "ONNX模型文件|*.onnx|所有文件|*.*",
                Title = "选择YOLO模型文件"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ModelPathTextBox.Text = openFileDialog.FileName;
            }
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ModelNameTextBox.Text))
            {
                MessageBox.Show("请输入模型名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(ModelPathTextBox.Text) || !File.Exists(ModelPathTextBox.Text))
            {
                MessageBox.Show("请选择有效的模型文件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var model = new ModelInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = ModelNameTextBox.Text,
                Description = ModelDescriptionTextBox.Text,
                ModelPath = ModelPathTextBox.Text,
                Type = (ModelType)ModelTypeComboBox.SelectedIndex,
                InputSize = int.TryParse(InputSizeTextBox.Text, out var inputSize) ? inputSize : 640,
                GpuId = int.TryParse(GpuIdTextBox.Text, out var gpuId) ? gpuId : 0,
                UseGpu = UseGpuCheckBox.IsChecked ?? true,
                ConfidenceThreshold = (float)ConfidenceSlider.Value,
                IouThreshold = (float)IouSlider.Value,
                SmoothEma = (float)SmoothEmaSlider.Value,
                SmoothConfirmHits = (int)SmoothConfirmHitsSlider.Value,
                SmoothMaxMissed = (int)SmoothMaxMissedSlider.Value,
                SmoothIouThreshold = (float)SmoothIouSlider.Value
            };

            // 尝试加载模型获取类别信息
            try
            {
                if (await _detectionService.InitializeAsync(model))
                {
                    model.Classes = _detectionService.GetClasses();
                    ClassesTextBox.Text = string.Join(", ", model.Classes);
                    _detectionService.Dispose();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载模型获取类别信息失败: {ex.Message}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (await _modelManager.AddModelAsync(model))
            {
                MessageBox.Show("模型添加成功", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadModels();
                ClearForm();
            }
            else
            {
                MessageBox.Show("模型添加失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel == null)
            {
                MessageBox.Show("请先选择一个模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _selectedModel.Name = ModelNameTextBox.Text;
            _selectedModel.Description = ModelDescriptionTextBox.Text;
            _selectedModel.ModelPath = ModelPathTextBox.Text;
            _selectedModel.Type = (ModelType)ModelTypeComboBox.SelectedIndex;
            _selectedModel.InputSize = int.TryParse(InputSizeTextBox.Text, out var inputSize) ? inputSize : 640;
            _selectedModel.GpuId = int.TryParse(GpuIdTextBox.Text, out var gpuId) ? gpuId : 0;
            _selectedModel.UseGpu = UseGpuCheckBox.IsChecked ?? true;
            _selectedModel.ConfidenceThreshold = (float)ConfidenceSlider.Value;
            _selectedModel.IouThreshold = (float)IouSlider.Value;
            _selectedModel.SmoothEma = (float)SmoothEmaSlider.Value;
            _selectedModel.SmoothConfirmHits = (int)SmoothConfirmHitsSlider.Value;
            _selectedModel.SmoothMaxMissed = (int)SmoothMaxMissedSlider.Value;
            _selectedModel.SmoothIouThreshold = (float)SmoothIouSlider.Value;

            if (await _modelManager.UpdateModelAsync(_selectedModel))
            {
                MessageBox.Show("模型更新成功", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadModels();
            }
            else
            {
                MessageBox.Show("模型更新失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel == null)
            {
                MessageBox.Show("请先选择一个模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"确定要删除模型 \"{_selectedModel.Name}\" 吗？", 
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (await _modelManager.DeleteModelAsync(_selectedModel.Id))
                {
                    MessageBox.Show("模型删除成功", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadModels();
                    ClearForm();
                    _selectedModel = null;
                }
                else
                {
                    MessageBox.Show("模型删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel == null)
            {
                MessageBox.Show("请先选择一个模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (await _modelManager.LoadModelAsync(_selectedModel.Id))
            {
                MessageBox.Show($"模型 \"{_selectedModel.Name}\" 已加载", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("模型加载失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel == null)
            {
                MessageBox.Show("请先选择一个模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var openFileDialog = new OpenFileDialog
            {
                Filter = "图像文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*",
                Title = "选择测试图像"
            };

            if (openFileDialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                // 初始化检测服务
                if (!await _detectionService.InitializeAsync(_selectedModel))
                {
                    MessageBox.Show("模型初始化失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 加载图像
                using var image = SKBitmap.Decode(openFileDialog.FileName);
                if (image == null)
                {
                    MessageBox.Show("无法加载图像", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 执行检测
                var result = await _detectionService.DetectAsync(image);

                // 显示结果
                var resultMessage = $"检测完成！\n\n" +
                    $"处理时间: {result.ProcessingTimeMs:F2} ms\n" +
                    $"检测到 {result.Objects.Count} 个对象:\n\n" +
                    string.Join("\n", result.Objects.Take(10).Select(o => 
                        $"  • {o.ClassName}: {o.Confidence:P2}"));

                if (result.Objects.Count > 10)
                {
                    resultMessage += $"\n  ... 还有 {result.Objects.Count - 10} 个对象";
                }

                MessageBox.Show(resultMessage, "检测结果", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"测试失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _detectionService.Dispose();
            Close();
        }

        private void ClearForm()
        {
            ModelNameTextBox.Clear();
            ModelDescriptionTextBox.Clear();
            ModelPathTextBox.Clear();
            ModelTypeComboBox.SelectedIndex = 0;
            InputSizeTextBox.Text = "640";
            GpuIdTextBox.Text = "0";
            UseGpuCheckBox.IsChecked = true;
            ConfidenceSlider.Value = 0.5;
            IouSlider.Value = 0.45;
            SmoothEmaSlider.Value = 0.2;
            SmoothConfirmHitsSlider.Value = 2;
            SmoothMaxMissedSlider.Value = 5;
            SmoothIouSlider.Value = 0.3;
            ClassesTextBox.Clear();
        }

        /// <summary>
        /// 扫描模型文件夹
        /// </summary>
        private async void ScanFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ScanFolderButton.IsEnabled = false;
                ScanFolderButton.Content = "🔍 扫描中...";

                var scanResults = await _modelManager.ScanModelsDirectoryAsync();

                if (scanResults.Count == 0)
                {
                    MessageBox.Show(
                        $"未在模型文件夹中找到ONNX模型文件。\n\n" +
                        $"模型文件夹路径:\n{_modelManager.YoloModelsDirectory}\n\n" +
                        $"请将ONNX模型文件放入此文件夹，然后重新扫描。",
                        "扫描结果", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var newModels = scanResults.Where(r => !r.IsAlreadyInDatabase).ToList();
                var existingModels = scanResults.Where(r => r.IsAlreadyInDatabase).ToList();

                var message = $"扫描完成！\n\n" +
                    $"找到 {scanResults.Count} 个ONNX模型文件\n" +
                    $"• 新模型: {newModels.Count} 个\n" +
                    $"• 已存在: {existingModels.Count} 个\n\n";

                if (newModels.Count > 0)
                {
                    message += "新模型列表:\n" +
                        string.Join("\n", newModels.Take(5).Select(m => $"  • {m.FileName} ({GetModelTypeName(m.DetectedType)})"));

                    if (newModels.Count > 5)
                    {
                        message += $"\n  ... 还有 {newModels.Count - 5} 个模型";
                    }

                    message += "\n\n是否自动导入这些新模型？";

                    var result = MessageBox.Show(message, "扫描结果", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        await ImportScannedModels(newModels);
                    }
                }
                else
                {
                    message += "所有模型都已导入到数据库中。";
                    MessageBox.Show(message, "扫描结果", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"扫描失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ScanFolderButton.IsEnabled = true;
                ScanFolderButton.Content = "🔍 扫描模型文件夹";
            }
        }

        /// <summary>
        /// 自动导入新模型
        /// </summary>
        private async void AutoImportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AutoImportButton.IsEnabled = false;
                AutoImportButton.Content = "📥 导入中...";

                var scanResults = await _modelManager.ScanModelsDirectoryAsync();
                var newModels = scanResults.Where(r => !r.IsAlreadyInDatabase).ToList();

                if (newModels.Count == 0)
                {
                    MessageBox.Show("没有发现新的模型文件需要导入。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await ImportScannedModels(newModels);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                AutoImportButton.IsEnabled = true;
                AutoImportButton.Content = "📥 自动导入新模型";
            }
        }

        /// <summary>
        /// 导入扫描到的模型
        /// </summary>
        private async Task ImportScannedModels(List<ModelScanResult> modelsToImport)
        {
            var importedCount = await _modelManager.ImportScannedModelsAsync(modelsToImport);

            if (importedCount > 0)
            {
                MessageBox.Show($"成功导入 {importedCount} 个模型！", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadModels();
            }
            else
            {
                MessageBox.Show("导入失败，请检查模型文件。", "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 获取模型类型的中文名称
        /// </summary>
        private string GetModelTypeName(ModelType type)
        {
            return type switch
            {
                ModelType.ObjectDetection => "目标检测",
                ModelType.Segmentation => "图像分割",
                ModelType.Classification => "图像分类",
                ModelType.PoseEstimation => "姿态估计",
                ModelType.OBBDetection => "旋转框检测",
                _ => "未知"
            };
        }
    }
}
