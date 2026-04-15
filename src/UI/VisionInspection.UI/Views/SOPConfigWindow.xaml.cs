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

        public SOPConfigWindow()
        {
            InitializeComponent();
            UpdateStepsList();
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

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
                Filter = "JSON文件|*.json|所有文件|*.*",
                Title = "加载SOP配置"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var steps = JsonSerializer.Deserialize<List<SOPConfigStep>>(json);
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
    }

    /// <summary>
    /// SOP配置步骤
    /// </summary>
    public class SOPConfigStep
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string DetectionType { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string Icon { get; set; } = "🔍";
    }
}
