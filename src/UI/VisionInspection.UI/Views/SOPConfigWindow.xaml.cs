using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// SOP配置窗口
    /// </summary>
    public partial class SOPConfigWindow : Window
    {
        private List<SOPConfigStep> _steps = new();
        private int _editingIndex = -1;
        private string _selectedIcon = "🔍";
        
        // 手部检测高级参数
        public int HandInferenceInterval { get; private set; } = 2;
        public int HandSmoothWindowSize { get; private set; } = 5;
        public float HandSmoothAlpha { get; private set; } = 0.7f;
        public float HandSkeletonThreshold { get; private set; } = 0.3f;

        public SOPConfigWindow()
        {
            InitializeComponent();
            InitializeDefaultConfig();
            UpdateStepsList();
        }

        /// <summary>
        /// 初始化默认配置
        /// </summary>
        private void InitializeDefaultConfig()
        {
            // 统一检测模式下，默认启用手部检测
            EnableHandPoseCheckBox.IsChecked = true;
            EnableHandPoseCheckBox.IsEnabled = true;

            // 默认最大手数为2（支持双手检测）
            MaxHandsComboBox.SelectedIndex = 1;  // 选择 "2"
            MaxHandsComboBox.IsEnabled = true;   // 启用手部检测时启用

            // 默认启用GPU
            UseGpuCheckBox.IsChecked = true;
        }

        /// <summary>
        /// 获取配置好的步骤
        /// </summary>
        public List<SOPConfigStep> GetSteps()
        {
            return _steps;
        }

        /// <summary>
        /// 设置步骤（用于加载已有配置）
        /// </summary>
        public void SetSteps(List<SOPConfigStep> steps)
        {
            _steps = new List<SOPConfigStep>(steps);
            UpdateStepsList();
        }

        /// <summary>
        /// 更新步骤列表显示
        /// </summary>
        private void UpdateStepsList()
        {
            StepsListPanel.Children.Clear();

            for (int i = 0; i < _steps.Count; i++)
            {
                var step = _steps[i];
                var item = CreateStepListItem(step, i);
                StepsListPanel.Children.Add(item);
            }

            // 更新序号
            StepTextBlock.Text = $"步骤 {_steps.Count + 1}";
        }

        /// <summary>
        /// 创建步骤列表项
        /// </summary>
        private Border CreateStepListItem(SOPConfigStep step, int index)
        {
            var border = new Border
            {
                Background = index == _editingIndex ? new SolidColorBrush(Color.FromRgb(230, 247, 255)) : Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 232, 232)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(15, 12, 15, 12),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

            // 序号
            var numberText = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(numberText, 0);
            grid.Children.Add(numberText);

            // 名称和描述
            var namePanel = new StackPanel { Margin = new Thickness(10, 0, 10, 0) };
            var nameText = new TextBlock
            {
                Text = $"{step.Icon} {step.Name}",
                FontWeight = FontWeights.Bold,
                FontSize = 14
            };
            var descText = new TextBlock
            {
                Text = step.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.Gray),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
                MaxWidth = 200
            };
            namePanel.Children.Add(nameText);
            namePanel.Children.Add(descText);
            Grid.SetColumn(namePanel, 1);
            grid.Children.Add(namePanel);

            // 检测类型
            var typeText = new TextBlock
            {
                Text = step.DetectionType,
                Foreground = new SolidColorBrush(Color.FromRgb(82, 196, 26)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(typeText, 2);
            grid.Children.Add(typeText);

            // 操作按钮
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            
            var editButton = new Button
            {
                Content = "✏️",
                Width = 30,
                Height = 30,
                Margin = new Thickness(2),
                ToolTip = "编辑"
            };
            editButton.Click += (s, e) => EditStep(index);
            
            var deleteButton = new Button
            {
                Content = "🗑️",
                Width = 30,
                Height = 30,
                Margin = new Thickness(2),
                ToolTip = "删除"
            };
            deleteButton.Click += (s, e) => DeleteStep(index);
            
            var upButton = new Button
            {
                Content = "↑",
                Width = 30,
                Height = 30,
                Margin = new Thickness(2),
                ToolTip = "上移",
                IsEnabled = index > 0
            };
            upButton.Click += (s, e) => MoveStep(index, -1);
            
            var downButton = new Button
            {
                Content = "↓",
                Width = 30,
                Height = 30,
                Margin = new Thickness(2),
                ToolTip = "下移",
                IsEnabled = index < _steps.Count - 1
            };
            downButton.Click += (s, e) => MoveStep(index, 1);

            buttonPanel.Children.Add(editButton);
            buttonPanel.Children.Add(deleteButton);
            buttonPanel.Children.Add(upButton);
            buttonPanel.Children.Add(downButton);
            Grid.SetColumn(buttonPanel, 3);
            grid.Children.Add(buttonPanel);

            border.Child = grid;
            
            // 点击选中编辑
            border.MouseLeftButtonUp += (s, e) => EditStep(index);
            
            return border;
        }

        /// <summary>
        /// 编辑步骤
        /// </summary>
        private void EditStep(int index)
        {
            if (index < 0 || index >= _steps.Count) return;

            _editingIndex = index;
            var step = _steps[index];

            StepNameTextBox.Text = step.Name;
            StepDescriptionTextBox.Text = step.Description;
            _selectedIcon = step.Icon;

            // 设置检测类型
            foreach (ComboBoxItem item in DetectionTypeComboBox.Items)
            {
                if (item.Content.ToString() == step.DetectionType)
                {
                    DetectionTypeComboBox.SelectedItem = item;
                    break;
                }
            }

            // 设置模型
            foreach (ComboBoxItem item in ModelComboBox.Items)
            {
                if (item.Content.ToString() == step.ModelName)
                {
                    ModelComboBox.SelectedItem = item;
                    break;
                }
            }

            // 切换按钮
            AddStepButton.Visibility = Visibility.Collapsed;
            UpdateStepButton.Visibility = Visibility.Visible;

            UpdateStepsList();
        }

        /// <summary>
        /// 删除步骤
        /// </summary>
        private void DeleteStep(int index)
        {
            if (MessageBox.Show($"确定要删除步骤 \"{_steps[index].Name}\" 吗？", "确认删除", 
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _steps.RemoveAt(index);
                if (_editingIndex == index)
                {
                    ClearForm();
                }
                else if (_editingIndex > index)
                {
                    _editingIndex--;
                }
                UpdateStepsList();
            }
        }

        /// <summary>
        /// 移动步骤
        /// </summary>
        private void MoveStep(int index, int direction)
        {
            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= _steps.Count) return;

            var step = _steps[index];
            _steps.RemoveAt(index);
            _steps.Insert(newIndex, step);

            if (_editingIndex == index)
                _editingIndex = newIndex;
            else if (_editingIndex == newIndex)
                _editingIndex = index;

            UpdateStepsList();
        }

        /// <summary>
        /// 清空表单
        /// </summary>
        private void ClearForm()
        {
            StepNameTextBox.Clear();
            StepDescriptionTextBox.Clear();
            DetectionTypeComboBox.SelectedIndex = 0;
            ModelComboBox.SelectedIndex = 0;
            _selectedIcon = "🔍";
            _editingIndex = -1;

            AddStepButton.Visibility = Visibility.Visible;
            UpdateStepButton.Visibility = Visibility.Collapsed;

            UpdateStepsList();
        }

        /// <summary>
        /// 添加步骤按钮点击
        /// </summary>
        private void AddStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(StepNameTextBox.Text))
            {
                MessageBox.Show("请输入步骤名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var step = new SOPConfigStep
            {
                Id = $"step_{_steps.Count + 1}",
                Name = StepNameTextBox.Text.Trim(),
                Description = StepDescriptionTextBox.Text.Trim(),
                DetectionType = (DetectionTypeComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "物体检测",
                ModelName = (ModelComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "默认YOLOv8模型",
                Icon = _selectedIcon
            };

            _steps.Add(step);
            ClearForm();
            UpdateStepsList();
        }

        /// <summary>
        /// 更新步骤按钮点击
        /// </summary>
        private void UpdateStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editingIndex < 0 || _editingIndex >= _steps.Count) return;

            if (string.IsNullOrWhiteSpace(StepNameTextBox.Text))
            {
                MessageBox.Show("请输入步骤名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var step = _steps[_editingIndex];
            step.Name = StepNameTextBox.Text.Trim();
            step.Description = StepDescriptionTextBox.Text.Trim();
            step.DetectionType = (DetectionTypeComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "物体检测";
            step.ModelName = (ModelComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "默认YOLOv8模型";
            step.Icon = _selectedIcon;

            ClearForm();
            UpdateStepsList();
        }

        /// <summary>
        /// 图标按钮点击
        /// </summary>
        private void IconButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                _selectedIcon = button.Content.ToString() ?? "🔍";
                
                // 更新按钮样式
                foreach (var child in IconPanel.Children)
                {
                    if (child is Button btn)
                    {
                        btn.BorderBrush = Brushes.Transparent;
                        btn.BorderThickness = new Thickness(1);
                    }
                }
                
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(24, 144, 255));
                button.BorderThickness = new Thickness(2);
            }
        }

        /// <summary>
        /// 清空按钮点击
        /// </summary>
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearForm();
        }

        /// <summary>
        /// 加载配置按钮点击
        /// </summary>
        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "SOP配置文件|*.json;*.yaml;*.yml|JSON文件|*.json|YAML文件|*.yaml;*.yml|所有文件|*.*",
                Title = "加载SOP配置"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var filePath = dialog.FileName;
                    var extension = Path.GetExtension(filePath).ToLowerInvariant();
                    List<SOPConfigStep> steps;

                    if (extension == ".json")
                    {
                        // JSON格式
                        var json = File.ReadAllText(filePath);
                        steps = JsonSerializer.Deserialize<List<SOPConfigStep>>(json);
                    }
                    else if (extension == ".yaml" || extension == ".yml")
                    {
                        // YAML格式 - 使用SOPYamlConverter解析
                        var workflow = VisionInspection.Modules.SOP.Models.SOPYamlConverter.LoadFromYaml(filePath);
                        steps = ConvertWorkflowToSteps(workflow);
                    }
                    else
                    {
                        MessageBox.Show($"不支持的文件格式：{extension}\n请使用 .json 或 .yaml/.yml 文件", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    if (steps != null)
                    {
                        _steps = steps;
                        ClearForm();
                        UpdateStepsList();
                        MessageBox.Show("配置加载成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载配置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 将SOPWorkflow转换为SOPConfigStep列表
        /// </summary>
        private List<SOPConfigStep> ConvertWorkflowToSteps(VisionInspection.Modules.SOP.Models.SOPWorkflow workflow)
        {
            var steps = new List<SOPConfigStep>();

            foreach (var step in workflow.Steps)
            {
                var configStep = new SOPConfigStep
                {
                    Id = step.StepId.ToString(),
                    Name = step.StepName,
                    Description = step.Description ?? "",
                    DetectionType = "物体检测",
                    ModelName = "默认YOLOv8模型",
                    Icon = GetIconForStep(step.StepName)
                };

                // 根据条件类型设置检测类型
                if (step.PassConditions.Count > 0)
                {
                    var condition = step.PassConditions[0];
                    configStep.DetectionType = condition.Type switch
                    {
                        VisionInspection.Modules.SOP.Models.ConditionType.ObjectPresent => "物体检测",
                        VisionInspection.Modules.SOP.Models.ConditionType.ObjectInZone => "区域检测",
                        VisionInspection.Modules.SOP.Models.ConditionType.ObjectStable => "稳定检测",
                        VisionInspection.Modules.SOP.Models.ConditionType.ObjectAbsent => "缺失检测",
                        VisionInspection.Modules.SOP.Models.ConditionType.TimeElapsed => "时间检测",
                        _ => "物体检测"
                    };
                }

                steps.Add(configStep);
            }

            return steps;
        }

        /// <summary>
        /// 根据步骤名称获取图标
        /// </summary>
        private string GetIconForStep(string stepName)
        {
            if (stepName.Contains("车")) return "🚗";
            if (stepName.Contains("牌")) return "📋";
            if (stepName.Contains("安全")) return "✓";
            if (stepName.Contains("PLC")) return "🔌";
            if (stepName.Contains("零件") || stepName.Contains("准备")) return "📦";
            if (stepName.Contains("装配")) return "🔧";
            if (stepName.Contains("质检") || stepName.Contains("检查")) return "🔍";
            if (stepName.Contains("完成")) return "✅";
            return "🔍";
        }

        /// <summary>
        /// 保存配置按钮点击
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON文件|*.json|所有文件|*.*",
                Title = "保存SOP配置",
                FileName = $"sop_config_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_steps, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(dialog.FileName, json);
                    MessageBox.Show("配置保存成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存配置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 确定按钮点击
        /// </summary>
        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
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
        /// 清空全部步骤
        /// </summary>
        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要清空所有步骤吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _steps.Clear();
                UpdateStepsList();
                ClearForm();
            }
        }

        // 检测模式选择变化 - 已移除（统一检测模式下不再需要）

        /// <summary>
        /// 启用手部检测勾选
        /// </summary>
        private void EnableHandPoseCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (MaxHandsComboBox != null)
                MaxHandsComboBox.IsEnabled = true;
        }

        /// <summary>
        /// 禁用手部检测勾选
        /// </summary>
        private void EnableHandPoseCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (MaxHandsComboBox != null)
                MaxHandsComboBox.IsEnabled = false;
        }

        /// <summary>
        /// 获取检测模式配置
        /// </summary>
        public SOPDetectionModeConfig GetDetectionModeConfig()
        {
            // 统一检测模式下，始终使用 UnifiedDetection
            return new SOPDetectionModeConfig
            {
                DetectionMode = "UnifiedDetection",
                EnableHandPoseEstimation = EnableHandPoseCheckBox.IsChecked ?? true,
                MaxNumHands = int.TryParse((MaxHandsComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var maxHands) ? maxHands : 2,
                UseGpu = UseGpuCheckBox.IsChecked ?? true
            };
        }

        /// <summary>
        /// 设置检测模式配置
        /// </summary>
        public void SetDetectionModeConfig(SOPDetectionModeConfig config)
        {
            // 统一检测模式下，检测模式固定为 UnifiedDetection，无需设置下拉框

            // 设置手部检测
            EnableHandPoseCheckBox.IsChecked = config.EnableHandPoseEstimation;
            EnableHandPoseCheckBox.IsEnabled = true;
            
            // 设置最大手数
            foreach (ComboBoxItem item in MaxHandsComboBox.Items)
            {
                if (item.Content?.ToString() == config.MaxNumHands.ToString())
                {
                    MaxHandsComboBox.SelectedItem = item;
                    break;
                }
            }

            // 设置GPU
            UseGpuCheckBox.IsChecked = config.UseGpu;
        }

        /// <summary>
        /// 高级参数按钮点击
        /// </summary>
        private void AdvancedParamsButton_Click(object sender, RoutedEventArgs e)
        {
            var paramsWindow = new HandDetectionParamsWindow(
                HandInferenceInterval,
                HandSmoothWindowSize,
                HandSmoothAlpha,
                HandSkeletonThreshold);
            
            paramsWindow.Owner = this;
            
            if (paramsWindow.ShowDialog() == true)
            {
                // 保存参数
                HandInferenceInterval = paramsWindow.InferenceInterval;
                HandSmoothWindowSize = paramsWindow.SmoothWindowSize;
                HandSmoothAlpha = paramsWindow.SmoothAlpha;
                HandSkeletonThreshold = paramsWindow.SkeletonConfidenceThreshold;
                
                System.Diagnostics.Debug.WriteLine($"[SOPConfig] 高级参数已更新: " +
                    $"InferenceInterval={HandInferenceInterval}, " +
                    $"SmoothWindow={HandSmoothWindowSize}, " +
                    $"SmoothAlpha={HandSmoothAlpha:F2}, " +
                    $"SkeletonThreshold={HandSkeletonThreshold:F2}");
            }
        }
    }

    /// <summary>
    /// SOP检测模式配置
    /// </summary>
    public class SOPDetectionModeConfig
    {
        public string DetectionMode { get; set; } = "ObjectBased";
        public bool EnableHandPoseEstimation { get; set; } = false;
        public int MaxNumHands { get; set; } = 1;
        public bool UseGpu { get; set; } = true;
    }

    /// <summary>
    /// SOP配置步骤
    /// </summary>
    public class SOPConfigStep
    {
        [System.Text.Json.Serialization.JsonPropertyName("Id")]
        public string Id { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("Name")]
        public string Name { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("Description")]
        public string Description { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("DetectionType")]
        public string DetectionType { get; set; } = "物体检测";
        
        [System.Text.Json.Serialization.JsonPropertyName("ModelName")]
        public string ModelName { get; set; } = "默认YOLOv8模型";
        
        [System.Text.Json.Serialization.JsonPropertyName("Icon")]
        public string Icon { get; set; } = "🔍";
    }
}
